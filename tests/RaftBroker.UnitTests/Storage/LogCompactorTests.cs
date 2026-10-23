using RaftBroker.Core.Primitives;
using RaftBroker.Storage.Compaction;
using RaftBroker.Storage.Log;
using RaftBroker.Storage.Snapshot;
using RaftBroker.TestKit.IO;
using RaftBroker.TestKit.Time;

namespace RaftBroker.UnitTests.Storage;

/// <summary>
/// Сжатие истории (<c>S1-T10</c>): порядок «снапшот, проверка, обрезка» и отсутствие дыры
/// между снапшотом и началом журнала при крахе в любой точке.
/// </summary>
/// <remarks>
/// <para>
/// Проверяется не «компакция прошла», а инвариант R4 в формулировке, которую видно на диске:
/// после каждого шага журнал либо цел, либо обрезан не дальше опубликованного снапшота.
/// Поэтому точки отказа перебираются инъекцией на конкретной операции дубля, а не «как повезёт».
/// </para>
/// <para>
/// Сегменты пишутся настоящим писателем, снапшот - настоящим <see cref="SnapshotStore"/>:
/// если кодеки разъедутся, это обязано упасть здесь, а не при первом рестарте узла.
/// </para>
/// </remarks>
public sealed class LogCompactorTests
{
    private const string LogDirectory = "node/log";

    private const string SnapshotDirectory = "node/snapshots";

    private static readonly byte[] State = [1, 2, 3, 4, 5];

    [Fact]
    public async Task Compact_PublishesTheSnapshotBeforeTrimmingTheLog()
    {
        // Arrange: три сегмента по две записи; снапшот накрывает первые два целиком.
        var (fileSystem, log) = await OpenLogAsync(recordsPerSegment: 2, segments: 3);
        var snapshots = CreateSnapshots(fileSystem);
        var compactor = new LogCompactor(log, snapshots);

        // Act
        await compactor.CompactAsync(new SnapshotMetadata(new LogIndex(4), new Term(1)), State, CancellationToken.None);

        // Assert: снапшот попал в каталог до первого удаления сегмента.
        var operations = fileSystem.Operations;
        var rename = operations.ToList().FindIndex(operation => operation.Kind == StorageOperationKind.Rename);
        var firstDelete = operations.ToList().FindIndex(operation => operation.Kind == StorageOperationKind.Delete);

        rename.Should().BeGreaterThanOrEqualTo(0);
        firstDelete.Should().BeGreaterThan(rename);

        // Обрезаны ровно два первых сегмента: третий продолжает журнал.
        fileSystem.Exists(SegmentPath(1)).Should().BeFalse();
        fileSystem.Exists(SegmentPath(3)).Should().BeFalse();
        fileSystem.Exists(SegmentPath(5)).Should().BeTrue();
        log.FirstIndex.Value.Should().Be(5);
        log.LastIndex.Value.Should().Be(6);
    }

    [Fact]
    public async Task Compact_BelowTheSnapshotReportsCompactedInsteadOfMissing()
    {
        // Arrange
        var (_, log) = await OpenLogAsync(recordsPerSegment: 2, segments: 3);
        var compactor = new LogCompactor(log, CreateSnapshots(new FakeStorageFileSystem()));

        // Act
        await compactor.CompactAsync(new SnapshotMetadata(new LogIndex(2), new Term(1)), State, CancellationToken.None);

        // Assert: ниже снапшота - «уже сжато» (follower получит снапшот), а не «записи нет».
        (await log.GetAsync(new LogIndex(1), CancellationToken.None)).Status.Should().Be(LogLookupStatus.Compacted);
        (await log.GetAsync(new LogIndex(3), CancellationToken.None)).Status.Should().Be(LogLookupStatus.Found);
    }

