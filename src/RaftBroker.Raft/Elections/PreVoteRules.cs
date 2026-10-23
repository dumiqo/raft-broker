using RaftBroker.Core.Primitives;
using RaftBroker.Raft.Messages;

namespace RaftBroker.Raft.Elections;

/// <summary>
/// Таблица правил PreVote (<c>S2-T07</c>, правило 4 спецификации этапа).
/// </summary>
/// <remarks>
/// <para>
/// Чистая функция, как и <see cref="RequestVoteRules"/>: ни term, ни <c>VotedFor</c>, ни диск здесь
/// не меняются - это и есть весь смысл PreVote. Согласие означает только «я бы проголосовал»,
/// а настоящий голос будет отдельным раундом.
/// </para>
/// <para>
/// Проверка up-to-date переиспользуется из <see cref="RequestVoteRules.IsUpToDate"/>: если бы у
/// PreVote была своя копия, негативный тест перестал бы проверять оба пути.
/// </para>
/// </remarks>
public static class PreVoteRules
{
    /// <summary>Дал бы узел голос кандидату, не меняя своего состояния.</summary>
    /// <param name="request">Запрос PreVote.</param>
    /// <param name="currentTerm">Текущий term отвечающего.</param>
    /// <param name="localLastLogTerm">Term последней записи отвечающего.</param>
    /// <param name="localLastLogIndex">Индекс последней записи отвечающего.</param>
    /// <returns>Согласие или отказ; состояние узла не меняется ни в одном случае.</returns>
    /// <remarks>
    /// Правило сравнения term выбрано в ADR <c>0016-prevote.md</c>: больший term кандидата даёт
    /// согласие без разбора лога (кандидат всё равно уйдёт вперёд), равный - только если лог
    /// кандидата не старше. Меньший term - отказ: это кандидат из прошлого.
    /// </remarks>
    public static bool WouldGrant(
        PreVoteRequest request,
        Term currentTerm,
        Term localLastLogTerm,
        LogIndex localLastLogIndex)
    {
        if (request.Term < currentTerm)
        {
            return false;
        }

        return request.Term > currentTerm
            || RequestVoteRules.IsUpToDate(
                request.LastLogIndex,
                request.LastLogTerm,
                localLastLogIndex,
                localLastLogTerm);
    }
}
