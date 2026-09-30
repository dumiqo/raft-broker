namespace RaftBroker.Storage.Log;

/// <summary>
/// Результат разбора заголовка сегмента.
/// </summary>
/// <remarks>
/// Отдельный тип от <see cref="LogReadStatus"/> даёт то же разделение, что и у фрейма:
/// <see cref="LogReadStatus.Truncated"/> означает файл короче заголовка (оборванное создание
/// сегмента, §9.3 документа), <see cref="LogReadStatus.Corrupt"/> - заголовок есть целиком,
/// но нечитаем.
/// </remarks>
public readonly struct SegmentHeaderReadResult
{
    private SegmentHeaderReadResult(LogReadStatus status, SegmentHeader? header, string reason)
    {
        Status = status;
        Header = header;
        Reason = reason;
    }

    /// <summary>Статус разбора.</summary>
    public LogReadStatus Status { get; }

    /// <summary>Разобранный заголовок; <c>null</c>, если разбор не удался.</summary>
    public SegmentHeader? Header { get; }

    /// <summary>Причина отказа; пустая строка при успехе.</summary>
    public string Reason { get; }

    /// <summary>Разбор удался.</summary>
    public bool IsSuccess => Status == LogReadStatus.Success;

    /// <summary>Успешный разбор.</summary>
    /// <param name="header">Разобранный заголовок.</param>
    internal static SegmentHeaderReadResult Success(SegmentHeader header)
        => new(LogReadStatus.Success, header, string.Empty);

    /// <summary>Файл короче заголовка.</summary>
    /// <param name="reason">Причина.</param>
    internal static SegmentHeaderReadResult Truncated(string reason)
        => new(LogReadStatus.Truncated, null, reason);

    /// <summary>Заголовок есть целиком, но нарушает формат.</summary>
    /// <param name="reason">Причина.</param>
    internal static SegmentHeaderReadResult Corrupt(string reason)
        => new(LogReadStatus.Corrupt, null, reason);
}
