using RaftBroker.Core.Primitives;
using RaftBroker.Raft;
using RaftBroker.Raft.State;

namespace RaftBroker.SimulationTests.Fuzzer;

/// <summary>
/// Инварианты выборов, проверяемые после каждого шага фаззера (<c>S2-T10</c>).
/// </summary>
/// <remarks>
/// <para>
/// Проверки независимы от <see cref="RaftNode"/>: они смотрят только на публичное состояние
/// (<see cref="RaftNode.CurrentTerm"/>, <see cref="RaftNode.VotedFor"/>, <see cref="RaftNode.Role"/>,
/// <see cref="RaftNode.LeaderId"/>). Поэтому одна и та же проверка ловит и настоящую ошибку протокола,
/// и намеренно сломанный шаг мутационного прогона.
/// </para>
/// <para>
/// Что проверяется:
/// </para>
/// <list type="bullet">
/// <item>R1 Election Safety - не больше одного лидера на term;</item>
/// <item>R6 - голос узла в term не меняется, в том числе после перезапуска;</item>
/// <item>term узла не убывает;</item>
/// <item>ушедший лидер не возвращается лидером в том же term и не указывает на себя как на лидера.</item>
/// </list>
/// </remarks>
internal sealed class ElectionSafety
{
    private readonly int _seed;
    private readonly Dictionary<Term, NodeId> _leadersByTerm = [];
    private readonly Dictionary<(NodeId Node, Term Term), NodeId> _votes = [];
    private readonly Dictionary<NodeId, Term> _highestTermSeen = [];
    private readonly HashSet<(NodeId Node, Term Term)> _resigned = [];
    private readonly List<string> _timeline = [];

    /// <summary>Создаёт проверку, привязанную к сиду прогона: сид попадает в текст падения.</summary>
    /// <param name="seed">Сид прогона, которым прогон воспроизводится.</param>
    public ElectionSafety(int seed) => _seed = seed;

    /// <summary>Смены лидера по шагам - компактный отпечаток прогона для проверки детерминизма.</summary>
    public IReadOnlyList<string> Timeline => _timeline;

    /// <summary>Проверяет состояние всех живых узлов после шага.</summary>
    /// <param name="nodes">Живые узлы в порядке шагов.</param>
    /// <param name="step">Номер шага: попадает в сообщение о нарушении.</param>
    /// <exception cref="ElectionSafetyViolationException">Нарушен один из инвариантов.</exception>
    public void Observe(IReadOnlyList<RaftNode> nodes, int step)
    {
        foreach (var node in nodes)
        {
            var term = node.CurrentTerm;

            if (_highestTermSeen.TryGetValue(node.Id, out var highest) && term < highest)
            {
                Fail(step, $"term узла '{node.Id}' убывает: {highest} -> {term}");
            }

            _highestTermSeen[node.Id] = term;
            ObserveVote(node, term, step);
            ObserveRole(node, term, step);
        }
    }

    /// <summary>Сообщает о нарушении: текст содержит сид, шаг, последние события и команду повтора.</summary>
    /// <param name="step">Номер шага.</param>
    /// <param name="violation">Что именно нарушено.</param>
    /// <exception cref="ElectionSafetyViolationException">Всегда.</exception>
    public void Fail(int step, string violation) => throw new ElectionSafetyViolationException(
        $"Сид {_seed}, шаг {step}: {violation}.{Environment.NewLine}"
        + $"Последние события:{Environment.NewLine}{string.Join(Environment.NewLine, _timeline.TakeLast(10))}{Environment.NewLine}"
        + $"Повторить: ElectionFuzzer.RunAsync({_seed}).");

    private void ObserveVote(RaftNode node, Term term, int step)
    {
        if (node.VotedFor is not { } votedFor)
        {
            return;
        }

        if (_votes.TryGetValue((node.Id, term), out var previous) && previous != votedFor)
        {
            Fail(step, $"R6: узел '{node.Id}' голосовал в term {term} за '{previous}', а теперь за '{votedFor}'");
        }

        _votes[(node.Id, term)] = votedFor;
    }

    private void ObserveRole(RaftNode node, Term term, int step)
    {
        if (node.Role != RaftRole.Leader)
        {
            if (_leadersByTerm.TryGetValue(term, out var leader) && leader == node.Id)
            {
                _resigned.Add((node.Id, term));
            }

            if (node.LeaderId == node.Id)
            {
                Fail(step, $"узел '{node.Id}' не лидер, но LeaderId указывает на себя");
            }

            return;
        }

        if (_leadersByTerm.TryGetValue(term, out var existing) && existing != node.Id)
        {
            Fail(step, $"R1: в term {term} уже был лидер '{existing}', а '{node.Id}' тоже лидер");
        }

        if (_resigned.Contains((node.Id, term)))
        {
            Fail(step, $"узел '{node.Id}' снова лидер в term {term} после ухода");
        }

        if (!_leadersByTerm.ContainsKey(term))
        {
            _leadersByTerm[term] = node.Id;
            _timeline.Add($"шаг {step}: '{node.Id}' лидер в term {term}");
        }
    }
}
