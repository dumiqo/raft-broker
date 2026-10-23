using RaftBroker.Core.Primitives;
using RaftBroker.Raft.Messages;

namespace RaftBroker.Raft.Elections;

/// <summary>
/// Один раунд выборов (<c>S2-T05</c>): term и запрос фиксируются на момент отправки.
/// </summary>
/// <param name="Id">Номер раунда: ответы прошлых раундов не применяются.</param>
/// <param name="Term">Term, в котором идёт раунд.</param>
/// <param name="Request">Запрос голоса, одинаковый для всех пиров.</param>
/// <param name="Peers">Пиры, у которых просят голос; себя в списке нет.</param>
internal sealed record ElectionRound(long Id, Term Term, RequestVoteRequest Request, NodeId[] Peers);

/// <summary>Ответ пира на запрос голоса; <c>null</c> - пир не ответил (<c>S2-T05</c>).</summary>
/// <param name="Peer">Кто отвечал.</param>
/// <param name="Response">Ответ или <c>null</c>, если связи не было.</param>
internal readonly record struct VoteReply(NodeId Peer, RequestVoteResponse? Response);

/// <summary>Ответ пира на AppendEntries; <c>null</c> - пир не ответил (<c>S2-T06</c>).</summary>
/// <param name="Peer">Кто отвечал.</param>
/// <param name="Response">Ответ или <c>null</c>, если связи не было.</param>
internal readonly record struct AppendEntriesReply(NodeId Peer, AppendEntriesResponse? Response);
