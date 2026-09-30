using System.Globalization;
using RaftBroker.Core.Primitives;
using RaftBroker.Storage.IO;
using RaftBroker.Storage.Log;
using RaftBroker.TestKit.Time;

namespace RaftBroker.UnitTests.Storage;

/// <summary>
/// S1-T03: рантайм-реализация файловой системы и сквозная запись сегмента на настоящий диск.
/// </summary>
/// <remarks>
/// Дубль из <c>RaftBroker.TestKit</c> проверяет политики и порядок операций, но не то,
/// что <see cref="SystemStorageFileSystem"/> действительно создаёт файл и fsync-ает его.
/// Этот тест закрывает границу: он единственный в S1-T03 пишет на диск, и каталог
/// создаётся во временном каталоге, а не в репозитории.
/// </remarks>
public sealed class SystemStorageFileSystemTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "raft-broker-system-storage-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    [Fact]
    public async Task CreateAsync_WritesBytesThatSurviveClosingTheFile()
    {
        var path = Path.Combine(_directory, "nested", LogSegmentFormat.GetFileName(new LogIndex(1)));

        await using (var file = await SystemStorageFileSystem.Instance.CreateAsync(path, CancellationToken.None))
        {
            file.Path.Should().Be(Path.GetFullPath(path));

            await file.WriteAsync(new byte[] { 1, 2, 3, 4 }, CancellationToken.None);
            await file.FlushToDiskAsync(CancellationToken.None);
        }

        File.Exists(path).Should().BeTrue("CreateAsync обязан создать и каталог, и файл");
        File.ReadAllBytes(path).Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public async Task CreateAsync_OnAnExistingFile_Throws()
    {
        var path = Path.Combine(_directory, LogSegmentFormat.GetFileName(new LogIndex(1)));

        await using (var file = await SystemStorageFileSystem.Instance.CreateAsync(path, CancellationToken.None))
        {
            await file.WriteAsync(new byte[] { 1 }, CancellationToken.None);
        }

        Func<Task> create = async () => await SystemStorageFileSystem.Instance.CreateAsync(path, CancellationToken.None);

        // Перезапись существующего сегмента форматом запрещена: она обязана быть ошибкой.
        await create.Should().ThrowAsync<IOException>();
    }

    [Fact]
    public async Task SegmentWriter_OnRealFileSystem_WritesSegmentsTheCodecsReadBack()
    {
        var options = new SegmentWriterOptions
        {
            Directory = _directory,
            MaxSegmentSize = 100,
        };

        LogRecord[] records =
        [
            SegmentWriterTestData.Command(1),
            SegmentWriterTestData.Command(2),
            SegmentWriterTestData.Command(3),
        ];

        await using (var writer = new SegmentWriter(
            SystemStorageFileSystem.Instance,
            new TestClock(startMs: SegmentWriterTestData.MonotonicStartMs, unixTimeMs: SegmentWriterTestData.NowMs),
            options))
        {
            await writer.AppendAsync(records, CancellationToken.None);
        }

        var segments = Directory.EnumerateFiles(_directory, "*.seg").Order(StringComparer.Ordinal).ToList();

        segments.Should().Equal(
            Path.Combine(_directory, LogSegmentFormat.GetFileName(new LogIndex(1))),
            Path.Combine(_directory, LogSegmentFormat.GetFileName(new LogIndex(3))));

        ReadAllRecords(segments).Should().Equal(records);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static List<LogRecord> ReadAllRecords(IEnumerable<string> segments)
    {
        var records = new List<LogRecord>();

        foreach (var segment in segments)
        {
            var bytes = File.ReadAllBytes(segment);

            var header = SegmentHeaderCodec.Read(bytes);
            header.IsSuccess.Should().BeTrue(header.Reason);
            header.Header!.CreatedAtUnixMs.Should().Be(SegmentWriterTestData.NowMs);

            var offset = LogSegmentFormat.HeaderSize;

            while (offset < bytes.Length)
            {
                var frame = LogFrameCodec.Read(bytes.AsSpan(offset));
                frame.IsSuccess.Should().BeTrue(frame.Reason);
                records.Add(frame.Record!);
                offset += frame.BytesConsumed;
            }
        }

        return records;
    }
}
