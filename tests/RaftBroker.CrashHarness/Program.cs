using System.Buffers.Binary;
using System.Globalization;
using RaftBroker.Core.Primitives;
using RaftBroker.Storage.Integrity;
using RaftBroker.Storage.IO;
using RaftBroker.Storage.Log;
using RaftBroker.Storage.Snapshot;

namespace RaftBroker.CrashHarness;

/// <summary>
/// Жертва краш-тестов (<c>S1-T05</c>): пишет сегмент так, как его оставил бы упавший процесс,
/// сообщает <c>READY</c> и ждёт, пока родительский тест убьёт процесс.
/// </summary>
/// <remarks>
/// <para>
/// Байты собираются теми же кодеками, что и у писателя (<c>S1-T02</c>): <see cref="SegmentWriter"/>
/// сюда не годится, потому что оборванный фрейм он записать не может по построению, а открыть
/// второй дескриптор на запись к тому же файлу не даёт его <c>FileShare.Read</c>.
/// </para>
/// <para>
/// Права на fsync здесь те же, что у режима <see cref="DurabilityMode.PerEntry"/>: каждая
/// подтверждённая запись и оборванный хвост сброшены на устройство до <c>READY</c>. Поэтому
/// состояние файла после убийства процесса детерминировано, и родительский тест проверяет
/// не «как повезёт», а инвариант R6: подтверждённое переживает крах, недописанное - нет.
/// </para>
/// <para>
/// После <c>READY</c> процесс намеренно ничего не делает: любая уборка по выходу сделала бы
/// тест бессмысленным. Ждать приходится вечно - завершает процесс только <c>Kill</c>.
/// </para>
/// </remarks>
internal static class Program
{
    /// <summary>Длина payload подтверждённых записей: та же раскладка, что в краш-тесте.</summary>
    private const int DurablePayloadLength = 4;

    /// <summary>Длина payload оборванного фрейма: нужна, чтобы обрыв пришёлся на середину payload.</summary>
    private const int TornPayloadLength = 64;

    /// <summary>Момент создания заголовка: фиксированный, чтобы файл был воспроизводим.</summary>
    private const long CreatedAtUnixMs = 1_700_000_000_000;

