using RaftBroker.Core.Primitives;

namespace RaftBroker.Storage.Log;

/// <summary>
/// Содержимое заголовка сегмента: то, что записано в первых 30 байтах файла.
/// </summary>
/// <remarks>
/// Полей ровно два: индекс первой записи сегмента и момент создания файла. Версия формата
/// в объекте не хранится - её знает кодек (это константа сборки), а в файле она лежит,
/// чтобы читатель мог отказаться от неизвестного формата, а не догадываться.
/// <see cref="CreatedAtUnixMs"/> - информационное поле: оно не участвует ни в одной проверке
/// корректности и существует только для диагностики.
/// </remarks>
public sealed record SegmentHeader
{
    /// <summary>Создаёт заголовок сегмента.</summary>
    /// <param name="baseIndex">Индекс первой записи сегмента; он же в имени файла.</param>
    /// <param name="createdAtUnixMs">Момент создания файла в миллисекундах Unix.</param>
    /// <exception cref="ArgumentOutOfRangeException">Индекс меньше единицы.</exception>
    public SegmentHeader(LogIndex baseIndex, long createdAtUnixMs)
    {
        if (baseIndex.Value < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(baseIndex),
                baseIndex.Value,
                "Первый сегмент журнала начинается с записи 1, нулевой baseIndex невозможен.");
        }

        BaseIndex = baseIndex;
        CreatedAtUnixMs = createdAtUnixMs;
    }

    /// <summary>Индекс первой записи сегмента.</summary>
    public LogIndex BaseIndex { get; }

    /// <summary>Момент создания файла, миллисекунды Unix. Только для диагностики.</summary>
    public long CreatedAtUnixMs { get; }

    /// <inheritdoc />
    public override string ToString() => $"сегмент с {BaseIndex}, создан {CreatedAtUnixMs} мс Unix";
}
