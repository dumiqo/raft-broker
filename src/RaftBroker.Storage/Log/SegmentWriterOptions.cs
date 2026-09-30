namespace RaftBroker.Storage.Log;

/// <summary>
/// Настройки писателя сегментов журнала.
/// </summary>
/// <remarks>
/// Размер сегмента - конфигурация, а не формат: читатель обязан принимать сегмент любого
/// размера (<c>docs/formats/log-segment.md</c>, §8), поэтому изменение этого значения
/// не требует новой версии формата. Каталог - часть конфигурации узла; имя файла внутри
/// него задаётся форматом (§7) и не настраивается.
/// </remarks>
public sealed record SegmentWriterOptions
{
    /// <summary>Каталог, в котором лежат сегменты журнала.</summary>
    public required string Directory { get; init; }

    /// <summary>
    /// Номинальный максимум размера сегмента в байтах. Значение по умолчанию - 64 МиБ
    /// (ADR <c>0003-log-segment-format.md</c>).
    /// </summary>
    public long MaxSegmentSize { get; init; } = LogSegmentFormat.MaxSegmentSize;

    /// <summary>
    /// Режим долговечности для вызовов append без явно указанного режима.
    /// По умолчанию - <see cref="DurabilityMode.GroupCommit"/> (решение владельца 2026-09-28).
    /// </summary>
    public DurabilityMode DefaultDurabilityMode { get; init; } = DurabilityMode.GroupCommit;

    /// <summary>Проверяет настройки до первого обращения к диску.</summary>
    /// <exception cref="ArgumentException">Каталог пуст.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Максимум сегмента меньше минимального фрейма или режим долговечности не определён.
    /// </exception>
    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Directory);

        if (MaxSegmentSize < LogSegmentFormat.FrameOverhead)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxSegmentSize),
                MaxSegmentSize,
                $"Максимум сегмента меньше минимального фрейма {LogSegmentFormat.FrameOverhead} байт: ни одну запись записать не удалось бы.");
        }

        if (!Enum.IsDefined(DefaultDurabilityMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(DefaultDurabilityMode),
                DefaultDurabilityMode,
                "Неизвестный режим долговечности.");
        }
    }
}
