namespace RaftBroker.Storage.Log;

/// <summary>
/// Результат открытия сегмента: читатель, либо причина, по которой сегмент открыть нельзя.
/// </summary>
/// <remarks>
/// Статусы те же, что у разбора заголовка (<see cref="LogReadStatus"/>), и означают то же:
/// <see cref="LogReadStatus.Truncated"/> - файл короче заголовка (оборванное создание сегмента,
/// §9.3), <see cref="LogReadStatus.Corrupt"/> - заголовок есть целиком, но нечитаем либо не
/// совпадает с именем файла (§9.4). Исключением это не объявляется по той же причине, что и у
/// кодека: что делать с таким сегментом, решает восстановление (<c>S1-T05</c>) - в последнем
/// сегменте короткий файл отбрасывается, а нечитаемый заголовок останавливает старт ради
/// сохранности данных.
/// </remarks>
public readonly struct SegmentOpenResult
{
    private SegmentOpenResult(LogReadStatus status, SegmentReader? reader, string reason)
    {
        Status = status;
        Reader = reader;
        Reason = reason;
    }

    /// <summary>Статус открытия.</summary>
    public LogReadStatus Status { get; }

    /// <summary>Открытый сегмент; <c>null</c>, если открыть не удалось.</summary>
    /// <remarks>
    /// При неуспехе файл уже закрыт: владельца у него нет, и утечки дескриптора не будет.
    /// </remarks>
    public SegmentReader? Reader { get; }

    /// <summary>Причина отказа; пустая строка при успехе.</summary>
    public string Reason { get; }

    /// <summary>Сегмент открыт.</summary>
    public bool IsSuccess => Status == LogReadStatus.Success;

    /// <summary>Сегмент открыт.</summary>
    /// <param name="reader">Читатель сегмента.</param>
    internal static SegmentOpenResult Success(SegmentReader reader)
        => new(LogReadStatus.Success, reader, string.Empty);

    /// <summary>Файл короче заголовка.</summary>
    /// <param name="reason">Причина.</param>
    internal static SegmentOpenResult Truncated(string reason)
        => new(LogReadStatus.Truncated, null, reason);

    /// <summary>Заголовок есть целиком, но нарушает формат или не совпадает с именем файла.</summary>
    /// <param name="reason">Причина.</param>
    internal static SegmentOpenResult Corrupt(string reason)
        => new(LogReadStatus.Corrupt, null, reason);
}
