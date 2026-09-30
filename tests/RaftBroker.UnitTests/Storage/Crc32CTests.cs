using FsCheck;
using FsCheck.Xunit;
using RaftBroker.Storage.Integrity;

namespace RaftBroker.UnitTests.Storage;

/// <summary>
/// S1-T02: CRC-32C сегмента журнала.
/// </summary>
/// <remarks>
/// Векторы взяты из <c>docs/formats/log-segment.md</c>, §5, и посчитаны независимой
/// реализацией (не этим кодом), поэтому тест проверяет таблицу CRC, а не её совпадение
/// с самой собой. Формат фиксирует значения CRC, а не способ вычисления: если позже
/// появится аппаратное ускорение (<c>Sse42.Crc32</c>), эти же векторы станут его проверкой.
/// </remarks>
public sealed class Crc32CTests
{
    [Fact]
    public void EmptyInput_IsZero()
    {
        Crc32C.Compute([]).Should().Be(0x00000000u);
    }

    [Fact]
    public void TextVector_MatchesTheFormatDocument()
    {
        Crc32C.Compute("123456789"u8).Should().Be(0xE3069283u);
    }

    [Fact]
    public void BinaryVectors_MatchTheFormatDocument()
    {
        Crc32C.Compute(new byte[32]).Should().Be(0x8A9136AAu);
        Crc32C.Compute(Enumerable.Repeat((byte)0xFF, 32).ToArray()).Should().Be(0x62A8AB43u);

        byte[] ascending = [.. Enumerable.Range(0, 32).Select(value => (byte)value)];
        Crc32C.Compute(ascending).Should().Be(0x46DD794Eu);
    }

    [Fact]
    public void IdenticalInput_GivesIdenticalSum()
    {
        byte[] first = [1, 2, 3, 4, 5];
        byte[] second = [1, 2, 3, 4, 5];

        Crc32C.Compute(first).Should().Be(Crc32C.Compute(second));
    }

    /// <summary>
    /// Формат обещает, что CRC ловит порчу байтов. Свойство проверяет именно то, что обещано:
    /// любой одиночный перевёрнутый бит в данных меняет контрольную сумму.
    /// </summary>
    [Property(MaxTest = 1_000, Arbitrary = new[] { typeof(StorageArbitraries) })]
    public void FlippingAnySingleBit_ChangesTheSum(byte[] data, int bitIndex)
    {
        int bit = (int)((uint)bitIndex % (uint)(data.Length * 8));
        var mutated = (byte[])data.Clone();
        mutated[bit / 8] ^= (byte)(1 << (bit % 8));

        Crc32C.Compute(mutated).Should().NotBe(Crc32C.Compute(data));
    }
}
