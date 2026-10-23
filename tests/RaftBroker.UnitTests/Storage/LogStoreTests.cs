using RaftBroker.Core.Primitives;
using RaftBroker.Storage.Log;
using RaftBroker.TestKit.IO;
using RaftBroker.TestKit.Time;

namespace RaftBroker.UnitTests.Storage;

/// <summary>
/// S1-T06: журнал как последовательность записей поверх сегментов.
/// </summary>
/// <remarks>
/// Проверяется то, чего не видно ни в писателе, ни в читателе: границы между сегментами
/// (чтение через несколько файлов), продолжение журнала после перезапуска, усечение
/// конфликтующего хвоста (R4) и хранение по снапшоту. Отдельно - дефекты, которые
/// прощают потерю подтверждённых записей: пустой последний сегмент (§8) и утечка
/// читателей на пути отказа.
/// </remarks>
public sealed class LogStoreTests
{
    /// <summary>В сегмент помещаются заголовок и два фрейма, но не три.</summary>
    private const long SegmentWithTwoFrames = 100;

    [Fact]
    public async Task OpenOnEmptyDirectory_IsAnEmptyJournal()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using var store = await OpenAsync(fileSystem);

        store.FirstIndex.Value.Should().Be(1);
        store.LastIndex.Value.Should().Be(0);
        store.LastTerm.Value.Should().Be(0);
        store.CommittedIndex.Value.Should().Be(0);
        fileSystem.Paths.Should().BeEmpty("журнал создаётся первой записью, а не открытием");
    }

    [Fact]
    public async Task Append_WritesRecordsIntoASegmentNamedAfterTheFirstIndex()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using var store = await OpenAsync(fileSystem);

        await store.AppendAsync([LogTestData.Command(1), LogTestData.Command(2)], CancellationToken.None);

        fileSystem.Paths.Should().Equal(LogTestData.SegmentPath(1));
        store.LastIndex.Value.Should().Be(2);
        store.LastTerm.Value.Should().Be(1);
        RecordsAt(fileSystem, 1).Select(record => record.Index.Value).Should().Equal(1, 2);
    }

    [Fact]
    public async Task Append_StartsAtFirstIndexOfAnEmptyJournalWithoutSnapshot()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using var store = await OpenAsync(fileSystem);

        var append = () => store.AppendAsync([LogTestData.Command(3)], CancellationToken.None).AsTask();

        // Пустой журнал продолжается с индекса 1: писать в него можно только её.
        await append.Should().ThrowAsync<InvalidOperationException>();
        fileSystem.Operations.Should().BeEmpty();
    }

    [Fact]
    public async Task Reopen_ContinuesTheSameSegment()
    {
        var fileSystem = new FakeStorageFileSystem();

        await using (var store = await OpenAsync(fileSystem))
        {
            await store.AppendAsync([LogTestData.Command(1), LogTestData.Command(2)], CancellationToken.None);
        }

        await using (var store = await OpenAsync(fileSystem))
        {
            store.LastIndex.Value.Should().Be(2);
            store.LastTerm.Value.Should().Be(1);

            await store.AppendAsync([LogTestData.Command(3)], CancellationToken.None);
        }

        fileSystem.Paths.Should().Equal(LogTestData.SegmentPath(1));
        RecordsAt(fileSystem, 1).Select(record => record.Index.Value).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task ReopenWithEmptyLastSegment_TakesTheJournalTailFromThePreviousSegment()
    {
        // Так выглядит падение между шагами rollover (§8): новый файл создан и его заголовок
        // сброшен на устройство, а первый фрейм ещё не записан. Журнал кончается предыдущим
        // сегментом, а дописывать нужно в пустой - новый файл с тем же базовым индексом
        // создать нельзя (§7).
        var fileSystem = new FakeStorageFileSystem();
        await using (var store = await OpenAsync(fileSystem, SegmentWithTwoFrames))
        {
            await store.AppendAsync([LogTestData.Command(1), LogTestData.Command(2)], CancellationToken.None);
        }

        fileSystem.Seed(LogTestData.SegmentPath(3), LogTestData.HeaderOnlySegment(3));

        await using (var store = await OpenAsync(fileSystem, SegmentWithTwoFrames))
        {
            store.LastIndex.Value.Should().Be(2);

            await store.AppendAsync([LogTestData.Command(3)], CancellationToken.None);

            store.LastIndex.Value.Should().Be(3);
            store.FirstIndex.Value.Should().Be(1);
        }

        RecordsAt(fileSystem, 3).Select(record => record.Index.Value).Should().Equal(3);
    }

    [Fact]
    public async Task GetAsync_TellsFoundFromNotYetAndFromCompacted()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using var store = await OpenAsync(fileSystem);
        await store.AppendAsync([LogTestData.Command(1), LogTestData.Command(2), LogTestData.Command(3)], CancellationToken.None);

        var found = await store.GetAsync(new LogIndex(2), CancellationToken.None);
        found.Status.Should().Be(LogLookupStatus.Found);
        found.Record!.Index.Value.Should().Be(2);

        var notYet = await store.GetAsync(new LogIndex(4), CancellationToken.None);
        notYet.Status.Should().Be(LogLookupStatus.NotYet);
        notYet.Record.Should().BeNull();

        // Снапшот на 2 оставляет сегмент на диске (он последний), но записи ниже 3 больше
        // не читаются: именно это отличает «сжато» от «ещё нет» (S3-T02).
        await store.CompactAsync(new LogIndex(2), CancellationToken.None);

        var compacted = await store.GetAsync(new LogIndex(2), CancellationToken.None);
        compacted.Status.Should().Be(LogLookupStatus.Compacted);
        compacted.Record.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_FindsRecordsInEverySegmentOfAThreeSegmentJournal()
    {
        // Три сегмента: 1 (1,2), 3 (3,4), 5 (5,6). Поиск идёт бинпоиском по базовым индексам,
        // и ошибка «на один сегмент левее» видна только когда сегментов больше двух.
        var fileSystem = new FakeStorageFileSystem();
        await using var store = await OpenAsync(fileSystem, SegmentWithTwoFrames);
        await store.AppendAsync(
            [
                LogTestData.Command(1),
                LogTestData.Command(2),
                LogTestData.Command(3, term: 2),
                LogTestData.Command(4, term: 2),
                LogTestData.Command(5, term: 9),
                LogTestData.Command(6, term: 9),
            ],
            CancellationToken.None);

        fileSystem.Paths.Should().Equal(
            LogTestData.SegmentPath(1),
            LogTestData.SegmentPath(3),
            LogTestData.SegmentPath(5));

        foreach (var (index, term) in new[] { (1, 1), (2, 1), (3, 2), (4, 2), (5, 9), (6, 9) })
        {
            var found = await store.GetAsync(new LogIndex(index), CancellationToken.None);

            found.Status.Should().Be(LogLookupStatus.Found, "запись {0} лежит в сегменте", index);
            found.Record!.Index.Value.Should().Be(index);
            found.Record.Term.Value.Should().Be(term);
        }

        (await store.GetAsync(new LogIndex(7), CancellationToken.None)).Status.Should().Be(LogLookupStatus.NotYet);
    }

    [Fact]
    public async Task Append_AfterCompactAsync_ContinuesTheJournalPastTheSnapshotBoundary()
    {
        // Компакция убирает сегменты ниже снапшота, но журнал продолжается: писатель стоит
        // в последнем сегменте, и следующий rollover обязан создать файл выше границы, а не
        // вернуться в удалённую историю.
        var fileSystem = new FakeStorageFileSystem();
        await using var store = await OpenAsync(fileSystem, SegmentWithTwoFrames);
        await store.AppendAsync(
            [LogTestData.Command(1), LogTestData.Command(2), LogTestData.Command(3), LogTestData.Command(4)],
            CancellationToken.None);
        await store.CompactAsync(new LogIndex(2), CancellationToken.None);

        await store.AppendAsync([LogTestData.Command(5), LogTestData.Command(6)], CancellationToken.None);

        fileSystem.Paths.Should().Equal(LogTestData.SegmentPath(3), LogTestData.SegmentPath(5));
        store.FirstIndex.Value.Should().Be(3, "снапшот остаётся границей читаемой истории");
        store.LastIndex.Value.Should().Be(6);
        RecordsAt(fileSystem, 3).Select(record => record.Index.Value).Should().Equal(3, 4);
        RecordsAt(fileSystem, 5).Select(record => record.Index.Value).Should().Equal(5, 6);
    }

    [Fact]
    public async Task ReadRangeAsync_ReadsAcrossSegmentsAndStopsAtMaxCount()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using var store = await OpenAsync(fileSystem, SegmentWithTwoFrames);
        await store.AppendAsync(
            [LogTestData.Command(1), LogTestData.Command(2), LogTestData.Command(3), LogTestData.Command(4)],
            CancellationToken.None);

        fileSystem.Paths.Should().Equal(LogTestData.SegmentPath(1), LogTestData.SegmentPath(3));

        var all = await store.ReadRangeAsync(new LogIndex(1), maxCount: 10, CancellationToken.None);
        all.Select(record => record.Index.Value).Should().Equal(1, 2, 3, 4);

        var tail = await store.ReadRangeAsync(new LogIndex(3), maxCount: 10, CancellationToken.None);
        tail.Select(record => record.Index.Value).Should().Equal(3, 4);

        var limited = await store.ReadRangeAsync(new LogIndex(2), maxCount: 2, CancellationToken.None);
        limited.Select(record => record.Index.Value).Should().Equal(2, 3);

        var empty = await store.ReadRangeAsync(new LogIndex(5), maxCount: 2, CancellationToken.None);
        empty.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ReadRangeAsync_RejectsNonPositiveCount(int maxCount)
    {
        var fileSystem = new FakeStorageFileSystem();
        await using var store = await OpenAsync(fileSystem);

        var read = () => store.ReadRangeAsync(new LogIndex(1), maxCount, CancellationToken.None).AsTask();

        await read.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task ReadRangeAsync_RefusesToReadBelowTheSnapshot()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using var store = await OpenAsync(fileSystem);
        await store.AppendAsync([LogTestData.Command(1), LogTestData.Command(2)], CancellationToken.None);
        await store.CompactAsync(new LogIndex(1), CancellationToken.None);

        var read = () => store.ReadRangeAsync(new LogIndex(1), maxCount: 10, CancellationToken.None).AsTask();

        // Сжатые записи не читаются: отдать неполную историю хуже, чем отказать.
        await read.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task TruncateSuffixAsync_CutsInsideTheLastSegment()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using var store = await OpenAsync(fileSystem);
        await store.AppendAsync(
            [LogTestData.Command(1), LogTestData.Command(2), LogTestData.Command(3), LogTestData.Command(4)],
            CancellationToken.None);

        await store.TruncateSuffixAsync(new LogIndex(3), CancellationToken.None);

        store.LastIndex.Value.Should().Be(2);
        store.LastTerm.Value.Should().Be(1);
        RecordsAt(fileSystem, 1).Select(record => record.Index.Value).Should().Equal(1, 2);

        // Продолжение начинается с того же места: конфликт разрешается так же, как в Raft.
        await store.AppendAsync([LogTestData.Command(3, term: 5)], CancellationToken.None);

        store.LastIndex.Value.Should().Be(3);
        store.LastTerm.Value.Should().Be(5);
        RecordsAt(fileSystem, 1).Select(record => record.Term.Value).Should().Equal(1, 1, 5);
    }

    [Fact]
    public async Task TruncateSuffixAsync_RefusesToCutCommittedRecords()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using var store = await OpenAsync(fileSystem);
        await store.AppendAsync([LogTestData.Command(1), LogTestData.Command(2), LogTestData.Command(3)], CancellationToken.None);
        await store.MarkCommittedAsync(new LogIndex(2), CancellationToken.None);

        var cut = () => store.TruncateSuffixAsync(new LogIndex(2), CancellationToken.None).AsTask();

        await cut.Should().ThrowAsync<InvalidOperationException>();
        store.LastIndex.Value.Should().Be(3);
        RecordsAt(fileSystem, 1).Should().HaveCount(3);
    }

    [Fact]
    public async Task TruncateSuffixAsync_BeyondTheJournal_ChangesNothing()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using var store = await OpenAsync(fileSystem);
        await store.AppendAsync([LogTestData.Command(1)], CancellationToken.None);

        await store.TruncateSuffixAsync(new LogIndex(5), CancellationToken.None);

        store.LastIndex.Value.Should().Be(1);
        RecordsAt(fileSystem, 1).Should().ContainSingle();
    }

    [Fact]
    public async Task MarkCommittedAsync_RejectsBackwardsAndBeyondTheJournal()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using var store = await OpenAsync(fileSystem);
        await store.AppendAsync([LogTestData.Command(1), LogTestData.Command(2)], CancellationToken.None);
        await store.MarkCommittedAsync(new LogIndex(2), CancellationToken.None);

        var backwards = () => store.MarkCommittedAsync(new LogIndex(1), CancellationToken.None).AsTask();
        var beyond = () => store.MarkCommittedAsync(new LogIndex(3), CancellationToken.None).AsTask();

        await backwards.Should().ThrowAsync<InvalidOperationException>();
        await beyond.Should().ThrowAsync<ArgumentOutOfRangeException>();
        store.CommittedIndex.Value.Should().Be(2);
    }

    [Fact]
    public async Task CompactAsync_RemovesSealedSegmentsCoveredByTheSnapshot()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using var store = await OpenAsync(fileSystem, SegmentWithTwoFrames);
        await store.AppendAsync(
            [LogTestData.Command(1), LogTestData.Command(2), LogTestData.Command(3), LogTestData.Command(4)],
            CancellationToken.None);

        await store.CompactAsync(new LogIndex(2), CancellationToken.None);

        // Последний сегмент не удаляется никогда: он продолжает журнал (§9.6).
        fileSystem.Paths.Should().Equal(LogTestData.SegmentPath(3));
        store.FirstIndex.Value.Should().Be(3);
        store.LastIndex.Value.Should().Be(4);
        (await store.GetAsync(new LogIndex(1), CancellationToken.None)).Status.Should().Be(LogLookupStatus.Compacted);
    }

    [Fact]
    public async Task CompactAsync_KeepsTheLastSegmentEvenWhenItIsFullyCovered()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using var store = await OpenAsync(fileSystem);
        await store.AppendAsync([LogTestData.Command(1), LogTestData.Command(2)], CancellationToken.None);

        await store.CompactAsync(new LogIndex(2), CancellationToken.None);

        fileSystem.Paths.Should().Equal(LogTestData.SegmentPath(1));
        store.FirstIndex.Value.Should().Be(3);
        store.LastIndex.Value.Should().Be(2);
    }

    [Fact]
    public async Task CompactAsync_RejectsBackwardsAndBeyondTheJournal()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using var store = await OpenAsync(fileSystem);
        await store.AppendAsync([LogTestData.Command(1), LogTestData.Command(2)], CancellationToken.None);
        await store.CompactAsync(new LogIndex(1), CancellationToken.None);

        var backwards = () => store.CompactAsync(new LogIndex(0), CancellationToken.None).AsTask();
        var beyond = () => store.CompactAsync(new LogIndex(3), CancellationToken.None).AsTask();

        await backwards.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await beyond.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task FlushToDiskAsync_OnAnEmptyJournal_TouchesNothing()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using var store = await OpenAsync(fileSystem);

        await store.FlushToDiskAsync(CancellationToken.None);

        fileSystem.Operations.Should().BeEmpty();
    }

    [Fact]
    public async Task PeriodicMode_BecomesDurableOnlyAfterFlushToDisk()
    {
        // Политика Periodic не вызывает fsync сама: долговечность даёт только явный сброс.
        var fileSystem = new FakeStorageFileSystem();
        await using var store = await OpenAsync(fileSystem);

        await store.AppendAsync([LogTestData.Command(1)], DurabilityMode.Periodic, CancellationToken.None);
        fileSystem.FlushToDiskCount.Should().Be(1, "fsync заголовка нового сегмента обязателен (§9.3)");

        await store.FlushToDiskAsync(CancellationToken.None);
        fileSystem.FlushToDiskCount.Should().Be(2);
    }

    [Fact]
    public async Task OpenAsync_ReportsCorruptCatalogWithoutHoldingReaders()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using (var store = await OpenAsync(fileSystem, SegmentWithTwoFrames))
        {
            await store.AppendAsync(
                [LogTestData.Command(1), LogTestData.Command(2), LogTestData.Command(3), LogTestData.Command(4)],
                CancellationToken.None);
        }

        var bytes = fileSystem.ReadAllBytes(LogTestData.SegmentPath(1));
        fileSystem.Seed(LogTestData.SegmentPath(1), bytes.AsSpan(0, bytes.Length - 3));

        var open = await LogStore.OpenAsync(fileSystem, Clock, Options(SegmentWithTwoFrames), CancellationToken.None);

        open.Status.Should().Be(LogReadStatus.Corrupt);
        open.Store.Should().BeNull();
        fileSystem.OpenReadFileCount.Should().Be(0);
    }

    [Fact]
    public async Task OpenAsync_RepairsATruncatedLastSegmentWithoutHoldingReaders()
    {
        // Ремонт оборванного хвоста - дело старта (S1-T07): наружу журнал уже приходит целым,
        // а не как статус «нужно восстановление». Отчёт при этом не молчит.
        var fileSystem = new FakeStorageFileSystem();
        await using (var store = await OpenAsync(fileSystem))
        {
            await store.AppendAsync([LogTestData.Command(1)], CancellationToken.None);
        }

        var bytes = fileSystem.ReadAllBytes(LogTestData.SegmentPath(1));
        fileSystem.Seed(LogTestData.SegmentPath(1), bytes.AsSpan(0, bytes.Length - 3));

        var open = await LogStore.OpenAsync(fileSystem, Clock, Options(), CancellationToken.None);

        open.IsSuccess.Should().BeTrue(open.Reason);
        open.Report.Repaired.Should().BeTrue();
        fileSystem.OpenReadFileCount.Should().Be(0, "восстановление не задерживает ни одного читателя");
    }

    [Fact]
    public async Task OpenAsync_RejectsNonContinuousCatalog()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using (var store = await OpenAsync(fileSystem))
        {
            await store.AppendAsync([LogTestData.Command(1), LogTestData.Command(2)], CancellationToken.None);
        }

        fileSystem.Seed(LogTestData.SegmentPath(9), LogTestData.HeaderOnlySegment(9));

        var open = await LogStore.OpenAsync(fileSystem, Clock, Options(), CancellationToken.None);

        open.Status.Should().Be(LogReadStatus.Corrupt);
        open.Path.Should().Be(LogTestData.SegmentPath(9));
    }

    [Fact]
    public async Task DisposedStore_RefusesToWork()
    {
        var fileSystem = new FakeStorageFileSystem();
        var store = await OpenAsync(fileSystem);
        await store.AppendAsync([LogTestData.Command(1)], CancellationToken.None);
        await store.DisposeAsync();

        var append = () => store.AppendAsync([LogTestData.Command(2)], CancellationToken.None).AsTask();
        var get = () => store.GetAsync(new LogIndex(1), CancellationToken.None).AsTask();

        await append.Should().ThrowAsync<ObjectDisposedException>();
        await get.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task Append_RejectsUnknownDurabilityMode()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using var store = await OpenAsync(fileSystem);

        var append = () => store.AppendAsync([LogTestData.Command(1)], (DurabilityMode)42, CancellationToken.None).AsTask();

        await append.Should().ThrowAsync<ArgumentOutOfRangeException>();
        fileSystem.Operations.Should().BeEmpty();
    }

    [Fact]
    public async Task Append_WithAnEmptyBatch_ChangesNothing()
    {
        // Пустая партия - законный no-op: планировщик вправе ничего не собрать.
        var fileSystem = new FakeStorageFileSystem();
        await using var store = await OpenAsync(fileSystem);

        await store.AppendAsync([], CancellationToken.None);

        fileSystem.Operations.Should().BeEmpty();
        store.LastIndex.Value.Should().Be(0);
    }

    [Fact]
    public async Task EmptyJournal_AnswersReadsWithoutTouchingTheDisk()
    {
        // Чтение из пустого журнала - штатный случай на старте лидера без истории,
        // а не ошибка: ответ «записи ещё нет», и ни одного открытия файла.
        var fileSystem = new FakeStorageFileSystem();
        await using var store = await OpenAsync(fileSystem);

        (await store.GetAsync(new LogIndex(1), CancellationToken.None)).Status.Should().Be(LogLookupStatus.NotYet);
        (await store.ReadRangeAsync(new LogIndex(1), maxCount: 10, CancellationToken.None)).Should().BeEmpty();

        fileSystem.Operations.Should().BeEmpty();
    }

    [Fact]
    public async Task TruncateSuffixAsync_RemovesWholeSegmentsAboveTheCut()
    {
        // Три сегмента: 1 (1,2), 3 (3,4), 5 (5,6). Обрезка с 3 убирает и 5, и 3 - файлов
        // выше точки обрезки остаться не должно, иначе следующий append создаст файл поверх.
        var fileSystem = new FakeStorageFileSystem();
        await using var store = await OpenAsync(fileSystem, SegmentWithTwoFrames);
        await store.AppendAsync(
            [
                LogTestData.Command(1),
                LogTestData.Command(2),
                LogTestData.Command(3),
                LogTestData.Command(4),
                LogTestData.Command(5),
                LogTestData.Command(6),
            ],
            CancellationToken.None);

        fileSystem.Paths.Should().Equal(
            LogTestData.SegmentPath(1),
            LogTestData.SegmentPath(3),
            LogTestData.SegmentPath(5));

        await store.TruncateSuffixAsync(new LogIndex(3), CancellationToken.None);

        fileSystem.Paths.Should().Equal(LogTestData.SegmentPath(1));
        store.LastIndex.Value.Should().Be(2);

        await store.AppendAsync([LogTestData.Command(3), LogTestData.Command(4)], CancellationToken.None);

        fileSystem.Paths.Should().Equal(LogTestData.SegmentPath(1), LogTestData.SegmentPath(3));
        RecordsAt(fileSystem, 3).Select(record => record.Index.Value).Should().Equal(3, 4);
    }

    [Fact]
    public async Task TruncateSuffixAsync_DownToTheFirstRecord_LeavesAnEmptyJournalThatCanGrowAgain()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using var store = await OpenAsync(fileSystem, SegmentWithTwoFrames);
        await store.AppendAsync(
            [LogTestData.Command(1), LogTestData.Command(2), LogTestData.Command(3), LogTestData.Command(4)],
            CancellationToken.None);

        await store.TruncateSuffixAsync(new LogIndex(1), CancellationToken.None);

        // Журнал пуст, но это не «нет файлов»: писать снова можно с индекса 1.
        fileSystem.Paths.Should().BeEmpty();
        store.LastIndex.Value.Should().Be(0);
        store.LastTerm.Value.Should().Be(0);
        store.FirstIndex.Value.Should().Be(1);

        await store.AppendAsync([LogTestData.Command(1)], CancellationToken.None);

        fileSystem.Paths.Should().Equal(LogTestData.SegmentPath(1));
        store.LastIndex.Value.Should().Be(1);
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var fileSystem = new FakeStorageFileSystem();
        var store = await OpenAsync(fileSystem);

        await store.DisposeAsync();
        var second = () => store.DisposeAsync().AsTask();

        await second.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Read_AfterTheSegmentWasReplacedOnDisk_ThrowsInsteadOfReportingNoRecord()
    {
        // Порча после успешного старта - не «записи нет»: иначе испорченный диск выглядел бы
        // как обычное отставание реплики, и узел продолжил бы работу с дырой в журнале.
        var fileSystem = new FakeStorageFileSystem();
        await using var store = await OpenAsync(fileSystem);
        await store.AppendAsync([LogTestData.Command(1)], CancellationToken.None);
        fileSystem.Seed(LogTestData.SegmentPath(1), [1, 2, 3]);

        var get = () => store.GetAsync(new LogIndex(1), CancellationToken.None).AsTask();

        await get.Should().ThrowAsync<InvalidDataException>();
        fileSystem.OpenReadFileCount.Should().Be(0, "читатель закрывается даже когда отказ бросает исключение");
    }

    [Fact]
    public async Task Read_AfterTheTailWasCutOnDisk_ThrowsInsteadOfReturningAShortRange()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using var store = await OpenAsync(fileSystem);
        await store.AppendAsync([LogTestData.Command(1), LogTestData.Command(2)], CancellationToken.None);
        var bytes = fileSystem.ReadAllBytes(LogTestData.SegmentPath(1));
        fileSystem.Seed(LogTestData.SegmentPath(1), bytes.AsSpan(0, bytes.Length - 3));

        var read = () => store.ReadRangeAsync(new LogIndex(1), maxCount: 10, CancellationToken.None).AsTask();

        await read.Should().ThrowAsync<InvalidDataException>();
        fileSystem.OpenReadFileCount.Should().Be(0);
    }

    [Fact]
    public async Task Read_WhenTheSegmentFailsDuringScanning_ClosesTheReaderAndRethrows()
    {
        // Отказ ввода-вывода посреди прохода не должен оставлять открытый дескриптор:
        // иначе восстановление (<c>S1-T07</c>) не смогло бы усечь сегмент на Windows.
        var fileSystem = new FakeStorageFileSystem();
        await using var store = await OpenAsync(fileSystem);
        await store.AppendAsync([LogTestData.Command(1), LogTestData.Command(2)], CancellationToken.None);

        var reads = 0;
        fileSystem.FailureInjector = operation =>
        {
            if (operation.Kind != StorageOperationKind.Read)
            {
                return null;
            }

            reads++;

            // Первое чтение - заголовок (открытие), второе - первый фрейм (сканирование).
            return reads == 2 ? new IOException("устройство отвалилось") : null;
        };

        var get = () => store.GetAsync(new LogIndex(1), CancellationToken.None).AsTask();

        await get.Should().ThrowAsync<IOException>();
        fileSystem.OpenReadFileCount.Should().Be(0);
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
        var open = await LogStore.OpenAsync(
            fileSystem,
            new TestClock(startMs: LogTestData.MonotonicStartMs, unixTimeMs: LogTestData.NowMs),
            Options(maxSegmentSize),
            CancellationToken.None);

        open.IsSuccess.Should().BeTrue(open.Reason);

        return open.Store!;
    }

    private static List<LogRecord> RecordsAt(FakeStorageFileSystem fileSystem, long baseIndex)
        => LogTestData.ReadSegment(fileSystem, baseIndex);
}