    [Fact]
    public async Task Compact_WhenTheSnapshotDoesNotReadBack_LeavesTheLogUntouched()
    {
        // Arrange: публикация не состоялась - файл исчез между записью и проверкой.
        var (fileSystem, log) = await OpenLogAsync(recordsPerSegment: 2, segments: 3);
        var snapshots = CreateSnapshots(fileSystem);
        var compactor = new LogCompactor(log, snapshots);

        fileSystem.FailureInjector = operation => operation.Kind == StorageOperationKind.Rename
            ? new System.IO.FileNotFoundException("файл снапшота исчез")
            : null;

        // Act
        await Assert.ThrowsAsync<System.IO.FileNotFoundException>(
            async () => await compactor.CompactAsync(
                new SnapshotMetadata(new LogIndex(4), new Term(1)),
                State,
                CancellationToken.None));

        // Assert: ни один сегмент не удалён, журнал читается целиком.
        fileSystem.FailureInjector = null;
        log.FirstIndex.Value.Should().Be(1);
        (await log.GetAsync(new LogIndex(1), CancellationToken.None)).Status.Should().Be(LogLookupStatus.Found);
    }

    [Fact]
    public async Task Compact_KeepsTheNewestSnapshotAndDropsTheOlderOnes()
    {
        // Arrange: два снапшота уже лежат в каталоге.
        var (fileSystem, log) = await OpenLogAsync(recordsPerSegment: 2, segments: 3);
        var snapshots = CreateSnapshots(fileSystem);
        var compactor = new LogCompactor(log, snapshots);

        var older = new SnapshotMetadata(new LogIndex(2), new Term(1));
        var newer = new SnapshotMetadata(new LogIndex(4), new Term(1));

        await snapshots.WriteAsync(older, State, CancellationToken.None);

        // Act
        await compactor.CompactAsync(newer, State, CancellationToken.None);

        // Assert: старый удалён, свежий на месте и читается, а журнал начинается сразу за ним.
        fileSystem.Exists(Path.Combine(SnapshotDirectory, older.FileName)).Should().BeFalse();
        (await snapshots.GetLatestAsync(CancellationToken.None)).Should().Be(newer);
        (await snapshots.ReadStateAsync(newer, CancellationToken.None)).ToArray().Should().Equal(State);
        log.FirstIndex.Value.Should().Be(5);
    }

    [Fact]
    public async Task CrashAfterTheSnapshotBeforeTheTrim_RecoversFromTheNewSnapshotWithTheWholeLog()
    {
        // Arrange: крах на первом удалении сегмента.
        var (fileSystem, log) = await OpenLogAsync(recordsPerSegment: 2, segments: 3);
        var snapshots = CreateSnapshots(fileSystem);
        var compactor = new LogCompactor(log, snapshots);
        var metadata = new SnapshotMetadata(new LogIndex(4), new Term(1));

        fileSystem.FailureInjector = operation => operation.Kind == StorageOperationKind.Delete
            ? new IOException("обрыв на удалении сегмента")
            : null;

        // Act
        await Assert.ThrowsAsync<IOException>(
            async () => await compactor.CompactAsync(metadata, State, CancellationToken.None));

        // Assert: после «рестарта» снапшот на месте, журнал начинается с 1 и непрерывен.
        fileSystem.FailureInjector = null;

        (await snapshots.GetLatestAsync(CancellationToken.None)).Should().Be(metadata);

        var reopened = await ReopenAsync(fileSystem);

        reopened.FirstIndex.Value.Should().Be(1);
        reopened.LastIndex.Value.Should().Be(6);
    }

