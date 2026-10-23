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
/// <para>
/// Файл, открытый на запись, идёт без буфера процесса (<c>bufferSize: 1</c>, <c>S1-T06</c>):
/// иначе дописанные байты оставались бы в <see cref="FileStream"/> и читатель последнего
/// сегмента (он открывает тот же файл по <see cref="OpenReadAsync"/>) не видел бы их до
/// fsync. Буферизация тут ничего не даёт: партии записей собирает писатель, а не поток.
/// Три границы отказов - процесс, ядро, устройство - остаются различимыми:
/// <see cref="IStorageFile.WriteAsync"/> доводит байты до ядра, <see cref="IStorageFile.FlushToDiskAsync"/> -
/// до устройства.
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
            bufferSize: 1,
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

    /// <inheritdoc />
    public ValueTask<IStorageFile> OpenAppendAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = Path.GetFullPath(path);

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Файл '{fullPath}' не найден.", fullPath);
        }

        // FileMode.Open + Seek(конец) вместо FileMode.Append: у Append начальная позиция
        // запоминается при открытии и после усечения файла указывала бы за его конец.
        // FileShare.Read - тот же режим, что у CreateAsync: читатели обязаны открывать этот
        // же файл, пока писатель его дописывает (последний сегмент читается читателем сегмента).
        var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 1,
            FileOptions.Asynchronous);

        stream.Seek(0, SeekOrigin.End);

        return ValueTask.FromResult<IStorageFile>(new SystemStorageFile(fullPath, stream));
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> ListFilesAsync(string directory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = Path.GetFullPath(directory);

        if (!Directory.Exists(fullPath))
        {
            return ValueTask.FromResult<IReadOnlyList<string>>([]);
        }

        IReadOnlyList<string> names =
        [
            .. Directory
                .EnumerateFiles(fullPath)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => name!)
                .Order(StringComparer.Ordinal),
        ];

        return ValueTask.FromResult(names);
    }

    /// <inheritdoc />
    public ValueTask TruncateAsync(string path, long length, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = Path.GetFullPath(path);

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Файл '{fullPath}' не найден.", fullPath);
        }

        var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous);

        try
        {
            if (length > stream.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(length),
                    length,
                    $"Усечение до {length} байт: файл '{fullPath}' имеет длину {stream.Length}. "
                    + "Восстановление только укорачивает файл (§9.2).");
            }

            stream.SetLength(length);

            // Оборванный хвост исчезает только после того, как изменение достигло устройства:
            // без Flush(flushToDisk: true) повторный старт после падения увидел бы прежний файл.
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            stream.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask RenameAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        cancellationToken.ThrowIfCancellationRequested();

        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(destinationPath);

        if (!File.Exists(source))
        {
            throw new FileNotFoundException($"Файл '{source}' не найден.", source);
        }

        // overwrite: false - и семантика контракта, и атомарность: MoveFileEx без REPLACE_EXISTING
        // и rename(2) с проверкой имени не оставляют окна, в котором целевого файла нет.
        File.Move(source, destination, overwrite: false);

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask FlushDirectoryAsync(string directory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = Path.GetFullPath(directory);

        if (!Directory.Exists(fullPath))
        {
            return ValueTask.CompletedTask;
        }

        TryFlushDirectory(fullPath);

        return ValueTask.CompletedTask;
    }

    /// <summary>Пытается сбросить каталог; там, где платформа этого не умеет, молча ничего не делает.</summary>
    /// <remarks>
    /// <para>
    /// Ветвления по ОС здесь нет намеренно (ADR <c>0008</c>): вызов <c>open</c>/<c>fsync</c>
    /// из libc на Windows не разрешается вовсе, поэтому попытка заканчивается
    /// <see cref="DllNotFoundException"/>, и это единственный признак «платформа не умеет».
    /// На Windows сброс каталога не определён, а метаданные NTFS журналируются файловой
    /// системой - эта разница записана в <c>docs/formats/snapshot.md</c> §7, а не спрятана в коде.
    /// </para>
    /// <para>
    /// Отказ сброса не пробрасывается: к этому моменту файл уже переименован и цел, и падать
    /// из-за отсутствия оптимизации долговечности значило бы запретить снапшоты на платформе,
    /// которая просто не умеет их сбрасывать.
    /// </para>
    /// </remarks>
    private static void TryFlushDirectory(string path)
    {
        var descriptor = -1;

        try
        {
            descriptor = Posix.Open(path, Posix.OpenReadOnly);

            if (descriptor < 0)
            {
                return;
            }

            _ = Posix.Fsync(descriptor);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            // Платформа без libc (Windows): сбрасывать каталог нечем, см. remarks.
        }
        finally
        {
            if (descriptor >= 0)
            {
                _ = Posix.Close(descriptor);
            }
        }
    }

    /// <summary>Минимальный набор POSIX-вызовов для сброса каталога.</summary>
    /// <remarks>
    /// Только <c>open</c>/<c>fsync</c>/<c>close</c> и только здесь: платформенный код живёт
    /// в <c>Storage/IO</c> и больше нигде (проверяет <c>PlatformBoundaryTests</c>).
    /// </remarks>
    private static class Posix
    {
        /// <summary>Флаг <c>O_RDONLY</c>: каталог открывается только на чтение.</summary>
        internal const int OpenReadOnly = 0;

        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "open", SetLastError = true)]
        internal static extern int Open(string path, int flags);

        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
        internal static extern int Fsync(int descriptor);

        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "close", SetLastError = true)]
        internal static extern int Close(int descriptor);
    }

    /// <inheritdoc />
    public ValueTask DeleteAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = Path.GetFullPath(path);

        // File.Delete не бросает, если файла нет: повторный старт после падения между решением
        // об удалении и самим удалением просто повторит операцию.
        File.Delete(fullPath);

        return ValueTask.CompletedTask;
    }

    /// <summary>Открытый <see cref="FileStream"/>; fsync - единственное, чего нет у <see cref="Stream"/>.</summary>
    private sealed class SystemStorageFile(string path, FileStream stream) : IStorageFile
    {
        public string Path { get; } = path;

        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
            => stream.WriteAsync(buffer, cancellationToken);

        public ValueTask TruncateAsync(long length, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (length > stream.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(length),
                    length,
                    $"Усечение до {length} байт: файл '{Path}' имеет длину {stream.Length}.");
            }

            stream.SetLength(length);

            // Позиция может оказаться за новым концом: без этого следующая запись
            // оставила бы дыру между концом файла и своим началом.
            stream.Seek(0, SeekOrigin.End);
            stream.Flush(flushToDisk: true);

            return ValueTask.CompletedTask;
        }

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
