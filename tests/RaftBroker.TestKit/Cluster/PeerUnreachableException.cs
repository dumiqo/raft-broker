using RaftBroker.Core.Primitives;

namespace RaftBroker.TestKit.Cluster;

/// <summary>
/// Сообщение не дошло: партиция или получателя нет (<c>S2-T05</c>).
/// </summary>
/// <remarks>
/// В бою такую роль играет ошибка gRPC или истёкший deadline (<c>S4-T01</c>). Узел на любой сбой
/// связи реагирует одинаково - «ответа нет», поэтому тип исключения важен только тесту.
/// </remarks>
public sealed class PeerUnreachableException : Exception
{
    /// <summary>Создаёт исключение для недоставленного сообщения.</summary>
    /// <param name="from">Отправитель.</param>
    /// <param name="to">Получатель.</param>
    public PeerUnreachableException(NodeId from, NodeId to)
        : base($"Сообщение от '{from}' к '{to}' не доставлено: сеть перерезана или получатель не зарегистрирован.")
    {
    }
}
