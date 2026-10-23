namespace RaftBroker.Storage.Log;

/// <summary>
/// Результат поиска записи по индексу: сама запись либо причина, по которой её нет.
/// </summary>
/// <remarks>
/// Отсутствие записи - не ошибка: и «ещё не дописана», и «уже сжата снапшотом» вызывающий обязан
/// обработать, но по-разному (<see cref="LogLookupStatus"/>). Поэтому результат-значение,
/// а не <c>null</c>: различать эти два случая обязан контракт, а не догадка вызывающего.
/// Исключение остаётся для ошибок вызывающего и для порчи файла на диске уже после открытия
/// журнала - такую порчу нельзя выдавать за «записи нет».
/// </remarks>
public readonly struct LogLookupResult
{
    private LogLookupResult(LogLookupStatus status, LogRecord? record)
    {
        Status = status;
        Record = record;
    }

    /// <summary>Итог поиска.</summary>
    public LogLookupStatus Status { get; }

    /// <summary>Найденная запись; <c>null</c>, если <see cref="Status"/> не <see cref="LogLookupStatus.Found"/>.</summary>
    public LogRecord? Record { get; }

    /// <summary>Запись найдена.</summary>
    /// <param name="record">Запись журнала.</param>
    internal static LogLookupResult Found(LogRecord record)
        => new(LogLookupStatus.Found, record);

    /// <summary>Записи с таким индексом ещё нет.</summary>
    internal static LogLookupResult NotYet()
        => new(LogLookupStatus.NotYet, null);

    /// <summary>Запись вошла в снапшот.</summary>
    internal static LogLookupResult Compacted()
        => new(LogLookupStatus.Compacted, null);
}
