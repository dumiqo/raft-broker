using RaftBroker.Core.Primitives;

namespace RaftBroker.Raft.Messages;

/// <summary>
/// PreVote (<c>S2-T07</c>): «дали бы голос, если бы я начал выборы?» - без изменения чьего-либо term.
/// </summary>
/// <remarks>
/// Отдельный тип, а не <see cref="RequestVoteRequest"/>, потому что ответы означают разное: голос
/// по RequestVote уже потрачен и записан на диск, а PreVote - только намерение. Слить их в один тип
/// значит иначе однажды посчитать PreVote настоящим голосом.
/// </remarks>
/// <param name="Candidate">Кто спрашивает.</param>
/// <param name="Term">Term, в котором кандидат начал бы выборы: его текущий term + 1.</param>
/// <param name="LastLogIndex">Индекс последней записи кандидата.</param>
/// <param name="LastLogTerm">Term последней записи кандидата.</param>
public readonly record struct PreVoteRequest(NodeId Candidate, Term Term, LogIndex LastLogIndex, Term LastLogTerm);
