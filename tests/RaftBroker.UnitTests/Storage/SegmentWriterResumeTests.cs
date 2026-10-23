using RaftBroker.Core.Primitives;
using RaftBroker.Storage.Log;
using RaftBroker.TestKit.IO;
using RaftBroker.TestKit.Time;

namespace RaftBroker.UnitTests.Storage;

/// <summary>
/// S1-T06: продолжение существующего сегмента и отказ после неудачного rollover.
/// </summary>
/// <remarks>
/// Проверяется то, чего нет у только что созданного писателя: унаследованный пустой сегмент
/// (§8) и сегмент, который не удалось открыть во время rollover. Обе ветки ведут к тихой потере
/// подтверждённых записей, если их не проверить: первая пишет запись не с тем индексом, вторая
/// оставляет журнал без активного файла.
/// </remarks>
public sealed class SegmentWriterResumeTests
{
    /// <summary>В сегмент помещаются заголовок и два фрейма, но не три.</summary>
    private const int SegmentWithTwoFrames = 100;

    [Fact]
    public async Task ResumeEmptySegment_FirstRecordMustTakeTheSegmentBaseIndex()
    {
        // Пустой последний сегмент с базовым индексом 1 - штатный файл (§8): журнал пуст,
        // а сегмент уже создан. Первая запись обязана попасть в индекс 1, иначе сегмент
        // начнётся не с той записи, что записано в его заголовке, и перестанет читаться.
        var fileSystem = new FakeStorageFileSystem();
        fileSystem.Seed(LogTestData.SegmentPath(1), LogTestData.HeaderOnlySegment(1));
        await using var writer = await ResumeAsync(fileSystem, baseIndex: 1, lastIndex: 0, lastTerm: 0);

        var append = () => writer.AppendAsync([LogTestData.Command(2)], CancellationToken.None).AsTask();

        await append.Should().ThrowAsync<InvalidOperationException>();
        writer.LastIndex.Should().BeNull();
        LogTestData.ReadSegment(fileSystem, 1).Should().BeEmpty();
    }

    [Fact]
    public async Task ResumeEmptySegment_AcceptsTheBaseIndexItself()
    {
        var fileSystem = new FakeStorageFileSystem();
        fileSystem.Seed(LogTestData.SegmentPath(1), LogTestData.HeaderOnlySegment(1));
        await using var writer = await ResumeAsync(fileSystem, baseIndex: 1, lastIndex: 0, lastTerm: 0);

        await writer.AppendAsync([LogTestData.Command(1)], CancellationToken.None);

        LogTestData.ReadSegment(fileSystem, 1).Select(record => record.Index.Value).Should().Equal(1);
        writer.LastIndex!.Value.Value.Should().Be(1);
    }

