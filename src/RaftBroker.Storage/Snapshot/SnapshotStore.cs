using RaftBroker.Core.Primitives;
using RaftBroker.Core.Time;
using RaftBroker.Storage.Integrity;
using RaftBroker.Storage.IO;

namespace RaftBroker.Storage.Snapshot;

/// <summary>
/// Хранилище снапшотов в каталоге: файлы <c>snapshot-{index:D20}.snap</c> и их публикация.
/// </summary>
/// <remarks>
/// <para>
/// Формат и порядок записи - <c>docs/formats/snapshot.md</c>. Публикация одна и та же на всех
/// платформах: временный файл, fsync файла, закрытие, переименование, fsync каталога (§7).
/// Промежуточного состояния, читаемого как снапшот, не существует: имя <c>.tmp</c> снапшотом
/// не считается никогда.
/// </para>
/// <para>
/// Кэша нет: актуальный снапшот определяется по каталогу при каждом обращении. Их единицы,
/// а обращение происходит на старте и при сжатии журнала (<c>S1-T10</c>); кэш появится
/// по измерению, как и кэш читателей журнала (ADR <c>0007</c>).
/// </para>
/// </remarks>
public sealed class SnapshotStore : ISnapshotStore
{
    private readonly IStorageFileSystem _fileSystem;
    private readonly IClock _clock;
    private readonly string _directory;

