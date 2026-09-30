using FsCheck;
using FsCheck.Xunit;
using RaftBroker.Storage.Log;

namespace RaftBroker.UnitTests.Storage;

/// <summary>
/// S1-T02: property-тесты кодеков записи и заголовка.
/// </summary>
/// <remarks>
/// Это тот самый критерий задачи "10 000 случайных записей проходят round-trip".
/// Генераторы живут в <see cref="StorageArbitraries"/> и выдают только корректные значения,
/// поэтому свойство проверяет кодек, а не собственную способность строить мусор.
/// </remarks>
public sealed class LogCodecPropertyTests
{
    /// <summary>Критерий задачи: десять тысяч случайных записей должны пережить запись и чтение.</summary>
    [Property(MaxTest = 10_000, Arbitrary = new[] { typeof(StorageArbitraries) })]
    public void RoundTrip_PreservesTheRecord(LogRecord record)
    {
        byte[] frame = new byte[LogFrameCodec.GetFrameSize(record.Payload.Length)];

        LogFrameCodec.Write(frame, record);

        var result = LogFrameCodec.Read(frame);

        result.Status.Should().Be(LogReadStatus.Success, result.Reason);
        result.BytesConsumed.Should().Be(frame.Length);
        result.Record.Should().Be(record);
    }

    /// <summary>
    /// Формат обещает, что CRC ловит порчу: любое изменение байта в области, покрытой CRC,
    /// обязано закончиться отказом, а не "похоже, запись в порядке".
    /// </summary>
    [Property(MaxTest = 2_000, Arbitrary = new[] { typeof(StorageArbitraries) })]
    public void AnyByteFlipInsideTheCrcRegion_IsCorruption(LogRecord record, int flipPosition, byte mask)
    {
        byte[] frame = new byte[LogFrameCodec.GetFrameSize(record.Payload.Length)];
        LogFrameCodec.Write(frame, record);

        int crcRegionLength = LogSegmentFormat.MinFrameLength + record.Payload.Length;
        int position = sizeof(uint) + (int)((uint)flipPosition % (uint)crcRegionLength);
        frame[position] ^= mask == 0 ? (byte)0x01 : mask;

        // Поле длины не тронуто, поэтому фрейм по-прежнему целиком лежит в буфере.
        var result = LogFrameCodec.Read(frame);

        result.Status.Should().Be(LogReadStatus.Corrupt, $"изменён байт {position}");
    }

    /// <summary>
    /// Второе обещание формата: чтение никогда не падает исключением на порче.
    /// Исключение в хранилище - это падение узла на повреждённом файле, а нужно решение
    /// "усечь или отказать" с внятной причиной (см. §9).
    /// </summary>
    [Property(MaxTest = 1_000)]
    public void ReadingArbitraryBytes_NeverThrows(byte[]? bytes)
    {
        var result = LogFrameCodec.Read(bytes ?? []);

        if (result.Status == LogReadStatus.Success)
        {
            result.Record.Should().NotBeNull();
            result.BytesConsumed.Should().Be(LogFrameCodec.GetFrameSize(result.Record!.Payload.Length));
        }
        else
        {
            result.Record.Should().BeNull();
            result.Reason.Should().NotBeNullOrWhiteSpace();
        }
    }

    /// <summary>Чтение заголовка тоже обязано быть тотальным.</summary>
    [Property(MaxTest = 1_000)]
    public void ReadingArbitraryHeaderBytes_NeverThrows(byte[]? bytes)
    {
        var result = SegmentHeaderCodec.Read(bytes ?? []);

        if (result.Status == LogReadStatus.Success)
        {
            result.Header!.BaseIndex.Value.Should().BeGreaterThan(0);
        }
        else
        {
            result.Header.Should().BeNull();
            result.Reason.Should().NotBeNullOrWhiteSpace();
        }
    }
}
