using RaftBroker.Core.Primitives;

namespace RaftBroker.Raft.Messages;

/// <summary>
/// Ответ на запрос голоса (<c>S2-T04</c>, <c>proto/raft.proto</c>, <c>RequestVoteResponse</c>).
/// </summary>
/// <param name="Term">Term отвечающего узла: кандидат обязан увидеть больший term и уйти в follower.</param>
/// <param name="VoteGranted">Дан ли голос.</param>
/// <remarks>
/// Term в ответе есть всегда, а не только при отказе: без него кандидат с устаревшим term
/// продолжал бы раунд и собирал отказы, не зная причины.
/// </remarks>
public readonly record struct RequestVoteResponse(Term Term, bool VoteGranted);
