using RaftBroker.Core.Primitives;
using RaftBroker.Storage.Integrity;
using RaftBroker.Storage.Snapshot;
using RaftBroker.TestKit.IO;
using RaftBroker.TestKit.Time;

namespace RaftBroker.UnitTests.Storage;

/// <summary>
/// Хранилище снапшотов (S1-T09): публикация переименованием, проверки при чтении, удержание
/// самого свежего снапшота.
/// </summary>
/// <remarks>
/// Проверки идут в порядке, обратном записи: сначала то, что видно в каталоге и в журнале
/// операций, потом содержимое файла, потом поведение при порче. Порядок операций - часть
/// контракта (ADR <c>0012</c>), поэтому он проверяется явно, а не «по факту успеха».
/// </remarks>
public sealed class SnapshotStoreTests
{
    private const string Directory = "snapshots";

    private static readonly SnapshotMetadata First = new(new LogIndex(5), new Term(2));
    private static readonly SnapshotMetadata Second = new(new LogIndex(12), new Term(4));

    private static readonly byte[] SmallState = [1, 2, 3, 4];

    [Fact]
    public async Task Write_PublishesTheFileByRenameAfterFsync()
    {
        // Arrange
        var fileSystem = new FakeStorageFileSystem();
        var store = CreateStore(fileSystem);

        var state = new byte[] { 7, 8, 9 };

        // Act
        await store.WriteAsync(First, state, TestContext.Current.CancellationToken);

        // Assert: временный файл, fsync, переименование, сброс каталога - ровно в этом порядке.
        var kinds = fileSystem.Operations.Select(operation => operation.Kind).ToArray();

        Assert.Equal(
            [
                StorageOperationKind.Create,
                StorageOperationKind.Write,
                StorageOperationKind.Write,
                StorageOperationKind.FlushToDisk,
                StorageOperationKind.Rename,
                StorageOperationKind.FlushDirectory,
            ],
            kinds);

        Assert.Equal(Path.Combine(Directory, SnapshotFormat.GetTempFileName(First.LastIncludedIndex)), fileSystem.Operations[0].Path);

        // Переименование записано исходным именем: именно оно уходит из каталога.
        Assert.Equal(Path.Combine(Directory, SnapshotFormat.GetTempFileName(First.LastIncludedIndex)), fileSystem.Operations[4].Path);
        Assert.Equal(Directory, fileSystem.Operations[5].Path);
        Assert.False(fileSystem.Exists(Path.Combine(Directory, SnapshotFormat.GetTempFileName(First.LastIncludedIndex))));
        Assert.True(fileSystem.Exists(Path.Combine(Directory, First.FileName)));
        Assert.Equal(
            SnapshotFormat.HeaderSize + state.Length,
            fileSystem.ReadAllBytes(Path.Combine(Directory, First.FileName)).Length);
    }

