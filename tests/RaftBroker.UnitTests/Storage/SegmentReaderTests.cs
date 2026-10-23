using System.Buffers.Binary;
using RaftBroker.Core.Primitives;
using RaftBroker.Storage.Integrity;
using RaftBroker.Storage.Log;
using RaftBroker.TestKit.IO;

namespace RaftBroker.UnitTests.Storage;

/// <summary>
/// S1-T04: чтение одного сегмента - заголовок, проход вперёд, итерация назад, доступ по индексу.
/// </summary>
/// <remarks>
/// <para>
/// Сегменты пишутся настоящим писателем S1-T03, а не собираются руками: если writer и reader
/// разъедутся в раскладке, это обязано упасть здесь, а не в S1-T05. Порча и обрывы, которых
/// писатель получить не может, задаются через <see cref="FakeStorageFileSystem.Seed"/>.
/// </para>
/// <para>
/// Проверяется главное свойство задачи: читатель не решает "усечь или отказать" сам. Ни один
/// тест порчи не ожидает от него брошенного исключения - он обязан сообщить, до какого смещения
/// файл валиден (<see cref="SegmentScanResult.ValidEndOffset"/>), а судьбу хвоста определяет
/// восстановление S1-T05.
/// </para>
/// </remarks>
public sealed class SegmentReaderTests
{
    /// <summary>Смещение второго фрейма: заголовок 30 байт плюс фрейм с payload 4 байта.</summary>
    private static int SecondFrameOffset => LogSegmentFormat.HeaderSize + LogFrameCodec.GetFrameSize(4);

    /// <summary>Смещение третьего фрейма: он нужен там, где порча должна оставить два валидных фрейма.</summary>
    private static int ThirdFrameOffset => SecondFrameOffset + LogFrameCodec.GetFrameSize(4);

    [Fact]
    public async Task Open_WhenHeaderReadIsCancelled_ClosesTheFile()
    {
        // Отмена внутри чтения заголовка не должна оставлять открытый дескриптор: на Windows
        // занятый файл не даёт обрезке и компакции удалить сегмент.
        var fileSystem = new FakeStorageFileSystem();
        var path = await LogTestData.WriteSegmentAsync(fileSystem, LogTestData.Command(1));

        fileSystem.FailureInjector = operation => operation.Kind == StorageOperationKind.Read
            ? new OperationCanceledException("чтение заголовка отменено")
            : null;

        Func<Task> open = async () => await SegmentReader.OpenAsync(fileSystem, path, CancellationToken.None);

        await open.Should().ThrowAsync<OperationCanceledException>();
        fileSystem.OpenReadFileCount.Should().Be(0);
    }

    [Fact]
    public async Task Open_ReadsHeaderAndExposesPathAndLength()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = await LogTestData.WriteSegmentAsync(fileSystem, LogTestData.Command(1));

        var result = await SegmentReader.OpenAsync(fileSystem, path, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason);
        result.Reader!.Path.Should().Be(path);
        result.Reader.Header.BaseIndex.Value.Should().Be(1);
        result.Reader.Header.CreatedAtUnixMs.Should().Be(LogTestData.NowMs);
        result.Reader.Length.Should().Be(fileSystem.ReadAllBytes(path).Length);