    private static async Task<int> Main(string[] args)
    {
        try
        {
            var path = args switch
            {
                ["torn-frame", var directory, var durableRecords, var tornBytes] => await WriteTornFrameAsync(
                    directory,
                    ParseCount(durableRecords, nameof(durableRecords)),
                    ParseCount(tornBytes, nameof(tornBytes))),

                ["torn-header", var directory, var headerBytes] => await WriteTornHeaderAsync(
                    directory,
                    ParseCount(headerBytes, nameof(headerBytes))),

                ["snapshot-mid-write", var directory, var snapshotIndex, var stateBytes] => await WriteUnpublishedSnapshotAsync(
                    directory,
                    ParseCount(snapshotIndex, nameof(snapshotIndex)),
                    ParseCount(stateBytes, nameof(stateBytes))),

                _ => throw new ArgumentException(
                    "Использование: RaftBroker.CrashHarness torn-frame <каталог> <записей> <байтов фрейма> "
                    + "| torn-header <каталог> <байтов заголовка> "
                    + "| snapshot-mid-write <каталог> <индекс> <байтов состояния>."),
            };

            Console.Out.WriteLine($"READY {path}");
            Console.Out.Flush();

            // Дальше процесс обязан только умереть: родитель убивает его снаружи.
            await Task.Delay(Timeout.Infinite);

            return 0;
        }
        catch (Exception exception)
        {
            // Ошибку видно в stderr, но код возврата важнее: родитель ждёт READY и падает по таймауту.
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    /// <summary>
    /// Пишет подтверждённые записи (fsync после каждой) и следом оборванный фрейм, у которого
    /// на устройство попало только начало: так выглядит смерть процесса с <c>PerEntry</c>.
    /// </summary>
    private static async Task<string> WriteTornFrameAsync(string directory, int durableRecords, int tornBytes)
    {
        if (durableRecords < 0)
        {
            throw new ArgumentException("Число подтверждённых записей не может быть отрицательным.", nameof(durableRecords));
        }

        var tornFrameSize = LogFrameCodec.GetFrameSize(TornPayloadLength);

        if (tornBytes <= 0 || tornBytes >= tornFrameSize)
        {
            throw new ArgumentException(
                $"Оборванный фрейм должен быть обрезан внутри себя: 1..{tornFrameSize - 1} байт, получено {tornBytes}.",
                nameof(tornBytes));
        }

        var path = GetSegmentPath(directory);
        await using var file = await SystemStorageFileSystem.Instance.CreateAsync(path, CancellationToken.None);

        // Порядок тот же, что у писателя: fsync заголовка до первого фрейма (§8).
        var header = new byte[LogSegmentFormat.HeaderSize];
        SegmentHeaderCodec.Write(header, new SegmentHeader(new LogIndex(1), CreatedAtUnixMs));
        await file.WriteAsync(header, CancellationToken.None);
        await file.FlushToDiskAsync(CancellationToken.None);

        for (var index = 1L; index <= durableRecords; index++)
        {
            await WriteFrameAsync(file, LogRecord.Command(new LogIndex(index), new Term(1), DurablePayload(index)));
            await file.FlushToDiskAsync(CancellationToken.None);
        }

        var torn = new byte[tornFrameSize];
        LogFrameCodec.Write(torn, LogRecord.Command(new LogIndex(durableRecords + 1), new Term(1), TornPayload()));
        await file.WriteAsync(torn.AsMemory(0, tornBytes), CancellationToken.None);

        // Оборванный хвост тоже сброшен: иначе после убийства процесса на диске могло бы
        // не оказаться и его, и тест проверял бы не то состояние.
        await file.FlushToDiskAsync(CancellationToken.None);

        return path;
    }

    /// <summary>Пишет только начало заголовка: так выглядит смерть процесса во время создания сегмента (§9.3).</summary>
    private static async Task<string> WriteTornHeaderAsync(string directory, int headerBytes)
    {
        if (headerBytes <= 0 || headerBytes >= LogSegmentFormat.HeaderSize)
        {
            throw new ArgumentException(
                $"Заголовок должен быть обрезан внутри себя: 1..{LogSegmentFormat.HeaderSize - 1} байт, получено {headerBytes}.",
                nameof(headerBytes));
        }

        var path = GetSegmentPath(directory);
        await using var file = await SystemStorageFileSystem.Instance.CreateAsync(path, CancellationToken.None);

        var header = new byte[LogSegmentFormat.HeaderSize];
        SegmentHeaderCodec.Write(header, new SegmentHeader(new LogIndex(1), CreatedAtUnixMs));
        await file.WriteAsync(header.AsMemory(0, headerBytes), CancellationToken.None);
        await file.FlushToDiskAsync(CancellationToken.None);

        return path;
    }

    /// <summary>
    /// Пишет снапшот до переименования: так выглядит смерть процесса между fsync временного
    /// файла и публикацией (<c>S1-T09</c>, §7 формата снапшота).
    /// </summary>
    /// <remarks>
    /// Заголовок и начало состояния попадают на устройство, но имя остаётся <c>.tmp</c>, то есть
    /// снапшотом файл не считается. Родительский тест проверяет, что после смерти процесса
    /// каталог не содержит снапшота вовсе, а осиротевший временный файл убирается при следующем
    /// обращении к каталогу.
    /// </remarks>
    private static async Task<string> WriteUnpublishedSnapshotAsync(string directory, long snapshotIndex, int stateBytes)
    {
        if (snapshotIndex <= 0)
        {
            throw new ArgumentException("Индекс снапшота должен быть положительным.", nameof(snapshotIndex));
        }

        if (stateBytes < 0 || stateBytes > SnapshotFormat.MaxStateLength)
        {
            throw new ArgumentException("Длина состояния вне границ формата.", nameof(stateBytes));
        }

        var metadata = new SnapshotMetadata(new LogIndex(snapshotIndex), new Term(3));
        var path = Path.GetFullPath(
            Path.Combine(directory, SnapshotFormat.GetTempFileName(metadata.LastIncludedIndex)));

        await using var file = await SystemStorageFileSystem.Instance.CreateAsync(path, CancellationToken.None);

        var state = new byte[stateBytes];
        var header = new byte[SnapshotFormat.HeaderSize];
        SnapshotHeaderCodec.Encode(metadata, CreatedAtUnixMs, state.Length, Crc32C.Compute(state), header);

        await file.WriteAsync(header, CancellationToken.None);
        await file.WriteAsync(state, CancellationToken.None);

        // Сброс тот же, что у хранилища: до переименования файл уже цел, но ещё не снапшот.
        await file.FlushToDiskAsync(CancellationToken.None);

        return path;
    }

    private static async Task WriteFrameAsync(IStorageFile file, LogRecord record)
    {
        var frame = new byte[LogFrameCodec.GetFrameSize(record.Payload.Length)];
        LogFrameCodec.Write(frame, record);

        await file.WriteAsync(frame, CancellationToken.None);
    }

    /// <summary>Путь сегмента с базовым индексом 1 в заданном каталоге.</summary>
    private static string GetSegmentPath(string directory)
        => Path.GetFullPath(Path.Combine(directory, LogSegmentFormat.GetFileName(new LogIndex(1))));

    /// <summary>Payload подтверждённой записи: 4 байта индекса little-endian (ту же раскладку строит краш-тест).</summary>
    private static byte[] DurablePayload(long index)
    {
        var payload = new byte[DurablePayloadLength];
        BinaryPrimitives.WriteInt32LittleEndian(payload, (int)index);

        return payload;
    }

    private static byte[] TornPayload()
    {
        var payload = new byte[TornPayloadLength];
        BinaryPrimitives.WriteInt32LittleEndian(payload, 0);

        return payload;
    }

    private static int ParseCount(string value, string name)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new ArgumentException($"Не число: {name}={value}.", name);
}
