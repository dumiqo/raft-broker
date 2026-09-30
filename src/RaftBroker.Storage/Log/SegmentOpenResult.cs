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
    private SegmentOpenResult(LogReadStatus status, SegmentReader? reader, long length, string reason)
    {
        Status = status;
        Reader = reader;
        Length = length;
        Reason = reason;
    }

    /// <summary>Статус открытия.</summary>
    public LogReadStatus Status { get; }

    /// <summary>Открытый сегмент; <c>null</c>, если открыть не удалось.</summary>
    /// <remarks>
    /// При неуспехе файл уже закрыт: владельца у него нет, и утечки дескриптора не будет.
    /// </remarks>
    public SegmentReader? Reader { get; }

    /// <summary>
    /// Длина файла на момент открытия - даже тогда, когда сегмент открыть не удалось.
    /// </summary>
    /// <remarks>
    /// Нужна отчёту о восстановлении (<c>S1-T05</c>): удаляя оборванное создание (§9.3), он
    /// сообщает, сколько байт было отброшено, а повторно открывать файл ради размера незачем -
    /// он всё равно только что был открыт.
    /// </remarks>
    public long Length { get; }

    /// <summary>Причина отказа; пустая строка при успехе.</summary>
    public string Reason { get; }

    /// <summary>Сегмент открыт.</summary>
    public bool IsSuccess => Status == LogReadStatus.Success;

    /// <summary>Сегмент открыт.</summary>
    /// <param name="reader">Читатель сегмента.</param>
    internal static SegmentOpenResult Success(SegmentReader reader)
        => new(LogReadStatus.Success, reader, reader.Length, string.Empty);

    /// <summary>Файл короче заголовка.</summary>
    /// <param name="length">Длина файла.</param>
    /// <param name="reason">Причина.</param>
    internal static SegmentOpenResult Truncated(long length, string reason)
        => new(LogReadStatus.Truncated, null, length, reason);

    /// <summary>Заголовок есть целиком, но нарушает формат или не совпадает с именем файла.</summary>
    /// <param name="length">Длина файла.</param>
    /// <param name="reason">Причина.</param>
    internal static SegmentOpenResult Corrupt(long length, string reason)
        => new(LogReadStatus.Corrupt, null, length, reason);
}