    /// <summary>Создаёт хранилище снапшотов в каталоге.</summary>
    /// <param name="fileSystem">Файловая система: рантайм-реализация или тестовый дубль.</param>
    /// <param name="clock">Источник времени для момента создания снапшота.</param>
    /// <param name="directory">Каталог снапшотов; создаётся при первой записи.</param>
    public SnapshotStore(IStorageFileSystem fileSystem, IClock clock, string directory)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        _fileSystem = fileSystem;
        _clock = clock;
        _directory = directory;
    }

    /// <inheritdoc />
    public async ValueTask<SnapshotMetadata?> GetLatestAsync(CancellationToken cancellationToken)
    {
        var names = await _fileSystem.ListFilesAsync(_directory, cancellationToken).ConfigureAwait(false);

        // Осиротевшие .tmp - след краха до переименования (§7). Опубликованными они не были,
        // поэтому удаляются здесь, а не считаются «почти снапшотом».
        foreach (var name in names)
        {
            if (SnapshotFormat.IsTempFileName(name))
            {
                await _fileSystem.DeleteAsync(Path.Combine(_directory, name), cancellationToken).ConfigureAwait(false);
            }
        }

        var latest = LatestSnapshotName(names);

        if (latest is null)
        {
            return null;
        }

        var header = await ReadVerifiedHeaderAsync(Path.Combine(_directory, latest), cancellationToken).ConfigureAwait(false);

        return header.Metadata;
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(
        SnapshotMetadata metadata,
        ReadOnlyMemory<byte> state,
        CancellationToken cancellationToken)
    {
        if (metadata.LastIncludedIndex.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(metadata),
                metadata.LastIncludedIndex.Value,
                "Снапшот на индексе 0 не существует: пустой журнал нечего сжимать.");
        }

        if (state.Length > SnapshotFormat.MaxStateLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(state),
                state.Length,
                $"Состояние длиннее {SnapshotFormat.MaxStateLength} байт: снапшот такого размера отвергается по длине, а не падением по памяти.");
        }

        var tempPath = Path.Combine(_directory, SnapshotFormat.GetTempFileName(metadata.LastIncludedIndex));
        var finalPath = Path.Combine(_directory, SnapshotFormat.GetFileName(metadata.LastIncludedIndex));
        var header = new byte[SnapshotFormat.HeaderSize];

        SnapshotHeaderCodec.Encode(
            metadata,
            _clock.UnixTimeMs,
            state.Length,
            Crc32C.Compute(state.Span),
            header);

        // Запись идёт только во временный файл: до переименования опубликованного снапшота
        // с этим индексом не существует, а существующий - не меняется.
        await using (var file = await _fileSystem.CreateAsync(tempPath, cancellationToken).ConfigureAwait(false))
        {
            await file.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await file.WriteAsync(state, cancellationToken).ConfigureAwait(false);
            await file.FlushToDiskAsync(cancellationToken).ConfigureAwait(false);
        }

        await _fileSystem.RenameAsync(tempPath, finalPath, cancellationToken).ConfigureAwait(false);
        await _fileSystem.FlushDirectoryAsync(_directory, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<ReadOnlyMemory<byte>> ReadStateAsync(
        SnapshotMetadata metadata,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(_directory, metadata.FileName);
        var header = await ReadVerifiedHeaderAsync(path, cancellationToken).ConfigureAwait(false);

        if (header.Metadata != metadata)
        {
            throw new InvalidDataException(
                $"Файл '{path}' содержит снапшот {header.Metadata.LastIncludedIndex}/{header.Metadata.LastIncludedTerm}, "
                + $"а запрошен {metadata.LastIncludedIndex}/{metadata.LastIncludedTerm}: метаданные не совпадают с файлом.");
        }

        return await ReadStateAsync(path, header, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DeleteOlderThanAsync(LogIndex index, CancellationToken cancellationToken)
    {
        var names = await _fileSystem.ListFilesAsync(_directory, cancellationToken).ConfigureAwait(false);
        var latest = LatestSnapshotName(names);

        foreach (var name in names)
        {
            if (!SnapshotFormat.TryParseFileName(name, out var snapshotIndex)
                || snapshotIndex >= index
                || name == latest)
            {
                continue;
            }

            await _fileSystem.DeleteAsync(Path.Combine(_directory, name), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Имя снапшота с наибольшим индексом; <c>null</c>, если снапшотов нет.</summary>
    /// <remarks>
    /// Имена фиксированной длины (§2), поэтому лексикографический порядок совпадает с числовым:
    /// отдельная сортировка по разобранным индексам не нужна.
    /// </remarks>
    private static string? LatestSnapshotName(IReadOnlyList<string> names)
    {
        string? latest = null;

        foreach (var name in names)
        {
            if (SnapshotFormat.TryParseFileName(name, out _)
                && (latest is null || string.CompareOrdinal(name, latest) > 0))
            {
                latest = name;
            }
        }

        return latest;
    }

    /// <summary>Читает заголовок файла и проверяет его вместе с длиной файла (§6).</summary>
    private async ValueTask<SnapshotHeader> ReadVerifiedHeaderAsync(string path, CancellationToken cancellationToken)
    {
        IStorageReadFile file;

        try
        {
            file = await _fileSystem.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException exception)
        {
            // Отсутствующий файл - тот же отказ, что и испорченный: метаданные, полученные
            // раньше, теперь не соответствуют диску, и молча вернуть «снапшота нет» нельзя.
            throw new InvalidDataException($"Файл снапшота '{path}' не найден.", exception);
        }

        await using var _ = file;
        var buffer = new byte[SnapshotFormat.HeaderSize];
        await ReadExactlyAsync(file, buffer, cancellationToken).ConfigureAwait(false);

        if (!SnapshotHeaderCodec.TryDecode(buffer, out var header, out var reason))
        {
            throw new InvalidDataException($"Файл '{path}' не читается как снапшот: {reason}.");
        }

        if (file.Length != header.FileLength)
        {
            throw new InvalidDataException(
                $"Файл '{path}' имеет длину {file.Length} байт, а заголовок обещает {header.FileLength}: "
                + "дописанный или оборванный файл снапшотом не считается (§6 шаг 6).");
        }

        return header;
    }

    /// <summary>Читает состояние и сверяет его CRC с заголовком (§6 шаг 7).</summary>
    private async ValueTask<ReadOnlyMemory<byte>> ReadStateAsync(
        string path,
        SnapshotHeader header,
        CancellationToken cancellationToken)
    {
        await using var file = await _fileSystem.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
        var state = new byte[header.StateLength];
        if (state.Length > 0)
        {
            await ReadExactlyAsync(file, state, cancellationToken, SnapshotFormat.HeaderSize).ConfigureAwait(false);
        }

        var crc = Crc32C.Compute(state);

        if (crc != header.StateCrc32C)
        {
            throw new InvalidDataException(
                $"CRC состояния в '{path}' не совпадает: записано {header.StateCrc32C:X8}, посчитано {crc:X8}.");
        }

        return state;
    }

    /// <summary>Дочитывает ровно <paramref name="destination"/>.Length байт с указанного смещения.</summary>
    /// <remarks>
    /// Цикл обязателен: настоящий <see cref="System.IO.FileStream"/> вправе вернуть меньше
    /// запрошенного, и «прочитал столько же, сколько просил» - предположение, а не гарантия.
    /// Недобор - отказ, а не короткое значение: длина файла уже сверена с заголовком, поэтому
    /// меньше байтов означает, что файл изменился под читателем.
    /// </remarks>
    private static async ValueTask ReadExactlyAsync(
        IStorageReadFile file,
        Memory<byte> destination,
        CancellationToken cancellationToken,
        long offset = 0)
    {
        var total = 0;

        while (total < destination.Length)
        {
            var read = await file.ReadAsync(offset + total, destination[total..], cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                throw new InvalidDataException(
                    $"Файл '{file.Path}' оборван: прочитано {total} байт из {destination.Length}.");
            }

            total += read;
        }
    }
}
