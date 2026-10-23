using RaftBroker.Core.Primitives;
using RaftBroker.Raft;
using RaftBroker.Raft.Messages;

namespace RaftBroker.TestKit.Cluster;

/// <summary>
/// Транспорт поверх <see cref="InProcCluster"/> (<c>S2-T05</c>): доставка в обработчик пира и его ответ.
/// </summary>
/// <param name="cluster">Сеть, через которую идут сообщения.</param>
/// <param name="self">Узел-отправитель.</param>
internal sealed class InProcTransport(InProcCluster cluster, NodeId self) : IPeerTransport
{
    /// <inheritdoc />
    public ValueTask<RequestVoteResponse> RequestVoteAsync(
        NodeId peer,
        RequestVoteRequest request,
        CancellationToken cancellationToken)
        => cluster.SendAsync(
            self,
            peer,
            request,
            static (handler, message, token) => handler.HandleRequestVoteAsync(message, token),
            cancellationToken);

    /// <inheritdoc />
    public ValueTask<AppendEntriesResponse> AppendEntriesAsync(
        NodeId peer,
        AppendEntriesRequest request,
        CancellationToken cancellationToken)
        => cluster.SendAsync(
            self,
            peer,
            request,
            static (handler, message, token) => handler.HandleAppendEntriesAsync(message, token),
            cancellationToken);
}
