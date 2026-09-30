using System.Buffers.Binary;
using System.Globalization;
using RaftBroker.Core.Primitives;
using RaftBroker.Storage.IO;
using RaftBroker.Storage.Log;

namespace RaftBroker.IntegrationTests.Storage;

/// <summary>
/// Восстановление после настоящего завершения процесса (<c>S1-T05</c>).
/// </summary>
/// <remarks>
/// <para>
/// Тесты на <c>FakeStorageFileSystem</c> проверяют восстановление на модели "байты в дубле":
/// они могут перебрать все точки обрыва, но ничего не говорят о том, что <c>fsync</c> писателя
/// действительно доходит до устройства. Здесь обрыв делает операционная система, убивая процесс
/// целиком, а сегмент остаётся ровно там, где его застала смерть.
/// </para>
/// <para>
/// Проверяется инвариант R6 в самой сильной формулировке, какая возможна без отказа диска:
/// подтверждённые записи (fsync уже прошёл) переживают крах, оборванный хвост не переживает
/// и не превращается ни в потерю подтверждённого, ни в отказ старта.
/// </para>
/// </remarks>
public sealed class LogRecoveryCrashTests : IDisposable
{
    private const int DurableRecords = 3;

    /// <summary>Длина payload подтверждённых записей: та же раскладка, что в <c>RaftBroker.CrashHarness</c>.</summary>
    private const int DurablePayloadLength = 4;

    /// <summary>Длина payload оборванного фрейма: нужна, чтобы обрыв пришёлся на середину payload.</summary>
    private const int TornPayloadLength = 64;

    private static readonly TimeSpan MarkerTimeout = TimeSpan.FromSeconds(60);

    private static readonly int DurableFrameSize = LogFrameCodec.GetFrameSize(DurablePayloadLength);

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "raft-broker-crash-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    [Fact]
    public async Task KilledProcess_TornLastFrame_RecoveryKeepsExactlyTheDurableRecords()
    {
        // Обрыв внутри payload последнего фрейма: не хватает CRC, то есть фрейм не доказан.
        var tornBytes = LogFrameCodec.GetFrameSize(TornPayloadLength) - 10;
        var durableBytes = LogSegmentFormat.HeaderSize + (DurableRecords * DurableFrameSize);
        var path = SegmentPath();

        await using (var victim = CrashVictim.Start("torn-frame", _directory, "3", tornBytes.ToString(CultureInfo.InvariantCulture)))
        {
            await victim.WaitForMarkerAsync("READY", MarkerTimeout);

            // Метка означает, что все fsync уже прошли, а хвост оборван. Дальше - смерть процесса.
            new FileInfo(path).Length.Should().Be(durableBytes + tornBytes);

            await victim.KillAsync();
        }

        // Убитый процесс не убрал за собой ничего: файл остался ровно в том виде, в каком был.
        File.Exists(path).Should().BeTrue();
        new FileInfo(path).Length.Should().Be(durableBytes + tornBytes);

        var recovery = await SegmentRecovery.RecoverAsync(
            SystemStorageFileSystem.Instance,
            path,
            SegmentKind.Last,
            CancellationToken.None);

        recovery.Status.Should().Be(SegmentRecoveryStatus.TruncatedTail);
        recovery.FrameCount.Should().Be(DurableRecords);
        recovery.LastIndex!.Value.Value.Should().Be(DurableRecords);
        recovery.LastTerm!.Value.Value.Should().Be(1);
        recovery.DroppedBytes.Should().Be(tornBytes);

        // Подтверждённые записи на месте и читаются как целые: усечение не тронуло префикс.
        var survivors = await ReadAllAsync(path);

        survivors.Should().Equal(ExpectedRecords());
        new FileInfo(path).Length.Should().Be(durableBytes);
    }

