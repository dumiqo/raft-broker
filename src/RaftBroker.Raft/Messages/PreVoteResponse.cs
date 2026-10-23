using RaftBroker.Core.Primitives;

namespace RaftBroker.Raft.Messages;

/// <summary>
/// Ответ на PreVote (<c>S2-T07</c>): дал бы голос или нет.
/// </summary>
/// <remarks>
/// <see cref="Term"/> - текущий term ответившего, а не term кандидата: ответ не двигает term ни у
/// кого, он лишь сообщает, где живёт получатель.
/// </remarks>
/// <param name="Term">Текущий term ответившего.</param>
/// <param name="VoteGranted">Дал бы голос.</param>
public readonly record struct PreVoteResponse(Term Term, bool VoteGranted);