    [Fact]
    public async Task ResumeEmptySegmentAfterSnapshot_ContinuesAfterTheLastRecord()
    {
        // Журнал начат со снапшота: сегмент с базовым индексом 5, записей ещё нет.
        var fileSystem = new FakeStorageFileSystem();
        fileSystem.Seed(LogTestData.SegmentPath(5), LogTestData.HeaderOnlySegment(5));
        await using var writer = await ResumeAsync(fileSystem, baseIndex: 5, lastIndex: 4, lastTerm: 3);

        await writer.AppendAsync([LogTestData.Command(5, term: 3)], CancellationToken.None);

        LogTestData.ReadSegment(fileSystem, 5).Select(record => record.Index.Value).Should().Equal(5);

        var append = () => writer.AppendAsync([LogTestData.Command(7, term: 3)], CancellationToken.None).AsTask();

        await append.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ResumeNonEmptySegment_ContinuesAfterItsLastRecord()
    {
        var fileSystem = new FakeStorageFileSystem();
        await LogTestData.WriteSegmentAsync(fileSystem, LogTestData.Command(1), LogTestData.Command(2, term: 2));
        await using var writer = await ResumeAsync(fileSystem, baseIndex: 1, lastIndex: 2, lastTerm: 2);

        await writer.AppendAsync([LogTestData.Command(3, term: 2)], CancellationToken.None);

        LogTestData.ReadSegment(fileSystem, 1).Select(record => record.Index.Value).Should().Equal(1, 2, 3);
        writer.CurrentSegmentBaseIndex!.Value.Value.Should().Be(1);
    }

    [Fact]
    public async Task ResumeNonEmptySegment_RejectsLowerTerm()
    {
        var fileSystem = new FakeStorageFileSystem();
        await LogTestData.WriteSegmentAsync(fileSystem, LogTestData.Command(1), LogTestData.Command(2, term: 5));
        await using var writer = await ResumeAsync(fileSystem, baseIndex: 1, lastIndex: 2, lastTerm: 5);

        var append = () => writer.AppendAsync([LogTestData.Command(3, term: 4)], CancellationToken.None).AsTask();

        await append.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData(0, 0, LogSegmentFormat.HeaderSize)]
    [InlineData(1, 0, LogSegmentFormat.HeaderSize - 1)]
    [InlineData(1, 1, LogSegmentFormat.HeaderSize)]
    [InlineData(5, 3, LogSegmentFormat.HeaderSize)]
    public async Task Resume_RejectsContradictoryState(long baseIndex, long lastIndex, long length)
    {
        // Состояние противоречиво - это ошибка вызывающего, а не догадка писателя:
        // продолжать сегмент, о котором известно, что он не такой, нельзя.
        var fileSystem = new FakeStorageFileSystem();
        var resume = new SegmentResumeState
        {
            BaseIndex = new LogIndex(baseIndex),
            LastIndex = new LogIndex(lastIndex),
            LastTerm = new Term(1),
            Length = length,
        };

        var resumeWriter = () => SegmentWriter.ResumeAsync(
            fileSystem,
            new TestClock(startMs: LogTestData.MonotonicStartMs, unixTimeMs: LogTestData.NowMs),
            WriterOptions(LogSegmentFormat.MaxSegmentSize),
            resume,
            CancellationToken.None).AsTask();

        await resumeWriter.Should().ThrowAsync<ArgumentException>();
        fileSystem.Operations.Should().BeEmpty("противоречивое состояние проверяется до открытия файла");
    }

    [Fact]
    public async Task FailedRollover_MakesTheWriterUnusable()
    {
        // Rollover - три шага (§8): fsync запечатанного сегмента, создание нового файла и fsync
        // его заголовка, запись фрейма. Отказ на втором шаге оставляет журнал без активного
        // сегмента; молча продолжать нельзя - следующая запись попала бы в неизвестно что.
        var fileSystem = new FakeStorageFileSystem();
        await using var writer = LogTestData.CreateWriter(fileSystem, maxSegmentSize: SegmentWithTwoFrames);
        await writer.AppendAsync([LogTestData.Command(1), LogTestData.Command(2)], CancellationToken.None);

        fileSystem.FailureInjector = operation => operation.Kind == StorageOperationKind.Create
            && operation.Path == LogTestData.SegmentPath(3)
                ? new IOException("нет места на устройстве")
                : null;

        var failed = () => writer.AppendAsync([LogTestData.Command(3)], CancellationToken.None).AsTask();

        await failed.Should().ThrowAsync<IOException>();
        writer.LastIndex!.Value.Value.Should().Be(2, "незавершённая запись не подтверждена");

        fileSystem.FailureInjector = null;

        var next = () => writer.AppendAsync([LogTestData.Command(4)], CancellationToken.None).AsTask();

        await next.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*непригоден*");
        fileSystem.Exists(LogTestData.SegmentPath(4)).Should().BeFalse();
    }

    [Fact]
    public async Task CreatedSegmentBaseIndexes_ReportsEveryRolloverAndStartsWithNothing()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using var writer = LogTestData.CreateWriter(fileSystem, maxSegmentSize: SegmentWithTwoFrames);
        writer.CreatedSegmentBaseIndexes.Should().BeEmpty();

        await writer.AppendAsync([LogTestData.Command(1), LogTestData.Command(2)], CancellationToken.None);
        await writer.AppendAsync([LogTestData.Command(3)], CancellationToken.None);

        writer.CreatedSegmentBaseIndexes.Select(index => index.Value).Should().Equal(1, 3);
    }

    [Fact]
    public async Task Resume_DoesNotReportTheInheritedSegmentAsCreated()
    {
        // Унаследованный сегмент знает тот, кто вызвал продолжение: повторно объявлять его
        // созданным значило бы добавить его в каталог второй раз.
        var fileSystem = new FakeStorageFileSystem();
        fileSystem.Seed(LogTestData.SegmentPath(1), LogTestData.HeaderOnlySegment(1));
        await using var writer = await ResumeAsync(fileSystem, baseIndex: 1, lastIndex: 0, lastTerm: 0);

        await writer.AppendAsync([LogTestData.Command(1)], CancellationToken.None);

        writer.CreatedSegmentBaseIndexes.Should().BeEmpty();
        writer.CurrentSegmentBaseIndex!.Value.Value.Should().Be(1);
    }

