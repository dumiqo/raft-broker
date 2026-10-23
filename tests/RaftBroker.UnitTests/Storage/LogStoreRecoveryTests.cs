using RaftBroker.Core.Primitives;
using RaftBroker.Storage.Log;
using RaftBroker.TestKit.IO;
using RaftBroker.TestKit.Time;

namespace RaftBroker.UnitTests.Storage;

/// <summary>
/// S1-T07: восстановление при старте.
/// </summary>
/// <remarks>
/// Проверяется то, что видно только на уровне журнала: оборванный последний сегмент чинится
/// сам, отчёт об этом не молчит (§9.2, §9.3), порча по-прежнему останавливает старт и не трогает
/// диск (§9.4), а терм на стыке файлов не убывает (§4). Отдельно - инвариант задачи: после
/// рестарта <c>LastIndex</c> и <c>LastTerm</c> те же, что были до него.
/// </remarks>
public sealed class LogStoreRecoveryTests
{
    /// <summary>В сегмент помещаются заголовок и два фрейма, но не три: так появляются стыки файлов.</summary>
    private const long SegmentWithTwoFrames = 100;

    [Fact]
    public async Task OpenAsync_TruncatesTornTail_AndReportsDroppedBytes()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using (var store = await OpenAsync(fileSystem))
        {
            await store.AppendAsync([LogTestData.Command(1), LogTestData.Command(2)], CancellationToken.None);
        }

        var bytes = fileSystem.ReadAllBytes(LogTestData.SegmentPath(1));
        fileSystem.Seed(LogTestData.SegmentPath(1), bytes.AsSpan(0, bytes.Length - 3));

        var open = await LogStore.OpenAsync(fileSystem, Clock, Options(), CancellationToken.None);

        open.IsSuccess.Should().BeTrue(open.Reason);
        open.Report.Repaired.Should().BeTrue();
        open.Report.TruncatedSegments.Should().Be(1);
        open.Report.RemovedSegments.Should().Be(0);
        open.Report.DroppedBytes.Should().Be(LogFrameCodec.GetFrameSize(4) - 3, "отброшен недописанный хвост последнего фрейма");
        open.Report.Path.Should().Be(LogTestData.SegmentPath(1));
        open.Report.Reason.Should().NotBeNullOrWhiteSpace("причина усечения нужна в отчёте, а не только в файле");

