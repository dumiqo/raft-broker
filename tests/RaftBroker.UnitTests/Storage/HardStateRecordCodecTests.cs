using System.Buffers.Binary;
using System.Text;
using RaftBroker.Core.Primitives;
using RaftBroker.Storage.HardState;
using RaftBroker.Storage.Integrity;

namespace RaftBroker.UnitTests.Storage;

/// <summary>
/// Кодек записи hard state (<c>S2-T02</c>): раскладка байтов, CRC, порча.
/// </summary>
public sealed class HardStateRecordCodecTests
{
    private static readonly HardState Voted = new(new Term(5), new NodeId("node-1"));

    [Fact]
    public void Write_ThenRead_RoundTripsTheState()
    {
        // Arrange
        var record = new byte[HardStateRecordCodec.GetRecordSize(Encoding.UTF8.GetByteCount("node-1"))];

        // Act
        HardStateRecordCodec.Write(record, 3, Voted);

        var parsed = HardStateRecordCodec.Read(record);

        // Assert
        parsed.Sequence.Should().Be(3);
        parsed.State.Should().Be(Voted);
    }

    [Fact]
    public void Write_WithNoVote_ProducesTheShortestRecord()
    {
        // Arrange
        var record = new byte[HardStateFormat.HeaderSize + HardStateFormat.CrcSize];

        // Act
        HardStateRecordCodec.Write(record, 1, new HardState(new Term(7), null));

        var parsed = HardStateRecordCodec.Read(record);

        // Assert
        parsed.State.VotedFor.Should().BeNull();
        parsed.State.CurrentTerm.Should().Be(new Term(7));
    }

    [Fact]
    public void GetRecordSize_ForNoVote_IsHeaderPlusCrc()
    {
        HardStateRecordCodec.GetRecordSize(0).Should().Be(30);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(HardStateFormat.MaxVotedForLength + 1)]
    public void GetRecordSize_OutsideTheFormat_Throws(int votedForLength)
    {
        var act = () => HardStateRecordCodec.GetRecordSize(votedForLength);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Write_IntoABufferOfTheWrongSize_Throws()
    {
        var act = () => HardStateRecordCodec.Write(new byte[10], 1, Voted);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ReadRecordSize_WithAForeignMagic_Throws()
    {
        var record = BuildRecord(1, "node-1");
        record[0] = (byte)'X';

        var act = () => HardStateRecordCodec.ReadRecordSize(record);

        act.Should().Throw<InvalidDataException>().WithMessage("*Сигнатура*");
    }

    [Fact]
    public void ReadRecordSize_WithATooLongVotedFor_Throws()
    {
        var record = BuildRecord(1, "node-1");
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(HardStateFormat.LengthOffset), 600);

        var act = () => HardStateRecordCodec.ReadRecordSize(record);

        act.Should().Throw<InvalidDataException>().WithMessage("*предела*");
    }

    [Fact]
    public void Read_WithACorruptedCrc_Throws()
    {
        var record = BuildRecord(1, "node-1");
        record[HardStateFormat.HeaderSize] ^= 0xFF;

        var act = () => HardStateRecordCodec.Read(record);

        act.Should().Throw<InvalidDataException>().WithMessage("*CRC*");
    }

    [Fact]
    public void Read_WithANegativeTerm_Throws()
    {
        var record = BuildRecord(-1, null);

        var act = () => HardStateRecordCodec.Read(record);

        act.Should().Throw<InvalidDataException>().WithMessage("*отрицателен*");
    }

    [Fact]
    public void Read_WithAVotedForThatIsNotAnIdentifier_Throws()
    {
        var record = BuildRecord(1, "node 1");

        var act = () => HardStateRecordCodec.Read(record);

        act.Should().Throw<InvalidDataException>().WithMessage("*идентификатором узла*");
    }

    [Fact]
    public void Read_WithABufferShorterThanTheRecord_Throws()
    {
        var record = BuildRecord(1, "node-1");

        var act = () => HardStateRecordCodec.Read(record.AsSpan(0, record.Length - 1));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Write_WithTheLongestIdentifier_FitsTheFormat()
    {
        // Arrange: 128 символов кириллицы - 256 байт UTF-8, худший реальный случай NodeId.
        var nodeId = new NodeId(new string('я', 128));
        var state = new HardState(new Term(1), nodeId);
        var size = HardStateRecordCodec.GetRecordSize(Encoding.UTF8.GetByteCount(nodeId.Value));

        // Act
        var record = new byte[size];
        HardStateRecordCodec.Write(record, 1, state);

        // Assert
        HardStateRecordCodec.Read(record).State.Should().Be(state);
    }

    /// <summary>Собирает запись вручную, чтобы испортить ровно одно поле и пересчитать CRC.</summary>
    private static byte[] BuildRecord(long term, string? votedFor)
    {
        var bytes = votedFor is null ? [] : Encoding.UTF8.GetBytes(votedFor);
        var record = new byte[HardStateFormat.HeaderSize + bytes.Length + HardStateFormat.CrcSize];

        HardStateFormat.Magic.CopyTo(record);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(HardStateFormat.MagicSize), 1);
        BinaryPrimitives.WriteInt64LittleEndian(
            record.AsSpan(HardStateFormat.MagicSize + HardStateFormat.SequenceSize),
            term);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(HardStateFormat.LengthOffset), (ushort)bytes.Length);
        bytes.CopyTo(record, HardStateFormat.HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(
            record.AsSpan(record.Length - HardStateFormat.CrcSize),
            Crc32C.Compute(record.AsSpan(0, record.Length - HardStateFormat.CrcSize)));

        return record;
    }
}
