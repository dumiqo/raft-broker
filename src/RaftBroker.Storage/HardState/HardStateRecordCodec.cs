using System.Buffers.Binary;
using System.Text;
using RaftBroker.Core.Primitives;
using RaftBroker.Storage.Integrity;

namespace RaftBroker.Storage.HardState;

/// <summary>
/// Кодек записи hard state - <c>docs/formats/hard-state.md</c>.
/// </summary>
/// <remarks>
/// Одна запись самодостаточна: сигнатура, номер, term, длина и байты <c>votedFor</c>, CRC-32C.
/// Поэтому файл читается сканированием с начала, а оборванная запись - это запись, до которой
/// не хватает байтов или у которой не сходится CRC. Порча внутри файла отличается от обрыва
/// хвоста: целая по длине запись с плохим CRC - это ошибка, а не «взять предыдущую» (иначе
/// после рестарта можно проголосовать второй раз в том же term).
/// </remarks>
public static class HardStateRecordCodec
{
    /// <summary>Размер записи для указанной длины идентификатора.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Длина вне <c>[0, MaxVotedForLength]</c>.</exception>
    public static int GetRecordSize(int votedForLength)
    {
        if (votedForLength < 0 || votedForLength > HardStateFormat.MaxVotedForLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(votedForLength),
                votedForLength,
                $"Длина votedFor вне [0, {HardStateFormat.MaxVotedForLength}].");
        }

        return HardStateFormat.HeaderSize + votedForLength + HardStateFormat.CrcSize;
    }

    /// <summary>Пишет запись целиком в буфер ровно нужной длины.</summary>
    /// <param name="destination">Буфер размером <see cref="GetRecordSize"/>.</param>
    /// <param name="sequence">Номер записи.</param>
    /// <param name="state">Состояние.</param>
    /// <exception cref="ArgumentException">
    /// Буфер не того размера или идентификатор узла не помещается в предел формата.
    /// </exception>
    public static void Write(Span<byte> destination, long sequence, HardState state)
    {
        var votedFor = EncodeVotedFor(state.VotedFor);
        var size = GetRecordSize(votedFor.Length);

        if (destination.Length != size)
        {
            throw new ArgumentException($"Запись занимает {size} байт, а буфер - {destination.Length}.", nameof(destination));
        }

        HardStateFormat.Magic.CopyTo(destination);
        BinaryPrimitives.WriteInt64LittleEndian(destination[HardStateFormat.MagicSize..], sequence);
        BinaryPrimitives.WriteInt64LittleEndian(destination[(HardStateFormat.MagicSize + HardStateFormat.SequenceSize)..], state.CurrentTerm.Value);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[HardStateFormat.LengthOffset..], (ushort)votedFor.Length);
        votedFor.CopyTo(destination[HardStateFormat.HeaderSize..]);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[(size - HardStateFormat.CrcSize)..], Crc32C.Compute(destination[..(size - HardStateFormat.CrcSize)]));
    }

    /// <summary>Проверяет сигнатуру в начале записи.</summary>
    public static bool HasMagic(ReadOnlySpan<byte> source)
        => source.Length >= HardStateFormat.MagicSize && source[..HardStateFormat.MagicSize].SequenceEqual(HardStateFormat.Magic);

    /// <summary>Читает длину записи из заголовка: сколько байтов нужно прочитать целиком.</summary>
    /// <exception cref="InvalidDataException">Сигнатура или длина не по формату.</exception>
    public static int ReadRecordSize(ReadOnlySpan<byte> header)
    {
        if (header.Length < HardStateFormat.HeaderSize)
        {
            throw new ArgumentException($"Заголовок записи короче {HardStateFormat.HeaderSize} байт.", nameof(header));
        }

        if (!HasMagic(header))
        {
            throw new InvalidDataException("Сигнатура записи hard state не совпадает: файл испорчен.");
        }

        var votedForLength = BinaryPrimitives.ReadUInt16LittleEndian(header[HardStateFormat.LengthOffset..]);

        if (votedForLength > HardStateFormat.MaxVotedForLength)
        {
            throw new InvalidDataException(
                $"Длина votedFor {votedForLength} больше предела {HardStateFormat.MaxVotedForLength}: файл испорчен.");
        }

        return GetRecordSize(votedForLength);
    }

    /// <summary>Разбирает целую запись, проверяя CRC.</summary>
    /// <exception cref="InvalidDataException">CRC не сходится, term отрицателен или идентификатор узла не разбирается.</exception>
    public static HardStateRecord Read(ReadOnlySpan<byte> record)
    {
        var size = ReadRecordSize(record);

        if (record.Length != size)
        {
            throw new ArgumentException($"Запись занимает {size} байт, а буфер - {record.Length}.", nameof(record));
        }

        var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(record[(size - HardStateFormat.CrcSize)..]);

        if (Crc32C.Compute(record[..(size - HardStateFormat.CrcSize)]) != expectedCrc)
        {
            throw new InvalidDataException("CRC записи hard state не сходится: файл испорчен.");
        }

        var sequence = BinaryPrimitives.ReadInt64LittleEndian(record[HardStateFormat.MagicSize..]);
        var term = BinaryPrimitives.ReadInt64LittleEndian(record[(HardStateFormat.MagicSize + HardStateFormat.SequenceSize)..]);

        if (term < 0)
        {
            throw new InvalidDataException($"Term {term} в hard state отрицателен: файл испорчен.");
        }

        var votedForLength = BinaryPrimitives.ReadUInt16LittleEndian(record[HardStateFormat.LengthOffset..]);
        var votedFor = DecodeVotedFor(record.Slice(HardStateFormat.HeaderSize, votedForLength));

        return new HardStateRecord(sequence, new HardState(new Term(term), votedFor));
    }

    private static byte[] EncodeVotedFor(NodeId? votedFor)
        => votedFor is null ? [] : Encoding.UTF8.GetBytes(votedFor.Value.Value);

    private static NodeId? DecodeVotedFor(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return null;
        }

        var text = Encoding.UTF8.GetString(bytes);

        try
        {
            return new NodeId(text);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException($"votedFor в hard state не является идентификатором узла: {text}", exception);
        }
    }
}
