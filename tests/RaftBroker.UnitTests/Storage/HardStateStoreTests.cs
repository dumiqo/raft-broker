using RaftBroker.Core.Primitives;
using RaftBroker.Storage.HardState;
using RaftBroker.TestKit.IO;

namespace RaftBroker.UnitTests.Storage;

/// <summary>
/// Хранилище hard state (<c>S2-T02</c>): кэш, порядок записи, обрыв хвоста и порча.
/// </summary>
/// <remarks>
/// Каждый тест «после рестарта» создаёт новый экземпляр <see cref="HardStateStore"/> над тем же
/// <see cref="FakeStorageFileSystem"/>: кэш прежнего процесса к состоянию на диске отношения
/// не имеет, и проверяться должно именно чтение файла.
/// </remarks>
public sealed class HardStateStoreTests
{
    private static readonly HardState Voted = new(new Term(4), new NodeId("node-2"));

    private const string Directory = "node";

    private static string Path => System.IO.Path.Combine(Directory, HardStateFormat.FileName);

    [Fact]
    public async Task ReadAsync_WhenTheFileIsMissing_ReturnsTheInitialStateAndCreatesTheFile()
    {
        // Arrange
        var fileSystem = new FakeStorageFileSystem();
        var store = CreateStore(fileSystem);

        // Act
        var state = await store.ReadAsync(CancellationToken.None);

        // Assert
        state.Should().Be(HardState.Empty);
        fileSystem.Exists(Path).Should().BeTrue("файл создаётся пустым, чтобы append работал без ветки CreateNew");
        fileSystem.Operations.Should().ContainSingle(operation => operation.Kind == StorageOperationKind.Create);
    }

    [Fact]
    public async Task ReadAsync_Twice_LoadsTheFileOnlyOnce()
    {
        // Arrange
        var fileSystem = new FakeStorageFileSystem();
        var store = CreateStore(fileSystem);

        // Act
        await store.ReadAsync(CancellationToken.None);
        await store.ReadAsync(CancellationToken.None);

        // Assert
        fileSystem.Operations.Should().ContainSingle(operation => operation.Kind == StorageOperationKind.Create);
    }

    [Fact]
    public async Task WriteAsync_ThenReadAfterRestart_ReturnsTheWrittenState()
    {
        // Arrange
        var fileSystem = new FakeStorageFileSystem();

        // Act
        await CreateStore(fileSystem).WriteAsync(Voted, CancellationToken.None);
        var state = await CreateStore(fileSystem).ReadAsync(CancellationToken.None);

        // Assert
        state.Should().Be(Voted);
        fileSystem.Operations.Should().Contain(operation => operation.Kind == StorageOperationKind.FlushToDisk);
    }

    [Fact]
    public async Task WriteAsync_FlushesToDiskBeforeItReturns()
    {
        // Arrange
        var fileSystem = new FakeStorageFileSystem();
        var store = CreateStore(fileSystem);

        // Act
        await store.WriteAsync(Voted, CancellationToken.None);

        // Assert: правило persist before respond - последняя операция записи это fsync.
        fileSystem.Operations[^1].Kind.Should().Be(StorageOperationKind.FlushToDisk);
    }

    [Fact]
    public async Task WriteAsync_SeveralTimes_KeepsTheLastState()
    {
        // Arrange
        var fileSystem = new FakeStorageFileSystem();
        var store = CreateStore(fileSystem);

        // Act
        await store.WriteAsync(new HardState(new Term(1), null), CancellationToken.None);
        await store.WriteAsync(new HardState(new Term(2), new NodeId("node-1")), CancellationToken.None);
        await store.WriteAsync(new HardState(new Term(2), new NodeId("node-3")), CancellationToken.None);

        // Assert
        var restarted = await CreateStore(fileSystem).ReadAsync(CancellationToken.None);

        restarted.Should().Be(new HardState(new Term(2), new NodeId("node-3")));
    }

