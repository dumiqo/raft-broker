namespace RaftBroker.Storage.IO;

/// <summary>
/// Рантайм-реализация <see cref="IStorageFileSystem"/> поверх <see cref="FileStream"/>.
/// </summary>
/// <remarks>
/// <para>
/// Файл открывается с <see cref="FileOptions.Asynchronous"/> (запись не блокирует поток
/// на время системного вызова) и <see cref="FileShare.Read"/>: читатели S1-T08 обязаны
/// уметь читать запечатанные сегменты, пока писатель работает с последним.
/// </para>
/// <para>
/// fsync в .NET асинхронного API не имеет: <see cref="FileStream.Flush(bool)"/> -
/// синхронный вызов. Это осознанно: писатель живёт на отдельном потоке (writer-actor
/// S1-T08), а имитировать асинхронный fsync обёрткой значило бы скрыть его реальную цену
/// от планировщика и от бенчмарков S8-T05.
/// </para>
/// </remarks>
public sealed class SystemStorageFileSystem : IStorageFileSystem
{
    /// <summary>Общий экземпляр: у класса нет состояния.</summary>
    public static SystemStorageFileSystem Instance { get; } = new();

    /// <inheritdoc />
    public ValueTask<IStorageFile> CreateAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var stream = new FileStream(
            fullPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous);

        return ValueTask.FromResult<IStorageFile>(new SystemStorageFile(fullPath, stream));
    }

    /// <inheritdoc />
    public ValueTask<IStorageReadFile> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = Path.GetFullPath(path);

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Файл '{fullPath}' не найден.", fullPath);
        }

        // FileShare.ReadWrite: писатель держит последний сегмент открытым на запись с
        // FileShare.Read (см. CreateAsync), а читатели запечатанных сегментов обязаны
        // открываться, пока он работает (§8 формата).
        var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 4096,
            FileOptions.Asynchronous);

        return ValueTask.FromResult<IStorageReadFile>(new SystemStorageReadFile(fullPath, stream));
    }

    /// <summary>Открытый <see cref="FileStream"/>; fsync - единственное, чего нет у <see cref="Stream"/>.</summary>
    private sealed class SystemStorageFile(string path, FileStream stream) : IStorageFile
    {
        public string Path { get; } = path;

        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
            => stream.WriteAsync(buffer, cancellationToken);

        public ValueTask FlushToDiskAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.Flush(flushToDisk: true);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }

    /// <summary>Открытый на чтение <see cref="FileStream"/> с абсолютной адресацией по смещению.</summary>
    private sealed class SystemStorageReadFile : IStorageReadFile
    {
        private readonly FileStream _stream;

        public SystemStorageReadFile(string path, FileStream stream)
        {
            _stream = stream;
            Path = path;
            Length = stream.Length;
        }

        public string Path { get; }

        public long Length { get; }

        public async ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(offset);

            if (offset >= Length)
            {
                return 0;
            }

            _stream.Position = offset;
            return await _stream.ReadAsync(destination, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => _stream.DisposeAsync();
    }
}
