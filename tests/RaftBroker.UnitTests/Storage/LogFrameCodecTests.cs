using System.Buffers.Binary;
using RaftBroker.Core.Primitives;
using RaftBroker.Storage.Log;

namespace RaftBroker.UnitTests.Storage;

/// <summary>
/// S1-T02: кодек фрейма журнала (<c>docs/formats/log-segment.md</c>, §4 и §5).
/// </summary>
/// <remarks>
/// Эталонные векторы ниже посчитаны независимой реализацией формата (не этим кодеком)
/// и записаны по полям: 4 байта длины, 1 байт типа, 8 байт терма, 8 байт индекса,
/// payload, 4 байта CRC. Так тест ловит не только "не сошлось", но и сдвиг раскладки.
/// </remarks>
public sealed class LogFrameCodecTests
{
    /// <summary>Индекс 7, терм 3, команда, payload <c>hello</c>.</summary>
    private static readonly byte[] CommandFrame =
        Hex.Bytes("16 00 00 00 | 01 | 03 00 00 00 00 00 00 00 | 07 00 00 00 00 00 00 00 | 68 65 6C 6C 6F | E4 7A 46 34");

    [Fact]
    public void WritesAndReadsTheCommandVector()
    {
        AssertGolden(
            LogRecord.Command(new LogIndex(7), new Term(3), "hello"u8.ToArray()),
            CommandFrame);
    }

    [Fact]
    public void WritesAndReadsTheNoopVector()
    {
        // Единственная запись типа noop: payload пуст, поэтому фрейм минимальной длины.
        AssertGolden(
            LogRecord.Noop(new LogIndex(1), new Term(0)),
            Hex.Bytes("11 00 00 00 | 02 | 00 00 00 00 00 00 00 00 | 01 00 00 00 00 00 00 00 | 70 66 18 2C"));
    }

    [Fact]
    public void WritesAndReadsTheConfigVector()
    {
        AssertGolden(
            LogRecord.Config(new LogIndex(2), new Term(1), new byte[] { 0x01, 0x02 }),
            Hex.Bytes("13 00 00 00 | 03 | 01 00 00 00 00 00 00 00 | 02 00 00 00 00 00 00 00 | 01 02 | 95 A7 82 15"));
    }

    [Fact]
    public void WritesAndReadsACommandWithoutPayload()
    {
        AssertGolden(
            LogRecord.Command(new LogIndex(9), new Term(255), Array.Empty<byte>()),
            Hex.Bytes("11 00 00 00 | 01 | FF 00 00 00 00 00 00 00 | 09 00 00 00 00 00 00 00 | B1 7B D4 01"));
    }

    [Fact]
    public void FieldOffsets_MatchTheDocumentedLayout()
    {
        BinaryPrimitives.ReadUInt32LittleEndian(CommandFrame).Should().Be(17 + 5);
        CommandFrame[4].Should().Be(0x01);
        BinaryPrimitives.ReadInt64LittleEndian(CommandFrame.AsSpan(5)).Should().Be(3);
        BinaryPrimitives.ReadInt64LittleEndian(CommandFrame.AsSpan(13)).Should().Be(7);
        CommandFrame.AsSpan(21, 5).ToArray().Should().Equal("hello"u8.ToArray());
        BinaryPrimitives.ReadUInt32LittleEndian(CommandFrame.AsSpan(26)).Should().Be(0x34467AE4u);
    }

    /// <summary>
    /// Оборванный хвост последнего сегмента - это <see cref="LogReadStatus.Truncated"/>:
    /// кодек не имеет права решать, усечь или отказать, он обязан лишь честно сообщить,
    /// что фрейм не помещается (решение - S1-T05).
    /// </summary>
    [Fact]
    public void EveryPrefixOfAFrame_IsTruncated()
    {
        for (int length = 0; length < CommandFrame.Length; length++)
        {
            var result = LogFrameCodec.Read(CommandFrame.AsSpan(0, length));

            result.Status.Should().Be(LogReadStatus.Truncated, $"префикс длиной {length} байт");
            result.Record.Should().BeNull();
            result.BytesConsumed.Should().Be(0);
            result.Reason.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void ZeroAndTooSmallFrameLength_AreCorruption()
    {
        AssertCorrupt(Mutate(CommandFrame, frame => BinaryPrimitives.WriteUInt32LittleEndian(frame, 0)), "меньше минимума");
        AssertCorrupt(Mutate(CommandFrame, frame => BinaryPrimitives.WriteUInt32LittleEndian(frame, LogSegmentFormat.MinFrameLength - 1)), "меньше минимума");
    }

    /// <summary>
    /// Слишком большая длина отвергается до проверки "хватает ли байтов": иначе порча поля
    /// длины выглядела бы как оборванный хвост, а это разные решения о судьбе сегмента.
    /// </summary>
    [Fact]
    public void TooLargeFrameLength_IsCorruption()
    {
        byte[] onlyLength = Hex.Bytes("00 00 00 00");
        BinaryPrimitives.WriteUInt32LittleEndian(onlyLength, LogSegmentFormat.MaxFrameLength + 1);

        AssertCorrupt(onlyLength, "больше максимума");
    }

    [Fact]
    public void UnknownRecordType_IsCorruption()
    {
        AssertCorrupt(Mutate(CommandFrame, frame => frame[4] = 0x00), "recordType");
        AssertCorrupt(Mutate(CommandFrame, frame => frame[4] = 0x04), "recordType");
        AssertCorrupt(Mutate(CommandFrame, frame => frame[4] = 0xFF), "recordType");
    }

    [Fact]
    public void NoopWithPayload_IsCorruption()
    {
        // Тип меняется на noop у фрейма, в котором есть payload: формально он читается, но формат это запрещает.
        AssertCorrupt(Mutate(CommandFrame, frame => frame[4] = 0x02), "noop");
    }

    [Fact]
    public void NegativeTermAndZeroIndex_AreCorruption()
    {
        AssertCorrupt(Mutate(CommandFrame, frame => BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(5), -1)), "терм");
        AssertCorrupt(Mutate(CommandFrame, frame => BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(13), 0)), "индекс");
    }

