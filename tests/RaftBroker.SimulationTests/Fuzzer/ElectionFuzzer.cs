using RaftBroker.Core.Primitives;
using RaftBroker.Core.Randomness;
using RaftBroker.Raft;
using RaftBroker.Raft.Elections;
using RaftBroker.Raft.Messages;
using RaftBroker.Raft.State;
using RaftBroker.TestKit.Cluster;
using RaftBroker.TestKit.Randomness;

namespace RaftBroker.SimulationTests.Fuzzer;

/// <summary>
/// Фаззер выборов (<c>S2-T10</c>): случайные паузы, партиции, падения и перезапуски узлов на
/// виртуальном времени, с проверкой инвариантов после каждого шага.
/// </summary>
/// <remarks>
/// <para>
/// Детерминизм держится на трёх вещах: весь «случай» приходит из <see cref="SeededRandomSource"/>
/// (ноль обращений к <c>Random.Shared</c>), время двигает только <see cref="TestClock"/>, а узлы
/// обходятся в фиксированном порядке слотов. Один и тот же сид даёт одну и ту же
/// <see cref="ElectionFuzzResult.Timeline"/> - это и проверяет тест детерминизма.
/// </para>
/// <para>
/// Задержки в сети не моделируются ожиданием: недоставка - это партиция или упавший узел, а не
/// <c>Task.Delay</c>. Поэтому тысяча прогонов идёт секунды, а не часы.
/// </para>
/// <para>
/// История событий копится всегда, но печатается только при падении (<see cref="ElectionSafety.Fail"/>):
/// иначе вывод тысячи прогонов стал бы нечитаемым.
/// </para>
/// </remarks>
public static class ElectionFuzzer
{
    /// <summary>Запускает один воспроизводимый прогон.</summary>
    /// <param name="seed">Сид: одна и та же пара (сид, параметры) даёт один и тот же прогон.</param>
    /// <param name="options">Параметры прогона; по умолчанию три узла, PreVote и CheckQuorum включены.</param>
    /// <returns>Итог: сид и история смен лидера.</returns>
    /// <exception cref="ElectionSafetyViolationException">Нарушен инвариант протокола.</exception>
    public static async ValueTask<ElectionFuzzResult> RunAsync(int seed, ElectionFuzzerOptions? options = null)
    {
        var settings = options ?? new ElectionFuzzerOptions();
        var scenario = new SeededRandomSource((ulong)seed + 1UL);
        var safety = new ElectionSafety(seed);

        await using var cluster = await RaftCluster.CreateAsync(new RaftClusterOptions
        {
            NodeCount = settings.NodeCount,
            Election = settings.Election,
            PreVote = settings.PreVote,
            CheckQuorum = settings.CheckQuorum,
            RandomFor = index => new SeededRandomSource(((ulong)seed + 1UL) * 31UL + (ulong)index),
        }).ConfigureAwait(false);

        var liar = settings.LyingVoter
            ? cluster.Nodes[0].Id
            : (NodeId?)null;

        if (liar is { } voter)
        {
            cluster.Network.Register(voter, new LyingVoter(cluster[voter]));
        }

        var down = new Dictionary<NodeId, DownNode>();
        var crashes = 0;
        var restarts = 0;

        for (var step = 0; step < settings.Steps; step++)
        {
            ChurnNetwork(cluster, scenario);

            var (crashed, restarted) = await CrashOrRestartAsync(cluster, scenario, step, down, liar, safety)
                .ConfigureAwait(false);
            crashes += crashed;
            restarts += restarted;

            await cluster.AdvanceAsync(TimeSpan.FromMilliseconds(scenario.Next(1, settings.MaxStepMs + 1)))
                .ConfigureAwait(false);
            safety.Observe(cluster.Nodes, step);
        }

        return new ElectionFuzzResult(seed, safety.Timeline, crashes, restarts);
    }

    /// <summary>Меняет связность: лечит сеть, изолирует узел или перерезает пару направлений.</summary>
    private static void ChurnNetwork(RaftCluster cluster, IRandomSource scenario)
    {
        var live = cluster.Nodes;

        switch (scenario.Next(0, 12))
        {
            case 0 or 1:
                cluster.Network.Heal();
                break;
            case 2 or 3:
                cluster.Network.Isolate(live[scenario.Next(0, live.Count)].Id);
                break;
            case 4 or 5:
                cluster.Network.Partition(
                    live[scenario.Next(0, live.Count)].Id,
                    live[scenario.Next(0, live.Count)].Id);
                break;
            case 6 or 7:
                var from = live[scenario.Next(0, live.Count)].Id;
                var to = live[scenario.Next(0, live.Count)].Id;
                cluster.Network.Partition(from, to);
                cluster.Network.Partition(to, from);
                break;
            default:
                break;
        }
    }

