using System.Buffers.Binary;
using RaftBroker.Core.Primitives;
using RaftBroker.Storage.Integrity;
using RaftBroker.Storage.Log;
using RaftBroker.TestKit.IO;
using RaftBroker.TestKit.Time;

namespace RaftBroker.UnitTests.Storage;

/// <summary>
/// Общие построения тестов журнала (S1-T03, S1-T04, S1-T05): часы, каталог, записи,
/// запись сегмента, чтение его назад и точечная порча готовых байтов.
/// </summary>
/// <remarks>
/// Файл должен оставаться общим для писателя и читателя: если каждый заведёт свой способ
/// ставить байты на диск, разъехавшуюся раскладку перестанет ловить хоть один тест.
/// </remarks>
internal static class LogTestData
{
    /// <summary>Каталог в настройках: файловая система поддельная, на диск ничего не попадает.</summary>
    internal const string Directory = "log";

    /// <summary>Момент времени, который обязан оказаться в информационном поле заголовка.</summary>
    internal const long NowMs = 1_700_000_000_000;

    /// <summary>Монотонный счётчик в тестовых часах намеренно не равен <see cref="NowMs"/>: поля не должны путаться.</summary>
    internal const long MonotonicStartMs = 42;

    internal static SegmentWriter CreateWriter(
        FakeStorageFileSystem fileSystem,
        long maxSegmentSize = LogSegmentFormat.MaxSegmentSize,
        DurabilityMode defaultMode = DurabilityMode.GroupCommit)
        => new(
            fileSystem,
            new TestClock(startMs: MonotonicStartMs, unixTimeMs: NowMs),
            new SegmentWriterOptions
            {
                Directory = Directory,
                MaxSegmentSize = maxSegmentSize,
                DefaultDurabilityMode = defaultMode,
            });

    internal static string SegmentPath(long baseIndex)
        => Path.Combine(Directory, LogSegmentFormat.GetFileName(new LogIndex(baseIndex)));

    /// <summary>Payload с предсказуемым содержимым: смешивание записей не должно проходить незамеченным.</summary>
    internal static byte[] Payload(long index, int length = 4)
    {
        var payload = new byte[length];

        for (var offset = 0; offset < length; offset++)
        {
            payload[offset] = (byte)((index + offset) & 0xFF);
        }

        return payload;
    }

    internal static LogRecord Command(long index, long term = 1, int payloadLength = 4)
        => LogRecord.Command(new LogIndex(index), new Term(term), Payload(index, payloadLength));

    internal static LogRecord Noop(long index, long term = 1)
        => LogRecord.Noop(new LogIndex(index), new Term(term));

    /// <summary>Записывает записи в один сегмент и возвращает путь этого сегмента.</summary>
    internal static async Task<string> WriteSegmentAsync(FakeStorageFileSystem fileSystem, params LogRecord[] records)
    {
        var writer = CreateWriter(fileSystem);
        await using (writer)
        {
            await writer.AppendAsync(records, CancellationToken.None);
            return writer.CurrentSegmentPath!;
        }
    }

    /// <summary>Содержимое файла с одним заголовком и ни одним фреймом: пустой сегмент (§8).</summary>
    internal static byte[] HeaderOnlySegment(long baseIndex)
    {
        var buffer = new byte[LogSegmentFormat.HeaderSize];
        SegmentHeaderCodec.Write(buffer, new SegmentHeader(new LogIndex(baseIndex), NowMs));
        return buffer;
    }

    /// <summary>Читает сегмент теми же кодеками, что и боевой код (S1-T02): writer и reader не должны разъехаться.</summary>
    internal static List<LogRecord> ReadSegment(FakeStorageFileSystem fileSystem, long baseIndex)
    {
        var bytes = fileSystem.ReadAllBytes(SegmentPath(baseIndex));

        var header = SegmentHeaderCodec.Read(bytes);
        header.IsSuccess.Should().BeTrue(header.Reason);
        header.Header!.BaseIndex.Value.Should().Be(baseIndex);

        var records = new List<LogRecord>();
        var offset = LogSegmentFormat.HeaderSize;

        while (offset < bytes.Length)
        {
            var frame = LogFrameCodec.Read(bytes.AsSpan(offset));
            frame.IsSuccess.Should().BeTrue(frame.Reason);
            records.Add(frame.Record!);
            offset += frame.BytesConsumed;
        }

        return records;
    }

    /// <summary>Смещения начал фреймов в готовом сегменте: тестам порчи нужны точные позиции.</summary>
    /// <param name="segment">Байты сегмента целиком, включая заголовок.</param>
    internal static List<int> FrameOffsets(byte[] segment)
    {
        var offsets = new List<int>();
        var offset = LogSegmentFormat.HeaderSize;

        while (offset < segment.Length)
        {
            offsets.Add(offset);
            offset += LogFrameCodec.GetFrameSize((int)BinaryPrimitives.ReadUInt32LittleEndian(segment.AsSpan(offset)) - LogSegmentFormat.MinFrameLength);
        }

        return offsets;
    }

    /// <summary>
    /// Пересчитывает CRC фрейма по его смещению.
    /// </summary>
    /// <param name="segment">Байты сегмента; CRC правится на месте.</param>
    /// <param name="frameOffset">Смещение начала фрейма.</param>
    /// <remarks>
    /// Нужно там, где тест меняет поле внутри фрейма (индекс, терм, тип): без пересчёта такое
    /// вмешательство выглядело бы порчей CRC, и проверялось бы не то правило.
    /// </remarks>
    internal static void FixFrameCrc(byte[] segment, int frameOffset)
    {
        var frameLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(segment.AsSpan(frameOffset));
        var crc = Crc32C.Compute(segment.AsSpan(frameOffset + sizeof(uint), frameLength));
        BinaryPrimitives.WriteUInt32LittleEndian(segment.AsSpan(frameOffset + sizeof(uint) + frameLength), crc);
    }
}
