using RaftBroker.Core.Primitives;
using RaftBroker.Raft;
using RaftBroker.Raft.Messages;

namespace RaftBroker.TestKit.Cluster;

/// <summary>
/// In-proc сеть для узлов Raft (<c>S2-T05</c>): доставка RPC в обработчик получателя, партиции и счётчики.
/// </summary>
/// <remarks>
/// <para>
/// Доставка синхронная: <see cref="IPeerTransport.RequestVoteAsync"/> вызывает обработчик пира и
/// возвращает его ответ. Это законно, потому что узел <b>никогда</b> не ждёт пира, держа свои
/// воротца (раунд выборов идёт в три фазы, см. <c>RaftNode</c>): взаимные вызовы двух узлов
/// не превращаются в клинч. Задержки и потери моделируются партициями, а не ожиданием, поэтому
/// прогон на виртуальных часах остаётся детерминированным.
/// </para>
/// <para>
/// Сеть помнит, кто зарегистрирован: сообщение в незарегистрированный или отрезанный узел
/// бросает <see cref="PeerUnreachableException"/>, то есть ровно то, что узел обязан пережить.
/// </para>
/// </remarks>
public sealed class InProcCluster
{
    private readonly Dictionary<NodeId, IRaftMessageHandler> _handlers = [];
    private readonly HashSet<(NodeId From, NodeId To)> _cut = [];

    /// <summary>Сколько сообщений сеть приняла в доставку.</summary>
    public int SentCount { get; private set; }

    /// <summary>Сколько сообщений не дошло из-за партиции или отсутствия получателя.</summary>
    public int DroppedCount { get; private set; }

    /// <summary>Все узлы, зарегистрированные в сети.</summary>
    public IReadOnlyCollection<NodeId> Nodes => [.. _handlers.Keys.Order()];

    /// <summary>Регистрирует получателя RPC.</summary>
    /// <param name="node">Идентификатор узла.</param>
    /// <param name="handler">Кто принимает сообщения.</param>
    public void Register(NodeId node, IRaftMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        _handlers[node] = handler;
    }

    /// <summary>Создаёт транспорт, который отправляет сообщения от имени узла.</summary>
    /// <param name="self">Отправитель.</param>
    public IPeerTransport CreateTransport(NodeId self) => new InProcTransport(this, self);

    /// <summary>Перерезает направление <paramref name="from"/> → <paramref name="to"/>.</summary>
    public void Partition(NodeId from, NodeId to) => _cut.Add((from, to));

    /// <summary>Изолирует узел: сообщения не доходят ни от него, ни к нему.</summary>
    /// <param name="node">Кого изолировать.</param>
    public void Isolate(NodeId node)
    {
        foreach (var other in _handlers.Keys)
        {
            _cut.Add((node, other));
            _cut.Add((other, node));
        }
    }

    /// <summary>Возвращает сеть в исходное состояние: все направления снова работают.</summary>
    public void Heal() => _cut.Clear();

    internal ValueTask<TResponse> SendAsync<TRequest, TResponse>(
        NodeId from,
        NodeId to,
        TRequest request,
        Func<IRaftMessageHandler, TRequest, CancellationToken, ValueTask<TResponse>> dispatch,
        CancellationToken cancellationToken)
    {
        SentCount++;

        if (_cut.Contains((from, to)) || !_handlers.TryGetValue(to, out var handler))
        {
            DroppedCount++;

            return ValueTask.FromException<TResponse>(new PeerUnreachableException(from, to));
        }

        return dispatch(handler, request, cancellationToken);
    }
}