    [Fact]
    public async Task WriteAsync_WithAnOlderTerm_ThrowsAndLeavesTheFileUntouched()
    {
        // Arrange
        var fileSystem = new FakeStorageFileSystem();
        var store = CreateStore(fileSystem);
        await store.WriteAsync(Voted, CancellationToken.None);
        var before = fileSystem.ReadAllBytes(Path);

        // Act
        var act = async () => await store.WriteAsync(new HardState(new Term(3), null), CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        fileSystem.ReadAllBytes(Path).Should().Equal(before);
    }

    [Fact]
    public async Task WriteAsync_WhenTheFlushFails_KeepsTheOldStateAndRepairsTheTailOnTheNextWrite()
    {
        // Arrange
        var fileSystem = new FakeStorageFileSystem();
        var store = CreateStore(fileSystem);
        await store.WriteAsync(Voted, CancellationToken.None);

        var fail = true;
        fileSystem.FailureInjector = operation =>
            operation.Kind == StorageOperationKind.FlushToDisk && fail
                ? new IOException("fsync не удался.")
                : null;

        // Act
        var act = async () => await store.WriteAsync(new HardState(new Term(9), null), CancellationToken.None);
        await act.Should().ThrowAsync<IOException>();

        fail = false;
        var afterFailure = await store.ReadAsync(CancellationToken.None);

        await store.WriteAsync(new HardState(new Term(10), new NodeId("node-1")), CancellationToken.None);
        var restarted = await CreateStore(fileSystem).ReadAsync(CancellationToken.None);

        // Assert
        afterFailure.Should().Be(Voted, "неподтверждённая запись состоянием не считается");
        restarted.Should().Be(new HardState(new Term(10), new NodeId("node-1")));
        fileSystem.Operations.Should().Contain(operation => operation.Kind == StorageOperationKind.Truncate);
    }

    [Fact]
    public async Task ReadAsync_WithATornTail_TruncatesItAndKeepsTheLastWholeRecord()
    {
        // Arrange
        var fileSystem = new FakeStorageFileSystem();
        var store = CreateStore(fileSystem);
        await store.WriteAsync(new HardState(new Term(1), null), CancellationToken.None);
        await store.WriteAsync(Voted, CancellationToken.None);

        var whole = fileSystem.ReadAllBytes(Path);
        // Оборванная запись: попало только начало заголовка, меньше HeaderSize байт.
        var torn = new byte[whole.Length + 5];
        whole.CopyTo(torn, 0);
        fileSystem.Seed(Path, torn);

        // Act
        var state = await CreateStore(fileSystem).ReadAsync(CancellationToken.None);

        // Assert
        state.Should().Be(Voted);
        fileSystem.ReadAllBytes(Path).Should().Equal(whole);
    }

    [Fact]
    public async Task ReadAsync_WithOnlyTheBeginningOfAHeader_TruncatesTheFileToZero()
    {
        // Arrange
        var fileSystem = new FakeStorageFileSystem();
        fileSystem.Seed(Path, HardStateFormat.Magic[..6]);

        // Act
        var state = await CreateStore(fileSystem).ReadAsync(CancellationToken.None);

        // Assert
        state.Should().Be(HardState.Empty);
        fileSystem.ReadAllBytes(Path).Should().BeEmpty();
    }

    [Fact]
    public async Task ReadAsync_WithACorruptedWholeRecord_StopsWithAnError()
    {
        // Arrange
        var fileSystem = new FakeStorageFileSystem();
        await CreateStore(fileSystem).WriteAsync(Voted, CancellationToken.None);

        var bytes = fileSystem.ReadAllBytes(Path);
        bytes[HardStateFormat.HeaderSize] ^= 0xFF;
        fileSystem.Seed(Path, bytes);

        // Act
        var act = async () => await CreateStore(fileSystem).ReadAsync(CancellationToken.None);

        // Assert: порча внутри файла не откатывается к предыдущему term - иначе возможен второй голос.
        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task ReadAsync_WithAForeignRecordOfFullLength_StopsWithAnError()
    {
        // Arrange
        var fileSystem = new FakeStorageFileSystem();
        fileSystem.Seed(Path, new byte[HardStateFormat.HeaderSize + HardStateFormat.CrcSize]);

        // Act
        var act = async () => await CreateStore(fileSystem).ReadAsync(CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task ReadAsync_WhenTheFileIsEmpty_ReturnsTheInitialState()
    {
        // Arrange
        var fileSystem = new FakeStorageFileSystem();
        fileSystem.Seed(Path, ReadOnlySpan<byte>.Empty);

        // Act
        var state = await CreateStore(fileSystem).ReadAsync(CancellationToken.None);

        // Assert
        state.Should().Be(HardState.Empty);
    }

    [Fact]
    public async Task ReadAsync_WithShortReads_StillReadsTheWholeRecord()
    {
        // Arrange
        var fileSystem = new FakeStorageFileSystem { MaxReadSize = 3 };
        await CreateStore(fileSystem).WriteAsync(Voted, CancellationToken.None);

        // Act
        var state = await CreateStore(fileSystem).ReadAsync(CancellationToken.None);

        // Assert
        state.Should().Be(Voted);
    }

    private static HardStateStore CreateStore(FakeStorageFileSystem fileSystem) => new(fileSystem, Directory);
}