        // После ремонта журнал продолжается с последней целой записи, а не с начала.
        open.Store!.LastIndex.Value.Should().Be(1);
        open.Store.LastTerm.Value.Should().Be(1);
        open.Report.FirstIndex.Value.Should().Be(1);
        open.Report.LastIndex.Value.Should().Be(1);
        open.Report.LastTerm.Value.Should().Be(1);
        fileSystem.ReadAllBytes(LogTestData.SegmentPath(1)).Length
            .Should().Be(LogSegmentFormat.HeaderSize + LogFrameCodec.GetFrameSize(4));
    }

    [Fact]
    public async Task OpenAsync_RemovesTornSegmentCreation_AndContinuesThePreviousSegment()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using (var store = await OpenAsync(fileSystem, SegmentWithTwoFrames))
        {
            await store.AppendAsync([LogTestData.Command(1), LogTestData.Command(2), LogTestData.Command(3)], CancellationToken.None);
        }

        // Файл короче заголовка - оборванное создание сегмента (§9.3): подтверждённых записей в нём быть не может.
        fileSystem.Seed(LogTestData.SegmentPath(3), [1, 2, 3, 4]);

        var open = await LogStore.OpenAsync(fileSystem, Clock, Options(SegmentWithTwoFrames), CancellationToken.None);

        open.IsSuccess.Should().BeTrue(open.Reason);
        open.Report.Repaired.Should().BeTrue();
        open.Report.RemovedSegments.Should().Be(1);
        open.Report.TruncatedSegments.Should().Be(0);
        open.Report.DroppedBytes.Should().Be(4, "удалённый файл учтён целиком");
        open.Report.Path.Should().Be(LogTestData.SegmentPath(3));

        // Журнал кончается предыдущим сегментом и продолжается его последней записью.
        open.Store!.LastIndex.Value.Should().Be(2);
        open.Store.LastTerm.Value.Should().Be(1);
        fileSystem.Paths.Should().NotContain(LogTestData.SegmentPath(3));
    }

    [Fact]
    public async Task OpenAsync_RefusesCorruptSealedSegment_AndLeavesTheDiskAlone()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using (var store = await OpenAsync(fileSystem, SegmentWithTwoFrames))
        {
            await store.AppendAsync([LogTestData.Command(1), LogTestData.Command(2), LogTestData.Command(3)], CancellationToken.None);
        }

        // Порча в запечатанном сегменте - отказ, а не усечение: усечение потеряло бы историю (§9.4).
        var bytes = fileSystem.ReadAllBytes(LogTestData.SegmentPath(1));
        bytes[LogSegmentFormat.HeaderSize + 6] ^= 0xFF;
        fileSystem.Seed(LogTestData.SegmentPath(1), bytes);
        var before = fileSystem.Operations.Count;

        var open = await LogStore.OpenAsync(fileSystem, Clock, Options(SegmentWithTwoFrames), CancellationToken.None);

        open.Status.Should().Be(LogReadStatus.Corrupt);
        open.Store.Should().BeNull();
        open.Path.Should().Be(LogTestData.SegmentPath(1));
        open.Report.IsOpen.Should().BeFalse("у отказа отчёта о ремонте нет: причина лежит в Reason");
        fileSystem.Operations.Skip(before)
            .Should().NotContain(
                operation => operation.Kind == StorageOperationKind.Truncate || operation.Kind == StorageOperationKind.Delete,
                "порча не чинится автоматически: диск остаётся как был");
    }

    [Fact]
    public async Task OpenAsync_RefusesWhenTheTermDecreasesAcrossSegments()
    {
        var fileSystem = new FakeStorageFileSystem();

        // Стык файлов - единственное место, где терм сравнивать некому: внутри сегмента это
        // делает чтение (§4), а между сегментами - только старт журнала.
        await LogTestData.WriteSegmentAsync(fileSystem, LogTestData.Command(1, term: 2), LogTestData.Command(2, term: 2));
        await LogTestData.WriteSegmentAsync(fileSystem, LogTestData.Command(3, term: 1), LogTestData.Command(4, term: 1));

        var open = await LogStore.OpenAsync(fileSystem, Clock, Options(), CancellationToken.None);

        open.Status.Should().Be(LogReadStatus.Corrupt);
        open.Path.Should().Be(LogTestData.SegmentPath(3));
        open.Reason.Should().Contain("терм");
        fileSystem.OpenReadFileCount.Should().Be(0);
    }

    [Fact]
    public async Task OpenAsync_RefusesWhenTheLastSegmentIsCorruptInTheMiddle()
    {
        // Оборванный хвост и порча в середине последнего сегмента выглядят одинаково для каталога
        // (разбор не дошёл до конца файла), но требуют разного: за повреждением есть целый фрейм (§9.5),
        // значит усечение потеряло бы подтверждённое - отказ.
        var fileSystem = new FakeStorageFileSystem();
        await using (var store = await OpenAsync(fileSystem))
        {
            await store.AppendAsync(
                [LogTestData.Command(1), LogTestData.Command(2), LogTestData.Command(3)],
                CancellationToken.None);
        }

        var bytes = fileSystem.ReadAllBytes(LogTestData.SegmentPath(1));
        bytes[LogTestData.FrameOffsets(bytes)[1] + LogSegmentFormat.FrameHeaderSize] ^= 0xFF;
        fileSystem.Seed(LogTestData.SegmentPath(1), bytes);
        var before = fileSystem.Operations.Count;

        var open = await LogStore.OpenAsync(fileSystem, Clock, Options(), CancellationToken.None);

        open.Status.Should().Be(LogReadStatus.Corrupt);
        open.Path.Should().Be(LogTestData.SegmentPath(1));
        open.Reason.Should().Contain("порча в середине");
        fileSystem.Operations.Skip(before)
            .Should().NotContain(
                operation => operation.Kind == StorageOperationKind.Truncate || operation.Kind == StorageOperationKind.Delete,
                "диск не тронут: порча остаётся человеку, а не автолечению");
    }

    [Fact]
    public async Task OpenAsync_OnEmptyLastSegment_ReportsTheEndOfThePreviousSegment()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using (var store = await OpenAsync(fileSystem, SegmentWithTwoFrames))
        {
            await store.AppendAsync([LogTestData.Command(1), LogTestData.Command(2), LogTestData.Command(3)], CancellationToken.None);
        }

        fileSystem.Seed(LogTestData.SegmentPath(3), LogTestData.HeaderOnlySegment(3));

        var open = await LogStore.OpenAsync(fileSystem, Clock, Options(SegmentWithTwoFrames), CancellationToken.None);

        open.IsSuccess.Should().BeTrue(open.Reason);
        open.Report.Repaired.Should().BeFalse("пустой последний сегмент - штатное состояние §8, а не ремонт");
        open.Report.LastIndex.Value.Should().Be(2);
        open.Report.LastTerm.Value.Should().Be(1);
    }

    [Fact]
    public async Task Reopen_AfterASeriesOfWrites_KeepsTheLastIndexAndTerm()
    {
        var fileSystem = new FakeStorageFileSystem();
        long lastIndex;

        await using (var store = await OpenAsync(fileSystem, SegmentWithTwoFrames))
        {
            for (var batch = 0; batch < 3; batch++)
            {
                await store.AppendAsync(
                    [LogTestData.Command((batch * 2) + 1, term: batch + 1), LogTestData.Command((batch * 2) + 2, term: batch + 1)],
                    CancellationToken.None);
            }

            lastIndex = store.LastIndex.Value;
            store.LastTerm.Value.Should().Be(3);
        }

        var open = await LogStore.OpenAsync(fileSystem, Clock, Options(SegmentWithTwoFrames), CancellationToken.None);

        open.IsSuccess.Should().BeTrue(open.Reason);
        open.Report.Repaired.Should().BeFalse("штатный рестарт ничего не чинит");
        open.Store!.LastIndex.Value.Should().Be(lastIndex);
        open.Store.LastTerm.Value.Should().Be(3);
        open.Report.FirstIndex.Should().Be(open.Store.FirstIndex);
        open.Report.LastIndex.Should().Be(open.Store.LastIndex);
        open.Report.LastTerm.Should().Be(open.Store.LastTerm);
    }

    private static TestClock Clock => new(startMs: LogTestData.MonotonicStartMs, unixTimeMs: LogTestData.NowMs);

    private static SegmentWriterOptions Options(long maxSegmentSize = LogSegmentFormat.MaxSegmentSize)
        => new()
        {
            Directory = LogTestData.Directory,
            MaxSegmentSize = maxSegmentSize,
        };

    private static async ValueTask<ILogStore> OpenAsync(
        FakeStorageFileSystem fileSystem,
        long maxSegmentSize = LogSegmentFormat.MaxSegmentSize)
    {
        var open = await LogStore.OpenAsync(fileSystem, Clock, Options(maxSegmentSize), CancellationToken.None);

        open.IsSuccess.Should().BeTrue(open.Reason);

        return open.Store!;
    }
}
