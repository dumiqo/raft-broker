using RaftBroker.Core.Primitives;
using RaftBroker.Raft.Messages;

namespace RaftBroker.Raft.Elections;

/// <summary>Решение по запросу голоса (<c>S2-T04</c>): что сделать до ответа.</summary>
/// <remarks>
/// Отдельный тип вместо пары «bool + bool»: обработчик обязан различать «отказ без записи» и
/// «смена term с отказом», и перепутать их - значит либо забыть fsync, либо записать лишний term.
/// </remarks>
public enum VoteDecision
{
    /// <summary>Отказ, состояние не меняется, диска не касаемся.</summary>
    Deny = 0,

    /// <summary>Голос в текущем term: пишем только <c>votedFor</c>.</summary>
    Grant = 1,

    /// <summary>Больший term: пишем term и голос, узел становится follower.</summary>
    StepDownAndGrant = 2,

    /// <summary>Больший term, но лог кандидата устарел: пишем term, голоса не даём.</summary>
    StepDownAndDeny = 3,
}

/// <summary>
/// Правила голосования на выборах (<c>S2-T04</c>): term, up-to-date (§5.4.1), один голос на term.
/// </summary>
/// <remarks>
/// <para>
/// Чистая функция без состояния и без диска: таблица случаев из спецификации этапа читается
/// как таблица, а побочные эффекты (fsync, смена роли) остаются в <see cref="RequestVoteHandler"/>.
/// </para>
/// <para>
/// Порядок проверок - часть правила, а не стиль:
/// </para>
/// <list type="number">
/// <item>меньший term отбивается сразу: диск не трогаем, потому что ничего не меняется;</item>
/// <item>больший term обнуляет голос <b>до</b> вопроса о логе: иначе следующий term унаследовал бы чужой голос;</item>
/// <item>повторный голос тому же кандидату даётся без пересмотра лога: потерянный ответ - нормальный случай,
/// а лог короче не становится.</item>
/// </list>
/// </remarks>
public static class RequestVoteRules
{
    /// <summary>Решает, что делать с запросом голоса.</summary>
    /// <param name="request">Запрос кандидата.</param>
    /// <param name="currentTerm">Текущий term узла.</param>
    /// <param name="votedFor">За кого узел голосовал в текущем term; <c>null</c> - не голосовал.</param>
    /// <param name="lastLogTerm">Term последней записи локального журнала; 0 - журнал пуст.</param>
    /// <param name="lastLogIndex">Индекс последней записи локального журнала; 0 - журнал пуст.</param>
    public static VoteDecision Decide(
        RequestVoteRequest request,
        Term currentTerm,
        NodeId? votedFor,
        Term lastLogTerm,
        LogIndex lastLogIndex)
    {
        if (request.Term < currentTerm)
        {
            return VoteDecision.Deny;
        }

        if (request.Term > currentTerm)
        {
            return IsUpToDate(request, lastLogTerm, lastLogIndex)
                ? VoteDecision.StepDownAndGrant
                : VoteDecision.StepDownAndDeny;
        }

        if (votedFor == request.Candidate)
        {
            return VoteDecision.Grant;
        }

        if (votedFor is not null)
        {
            return VoteDecision.Deny;
        }

        return IsUpToDate(request, lastLogTerm, lastLogIndex) ? VoteDecision.Grant : VoteDecision.Deny;
    }

    /// <summary>
    /// Правило §5.4.1: лог кандидата не менее актуален, если сравнивать пару <c>(term, index)</c> целиком.
    /// </summary>
    /// <remarks>
    /// Сначала term, потом index: узел с записью term 7 index 1 актуальнее узла с term 6 index 100,
    /// потому что term растёт только через выборы, а index - через записи одного лидера.
    /// </remarks>
    public static bool IsUpToDate(
        LogIndex candidateLastLogIndex,
        Term candidateLastLogTerm,
        LogIndex lastLogIndex,
        Term lastLogTerm)
        => candidateLastLogTerm > lastLogTerm
            || (candidateLastLogTerm == lastLogTerm && candidateLastLogIndex >= lastLogIndex);

    private static bool IsUpToDate(RequestVoteRequest request, Term lastLogTerm, LogIndex lastLogIndex)
        => IsUpToDate(request.LastLogIndex, request.LastLogTerm, lastLogIndex, lastLogTerm);
}
