using RaftBroker.Core.Primitives;

namespace RaftBroker.Storage.Log;

/// <summary>
/// Что старт сделал с журналом на диске и где теперь кончается история.
/// </summary>
/// <remarks>
/// <para>
/// Восстановление при старте (<c>S1-T07</c>) меняет диск, поэтому обязано отчитываться, а не
/// молчать: усечённый хвост - это потеря байтов, и вызывающий должен иметь возможность
/// положить её в лог узла, не читая файлы повторно.
/// </para>
/// <para>
/// Позиции (<see cref="FirstIndex"/>, <see cref="LastIndex"/>, <see cref="LastTerm"/>) - снимок
/// момента открытия: они же доступны на самом журнале, и после первого append разойдутся с ним.
/// Хранятся здесь, потому что старт - единственное место, где отчёт и открытый журнал рождаются
/// одновременно, и сверять их потом вызывающему нечем.
/// </para>
/// <para>
/// Нулевое значение - журнал не открывался (отказ): тогда причина отказа лежит в
/// <see cref="LogStoreOpenResult.Reason"/>, а не здесь.
/// </para>
/// </remarks>
public readonly struct LogRecoveryReport
{
    private LogRecoveryReport(
        bool repaired,
        int truncatedSegments,
        int removedSegments,
        long droppedBytes,
        string path,
        string reason,
        LogIndex firstIndex,
        LogIndex lastIndex,
        Term lastTerm)
    {
        Repaired = repaired;
        TruncatedSegments = truncatedSegments;
        RemovedSegments = removedSegments;
        DroppedBytes = droppedBytes;
        Path = path;
        Reason = reason;
        FirstIndex = firstIndex;
        LastIndex = lastIndex;
        LastTerm = lastTerm;
    }

    /// <summary>Журнал открыт.</summary>
    public bool IsOpen { get; init; }

    /// <summary>Восстановление изменило диск: что-то усечено или удалено.</summary>
    public bool Repaired { get; }

    /// <summary>Сколько сегментов усечено по оборванному хвосту (§9.2).</summary>
    public int TruncatedSegments { get; }

    /// <summary>Сколько оборванных созданий сегмента удалено (§9.3).</summary>
    public int RemovedSegments { get; }

    /// <summary>Сколько байт отброшено: усечённые хвосты плюс удалённые файлы.</summary>
    public long DroppedBytes { get; }

    /// <summary>Последний файл, который тронуло восстановление; пустая строка, если ремонта не было.</summary>
    public string Path { get; }

    /// <summary>Почему восстановление поступило так; пустая строка, если ремонта не было.</summary>
    public string Reason { get; }

    /// <summary>Первая доступная запись журнала на момент открытия.</summary>
    public LogIndex FirstIndex { get; }

    /// <summary>Последняя запись журнала на момент открытия.</summary>
    public LogIndex LastIndex { get; }

    /// <summary>Терм последней записи на момент открытия.</summary>
    public Term LastTerm { get; }

    /// <summary>Собирает отчёт по итогам ремонта и открытому журналу.</summary>
    /// <param name="repairs">Что восстановление сделало, по порядку.</param>
    /// <param name="store">Открытый журнал: источник позиций.</param>
    internal static LogRecoveryReport For(IReadOnlyList<SegmentRecoveryResult> repairs, ILogStore store)
    {
        var truncated = 0;
        var removed = 0;
        long dropped = 0;

        foreach (var repair in repairs)
        {
            if (repair.Status == SegmentRecoveryStatus.TruncatedTail)
            {
                truncated++;
            }
            else
            {
                removed++;
            }

            dropped += repair.DroppedBytes;
        }

        var last = repairs.Count == 0 ? default : repairs[^1];

        return new LogRecoveryReport(
            repaired: repairs.Count > 0,
            truncatedSegments: truncated,
            removedSegments: removed,
            droppedBytes: dropped,
            path: last.Path ?? string.Empty,
            reason: last.Reason ?? string.Empty,
            firstIndex: store.FirstIndex,
            lastIndex: store.LastIndex,
            lastTerm: store.LastTerm)
        { IsOpen = true };
    }
}
