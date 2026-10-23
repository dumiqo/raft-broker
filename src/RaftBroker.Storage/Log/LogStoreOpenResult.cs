namespace RaftBroker.Storage.Log;

/// <summary>
/// Результат открытия журнала: хранилище либо причина, по которой журнал открыть нельзя.
/// </summary>
/// <remarks>
/// <para>
/// Статусы те же, что у открытия сегмента (<see cref="SegmentOpenResult"/>): <see cref="LogReadStatus.Success"/> -
/// журнал открыт, <see cref="LogReadStatus.Truncated"/> - файл (последний сегмент) оборван и требует
/// восстановления (§9.2, §9.3), <see cref="LogReadStatus.Corrupt"/> - структурная порча (§9.4).
/// </para>
/// <para>
/// Исключением это не объявляется по той же причине, что и у сегмента: восстановление
/// (<c>S1-T05</c>, <c>S1-T07</c>) - отдельный шаг, и его отсутствие не должно превращаться
/// в \"непонятное исключение из недр хранилища\". Отличать \"нужно восстановление\" от \"порча\"
/// обязательно: первое чинится автоматически, второе останавливает старт.
/// </para>
/// </remarks>
public readonly struct LogStoreOpenResult
{
    private LogStoreOpenResult(LogReadStatus status, ILogStore? store, string path, string reason, LogRecoveryReport report)
    {
        Status = status;
        Store = store;
        Path = path;
        Reason = reason;
        Report = report;
    }

    /// <summary>Статус открытия.</summary>
    public LogReadStatus Status { get; }

    /// <summary>Открытый журнал; <c>null</c>, если открыть не удалось.</summary>
    /// <remarks>
    /// Наружу - интерфейс, а не <see cref="LogStore"/>: вызывающему нужен контракт журнала,
    /// а не знание о сегментах, и тест может подменить реализацию.
    /// </remarks>
    public ILogStore? Store { get; }

    /// <summary>Файл, на котором открытие остановилось; пустая строка при успехе.</summary>
    public string Path { get; }

    /// <summary>Причина отказа; пустая строка при успехе.</summary>
    public string Reason { get; }

    /// <summary>Что восстановление сделало с журналом на диске; нулевое значение при отказе.</summary>
    /// <remarks>
    /// Отчёт есть только у успешного открытия: при <see cref="LogReadStatus.Truncated"/> и
    /// <see cref="LogReadStatus.Corrupt"/> восстанавливать нечего или нельзя, и причина лежит в
    /// <see cref="Reason"/>. Так у вызывающего один источник ответа «почему не открылось».
    /// </remarks>
    public LogRecoveryReport Report { get; }

    /// <summary>Журнал открыт.</summary>
    public bool IsSuccess => Status == LogReadStatus.Success;

    /// <summary>Журнал открыт.</summary>
    /// <param name="store">Хранилище.</param>
    /// <param name="report">Что восстановление сделало с журналом на диске.</param>
    internal static LogStoreOpenResult Success(ILogStore store, LogRecoveryReport report)
        => new(LogReadStatus.Success, store, string.Empty, string.Empty, report);

    /// <summary>Журнал нарушает формат: автоматическое исправление запрещено.</summary>
    /// <param name="path">Путь файла или каталога.</param>
    /// <param name="reason">Причина.</param>
    internal static LogStoreOpenResult Corrupt(string path, string reason)
        => new(LogReadStatus.Corrupt, null, path, reason, default);
}
