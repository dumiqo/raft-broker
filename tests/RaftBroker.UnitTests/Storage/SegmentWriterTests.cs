using RaftBroker.Core.Primitives;
using RaftBroker.Storage.Log;
using RaftBroker.TestKit.IO;
using RaftBroker.TestKit.Time;

namespace RaftBroker.UnitTests.Storage;

/// <summary>
/// S1-T03: раскладка сегментов, порядок rollover и проверки последовательности записей.
/// </summary>
/// <remarks>
/// Долговечность (число и порядок fsync) проверяется отдельно в
/// <see cref="SegmentWriterDurabilityTests"/>. Векторы байтов фрейма и заголовка - в S1-T02;
/// здесь проверяется то, что писатель делает с ними: имя файла, порядок операций,
/// границы сегментов и отказ на неверной последовательности.
/// </remarks>
public sealed class SegmentWriterTests
{
    /// <summary>Максимум, при котором в сегмент помещаются заголовок и два фрейма, но не три.</summary>
    private const int TwoFrameSegment = 100;

    /// <summary>Заголовок 30 байт и фрейм с payload 4 байта (21 + 4 + 4).</summary>
    private static int CommandFrameSize => LogFrameCodec.GetFrameSize(4);

    [Fact]
    public async Task FirstAppend_CreatesSegmentNamedAfterFirstRecord()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using var writer = LogTestData.CreateWriter(fileSystem);

        await writer.AppendAsync([LogTestData.Command(1)], CancellationToken.None);