        await result.Reader.DisposeAsync();
    }

    [Fact]
    public async Task Open_FileShorterThanHeader_ReportsTruncatedAndClosesTheHandle()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = LogTestData.SegmentPath(1);
        fileSystem.Seed(path, [0x52, 0x42]);

        var result = await SegmentReader.OpenAsync(fileSystem, path, CancellationToken.None);

        result.Status.Should().Be(LogReadStatus.Truncated);
        result.Reader.Should().BeNull();
        result.Reason.Should().Contain("заголовок занимает");
        fileSystem.OpenReadFileCount.Should().Be(0, "при отказе открытия дескриптор обязан быть закрыт");
    }

    [Fact]
    public async Task Open_HeaderWithWrongMagic_ReportsCorrupt()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = await LogTestData.WriteSegmentAsync(fileSystem, LogTestData.Command(1));

        var bytes = fileSystem.ReadAllBytes(path);
        bytes[0] = (byte)'X';
        fileSystem.Seed(path, bytes);

        var result = await SegmentReader.OpenAsync(fileSystem, path, CancellationToken.None);

        result.Status.Should().Be(LogReadStatus.Corrupt);
        result.Reason.Should().Contain("магия");
        fileSystem.OpenReadFileCount.Should().Be(0);
    }

    [Fact]
    public async Task Open_UnknownFormatVersion_ReportsCorruptWithTheVersion()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = await LogTestData.WriteSegmentAsync(fileSystem, LogTestData.Command(1));

        var bytes = fileSystem.ReadAllBytes(path);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(LogSegmentFormat.MagicSize), 99);
        fileSystem.Seed(path, bytes);

        var result = await SegmentReader.OpenAsync(fileSystem, path, CancellationToken.None);

        result.Status.Should().Be(LogReadStatus.Corrupt);
        result.Reason.Should().Contain("99");
        fileSystem.OpenReadFileCount.Should().Be(0);
    }

    [Fact]
    public async Task Open_BaseIndexDifferentFromFileName_ReportsCorrupt()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = await LogTestData.WriteSegmentAsync(fileSystem, LogTestData.Command(1));

        // Тот же сегмент под именем с другим baseIndex: §9.1 правило 3 требует совпадения.
        var renamed = LogTestData.SegmentPath(2);
        fileSystem.Seed(renamed, fileSystem.ReadAllBytes(path));

        var result = await SegmentReader.OpenAsync(fileSystem, renamed, CancellationToken.None);

        result.Status.Should().Be(LogReadStatus.Corrupt);
        result.Reason.Should().Contain("§9.1");
        fileSystem.OpenReadFileCount.Should().Be(0);
    }

    [Fact]
    public async Task Open_FileNameWithoutSegmentShape_ReportsCorrupt()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = await LogTestData.WriteSegmentAsync(fileSystem, LogTestData.Command(1));

        var foreignPath = Path.Combine(LogTestData.Directory, "notes.txt");
        fileSystem.Seed(foreignPath, fileSystem.ReadAllBytes(path));

        var result = await SegmentReader.OpenAsync(fileSystem, foreignPath, CancellationToken.None);

        result.Status.Should().Be(LogReadStatus.Corrupt);
        result.Reason.Should().Contain(".seg");
    }

    [Fact]
    public async Task Open_FileThatWasNeverCreated_ThrowsFileNotFound()
    {
        var fileSystem = new FakeStorageFileSystem();

        Func<Task> open = async () => await SegmentReader.OpenAsync(
            fileSystem,
            LogTestData.SegmentPath(1),
            CancellationToken.None);

        await open.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task Scan_EmptySegment_IsCompleteWithoutRecords()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = LogTestData.SegmentPath(1);
        fileSystem.Seed(path, LogTestData.HeaderOnlySegment(1));

        var result = await SegmentReader.OpenAsync(fileSystem, path, CancellationToken.None);
        await using var reader = result.Reader!;
        var scan = await reader.ScanAsync(CancellationToken.None);

        scan.Status.Should().Be(SegmentScanStatus.Complete);
        scan.Reason.Should().BeEmpty();
        scan.FrameCount.Should().Be(0);
        scan.LastIndex.Should().BeNull();
        scan.LastTerm.Should().BeNull();
        scan.ValidEndOffset.Should().Be(LogSegmentFormat.HeaderSize);

        var forward = new List<LogRecord>();
        await foreach (var record in reader.ReadForwardAsync(CancellationToken.None))
        {
            forward.Add(record);
        }

        forward.Should().BeEmpty();
    }

    [Fact]
    public async Task Scan_SingleFrameEndingExactlyAtEndOfFile_IsComplete()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = await LogTestData.WriteSegmentAsync(fileSystem, LogTestData.Command(1));

        var result = await SegmentReader.OpenAsync(fileSystem, path, CancellationToken.None);
        await using var reader = result.Reader!;
        var scan = await reader.ScanAsync(CancellationToken.None);

        // "Запись точно в конец": последний фрейм кончается на последнем байте файла,
        // и остаток, который можно было бы принять за обрыв, не появляется.
        scan.Status.Should().Be(SegmentScanStatus.Complete);
        scan.ValidEndOffset.Should().Be(reader.Length);
        scan.FrameCount.Should().Be(1);
        scan.LastIndex!.Value.Value.Should().Be(1);
        scan.LastTerm!.Value.Value.Should().Be(1);
    }

    [Fact]
    public async Task Scan_MultipleFrames_ReportsCountLastIndexAndLastTerm()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = await LogTestData.WriteSegmentAsync(
            fileSystem,
            LogTestData.Command(1, term: 1),
            LogTestData.Noop(2, term: 2),
            LogTestData.Command(3, term: 2));

        var result = await SegmentReader.OpenAsync(fileSystem, path, CancellationToken.None);
        await using var reader = result.Reader!;
        var scan = await reader.ScanAsync(CancellationToken.None);

        scan.Status.Should().Be(SegmentScanStatus.Complete);
        scan.FrameCount.Should().Be(3);
        scan.LastIndex!.Value.Value.Should().Be(3);
        scan.LastTerm!.Value.Value.Should().Be(2);
        scan.ValidEndOffset.Should().Be(reader.Length);
        reader.LastIndex!.Value.Value.Should().Be(3);
        reader.LastTerm!.Value.Value.Should().Be(2);
        reader.FrameCount.Should().Be(3);
    }

    [Fact]
    public async Task Scan_SegmentStartingAfterOne_KeepsAbsoluteIndexes()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = await LogTestData.WriteSegmentAsync(
            fileSystem,
            LogTestData.Command(5),
            LogTestData.Command(6));

        var result = await SegmentReader.OpenAsync(fileSystem, path, CancellationToken.None);
        await using var reader = result.Reader!;
        var scan = await reader.ScanAsync(CancellationToken.None);

        scan.LastIndex!.Value.Value.Should().Be(6);
        (await reader.ReadAtAsync(new LogIndex(5), CancellationToken.None)).Should().NotBeNull();
        (await reader.ReadAtAsync(new LogIndex(6), CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task Scan_TornTail_StopsAtTheFirstIncompleteFrame()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = await LogTestData.WriteSegmentAsync(
            fileSystem,
            LogTestData.Command(1),
            LogTestData.Command(2),
            LogTestData.Command(3));

        var bytes = fileSystem.ReadAllBytes(path);
        fileSystem.Seed(path, bytes.AsSpan(0, ThirdFrameOffset + 10));

        var result = await SegmentReader.OpenAsync(fileSystem, path, CancellationToken.None);
        await using var reader = result.Reader!;
        var scan = await reader.ScanAsync(CancellationToken.None);

        // Обрыв пришёлся на середину третьего фрейма: валидны первые два.
        scan.Status.Should().Be(SegmentScanStatus.TailProblem);
        scan.ValidEndOffset.Should().Be(ThirdFrameOffset);
        scan.FrameCount.Should().Be(2);
        scan.LastIndex!.Value.Value.Should().Be(2);
        scan.Reason.Should().Contain("до конца файла");
    }

    [Fact]
    public async Task Scan_DamagedCrc_ReportsTailProblemInsteadOfSilentTruncation()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = await LogTestData.WriteSegmentAsync(
            fileSystem,
            LogTestData.Command(1),
            LogTestData.Command(2),
            LogTestData.Command(3));

        // Порча внутри payload последнего фрейма: байты есть все, CRC не сходится.
        var bytes = fileSystem.ReadAllBytes(path);
        bytes[ThirdFrameOffset + 10] ^= 0xFF;
        fileSystem.Seed(path, bytes);

        var result = await SegmentReader.OpenAsync(fileSystem, path, CancellationToken.None);
        await using var reader = result.Reader!;
        var scan = await reader.ScanAsync(CancellationToken.None);

        // Порча CRC не превращается в молчаливое усечение: читатель говорит, что именно не сошлось.
        scan.Status.Should().Be(SegmentScanStatus.TailProblem);
        scan.ValidEndOffset.Should().Be(ThirdFrameOffset);
        scan.FrameCount.Should().Be(2);
        scan.Reason.Should().Contain("CRC32C");
    }

    [Fact]
    public async Task Scan_IndexGap_ReportsTailProblem()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = await LogTestData.WriteSegmentAsync(
            fileSystem,
            LogTestData.Command(1),
            LogTestData.Command(2),
            LogTestData.Command(3));

        var bytes = fileSystem.ReadAllBytes(path);
        PatchIndex(bytes, ThirdFrameOffset, 7);
        fileSystem.Seed(path, bytes);

        var result = await SegmentReader.OpenAsync(fileSystem, path, CancellationToken.None);
        await using var reader = result.Reader!;
        var scan = await reader.ScanAsync(CancellationToken.None);

        scan.Status.Should().Be(SegmentScanStatus.TailProblem);
        scan.ValidEndOffset.Should().Be(ThirdFrameOffset);
        scan.FrameCount.Should().Be(2);
        scan.Reason.Should().Contain("ожидался индекс 3");
    }

    [Fact]
    public async Task Scan_DecreasingTerm_ReportsTailProblem()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = await LogTestData.WriteSegmentAsync(
            fileSystem,
            LogTestData.Command(1, term: 5),
            LogTestData.Command(2, term: 5),
            LogTestData.Command(3, term: 5));

        // Писатель сам не даёт записать убывающий терм, поэтому нарушение создаётся порчей байтов.
        var bytes = fileSystem.ReadAllBytes(path);
        PatchTerm(bytes, ThirdFrameOffset, 4);
        fileSystem.Seed(path, bytes);

        var result = await SegmentReader.OpenAsync(fileSystem, path, CancellationToken.None);
        await using var reader = result.Reader!;
        var scan = await reader.ScanAsync(CancellationToken.None);

        scan.Status.Should().Be(SegmentScanStatus.TailProblem);
        scan.ValidEndOffset.Should().Be(ThirdFrameOffset);
        scan.Reason.Should().Contain("терм");
    }

    [Fact]
    public async Task Scan_UnknownRecordType_ReportsTailProblem()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = await LogTestData.WriteSegmentAsync(
            fileSystem,
            LogTestData.Command(1),
            LogTestData.Command(2));

        var bytes = fileSystem.ReadAllBytes(path);
        bytes[SecondFrameOffset + 4] = 0x7F;
        FixFrameCrc(bytes, SecondFrameOffset);
        fileSystem.Seed(path, bytes);

        var result = await SegmentReader.OpenAsync(fileSystem, path, CancellationToken.None);
        await using var reader = result.Reader!;
        var scan = await reader.ScanAsync(CancellationToken.None);

        scan.Status.Should().Be(SegmentScanStatus.TailProblem);
        scan.FrameCount.Should().Be(1);
        scan.Reason.Should().Contain("recordType");
    }

    [Fact]
    public async Task Scan_SecondCall_DoesNotReadTheFileAgain()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = await LogTestData.WriteSegmentAsync(
            fileSystem,
            LogTestData.Command(1),
            LogTestData.Command(2));

        var result = await SegmentReader.OpenAsync(fileSystem, path, CancellationToken.None);
        await using var reader = result.Reader!;

        var first = await reader.ScanAsync(CancellationToken.None);
        var readsAfterFirst = fileSystem.ReadCount;
        var second = await reader.ScanAsync(CancellationToken.None);

        second.Should().Be(first);
        fileSystem.ReadCount.Should().Be(readsAfterFirst, "повторное сканирование обязано использовать кэш");
    }

    [Fact]
    public async Task Read_WithShortReads_ReturnsTheSameRecords()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = await LogTestData.WriteSegmentAsync(
            fileSystem,
            LogTestData.Command(1),
            LogTestData.Command(2),
            LogTestData.Command(3));

        fileSystem.MaxReadSize = 3;

        var result = await SegmentReader.OpenAsync(fileSystem, path, CancellationToken.None);
        await using var reader = result.Reader!;
        var scan = await reader.ScanAsync(CancellationToken.None);

        // Настоящий поток вправе вернуть меньше запрошенного: цикл дочитывания обязан собрать фрейм.
        scan.Status.Should().Be(SegmentScanStatus.Complete);
        scan.FrameCount.Should().Be(3);
        (await ReadAllForwardAsync(reader)).Should().Equal(
            LogTestData.Command(1),
            LogTestData.Command(2),
            LogTestData.Command(3));
    }

    [Fact]
    public async Task ReadForward_ReturnsRecordsInAscendingOrder()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = await LogTestData.WriteSegmentAsync(
            fileSystem,
            LogTestData.Command(1, term: 1),
            LogTestData.Noop(2, term: 1),
            LogTestData.Command(3, term: 2, payloadLength: 16));

        var result = await SegmentReader.OpenAsync(fileSystem, path, CancellationToken.None);
        await using var reader = result.Reader!;
        await reader.ScanAsync(CancellationToken.None);

        (await ReadAllForwardAsync(reader)).Should().Equal(
            LogTestData.Command(1, term: 1),
            LogTestData.Noop(2, term: 1),
            LogTestData.Command(3, term: 2, payloadLength: 16));
    }

    [Fact]
    public async Task ReadBackward_ReturnsRecordsInDescendingOrder()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = await LogTestData.WriteSegmentAsync(
            fileSystem,
            LogTestData.Command(1),
            LogTestData.Command(2),
            LogTestData.Command(3));

        var result = await SegmentReader.OpenAsync(fileSystem, path, CancellationToken.None);
        await using var reader = result.Reader!;
        await reader.ScanAsync(CancellationToken.None);

        var backward = new List<LogRecord>();

        await foreach (var record in reader.ReadBackwardAsync(CancellationToken.None))
        {
            backward.Add(record);
        }

        backward.Should().Equal(
            LogTestData.Command(3),
            LogTestData.Command(2),
            LogTestData.Command(1));
    }

    [Fact]
    public async Task ReadForward_OfTornSegment_ReturnsOnlyTheValidPrefix()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = await LogTestData.WriteSegmentAsync(
            fileSystem,
            LogTestData.Command(1),
            LogTestData.Command(2),
            LogTestData.Command(3));

        var bytes = fileSystem.ReadAllBytes(path);
        fileSystem.Seed(path, bytes.AsSpan(0, bytes.Length - 4));

        var result = await SegmentReader.OpenAsync(fileSystem, path, CancellationToken.None);
        await using var reader = result.Reader!;
        await reader.ScanAsync(CancellationToken.None);

        (await ReadAllForwardAsync(reader)).Should().Equal(
            LogTestData.Command(1),
            LogTestData.Command(2));
    }

    [Fact]
    public async Task ReadAt_ReturnsTheRecordOfThatIndex()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = await LogTestData.WriteSegmentAsync(
            fileSystem,
            LogTestData.Command(1),
            LogTestData.Command(2),
            LogTestData.Command(3));

        var result = await SegmentReader.OpenAsync(fileSystem, path, CancellationToken.None);
        await using var reader = result.Reader!;
        await reader.ScanAsync(CancellationToken.None);

        (await reader.ReadAtAsync(new LogIndex(2), CancellationToken.None))
            .Should().Be(LogTestData.Command(2));

        // Первая и последняя записи сегмента - границы, на которых чаще всего ошибаются.
        (await reader.ReadAtAsync(new LogIndex(1), CancellationToken.None))
            .Should().Be(LogTestData.Command(1));
        (await reader.ReadAtAsync(new LogIndex(3), CancellationToken.None))
            .Should().Be(LogTestData.Command(3));
    }

    [Fact]
    public async Task ReadAt_IndexOutsideSegment_ReturnsNull()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = await LogTestData.WriteSegmentAsync(fileSystem, LogTestData.Command(5));

        var result = await SegmentReader.OpenAsync(fileSystem, path, CancellationToken.None);
        await using var reader = result.Reader!;
        await reader.ScanAsync(CancellationToken.None);

        (await reader.ReadAtAsync(new LogIndex(4), CancellationToken.None)).Should().BeNull();
        (await reader.ReadAtAsync(new LogIndex(6), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Read_WithoutScan_Throws()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = await LogTestData.WriteSegmentAsync(fileSystem, LogTestData.Command(1));

        var result = await SegmentReader.OpenAsync(fileSystem, path, CancellationToken.None);
        await using var reader = result.Reader!;

        Func<Task> readAt = async () => await reader.ReadAtAsync(new LogIndex(1), CancellationToken.None);
        Func<Task> enumerate = async () =>
        {
            await foreach (var _ in reader.ReadForwardAsync(CancellationToken.None))
            {
            }
        };
        Action frameCount = () => _ = reader.FrameCount;

        await readAt.Should().ThrowAsync<InvalidOperationException>();
        await enumerate.Should().ThrowAsync<InvalidOperationException>();
        frameCount.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task ReadFrameAt_ReadsTheFrameAtTheOffsetWithoutScanning()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = await LogTestData.WriteSegmentAsync(
            fileSystem,
            LogTestData.Command(1),
            LogTestData.Command(2));

        var result = await SegmentReader.OpenAsync(fileSystem, path, CancellationToken.None);
        await using var reader = result.Reader!;

        var first = await reader.ReadFrameAtAsync(LogSegmentFormat.HeaderSize, CancellationToken.None);
        var second = await reader.ReadFrameAtAsync(SecondFrameOffset, CancellationToken.None);

        first.IsSuccess.Should().BeTrue(first.Reason);
        first.Record.Should().Be(LogTestData.Command(1));
        first.BytesConsumed.Should().Be(LogFrameCodec.GetFrameSize(4));
        second.Record.Should().Be(LogTestData.Command(2));
    }

    [Fact]
    public async Task ReadFrameAt_PastTheEndOfFile_ReportsTruncated()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = await LogTestData.WriteSegmentAsync(fileSystem, LogTestData.Command(1));

        var result = await SegmentReader.OpenAsync(fileSystem, path, CancellationToken.None);
        await using var reader = result.Reader!;

        var frame = await reader.ReadFrameAtAsync(reader.Length, CancellationToken.None);

        frame.Status.Should().Be(LogReadStatus.Truncated);
        frame.Record.Should().BeNull();
    }

    [Fact]
    public async Task ReadFrameLength_ReturnsNullWhenThereIsNoReadableFrameLength()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = await LogTestData.WriteSegmentAsync(fileSystem, LogTestData.Command(1));

        var result = await SegmentReader.OpenAsync(fileSystem, path, CancellationToken.None);
        await using var reader = result.Reader!;

        var length = await reader.ReadFrameLengthAsync(LogSegmentFormat.HeaderSize, CancellationToken.None);

        length.Should().Be(LogSegmentFormat.MinFrameLength + 4);
        (await reader.ReadFrameLengthAsync(reader.Length - 2, CancellationToken.None)).Should().BeNull();

        // Проба продолжения (§9.5 шаг 5): значение вне границ означает, что за повреждением
        // не может быть валидного фрейма, и длина не нужна.
        var bytes = fileSystem.ReadAllBytes(path);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(LogSegmentFormat.HeaderSize), 0);
        fileSystem.Seed(path, bytes);

        (await reader.ReadFrameLengthAsync(LogSegmentFormat.HeaderSize, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Dispose_ClosesTheReadHandle()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = await LogTestData.WriteSegmentAsync(fileSystem, LogTestData.Command(1));

        var result = await SegmentReader.OpenAsync(fileSystem, path, CancellationToken.None);
        var reader = result.Reader!;
        fileSystem.OpenReadFileCount.Should().Be(1);

        await reader.DisposeAsync();

        fileSystem.OpenReadFileCount.Should().Be(0);

        Func<Task> scan = async () => await reader.ScanAsync(CancellationToken.None);
        await scan.Should().ThrowAsync<ObjectDisposedException>();

        // Повторный Dispose не должен ни падать, ни уводить счётчик в минус.
        await reader.DisposeAsync();
        fileSystem.OpenReadFileCount.Should().Be(0);
    }

    private static async Task<List<LogRecord>> ReadAllForwardAsync(SegmentReader reader)
    {
        var records = new List<LogRecord>();

        await foreach (var record in reader.ReadForwardAsync(CancellationToken.None))
        {
            records.Add(record);
        }

        return records;
    }

    /// <summary>Меняет индекс фрейма и пересчитывает его CRC: иначе порча индекса выглядела бы как порча CRC.</summary>
    private static void PatchIndex(byte[] bytes, int frameOffset, long index)
    {
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(frameOffset + 13), index);
        FixFrameCrc(bytes, frameOffset);
    }

    /// <summary>Меняет терм фрейма и пересчитывает его CRC.</summary>
    private static void PatchTerm(byte[] bytes, int frameOffset, long term)
    {
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(frameOffset + 5), term);
        FixFrameCrc(bytes, frameOffset);
    }

    private static void FixFrameCrc(byte[] bytes, int frameOffset)
    {
        var frameLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(frameOffset));
        var crc = Crc32C.Compute(bytes.AsSpan(frameOffset + sizeof(uint), frameLength));
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(frameOffset + sizeof(uint) + frameLength),
            crc);
    }
}
