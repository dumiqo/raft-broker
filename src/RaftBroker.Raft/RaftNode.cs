using RaftBroker.Core.Primitives;
using RaftBroker.Core.Randomness;
using RaftBroker.Core.Time;
using RaftBroker.Raft.Elections;
using RaftBroker.Raft.Messages;
using RaftBroker.Raft.State;
using RaftBroker.Storage.Log;

namespace RaftBroker.Raft;

/// <summary>
/// Узел Raft на этапе S2: роли, term и выборы (<c>S2-T05</c>).
/// </summary>
/// <remarks>
/// <para>
/// Узел не заводит собственного цикла: таймер выборов только помечает, что срок истёк, а работу
/// делает <see cref="StepAsync"/>. Так прогон на виртуальных часах детерминирован, а настоящий
/// цикл появляется вместе с настоящим транспортом в <c>S4-T01</c> (ADR <c>0015-election-seams.md</c>).
/// Вызывать <see cref="StepAsync"/> обязан рантайм: в бою - цикл S4, в тестах - сам тест.
/// </para>
/// <para>
/// Раунд выборов идёт в три фазы, и это не стиль:
/// </para>
/// <list type="number">
/// <item>под воротцами - поднять term, записать голос за себя, собрать запросы;</item>
/// <item>без воротцев - разослать запросы параллельно и дождаться ответов;</item>
/// <item>под воротцами - применить ответы, если раунд всё ещё тот же.</item>
/// </list>
/// <para>
/// Отправка под воротцами дала бы клинч: два узла, одновременно проводящих выборы, ждали бы
/// воротца друг друга. Ответы старого раунда не применяются: номер раунда и term фиксируются
/// на момент отправки, а роль проверяется перед подсчётом.
/// </para>
/// <para>
/// Свой таймаут раунда не заводится: он принадлежит транспорту (deadline в S4). Иначе в узле
/// появились бы реальные таймеры, несовместимые с <c>TestClock</c>, и фаззер <c>S2-T10</c>
/// перестал бы быть детерминированным.
/// </para>
/// </remarks>
public sealed class RaftNode : IRaftMessageHandler, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly RaftState _state;
    private readonly ILogStore _log;
    private readonly IPeerTransport _transport;
    private readonly IClusterConfiguration _configuration;
    private readonly ElectionTimer _timer;

    /// <summary>Сигнал таймера: выставляется в его потоке, читается в <see cref="StepAsync"/>.</summary>
    private volatile bool _electionDue;

    /// <summary>Номер раунда: ответы прошлых раундов не считаются.</summary>
    private long _round;

    /// <summary>Создаёт узел.</summary>
    /// <param name="options">Параметры узла.</param>
    /// <param name="state">Состояние узла, уже загруженное из hard state.</param>
    /// <param name="log">Журнал: источник <c>LastIndex</c>/<c>LastTerm</c> для правила up-to-date.</param>
    /// <param name="transport">Транспорт до пиров.</param>
    /// <param name="configuration">Источник состава кворума.</param>
    /// <param name="clock">Источник времени для таймера выборов.</param>
    /// <param name="random">Источник разброса срока выборов.</param>
    /// <exception cref="ArgumentException">Узел не входит в конфигурацию кластера.</exception>
    public RaftNode(
        RaftNodeOptions options,
        RaftState state,
        ILogStore log,
        IPeerTransport transport,
        IClusterConfiguration configuration,
        IClock clock,
        IRandomSource random)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(random);

        if (!configuration.Voters.Contains(options.Id))
        {
            // Узел вне конфигурации не может считать себя частью кворума: ошибка конфигурации,
            // а не повод тихо выигрывать выборы в одиночку.
            throw new ArgumentException($"Узел '{options.Id}' не входит в конфигурацию кластера.", nameof(configuration));
        }

        _state = state;
        _log = log;
        _transport = transport;
        _configuration = configuration;
        Id = options.Id;
        _timer = new ElectionTimer(clock, random, options.Election, () => _electionDue = true);
    }

    /// <summary>Идентификатор узла.</summary>
    public NodeId Id { get; }

    /// <summary>Текущая роль.</summary>
    public RaftRole Role => _state.Role;

    /// <summary>Текущий term.</summary>
    public Term CurrentTerm => _state.CurrentTerm;

    /// <summary>Кому узел отдал голос в текущем term; <c>null</c> - никому.</summary>
    public NodeId? VotedFor => _state.VotedFor;

    /// <summary>Известный лидер, подсказка для клиентов.</summary>
    public NodeId? LeaderId => _state.LeaderId;

    /// <summary>Запускает отсчёт до первых выборов.</summary>
    public void Start() => _timer.Reset();

    /// <summary>Выполняет отложенную работу: выборы по истёкшему сроку.</summary>
    /// <param name="cancellationToken">Отмена: прерывание до записи голоса означает, что раунд не начался.</param>
    public async ValueTask StepAsync(CancellationToken cancellationToken)
    {
        ElectionRound? round = null;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!_electionDue)
            {
                return;
            }

            _electionDue = false;
            round = await BeginElectionAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        var replies = await CollectVotesAsync(round, cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await FinishElectionAsync(round, replies, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<RequestVoteResponse> HandleRequestVoteAsync(
        RequestVoteRequest request,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var response = await RequestVoteHandler
                .HandleAsync(request, _state, _log.LastTerm, _log.LastIndex, cancellationToken)
                .ConfigureAwait(false);

            if (response.VoteGranted)
            {
                // Отдавшему голос незачем сразу идти на выборы: отсчёт начинается заново.
                _timer.Reset();
            }

            return response;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);

        try
        {
            _timer.Dispose();
        }
        finally
        {
            _gate.Release();
        }

        _gate.Dispose();
    }

    /// <summary>Фаза 1: новый term, голос за себя на диске, запросы для пиров.</summary>
    private async ValueTask<ElectionRound> BeginElectionAsync(CancellationToken cancellationToken)
    {
        var term = _state.CurrentTerm.Next();

        // Сначала диск: подтверждённый голос за себя не может быть потерян крахом (R6).
        await _state.PersistAsync(term, Id, cancellationToken).ConfigureAwait(false);
        _state.Role = RaftRole.Candidate;
        _state.LeaderId = null;

        // Отсчёт начинается заново: интервал между раундами не меньше T, иначе одинокая нода
        // раскручивала бы term бесконечным циклом выборов.
        _timer.Reset();

        var request = new RequestVoteRequest(Id, term, _log.LastIndex, _log.LastTerm);

        return new ElectionRound(++_round, term, request, [.. _configuration.Voters.Where(voter => voter != Id)]);
    }

    /// <summary>Фаза 2: запросы всем пирам параллельно; недоступный пир - просто нет голоса.</summary>
    private async ValueTask<VoteReply[]> CollectVotesAsync(ElectionRound round, CancellationToken cancellationToken)
    {
        var requests = round.Peers
            .Select(peer => AskAsync(peer, round.Request, cancellationToken).AsTask())
            .ToArray();

        return await Task.WhenAll(requests).ConfigureAwait(false);
    }

    private async ValueTask<VoteReply> AskAsync(NodeId peer, RequestVoteRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _transport.RequestVoteAsync(peer, request, cancellationToken).ConfigureAwait(false);

            return new VoteReply(peer, response);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Любой сбой связи - это «ответа нет», а не повод остановить раунд: кворум могли
            // дать остальные, и обрыв до одного пира не должен срывать выборы.
            return new VoteReply(peer, null);
        }
    }

    /// <summary>Фаза 3: посчитать кворум, если раунд всё ещё текущий.</summary>
    private async ValueTask FinishElectionAsync(
        ElectionRound round,
        VoteReply[] replies,
        CancellationToken cancellationToken)
    {
        if (!IsCurrent(round))
        {
            // Пока запросы летели, узел сменил роль или term: ответы принадлежат прошлому раунду.
            return;
        }

        foreach (var reply in replies)
        {
            if (reply.Response is { } response && response.Term > _state.CurrentTerm)
            {
                await StepDownAsync(response.Term, cancellationToken).ConfigureAwait(false);

                return;
            }
        }

        var acknowledgers = new List<NodeId> { Id };

        foreach (var reply in replies)
        {
            if (reply.Response is { VoteGranted: true })
            {
                acknowledgers.Add(reply.Peer);
            }
        }

        if (!_configuration.IsQuorum(acknowledgers))
        {
            // Остаёмся кандидатом: следующий срок таймера начнёт новый раунд с term + 1.
            return;
        }

        await BecomeLeaderAsync(cancellationToken).ConfigureAwait(false);
    }

    private bool IsCurrent(ElectionRound round)
        => _round == round.Id && _state.Role == RaftRole.Candidate && _state.CurrentTerm == round.Term;

    /// <summary>Победа: роль лидера и остановка таймера выборов.</summary>
    private ValueTask BecomeLeaderAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _state.Role = RaftRole.Leader;
        _state.LeaderId = Id;
        _timer.Stop();

        return ValueTask.CompletedTask;
    }

    /// <summary>Уход в follower по большему term: term на диск до смены роли.</summary>
    private async ValueTask StepDownAsync(Term newTerm, CancellationToken cancellationToken)
    {
        await _state.PersistAsync(newTerm, votedFor: null, cancellationToken).ConfigureAwait(false);
        _state.Role = RaftRole.Follower;
        _state.LeaderId = null;
        _timer.Reset();
    }
}