        fileSystem.Paths.Should().Equal(LogTestData.SegmentPath(1));
        writer.LastIndex!.Value.Value.Should().Be(1);
        writer.LastTerm!.Value.Value.Should().Be(1);
        writer.CurrentSegmentSize.Should().Be(LogSegmentFormat.HeaderSize + CommandFrameSize);
    }

    [Fact]
    public async Task FirstSegment_MayStartAfterOneWhenRecoveryBeginsFromSnapshot()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using var writer = LogTestData.CreateWriter(fileSystem);

        await writer.AppendAsync([LogTestData.Command(5)], CancellationToken.None);

        fileSystem.Paths.Should().Equal(LogTestData.SegmentPath(5));

        var header = SegmentHeaderCodec.Read(fileSystem.ReadAllBytes(LogTestData.SegmentPath(5)));
        header.IsSuccess.Should().BeTrue(header.Reason);
        header.Header!.BaseIndex.Value.Should().Be(5);
        header.Header.CreatedAtUnixMs.Should().Be(LogTestData.NowMs);
    }

    [Fact]
    public async Task SegmentHeader_TakesTheTimestampFromUnixTimeNotFromTheMonotonicCounter()
    {
        var fileSystem = new FakeStorageFileSystem();
        var clock = new TestClock(startMs: LogTestData.MonotonicStartMs, unixTimeMs: LogTestData.NowMs);
        await using var writer = new SegmentWriter(
            fileSystem,
            clock,
            new SegmentWriterOptions { Directory = LogTestData.Directory });

        await writer.AppendAsync([LogTestData.Command(1)], CancellationToken.None);

        // Формат требует Unix-мс (§3), а не монотонные мс процесса: это разные значения IClock.
        var header = SegmentHeaderCodec.Read(fileSystem.ReadAllBytes(LogTestData.SegmentPath(1)));
        header.IsSuccess.Should().BeTrue(header.Reason);
        header.Header!.CreatedAtUnixMs.Should().Be(LogTestData.NowMs);
        clock.TimestampMs.Should().Be(LogTestData.MonotonicStartMs);
    }

    [Fact]
    public async Task Append_WritesHeaderThenFrame_WithFsyncBeforeTheFrame()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using var writer = LogTestData.CreateWriter(fileSystem);

        await writer.AppendAsync([LogTestData.Command(1)], CancellationToken.None);

        // §9.3: заголовок нового сегмента попадает на устройство раньше первого фрейма,
        // иначе сегмент с нечитаемым заголовком мог бы содержать подтверждённые записи.
        // Последний fsync - групповой, режим по умолчанию.
        Summarize(fileSystem).Should().Equal(
            (StorageOperationKind.Create, FileName(1)),
            (StorageOperationKind.Write, FileName(1)),
            (StorageOperationKind.FlushToDisk, FileName(1)),
            (StorageOperationKind.Write, FileName(1)),
            (StorageOperationKind.FlushToDisk, FileName(1)));

        fileSystem.Operations[1].ByteCount.Should().Be(LogSegmentFormat.HeaderSize);
        fileSystem.Operations[3].ByteCount.Should().Be(CommandFrameSize);
    }

    [Fact]
    public async Task Append_WritesRecordsThatReadBackUnchanged()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using var writer = LogTestData.CreateWriter(fileSystem);

        LogRecord[] records =
        [
            LogTestData.Command(1),
            LogTestData.Noop(2, term: 2),
            LogTestData.Command(3, term: 2, payloadLength: 0),
        ];

        await writer.AppendAsync(records, CancellationToken.None);

        LogTestData.ReadSegment(fileSystem, 1).Should().Equal(records);
    }

    [Fact]
    public async Task Append_RejectsAGapInTheBatchBeforeWritingAnything()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using var writer = LogTestData.CreateWriter(fileSystem);

        Func<Task> append = async () => await writer.AppendAsync(
            [LogTestData.Command(2), LogTestData.Command(4)],
            CancellationToken.None);

        await append.Should().ThrowAsync<InvalidOperationException>();
        fileSystem.Operations.Should().BeEmpty("неверная последовательность обязана быть видна до первого байта на диске");
    }

    [Fact]
    public async Task Append_RejectsAGapBetweenCalls()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using var writer = LogTestData.CreateWriter(fileSystem);

        await writer.AppendAsync([LogTestData.Command(1)], CancellationToken.None);
        var operationsAfterFirstAppend = fileSystem.Operations.Count;

        Func<Task> append = async () => await writer.AppendAsync([LogTestData.Command(3)], CancellationToken.None);

        await append.Should().ThrowAsync<InvalidOperationException>();
        fileSystem.Operations.Should().HaveCount(operationsAfterFirstAppend);
    }

    [Fact]
    public async Task Append_RejectsARepeatedIndex()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using var writer = LogTestData.CreateWriter(fileSystem);

        Func<Task> append = async () => await writer.AppendAsync(
            [LogTestData.Command(2), LogTestData.Command(2)],
            CancellationToken.None);

        await append.Should().ThrowAsync<InvalidOperationException>();
        fileSystem.Operations.Should().BeEmpty();
    }

    [Fact]
    public async Task Append_RejectsADecreasingTerm()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using var writer = LogTestData.CreateWriter(fileSystem);

        Func<Task> append = async () => await writer.AppendAsync(
            [LogTestData.Command(1, term: 5), LogTestData.Command(2, term: 4)],
            CancellationToken.None);

        await append.Should().ThrowAsync<InvalidOperationException>();
        fileSystem.Operations.Should().BeEmpty();
    }

    [Fact]
    public async Task Append_AllowsTheTermToStayEqual()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using var writer = LogTestData.CreateWriter(fileSystem);

        LogRecord[] records =
        [
            LogTestData.Command(1, term: 3),
            LogTestData.Noop(2, term: 3),
        ];

        await writer.AppendAsync(records, CancellationToken.None);

        LogTestData.ReadSegment(fileSystem, 1).Should().Equal(records);
    }

    [Fact]
    public async Task Append_RejectsAFrameBiggerThanTheWholeSegment()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using var writer = LogTestData.CreateWriter(
            fileSystem,
            maxSegmentSize: LogSegmentFormat.FrameOverhead);

        Func<Task> append = async () => await writer.AppendAsync(
            [LogTestData.Command(1, payloadLength: 1)],
            CancellationToken.None);

        await append.Should().ThrowAsync<InvalidOperationException>();
        fileSystem.Operations.Should().BeEmpty("фрейм между сегментами не разрывается: ошибка обязана быть программной, а не тихой записью");
    }

    [Fact]
    public async Task Append_WithAnAlreadyCancelledToken_WritesNothing()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using var writer = LogTestData.CreateWriter(fileSystem);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        Func<Task> append = async () => await writer.AppendAsync([LogTestData.Command(1)], cancellation.Token);

        await append.Should().ThrowAsync<OperationCanceledException>();
        fileSystem.Operations.Should().BeEmpty();
    }

    [Fact]
    public async Task EmptyAppend_DoesNotCreateASegment()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using var writer = LogTestData.CreateWriter(fileSystem);

        await writer.AppendAsync([], CancellationToken.None);

        fileSystem.Operations.Should().BeEmpty();
        writer.LastIndex.HasValue.Should().BeFalse();
        writer.CurrentSegmentPath.Should().BeNull();
        writer.CurrentSegmentSize.Should().Be(0);
    }

    [Fact]
    public async Task Rollover_SplitsSegmentsAtRecordBoundaries()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using var writer = LogTestData.CreateWriter(fileSystem, maxSegmentSize: TwoFrameSegment);

        LogRecord[] records =
        [
            LogTestData.Command(1),
            LogTestData.Command(2),
            LogTestData.Command(3),
            LogTestData.Command(4),
        ];

        await writer.AppendAsync(records, CancellationToken.None);

        fileSystem.Paths.Should().Equal(
            LogTestData.SegmentPath(1),
            LogTestData.SegmentPath(3));

        LogTestData.ReadSegment(fileSystem, 1).Should().Equal(records[0], records[1]);
        LogTestData.ReadSegment(fileSystem, 3).Should().Equal(records[2], records[3]);
        writer.LastIndex!.Value.Value.Should().Be(4);
        writer.CurrentSegmentPath.Should().Be(LogTestData.SegmentPath(3));
    }

    [Fact]
    public async Task Rollover_SealsAndCreatesInTheDocumentedOrder()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using var writer = LogTestData.CreateWriter(fileSystem, maxSegmentSize: TwoFrameSegment);

        await writer.AppendAsync(
            [
                LogTestData.Command(1),
                LogTestData.Command(2),
                LogTestData.Command(3),
            ],
            CancellationToken.None);

        // §8: fsync запечатываемого сегмента, затем создание нового файла, fsync его заголовка,
        // и только потом фрейм, для которого сделан новый сегмент.
        Summarize(fileSystem).Should().Equal(
            (StorageOperationKind.Create, FileName(1)),
            (StorageOperationKind.Write, FileName(1)),
            (StorageOperationKind.FlushToDisk, FileName(1)),
            (StorageOperationKind.Write, FileName(1)),
            (StorageOperationKind.Write, FileName(1)),
            (StorageOperationKind.FlushToDisk, FileName(1)),
            (StorageOperationKind.Create, FileName(3)),
            (StorageOperationKind.Write, FileName(3)),
            (StorageOperationKind.FlushToDisk, FileName(3)),
            (StorageOperationKind.Write, FileName(3)),
            (StorageOperationKind.FlushToDisk, FileName(3)));
    }

    [Fact]
    public async Task Rollover_KeepsEverySegmentWithinItsLimit()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using var writer = LogTestData.CreateWriter(fileSystem, maxSegmentSize: TwoFrameSegment);

        await writer.AppendAsync(
            [
                LogTestData.Command(1),
                LogTestData.Command(2),
                LogTestData.Command(3),
                LogTestData.Command(4),
            ],
            CancellationToken.None);

        fileSystem.ReadAllBytes(LogTestData.SegmentPath(1)).Length.Should().BeLessThanOrEqualTo(TwoFrameSegment);
        fileSystem.ReadAllBytes(LogTestData.SegmentPath(3)).Length.Should().BeLessThanOrEqualTo(TwoFrameSegment);
        writer.CurrentSegmentSize.Should().BeLessThanOrEqualTo(TwoFrameSegment);
    }

    [Fact]
    public async Task Segment_ExceedsTheLimitWhenASingleFrameCannotFitOtherwise()
    {
        // Фрейм не разрывается (§8), поэтому сегмент из одного фрейма может превысить номинальный
        // максимум: это не ошибка, а следствие двух правил формата.
        var fileSystem = new FakeStorageFileSystem();
        var maxSegmentSize = CommandFrameSize;
        await using var writer = LogTestData.CreateWriter(fileSystem, maxSegmentSize);

        await writer.AppendAsync(
            [LogTestData.Command(1), LogTestData.Command(2)],
            CancellationToken.None);

        var singleFrameSegmentSize = LogSegmentFormat.HeaderSize + CommandFrameSize;

        fileSystem.Paths.Should().Equal(
            LogTestData.SegmentPath(1),
            LogTestData.SegmentPath(2));
        fileSystem.ReadAllBytes(LogTestData.SegmentPath(1)).Length.Should().Be(singleFrameSegmentSize);
        writer.CurrentSegmentSize.Should().Be(singleFrameSegmentSize);
    }

    [Fact]
    public async Task Append_AfterDispose_Throws()
    {
        var fileSystem = new FakeStorageFileSystem();
        var writer = LogTestData.CreateWriter(fileSystem);
        await writer.DisposeAsync();

        Func<Task> append = async () => await writer.AppendAsync([LogTestData.Command(1)], CancellationToken.None);

        await append.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task Dispose_IsIdempotent()
    {
        var fileSystem = new FakeStorageFileSystem();
        var writer = LogTestData.CreateWriter(fileSystem);

        await writer.DisposeAsync();
        Func<Task> disposeAgain = async () => await writer.DisposeAsync();

        await disposeAgain.Should().NotThrowAsync();
    }

    [Fact]
    public void Constructor_RejectsAnEmptyDirectory()
    {
        var fileSystem = new FakeStorageFileSystem();

        Func<SegmentWriter> create = () => new SegmentWriter(
            fileSystem,
            new TestClock(),
            new SegmentWriterOptions { Directory = "  " });

        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_RejectsASegmentLimitBelowTheSmallestFrame()
    {
        var fileSystem = new FakeStorageFileSystem();

        Func<SegmentWriter> create = () => LogTestData.CreateWriter(
            fileSystem,
            maxSegmentSize: LogSegmentFormat.FrameOverhead - 1);

        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_RejectsAnUnknownDurabilityMode()
    {
        var fileSystem = new FakeStorageFileSystem();

        Func<SegmentWriter> create = () => LogTestData.CreateWriter(
            fileSystem,
            defaultMode: (DurabilityMode)99);

        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static string FileName(long baseIndex) => Path.GetFileName(LogTestData.SegmentPath(baseIndex));

    private static (StorageOperationKind Kind, string Name)[] Summarize(FakeStorageFileSystem fileSystem)
        => [.. fileSystem.Operations.Select(operation => (operation.Kind, Path.GetFileName(operation.Path)))];
}
