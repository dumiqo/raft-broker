using RaftBroker.Storage.IO;

namespace RaftBroker.TestKit.IO;

/// <summary>Вид операции над файлом, которую видит тест.</summary>
public enum StorageOperationKind
{
    /// <summary>Создание нового файла.</summary>
    Create,

    /// <summary>Дозапись байтов.</summary>
    Write,

    /// <summary>Сброс на устройство (fsync).</summary>
    FlushToDisk,

    /// <summary>Чтение байтов по смещению.</summary>
    Read,

    /// <summary>Укорачивание файла до новой длины (S1-T05).</summary>
    Truncate,

    /// <summary>Удаление файла (S1-T05).</summary>
    Delete,
}

/// <summary>Запись в журнале операций файловой системы: порядок операций и есть предмет проверки.</summary>
/// <param name="Kind">Вид операции.</param>
/// <param name="Path">Файл, над которым выполнена операция.</param>
/// <param name="ByteCount">
/// Число байтов; осмысленно для <see cref="StorageOperationKind.Write"/>,
/// <see cref="StorageOperationKind.Read"/> (сколько прочитано) и
/// <see cref="StorageOperationKind.Truncate"/> (новая длина файла).
/// </param>
public readonly record struct StorageOperation(StorageOperationKind Kind, string Path, int ByteCount = 0);

/// <summary>
/// Файловая система в памяти: считает fsync и чтения, умеет падать на заданной операции.
/// </summary>
/// <remarks>
/// <para>
/// Живёт в <c>RaftBroker.TestKit</c> (ADR 0002), потому что нужна и unit-, и интеграционным,
/// и simulation-тестам. Существует ради проверок, которые нельзя сделать на реальном
/// <see cref="FileStream"/>, не заглядывая в него через рефлексию:
/// </para>
/// <list type="bullet">
/// <item><see cref="FlushToDiskCount"/> - "DurabilityMode действительно влияет на вызовы fsync"
/// (чек-лист R1, пункт 3);</item>
/// <item><see cref="FailureInjector"/> - падение ровно в заданной точке записи для crash-тестов
/// S1-T05 и S1-T09;</item>
/// <item><see cref="Seed"/> - порченые и оборванные сегменты, которых писателем не получить
/// (S1-T04, S1-T05);</item>
/// <item><see cref="MaxReadSize"/> - короткие чтения: настоящий поток вправе вернуть меньше
/// запрошенного, и цикл дочитывания обязан быть проверен.</item>
/// </list>
/// <para>
/// Содержимое файла возвращается "как записано": дубль намеренно не моделирует разницу между
/// буфером процесса, буфером ядра и устройством - политики долговечности проверяются по числу
/// и порядку вызовов <see cref="IStorageFile.FlushToDiskAsync"/>, а не по содержимому.
/// </para>
/// </remarks>
public sealed class FakeStorageFileSystem : IStorageFileSystem
{
    private readonly Dictionary<string, FakeStorageFile> _files = new(StringComparer.Ordinal);
    private readonly List<StorageOperation> _operations = [];

    /// <summary>
    /// Если задан и вернул исключение, операция попадает в журнал и затем падает.
    /// Так моделируется отказ в конкретной точке записи.
    /// </summary>
    public Func<StorageOperation, Exception?>? FailureInjector { get; set; }

    /// <summary>Все операции над файлами в порядке выполнения.</summary>
    public IReadOnlyList<StorageOperation> Operations => _operations;

    /// <summary>Пути созданных файлов в лексикографическом порядке (он же числовой для имён сегментов).</summary>
    public IReadOnlyList<string> Paths => [.. _files.Keys.Order(StringComparer.Ordinal)];

    /// <summary>Сколько раз файлы просили fsync.</summary>
    public int FlushToDiskCount => _operations.Count(operation => operation.Kind == StorageOperationKind.FlushToDisk);

    /// <summary>Сколько раз файлы просили запись байтов.</summary>
    public int WriteCount => _operations.Count(operation => operation.Kind == StorageOperationKind.Write);

    /// <summary>Сколько раз файлы просили чтение байтов.</summary>
    public int ReadCount => _operations.Count(operation => operation.Kind == StorageOperationKind.Read);

