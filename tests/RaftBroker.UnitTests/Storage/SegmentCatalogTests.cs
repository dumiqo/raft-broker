using RaftBroker.Core.Primitives;
using RaftBroker.Storage.Log;
using RaftBroker.TestKit.IO;

namespace RaftBroker.UnitTests.Storage;

/// <summary>
/// S1-T06: каталог журнала как единая норма «какие файлы образуют журнал».
/// </summary>
/// <remarks>
/// Каталог - единственное место, где видно все сегменты сразу, поэтому здесь проверяются
/// правила §9.5, которые до <c>S1-T06</c> не проверял никто: имя файла, непрерывность
/// диапазонов, пустой сегмент только последним и разделение «оборван» / «испорчен».
/// Второй предмет проверки - дескрипторы: каталог не имеет права удерживать открытые
/// читатели ни на успехе, ни на отказе, иначе восстановление (<c>S1-T07</c>) не сможет
/// усечь последний сегмент на Windows.
/// </remarks>
public sealed class SegmentCatalogTests
{
    /// <summary>В сегмент помещаются заголовок и два фрейма, но не три.</summary>
    private const int SegmentWithTwoFrames = 100;

    [Fact]
    public async Task EmptyDirectory_IsAnEmptyJournal()
    {
        var fileSystem = new FakeStorageFileSystem();

        var catalog = await ReadAsync(fileSystem);

        catalog.IsSuccess.Should().BeTrue(catalog.Reason);
        catalog.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task ForeignFileName_IsCorrupt()
    {
        var fileSystem = new FakeStorageFileSystem();
        fileSystem.Seed(Path.Combine(LogTestData.Directory, "notes.txt"), [1, 2, 3]);

        var catalog = await ReadAsync(fileSystem);

        catalog.Status.Should().Be(LogReadStatus.Corrupt);
        catalog.Path.Should().Be(Path.Combine(LogTestData.Directory, "notes.txt"));
    }

    [Fact]
    public async Task Segments_AreOrderedByBaseIndexAndTheLastOneIsMarkedLast()
    {
        var fileSystem = new FakeStorageFileSystem();
        await AppendAsync(fileSystem, 1, 2, 3, 4);

        var catalog = await ReadAsync(fileSystem);

        catalog.IsSuccess.Should().BeTrue(catalog.Reason);
        catalog.Entries.Select(entry => entry.BaseIndex.Value).Should().Equal(1, 3);
        catalog.Entries.Select(entry => entry.Kind).Should().Equal(SegmentKind.Sealed, SegmentKind.Last);
        catalog.Entries.Select(entry => entry.LastIndex!.Value.Value).Should().Equal(2, 4);
    }

    [Fact]
    public async Task FirstSegment_MayStartAfterOne()
    {
        // Историю ниже могла убрать компакция: сверять начало журнала со снапшотом -
        // дело хранилища, а не каталога.
        var fileSystem = new FakeStorageFileSystem();
        await using (var writer = LogTestData.CreateWriter(fileSystem))
        {
            await writer.AppendAsync([LogTestData.Command(7)], CancellationToken.None);
        }

        var catalog = await ReadAsync(fileSystem);

        catalog.IsSuccess.Should().BeTrue(catalog.Reason);
        catalog.Entries.Should().ContainSingle().Which.BaseIndex.Value.Should().Be(7);
    }

    [Fact]
    public async Task GapBetweenSegments_IsCorrupt()
    {
        var fileSystem = new FakeStorageFileSystem();
        await AppendAsync(fileSystem, 1, 2, 3, 4);
        fileSystem.Seed(LogTestData.SegmentPath(9), LogTestData.HeaderOnlySegment(9));

        var catalog = await ReadAsync(fileSystem);

        // Сегмент 3 кончается на 4, значит следующий обязан начинаться с 5.
        catalog.Status.Should().Be(LogReadStatus.Corrupt);
        catalog.Path.Should().Be(LogTestData.SegmentPath(9));
    }

    [Fact]
    public async Task EmptySegmentThatIsNotLast_IsCorrupt()
    {
        var fileSystem = new FakeStorageFileSystem();
        await AppendAsync(fileSystem, 1, 2, 3, 4);
        fileSystem.Seed(LogTestData.SegmentPath(1), LogTestData.HeaderOnlySegment(1));

        var catalog = await ReadAsync(fileSystem);

        catalog.Status.Should().Be(LogReadStatus.Corrupt);
        catalog.Path.Should().Be(LogTestData.SegmentPath(1));
    }

    [Fact]
    public async Task TruncatedTailOfTheLastSegment_IsReportedAsTruncated()
    {
        var fileSystem = new FakeStorageFileSystem();
        await AppendAsync(fileSystem, 1, 2);
        var bytes = fileSystem.ReadAllBytes(LogTestData.SegmentPath(1));
        fileSystem.Seed(LogTestData.SegmentPath(1), bytes.AsSpan(0, bytes.Length - 3));

        var catalog = await ReadAsync(fileSystem);

        catalog.Status.Should().Be(LogReadStatus.Truncated);
        catalog.Reason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task TruncatedTailOfANonLastSegment_IsCorrupt()
    {
        // Усекать можно только хвост последнего сегмента (§8): оборванный запечатанный
        // сегмент означает потерянную середину журнала, а не недописанную запись.
        var fileSystem = new FakeStorageFileSystem();
        await AppendAsync(fileSystem, 1, 2, 3, 4);
        var bytes = fileSystem.ReadAllBytes(LogTestData.SegmentPath(1));
        fileSystem.Seed(LogTestData.SegmentPath(1), bytes.AsSpan(0, bytes.Length - 3));

        var catalog = await ReadAsync(fileSystem);

        catalog.Status.Should().Be(LogReadStatus.Corrupt);
    }

    [Fact]
    public async Task FileShorterThanTheHeaderAsLast_IsReportedAsTruncated()
    {
        // Оборванное создание сегмента (§9.3): удалить его - работа восстановления (S1-T07).
        var fileSystem = new FakeStorageFileSystem();
        fileSystem.Seed(LogTestData.SegmentPath(1), [1, 2, 3]);

        var catalog = await ReadAsync(fileSystem);

        catalog.Status.Should().Be(LogReadStatus.Truncated);
        catalog.Path.Should().Be(LogTestData.SegmentPath(1));
    }

    [Fact]
    public async Task FileShorterThanTheHeaderAsNonLast_IsCorrupt()
    {
        // Запечатанный сегмент не мог быть создан недописанным (§8): короткий файл здесь
        // означает потерю данных, а не прерванную запись.
        var fileSystem = new FakeStorageFileSystem();
        await AppendAsync(fileSystem, 1, 2, 3, 4);
        fileSystem.Seed(LogTestData.SegmentPath(1), [1, 2, 3]);

        var catalog = await ReadAsync(fileSystem);

        catalog.Status.Should().Be(LogReadStatus.Corrupt);
    }

    [Fact]
    public async Task Catalog_DoesNotHoldReaders()
    {
        var fileSystem = new FakeStorageFileSystem();
        await AppendAsync(fileSystem, 1, 2, 3, 4);

        var catalog = await ReadAsync(fileSystem);

        catalog.IsSuccess.Should().BeTrue(catalog.Reason);
        fileSystem.OpenReadFileCount.Should().Be(0);
    }

    [Fact]
    public async Task Catalog_DoesNotHoldReadersOnFailure()
    {
        var fileSystem = new FakeStorageFileSystem();
        await AppendAsync(fileSystem, 1, 2, 3, 4);
        var bytes = fileSystem.ReadAllBytes(LogTestData.SegmentPath(1));
        fileSystem.Seed(LogTestData.SegmentPath(1), bytes.AsSpan(0, bytes.Length - 3));

        var catalog = await ReadAsync(fileSystem);

        catalog.Status.Should().Be(LogReadStatus.Corrupt);
        fileSystem.OpenReadFileCount.Should().Be(0);
    }

    private static ValueTask<SegmentCatalogResult> ReadAsync(FakeStorageFileSystem fileSystem)
        => SegmentCatalog.ReadAsync(fileSystem, LogTestData.Directory, CancellationToken.None);

    private static async Task AppendAsync(FakeStorageFileSystem fileSystem, params long[] indexes)
    {
        var records = indexes.Select(index => LogTestData.Command(index)).ToArray();
        await using var writer = LogTestData.CreateWriter(fileSystem, maxSegmentSize: SegmentWithTwoFrames);
        await writer.AppendAsync(records, CancellationToken.None);
    }
}