    [Fact]
    public async Task CrashBetweenSegmentDeletions_LeavesAContinuousLogThatCanBeTrimmedAgain()
    {
        // Arrange: журнал из четырёх сегментов; первый удаляется, второй - уже нет.
        var (fileSystem, log) = await OpenLogAsync(recordsPerSegment: 2, segments: 4);
        var snapshots = CreateSnapshots(fileSystem);
        var compactor = new LogCompactor(log, snapshots);
        var metadata = new SnapshotMetadata(new LogIndex(6), new Term(1));

        var deletes = 0;

        fileSystem.FailureInjector = operation =>
            operation.Kind == StorageOperationKind.Delete && ++deletes == 2
                ? new IOException("обрыв между удалениями")
                : null;

        // Act
        await Assert.ThrowsAsync<IOException>(
            async () => await compactor.CompactAsync(metadata, State, CancellationToken.None));

        // Assert: каталог непрерывен, старт возможен, повторная компакция догоняет снапшот.
        fileSystem.FailureInjector = null;

        var reopened = await ReopenAsync(fileSystem);

        reopened.FirstIndex.Value.Should().Be(3);
        reopened.LastIndex.Value.Should().Be(8);

        await new LogCompactor(reopened, snapshots).CompactAsync(metadata, State, CancellationToken.None);

        reopened.FirstIndex.Value.Should().Be(7);
    }

    [Fact]
    public async Task Compact_NeverRemovesTheSegmentsAboveTheSnapshot()
    {
        // Arrange: снапшот накрывает середину второго сегмента.
        var (fileSystem, log) = await OpenLogAsync(recordsPerSegment: 2, segments: 3);
        var compactor = new LogCompactor(log, CreateSnapshots(fileSystem));

        // Act
        await compactor.CompactAsync(new SnapshotMetadata(new LogIndex(3), new Term(1)), State, CancellationToken.None);

        // Assert: сегмент с записями 3-4 остаётся целиком - удаляется только целый сегмент.
        // Первая живая запись журнала - 4: запись 3 уже в снапшоте, хотя её сегмент ещё на диске.
        log.FirstIndex.Value.Should().Be(4);
        fileSystem.Exists(SegmentPath(3)).Should().BeTrue();
        (await log.GetAsync(new LogIndex(3), CancellationToken.None)).Status.Should().Be(LogLookupStatus.Compacted);
        (await log.GetAsync(new LogIndex(4), CancellationToken.None)).Status.Should().Be(LogLookupStatus.Found);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(2)]
    public async Task Compact_WhenTheStoreDoesNotPublishWhatWasWritten_RefusesToTrimTheLog(int? latestIndex)
    {
        // Arrange: хранилище, которое не публикует ничего - имитация молчаливого отказа диска.
        // Доверять возврату WriteAsync нельзя, поэтому компактор обязан перечитать каталог.
        var (fileSystem, log) = await OpenLogAsync(recordsPerSegment: 2, segments: 3);
        var snapshots = new SilentSnapshotStore(
            latestIndex is { } index ? new SnapshotMetadata(new LogIndex(index), new Term(1)) : null);

        // Act
        var error = await Assert.ThrowsAsync<System.IO.InvalidDataException>(
            async () => await new LogCompactor(log, snapshots).CompactAsync(
                new SnapshotMetadata(new LogIndex(4), new Term(1)),
                State,
                CancellationToken.None));

        // Assert: журнал не тронут, в сообщении назван актуальный снапшот (или его отсутствие).
        error.Message.Should().Contain(latestIndex is null ? "нет" : latestIndex.Value.ToString());
        log.FirstIndex.Value.Should().Be(1);
        fileSystem.Operations.Should().NotContain(operation => operation.Kind == StorageOperationKind.Delete);
    }

    [Fact]
    public async Task Compact_WhenTheSameSnapshotIsAlreadyPublished_OnlyCompletesTheTrim()
    {
        // Arrange: крах случился после публикации, до обрезки - значит снапшот уже на диске.
        var (fileSystem, log) = await OpenLogAsync(recordsPerSegment: 2, segments: 3);
        var snapshots = CreateSnapshots(fileSystem);
        var metadata = new SnapshotMetadata(new LogIndex(4), new Term(1));

        await snapshots.WriteAsync(metadata, State, CancellationToken.None);

        var renames = fileSystem.Operations.Count(operation => operation.Kind == StorageOperationKind.Rename);

        // Act: старт повторяет компакцию тем же снапшотом.
        await new LogCompactor(log, snapshots).CompactAsync(metadata, State, CancellationToken.None);

        // Assert: файл не переписывался, а журнал догнал снапшот.
        fileSystem.Operations.Count(operation => operation.Kind == StorageOperationKind.Rename).Should().Be(renames);
        log.FirstIndex.Value.Should().Be(5);
    }