    /// <summary>Сколько байтов всего прочитано (с учётом коротких чтений).</summary>
    public int BytesRead => _operations
        .Where(operation => operation.Kind == StorageOperationKind.Read)
        .Sum(operation => operation.ByteCount);

    /// <summary>
    /// Предел на одно чтение: если задан, <see cref="IStorageReadFile.ReadAsync"/> вернёт
    /// не больше этого числа байт. Моделирует короткие чтения, которые вправе вернуть
    /// настоящий <see cref="System.IO.FileStream"/>.
    /// </summary>
    public int? MaxReadSize { get; set; }

    /// <summary>Сколько читающих файлов сейчас открыто: утечка дескриптора обязана быть видна.</summary>
    public int OpenReadFileCount { get; private set; }

    /// <summary>
    /// Задаёт содержимое файла напрямую, минуя операции хранилища.
    /// </summary>
    /// <param name="path">Путь файла; если он уже существует, содержимое заменяется.</param>
    /// <remarks>
    /// Это вход теста, а не операция хранилища: в <see cref="Operations"/> он не попадает.
    /// Существующий открытый файл сохраняет тот же экземпляр, поэтому замена содержимого
    /// видна уже открытым читателям и писателям.
    /// </remarks>
    public void Seed(string path, ReadOnlySpan<byte> content)
    {
        if (!_files.TryGetValue(path, out var file))
        {
            file = new FakeStorageFile(this, path);
            _files.Add(path, file);
        }

        file.ReplaceContent(content);
    }

    /// <inheritdoc />
    public ValueTask<IStorageFile> CreateAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Record(new StorageOperation(StorageOperationKind.Create, path));

        if (_files.ContainsKey(path))
        {
            throw new IOException($"Файл '{path}' уже существует: создание имеет семантику CreateNew.");
        }

        var file = new FakeStorageFile(this, path);
        _files.Add(path, file);

        return ValueTask.FromResult<IStorageFile>(file);
    }

    /// <inheritdoc />
    public ValueTask<IStorageReadFile> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_files.TryGetValue(path, out var file))
        {
            throw new FileNotFoundException($"Файл '{path}' не создавался.", path);
        }

        return ValueTask.FromResult<IStorageReadFile>(new FakeStorageReadFile(this, file));
    }

    /// <inheritdoc />
    public ValueTask<IStorageFile> OpenAppendAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_files.TryGetValue(path, out var file))
        {
            throw new FileNotFoundException($"Файл '{path}' не создавался.", path);
        }

        // Запись в FakeStorageFile всегда дописывает в конец - ровно семантика FileMode.Append.
        // Дескриптор считается новым: файл, закрытый предыдущим писателем, снова пригоден для записи.
        file.Reopen();

        return ValueTask.FromResult<IStorageFile>(file);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> ListFilesAsync(string directory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<string> names =
        [
            .. _files.Keys
                .Where(path => Path.GetDirectoryName(path) == directory)
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
        cancellationToken.ThrowIfCancellationRequested();

        if (!_files.TryGetValue(path, out var file))
        {
            throw new FileNotFoundException($"Файл '{path}' не создавался.", path);
        }

        if (length > file.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length),
                length,
                $"Усечение до {length} байт: файл '{path}' имеет длину {file.Count}.");
        }

        Record(new StorageOperation(StorageOperationKind.Truncate, path, (int)length));
        file.Truncate(length);

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DeleteAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Record(new StorageOperation(StorageOperationKind.Delete, path));
        _files.Remove(path);

        return ValueTask.CompletedTask;
    }

    /// <summary>Содержимое файла в том виде, в каком его записали.</summary>
    /// <param name="path">Путь файла.</param>
    /// <returns>Скопированные байты файла.</returns>
    public byte[] ReadAllBytes(string path)
        => _files.TryGetValue(path, out var file)
            ? file.ToArray()
            : throw new FileNotFoundException($"Файл '{path}' не создавался.", path);

    /// <summary>Создавался ли файл.</summary>
    /// <param name="path">Путь файла.</param>
    public bool Exists(string path) => _files.ContainsKey(path);

    internal void Record(StorageOperation operation)
    {
        _operations.Add(operation);

        var failure = FailureInjector?.Invoke(operation);

        if (failure is not null)
        {
            throw failure;
        }
    }

    /// <summary>Читает байты файла с учётом <see cref="MaxReadSize"/>.</summary>
    internal int Read(FakeStorageFile file, long offset, Memory<byte> destination)
    {
        var available = Math.Max(0, Math.Min((long)destination.Length, file.Count - offset));
        var limit = MaxReadSize is { } max ? Math.Min(available, max) : available;
        var count = (int)limit;

        Record(new StorageOperation(StorageOperationKind.Read, file.Path, count));

        file.CopyTo(offset, destination.Span[..count]);
        return count;
    }

    internal void OnReadFileOpened() => OpenReadFileCount++;

    internal void OnReadFileClosed() => OpenReadFileCount--;
}

