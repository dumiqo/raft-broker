using RaftBroker.Core.Primitives;
using RaftBroker.Storage.Log;
using RaftBroker.Storage.Snapshot;

namespace RaftBroker.Storage.Compaction;

/// <summary>
/// Сжатие истории: снапшот на диск, проверка, что он читается, и только потом обрезка журнала.
/// </summary>
/// <remarks>
/// <para>
/// Порядок шагов - единственное, что защищает инвариант R4 («снапшот плюс хвост лога»,
/// <c>docs/invariants.md</c>). Снапшот пишется первым, читается обратно и сверяется с тем, что
/// просили записать; журнал обрезается только после этого. Обратный порядок дал бы окно, в
/// котором сегменты уже удалены, а снапшота ещё нет, - то есть отставший follower не смог бы
/// догнать узел ни продолжением репликации, ни снапшотом.
/// </para>
/// <para>
/// Крах в любой точке даёт одно из двух состояний: старый снапшот с целым журналом или новый
/// снапшот с журналом, обрезанным не дальше снапшота. «Дыры» между снапшотом и началом журнала
/// не бывает ни при каком порядке удаления, потому что удаление идёт с самых старых сегментов
/// и каждый удаляется целиком (<c>docs/adr/0013-compaction-order.md</c>).
/// </para>
/// <para>
/// Что сжимать - не решение хранилища: снапшот приходит от применения записей, а триггер по
/// числу применённых записей - задача <c>S3-T08</c>. Поэтому индекс снапшота не сравнивается
/// с точкой коммита: это две разные границы, и хранилище не имеет права угадывать, какая из
/// них ограничивает удаление.
/// </para>
/// </remarks>
public sealed class LogCompactor
{
    private readonly ILogStore _log;
    private readonly ISnapshotStore _snapshots;

    /// <summary>Создаёт сжатие над парой хранилищ.</summary>
    /// <param name="log">Журнал, который обрезается.</param>
    /// <param name="snapshots">Хранилище снапшотов, которое публикует состояние.</param>
    public LogCompactor(ILogStore log, ISnapshotStore snapshots)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(snapshots);

        _log = log;
        _snapshots = snapshots;
    }

    /// <summary>Публикует снапшот и обрезает журнал до него.</summary>
    /// <param name="metadata">Индекс и терм последней записи, вошедшей в снапшот.</param>
    /// <param name="state">Байты состояния приложения.</param>
    /// <param name="cancellationToken">Отмена до публикации снапшота.</param>
    /// <returns>Метаданные опубликованного снапшота.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// В каталоге уже есть снапшот на больший индекс: снапшот не ходит назад.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// Снапшот записан, но не читается обратно. Журнал в этом случае не тронут: удалять историю,
    /// за которую нечего предъявить, нельзя.
    /// </exception>
    /// <remarks>
    /// Повторный вызов с тем же снапшотом не пишет файл заново: после краха между публикацией и
    /// обрезкой старт повторяет компакцию ровно этим вызовом, а публикация с заменой запрещена
    /// (<c>docs/formats/snapshot.md</c> §7). Уже опубликованный снапшот всё равно перечитывается.
    /// </remarks>
    public async ValueTask<SnapshotMetadata> CompactAsync(
        SnapshotMetadata metadata,
        ReadOnlyMemory<byte> state,
        CancellationToken cancellationToken)
    {
        var published = await _snapshots.GetLatestAsync(cancellationToken).ConfigureAwait(false);

        if (published is { } current && current.LastIncludedIndex > metadata.LastIncludedIndex)
        {
            throw new ArgumentOutOfRangeException(
                nameof(metadata),
                metadata.LastIncludedIndex.Value,
                $"Снапшот не ходит назад: в каталоге уже есть снапшот на {current.LastIncludedIndex}.");
        }

        if (published != metadata)
        {
            await _snapshots.WriteAsync(metadata, state, cancellationToken).ConfigureAwait(false);
        }

        await VerifyPublishedAsync(metadata, cancellationToken).ConfigureAwait(false);

        // Обрезка после проверки. Отмена здесь не проверяется намеренно: журнал не должен
        // остаться обрезанным наполовину, а каждый удалённый сегмент целиком покрыт снапшотом.
        await _log.CompactAsync(metadata.LastIncludedIndex, cancellationToken).ConfigureAwait(false);

        // Старые снапшоты уходят последними: пока журнал не обрезан, они - запасной путь.
        await _snapshots.DeleteOlderThanAsync(metadata.LastIncludedIndex, cancellationToken).ConfigureAwait(false);

        return metadata;
    }

    /// <summary>Читает снапшот обратно с диска: и заголовок, и состояние с его CRC.</summary>
    /// <remarks>
    /// Проверка идёт через файловую систему, а не по памяти: смысл шага в том, чтобы после
    /// возврата <see cref="SnapshotStore.WriteAsync"/> убедиться, что файл действительно
    /// опубликован и цел, а не что вызов не бросил исключение.
    /// </remarks>
    private async ValueTask VerifyPublishedAsync(SnapshotMetadata metadata, CancellationToken cancellationToken)
    {
        var latest = await _snapshots.GetLatestAsync(cancellationToken).ConfigureAwait(false);

        if (latest != metadata)
        {
            throw new InvalidDataException(
                $"Снапшот {metadata.LastIncludedIndex}/{metadata.LastIncludedTerm} не опубликован: "
                + $"актуальный в каталоге - {latest?.LastIncludedIndex.ToString() ?? "нет"}. Журнал не обрезан.");
        }

        _ = await _snapshots.ReadStateAsync(metadata, cancellationToken).ConfigureAwait(false);
    }
}
