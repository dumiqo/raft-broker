using System.Globalization;
using RaftBroker.Core.Primitives;
using RaftBroker.Storage.IO;
using RaftBroker.Storage.Snapshot;
using RaftBroker.TestKit.IO;
using RaftBroker.TestKit.Time;

namespace RaftBroker.IntegrationTests.Storage;

/// <summary>
/// Снапшот при крахе между шагами публикации (<c>S1-T09</c>).
/// </summary>
/// <remarks>
/// <para>
/// Инвариант один: в каталоге либо есть целый снапшот, либо нет снапшота вовсе. Модель на
/// <see cref="FakeStorageFileSystem"/> перебирает все шесть точек отказа записи, а настоящий
/// <c>Kill</c> процесса проверяет, что обещание не держится на честном слове <c>File.Move</c>.
/// </para>
/// <para>
/// Осиротевший <c>.tmp</c> - законный след краха (§7): он не читается никогда и удаляется при
/// следующем обращении к каталогу, а не считается «почти снапшотом».
/// </para>
/// </remarks>
public sealed class SnapshotCrashTests : IDisposable
{
    private static readonly SnapshotMetadata Target = new(new LogIndex(7), new Term(3));

    private static readonly byte[] State = [10, 20, 30, 40, 50];

    private static readonly TimeSpan MarkerTimeout = TimeSpan.FromSeconds(60);

    private const string Directory = "snapshots";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "raft-broker-snapshot-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    [Theory]
    [InlineData(StorageOperationKind.Create, 1)]
    [InlineData(StorageOperationKind.Write, 1)]
    [InlineData(StorageOperationKind.Write, 2)]
    [InlineData(StorageOperationKind.FlushToDisk, 1)]
    [InlineData(StorageOperationKind.Rename, 1)]
    public async Task CrashBeforePublication_LeavesNoSnapshotAndTheTempIsCleanedUp(
        StorageOperationKind kind,
        int occurrence)
    {
        // Arrange: падение ровно на указанной операции записи снапшота.
        var fileSystem = new FakeStorageFileSystem();
        var remaining = occurrence;

        fileSystem.FailureInjector = operation =>
            operation.Kind == kind && --remaining == 0
                ? new IOException($"Обрыв на операции {kind}.")
                : null;

        var store = CreateStore(fileSystem);

        // Act
        await Assert.ThrowsAsync<IOException>(
            async () => await store.WriteAsync(Target, State, TestContext.Current.CancellationToken));

        // Assert: опубликованного снапшота нет.
        Assert.False(fileSystem.Exists(Path.Combine(Directory, Target.FileName)));

        // Следующий старт: снапшота по-прежнему нет, а недописанный временный файл убран.
        fileSystem.FailureInjector = null;
        var afterRestart = CreateStore(fileSystem);

        Assert.Null(await afterRestart.GetLatestAsync(TestContext.Current.CancellationToken));
        Assert.False(fileSystem.Exists(Path.Combine(Directory, SnapshotFormat.GetTempFileName(Target.LastIncludedIndex))));
    }

    [Fact]
    public async Task CrashWhileFlushingTheDirectory_LeavesAWholePublishedSnapshot()
    {
        // Arrange: переименование уже прошло - снапшот опубликован, отказ сброса каталога
        // не должен превращать целый файл в «снапшота нет».
        var fileSystem = new FakeStorageFileSystem();

        fileSystem.FailureInjector = operation =>
            operation.Kind == StorageOperationKind.FlushDirectory
                ? new IOException("Обрыв на сбросе каталога.")
                : null;

        var store = CreateStore(fileSystem);

        // Act
        await Assert.ThrowsAsync<IOException>(
            async () => await store.WriteAsync(Target, State, TestContext.Current.CancellationToken));

        // Assert
        fileSystem.FailureInjector = null;
        var afterRestart = CreateStore(fileSystem);

        Assert.Equal(Target, await afterRestart.GetLatestAsync(TestContext.Current.CancellationToken));
        Assert.Equal(State, (await afterRestart.ReadStateAsync(Target, TestContext.Current.CancellationToken)).ToArray());
    }

    [Fact]
    public async Task Write_OnTheRealFileSystem_RoundTripsAndLeavesNoTempFile()
    {
        // Arrange: настоящий FileStream - проверка File.Move без замены и отсутствия .tmp после записи.
        var store = new SnapshotStore(
            SystemStorageFileSystem.Instance,
            new TestClock(unixTimeMs: 1_700_000_000_000),
            _directory);

        // Act
        await store.WriteAsync(Target, State, TestContext.Current.CancellationToken);

        var latest = await store.GetLatestAsync(TestContext.Current.CancellationToken);
        var read = await store.ReadStateAsync(Target, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(Target, latest);
        Assert.Equal(State, read.ToArray());
        Assert.Equal([Target.FileName], System.IO.Directory.GetFiles(_directory).Select(Path.GetFileName));
        Assert.Equal(SnapshotFormat.HeaderSize + State.Length, new FileInfo(Path.Combine(_directory, Target.FileName)).Length);
    }

    [Fact]
    public async Task KilledProcess_BeforeRename_LeavesNoSnapshotAndTheTempIsRemovedOnNextStart()
    {
        // Arrange: жертва дописала снапшот до fsync, но не переименовала его.
        await using (var victim = CrashVictim.Start(
            "snapshot-mid-write",
            _directory,
            Target.LastIncludedIndex.Value.ToString(CultureInfo.InvariantCulture),
            State.Length.ToString(CultureInfo.InvariantCulture)))
        {
            await victim.WaitForMarkerAsync("READY", MarkerTimeout);
            await victim.KillAsync();
        }

        var tempPath = Path.Combine(_directory, SnapshotFormat.GetTempFileName(Target.LastIncludedIndex));

        // Убитый процесс не убрал за собой ничего: временный файл остался ровно как был.
        Assert.True(File.Exists(tempPath));
        Assert.Equal(SnapshotFormat.HeaderSize + State.Length, new FileInfo(tempPath).Length);

        var store = new SnapshotStore(SystemStorageFileSystem.Instance, new TestClock(), _directory);

        // Act: снапшота нет, осиротевший временный файл убирается при первом обращении.
        var latest = await store.GetLatestAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(latest);
        Assert.False(File.Exists(tempPath));
        Assert.Empty(System.IO.Directory.GetFiles(_directory));
    }

    public void Dispose()
    {
        if (System.IO.Directory.Exists(_directory))
        {
            System.IO.Directory.Delete(_directory, recursive: true);
        }
    }

    private static SnapshotStore CreateStore(FakeStorageFileSystem fileSystem)
        => new(fileSystem, new TestClock(unixTimeMs: 1_700_000_000_000), Directory);
}