/// <summary>Файл в памяти: накапливает байты и сообщает об операциях владельцу.</summary>
internal sealed class FakeStorageFile(FakeStorageFileSystem owner, string path) : IStorageFile
{
    private readonly List<byte> _content = [];

    public string Path { get; } = path;

    /// <summary>Закрыт ли файл: писатель обязан закрывать сегмент при rollover.</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>
    /// Отмечает файл открытым заново: дозапись после закрытия - это новый дескриптор.
    /// </summary>
    /// <remarks>
    /// Нужно потому, что дубль хранит один экземпляр на файл: продолжение сегмента
    /// (<see cref="RaftBroker.Storage.Log.SegmentWriter.ResumeAsync"/>) закрывает и снова открывает его.
    /// Без сброса <see cref="IsDisposed"/> запись в только что открытый файл падала бы.
    /// </remarks>
    internal void Reopen() => IsDisposed = false;

    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        owner.Record(new StorageOperation(StorageOperationKind.Write, Path, buffer.Length));

        foreach (var value in buffer.Span)
        {
            _content.Add(value);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask TruncateAsync(long length, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        if (length > Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length),
                length,
                $"Усечение до {length} байт: файл '{Path}' имеет длину {Count}.");
        }

        owner.Record(new StorageOperation(StorageOperationKind.Truncate, Path, (int)length));
        Truncate(length);

        return ValueTask.CompletedTask;
    }

    public ValueTask FlushToDiskAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        owner.Record(new StorageOperation(StorageOperationKind.FlushToDisk, Path));
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }

    internal byte[] ToArray() => [.. _content];

    /// <summary>Число байтов в файле.</summary>
    internal int Count => _content.Count;

    /// <summary>Заменяет содержимое файла: тестовый вход для порченых сегментов.</summary>
    internal void ReplaceContent(ReadOnlySpan<byte> content)
    {
        _content.Clear();

        foreach (var value in content)
        {
            _content.Add(value);
        }
    }

    /// <summary>Укорачивает файл до <paramref name="length"/> байт.</summary>
    internal void Truncate(long length) => _content.RemoveRange((int)length, _content.Count - (int)length);

    /// <summary>Копирует <paramref name="count"/> байтов начиная с <paramref name="offset"/>.</summary>
    internal void CopyTo(long offset, Span<byte> destination)
    {
        for (var position = 0; position < destination.Length; position++)
        {
            destination[position] = _content[(int)(offset + position)];
        }
    }
}

/// <summary>Открытый на чтение файл в памяти: те же байты, что видит писатель.</summary>
/// <remarks>
/// Длина фиксируется при открытии - как у рантайм-реализации: читатель сегмента работает со
/// снимком файла, а не с тем, который дописывают.
/// </remarks>
internal sealed class FakeStorageReadFile : IStorageReadFile
{
    private readonly FakeStorageFileSystem _owner;
    private bool _disposed;

    internal FakeStorageReadFile(FakeStorageFileSystem owner, FakeStorageFile file)
    {
        _owner = owner;
        File = file;
        Length = file.Count;
        owner.OnReadFileOpened();
    }

    internal FakeStorageFile File { get; }

    public string Path => File.Path;

    public long Length { get; }

    public ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        return ValueTask.FromResult(_owner.Read(File, offset, destination));
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _owner.OnReadFileClosed();
        }

        return ValueTask.CompletedTask;
    }
}
