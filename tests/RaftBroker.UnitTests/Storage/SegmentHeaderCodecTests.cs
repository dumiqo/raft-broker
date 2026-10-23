using System.Buffers.Binary;
using FsCheck;
using FsCheck.Xunit;
using RaftBroker.Core.Primitives;
using RaftBroker.Storage.Integrity;
using RaftBroker.Storage.Log;

namespace RaftBroker.UnitTests.Storage;

/// <summary>
/// S1-T02: кодек заголовка сегмента (<c>docs/formats/log-segment.md</c>, §3 и §9.1).
/// Эталонные векторы посчитаны независимой реализацией формата, а не этим кодеком.
/// </summary>
public sealed class SegmentHeaderCodecTests
{
    private static readonly byte[] HeaderVector = Hex.Bytes(
        "52 42 53 45 47 4C 4F 47 | 01 00 | 01 00 00 00 00 00 00 00 | 00 6C 50 C4 A0 01 00 00 | F5 0B FC F1");

    [Fact]
    public void WritesAndReadsTheHeaderVector()
    {
        var header = new SegmentHeader(new LogIndex(1), 1_790_000_000_000);
        byte[] buffer = new byte[LogSegmentFormat.HeaderSize];

        SegmentHeaderCodec.Write(buffer, header);

        buffer.Should().Equal(HeaderVector);

        var result = SegmentHeaderCodec.Read(HeaderVector);

        result.Status.Should().Be(LogReadStatus.Success, result.Reason);
        result.Header.Should().Be(header);
    }

    [Fact]
    public void FieldOffsets_MatchTheDocumentedLayout()
    {
        HeaderVector.AsSpan(0, 8).ToArray().Should().Equal("RBSEGLOG"u8.ToArray());
        BinaryPrimitives.ReadUInt16LittleEndian(HeaderVector.AsSpan(8)).Should().Be(1);
        BinaryPrimitives.ReadInt64LittleEndian(HeaderVector.AsSpan(10)).Should().Be(1);
        BinaryPrimitives.ReadInt64LittleEndian(HeaderVector.AsSpan(18)).Should().Be(1_790_000_000_000);
        BinaryPrimitives.ReadUInt32LittleEndian(HeaderVector.AsSpan(26)).Should().Be(0xF1FC0BF5u);
    }

    [Fact]
    public void CreatedAt_IsOnlyADiagnosticField()
    {
        // Момент создания входит в CRC, но ни одной проверки корректности на нём не построено.
        var header = new SegmentHeader(new LogIndex(42), 0);
        byte[] buffer = new byte[LogSegmentFormat.HeaderSize];

        SegmentHeaderCodec.Write(buffer, header);

        buffer.Should().Equal(Hex.Bytes("52 42 53 45 47 4C 4F 47 | 01 00 | 2A 00 00 00 00 00 00 00 | 00 00 00 00 00 00 00 00 | 47 8C F6 BC"));
        SegmentHeaderCodec.Read(buffer).Header.Should().Be(header);
    }

    [Fact]
    public void ShortFile_IsTruncated_NotCorrupt()
    {
        for (int length = 0; length < LogSegmentFormat.HeaderSize; length++)
        {
            var result = SegmentHeaderCodec.Read(HeaderVector.AsSpan(0, length));

            result.Status.Should().Be(LogReadStatus.Truncated, $"префикс длиной {length} байт");
            result.Header.Should().BeNull();
        }
    }

    [Fact]
    public void WrongMagic_IsNotOurFile()
    {
        var result = SegmentHeaderCodec.Read(Mutate(header => header[0] = (byte)'X'));

        result.Status.Should().Be(LogReadStatus.Corrupt);
        result.Reason.Should().Contain("магия");
    }

    /// <summary>
    /// Файл будущей версии формата обязан отвергаться именно как "версия не поддерживается",
    /// а не как "заголовок повреждён": у него CRC сходится, и сообщение об ошибке должно
    /// называть версию, иначе разбираться с таким файлом вслепую (см. §10).
    /// </summary>
    [Fact]
    public void UnknownVersion_IsRefusedByNameWithItsNumber()
    {
        byte[] versionTwo = Hex.Bytes(
            "52 42 53 45 47 4C 4F 47 | 02 00 | 01 00 00 00 00 00 00 00 | 00 6C 50 C4 A0 01 00 00 | 88 0D 47 E0");

        var result = SegmentHeaderCodec.Read(versionTwo);

        result.Status.Should().Be(LogReadStatus.Corrupt);
        result.Reason.Should().Contain("2").And.Contain("формат");
    }

    [Fact]
    public void BrokenHeaderCrc_IsCorruption()
    {
        var result = SegmentHeaderCodec.Read(Mutate(header => header[26] ^= 0x01));

        result.Status.Should().Be(LogReadStatus.Corrupt);
        result.Reason.Should().Contain("CRC32C");
    }

    /// <summary>
    /// Нулевой baseIndex ловится и конструктором <see cref="SegmentHeader"/>, но в байтах такое
    /// возможно только вместе с пересчитанной CRC - иначе первым сработает CRC заголовка.
    /// Порядок проверок (магия, версия, CRC, затем поля) - из §9.1 документа.
    /// </summary>
    [Fact]
    public void BaseIndexBelowOne_IsCorruption()
    {
        byte[] header = (byte[])HeaderVector.Clone();
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(10), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(26), Crc32C.Compute(header.AsSpan(0, 26)));

        var result = SegmentHeaderCodec.Read(header);

        result.Status.Should().Be(LogReadStatus.Corrupt);
        result.Reason.Should().Contain("baseIndex");
    }

    [Fact]
    public void ToString_IsForDiagnosticsAndNamesTheSegment()
    {
        // Строка попадает в логи и в сообщения об порче: в ней должны быть и индекс, и время,
        // иначе разбирать инцидент придётся по hex-дампу.
        var header = new SegmentHeader(new LogIndex(7), createdAtUnixMs: 1_700_000_000_000);

        header.ToString().Should().Be("сегмент с 7, создан 1700000000000 мс Unix");
    }

    [Fact]
    public void BaseIndexBelowOne_IsRejectedOnConstruction()
    {
        var act = () => new SegmentHeader(new LogIndex(0), 1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Write_RejectsATooSmallBuffer()
    {
        byte[] buffer = new byte[LogSegmentFormat.HeaderSize - 1];

        var act = () => SegmentHeaderCodec.Write(buffer, new SegmentHeader(new LogIndex(1), 0));

        act.Should().Throw<ArgumentException>();
    }

    [Property(MaxTest = 500, Arbitrary = new[] { typeof(StorageArbitraries) })]
    public void RoundTrip_PreservesTheHeader(SegmentHeader header)
    {
        byte[] buffer = new byte[LogSegmentFormat.HeaderSize];

        SegmentHeaderCodec.Write(buffer, header);

        var result = SegmentHeaderCodec.Read(buffer);

        result.Status.Should().Be(LogReadStatus.Success, result.Reason);
        result.Header.Should().Be(header);
    }

    private static byte[] Mutate(Action<byte[]> mutation)
    {
        byte[] copy = (byte[])HeaderVector.Clone();
        mutation(copy);
        return copy;
    }
}