    [Fact]
    public void MaxSegmentSizeAboveIntMaxValue_IsRejected()
    {
        // Читатель адресует смещения фреймов 32-битным числом (S1-T04): сегмент длиннее
        // 2 ГиБ дал бы отрицательные смещения вместо порчи формата.
        var fileSystem = new FakeStorageFileSystem();

        var create = () => new SegmentWriter(
            fileSystem,
            new TestClock(startMs: LogTestData.MonotonicStartMs, unixTimeMs: LogTestData.NowMs),
            WriterOptions((long)int.MaxValue + 1));

        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Append_RejectsUnknownDurabilityModeEvenForEmptyBatch()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using var writer = LogTestData.CreateWriter(fileSystem);

        var append = () => writer.AppendAsync([], (DurabilityMode)42, CancellationToken.None).AsTask();

        await append.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Append_RejectsNullRecordWithoutTouchingTheDisk()
    {
        var fileSystem = new FakeStorageFileSystem();
        await using var writer = LogTestData.CreateWriter(fileSystem);

        var append = () => writer.AppendAsync([null!], CancellationToken.None).AsTask();

        await append.Should().ThrowAsync<ArgumentNullException>();
        fileSystem.Operations.Should().BeEmpty();
    }

    [Fact]
    public async Task FailedFrameWrite_RollsBackToTheLastWholeBoundary()
    {
        // Оборванная запись оставила в конце сегмента мусор: без отката следующая запись
        // дописалась бы после мусора, и весь сегмент перестал бы читаться (§9.4).
        var fileSystem = new FakeStorageFileSystem();
        await using var writer = LogTestData.CreateWriter(fileSystem);
        var writes = 0;

        fileSystem.FailureInjector = operation =>
        {
            if (operation.Kind != StorageOperationKind.Write)
            {
                return null;
            }

            writes++;

            // Первая запись - заголовок сегмента, вторая - фрейм.
            return writes == 2 ? new IOException("обрыв на записи фрейма") : null;
        };

        var failed = () => writer.AppendAsync([LogTestData.Command(1)], CancellationToken.None).AsTask();

        await failed.Should().ThrowAsync<IOException>();
        writer.LastIndex.Should().BeNull();
        fileSystem.ReadAllBytes(LogTestData.SegmentPath(1)).Should().HaveCount(LogSegmentFormat.HeaderSize);
        fileSystem.Operations.Should().Contain(operation =>
            operation.Kind == StorageOperationKind.Truncate && operation.ByteCount == LogSegmentFormat.HeaderSize);

        fileSystem.FailureInjector = null;

        // Сегмент цел, писатель пригоден: та же запись проходит со второй попытки.
        await writer.AppendAsync([LogTestData.Command(1)], CancellationToken.None);

        writer.LastIndex!.Value.Value.Should().Be(1);
        LogTestData.ReadSegment(fileSystem, 1).Select(record => record.Index.Value).Should().Equal(1);
    }

    [Fact]
    public async Task FailedRollback_MakesTheWriterUnusable()
    {
        // Откат не удался - в сегменте остался мусор. Молча продолжать нельзя: следующая
        // запись дописалась бы после него.
        var fileSystem = new FakeStorageFileSystem();
        await using var writer = LogTestData.CreateWriter(fileSystem);
        var writes = 0;

        fileSystem.FailureInjector = operation => operation.Kind switch
        {
            StorageOperationKind.Write when ++writes == 2 => new IOException("обрыв на записи фрейма"),
            StorageOperationKind.Truncate => new IOException("обрыв на откате"),
            _ => null,
        };

        var failed = () => writer.AppendAsync([LogTestData.Command(1)], CancellationToken.None).AsTask();

        await failed.Should().ThrowAsync<IOException>();

        fileSystem.FailureInjector = null;

        var next = () => writer.AppendAsync([LogTestData.Command(1)], CancellationToken.None).AsTask();

        await next.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*непригоден*");
    }

    [Fact]
    public async Task FailedHeaderWrite_LeavesNoWriterBehind()
    {
        // Заголовок сегмента пишется и fsync-ается до первого фрейма (§9.3): отказ здесь
        // означает, что сегмента нет, и следующий append обязан начать с создания файла.
        var fileSystem = new FakeStorageFileSystem();
        await using var writer = LogTestData.CreateWriter(fileSystem);

        fileSystem.FailureInjector = operation => operation.Kind == StorageOperationKind.Write
            ? new IOException("обрыв на заголовке")
            : null;

        var failed = () => writer.AppendAsync([LogTestData.Command(1)], CancellationToken.None).AsTask();

        await failed.Should().ThrowAsync<IOException>();
        writer.CurrentSegmentPath.Should().BeNull("писателю нечего продолжать");
        writer.LastIndex.Should().BeNull();
    }

    private static SegmentWriterOptions WriterOptions(long maxSegmentSize)
        => new()
        {
            Directory = LogTestData.Directory,
            MaxSegmentSize = maxSegmentSize,
        };

    /// <summary>Продолжает существующий сегмент с его настоящей длиной.</summary>
    private static ValueTask<SegmentWriter> ResumeAsync(
        FakeStorageFileSystem fileSystem,
        long baseIndex,
        long lastIndex,
        long lastTerm)
    {
        var path = LogTestData.SegmentPath(baseIndex);

        return SegmentWriter.ResumeAsync(
            fileSystem,
            new TestClock(startMs: LogTestData.MonotonicStartMs, unixTimeMs: LogTestData.NowMs),
            WriterOptions(LogSegmentFormat.MaxSegmentSize),
            new SegmentResumeState
            {
                BaseIndex = new LogIndex(baseIndex),
                LastIndex = new LogIndex(lastIndex),
                LastTerm = new Term(lastTerm),
                Length = fileSystem.ReadAllBytes(path).Length,
            },
            CancellationToken.None);
    }
}
