using RaftBroker.Storage.Log;
using RaftBroker.TestKit.IO;

namespace RaftBroker.UnitTests.Storage;

/// <summary>
/// S1-T03: политики долговечности проверяются наблюдением за вызовами fsync.
/// </summary>
/// <remarks>
/// Чек-лист R1 требует, чтобы <see cref="DurabilityMode"/> влиял на реальные вызовы
/// <see cref="RaftBroker.Storage.IO.IStorageFile.FlushToDiskAsync"/>, а не только на
/// комментарий. Счётчик и журнал операций даёт <see cref="FakeStorageFileSystem"/>;
/// отдельно проверяется, что возврат из append в групповом режиме не наступает раньше fsync.
/// </remarks>
public sealed class SegmentWriterDurabilityTests
{
    private const int TwoFrameSegment = 100;

    [Fact]
    public async Task PerEntry_FsyncsEveryRecord()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using var writer = SegmentWriterTestData.CreateWriter(fileSystem);

        await writer.AppendAsync(
            [SegmentWriterTestData.Command(1), SegmentWriterTestData.Command(2), SegmentWriterTestData.Command(3)],
            DurabilityMode.PerEntry,
            CancellationToken.None);

        // Заголовок сегмента - один fsync плюс по одному на каждую из трёх записей.
        fileSystem.FlushToDiskCount.Should().Be(4);
    }

    [Fact]
    public async Task GroupCommit_FsyncsOnceForTheWholeBatch()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using var writer = SegmentWriterTestData.CreateWriter(fileSystem);

        await writer.AppendAsync(
            [SegmentWriterTestData.Command(1), SegmentWriterTestData.Command(2), SegmentWriterTestData.Command(3)],
            DurabilityMode.GroupCommit,
            CancellationToken.None);

        fileSystem.FlushToDiskCount.Should().Be(2);
    }

    [Fact]
    public async Task GroupCommit_IsTheDefaultMode()
    {
        new SegmentWriterOptions { Directory = SegmentWriterTestData.Directory }
            .DefaultDurabilityMode.Should().Be(DurabilityMode.GroupCommit);

        var fileSystem = new FakeStorageFileSystem();
        await using var writer = SegmentWriterTestData.CreateWriter(fileSystem);

        await writer.AppendAsync(
            [SegmentWriterTestData.Command(1), SegmentWriterTestData.Command(2)],
            CancellationToken.None);

        fileSystem.FlushToDiskCount.Should().Be(2);
    }

    [Fact]
    public async Task Periodic_DoesNotFsyncOnAppend()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using var writer = SegmentWriterTestData.CreateWriter(fileSystem, defaultMode: DurabilityMode.Periodic);

        await writer.AppendAsync(
            [SegmentWriterTestData.Command(1), SegmentWriterTestData.Command(2), SegmentWriterTestData.Command(3)],
            CancellationToken.None);

        // Единственный fsync - заголовок сегмента (§9.3); записи на устройство не отправлены.
        fileSystem.FlushToDiskCount.Should().Be(1);
    }

    [Fact]
    public async Task Periodic_MakesRecordsDurableOnExplicitFlush()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using var writer = SegmentWriterTestData.CreateWriter(fileSystem, defaultMode: DurabilityMode.Periodic);

        await writer.AppendAsync([SegmentWriterTestData.Command(1)], CancellationToken.None);
        var fsyncsAfterAppend = fileSystem.FlushToDiskCount;

        await writer.FlushToDiskAsync(CancellationToken.None);

        fsyncsAfterAppend.Should().Be(1);
        fileSystem.FlushToDiskCount.Should().Be(2);
    }

    [Fact]
    public async Task Periodic_AppendDoesNotTouchTheDiskWhenFsyncWouldFail()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using var writer = SegmentWriterTestData.CreateWriter(fileSystem, defaultMode: DurabilityMode.Periodic);

        await writer.AppendAsync([SegmentWriterTestData.Command(1)], CancellationToken.None);
        var fsyncsAfterCreation = fileSystem.FlushToDiskCount;

        fileSystem.FailureInjector = operation => operation.Kind == StorageOperationKind.FlushToDisk
            ? new IOException("fsync не проходит: устройство недоступно")
            : null;

        Func<Task> append = async () => await writer.AppendAsync([SegmentWriterTestData.Command(2)], CancellationToken.None);

        await append.Should().NotThrowAsync("режим Periodic в append не вызывает fsync");
        fileSystem.FlushToDiskCount.Should().Be(fsyncsAfterCreation);

        Func<Task> flush = async () => await writer.FlushToDiskAsync(CancellationToken.None);
        await flush.Should().ThrowAsync<IOException>();
    }

    [Fact]
    public async Task GroupCommit_ReturnsOnlyAfterFsync()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using var writer = SegmentWriterTestData.CreateWriter(fileSystem);

        await writer.AppendAsync([SegmentWriterTestData.Command(1)], CancellationToken.None);

        fileSystem.FailureInjector = operation => operation.Kind == StorageOperationKind.FlushToDisk
            ? new IOException("fsync не прошёл")
            : null;

        Func<Task> append = async () => await writer.AppendAsync([SegmentWriterTestData.Command(2)], CancellationToken.None);

        // Вызывающий обязан узнать об отказе fsync, а не получить ложное "запись долговечна".
        await append.Should().ThrowAsync<IOException>();

        // Байты при этом уже дописаны в файл: отказ произошёл именно на fsync, а не на записи.
        fileSystem.ReadAllBytes(SegmentWriterTestData.SegmentPath(1)).Length.Should().BeGreaterThan(LogSegmentFormat.HeaderSize);
    }

    [Fact]
    public async Task PerEntry_PropagatesAnFsyncFailureOfTheSecondRecord()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using var writer = SegmentWriterTestData.CreateWriter(fileSystem, defaultMode: DurabilityMode.PerEntry);

        await writer.AppendAsync([SegmentWriterTestData.Command(1)], CancellationToken.None);

        fileSystem.FailureInjector = operation => operation.Kind == StorageOperationKind.FlushToDisk
            ? new IOException("fsync не прошёл")
            : null;

        Func<Task> append = async () => await writer.AppendAsync([SegmentWriterTestData.Command(2)], CancellationToken.None);

        await append.Should().ThrowAsync<IOException>();
    }

    [Fact]
    public async Task Rollover_InPeriodicMode_StillFsyncsTheSealedSegment()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using var writer = SegmentWriterTestData.CreateWriter(
            fileSystem,
            maxSegmentSize: TwoFrameSegment,
            defaultMode: DurabilityMode.Periodic);

        await writer.AppendAsync(
            [SegmentWriterTestData.Command(1), SegmentWriterTestData.Command(2), SegmentWriterTestData.Command(3)],
            CancellationToken.None);

        // Порядок §8 соблюдается и в режиме Periodic: запись в новый сегмент не начинается,
        // пока запечатываемый сегмент не отправлен на устройство.
        fileSystem.Operations
            .Select(operation => (operation.Kind, Path.GetFileName(operation.Path)))
            .Should().Equal(
                (StorageOperationKind.Create, FileName(1)),
                (StorageOperationKind.Write, FileName(1)),
                (StorageOperationKind.FlushToDisk, FileName(1)),
                (StorageOperationKind.Write, FileName(1)),
                (StorageOperationKind.Write, FileName(1)),
                (StorageOperationKind.FlushToDisk, FileName(1)),
                (StorageOperationKind.Create, FileName(3)),
                (StorageOperationKind.Write, FileName(3)),
                (StorageOperationKind.FlushToDisk, FileName(3)),
                (StorageOperationKind.Write, FileName(3)));

        // Три fsync: заголовок первого сегмента, запечатывание первого, заголовок второго.
        fileSystem.FlushToDiskCount.Should().Be(3);
    }

    [Fact]
    public async Task FlushToDisk_BeforeTheFirstAppend_DoesNothing()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using var writer = SegmentWriterTestData.CreateWriter(fileSystem);

        await writer.FlushToDiskAsync(CancellationToken.None);

        fileSystem.Operations.Should().BeEmpty();
    }

    private static string FileName(long baseIndex) => Path.GetFileName(SegmentWriterTestData.SegmentPath(baseIndex));
}
