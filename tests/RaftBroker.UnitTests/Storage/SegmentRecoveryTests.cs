using System.Buffers.Binary;
using RaftBroker.Core.Primitives;
using RaftBroker.Storage.Log;
using RaftBroker.TestKit.IO;

namespace RaftBroker.UnitTests.Storage;

/// <summary>
/// Восстановление сегмента (<c>S1-T05</c>): оборванный хвост последнего сегмента усекается до
/// последней валидной записи, порча в середине и любое повреждение запечатанного сегмента
/// останавливают старт, оборванное создание удаляется.
/// </summary>
/// <remarks>
/// Обрыв здесь получается усечением файла в дубле, поэтому проверяются <b>все</b> точки обрыва,
/// а не выборочные. Crash-тест с настоящим убийством процесса живёт отдельно, в
/// <c>RaftBroker.IntegrationTests/Storage/LogRecoveryCrashTests.cs</c>: он ловит расхождение
/// модели «байты в дубле» с реальным <c>fsync</c>, но побайтовым быть не может.
/// </remarks>
public sealed class SegmentRecoveryTests
{
    private const long First = 1;
    private const long Second = 2;
    private const long Third = 3;
    private const long Fourth = 4;
    private const int PayloadLength = 4;

    /// <summary>Размер одного фрейма команды с payload по умолчанию.</summary>
    private static int FrameSize => LogFrameCodec.GetFrameSize(PayloadLength);

    [Fact]
    public async Task IntactLastSegment_IsReportedIntactAndTheDiskIsNotTouched()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = await LogTestData.WriteSegmentAsync(
            fileSystem,
            LogTestData.Command(First),
            LogTestData.Command(Second),
            LogTestData.Noop(Third, term: 2));

        var before = fileSystem.ReadAllBytes(path);

        var result = await RecoverAsync(fileSystem, path, SegmentKind.Last);

        result.Status.Should().Be(SegmentRecoveryStatus.Intact);
        result.IsRefused.Should().BeFalse();
        result.ChangedFile.Should().BeFalse();
        result.Path.Should().Be(path);
        result.Reason.Should().BeEmpty();
        result.FrameCount.Should().Be(3);
        result.LastIndex!.Value.Value.Should().Be(Third);
        result.LastTerm!.Value.Value.Should().Be(2);
        result.ValidEndOffset.Should().Be(before.Length);
        result.DroppedBytes.Should().Be(0);

