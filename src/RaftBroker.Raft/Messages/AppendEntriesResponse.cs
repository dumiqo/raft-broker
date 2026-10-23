using RaftBroker.Core.Primitives;

namespace RaftBroker.Raft.Messages;

/// <summary>
/// Ответ на AppendEntries (<c>S2-T06</c>): term получателя и признак согласия.
/// </summary>
/// <remarks>
/// Отказ с большим term - единственный способ, которым follower сообщает лидеру, что тот устарел.
/// Поля подсказки для отката <c>nextIndex</c> появятся в <c>S3-T02</c> вместе с самой репликацией.
/// </remarks>
/// <param name="Term">Текущий term ответившего (может быть больше term лидера).</param>
/// <param name="Success">Согласие на сообщение.</param>
public readonly record struct AppendEntriesResponse(Term Term, bool Success);
