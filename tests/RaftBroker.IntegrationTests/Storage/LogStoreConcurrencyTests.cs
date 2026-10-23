using System.Globalization;
using RaftBroker.Core.Primitives;
using RaftBroker.Core.Time;
using RaftBroker.Storage.IO;
using RaftBroker.Storage.Log;
using RaftBroker.TestKit.IO;

// Тест держит собственный источник отмены (стресс и остановка читателей): подсказка анализатора
// "возьмите токен теста" здесь не применима.
#pragma warning disable xUnit1051

namespace RaftBroker.IntegrationTests.Storage;

/// <summary>
/// Конкурентность журнала (<c>S1-T08</c>): мутации идут через одни воротца, чтения их не ждут.
/// </summary>
/// <remarks>
/// <para>
/// Проверяются два утверждения дизайна (ADR <c>0011-log-store-concurrency.md</c>), которых не
/// видно из юнит-тестов на одиночных вызовах: (1) читатель не блокируется писателем, который уже
/// внутри записи, и (2) при неудачной мутации воротца отпускаются - иначе первый же отказ
/// превратил бы журнал в вечно занятый.
/// </para>
/// <para>
/// Первое утверждение проверяется детерминированно: файловая система-обёртка держит запись на
/// сигнале, поэтому "писатель внутри, читатель снаружи" - это состояние теста, а не гонка,
/// которая иногда складывается нужным образом.
/// </para>
/// </remarks>
public sealed class LogStoreConcurrencyTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>Payload фиксированной длины: содержимое в этих тестах не важно, важны индексы.</summary>
    private static readonly ReadOnlyMemory<byte> FourBytes = new byte[] { 1, 2, 3, 4 };

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "raft-broker-concurrency-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    [Fact]
    public async Task ReadWhileAppendIsInFlight_DoesNotWaitForTheWriter()
    {
        var fileSystem = new BlockingWriteFileSystem();
        var open = await LogStore.OpenAsync(fileSystem, SystemClock.Instance, Options(), CancellationToken.None);

        open.IsSuccess.Should().BeTrue(open.Reason);
        await using var store = open.Store!;

        await store.AppendAsync([LogRecord.Command(Index(1), new Term(1), FourBytes)], CancellationToken.None);

        fileSystem.BlockWrites();
        var appending = store.AppendAsync([LogRecord.Command(Index(2), new Term(1), FourBytes)], CancellationToken.None).AsTask();

        await fileSystem.WriteStarted.WaitAsync(Patience);

        // Писатель стоит внутри записи: здесь и проверяется, что чтение не берёт воротца.
        var lookup = await store.GetAsync(Index(1), CancellationToken.None).AsTask().WaitAsync(Patience);

        lookup.Status.Should().Be(LogLookupStatus.Found);
        lookup.Record!.Index.Should().Be(Index(1));
        store.LastIndex.Should().Be(Index(1), "снимок каталога меняется только после успешной записи");

        fileSystem.ReleaseWrites();

        await appending.WaitAsync(Patience);

        store.LastIndex.Should().Be(Index(2));
    }

    [Fact]
    public async Task FailedAppend_ReleasesTheGate()
    {
        var fileSystem = new FakeStorageFileSystem();
        var open = await LogStore.OpenAsync(fileSystem, SystemClock.Instance, Options(), CancellationToken.None);

        open.IsSuccess.Should().BeTrue(open.Reason);
        await using var store = open.Store!;

        await store.AppendAsync([LogRecord.Command(Index(1), new Term(1), FourBytes)], CancellationToken.None);

        var hole = async () => await store.AppendAsync([LogRecord.Command(Index(5), new Term(1), FourBytes)], CancellationToken.None);
        await hole.Should().ThrowAsync<InvalidOperationException>();

        // Отказ обязан оставить воротца свободными: следующий append идёт без ожидания.
        await store.AppendAsync([LogRecord.Command(Index(2), new Term(1), FourBytes)], CancellationToken.None).AsTask().WaitAsync(Patience);

        store.LastIndex.Should().Be(Index(2));
    }

    [Fact]
    public async Task ConcurrentReadersAndMaintenance_SeeAContinuousLog()
    {
        const int total = 120;

        var open = await LogStore.OpenAsync(SystemStorageFileSystem.Instance, SystemClock.Instance, Options(), CancellationToken.None);

        open.IsSuccess.Should().BeTrue(open.Reason);
        await using var store = open.Store!;

        using var stop = new CancellationTokenSource();
        var failures = new List<Exception>();

        var readers = Enumerable.Range(0, 3).Select(reader => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var records = await store.ReadRangeAsync(Index(1), 40, stop.Token);

                    // Каждое чтение - непрерывный префикс: индекс в ответе растёт на единицу.
                    for (var position = 1; position < records.Count; position++)
                    {
                        if (records[position].Index.Value != records[position - 1].Index.Value + 1)
                        {
                            throw new InvalidOperationException($"разрыв в выдаче чтения: {records[position - 1].Index} и {records[position].Index}");
                        }
                    }

                    var tail = await store.GetAsync(store.LastIndex == LogIndex.Empty ? Index(1) : store.LastIndex, stop.Token);
                    tail.Status.Should().BeOneOf(LogLookupStatus.Found, LogLookupStatus.NotYet);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception exception)
                {
                    lock (failures)
                    {
                        failures.Add(exception);
                    }

                    return;
                }
            }
        })).ToArray();

        var maintenance = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    // Те же воротца, что у append: flush и отметка коммита не должны ни обгонять
                    // запись, ни мешать читателям.
                    await store.FlushToDiskAsync(stop.Token);
                    await store.MarkCommittedAsync(store.LastIndex, stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        });

        for (var index = 1; index <= total; index++)
        {
            await store.AppendAsync([LogRecord.Command(Index(index), new Term(1), FourBytes)], CancellationToken.None);
        }

        // Последняя отметка коммита - из теста: обслуживание могло не успеть до неё.
        await store.MarkCommittedAsync(Index(total), CancellationToken.None);
        await stop.CancelAsync();
        await Task.WhenAll([.. readers, maintenance]);

        failures.Should().BeEmpty();
        store.LastIndex.Should().Be(Index(total));
        store.CommittedIndex.Should().Be(Index(total));

        var all = await store.ReadRangeAsync(Index(1), total + 10, CancellationToken.None);

        all.Should().HaveCount(total);
        all[^1].Index.Should().Be(Index(total));

        // Журнал закрывается и открывается заново: воротца не оставили полузаписанного сегмента.
        await store.DisposeAsync();

        var reopened = await LogStore.OpenAsync(SystemStorageFileSystem.Instance, SystemClock.Instance, Options(), CancellationToken.None);
        reopened.IsSuccess.Should().BeTrue(reopened.Reason);
        await using var second = reopened.Store!;

        second.LastIndex.Should().Be(Index(total));
        reopened.Report.Repaired.Should().BeFalse();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static LogIndex Index(long value) => new(value);

    private SegmentWriterOptions Options()
        => new()
        {
            Directory = _directory,

            // Маленький сегмент: rollover попадает внутрь параллельной работы, а не после неё.
            MaxSegmentSize = 1024,
        };

    /// <summary>Файловая система, которая умеет остановить запись на сигнале теста.</summary>
    /// <remarks>
    /// Нужна ровно для одного утверждения: пока писатель внутри записи, чтение обязано отвечать.
    /// Всё остальное делегируется <see cref="FakeStorageFileSystem"/>, поэтому поведение файлов
    /// остаётся прежним.
    /// </remarks>
    private sealed class BlockingWriteFileSystem : IStorageFileSystem
    {
        private readonly FakeStorageFileSystem _inner = new();
        private readonly TaskCompletionSource _writeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private volatile bool _block;

        public Task WriteStarted => _writeStarted.Task;

        public void BlockWrites() => _block = true;

        public void ReleaseWrites() => _release.TrySetResult();

        public ValueTask<IStorageFile> CreateAsync(string path, CancellationToken cancellationToken)
            => WrapAsync(_inner.CreateAsync(path, cancellationToken));

        public ValueTask<IStorageReadFile> OpenReadAsync(string path, CancellationToken cancellationToken)
            => _inner.OpenReadAsync(path, cancellationToken);

        public ValueTask<IStorageFile> OpenAppendAsync(string path, CancellationToken cancellationToken)
            => WrapAsync(_inner.OpenAppendAsync(path, cancellationToken));

        public ValueTask<IReadOnlyList<string>> ListFilesAsync(string directory, CancellationToken cancellationToken)
            => _inner.ListFilesAsync(directory, cancellationToken);

        public ValueTask TruncateAsync(string path, long length, CancellationToken cancellationToken)
            => _inner.TruncateAsync(path, length, cancellationToken);

        public ValueTask DeleteAsync(string path, CancellationToken cancellationToken)
            => _inner.DeleteAsync(path, cancellationToken);

        public ValueTask RenameAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
            => _inner.RenameAsync(sourcePath, destinationPath, cancellationToken);

        public ValueTask FlushDirectoryAsync(string directory, CancellationToken cancellationToken)
            => _inner.FlushDirectoryAsync(directory, cancellationToken);

        private async ValueTask<IStorageFile> WrapAsync(ValueTask<IStorageFile> pending)
            => new GatedFile(await pending.ConfigureAwait(false), this);

        private sealed class GatedFile(IStorageFile inner, BlockingWriteFileSystem owner) : IStorageFile
        {
            public string Path => inner.Path;

            public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
            {
                if (owner._block)
                {
                    owner._writeStarted.TrySetResult();
                    await owner._release.Task.ConfigureAwait(false);
                }

                await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            }

            public ValueTask FlushToDiskAsync(CancellationToken cancellationToken)
                => inner.FlushToDiskAsync(cancellationToken);

            public ValueTask DisposeAsync() => inner.DisposeAsync();

            public ValueTask TruncateAsync(long length, CancellationToken cancellationToken)
                => inner.TruncateAsync(length, cancellationToken);
        }
    }
}
