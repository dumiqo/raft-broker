using System.Buffers.Binary;
using RaftBroker.Core.Primitives;
using RaftBroker.Storage.Integrity;
using RaftBroker.Storage.Snapshot;

namespace RaftBroker.UnitTests.Storage;

/// <summary>
/// Кодек заголовка снапшота (<c>S1-T09</c>): раскладка 48 байт и порядок проверок §4-§6 формата.
/// </summary>
/// <remarks>
/// Проверки идут по одному полю: если бы все отказы были одного вида, порча любого байта
/// выглядела бы как «снапшот испорчен» и не говорила бы, что именно не сходится.
/// Хранилище добавляет к этому проверку длины файла, поэтому здесь проверяется только заголовок.
/// </remarks>
public sealed class SnapshotHeaderCodecTests
{
    private static readonly SnapshotMetadata Metadata = new(new LogIndex(42), new Term(7));

    private const long CreatedAtUnixMs = 1_700_000_000_000;

    private const int StateLength = 16;

    [Fact]
    public void RoundTrip_KeepsEveryField()
    {
        var header = new byte[SnapshotFormat.HeaderSize];

        SnapshotHeaderCodec.Encode(Metadata, CreatedAtUnixMs, StateLength, stateCrc32C: 0xDEADBEEF, header);

        SnapshotHeaderCodec.TryDecode(header, out var parsed, out var reason).Should().BeTrue(reason);

        parsed.Metadata.Should().Be(Metadata);
        parsed.CreatedAtUnixMs.Should().Be(CreatedAtUnixMs);
        parsed.StateLength.Should().Be(StateLength);
        parsed.StateCrc32C.Should().Be(0xDEADBEEF);
        parsed.FileLength.Should().Be(SnapshotFormat.HeaderSize + StateLength);
    }

    [Fact]
    public void Encode_ChecksTheHeaderCrcAgainstEverythingBeforeIt()
    {
        var header = new byte[SnapshotFormat.HeaderSize];

        SnapshotHeaderCodec.Encode(Metadata, CreatedAtUnixMs, StateLength, stateCrc32C: 1, header);

        BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(SnapshotFormat.HeaderCrcOffset))
            .Should().Be(Crc32C.Compute(header.AsSpan(0, SnapshotFormat.HeaderCrcOffset)));
    }

    [Fact]
    public void Encode_WithAShortBuffer_Throws()
    {
        var buffer = new byte[SnapshotFormat.HeaderSize - 1];

        var encode = () => SnapshotHeaderCodec.Encode(Metadata, CreatedAtUnixMs, StateLength, 0, buffer);

        encode.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TryDecode_WithAShortHeader_ReportsTruncation()
    {
        SnapshotHeaderCodec.TryDecode(new byte[SnapshotFormat.HeaderSize - 1], out _, out var reason)
            .Should().BeFalse();

        reason.Should().Contain("оборван");
    }

    [Fact]
    public void TryDecode_WithAForeignMagic_ReportsTheMagic()
    {
        var header = Build(header => header[0] ^= 0xFF);

        SnapshotHeaderCodec.TryDecode(header, out _, out var reason).Should().BeFalse();

        reason.Should().Contain("магия");
    }

    [Fact]
    public void TryDecode_WithANonPositiveIndex_ReportsTheIndex()
    {
        var header = Build(header => WriteInt64(header, 8, 0));

        SnapshotHeaderCodec.TryDecode(header, out _, out var reason).Should().BeFalse();

        reason.Should().Contain("не положителен");
    }

    [Fact]
    public void TryDecode_WithANegativeStateLength_ReportsTheLength()
    {
        var header = Build(header => WriteInt64(header, 32, -1));

        SnapshotHeaderCodec.TryDecode(header, out _, out var reason).Should().BeFalse();

        reason.Should().Contain("вне границ");
    }

    [Fact]
    public void TryDecode_WithAStateLengthAboveTheLimit_ReportsTheLength()
    {
        // Граница проверяется по заголовку: гигабайт в память не читается вовсе.
        var header = Build(source => WriteInt64(source, 32, SnapshotFormat.MaxStateLength + 1));

        SnapshotHeaderCodec.TryDecode(header, out _, out var reason).Should().BeFalse();

        reason.Should().Contain("вне границ");
    }

    [Fact]
    public void TryDecode_WithADamagedHeaderCrc_ReportsTheCrc()
    {
        // Порча самой суммы, а не поля: Build пересчитывает CRC, а здесь он обязан разойтись.
        var header = Build(_ => { });
        header[SnapshotFormat.HeaderCrcOffset] ^= 0x01;

        SnapshotHeaderCodec.TryDecode(header, out _, out var reason).Should().BeFalse();

        reason.Should().Contain("CRC заголовка");
    }

    /// <summary>Собирает целый заголовок и портит его <paramref name="mutate"/>, пересчитывая CRC.</summary>
    private static byte[] Build(Action<byte[]> mutate)
    {
        var header = new byte[SnapshotFormat.HeaderSize];

        SnapshotHeaderCodec.Encode(Metadata, CreatedAtUnixMs, StateLength, stateCrc32C: 0, header);
        mutate(header);

        // Порча поля заголовка не должна выглядеть как порча его CRC: иначе тест проверял бы
        // не то поле, а порядок проверок §6.
        WriteUInt32(header, SnapshotFormat.HeaderCrcOffset, Crc32C.Compute(header.AsSpan(0, SnapshotFormat.HeaderCrcOffset)));

        return header;
    }

    private static void WriteInt64(byte[] header, int offset, long value)
        => BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(offset), value);

    private static void WriteUInt32(byte[] header, int offset, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(offset), value);
}
