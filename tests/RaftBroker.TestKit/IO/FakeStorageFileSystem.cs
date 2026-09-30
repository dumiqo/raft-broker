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
}

/// <summary>Запись в журнале операций файловой системы: порядок операций и есть предмет проверки.</summary>
/// <param name="Kind">Вид операции.</param>
/// <param name="Path">Файл, над которым выполнена операция.</param>
/// <param name="ByteCount">Число байтов; осмысленно только для <see cref="StorageOperationKind.Write"/>.</param>
public readonly record struct StorageOperation(StorageOperationKind Kind, string Path, int ByteCount = 0);

/// <summary>
/// Файловая система в памяти: считает fsync и умеет падать на заданной операции.
/// </summary>
/// <remarks>
/// <para>
/// Живёт в <c>RaftBroker.TestKit</c> (ADR 0002), потому что нужна и unit-, и интеграционным,
/// и simulation-тестам. Существует ради двух проверок, которые нельзя сделать на реальном
/// <see cref="FileStream"/>, не заглядывая в него через рефлексию:
/// </para>
/// <list type="bullet">
/// <item><see cref="FlushToDiskCount"/> - "DurabilityMode действительно влияет на вызовы fsync"
/// (чек-лист R1, пункт 3);</item>
/// <item><see cref="FailureInjector"/> - падение ровно в заданной точке записи для crash-тестов
/// S1-T05 и S1-T09.</item>
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
}

/// <summary>Файл в памяти: накапливает байты и сообщает об операциях владельцу.</summary>
internal sealed class FakeStorageFile(FakeStorageFileSystem owner, string path) : IStorageFile
{
    private readonly List<byte> _content = [];

    public string Path { get; } = path;

    /// <summary>Закрыт ли файл: писатель обязан закрывать сегмент при rollover.</summary>
    public bool IsDisposed { get; private set; }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        owner.Record(new StorageOperation(StorageOperationKind.Write, Path, buffer.Length));

        foreach (var value in buffer.Span)
        {
            _content.Add(value);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask FlushToDiskAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        owner.Record(new StorageOperation(StorageOperationKind.FlushToDisk, Path));
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }

    internal byte[] ToArray() => [.. _content];
}