    [Fact]
    public async Task KilledProcess_TornHeader_RecoveryRemovesTheUnfinishedSegment()
    {
        // Оборванное создание сегмента (§9.3): заголовок не дописан, поэтому файл - мусор.
        const int headerBytes = LogSegmentFormat.HeaderSize - 13;
        var path = SegmentPath();

        await using (var victim = CrashVictim.Start("torn-header", _directory, headerBytes.ToString(CultureInfo.InvariantCulture)))
        {
            await victim.WaitForMarkerAsync("READY", MarkerTimeout);
            await victim.KillAsync();
        }

        new FileInfo(path).Length.Should().Be(headerBytes);

        var recovery = await SegmentRecovery.RecoverAsync(
            SystemStorageFileSystem.Instance,
            path,
            SegmentKind.Last,
            CancellationToken.None);

        recovery.Status.Should().Be(SegmentRecoveryStatus.Removed);
        recovery.ChangedFile.Should().BeTrue();
        recovery.DroppedBytes.Should().Be(headerBytes);
        File.Exists(path).Should().BeFalse("недописанный сегмент чинится удалением (§9.3)");
    }

    [Fact]
    public async Task RecoveryAfterACrash_IsIdempotentOnTheNextStart()
    {
        var tornBytes = LogFrameCodec.GetFrameSize(TornPayloadLength) - 1;
        var path = SegmentPath();

        await using (var victim = CrashVictim.Start("torn-frame", _directory, "3", tornBytes.ToString(CultureInfo.InvariantCulture)))
        {
            await victim.WaitForMarkerAsync("READY", MarkerTimeout);
            await victim.KillAsync();
        }

        var first = await SegmentRecovery.RecoverAsync(SystemStorageFileSystem.Instance, path, SegmentKind.Last, CancellationToken.None);
        first.Status.Should().Be(SegmentRecoveryStatus.TruncatedTail);

        // Следующий старт не должен ничего чинить: после первого восстановления сегмент целый,
        // а повторное восстановление не трогает диск (§9.2, «усекать только оборванный хвост»).
        var recovered = File.ReadAllBytes(path);

        var second = await SegmentRecovery.RecoverAsync(SystemStorageFileSystem.Instance, path, SegmentKind.Last, CancellationToken.None);

        second.Status.Should().Be(SegmentRecoveryStatus.Intact);
        second.ChangedFile.Should().BeFalse();
        second.FrameCount.Should().Be(DurableRecords);
        File.ReadAllBytes(path).Should().Equal(recovered);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string SegmentPath()
        => Path.Combine(_directory, LogSegmentFormat.GetFileName(new LogIndex(1)));

    /// <summary>Ожидаемые подтверждённые записи: та же раскладка payload, что строит жертва.</summary>
    private static IEnumerable<LogRecord> ExpectedRecords()
    {
        for (var index = 1; index <= DurableRecords; index++)
        {
            var payload = new byte[DurablePayloadLength];
            BinaryPrimitives.WriteInt32LittleEndian(payload, index);

            yield return LogRecord.Command(new LogIndex(index), new Term(1), payload);
        }
    }

    /// <summary>Читает сегмент тем же путём, каким его читает узел после восстановления.</summary>
    private static async Task<List<LogRecord>> ReadAllAsync(string path)
    {
        var open = await SegmentReader.OpenAsync(SystemStorageFileSystem.Instance, path, CancellationToken.None);
        open.IsSuccess.Should().BeTrue(open.Reason);

        await using var reader = open.Reader!;
        var scan = await reader.ScanAsync(CancellationToken.None);

        // Восстановление обязано оставить сегмент целым: следующий старт не найдёт здесь проблемы.
        scan.IsComplete.Should().BeTrue(scan.Reason);
        scan.FrameCount.Should().Be(DurableRecords);

        var records = new List<LogRecord>();

        await foreach (var record in reader.ReadForwardAsync(CancellationToken.None))
        {
            records.Add(record);
        }

        return records;
    }
}
