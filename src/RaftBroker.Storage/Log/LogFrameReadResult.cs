namespace RaftBroker.Storage.Log;

/// <summary>
/// Результат разбора одного фрейма из буфера.
/// </summary>
/// <remarks>
/// Кодек не знает ни имени файла, ни абсолютного смещения: он разбирает буфер с начала фрейма.
/// Позицию и файл добавляет тот, кто читает сегмент (<c>S1-T04</c>, <c>S1-T05</c>), иначе
/// диагностика "не сошлась CRC" осталась бы без координат.
/// </remarks>
public readonly struct LogFrameReadResult
{
    private LogFrameReadResult(LogReadStatus status, LogRecord? record, int bytesConsumed, string reason)
    {
        Status = status;
        Record = record;
        BytesConsumed = bytesConsumed;
        Reason = reason;
    }

    /// <summary>Статус разбора.</summary>
    public LogReadStatus Status { get; }

    /// <summary>Разобранная запись; <c>null</c>, если разбор не удался.</summary>
    public LogRecord? Record { get; }

    /// <summary>Сколько байт занял фрейм целиком; осмысленно при <see cref="LogReadStatus.Success"/>.</summary>
    public int BytesConsumed { get; }

    /// <summary>Причина отказа; пустая строка при успехе.</summary>
    public string Reason { get; }

    /// <summary>Разбор удался.</summary>
    public bool IsSuccess => Status == LogReadStatus.Success;

    /// <summary>Успешный разбор.</summary>
    /// <param name="record">Разобранная запись.</param>
    internal static LogFrameReadResult Success(LogRecord record)
        => new(LogReadStatus.Success, record, LogFrameCodec.GetFrameSize(record.Payload.Length), string.Empty);

    /// <summary>Байты обрываются на середине фрейма.</summary>
    /// <param name="reason">Причина.</param>
    internal static LogFrameReadResult Truncated(string reason)
        => new(LogReadStatus.Truncated, null, 0, reason);

    /// <summary>Фрейм есть целиком, но нарушает формат.</summary>
    /// <param name="reason">Причина.</param>
    internal static LogFrameReadResult Corrupt(string reason)
        => new(LogReadStatus.Corrupt, null, 0, reason);
}
