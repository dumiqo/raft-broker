using System.Buffers.Binary;
using RaftBroker.Core.Primitives;
using RaftBroker.Storage.Integrity;

namespace RaftBroker.Storage.Snapshot;

/// <summary>
/// Разобранный заголовок файла снапшота (§4 формата).
/// </summary>
/// <param name="Metadata">Индекс и терм последней вошедшей записи.</param>
/// <param name="CreatedAtUnixMs">Момент создания по стенным часам.</param>
/// <param name="StateLength">Длина состояния в байтах.</param>
/// <param name="StateCrc32C">CRC-32C состояния, как он записан в заголовке.</param>
public readonly record struct SnapshotHeader(
    SnapshotMetadata Metadata,
    long CreatedAtUnixMs,
    long StateLength,
    uint StateCrc32C)
{
    /// <summary>Полный размер файла, который обязан быть у снапшота с этим заголовком.</summary>
    public long FileLength => SnapshotFormat.HeaderSize + StateLength;
}

/// <summary>
/// Кодек заголовка снапшота: 48 байт, little-endian, два CRC-32C (§4, §5).
/// </summary>
/// <remarks>
/// Разбор возвращает статус, а не исключение: заголовок читается из файла, который мог быть
/// оборван или испорчен, и для вызывающего это ожидаемый исход, а не сбой кода.
/// Проверки идут в порядке §6: магия, CRC заголовка, индекс, длина состояния.
/// </remarks>
public static class SnapshotHeaderCodec
{
    /// <summary>Кодирует заголовок в буфер длиной <see cref="SnapshotFormat.HeaderSize"/>.</summary>
    /// <param name="metadata">Индекс и терм последней вошедшей записи.</param>
    /// <param name="createdAtUnixMs">Момент создания по стенным часам.</param>
    /// <param name="stateLength">Длина состояния в байтах.</param>
    /// <param name="stateCrc32C">CRC-32C состояния.</param>
    /// <param name="destination">Буфер заголовка.</param>
    /// <exception cref="ArgumentException">Буфер короче заголовка.</exception>
    public static void Encode(
        SnapshotMetadata metadata,
        long createdAtUnixMs,
        long stateLength,
        uint stateCrc32C,
        Span<byte> destination)
    {
        if (destination.Length < SnapshotFormat.HeaderSize)
        {
            throw new ArgumentException(
                $"Буфер заголовка снапшота короче {SnapshotFormat.HeaderSize} байт.",
                nameof(destination));
        }

        SnapshotFormat.Magic.CopyTo(destination);

        BinaryPrimitives.WriteInt64LittleEndian(destination[8..], metadata.LastIncludedIndex.Value);
        BinaryPrimitives.WriteInt64LittleEndian(destination[16..], metadata.LastIncludedTerm.Value);
        BinaryPrimitives.WriteInt64LittleEndian(destination[24..], createdAtUnixMs);
        BinaryPrimitives.WriteInt64LittleEndian(destination[32..], stateLength);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[SnapshotFormat.StateCrcOffset..], stateCrc32C);

        var headerCrc = Crc32C.Compute(destination[..SnapshotFormat.HeaderCrcOffset]);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[SnapshotFormat.HeaderCrcOffset..], headerCrc);
    }

    /// <summary>Разбирает заголовок и проверяет его (§6, шаги 2-5).</summary>
    /// <param name="header">Байты заголовка: не меньше <see cref="SnapshotFormat.HeaderSize"/>.</param>
    /// <param name="parsed">Разобранный заголовок при успехе.</param>
    /// <param name="reason">Причина отказа при <c>false</c>.</param>
    /// <returns><c>true</c>, если заголовок цел и пригоден к использованию.</returns>
    public static bool TryDecode(ReadOnlySpan<byte> header, out SnapshotHeader parsed, out string? reason)
    {
        parsed = default;
        reason = null;

        if (header.Length < SnapshotFormat.HeaderSize)
        {
            reason = $"заголовок снапшота оборван: {header.Length} байт из {SnapshotFormat.HeaderSize}";
            return false;
        }

        if (!header[..SnapshotFormat.MagicSize].SequenceEqual(SnapshotFormat.Magic))
        {
            reason = "магия снапшота не совпадает: файл не является снапшотом этого формата";
            return false;
        }

        var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(header[SnapshotFormat.HeaderCrcOffset..]);
        var actualCrc = Crc32C.Compute(header[..SnapshotFormat.HeaderCrcOffset]);

        if (expectedCrc != actualCrc)
        {
            reason = $"CRC заголовка снапшота не совпадает: записано {expectedCrc:X8}, посчитано {actualCrc:X8}";
            return false;
        }

        var lastIncludedIndex = BinaryPrimitives.ReadInt64LittleEndian(header[8..]);
        var lastIncludedTerm = BinaryPrimitives.ReadInt64LittleEndian(header[16..]);
        var createdAtUnixMs = BinaryPrimitives.ReadInt64LittleEndian(header[24..]);
        var stateLength = BinaryPrimitives.ReadInt64LittleEndian(header[32..]);
        var stateCrc32C = BinaryPrimitives.ReadUInt32LittleEndian(header[SnapshotFormat.StateCrcOffset..]);

        if (lastIncludedIndex <= 0)
        {
            reason = $"индекс последней вошедшей записи {lastIncludedIndex} не положителен: снапшота на пустом журнале не бывает";
            return false;
        }

        if (stateLength < 0 || stateLength > SnapshotFormat.MaxStateLength)
        {
            reason = $"длина состояния {stateLength} вне границ [0, {SnapshotFormat.MaxStateLength}]";
            return false;
        }

        parsed = new SnapshotHeader(
            new SnapshotMetadata(new LogIndex(lastIncludedIndex), new Term(lastIncludedTerm)),
            createdAtUnixMs,
            stateLength,
            stateCrc32C);

        return true;
    }
}
