using RaftBroker.Core.Primitives;
using RaftBroker.Storage.Log;
using RaftBroker.TestKit.IO;
using RaftBroker.TestKit.Time;

namespace RaftBroker.UnitTests.Storage;

/// <summary>
/// Общие построения тестов писателя сегментов (S1-T03): часы, каталог, записи и чтение назад.
/// </summary>
internal static class SegmentWriterTestData
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
}
