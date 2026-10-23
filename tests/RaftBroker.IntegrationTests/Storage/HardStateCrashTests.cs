using System.Globalization;
using RaftBroker.Core.Primitives;
using RaftBroker.Storage.HardState;
using RaftBroker.Storage.IO;

namespace RaftBroker.IntegrationTests.Storage;

/// <summary>
/// Hard state на настоящем диске и после убийства процесса (<c>S2-T02</c>).
/// </summary>
/// <remarks>
/// Инвариант один: голос, за который узел ответил, переживает крах процесса. Модель на
/// <see cref="FakeStorageFileSystem"/> проверяет ветки кодека, а здесь проверяется, что
/// обещание не держится на честном слове кэша записи файловой системы.
/// </remarks>
public sealed class HardStateCrashTests : IDisposable
{
    private static readonly TimeSpan MarkerTimeout = TimeSpan.FromSeconds(60);

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "raft-broker-hard-state-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    [Fact]
    public async Task Write_ThenReopenOnTheRealDisk_ReturnsTheSameState()
    {
        // Arrange
        var store = new HardStateStore(SystemStorageFileSystem.Instance, _directory);
        var state = new HardState(new Term(3), new NodeId("node-1"));

        // Act
        await store.WriteAsync(state, CancellationToken.None);
        var reopened = await new HardStateStore(SystemStorageFileSystem.Instance, _directory).ReadAsync(CancellationToken.None);

        // Assert
        reopened.Should().Be(state);
    }

    [Fact]
    public async Task Read_WithATornTailOnTheRealDisk_TruncatesItAndKeepsTheVote()
    {
        // Arrange: полная запись плюс байты, которые оставил бы оборванный append.
        var store = new HardStateStore(SystemStorageFileSystem.Instance, _directory);
        await store.WriteAsync(new HardState(new Term(5), new NodeId("node-2")), CancellationToken.None);
        var whole = new FileInfo(store.FilePath).Length;

        await using (var file = new FileStream(store.FilePath, FileMode.Append, FileAccess.Write))
        {
            await file.WriteAsync(HardStateFormat.Magic[..5].ToArray(), CancellationToken.None);
            await file.FlushAsync(CancellationToken.None);
        }

        // Act
        var state = await new HardStateStore(SystemStorageFileSystem.Instance, _directory).ReadAsync(CancellationToken.None);

        // Assert
        state.Should().Be(new HardState(new Term(5), new NodeId("node-2")));
        new FileInfo(store.FilePath).Length.Should().Be(whole);
    }

    [Fact]
    public async Task Vote_SurvivesProcessKill()
    {
        // Arrange: процесс голосует, сбрасывает запись на устройство и умирает без уборки.
        await using var victim = CrashVictim.Start("hard-state", _directory, "7", "node-1");
        await victim.WaitForMarkerAsync("READY", MarkerTimeout);
        await victim.KillAsync();

        // Act: новый процесс открывает тот же каталог.
        var state = await new HardStateStore(SystemStorageFileSystem.Instance, _directory).ReadAsync(CancellationToken.None);

        // Assert: голос и term помнятся - второй голос в term 7 станет явной ошибкой, а не забывчивостью.
        state.Should().Be(new HardState(new Term(7), new NodeId("node-1")));
    }

    [Fact]
    public async Task NoVote_SurvivesProcessKill()
    {
        // Arrange
        await using var victim = CrashVictim.Start("hard-state", _directory, "2", "-");
        await victim.WaitForMarkerAsync("READY", MarkerTimeout);
        await victim.KillAsync();

        // Act
        var state = await new HardStateStore(SystemStorageFileSystem.Instance, _directory).ReadAsync(CancellationToken.None);

        // Assert
        state.Should().Be(new HardState(new Term(2), null));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
