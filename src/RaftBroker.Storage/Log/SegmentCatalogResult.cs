namespace RaftBroker.Storage.Log;

/// <summary>
/// Результат чтения каталога журнала: сегменты по порядку либо причина, по которой журнал
/// открыть нельзя.
/// </summary>
/// <remarks>
/// Статусы те же, что у открытия одного сегмента (<see cref="LogReadStatus"/>):
/// <see cref="LogReadStatus.Truncated"/> - оборван последний сегмент (нужен шаг восстановления,
/// <c>S1-T07</c>), <see cref="LogReadStatus.Corrupt"/> - структурная порча (старт запрещён).
/// Результат, а не исключение: испорченный журнал - штатный вход восстановления, а не сбой
/// вызывающего. Пустой каталог - успех с пустым списком: журнал ещё не создан.
/// </remarks>
public readonly struct SegmentCatalogResult
{
    private SegmentCatalogResult(
        LogReadStatus status,
        IReadOnlyList<SegmentCatalogEntry> entries,
        string path,
        string reason)
    {
        Status = status;
        Entries = entries;
        Path = path;
        Reason = reason;
    }

    /// <summary>Статус чтения каталога.</summary>
    public LogReadStatus Status { get; }

    /// <summary>Сегменты по возрастанию базового индекса; пустой список при неуспехе.</summary>
    public IReadOnlyList<SegmentCatalogEntry> Entries { get; }

    /// <summary>Файл, на котором чтение остановилось; пустая строка при успехе.</summary>
    public string Path { get; }

    /// <summary>Причина отказа; пустая строка при успехе.</summary>
    public string Reason { get; }

    /// <summary>Каталог прочитан.</summary>
    public bool IsSuccess => Status == LogReadStatus.Success;

    /// <summary>Каталог прочитан.</summary>
    /// <param name="entries">Сегменты по порядку.</param>
    internal static SegmentCatalogResult Success(IReadOnlyList<SegmentCatalogEntry> entries)
        => new(LogReadStatus.Success, entries, string.Empty, string.Empty);

    /// <summary>Последний сегмент оборван: нужен шаг восстановления.</summary>
    /// <param name="path">Путь файла.</param>
    /// <param name="reason">Причина.</param>
    internal static SegmentCatalogResult Truncated(string path, string reason)
        => new(LogReadStatus.Truncated, [], path, reason);

    /// <summary>Каталог нарушает формат: автоматическое исправление запрещено.</summary>
    /// <param name="path">Путь файла или каталога.</param>
    /// <param name="reason">Причина.</param>
    internal static SegmentCatalogResult Corrupt(string path, string reason)
        => new(LogReadStatus.Corrupt, [], path, reason);
}
