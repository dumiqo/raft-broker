using RaftBroker.Core.Primitives;
using RaftBroker.Core.Randomness;
using RaftBroker.Core.Time;
using RaftBroker.Raft.Elections;
using RaftBroker.Raft.Messages;
using RaftBroker.Raft.State;
using RaftBroker.Storage.Log;

namespace RaftBroker.Raft;

/// <summary>
/// Узел Raft на этапе S2: роли, term и выборы (<c>S2-T05</c>), лидер и heartbeat (<c>S2-T06</c>).
/// </summary>
/// <remarks>
/// <para>
/// Узел не заводит собственного цикла: таймер выборов только помечает, что срок истёк, а работу
/// делает <see cref="StepAsync"/>. Так прогон на виртуальных часах детерминирован, а настоящий
/// цикл появляется вместе с настоящим транспортом в <c>S4-T01</c> (ADR <c>0015-election-seams.md</c>).
/// Вызывать <see cref="StepAsync"/> обязан рантайм: в бою - цикл S4, в тестах - сам тест.
/// </para>
/// <para>
/// Раунд выборов идёт в две фазы с подготовкой, и это не стиль:
/// </para>
/// <list type="number">
/// <item>под воротцами - поднять term, записать голос за себя, собрать запросы;</item>
/// <item>без воротцев - разослать запросы параллельно и дождаться ответов;</item>
/// <item>под воротцами - применить ответы, если раунд всё ещё тот же.</item>
/// </list>
/// <para>
/// При включённом PreVote (<c>S2-T07</c>) фаз две пары: сначала опрос «дали бы голос?», который
/// не трогает ни term, ни диск, и только при кворуме - настоящий раунд. Поэтому узел, выпавший из
/// сети, не накручивает term в одиночестве и не выбивает живого лидера по возвращении.
/// </para>
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
/// <para>
/// CheckQuorum и leader stickiness (<c>S2-T09</c>) живут на одном состоянии - «когда я последний
/// раз слышал кворум/лидера» - и на одном окне <c>T</c>. Своего таймера у них тоже нет: окно
/// проверяется в том же <see cref="StepAsync"/>, что и heartbeat, поэтому прогон на виртуальных
/// часах воспроизводим.
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
    private readonly IClock _clock;
    private readonly int _heartbeatIntervalMs;
    private readonly int _quorumWindowMs;
    private readonly bool _preVoteEnabled;
    private readonly bool _checkQuorum;
    private readonly Dictionary<NodeId, PeerProgress> _progress = [];

    /// <summary>Сигнал таймера: выставляется в его потоке, читается в <see cref="StepAsync"/>.</summary>
    private volatile bool _electionDue;

    /// <summary>Когда узел последний раз принял валидный heartbeat лидера; <c>null</c> - ни разу.</summary>
    private long? _lastLeaderContactMs;

    /// <summary>Номер раунда: ответы прошлых раундов не считаются.</summary>
    private long _round;

    /// <summary>Монотонная метка, после которой лидеру снова пора разослать heartbeat.</summary>
    private long _nextHeartbeatMs;

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
        _clock = clock;
        _heartbeatIntervalMs = (int)options.HeartbeatInterval.TotalMilliseconds;
        _quorumWindowMs = (int)options.Election.Timeout.TotalMilliseconds;
        _preVoteEnabled = options.PreVote;
        _checkQuorum = options.CheckQuorum;
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

    /// <summary>Представление лидера о пирах (<c>S2-T06</c>); у не-лидера пусто.</summary>
    /// <remarks>
    /// Снимок на момент чтения: меняется только под воротцами узла, поэтому читать его имеет смысл
    /// после завершения <see cref="StepAsync"/> (внутри узла его читает CheckQuorum, <c>S2-T09</c>).
    /// </remarks>
    public IReadOnlyDictionary<NodeId, PeerProgress> LeaderProgress => _progress;

    /// <summary>Запускает отсчёт до первых выборов.</summary>
    public void Start() => ResetElectionTimer();

    /// <summary>Выполняет отложенную работу: heartbeat лидера или раунд выборов по истёкшему сроку.</summary>
    /// <param name="cancellationToken">Отмена: прерывание до записи голоса означает, что раунд не начался.</param>
    public async ValueTask StepAsync(CancellationToken cancellationToken)
    {
        var round = await StartRoundAsync(cancellationToken).ConfigureAwait(false);

        while (round is not null)
        {
            // PreVote-раунд возвращает настоящий: два прохода, а не рекурсия.
            round = await RunRoundAsync(round, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Фаза 1: heartbeat по расписанию или старт раунда по сработавшему сроку.</summary>
    private async ValueTask<ElectionRound?> StartRoundAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_state.Role == RaftRole.Leader)
            {
                // Лидер выборов не ждёт: его работа - heartbeat по расписанию. Отправка идёт под
                // воротцами, потому что обработчики RPC не вызывают отправителя обратно: цикла
                // ожидания из этого не возникает.
                if (_clock.TimestampMs >= _nextHeartbeatMs)
                {
                    _nextHeartbeatMs = _clock.TimestampMs + _heartbeatIntervalMs;
                    await SendHeartbeatsAsync(cancellationToken).ConfigureAwait(false);
                }

                // CheckQuorum после отправки, а не до: иначе лидер, который шагает реже, чем
                // приходят ответы, считал бы кворум потерянным из-за собственного расписания.
                if (_state.Role == RaftRole.Leader && !HasResponsiveQuorum(_clock.TimestampMs))
                {
                    // Терм тот же, поэтому на диск не пишется ничего: это не терм-переход (S2-T08).
                    await StepDownAsync(_state.CurrentTerm, cancellationToken).ConfigureAwait(false);
                }

                return null;
            }

            if (!_electionDue)
            {
                return null;
            }

            _electionDue = false;

            return _preVoteEnabled
                ? BeginPreVoteRound()
                : await BeginVoteRoundAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Фазы 2 и 3: рассылка без воротцев, применение ответов - под ними.</summary>
    /// <returns>Следующий раунд (<c>S2-T07</c>: настоящие выборы после удачного PreVote) или <c>null</c>.</returns>
    private async ValueTask<ElectionRound?> RunRoundAsync(ElectionRound round, CancellationToken cancellationToken)
    {
        var replies = await CollectAsync(round, cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (round.Kind == ElectionRoundKind.PreVote)
            {
                return await CompletePreVoteAsync(round, replies, cancellationToken).ConfigureAwait(false);
            }

            await FinishElectionAsync(round, replies, cancellationToken).ConfigureAwait(false);

            return null;
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
            var previousTerm = _state.CurrentTerm;

            var response = await RequestVoteHandler
                .HandleAsync(
                    request,
                    _state,
                    _log.LastTerm,
                    _log.LastIndex,
                    cancellationToken,
                    IsLeaderContactFresh())
                .ConfigureAwait(false);

            if (response.VoteGranted || response.Term > previousTerm)
            {
                // Отдавшему голос незачем сразу идти на выборы, а ушедшему в follower по большему
                // терму - тем более. Уборка та же, что в StepDown: голос мог отобрать лидерство.
                // Отказ в том же терме ничего не трогает: иначе сломанный узел одним спамом
                // запросов не давал бы кластеру выбрать лидера.
                LeaveRole();
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

    /// <summary>
    /// Старт PreVote-раунда (<c>S2-T07</c>): term и голос не меняются, на диск не пишется ничего.
    /// </summary>
    private ElectionRound BeginPreVoteRound()
    {
        var term = _state.CurrentTerm.Next();

        // Роль - кандидат, иначе ответы раунда будут отброшены как чужие; term и голос при этом
        // прежние, а диск не трогается вовсе: в этом весь смысл PreVote.
        _state.Role = RaftRole.Candidate;
        _state.LeaderId = null;

        ResetElectionTimer();

        var request = new RequestVoteRequest(Id, term, _log.LastIndex, _log.LastTerm);

        return new ElectionRound(++_round, ElectionRoundKind.PreVote, _state.CurrentTerm, term, request, Peers());
    }

    /// <summary>Фаза 1 настоящего раунда: новый term, голос за себя на диске, запросы для пиров.</summary>
    private async ValueTask<ElectionRound> BeginVoteRoundAsync(CancellationToken cancellationToken)
    {
        var term = _state.CurrentTerm.Next();

        // Сначала диск: подтверждённый голос за себя не может быть потерян крахом (R6).
        await _state.PersistAsync(term, Id, cancellationToken).ConfigureAwait(false);
        _state.Role = RaftRole.Candidate;
        _state.LeaderId = null;

        // Отсчёт начинается заново: интервал между раундами не меньше T, иначе одинокая нода
        // раскручивала бы term бесконечным циклом выборов.
        ResetElectionTimer();

        var request = new RequestVoteRequest(Id, term, _log.LastIndex, _log.LastTerm);

        return new ElectionRound(++_round, ElectionRoundKind.Vote, _state.CurrentTerm, term, request, Peers());
    }

    private NodeId[] Peers() => [.. _configuration.Voters.Where(voter => voter != Id)];

    /// <summary>Фаза 2: запросы всем пирам параллельно; недоступный пир - просто нет голоса.</summary>
    private async ValueTask<ElectionReply[]> CollectAsync(ElectionRound round, CancellationToken cancellationToken)
    {
        var requests = round.Peers
            .Select(peer => AskAsync(peer, round, cancellationToken).AsTask())
            .ToArray();

        return await Task.WhenAll(requests).ConfigureAwait(false);
    }

    private async ValueTask<ElectionReply> AskAsync(
        NodeId peer,
        ElectionRound round,
        CancellationToken cancellationToken)
    {
        try
        {
            if (round.Kind == ElectionRoundKind.PreVote)
            {
                var preVote = new PreVoteRequest(Id, round.Term, round.Request.LastLogIndex, round.Request.LastLogTerm);
                var preVoteResponse = await _transport.PreVoteAsync(peer, preVote, cancellationToken).ConfigureAwait(false);

                return new ElectionReply(peer, preVoteResponse.Term, preVoteResponse.VoteGranted, Answered: true);
            }

            var response = await _transport.RequestVoteAsync(peer, round.Request, cancellationToken).ConfigureAwait(false);

            return new ElectionReply(peer, response.Term, response.VoteGranted, Answered: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PeerUnreachableException)
        {
            // Сбой связи - это «ответа нет», а не повод остановить раунд: кворум могли дать
            // остальные, и обрыв до одного пира не должен срывать выборы. Ловится только
            // объявленный отказ транспорта: чужой сбой обязан лететь наверх.
            return new ElectionReply(peer, default, Granted: false, Answered: false);
        }
    }

    /// <summary>
    /// Применение ответов PreVote (<c>S2-T07</c>): кворум «дал бы голос» открывает настоящий раунд.
    /// </summary>
    private async ValueTask<ElectionRound?> CompletePreVoteAsync(
        ElectionRound round,
        ElectionReply[] replies,
        CancellationToken cancellationToken)
    {
        if (!IsCurrent(round))
        {
            return null;
        }

        foreach (var reply in replies)
        {
            if (reply is { Answered: true } && RaftRoleTransitions.Decide(_state.CurrentTerm, reply.Term) == StepKind.StepDown)
            {
                // Кто-то живёт в большем терме: собеседование провалено, терм узла устарел.
                // Сам PreVote терм не двигал - это единственное место, где узел узнаёт новое.
                await StepDownAsync(reply.Term, cancellationToken).ConfigureAwait(false);

                return null;
            }
        }

        return HasQuorum(replies)
            ? await BeginVoteRoundAsync(cancellationToken).ConfigureAwait(false)
            : null;
    }

    /// <summary>Фаза 3: посчитать кворум, если раунд всё ещё текущий.</summary>
    private async ValueTask FinishElectionAsync(
        ElectionRound round,
        ElectionReply[] replies,
        CancellationToken cancellationToken)
    {
        if (!IsCurrent(round))
        {
            // Пока запросы летели, узел сменил роль или term: ответы принадлежат прошлому раунду.
            return;
        }

        foreach (var reply in replies)
        {
            if (reply is { Answered: true } && RaftRoleTransitions.Decide(_state.CurrentTerm, reply.Term) == StepKind.StepDown)
            {
                await StepDownAsync(reply.Term, cancellationToken).ConfigureAwait(false);

                return;
            }
        }

        if (!HasQuorum(replies))
        {
            // Остаёмся кандидатом: следующий срок таймера начнёт новый раунд с term + 1.
            return;
        }

        await BecomeLeaderAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Перезапускает таймер выборов и снимает уже сработавший сигнал.
    /// </summary>
    /// <remarks>
    /// Снимать сигнал обязательно: таймер мог сработать, а heartbeat - прийти в том же шаге.
    /// Без сброса признака узел пошёл бы на выборы, уже зная лидера, и поднял бы term на ровном месте.
    /// </remarks>
    private void ResetElectionTimer()
    {
        _electionDue = false;
        _timer.Reset();
    }

    /// <summary>Кворум по ответам раунда: собственный голос/намерение считается первым.</summary>
    /// <remarks>
    /// В PreVote «свой голос» - это намерение, а не запись на диске: кворум здесь означает
    /// «я бы проголосовал за себя и столько же пиров проголосовали бы», ничего не фиксируя.
    /// </remarks>
    private bool HasQuorum(ElectionReply[] replies)
    {
        var acknowledgers = new List<NodeId> { Id };

        foreach (var reply in replies)
        {
            if (reply is { Answered: true, Granted: true })
            {
                acknowledgers.Add(reply.Peer);
            }
        }

        return _configuration.IsQuorum(acknowledgers);
    }

    /// <summary>Раунд ещё текущий: та же роль, тот же term узла и тот же номер раунда.</summary>
    /// <remarks>
    /// Сравнивается term узла, а не term запроса: у PreVote запрос опережает term на единицу,
    /// и по нему свежесть раунда не проверить.
    /// </remarks>
    private bool IsCurrent(ElectionRound round)
        => _round == round.Id && _state.Role == RaftRole.Candidate && _state.CurrentTerm == round.NodeTerm;

    /// <summary>Победа: роль лидера, репликаторы пиров, no-op запись нового term и первый heartbeat.</summary>
    private async ValueTask BecomeLeaderAsync(CancellationToken cancellationToken)
    {
        _state.Role = RaftRole.Leader;
        _state.LeaderId = Id;
        _timer.Stop();

        await AppendNoopAsync(cancellationToken).ConfigureAwait(false);

        _progress.Clear();

        foreach (var peer in _configuration.Voters.Where(voter => voter != Id))
        {
            // matchIndex начинается с нуля: неподтверждённое не считается совпавшим, а nextIndex
            // считается после no-op записи - лидер отправляет следующую за ней.
            _progress[peer] = new PeerProgress(peer, _log.LastIndex.Next(), LogIndex.Empty, _clock.TimestampMs);
        }

        // Первый heartbeat - сразу, не дожидаясь интервала: иначе кластер узнаёт лидера только
        // через треть срока выборов, а записи прошлых term остаются неясными (5.4.2).
        _nextHeartbeatMs = _clock.TimestampMs + _heartbeatIntervalMs;
        await SendHeartbeatsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Дописывает пустую запись своего term (<c>S2-T06</c>): снимает вопрос «закоммичено ли что-то
    /// из прошлых term» до появления правила коммита в <c>S3</c>.
    /// </summary>
    private async ValueTask AppendNoopAsync(CancellationToken cancellationToken)
    {
        var noop = LogRecord.Noop(_log.LastIndex.Next(), _state.CurrentTerm);

        // Через журнал, а не мимо него: очередь писателя (S1-T08) одна на всех.
        await _log.AppendAsync([noop], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Рассылает heartbeat всем пирам и запоминает ответы.</summary>
    private async ValueTask SendHeartbeatsAsync(CancellationToken cancellationToken)
    {
        var request = AppendEntriesRequest.Heartbeat(
            Id,
            _state.CurrentTerm,
            _log.LastIndex,
            _log.LastTerm,
            _log.CommittedIndex);

        var sends = _progress.Keys
            .Select(peer => SendAppendEntriesAsync(peer, request, cancellationToken).AsTask())
            .ToArray();

        var replies = await Task.WhenAll(sends).ConfigureAwait(false);

        foreach (var reply in replies)
        {
            if (reply.Response is not { } response)
            {
                continue;
            }

            if (RaftRoleTransitions.Decide(_state.CurrentTerm, response.Term) == StepKind.StepDown)
            {
                // Пир живёт в большем терме: лидер устарел, уходить надо сразу.
                await StepDownAsync(response.Term, cancellationToken).ConfigureAwait(false);

                return;
            }

            if (_progress.TryGetValue(reply.Peer, out var progress))
            {
                // Любой ответ в нашем терме - это «пир жив», даже если журнал у него не сошёлся
                // (S3 отвечает Success: false отставшему): отставший, но живой пир всё равно
                // часть кворума, иначе исправный лидер уходил бы из-за чужого отставания.
                _progress[reply.Peer] = progress with { LastResponseMs = _clock.TimestampMs };
            }
        }
    }

    private async ValueTask<AppendEntriesReply> SendAppendEntriesAsync(
        NodeId peer,
        AppendEntriesRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _transport.AppendEntriesAsync(peer, request, cancellationToken).ConfigureAwait(false);

            return new AppendEntriesReply(peer, response);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PeerUnreachableException)
        {
            // Пир недоступен: лидер без кворума узнает об этом из CheckQuorum (S2-T09), а не здесь.
            // Ловится только объявленный отказ транспорта: сбой в обработчике пира - не «нет ответа».
            return new AppendEntriesReply(peer, null);
        }
    }

    /// <inheritdoc />
    public async ValueTask<PreVoteResponse> HandlePreVoteAsync(
        PreVoteRequest request,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var granted = !IsLeaderContactFresh()
                && PreVoteRules.WouldGrant(request, _state.CurrentTerm, _log.LastTerm, _log.LastIndex);

            // Ни term, ни голос, ни таймер: ответ на PreVote - это только намерение. Сброс таймера
            // здесь позволил бы любому узлу глушить выборы кластера одним опросом.
            return new PreVoteResponse(_state.CurrentTerm, granted);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<AppendEntriesResponse> HandleAppendEntriesAsync(
        AppendEntriesRequest request,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            switch (RaftRoleTransitions.Decide(_state.CurrentTerm, request.Term))
            {
                case StepKind.Ignore:
                    // Отставший лидер: ответ с настоящим термом - единственная подсказка, что он устарел.
                    return new AppendEntriesResponse(_state.CurrentTerm, Success: false);

                case StepKind.StepDown:
                case StepKind.ProcessInTerm:
                    // Оба случая - уход в follower: в большем терме это обязательно, а в своём
                    // лидер этого терма уже есть, и это не мы (кандидат проиграл выборы).
                    // Для своего терма StepDownAsync не пишет на диск: терм не вырос.
                    await StepDownAsync(request.Term, cancellationToken).ConfigureAwait(false);
                    break;
            }

            _state.LeaderId = request.Leader;

            // Валидный heartbeat - то самое «слышал лидера»: по нему же считается окно stickiness
            // и запрет на новые выборы, пока лидер жив (S2-T09).
            _lastLeaderContactMs = _clock.TimestampMs;

            return new AppendEntriesResponse(_state.CurrentTerm, Success: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Уход в follower (<c>S2-T08</c>): единственное место, где меняются роль, терм и голос.
    /// </summary>
    /// <param name="newTerm">Терм, из-за которого уходим; текущий терм - «проиграли в этом же терме».</param>
    /// <param name="cancellationToken">Отмена до записи на диск.</param>
    /// <remarks>
    /// <para>
    /// На диск пишется только рост терма: смена роли без роста ничего не делает долговечным, а
    /// голос в том же терме обязан остаться - второй голос в одном терме запрещён (§5.2).
    /// Поэтому вызов идемпотентен: два шага с одним термом дают одну запись.
    /// </para>
    /// <para>
    /// Здесь же умирают ресурсы роли: репликаторы и таймеры. Отправка после <c>StepDown</c> - это
    /// сообщение устаревшего лидера, то есть путь к двум лидерам.
    /// </para>
    /// </remarks>
    private async ValueTask StepDownAsync(Term newTerm, CancellationToken cancellationToken)
    {
        if (newTerm > _state.CurrentTerm)
        {
            await _state.PersistAsync(newTerm, votedFor: null, cancellationToken).ConfigureAwait(false);
        }

        _state.Role = RaftRole.Follower;
        _state.LeaderId = null;
        LeaveRole();
    }

    /// <summary>Кворум отвечал лидеру в пределах окна (<c>S2-T09</c>, правило 6 спецификации).</summary>
    /// <param name="nowMs">Текущее монотонное время.</param>
    /// <remarks>
    /// Состав кворума спрашивается у <see cref="IClusterConfiguration"/>, как и в выборах: своего
    /// счётчика «большинства» здесь нет. Себя лидер считает ответившим всегда - собственный голос
    /// и есть его часть кворума. Учитывается только ответ, а не факт отправки: «отправил» и
    /// «услышал» - разные вещи при партиции.
    /// </remarks>
    private bool HasResponsiveQuorum(long nowMs)
    {
        if (!_checkQuorum)
        {
            return true;
        }

        var responders = new List<NodeId> { Id };

        foreach (var progress in _progress.Values)
        {
            if (nowMs - progress.LastResponseMs < _quorumWindowMs)
            {
                responders.Add(progress.Peer);
            }
        }

        return _configuration.IsQuorum(responders);
    }

    /// <summary>
    /// Слышали ли валидный heartbeat лидера в пределах окна <c>T</c> (<c>S2-T09</c>, правило 5).
    /// </summary>
    /// <remarks>
    /// Отметка не сбрасывается уходом в follower: это факт о прошлом, а не о роли, и без него
    /// кандидат с большим term получал бы голос сразу после первого же запроса.
    /// </remarks>
    private bool IsLeaderContactFresh()
        => _checkQuorum
            && _lastLeaderContactMs is { } heard
            && _clock.TimestampMs - heard < _quorumWindowMs;

    /// <summary>Уборка ролевых ресурсов: репликаторы и сигнал таймера выборов.</summary>
    /// <remarks>
    /// Отдельно от <see cref="StepDownAsync"/>, потому что смену терма и голоса умеет и
    /// <c>RequestVoteHandler</c> (одна запись на диск вместо двух), а уборка нужна всем путям.
    /// </remarks>
    private void LeaveRole()
    {
        _progress.Clear();
        ResetElectionTimer();
    }
}