    /// <summary>Роняет или возвращает узлы; при возврате сверяет hard state до и после падения.</summary>
    /// <returns>Сколько узлов упало и сколько вернулось на этом шаге.</returns>
    private static async ValueTask<(int Crashes, int Restarts)> CrashOrRestartAsync(
        RaftCluster cluster,
        IRandomSource scenario,
        int step,
        Dictionary<NodeId, DownNode> down,
        NodeId? liar,
        ElectionSafety safety)
    {
        var restarts = 0;

        foreach (var id in down.Where(entry => entry.Value.RestartAtStep <= step).Select(entry => entry.Key).Order().ToArray())
        {
            var before = down[id];
            down.Remove(id);

            var node = await cluster.RestartAsync(id).ConfigureAwait(false);

            if (liar == id)
            {
                cluster.Network.Register(id, new LyingVoter(node));
            }

            if (node.CurrentTerm != before.Term || node.VotedFor != before.VotedFor)
            {
                safety.Fail(
                    step,
                    $"R6: узел '{id}' потерял hard state при рестарте: было (term {before.Term}, голос {before.VotedFor}), "
                    + $"стало (term {node.CurrentTerm}, голос {node.VotedFor})");
            }

            if (node.Role != RaftRole.Follower)
            {
                safety.Fail(step, $"узел '{id}' после рестарта в роли {node.Role}, а не follower");
            }

            restarts++;
        }

        if (cluster.Nodes.Count <= 1 || scenario.Next(0, 15) != 0)
        {
            return (0, restarts);
        }

        var victim = cluster.Nodes[scenario.Next(0, cluster.Nodes.Count)];
        down[victim.Id] = new DownNode(victim.CurrentTerm, victim.VotedFor, step + scenario.Next(1, 12));

        await cluster.StopAsync(victim.Id).ConfigureAwait(false);

        return (1, restarts);
    }

    /// <summary>Что узел записал на диск к моменту падения: с этим сверяется рестарт.</summary>
    private sealed record DownNode(Term Term, NodeId? VotedFor, int RestartAtStep);

    /// <summary>
    /// Мутация «второй голос в term»: избиратель подтверждает голос, даже когда уже голосовал.
    /// </summary>
    /// <remarks>
    /// Настоящий узел внутри продолжает работать по правилам (и пишет голос на диск), а наружу
    /// отвечает «да» всегда. Этого достаточно, чтобы два кандидата в одном term собрали кворум и
    /// R1 сработал: мутационный прогон обязан упасть, иначе неизвестно, что фаззер вообще ловит.
    /// </remarks>
    private sealed class LyingVoter(IRaftMessageHandler inner) : IRaftMessageHandler
    {
        /// <inheritdoc />
        public async ValueTask<RequestVoteResponse> HandleRequestVoteAsync(
            RequestVoteRequest request,
            CancellationToken cancellationToken)
        {
            var response = await inner.HandleRequestVoteAsync(request, cancellationToken).ConfigureAwait(false);

            return response with { VoteGranted = true };
        }

        /// <inheritdoc />
        public ValueTask<PreVoteResponse> HandlePreVoteAsync(
            PreVoteRequest request,
            CancellationToken cancellationToken) => inner.HandlePreVoteAsync(request, cancellationToken);

        /// <inheritdoc />
        public ValueTask<AppendEntriesResponse> HandleAppendEntriesAsync(
            AppendEntriesRequest request,
            CancellationToken cancellationToken) => inner.HandleAppendEntriesAsync(request, cancellationToken);
    }
}

/// <summary>Параметры прогона фаззера (<c>S2-T10</c>).</summary>
public sealed class ElectionFuzzerOptions
{
    /// <summary>Число узлов; по умолчанию три.</summary>
    public int NodeCount { get; init; } = 3;

    /// <summary>Сколько шагов делает прогон: шаг - это смена связности, падение и отрезок времени.</summary>
    public int Steps { get; init; } = 120;

    /// <summary>Верхняя граница случайного шага времени в миллисекундах.</summary>
    public int MaxStepMs { get; init; } = 40;

    /// <summary>Срок таймера выборов; меньше значения по умолчанию - больше выборов на прогон.</summary>
    public ElectionTimerOptions Election { get; init; } = new(TimeSpan.FromMilliseconds(60));

    /// <summary>Включён ли PreVote: прогоны идут и с ним, и без него.</summary>
    public bool PreVote { get; init; } = true;

    /// <summary>Включён ли CheckQuorum и leader stickiness.</summary>
    public bool CheckQuorum { get; init; } = true;

    /// <summary>
    /// Включена ли мутация «второй голос в term». По умолчанию нет: с ней любой прогон обязан падать.
    /// </summary>
    public bool LyingVoter { get; init; }
}

/// <summary>Итог прогона фаззера.</summary>
/// <param name="Seed">Сид, которым прогон воспроизводится.</param>
/// <param name="Timeline">Смены лидера по шагам: отпечаток прогона для проверки детерминизма.</param>
/// <param name="Crashes">Сколько раз узлы падали.</param>
/// <param name="Restarts">Сколько раз узлы возвращались в кластер.</param>
public sealed record ElectionFuzzResult(int Seed, IReadOnlyList<string> Timeline, int Crashes, int Restarts)
{
    /// <summary>Сколько раз в прогоне кто-то становился лидером.</summary>
    public int LeaderChanges => Timeline.Count;
}
