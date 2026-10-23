using System.Globalization;
using RaftBroker.Core.Primitives;
using RaftBroker.Storage.Compaction;
using RaftBroker.Storage.IO;
using RaftBroker.Storage.Log;
using RaftBroker.Storage.Snapshot;
using RaftBroker.TestKit.IO;
using RaftBroker.TestKit.Time;

namespace RaftBroker.IntegrationTests.Storage;

/// <summary>
/// Сжатие истории на настоящем диске (<c>S1-T10</c>).
/// </summary>
/// <remarks>
/// <para>
/// Дубль файловой системы умеет падать на заданной операции, поэтому порядок шагов и крах в каждой
/// точке проверяются на нём (<c>LogCompactorTests</c>). Здесь проверяется другое: что на реальной
/// файловой системе после «снапшот, проверка, обрезка» остаётся ровно то, что ожидалось, - журнал,
/// начинающийся сразу за снапшотом, и снапшот, который читается при следующем старте.
/// </para>
/// <para>
/// Проверка идёт через новое открытие обоих хранилищ, а не через те же объекты: смысл рестарта в
/// том, что состояние берётся с диска, а не из памяти процесса.
/// </para>
/// </remarks>
public sealed class CompactionDiskTests : IDisposable
{
    private static readonly byte[] State = [7, 7, 7, 7];

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "raft-broker-compaction-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    [Fact]
    public async Task Compact_OnARealDisk_LeavesTheLogStartingRightAfterTheSnapshot()
    {
        // Arrange: четыре сегмента по две записи.
        var logDirectory = Path.Combine(_directory, "log");
        var snapshotDirectory = Path.Combine(_directory, "snapshots");
        var clock = new TestClock(startMs: 42, unixTimeMs: 1_700_000_000_000);
        var options = new SegmentWriterOptions { Directory = logDirectory, MaxSegmentSize = SegmentSizeForTwoRecords };

        var open = await LogStore.OpenAsync(
            SystemStorageFileSystem.Instance,
            clock,
            options,
            TestContext.Current.CancellationToken);

        open.IsSuccess.Should().BeTrue(open.Reason);

        var log = open.Store!;

        await using (log)
        {
            for (var index = 1; index <= 8; index++)
            {
                await log.AppendAsync(
                    [LogRecord.Command(new LogIndex(index), new Term(1), Payload(index))],
                    TestContext.Current.CancellationToken);
            }

            // Act
            var metadata = new SnapshotMetadata(new LogIndex(6), new Term(1));

            await new LogCompactor(log, new SnapshotStore(SystemStorageFileSystem.Instance, clock, snapshotDirectory))
                .CompactAsync(metadata, State, TestContext.Current.CancellationToken);

            log.FirstIndex.Value.Should().Be(7);
        }

        // Assert: после рестарта журнал непрерывен, снапшот читается, лишних файлов нет.
        var reopened = await LogStore.OpenAsync(
            SystemStorageFileSystem.Instance,
            clock,
            options,
            TestContext.Current.CancellationToken);

        reopened.IsSuccess.Should().BeTrue(reopened.Reason);

        await using var store = reopened.Store!;

        store.FirstIndex.Value.Should().Be(7);
        store.LastIndex.Value.Should().Be(8);
        (await store.GetAsync(new LogIndex(7), TestContext.Current.CancellationToken)).Status
            .Should().Be(LogLookupStatus.Found);
        (await store.GetAsync(new LogIndex(6), TestContext.Current.CancellationToken)).Status
            .Should().Be(LogLookupStatus.Compacted);

        var snapshots = new SnapshotStore(SystemStorageFileSystem.Instance, clock, snapshotDirectory);

        (await snapshots.GetLatestAsync(TestContext.Current.CancellationToken))
            .Should().Be(new SnapshotMetadata(new LogIndex(6), new Term(1)));
        (await snapshots.ReadStateAsync(
            new SnapshotMetadata(new LogIndex(6), new Term(1)),
            TestContext.Current.CancellationToken)).ToArray().Should().Equal(State);

        System.IO.Directory.GetFiles(logDirectory).Select(Path.GetFileName).Should().Equal(
            LogSegmentFormat.GetFileName(new LogIndex(7)));
        System.IO.Directory.GetFiles(snapshotDirectory).Select(Path.GetFileName).Should().Equal(
            SnapshotFormat.GetFileName(new LogIndex(6)));
    }

    /// <summary>Размер сегмента, в который влезают ровно две записи с payload в четыре байта.</summary>
    private static long SegmentSizeForTwoRecords
        => LogSegmentFormat.HeaderSize + (2 * LogFrameCodec.GetFrameSize(4)) + 1;

    private static byte[] Payload(long index)
        => [unchecked((byte)index), unchecked((byte)(index + 1)), unchecked((byte)(index + 2)), 0];

    /// <inheritdoc />
    public void Dispose()
    {
        if (System.IO.Directory.Exists(_directory))
        {
            System.IO.Directory.Delete(_directory, recursive: true);
        }
    }
}
