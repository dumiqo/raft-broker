using RaftBroker.Core.Primitives;

namespace RaftBroker.Raft;

/// <summary>
/// Сообщение не дошло до пира (<c>S2-T05</c>): нет соединения, истёк deadline, пир не зарегистрирован.
/// </summary>
/// <remarks>
/// <para>
/// Объявленный отказ транспорта, а не «любое исключение»: узел ловит <b>только</b> этот тип и
/// превращает его в «ответа нет». Всё остальное (сбой в обработчике пира, ошибка в самом узле)
/// летит наверх - иначе баг внутри обработчика выглядел бы как недоступный пир, а фаззер
/// <c>S2-T10</c> вместо падения показал бы просто медленные выборы.
/// </para>
/// <para>
/// В бою этот тип бросает gRPC-транспорт (<c>S4-T01</c>) на ошибку соединения и истёкший deadline.
/// </para>
/// </remarks>
public sealed class PeerUnreachableException : Exception
{
    /// <summary>Создаёт исключение для недоставленного сообщения.</summary>
    /// <param name="from">Отправитель.</param>
    /// <param name="to">Получатель.</param>
    public PeerUnreachableException(NodeId from, NodeId to)
        : base($"Сообщение от '{from}' к '{to}' не доставлено: нет связи или получателя.")
    {
        From = from;
        To = to;
    }

    /// <summary>Отправитель недоставленного сообщения.</summary>
    public NodeId From { get; }

    /// <summary>Получатель недоставленного сообщения.</summary>
    public NodeId To { get; }
}
