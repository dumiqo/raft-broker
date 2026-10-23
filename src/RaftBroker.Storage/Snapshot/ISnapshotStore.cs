using RaftBroker.Core.Primitives;

namespace RaftBroker.Storage.Snapshot;

/// <summary>
/// Хранилище снапшотов: целые файлы состояния на индекс последней вошедшей записи.
/// </summary>
/// <remarks>
/// <para>
/// Контракт этапа S1 (<c>plan.md</c>, раздел контрактов). Формат - <c>docs/formats/snapshot.md</c>,
/// решения - <c>docs/adr/0012-snapshot-file-and-atomic-publication.md</c>.
/// </para>
/// <para>
/// Байты состояния хранилище не толкует: их сериализует приложение. Хранилище отвечает за то,
/// чтобы файл либо целиком валиден, либо не существовал: запись идёт во временный файл,
/// сбрасывается на устройство и публикуется переименованием (§7).
/// </para>
/// <para>
/// Порча снапшота - отказ, а не «снапшота нет»: единственная копия состояния не подменяется
/// пустым состоянием молча.
/// </para>
/// </remarks>
public interface ISnapshotStore
{
    /// <summary>Находит актуальный снапшот в каталоге.</summary>
    /// <param name="cancellationToken">Отмена до обращения к диску.</param>
    /// <returns>
    /// Метаданные снапшота с наибольшим <see cref="SnapshotMetadata.LastIncludedIndex"/> или
    /// <c>null</c>, если снапшотов нет. Недописанные временные файлы при этом удаляются.
    /// </returns>
    /// <exception cref="InvalidDataException">Файл не читается как снапшот этого формата.</exception>
    ValueTask<SnapshotMetadata?> GetLatestAsync(CancellationToken cancellationToken);

    /// <summary>Записывает снапшот и публикует его: целый файл или ничего.</summary>
    /// <param name="metadata">Индекс и терм последней вошедшей записи.</param>
    /// <param name="state">Байты состояния приложения.</param>
    /// <param name="cancellationToken">Отмена до публикации.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Индекс не положителен или состояние длиннее <see cref="SnapshotFormat.MaxStateLength"/>.
    /// </exception>
    /// <exception cref="IOException">
    /// Файл с таким индексом уже существует или запись не удалась. Недописанный временный файл
    /// остаётся в каталоге и удаляется при следующем обращении к нему.
    /// </exception>
    ValueTask WriteAsync(SnapshotMetadata metadata, ReadOnlyMemory<byte> state, CancellationToken cancellationToken);

    /// <summary>Читает состояние снапшота.</summary>
    /// <param name="metadata">Метаданные снапшота, которые возвращает <see cref="GetLatestAsync"/>.</param>
    /// <param name="cancellationToken">Отмена до чтения.</param>
    /// <exception cref="InvalidDataException">
    /// Файла нет или он не читается как снапшот этого формата, либо его метаданные не совпадают
    /// с переданными.
    /// </exception>
    ValueTask<ReadOnlyMemory<byte>> ReadStateAsync(SnapshotMetadata metadata, CancellationToken cancellationToken);

    /// <summary>Удаляет снапшоты, которые полностью ниже указанного индекса.</summary>
    /// <param name="index">Индекс, ниже которого снапшоты больше не нужны.</param>
    /// <param name="cancellationToken">Отмена между удалениями.</param>
    /// <remarks>
    /// Снапшот с наибольшим индексом не удаляется никогда, даже если он ниже
    /// <paramref name="index"/>: узел не остаётся без состояния, которое уже отдал follower-ам (§8).
    /// </remarks>
    ValueTask DeleteOlderThanAsync(LogIndex index, CancellationToken cancellationToken);
}
