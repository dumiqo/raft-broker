using RaftBroker.Core.Primitives;

namespace RaftBroker.Storage.Log;

/// <summary>
/// Результат полного прохода по сегменту: до какого смещения он валиден и что в нём было.
/// </summary>
/// <remarks>
/// Смещения считаются от начала файла, поэтому результат можно класть в отчёт о восстановлении
/// (<c>S1-T05</c>) без пересчёта: <see cref="ValidEndOffset"/> - это конец последнего валидного
/// фрейма, он же начало хвоста при <see cref="SegmentScanStatus.TailProblem"/>.
/// </remarks>
public readonly struct SegmentScanResult
{
    private SegmentScanResult(
        SegmentScanStatus status,
        string reason,
        long validEndOffset,
        int frameCount,
        LogIndex? lastIndex,
        Term? lastTerm)
    {
        Status = status;
        Reason = reason;
        ValidEndOffset = validEndOffset;
        FrameCount = frameCount;
        LastIndex = lastIndex;
        LastTerm = lastTerm;
    }

    /// <summary>Итог прохода.</summary>
    public SegmentScanStatus Status { get; }

    /// <summary>Причина остановки; пустая строка при <see cref="SegmentScanStatus.Complete"/>.</summary>
    public string Reason { get; }

    /// <summary>Смещение за последним валидным фреймом: <c>30</c>, если валидных фреймов нет.</summary>
    public long ValidEndOffset { get; }

    /// <summary>Число валидных фреймов.</summary>
    public int FrameCount { get; }

    /// <summary>Индекс последней валидной записи; <c>null</c> для пустого сегмента.</summary>
    public LogIndex? LastIndex { get; }

    /// <summary>Терм последней валидной записи; <c>null</c> для пустого сегмента.</summary>
    public Term? LastTerm { get; }

    /// <summary>Файл разобран до конца: последний фрейм кончился ровно на конце файла.</summary>
    public bool IsComplete => Status == SegmentScanStatus.Complete;

    /// <summary>Проход дошёл до конца файла.</summary>
    /// <param name="validEndOffset">Смещение за последним валидным фреймом.</param>
    /// <param name="frameCount">Число валидных фреймов.</param>
    /// <param name="lastIndex">Индекс последней валидной записи.</param>
    /// <param name="lastTerm">Терм последней валидной записи.</param>
    internal static SegmentScanResult Complete(long validEndOffset, int frameCount, LogIndex? lastIndex, Term? lastTerm)
        => new(SegmentScanStatus.Complete, string.Empty, validEndOffset, frameCount, lastIndex, lastTerm);

    /// <summary>Разбор остановился на невалидном хвосте.</summary>
    /// <param name="reason">Что именно не сошлось.</param>
    /// <param name="validEndOffset">Смещение первого невалидного фрейма.</param>
    /// <param name="frameCount">Число валидных фреймов до остановки.</param>
    /// <param name="lastIndex">Индекс последней валидной записи.</param>
    /// <param name="lastTerm">Терм последней валидной записи.</param>
    internal static SegmentScanResult TailProblem(string reason, long validEndOffset, int frameCount, LogIndex? lastIndex, Term? lastTerm)
        => new(SegmentScanStatus.TailProblem, reason, validEndOffset, frameCount, lastIndex, lastTerm);
}
