using RaftBroker.Core.Primitives;
using RaftBroker.Raft.Messages;

namespace RaftBroker.Raft.Elections;

/// <summary>
/// Вид раунда: PreVote ничего не меняет, настоящий раунд двигает term и голос (<c>S2-T07</c>).
/// </summary>
internal enum ElectionRoundKind
{
    /// <summary>Предварительный опрос: «дали бы голос?».</summary>
    PreVote = 0,

    /// <summary>Настоящие выборы: term поднят, голос за себя записан.</summary>
    Vote = 1,
}

/// <summary>
/// Один раунд выборов (<c>S2-T05</c>): term и запрос фиксируются на момент отправки.
/// </summary>
/// <param name="Id">Номер раунда: ответы прошлых раундов не применяются.</param>
/// <param name="Kind">Вид раунда: PreVote или настоящие выборы.</param>
/// <param name="NodeTerm">Term узла на момент старта раунда: по нему ответы проверяются на свежесть.</param>
/// <param name="Term">Term, от имени которого идёт раунд (в запросе).</param>
/// <param name="Request">Запрос голоса: поля те же, что нужны PreVote.</param>
/// <param name="Peers">Пиры, у которых просят голос; себя в списке нет.</param>
internal sealed record ElectionRound(
    long Id,
    ElectionRoundKind Kind,
    Term NodeTerm,
    Term Term,
    RequestVoteRequest Request,
    NodeId[] Peers);

/// <summary>
/// Ответ пира на раунд выборов, приведённый к общему виду (<c>S2-T07</c>).
/// </summary>
/// <param name="Peer">Кто отвечал.</param>
/// <param name="Term">Term ответившего.</param>
/// <param name="Granted">Согласие.</param>
/// <param name="Answered">Пир вообще ответил; <c>false</c> - связи не было.</param>
internal readonly record struct ElectionReply(NodeId Peer, Term Term, bool Granted, bool Answered);

/// <summary>Ответ пира на AppendEntries; <c>null</c> - пир не ответил (<c>S2-T06</c>).</summary>
/// <param name="Peer">Кто отвечал.</param>
/// <param name="Response">Ответ или <c>null</c>, если связи не было.</param>
internal readonly record struct AppendEntriesReply(NodeId Peer, AppendEntriesResponse? Response);