    [Fact]
    public async Task GetLatest_WithoutSnapshots_ReturnsNull()
    {
        var store = CreateStore(new FakeStorageFileSystem());

        Assert.Null(await store.GetLatestAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Write_ThenReadState_RoundTripsTheBytes()
    {
        // Arrange
        var fileSystem = new FakeStorageFileSystem();
        var store = CreateStore(fileSystem);
        var state = new byte[512];
        Random.Shared.NextBytes(state);

        // Act
        await store.WriteAsync(Second, state, TestContext.Current.CancellationToken);

        var latest = await store.GetLatestAsync(TestContext.Current.CancellationToken);
        var read = await store.ReadStateAsync(Second, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(Second, latest);
        Assert.Equal(state, read.ToArray());
    }

    [Fact]
    public async Task GetLatest_WithTwoSnapshots_ReturnsTheNewestByIndex()
    {
        // Arrange
        var fileSystem = new FakeStorageFileSystem();
        var store = CreateStore(fileSystem);

        // Act: сначала новый, потом старый - порядок записи не должен влиять на выбор.
        await store.WriteAsync(Second, SmallState, TestContext.Current.CancellationToken);
        await store.WriteAsync(First, SmallState, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(Second, await store.GetLatestAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetLatest_RemovesOrphanedTempFiles()
    {
        // Arrange: так выглядит крах между записью состояния и переименованием.
        var fileSystem = new FakeStorageFileSystem();
        var store = CreateStore(fileSystem);
        var tempPath = Path.Combine(Directory, SnapshotFormat.GetTempFileName(Second.LastIncludedIndex));

        await using (var orphan = await fileSystem.CreateAsync(tempPath, TestContext.Current.CancellationToken))
        {
            await orphan.WriteAsync(new byte[SnapshotFormat.HeaderSize], TestContext.Current.CancellationToken);
        }

        // Act
        var latest = await store.GetLatestAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(latest);
        Assert.False(fileSystem.Exists(tempPath));
        Assert.Contains(fileSystem.Operations, operation =>
            operation.Kind == StorageOperationKind.Delete && operation.Path == tempPath);
    }

    [Fact]
    public async Task GetLatest_WithOrphanedTempAndAPublishedSnapshot_KeepsTheSnapshot()
    {
        // Arrange
        var fileSystem = new FakeStorageFileSystem();
        var store = CreateStore(fileSystem);
        await store.WriteAsync(First, SmallState, TestContext.Current.CancellationToken);

        var tempPath = Path.Combine(Directory, SnapshotFormat.GetTempFileName(Second.LastIncludedIndex));

        await using (var orphan = await fileSystem.CreateAsync(tempPath, TestContext.Current.CancellationToken))
        {
            await orphan.WriteAsync(new byte[SnapshotFormat.HeaderSize], TestContext.Current.CancellationToken);
        }

        // Act
        var latest = await store.GetLatestAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(First, latest);
        Assert.False(fileSystem.Exists(tempPath));
    }

    [Fact]
    public async Task Write_RejectsNonPositiveIndex()
    {
        var store = CreateStore(new FakeStorageFileSystem());
        var metadata = new SnapshotMetadata(LogIndex.Empty, new Term(1));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await store.WriteAsync(metadata, SmallState, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Write_RejectsStateLongerThanTheLimit()
    {
        // Arrange: гигабайт в память не выделяется, проверка идёт по длине.
        var fileSystem = new FakeStorageFileSystem();
        var store = CreateStore(fileSystem);

        // Act
        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await store.WriteAsync(
                First,
                new byte[SnapshotFormat.MaxStateLength + 1],
                TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal("state", exception.ParamName);
        Assert.Empty(fileSystem.Operations);
    }

    [Fact]
    public async Task Write_WithTheSameIndexTwice_KeepsThePublishedFile()
    {
        // Arrange
        var fileSystem = new FakeStorageFileSystem();
        var store = CreateStore(fileSystem);
        await store.WriteAsync(First, SmallState, TestContext.Current.CancellationToken);

        // Act: замена существующего снапшота запрещена (ADR 0012).
        await Assert.ThrowsAsync<IOException>(
            async () => await store.WriteAsync(First, new byte[] { 9, 9 }, TestContext.Current.CancellationToken));

        // Assert: прежний файл цел и читается.
        Assert.Equal(SmallState, (await store.ReadStateAsync(First, TestContext.Current.CancellationToken)).ToArray());
    }

    [Fact]
    public async Task ReadState_WithMismatchedMetadata_Throws()
    {
        // Arrange
        var fileSystem = new FakeStorageFileSystem();
        var store = CreateStore(fileSystem);
        await store.WriteAsync(First, SmallState, TestContext.Current.CancellationToken);

        var other = new SnapshotMetadata(First.LastIncludedIndex, new Term(First.LastIncludedTerm.Value + 1));

        // Act
        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await store.ReadStateAsync(other, TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains("не совпадают", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadState_WhenTheFileIsMissing_ThrowsInvalidData()
    {
        // Arrange: метаданные есть, файла нет - единственная копия состояния потеряна.
        var store = CreateStore(new FakeStorageFileSystem());

        // Act
        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await store.ReadStateAsync(First, TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains(First.FileName, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetLatest_WithATruncatedHeader_Throws()
    {
        // Arrange
        var fileSystem = new FakeStorageFileSystem();
        var store = CreateStore(fileSystem);
        fileSystem.Seed(Path.Combine(Directory, First.FileName), new byte[SnapshotFormat.HeaderSize - 1]);

        // Act
        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await store.GetLatestAsync(TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains("оборван", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetLatest_WithAForeignMagic_Throws()
    {
        // Arrange
        var fileSystem = new FakeStorageFileSystem();
        var store = CreateStore(fileSystem);
        var file = BuildSnapshot(First, SmallState);
        file[0] ^= 0xFF;
        fileSystem.Seed(Path.Combine(Directory, First.FileName), file);

        // Act
        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await store.GetLatestAsync(TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains("магия", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetLatest_WithADamagedHeader_Throws()
    {
        // Arrange: порча в поле терма ломает CRC заголовка.
        var fileSystem = new FakeStorageFileSystem();
        var store = CreateStore(fileSystem);
        var file = BuildSnapshot(First, SmallState);
        file[16] ^= 0x01;
        fileSystem.Seed(Path.Combine(Directory, First.FileName), file);

        // Act
        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await store.GetLatestAsync(TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains("CRC заголовка", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetLatest_WithADamagedState_Throws()
    {
        // Arrange: заголовок цел, испорчен байт состояния.
        var fileSystem = new FakeStorageFileSystem();
        var store = CreateStore(fileSystem);
        var file = BuildSnapshot(First, SmallState);
        file[^1] ^= 0xFF;
        fileSystem.Seed(Path.Combine(Directory, First.FileName), file);

        // Act
        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await store.ReadStateAsync(First, TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains("CRC состояния", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetLatest_WithTrailingBytes_Throws()
    {
        // Arrange: длина файла не совпадает с обещанной заголовком.
        var fileSystem = new FakeStorageFileSystem();
        var store = CreateStore(fileSystem);
        var file = BuildSnapshot(First, SmallState);
        fileSystem.Seed(Path.Combine(Directory, First.FileName), [.. file, 0x00]);

        // Act
        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await store.GetLatestAsync(TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains("заголовок обещает", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetLatest_IgnoresFilesThatAreNotSnapshots()
    {
        // Arrange: посторонний файл в каталоге не должен ни выбираться, ни мешать.
        var fileSystem = new FakeStorageFileSystem();
        var store = CreateStore(fileSystem);
        fileSystem.Seed(Path.Combine(Directory, "notes.txt"), [1, 2, 3]);
        fileSystem.Seed(Path.Combine(Directory, "snapshot-abc.snap"), [1, 2, 3]);
        await store.WriteAsync(First, SmallState, TestContext.Current.CancellationToken);

        // Act
        var latest = await store.GetLatestAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(First, latest);
        Assert.True(fileSystem.Exists(Path.Combine(Directory, "notes.txt")));
    }

    [Fact]
    public async Task DeleteOlderThan_RemovesOnlySnapshotsStrictlyBelowTheIndex()
    {
        // Arrange
        var fileSystem = new FakeStorageFileSystem();
        var store = CreateStore(fileSystem);
        var third = new SnapshotMetadata(new LogIndex(30), new Term(6));

        await store.WriteAsync(First, SmallState, TestContext.Current.CancellationToken);
        await store.WriteAsync(Second, SmallState, TestContext.Current.CancellationToken);
        await store.WriteAsync(third, SmallState, TestContext.Current.CancellationToken);

        // Act
        await store.DeleteOlderThanAsync(Second.LastIncludedIndex, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(fileSystem.Exists(Path.Combine(Directory, First.FileName)));
        Assert.True(fileSystem.Exists(Path.Combine(Directory, Second.FileName)));
        Assert.True(fileSystem.Exists(Path.Combine(Directory, third.FileName)));
    }

    [Fact]
    public async Task DeleteOlderThan_NeverRemovesTheNewestSnapshot()
    {
        // Arrange: единственный снапшот ниже порога - узел не должен остаться без состояния.
        var fileSystem = new FakeStorageFileSystem();
        var store = CreateStore(fileSystem);
        await store.WriteAsync(First, SmallState, TestContext.Current.CancellationToken);

        // Act
        await store.DeleteOlderThanAsync(new LogIndex(1_000), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(First, await store.GetLatestAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Write_AfterTheStoreReportsNoSnapshots_StartsACleanDirectory()
    {
        // Arrange: старт с пустого каталога, два снапшота подряд, перезапуск.
        var fileSystem = new FakeStorageFileSystem();
        var store = CreateStore(fileSystem);

        Assert.Null(await store.GetLatestAsync(TestContext.Current.CancellationToken));

        // Act
        await store.WriteAsync(First, SmallState, TestContext.Current.CancellationToken);
        await store.DeleteOlderThanAsync(Second.LastIncludedIndex, TestContext.Current.CancellationToken);
        await store.WriteAsync(Second, SmallState, TestContext.Current.CancellationToken);

        // Assert: свежий экземпляр видит то же, что предыдущий, - кэша нет.
        Assert.Equal(Second, await CreateStore(fileSystem).GetLatestAsync(TestContext.Current.CancellationToken));
    }

    private static SnapshotStore CreateStore(FakeStorageFileSystem fileSystem)
        => new(fileSystem, new TestClock(unixTimeMs: 1_700_000_000_000), Directory);

    /// <summary>Собирает целый файл снапшота теми же кодеками, что и хранилище.</summary>
    private static byte[] BuildSnapshot(SnapshotMetadata metadata, byte[] state)
    {
        var file = new byte[SnapshotFormat.HeaderSize + state.Length];

        SnapshotHeaderCodec.Encode(metadata, 1_700_000_000_000, state.Length, Crc32C.Compute(state), file);
        state.CopyTo(file, SnapshotFormat.HeaderSize);

        return file;
    }
}