    [Fact]
    public async Task Compact_WithAnOlderSnapshotThanPublished_ThrowsAndKeepsTheLog()
    {
        // Arrange: в каталоге снапшот на 4, просят сжать до 2.
        var (fileSystem, log) = await OpenLogAsync(recordsPerSegment: 2, segments: 3);
        var snapshots = CreateSnapshots(fileSystem);

        await snapshots.WriteAsync(new SnapshotMetadata(new LogIndex(4), new Term(1)), State, CancellationToken.None);

        // Act
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await new LogCompactor(log, snapshots).CompactAsync(
                new SnapshotMetadata(new LogIndex(2), new Term(1)),
                State,
                CancellationToken.None));

        // Assert
        log.FirstIndex.Value.Should().Be(1);
    }

    [Fact]
    public async Task Compact_WithANonPositiveSnapshotIndex_ThrowsAndKeepsTheLog()
    {
        var (fileSystem, log) = await OpenLogAsync(recordsPerSegment: 2, segments: 2);
        var compactor = new LogCompactor(log, CreateSnapshots(fileSystem));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await compactor.CompactAsync(new SnapshotMetadata(LogIndex.Empty, new Term(1)), State, CancellationToken.None));

        log.FirstIndex.Value.Should().Be(1);
    }

    /// <summary>Открывает журнал и доводит его до нужного числа сегментов настоящим писателем.</summary>
    private static async Task<(FakeStorageFileSystem FileSystem, ILogStore Log)> OpenLogAsync(
        int recordsPerSegment,
        int segments)
    {
        var fileSystem = new FakeStorageFileSystem();
        var log = await ReopenAsync(fileSystem, recordsPerSegment: recordsPerSegment);

        for (var index = 1; index <= recordsPerSegment * segments; index++)
        {
            await log.AppendAsync([LogTestData.Command(index)], CancellationToken.None);
        }

        return (fileSystem, log);
    }

    private static async ValueTask<ILogStore> ReopenAsync(
        FakeStorageFileSystem fileSystem,
        int recordsPerSegment = 2)
    {
        // Размер сегмента вмещает ровно recordsPerSegment фреймов: следующий append делает rollover.
        var maxSegmentSize = LogSegmentFormat.HeaderSize
            + (recordsPerSegment * LogFrameCodec.GetFrameSize(4))
            + 1;

        var open = await LogStore.OpenAsync(
            fileSystem,
            new TestClock(startMs: LogTestData.MonotonicStartMs, unixTimeMs: LogTestData.NowMs),
            new SegmentWriterOptions { Directory = LogDirectory, MaxSegmentSize = maxSegmentSize },
            CancellationToken.None);

        open.IsSuccess.Should().BeTrue(open.Reason);

        return open.Store!;
    }

    private static SnapshotStore CreateSnapshots(FakeStorageFileSystem fileSystem)
        => new(fileSystem, new TestClock(unixTimeMs: LogTestData.NowMs), SnapshotDirectory);

    private static string SegmentPath(long baseIndex)
        => Path.Combine(LogDirectory, LogSegmentFormat.GetFileName(new LogIndex(baseIndex)));

    /// <summary>Хранилище снапшотов, которое принимает запись и ничего не публикует.</summary>
    private sealed class SilentSnapshotStore(SnapshotMetadata? latest) : ISnapshotStore
    {
        public ValueTask<SnapshotMetadata?> GetLatestAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(latest);

        public ValueTask WriteAsync(
            SnapshotMetadata metadata,
            ReadOnlyMemory<byte> state,
            CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask<ReadOnlyMemory<byte>> ReadStateAsync(
            SnapshotMetadata metadata,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);

        public ValueTask DeleteOlderThanAsync(LogIndex lastIncludedIndex, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }
}