        NoDestructiveOperations(fileSystem);
        fileSystem.ReadAllBytes(path).Should().Equal(before);
    }

    [Fact]
    public async Task IntactSealedSegment_IsReportedIntact()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = await LogTestData.WriteSegmentAsync(fileSystem, LogTestData.Command(First), LogTestData.Command(Second));

        var result = await RecoverAsync(fileSystem, path, SegmentKind.Sealed);

        // Запечатанный сегмент обязан кончаться ровно на границе фрейма (§9.5 шаг 6) - здесь это так.
        result.Status.Should().Be(SegmentRecoveryStatus.Intact);
        result.FrameCount.Should().Be(2);
        NoDestructiveOperations(fileSystem);
    }

    [Fact]
    public async Task EmptySegment_IsIntactForBothKinds()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = LogTestData.SegmentPath(First);
        fileSystem.Seed(path, LogTestData.HeaderOnlySegment(First));

        var asLast = await RecoverAsync(fileSystem, path, SegmentKind.Last);
        var asSealed = await RecoverAsync(fileSystem, path, SegmentKind.Sealed);

        foreach (var result in new[] { asLast, asSealed })
        {
            result.Status.Should().Be(SegmentRecoveryStatus.Intact);
            result.FrameCount.Should().Be(0);
            result.LastIndex.Should().BeNull();
            result.LastTerm.Should().BeNull();
            result.ValidEndOffset.Should().Be(LogSegmentFormat.HeaderSize);
        }

        NoDestructiveOperations(fileSystem);
    }

    [Fact]
    public async Task TruncationAtEveryByteOfTheSegment_IsAlwaysIntactOrTruncatedTail()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = await LogTestData.WriteSegmentAsync(
            fileSystem,
            LogTestData.Command(First),
            LogTestData.Command(Second, term: 2),
            LogTestData.Command(Third, term: 3),
            LogTestData.Noop(Fourth, term: 3));

        var whole = fileSystem.ReadAllBytes(path);
        var offsets = LogTestData.FrameOffsets(whole);
        var expectedRecords = new List<LogRecord>
        {
            LogTestData.Command(First),
            LogTestData.Command(Second, term: 2),
            LogTestData.Command(Third, term: 3),
            LogTestData.Noop(Fourth, term: 3),
        };

        // Обрыв на любом байте: валидный префикс либо доходит до конца файла, либо кончается ровно
        // на границе фрейма. Ни одна точка обрыва не выглядит порчей - это и есть §9.2.
        for (var cut = LogSegmentFormat.HeaderSize; cut < whole.Length; cut++)
        {
            fileSystem.Seed(path, whole.AsSpan(0, cut));

            var result = await RecoverAsync(fileSystem, path, SegmentKind.Last);

            result.Status.Should().BeOneOf(
                [SegmentRecoveryStatus.Intact, SegmentRecoveryStatus.TruncatedTail],
                "обрыв на байте {0} из {1} - это torn write (§9.2), а не порча",
                cut,
                whole.Length);

            var expectedFrames = offsets.Count(offset => offset + FrameSize <= cut);

            result.FrameCount.Should().Be(expectedFrames);
            result.ValidEndOffset.Should().Be(
                expectedFrames == 0 ? LogSegmentFormat.HeaderSize : offsets[expectedFrames - 1] + FrameSize);

            if (result.Status == SegmentRecoveryStatus.TruncatedTail)
            {
                result.DroppedBytes.Should().Be(cut - result.ValidEndOffset);
                result.ChangedFile.Should().BeTrue();
            }
            else
            {
                result.DroppedBytes.Should().Be(0);
                result.ChangedFile.Should().BeFalse();
            }

            // Диск после восстановления читается тем же кодеком и содержит ровно тот префикс.
            fileSystem.ReadAllBytes(path).Should().Equal(whole[..(int)result.ValidEndOffset]);
            LogTestData.ReadSegment(fileSystem, First).Should().Equal(expectedRecords.Take(expectedFrames));
        }
    }

    [Fact]
    public async Task TornTail_WithOnlyPartOfTheFrameLengthField_IsTruncated()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = await LogTestData.WriteSegmentAsync(fileSystem, LogTestData.Command(First), LogTestData.Command(Second));
        var whole = fileSystem.ReadAllBytes(path);
        var secondFrameOffset = LogTestData.FrameOffsets(whole)[1];

        fileSystem.Seed(path, whole.AsSpan(0, secondFrameOffset + 3));

        var result = await RecoverAsync(fileSystem, path, SegmentKind.Last);

        result.Status.Should().Be(SegmentRecoveryStatus.TruncatedTail);
        result.ValidEndOffset.Should().Be(secondFrameOffset);
        result.Reason.Should().Contain("frameLength");
        result.DroppedBytes.Should().Be(3);
    }

    [Fact]
    public async Task TornTail_DamagedCrcInTheTailItself_IsTruncated()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = await LogTestData.WriteSegmentAsync(fileSystem, LogTestData.Command(First), LogTestData.Command(Second));
        var bytes = fileSystem.ReadAllBytes(path);
        var secondFrameOffset = LogTestData.FrameOffsets(bytes)[1];

        // Порча внутри последнего фрейма, за которым в файле ничего нет: по §9.2 это torn write.
        bytes[secondFrameOffset + 5] ^= 0xFF;
        fileSystem.Seed(path, bytes);

        var result = await RecoverAsync(fileSystem, path, SegmentKind.Last);

        result.Status.Should().Be(SegmentRecoveryStatus.TruncatedTail);
        result.ValidEndOffset.Should().Be(secondFrameOffset);
        result.FrameCount.Should().Be(1);
        result.Reason.Should().Contain("CRC32C");
    }

    [Fact]
    public async Task TornTail_UnknownRecordTypeInTheTail_IsTruncated()
    {
        var (fileSystem, path, bytes, lastFrameOffset) = await TwoFrameSegmentAsync();
        bytes[lastFrameOffset + sizeof(uint)] = 0x7F;
        LogTestData.FixFrameCrc(bytes, lastFrameOffset);
        fileSystem.Seed(path, bytes);

        var result = await RecoverAsync(fileSystem, path, SegmentKind.Last);

        result.Status.Should().Be(SegmentRecoveryStatus.TruncatedTail);
        result.ValidEndOffset.Should().Be(lastFrameOffset);
        result.Reason.Should().Contain("recordType");
    }

    [Fact]
    public async Task TornTail_IndexGapInTheTail_IsTruncated()
    {
        var (fileSystem, path, bytes, lastFrameOffset) = await TwoFrameSegmentAsync();

        // Индекс последней записи перескочил: §4 нарушен, но продолжения за фреймом нет.
        WriteInt64(bytes, IndexOffset(lastFrameOffset), 42);
        LogTestData.FixFrameCrc(bytes, lastFrameOffset);
        fileSystem.Seed(path, bytes);

        var result = await RecoverAsync(fileSystem, path, SegmentKind.Last);

        result.Status.Should().Be(SegmentRecoveryStatus.TruncatedTail);
        result.Reason.Should().Contain("ожидался индекс");
    }

    [Fact]
    public async Task TornTail_DecreasingTermInTheTail_IsTruncated()
    {
        var (fileSystem, path, bytes, lastFrameOffset) = await TwoFrameSegmentAsync(term: 5);
        WriteInt64(bytes, TermOffset(lastFrameOffset), 0);
        LogTestData.FixFrameCrc(bytes, lastFrameOffset);
        fileSystem.Seed(path, bytes);

        var result = await RecoverAsync(fileSystem, path, SegmentKind.Last);

        result.Status.Should().Be(SegmentRecoveryStatus.TruncatedTail);
        result.Reason.Should().Contain("меньше терма");
    }

    [Fact]
    public async Task TornTail_FrameLengthOutOfBounds_MakesEverythingAfterItTorn()
    {
        var (fileSystem, path, bytes, lastFrameOffset) = await TwoFrameSegmentAsync();

        // §9.5 шаг 5: если L вне границ, продолжения за повреждением быть не может, и хвост
        // отбрасывается. §9.6 называет это остаточным риском, а не поводом отказывать всем.
        WriteUInt32(bytes, lastFrameOffset, uint.MaxValue);
        fileSystem.Seed(path, bytes);

        var result = await RecoverAsync(fileSystem, path, SegmentKind.Last);

        result.Status.Should().Be(SegmentRecoveryStatus.TruncatedTail);
        result.ValidEndOffset.Should().Be(lastFrameOffset);
        result.Reason.Should().Contain("frameLength");
    }

    [Fact]
    public async Task CorruptionInTheMiddle_DamagedCrc_IsRefusedAndTheFileIsKept()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = await LogTestData.WriteSegmentAsync(
            fileSystem,
            LogTestData.Command(First),
            LogTestData.Command(Second),
            LogTestData.Command(Third));

        var bytes = fileSystem.ReadAllBytes(path);
        var secondFrameOffset = LogTestData.FrameOffsets(bytes)[1];

        // Порча во втором фрейме, за которым идёт целый третий: это середина, а не хвост.
        bytes[secondFrameOffset + 5] ^= 0xFF;
        fileSystem.Seed(path, bytes);
        var damaged = fileSystem.ReadAllBytes(path);

        var result = await RecoverAsync(fileSystem, path, SegmentKind.Last);

        result.Status.Should().Be(SegmentRecoveryStatus.Refused);
        result.IsRefused.Should().BeTrue();
        result.ChangedFile.Should().BeFalse();
        result.Reason.Should().Contain("середин");
        result.Reason.Should().Contain("§9.5 шаг 5");
        result.ValidEndOffset.Should().Be(secondFrameOffset);
        result.FrameCount.Should().Be(1);
        result.DroppedBytes.Should().Be(0);

        // Автоматическое усечение здесь запрещено: иначе молча теряется закоммиченная история (§9.4).
        NoDestructiveOperations(fileSystem);
        fileSystem.ReadAllBytes(path).Should().Equal(damaged);
    }

    [Fact]
    public async Task CorruptionInTheMiddle_IndexGap_IsRefused()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = await LogTestData.WriteSegmentAsync(
            fileSystem,
            LogTestData.Command(First),
            LogTestData.Command(Second),
            LogTestData.Command(Third));

        var bytes = fileSystem.ReadAllBytes(path);
        var secondFrameOffset = LogTestData.FrameOffsets(bytes)[1];
        WriteInt64(bytes, IndexOffset(secondFrameOffset), 42);
        LogTestData.FixFrameCrc(bytes, secondFrameOffset);
        fileSystem.Seed(path, bytes);

        var result = await RecoverAsync(fileSystem, path, SegmentKind.Last);

        result.Status.Should().Be(SegmentRecoveryStatus.Refused);
        result.Reason.Should().Contain("ожидался индекс");
        result.FrameCount.Should().Be(1);
        NoDestructiveOperations(fileSystem);
    }

    [Fact]
    public async Task CorruptionInTheMiddle_UnknownRecordType_IsRefused()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = await LogTestData.WriteSegmentAsync(
            fileSystem,
            LogTestData.Command(First),
            LogTestData.Command(Second),
            LogTestData.Command(Third));

        var bytes = fileSystem.ReadAllBytes(path);
        var secondFrameOffset = LogTestData.FrameOffsets(bytes)[1];
        bytes[secondFrameOffset + sizeof(uint)] = 0x7F;
        LogTestData.FixFrameCrc(bytes, secondFrameOffset);
        fileSystem.Seed(path, bytes);

        var result = await RecoverAsync(fileSystem, path, SegmentKind.Last);

        result.Status.Should().Be(SegmentRecoveryStatus.Refused);
        result.Reason.Should().Contain("recordType");
        result.Reason.Should().Contain("середин");
        NoDestructiveOperations(fileSystem);
    }

    [Fact]
    public async Task CorruptionInTheMiddle_DecreasingTerm_IsRefused()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = await LogTestData.WriteSegmentAsync(
            fileSystem,
            LogTestData.Command(First, term: 5),
            LogTestData.Command(Second, term: 5),
            LogTestData.Command(Third, term: 5));

        var bytes = fileSystem.ReadAllBytes(path);
        var secondFrameOffset = LogTestData.FrameOffsets(bytes)[1];
        WriteInt64(bytes, TermOffset(secondFrameOffset), 4);
        LogTestData.FixFrameCrc(bytes, secondFrameOffset);
        fileSystem.Seed(path, bytes);

        var result = await RecoverAsync(fileSystem, path, SegmentKind.Last);

        result.Status.Should().Be(SegmentRecoveryStatus.Refused);
        result.Reason.Should().Contain("меньше терма");
        result.Reason.Should().Contain("середин");
        NoDestructiveOperations(fileSystem);
    }

    [Fact]
    public async Task SealedSegment_WithTornTail_IsRefusedNotTruncated()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = await LogTestData.WriteSegmentAsync(fileSystem, LogTestData.Command(First), LogTestData.Command(Second));
        var bytes = fileSystem.ReadAllBytes(path);
        var secondFrameOffset = LogTestData.FrameOffsets(bytes)[1];
        fileSystem.Seed(path, bytes.AsSpan(0, secondFrameOffset + 10));
        var damaged = fileSystem.ReadAllBytes(path);

        var result = await RecoverAsync(fileSystem, path, SegmentKind.Sealed);

        // Запечатанный сегмент не может быть оборван: писатель fsync-нул его перед преемником (§8, §9.2).
        result.Status.Should().Be(SegmentRecoveryStatus.Refused);
        result.Reason.Should().Contain("запечатанный");
        result.FrameCount.Should().Be(1);
        NoDestructiveOperations(fileSystem);
        fileSystem.ReadAllBytes(path).Should().Equal(damaged);
    }

    [Fact]
    public async Task TrailingGarbage_IsRefusedInASealedSegmentButTruncatedInTheLastOne()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = await LogTestData.WriteSegmentAsync(fileSystem, LogTestData.Command(First), LogTestData.Command(Second));
        var bytes = fileSystem.ReadAllBytes(path).ToList();
        bytes.AddRange([0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
        fileSystem.Seed(path, [.. bytes]);

        var asSealed = await RecoverAsync(fileSystem, path, SegmentKind.Sealed);
        var asLast = await RecoverAsync(fileSystem, path, SegmentKind.Last);

        // Одни и те же байты: остаток в запечатанном сегменте - порча (§9.5 шаг 6), в последнем -
        // оборванный хвост (§9.2). Ровно поэтому вид сегмента - вход восстановления, а не догадка.
        asSealed.Status.Should().Be(SegmentRecoveryStatus.Refused);
        asLast.Status.Should().Be(SegmentRecoveryStatus.TruncatedTail);
        asLast.DroppedBytes.Should().Be(5);
        fileSystem.ReadAllBytes(path).Should().Equal(bytes.Take(bytes.Count - 5));
    }

    [Fact]
    public async Task ShortFileInTheLastSegment_IsRemoved()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = LogTestData.SegmentPath(First);
        fileSystem.Seed(path, new byte[17]);

        var result = await RecoverAsync(fileSystem, path, SegmentKind.Last);

        result.Status.Should().Be(SegmentRecoveryStatus.Removed);
        result.ChangedFile.Should().BeTrue();
        result.IsRefused.Should().BeFalse();
        result.DroppedBytes.Should().Be(17);
        result.Reason.Should().Contain("§9.3");
        result.ValidEndOffset.Should().Be(0);
        result.FrameCount.Should().Be(0);
        result.LastIndex.Should().BeNull();

        fileSystem.Exists(path).Should().BeFalse("оборванное создание сегмента чинится удалением (§9.3)");
        fileSystem.Operations.Should().ContainSingle(operation => operation.Kind == StorageOperationKind.Delete);
    }

    [Fact]
    public async Task EmptyFileInTheLastSegment_IsRemoved()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = LogTestData.SegmentPath(First);
        fileSystem.Seed(path, []);

        var result = await RecoverAsync(fileSystem, path, SegmentKind.Last);

        result.Status.Should().Be(SegmentRecoveryStatus.Removed);
        result.DroppedBytes.Should().Be(0);
        fileSystem.Exists(path).Should().BeFalse();
    }

    [Fact]
    public async Task ShortFileInASealedSegment_IsRefusedAndKept()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = LogTestData.SegmentPath(First);
        fileSystem.Seed(path, new byte[17]);

        var result = await RecoverAsync(fileSystem, path, SegmentKind.Sealed);

        result.Status.Should().Be(SegmentRecoveryStatus.Refused);
        result.Reason.Should().Contain("запечатанный");
        result.Reason.Should().Contain("§9.3");
        fileSystem.Exists(path).Should().BeTrue();
        NoDestructiveOperations(fileSystem);
    }

    [Fact]
    public async Task UnreadableHeaderInTheLastSegment_IsRefusedAndKept()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = LogTestData.SegmentPath(First);
        var bytes = LogTestData.HeaderOnlySegment(First);
        bytes[0] ^= 0xFF;
        fileSystem.Seed(path, bytes);

        var result = await RecoverAsync(fileSystem, path, SegmentKind.Last);

        // §9.3: удалять такой файл нельзя - на смещении 30 могли остаться валидные фреймы.
        result.Status.Should().Be(SegmentRecoveryStatus.Refused);
        result.Reason.Should().Contain("§9.3");
        result.Reason.Should().Contain("магия");
        result.DroppedBytes.Should().Be(0);
        fileSystem.Exists(path).Should().BeTrue();
        NoDestructiveOperations(fileSystem);
    }

    [Fact]
    public async Task DamagedHeaderCrcInASealedSegment_IsRefused()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = LogTestData.SegmentPath(First);
        var bytes = LogTestData.HeaderOnlySegment(First);
        bytes[LogSegmentFormat.HeaderSize - 1] ^= 0xFF;
        fileSystem.Seed(path, bytes);

        var result = await RecoverAsync(fileSystem, path, SegmentKind.Sealed);

        result.Status.Should().Be(SegmentRecoveryStatus.Refused);
        result.Reason.Should().Contain("§9.4");
        fileSystem.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task UnknownFormatVersionInTheLastSegment_IsRefused()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = LogTestData.SegmentPath(First);
        var bytes = LogTestData.HeaderOnlySegment(First);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(LogSegmentFormat.MagicSize), 99);
        fileSystem.Seed(path, bytes);

        var result = await RecoverAsync(fileSystem, path, SegmentKind.Last);

        // Версия читается раньше CRC: файл будущей версии отвергается по версии, а не «CRC не сошлась».
        result.Status.Should().Be(SegmentRecoveryStatus.Refused);
        result.Reason.Should().Contain("версия формата 99");
        fileSystem.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task HeaderBaseIndexThatDoesNotMatchTheFileName_IsRefused()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = LogTestData.SegmentPath(First);
        fileSystem.Seed(path, LogTestData.HeaderOnlySegment(Second));

        var result = await RecoverAsync(fileSystem, path, SegmentKind.Last);

        result.Status.Should().Be(SegmentRecoveryStatus.Refused);
        result.Reason.Should().Contain("§9.1");
        fileSystem.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task ForeignFileInTheLogDirectory_IsRefused()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = Path.Combine(LogTestData.Directory, "notes.txt");
        fileSystem.Seed(path, LogTestData.HeaderOnlySegment(First));

        var result = await RecoverAsync(fileSystem, path, SegmentKind.Last);

        result.Status.Should().Be(SegmentRecoveryStatus.Refused);
        result.Reason.Should().Contain(".seg");
        fileSystem.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task MissingFile_Throws()
    {
        var fileSystem = new FakeStorageFileSystem();

        Func<Task> recover = async () => await RecoverAsync(fileSystem, LogTestData.SegmentPath(First), SegmentKind.Last);

        await recover.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task AlreadyCancelledRecovery_ChangesNothing()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = await LogTestData.WriteSegmentAsync(fileSystem, LogTestData.Command(First), LogTestData.Command(Second));
        var bytes = fileSystem.ReadAllBytes(path);
        var secondFrameOffset = LogTestData.FrameOffsets(bytes)[1];
        fileSystem.Seed(path, bytes.AsSpan(0, secondFrameOffset + 10));

        var operationsBefore = fileSystem.Operations.Count;

        Func<Task> recover = async () => await SegmentRecovery.RecoverAsync(
            fileSystem,
            path,
            SegmentKind.Last,
            new CancellationToken(canceled: true));

        await recover.Should().ThrowAsync<OperationCanceledException>();
        fileSystem.Operations.Count.Should().Be(operationsBefore);
        NoDestructiveOperations(fileSystem);
    }

    [Fact]
    public async Task FailedReadDuringRecovery_LeavesTheFileUntouched()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = await LogTestData.WriteSegmentAsync(fileSystem, LogTestData.Command(First), LogTestData.Command(Second));
        var bytes = fileSystem.ReadAllBytes(path);
        var secondFrameOffset = LogTestData.FrameOffsets(bytes)[1];
        fileSystem.Seed(path, bytes.AsSpan(0, secondFrameOffset + 10));
        var damaged = fileSystem.ReadAllBytes(path);

        fileSystem.FailureInjector = operation => operation.Kind == StorageOperationKind.Read
            ? new IOException("Устройство отказало во время восстановления.")
            : null;

        Func<Task> recover = async () => await RecoverAsync(fileSystem, path, SegmentKind.Last);

        await recover.Should().ThrowAsync<IOException>();
        fileSystem.ReadAllBytes(path).Should().Equal(damaged);
        NoDestructiveOperations(fileSystem);
    }

    [Fact]
    public async Task AfterTruncation_TheSegmentIsCompleteAndKeepsTheSurvivingRecords()
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = await LogTestData.WriteSegmentAsync(
            fileSystem,
            LogTestData.Command(First),
            LogTestData.Command(Second, term: 2),
            LogTestData.Command(Third, term: 3));

        var whole = fileSystem.ReadAllBytes(path);
        var thirdFrameOffset = LogTestData.FrameOffsets(whole)[2];
        fileSystem.Seed(path, whole.AsSpan(0, thirdFrameOffset + 10));

        var recovery = await RecoverAsync(fileSystem, path, SegmentKind.Last);
        recovery.Status.Should().Be(SegmentRecoveryStatus.TruncatedTail);

        var open = await SegmentReader.OpenAsync(fileSystem, path, CancellationToken.None);
        open.IsSuccess.Should().BeTrue(open.Reason);
        await using var reader = open.Reader!;
        var scan = await reader.ScanAsync(CancellationToken.None);

        // Восстановление оставляет сегмент, который читается как целый: остаток файла - ровно
        // валидный префикс, поэтому следующий старт не найдёт здесь ничего нового.
        scan.IsComplete.Should().BeTrue(scan.Reason);
        scan.FrameCount.Should().Be(2);
        scan.ValidEndOffset.Should().Be(thirdFrameOffset);
        LogTestData.ReadSegment(fileSystem, First).Should().Equal(LogTestData.Command(First), LogTestData.Command(Second, term: 2));
    }

    private static ValueTask<SegmentRecoveryResult> RecoverAsync(
        FakeStorageFileSystem fileSystem,
        string path,
        SegmentKind kind)
        => SegmentRecovery.RecoverAsync(fileSystem, path, kind, CancellationToken.None);

    /// <summary>Сегмент из двух фреймов: портить последний фрейм удобнее всего в нём.</summary>
    /// <param name="term">Терм обеих записей: тесту убывания терма нужен запас.</param>
    private static async Task<(FakeStorageFileSystem FileSystem, string Path, byte[] Bytes, int LastFrameOffset)> TwoFrameSegmentAsync(
        long term = 1)
    {
        var fileSystem = new FakeStorageFileSystem();
        var path = await LogTestData.WriteSegmentAsync(
            fileSystem,
            LogTestData.Command(First, term),
            LogTestData.Command(Second, term));
        var bytes = fileSystem.ReadAllBytes(path);

        return (fileSystem, path, bytes, LogTestData.FrameOffsets(bytes)[1]);
    }

    /// <summary>Смещение поля <c>index</c> внутри фрейма: последнее поле перед payload.</summary>
    private static int IndexOffset(int frameOffset)
        => frameOffset + LogSegmentFormat.FrameHeaderSize - sizeof(long);

    /// <summary>Смещение поля <c>term</c> внутри фрейма: сразу за полем типа записи.</summary>
    private static int TermOffset(int frameOffset)
        => frameOffset + sizeof(uint) + sizeof(byte);

    private static void WriteInt64(byte[] bytes, int offset, long value)
        => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset), value);

    private static void WriteUInt32(byte[] bytes, int offset, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);

    /// <summary>Восстановление не должно ни усекать, ни удалять файл, когда на то нет права (§9.4).</summary>
    private static void NoDestructiveOperations(FakeStorageFileSystem fileSystem)
        => fileSystem.Operations.Should().NotContain(
            operation => operation.Kind == StorageOperationKind.Truncate
                || operation.Kind == StorageOperationKind.Delete);
}