    [Fact]
    public void ChangedBytesBreakTheCrc()
    {
        AssertCorrupt(Mutate(CommandFrame, frame => frame[21] ^= 0x01), "CRC32C");
        AssertCorrupt(Mutate(CommandFrame, frame => frame[26] ^= 0x01), "CRC32C");
    }

    /// <summary>
    /// Порча поля длины, которая осталась в границах формата, неотличима от оборванного хвоста
    /// на уровне кодека: он видит только "не хватает байтов". Остаточный риск описан в
    /// <c>docs/formats/log-segment.md</c>, §9.6; различать эти случаи будет восстановление (S1-T05, S8-T03).
    /// </summary>
    [Fact]
    public void CorruptedLengthInsideTheLimits_LooksLikeATruncatedTail()
    {
        var result = LogFrameCodec.Read(Mutate(CommandFrame, frame => BinaryPrimitives.WriteUInt32LittleEndian(frame, 17 + 5 + 1)));

        result.Status.Should().Be(LogReadStatus.Truncated);
    }

    [Fact]
    public void MaxPayload_RoundTrips()
    {
        byte[] payload = new byte[LogSegmentFormat.MaxPayloadLength];
        for (int index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)index;
        }

        var record = LogRecord.Command(new LogIndex(10), new Term(4), payload);
        byte[] buffer = new byte[LogFrameCodec.GetFrameSize(payload.Length)];

        LogFrameCodec.Write(buffer, record);

        buffer.Length.Should().Be(LogSegmentFormat.FrameOverhead + LogSegmentFormat.MaxPayloadLength);

        var result = LogFrameCodec.Read(buffer);

        result.Status.Should().Be(LogReadStatus.Success);
        result.Record!.Payload.ToArray().Should().Equal(payload);
    }

    [Fact]
    public void Write_RejectsATooSmallBuffer()
    {
        byte[] buffer = new byte[CommandFrame.Length - 1];

        var act = () => LogFrameCodec.Write(buffer, LogRecord.Command(new LogIndex(7), new Term(3), "hello"u8.ToArray()));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void GetFrameSize_RejectsPayloadOutsideTheFormat()
    {
        LogFrameCodec.GetFrameSize(0).Should().Be(LogSegmentFormat.FrameOverhead);
        LogFrameCodec.GetFrameSize(LogSegmentFormat.MaxPayloadLength).Should().Be(LogSegmentFormat.FrameOverhead + LogSegmentFormat.MaxPayloadLength);

        var negative = () => LogFrameCodec.GetFrameSize(-1);
        var aboveLimit = () => LogFrameCodec.GetFrameSize(LogSegmentFormat.MaxPayloadLength + 1);

        negative.Should().Throw<ArgumentOutOfRangeException>();
        aboveLimit.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static void AssertGolden(LogRecord record, byte[] expected)
    {
        byte[] buffer = new byte[LogFrameCodec.GetFrameSize(record.Payload.Length)];

        LogFrameCodec.Write(buffer, record);

        buffer.Should().Equal(expected);
        LogFrameCodec.GetFrameSize(record.Payload.Length).Should().Be(expected.Length);

        var result = LogFrameCodec.Read(expected);

        result.Status.Should().Be(LogReadStatus.Success, result.Reason);
        result.Record.Should().Be(record);
        result.BytesConsumed.Should().Be(expected.Length);

        // Тот же фрейм в середине сегмента: за ним есть другие байты, и это не должно мешать.
        byte[] withTail = [.. expected, 0xAA, 0xBB];
        var tailResult = LogFrameCodec.Read(withTail);

        tailResult.Status.Should().Be(LogReadStatus.Success, tailResult.Reason);
        tailResult.BytesConsumed.Should().Be(expected.Length);
    }

    private static void AssertCorrupt(byte[] frame, string expectedReasonFragment)
    {
        var result = LogFrameCodec.Read(frame);

        result.Status.Should().Be(LogReadStatus.Corrupt);
        result.Record.Should().BeNull();
        result.BytesConsumed.Should().Be(0);
        result.Reason.Should().Contain(expectedReasonFragment);
    }

    private static byte[] Mutate(byte[] frame, Action<byte[]> mutation)
    {
        byte[] copy = (byte[])frame.Clone();
        mutation(copy);
        return copy;
    }
}
