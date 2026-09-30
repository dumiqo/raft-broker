using System.Buffers.Binary;
using RaftBroker.Core.Primitives;
using RaftBroker.Storage.Integrity;

namespace RaftBroker.Storage.Log;

/// <summary>
/// Кодек заголовка сегмента: 30 байт, с которых начинается каждый файл сегмента.
/// </summary>
/// <remarks>
/// Раскладка - из <c>docs/formats/log-segment.md</c>, §3: магия <c>RBSEGLOG</c> (8 байт),
/// <c>formatVersion</c> (u16), <c>baseIndex</c> (i64), <c>createdAtUnixMs</c> (i64),
/// <c>headerCrc32c</c> (u32) по первым 26 байтам. Все числа - little-endian.
/// Порядок проверок при чтении повторяет §9.1: длина, магия, версия, CRC, индекс.
/// Версия проверяется до CRC намеренно: файл будущей версии формата обязан быть отвергнут
/// как "версия не поддерживается", а не как "заголовок повреждён".
/// </remarks>
public static class SegmentHeaderCodec
{
    private const int VersionOffset = LogSegmentFormat.MagicSize;
    private const int BaseIndexOffset = VersionOffset + sizeof(ushort);
    private const int CreatedAtOffset = BaseIndexOffset + sizeof(long);
    private const int CrcOffset = CreatedAtOffset + sizeof(long);

    /// <summary>Записывает заголовок в буфер.</summary>
    /// <param name="destination">Буфер, начиная с которого пишется заголовок.</param>
    /// <param name="header">Заголовок сегмента.</param>
    /// <exception cref="ArgumentException">Буфер короче <see cref="LogSegmentFormat.HeaderSize"/>.</exception>
    public static void Write(Span<byte> destination, SegmentHeader header)
    {
        if (destination.Length < LogSegmentFormat.HeaderSize)
        {
            throw new ArgumentException(
                $"Для заголовка нужно {LogSegmentFormat.HeaderSize} байт, в буфере {destination.Length}.",
                nameof(destination));
        }

        LogSegmentFormat.Magic.CopyTo(destination);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[VersionOffset..], LogSegmentFormat.FormatVersion);
        BinaryPrimitives.WriteInt64LittleEndian(destination[BaseIndexOffset..], header.BaseIndex.Value);
        BinaryPrimitives.WriteInt64LittleEndian(destination[CreatedAtOffset..], header.CreatedAtUnixMs);

        var crc = Crc32C.Compute(destination[..CrcOffset]);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[CrcOffset..], crc);
    }

    /// <summary>Разбирает заголовок из начала буфера.</summary>
    /// <param name="source">Буфер, начинающийся с заголовка; может содержать и следующие за ним байты.</param>
    /// <returns>Успех с заголовком либо причина отказа.</returns>
    public static SegmentHeaderReadResult Read(ReadOnlySpan<byte> source)
    {
        if (source.Length < LogSegmentFormat.HeaderSize)
        {
            return SegmentHeaderReadResult.Truncated(
                $"заголовок занимает {LogSegmentFormat.HeaderSize} байт, в буфере {source.Length}.");
        }

        if (!source[..LogSegmentFormat.MagicSize].SequenceEqual(LogSegmentFormat.Magic))
        {
            return SegmentHeaderReadResult.Corrupt(
                "магия не RBSEGLOG: это не файл сегмента либо повреждено начало.");
        }

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(source[VersionOffset..]);

        if (version != LogSegmentFormat.FormatVersion)
        {
            return SegmentHeaderReadResult.Corrupt(
                $"версия формата {version}: эта реализация понимает только {LogSegmentFormat.FormatVersion}.");
        }

        uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(source[CrcOffset..]);
        uint computedCrc = Crc32C.Compute(source[..CrcOffset]);

        if (storedCrc != computedCrc)
        {
            return SegmentHeaderReadResult.Corrupt(
                $"CRC32C заголовка не совпала: в поле 0x{storedCrc:X8}, вычислено 0x{computedCrc:X8}.");
        }

        long baseIndex = BinaryPrimitives.ReadInt64LittleEndian(source[BaseIndexOffset..]);

        if (baseIndex < 1)
        {
            return SegmentHeaderReadResult.Corrupt(
                $"baseIndex {baseIndex} меньше единицы.");
        }

        return SegmentHeaderReadResult.Success(new SegmentHeader(new LogIndex(baseIndex), BinaryPrimitives.ReadInt64LittleEndian(source[CreatedAtOffset..])));
    }
}
