using RaftBroker.Core.Primitives;

namespace RaftBroker.Raft.Messages;

/// <summary>
/// Запрос голоса на выборах (<c>S2-T04</c>, <c>proto/raft.proto</c>, <c>RequestVoteRequest</c>).
/// </summary>
/// <param name="Candidate">Кто просит голос; в proto это поле заголовка <c>RaftHeader.from</c>.</param>
/// <param name="Term">Term, в котором кандидат проводит выборы.</param>
/// <param name="LastLogIndex">Индекс последней записи журнала кандидата; 0 - журнал пуст.</param>
/// <param name="LastLogTerm">Term последней записи журнала кандидата; 0 - журнал пуст.</param>
/// <remarks>
/// Имена и смысл полей повторяют контракт <c>proto/raft.proto</c>: <c>S4-T01</c> положит их в
/// <c>RaftHeader</c> без переименований, а номера полей там заморожены <c>ProtoContractTests</c>.
/// Команда в запросе не передаётся: она в заголовке, здесь только то, что решает голосование.
/// </remarks>
public readonly record struct RequestVoteRequest(NodeId Candidate, Term Term, LogIndex LastLogIndex, Term LastLogTerm);
