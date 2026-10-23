using System.Globalization;
using RaftBroker.Core.Primitives;
using RaftBroker.Storage.IO;
using RaftBroker.Storage.Log;
using RaftBroker.TestKit.Time;

namespace RaftBroker.UnitTests.Storage;

/// <summary>
/// S1-T03: рантайм-реализация файловой системы и сквозная запись сегмента на настоящий диск.
/// S1-T04: то же самое для чтения.
/// S1-T05: то же самое для усечения, удаления и восстановления оборванного хвоста.
/// </summary>
/// <remarks>
/// Дубль из <c>RaftBroker.TestKit</c> проверяет политики и порядок операций, но не то,
/// что <see cref="SystemStorageFileSystem"/> действительно создаёт файл, fsync-ает его
/// и читает его по смещению. Этот тест закрывает границу: он единственный в S1-T03 и S1-T04
/// пишет на диск, и каталог создаётся во временном каталоге, а не в репозитории.
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
            LogTestData.Command(1),
            LogTestData.Command(2),
            LogTestData.Command(3),
        ];

        await using (var writer = new SegmentWriter(
            SystemStorageFileSystem.Instance,
            new TestClock(startMs: LogTestData.MonotonicStartMs, unixTimeMs: LogTestData.NowMs),
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

    [Fact]
    public async Task OpenReadAsync_ReturnsTheBytesWrittenEarlier()
    {
        var path = Path.Combine(_directory, LogSegmentFormat.GetFileName(new LogIndex(1)));

        await using (var file = await SystemStorageFileSystem.Instance.CreateAsync(path, CancellationToken.None))
        {
            await file.WriteAsync(new byte[] { 1, 2, 3, 4, 5, 6 }, CancellationToken.None);
        }

        await using var readFile = await SystemStorageFileSystem.Instance.OpenReadAsync(path, CancellationToken.None);

        readFile.Path.Should().Be(Path.GetFullPath(path));
        readFile.Length.Should().Be(6);

        // Адресация по смещению: позиция в потоке не переносится между вызовами.
        var single = new byte[1];
        (await readFile.ReadAsync(3, single, CancellationToken.None)).Should().Be(1);
        single[0].Should().Be(4);

        var rest = new byte[8];
        (await readFile.ReadAsync(2, rest, CancellationToken.None)).Should().Be(4);
        rest[..4].Should().Equal(3, 4, 5, 6);
        (await readFile.ReadAsync(6, rest, CancellationToken.None)).Should().Be(0, "за концом файла чтение обязано возвращать 0, а не падать");
    }

    [Fact]
    public async Task OpenReadAsync_MissingFile_Throws()
    {
        var path = Path.Combine(_directory, LogSegmentFormat.GetFileName(new LogIndex(1)));

        Func<Task> open = async () => await SystemStorageFileSystem.Instance.OpenReadAsync(path, CancellationToken.None);

        await open.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task OpenReadAsync_WhileTheWriterStillHoldsTheFile_ReadsTheDurablyWrittenBytes()
    {
        var options = new SegmentWriterOptions
        {
            Directory = _directory,
            MaxSegmentSize = LogSegmentFormat.MaxSegmentSize,
        };

        var clock = new TestClock(
            startMs: LogTestData.MonotonicStartMs,
            unixTimeMs: LogTestData.NowMs);
        var writer = new SegmentWriter(SystemStorageFileSystem.Instance, clock, options);
        await using (writer)
        {
            await writer.AppendAsync([LogTestData.Command(1)], CancellationToken.None);
            var path = writer.CurrentSegmentPath!;

            // Последний сегмент остаётся открытым у писателя; читатель обязан его открыть
            // (FileShare.ReadWrite) и увидеть то, что уже долговечно.
            await using var reader = (await SegmentReader.OpenAsync(SystemStorageFileSystem.Instance, path, CancellationToken.None)).Reader!;

            var scan = await reader.ScanAsync(CancellationToken.None);

            scan.Status.Should().Be(SegmentScanStatus.Complete);
            scan.LastIndex!.Value.Value.Should().Be(1);

            await writer.AppendAsync([LogTestData.Command(2), LogTestData.Command(3)], CancellationToken.None);
            await writer.FlushToDiskAsync(CancellationToken.None);

            // Длина у читателя - снимок на момент открытия, но новый фрейм начинается
            // за ней, поэтому он его не видит: перечитывать хвост - дело восстановления.
            reader.Length.Should().Be(LogSegmentFormat.HeaderSize + LogFrameCodec.GetFrameSize(4));
        }
    }

    [Fact]
    public async Task SegmentReader_OnRealFileSystem_ReadsBackWhatTheWriterWrote()
    {
        var options = new SegmentWriterOptions
        {
            Directory = _directory,
            MaxSegmentSize = LogSegmentFormat.MaxSegmentSize,
        };

        LogRecord[] records =
        [
            LogTestData.Command(1, term: 1),
            LogTestData.Noop(2, term: 1),
            LogTestData.Command(3, term: 2),
        ];

        string path;

        var clock = new TestClock(
            startMs: LogTestData.MonotonicStartMs,
            unixTimeMs: LogTestData.NowMs);
        await using (var writer = new SegmentWriter(SystemStorageFileSystem.Instance, clock, options))
        {
            await writer.AppendAsync(records, CancellationToken.None);
            path = writer.CurrentSegmentPath!;
        }

        var open = await SegmentReader.OpenAsync(SystemStorageFileSystem.Instance, path, CancellationToken.None);
        open.IsSuccess.Should().BeTrue(open.Reason);
        await using var reader = open.Reader!;

        var scan = await reader.ScanAsync(CancellationToken.None);
        scan.Status.Should().Be(SegmentScanStatus.Complete);
        reader.Header.CreatedAtUnixMs.Should().Be(LogTestData.NowMs);
        (await reader.ReadAtAsync(new LogIndex(2), CancellationToken.None))!.Should().Be(records[1]);

        var forward = new List<LogRecord>();

        await foreach (var record in reader.ReadForwardAsync(CancellationToken.None))
        {
            forward.Add(record);
        }

        forward.Should().Equal(records);
    }

    [Fact]
    public async Task SegmentReader_OnTruncatedRealFile_ReportsTailProblemAndKeepsTheValidPrefix()
    {
        var options = new SegmentWriterOptions
        {
            Directory = _directory,
            MaxSegmentSize = LogSegmentFormat.MaxSegmentSize,
        };

        var clock = new TestClock(
            startMs: LogTestData.MonotonicStartMs,
            unixTimeMs: LogTestData.NowMs);
        string path;

        await using (var writer = new SegmentWriter(SystemStorageFileSystem.Instance, clock, options))
        {
            await writer.AppendAsync(
                [LogTestData.Command(1), LogTestData.Command(2)],
                CancellationToken.None);
            path = writer.CurrentSegmentPath!;
        }

        // Обрыв хвоста без записи: именно так выглядит падение посреди дозаписи (§9.3).
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            file.SetLength(file.Length - 2);
        }

        await using var reader = (await SegmentReader.OpenAsync(SystemStorageFileSystem.Instance, path, CancellationToken.None)).Reader!;
        var scan = await reader.ScanAsync(CancellationToken.None);

        scan.Status.Should().Be(SegmentScanStatus.TailProblem);
        scan.FrameCount.Should().Be(1);
        scan.ValidEndOffset.Should().Be(LogSegmentFormat.HeaderSize + LogFrameCodec.GetFrameSize(4));

        var forward = new List<LogRecord>();

        await foreach (var record in reader.ReadForwardAsync(CancellationToken.None))
        {
            forward.Add(record);
        }

        forward.Should().Equal(LogTestData.Command(1));
    }

    [Fact]
    public async Task TruncateAsync_ShrinksTheFileOnDisk()
    {
        var path = Path.Combine(_directory, "data.bin");
        var fileSystem = SystemStorageFileSystem.Instance;

        await using (var file = await fileSystem.CreateAsync(path, CancellationToken.None))
        {
            await file.WriteAsync(new byte[] { 1, 2, 3, 4, 5 }, CancellationToken.None);
            await file.FlushToDiskAsync(CancellationToken.None);
        }

        await fileSystem.TruncateAsync(path, 3, CancellationToken.None);

        // Усечение обязано быть видно на диске, а не только в открытом дескрипторе.
        File.ReadAllBytes(path).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task TruncateAsync_ToALongerLength_ThrowsAndLeavesTheFileAlone()
    {
        var path = Path.Combine(_directory, "data.bin");
        var fileSystem = SystemStorageFileSystem.Instance;

        await using (var file = await fileSystem.CreateAsync(path, CancellationToken.None))
        {
            await file.WriteAsync(new byte[] { 1, 2 }, CancellationToken.None);
            await file.FlushToDiskAsync(CancellationToken.None);
        }

        Func<Task> truncate = async () => await fileSystem.TruncateAsync(path, 5, CancellationToken.None);

        await truncate.Should().ThrowAsync<ArgumentOutOfRangeException>();
        File.ReadAllBytes(path).Should().Equal(1, 2);
    }

    [Fact]
    public async Task TruncateAsync_OnAMissingFile_Throws()
    {
        var path = Path.Combine(_directory, "absent.bin");

        Func<Task> truncate = async () => await SystemStorageFileSystem.Instance.TruncateAsync(path, 0, CancellationToken.None);

        await truncate.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task DeleteAsync_RemovesTheFileAndToleratesAnAlreadyMissingOne()
    {
        var path = Path.Combine(_directory, "data.bin");
        var fileSystem = SystemStorageFileSystem.Instance;

        await using (var file = await fileSystem.CreateAsync(path, CancellationToken.None))
        {
            await file.WriteAsync(new byte[] { 1 }, CancellationToken.None);
        }

        await fileSystem.DeleteAsync(path, CancellationToken.None);
        File.Exists(path).Should().BeFalse();

        // Повторный старт после падения между решением об удалении и самим удалением не должен падать.
        Func<Task> deleteAgain = async () => await fileSystem.DeleteAsync(path, CancellationToken.None);

        await deleteAgain.Should().NotThrowAsync();
    }

    [Fact]
    public async Task OpenAppendAsync_ContinuesTheFileInsteadOfOverwritingIt()
    {
        // Продолжение сегмента (S1-T06) - единственная запись, которая не создаёт файл:
        // FileMode.Append обязан дописать в конец, а не начать файл заново.
        var path = Path.Combine(_directory, "data.bin");
        var fileSystem = SystemStorageFileSystem.Instance;

        await using (var file = await fileSystem.CreateAsync(path, CancellationToken.None))
        {
            await file.WriteAsync(new byte[] { 1, 2 }, CancellationToken.None);
            await file.FlushToDiskAsync(CancellationToken.None);
        }

        await using (var file = await fileSystem.OpenAppendAsync(path, CancellationToken.None))
        {
            await file.WriteAsync(new byte[] { 3, 4 }, CancellationToken.None);
            await file.FlushToDiskAsync(CancellationToken.None);
        }

        File.ReadAllBytes(path).Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public async Task OpenAppendAsync_TruncatesAndKeepsWritingAtTheNewEnd()
    {
        // Усечение через писатель (обрезка конфликтующего хвоста) не должно оставлять дыру
        // между новым концом файла и следующей записью.
        var path = Path.Combine(_directory, "data.bin");
        var fileSystem = SystemStorageFileSystem.Instance;

        await using (var file = await fileSystem.CreateAsync(path, CancellationToken.None))
        {
            await file.WriteAsync(new byte[] { 1, 2, 3, 4, 5 }, CancellationToken.None);
            await file.FlushToDiskAsync(CancellationToken.None);
        }

        await using (var file = await fileSystem.OpenAppendAsync(path, CancellationToken.None))
        {
            await file.TruncateAsync(3, CancellationToken.None);
            new FileInfo(path).Length.Should().Be(3, "усечение через открытый писатель видно сразу");

            await file.WriteAsync(new byte[] { 9 }, CancellationToken.None);
            await file.FlushToDiskAsync(CancellationToken.None);
        }

        File.ReadAllBytes(path).Should().Equal(1, 2, 3, 9);
    }

    [Fact]
    public async Task OpenAppendAsync_TruncateToALongerLength_Throws()
    {
        var path = Path.Combine(_directory, "data.bin");
        var fileSystem = SystemStorageFileSystem.Instance;

        await using (var file = await fileSystem.CreateAsync(path, CancellationToken.None))
        {
            await file.WriteAsync(new byte[] { 1, 2 }, CancellationToken.None);
        }

        var append = await fileSystem.OpenAppendAsync(path, CancellationToken.None);

        Func<Task> truncate = async () => await append.TruncateAsync(5, CancellationToken.None);

        await truncate.Should().ThrowAsync<ArgumentOutOfRangeException>();

        await append.DisposeAsync();
        File.ReadAllBytes(path).Should().Equal(1, 2);
    }

    [Fact]
    public async Task OpenAppendAsync_OnAMissingFile_Throws()
    {
        var path = Path.Combine(_directory, "absent.bin");

        Func<Task> open = async () => await SystemStorageFileSystem.Instance.OpenAppendAsync(path, CancellationToken.None);

        await open.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task ListFilesAsync_ReturnsFileNamesOnlyAndAnEmptyListForAMissingDirectory()
    {
        // Каталог журнала читается целиком (SegmentCatalog, S1-T06): имена без путей,
        // в порядке, на который можно положиться.
        var path = Path.Combine(_directory, "nested");
        var fileSystem = SystemStorageFileSystem.Instance;

        await using (var file = await fileSystem.CreateAsync(Path.Combine(path, "b.bin"), CancellationToken.None))
        {
            await file.WriteAsync(new byte[] { 1 }, CancellationToken.None);
        }

        await using (var file = await fileSystem.CreateAsync(Path.Combine(path, "a.bin"), CancellationToken.None))
        {
            await file.WriteAsync(new byte[] { 1 }, CancellationToken.None);
        }

        var names = await fileSystem.ListFilesAsync(path, CancellationToken.None);

        names.Should().Equal("a.bin", "b.bin");

        // Отсутствующий каталог - пустой журнал, а не исключение: он появится первым append.
        var missing = await fileSystem.ListFilesAsync(Path.Combine(_directory, "absent"), CancellationToken.None);

        missing.Should().BeEmpty();
    }

    [Fact]
    public async Task SegmentRecovery_OnRealFileSystem_TruncatesTheTornTailOnDisk()
    {
        var options = new SegmentWriterOptions
        {
            Directory = _directory,
            MaxSegmentSize = LogSegmentFormat.MaxSegmentSize,
        };

        await using (var writer = new SegmentWriter(
            SystemStorageFileSystem.Instance,
            new TestClock(startMs: LogTestData.MonotonicStartMs, unixTimeMs: LogTestData.NowMs),
            options))
        {
            await writer.AppendAsync(
                [LogTestData.Command(1), LogTestData.Command(2), LogTestData.Command(3)],
                CancellationToken.None);
        }

        var segmentPath = Path.Combine(_directory, LogSegmentFormat.GetFileName(new LogIndex(1)));
        var thirdFrameOffset = LogSegmentFormat.HeaderSize + (2 * LogFrameCodec.GetFrameSize(4));

        // Обрыв ровно так, как его оставляет упавший процесс: файл оборван внутри третьего фрейма.
        using (var stream = new FileStream(segmentPath, FileMode.Open, FileAccess.Write))
        {
            stream.SetLength(thirdFrameOffset + 10);
        }

        var recovery = await SegmentRecovery.RecoverAsync(
            SystemStorageFileSystem.Instance,
            segmentPath,
            SegmentKind.Last,
            CancellationToken.None);

        recovery.Status.Should().Be(SegmentRecoveryStatus.TruncatedTail);
        recovery.DroppedBytes.Should().Be(10);
        new FileInfo(segmentPath).Length.Should().Be(thirdFrameOffset);

        var open = await SegmentReader.OpenAsync(SystemStorageFileSystem.Instance, segmentPath, CancellationToken.None);
        open.IsSuccess.Should().BeTrue(open.Reason);
        await using var reader = open.Reader!;
        var scan = await reader.ScanAsync(CancellationToken.None);

        scan.IsComplete.Should().BeTrue(scan.Reason);
        scan.FrameCount.Should().Be(2);
        scan.LastIndex!.Value.Value.Should().Be(2);
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
            header.Header!.CreatedAtUnixMs.Should().Be(LogTestData.NowMs);

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
