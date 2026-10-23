using RaftBroker.Core.Primitives;

namespace RaftBroker.Raft.State;

/// <summary>
/// Что узел делает с входящим термом (<c>S2-T08</c>): три исхода на все сообщения.
/// </summary>
/// <remarks>
/// Отдельный тип вместо четырёх значений «стать кандидатом / стать лидером»: победа и таймаут -
/// это не переходы по терму входящего сообщения, а события узла, и смешивать их в одной функции
/// значит проверять в матрице то, чего в матрице нет.
/// </remarks>
internal enum StepKind
{
    /// <summary>Терм входящего меньше: сообщение устарело, состояние не меняется.</summary>
    Ignore = 0,

    /// <summary>Терм входящего больше: сначала <c>StepDownAsync</c>, потом обработка.</summary>
    StepDown = 1,

    /// <summary>Терм тот же: обычная обработка по роли, диск не трогается.</summary>
    ProcessInTerm = 2,
}

/// <summary>
/// Матрица переходов по терму (<c>S2-T08</c>): роль × (терм меньше, равен, больше).
/// </summary>
/// <remarks>
/// <para>
/// Единственное место, где решается «уходить в follower или нет». Раньше это решение повторялось
/// в каждом обработчике и уже успело разойтись: где-то забывался сброс <c>VotedFor</c>, где-то
/// не очищались репликаторы. Роль в разборе не участвует: переход по терму от неё не зависит,
/// она нужна дальше - обработчику, который знает, что делать с сообщением своего терма.
/// </para>
/// <para>
/// Отдельно <see cref="Decide"/> не покрывает переходы без входящего терма: проигранные выборы
/// (<c>Candidate → Follower</c> по heartbeat своего терма) и победа (<c>Candidate → Leader</c>).
/// Первый идёт через <c>StepDownAsync</c> с текущим термом (без записи на диск), второй - через
/// <c>BecomeLeaderAsync</c>.
/// </para>
/// </remarks>
internal static class RaftRoleTransitions
{
    /// <summary>Разбирает входящий терм.</summary>
    /// <param name="currentTerm">Текущий терм узла.</param>
    /// <param name="incomingTerm">Терм из сообщения.</param>
    /// <returns>Что делать с термом; роль на решение не влияет.</returns>
    public static StepKind Decide(Term currentTerm, Term incomingTerm)
    {
        var comparison = incomingTerm.CompareTo(currentTerm);

        if (comparison < 0)
        {
            return StepKind.Ignore;
        }

        return comparison > 0 ? StepKind.StepDown : StepKind.ProcessInTerm;
    }
}
