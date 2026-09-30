using System.Buffers.Binary;
using RaftBroker.Core.Primitives;
using RaftBroker.Storage.Integrity;

namespace RaftBroker.Storage.Log;

/// <summary>
/// Кодек одного фрейма журнала: байты формата версии 1 в обе стороны.
/// </summary>
/// <remarks>
/// Раскладка и правила - из <c>docs/formats/log-segment.md</c>, §4:
/// <c>frameLength (u32) | recordType (u8) | term (i64) | index (i64) | payload | crc32c (u32)</c>,
/// где <c>frameLength = 17 + payload.Length</c> и не включает себя и CRC, а CRC считается
/// по байтам <c>recordType..payload</c> (то есть по <c>frameLength</c> байтам от смещения 4).
/// Все числа - little-endian, независимо от платформы.
/// </remarks>
public static class LogFrameCodec
{
    /// <summary>Размер фрейма на диске для payload заданной длины.</summary>
    /// <param name="payloadLength">Длина payload в байтах.</param>
    /// <returns>Число байтов, которое займёт фрейм.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Длина payload вне границ формата.</exception>
    public static int GetFrameSize(int payloadLength)
    {
        if (payloadLength < 0 || payloadLength > LogSegmentFormat.MaxPayloadLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payloadLength),
                payloadLength,
                $"Длина payload должна быть в диапазоне 0..{LogSegmentFormat.MaxPayloadLength}.");
        }

        return LogSegmentFormat.FrameOverhead + payloadLength;
    }

    /// <summary>Записывает фрейм в буфер.</summary>
    /// <param name="destination">Буфер, начиная с которого пишется фрейм.</param>
    /// <param name="record">Запись; её поля уже проверены конструктором.</param>
    /// <exception cref="ArgumentException">Буфер короче <see cref="GetFrameSize"/>.</exception>
    public static void Write(Span<byte> destination, LogRecord record)
    {
        var frameSize = GetFrameSize(record.Payload.Length);

        if (destination.Length < frameSize)
        {
            throw new ArgumentException(
                $"Для фрейма нужно {frameSize} байт, в буфере {destination.Length}.",
                nameof(destination));
        }

        var frameLength = LogSegmentFormat.MinFrameLength + record.Payload.Length;

        BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)frameLength);
        destination[4] = (byte)record.Type;
        BinaryPrimitives.WriteInt64LittleEndian(destination[5..], record.Term.Value);
        BinaryPrimitives.WriteInt64LittleEndian(destination[13..], record.Index.Value);
        record.Payload.Span.CopyTo(destination[LogSegmentFormat.FrameHeaderSize..]);

        var crc = Crc32C.Compute(destination.Slice(4, frameLength));
        BinaryPrimitives.WriteUInt32LittleEndian(destination[(LogSegmentFormat.FrameHeaderSize + record.Payload.Length)..], crc);
    }

    /// <summary>Разбирает фрейм с начала буфера.</summary>
    /// <param name="source">Буфер, начинающийся с фрейма; может содержать и следующие за ним байты.</param>
    /// <returns>
    /// Успех с записью и числом занятых байт, <see cref="LogReadStatus.Truncated"/>, если фрейм
    /// не помещается в буфере, или <see cref="LogReadStatus.Corrupt"/>, если фрейм есть целиком,
    /// но нарушает формат.
    /// </returns>
    public static LogFrameReadResult Read(ReadOnlySpan<byte> source)
    {
        if (source.Length < sizeof(uint))
        {
            return LogFrameReadResult.Truncated(
                $"нет поля frameLength: в буфере {source.Length} байт из 4.");
        }

        uint frameLength = BinaryPrimitives.ReadUInt32LittleEndian(source);

        if (frameLength < LogSegmentFormat.MinFrameLength)
        {
            return LogFrameReadResult.Corrupt(
                $"frameLength {frameLength} меньше минимума {LogSegmentFormat.MinFrameLength}.");
        }

        if (frameLength > LogSegmentFormat.MaxFrameLength)
        {
            return LogFrameReadResult.Corrupt(
                $"frameLength {frameLength} больше максимума {LogSegmentFormat.MaxFrameLength}.");
        }

        int totalSize = sizeof(uint) + (int)frameLength + LogSegmentFormat.FrameCrcSize;

        if (source.Length < totalSize)
        {
            return LogFrameReadResult.Truncated(
                $"фрейм занимает {totalSize} байт, в буфере {source.Length}.");
        }

        byte rawType = source[4];
        var type = (LogRecordType)rawType;

        if (!LogRecord.IsKnown(type))
        {
            return LogFrameReadResult.Corrupt(
                $"неизвестный recordType 0x{rawType:X2}: таблица типов закрыта.");
        }

        int payloadLength = (int)frameLength - LogSegmentFormat.MinFrameLength;

        if (type == LogRecordType.Noop && payloadLength != 0)
        {
            return LogFrameReadResult.Corrupt(
                $"запись noop с payload {payloadLength} байт.");
        }

        long term = BinaryPrimitives.ReadInt64LittleEndian(source[5..]);
        long index = BinaryPrimitives.ReadInt64LittleEndian(source[13..]);

        if (term < 0)
        {
            return LogFrameReadResult.Corrupt($"терм {term} отрицателен.");
        }

        if (index < 1)
        {
            return LogFrameReadResult.Corrupt($"индекс записи {index} меньше единицы.");
        }

        uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(source[(LogSegmentFormat.FrameHeaderSize + payloadLength)..]);
        uint computedCrc = Crc32C.Compute(source.Slice(sizeof(uint), (int)frameLength));

        if (storedCrc != computedCrc)
        {
            return LogFrameReadResult.Corrupt(
                $"CRC32C не совпала: в поле 0x{storedCrc:X8}, вычислено 0x{computedCrc:X8}.");
        }

        var payload = source.Slice(LogSegmentFormat.FrameHeaderSize, payloadLength).ToArray();
        var record = new LogRecord(new LogIndex(index), new Term(term), type, payload);

        return LogFrameReadResult.Success(record);
    }
}
