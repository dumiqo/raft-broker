using RaftBroker.Raft.Messages;

namespace RaftBroker.Raft;

/// <summary>
/// Приёмник RPC Raft (<c>S2-T05</c>): то, что транспорт вызывает на стороне получателя.
/// </summary>
/// <remarks>
/// Отдельный интерфейс, а не сам <see cref="RaftNode"/>: in-proc транспорт в тестах и фаззере
/// (<c>S2-T10</c>) должен уметь доставить сообщение в заглушку и не требовать весь узел.
/// Обработчики никогда не отправляют сообщения обратно: ответ - возвращаемое значение, поэтому
/// взаимные вызовы двух узлов не превращаются в клинч.
/// </remarks>
public interface IRaftMessageHandler
{
    /// <summary>Обрабатывает запрос голоса.</summary>
    ValueTask<RequestVoteResponse> HandleRequestVoteAsync(RequestVoteRequest request, CancellationToken cancellationToken);

    /// <summary>Обрабатывает AppendEntries: heartbeat обновляет представление follower о лидере.</summary>
    ValueTask<AppendEntriesResponse> HandleAppendEntriesAsync(AppendEntriesRequest request, CancellationToken cancellationToken);
}
