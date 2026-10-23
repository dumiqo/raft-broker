namespace RaftBroker.Storage.HardState;

/// <summary>
/// Константы формата файла hard state - <c>docs/formats/hard-state.md</c>.
/// </summary>
/// <remarks>
/// Файл - последовательность самодостаточных записей, каждая с CRC и порядковым номером;
/// текущее состояние - последняя целая запись. Отдельного заголовка файла нет: он ничего
/// не сообщал бы о состоянии, а испортить заголовок - значит потерять весь файл.
/// </remarks>
public static class HardStateFormat
{
    /// <summary>Имя файла в каталоге узла.</summary>
    public const string FileName = "hard-state";

    /// <summary>Сигнатура записи: отличает новую запись от мусора, оставленного оборванной.</summary>
    public const int MagicSize = 8;

    /// <summary>Порядковый номер записи: только для диагностики, истина - порядок в файле.</summary>
    public const int SequenceSize = 8;

    /// <summary>Поле <c>currentTerm</c>.</summary>
    public const int TermSize = 8;

    /// <summary>Длина <c>votedFor</c> в байтах UTF-8; ноль означает «голос не отдан».</summary>
    public const int LengthSize = 2;

    /// <summary>Смещение длины идентификатора в заголовке записи.</summary>
    public const int LengthOffset = MagicSize + SequenceSize + TermSize;

    /// <summary>Заголовок записи: сигнатура, номер, term и длина идентификатора.</summary>
    public const int HeaderSize = MagicSize + SequenceSize + TermSize + LengthSize;

    /// <summary>CRC-32C записи: единственное, что отличает целую запись от оборванной.</summary>
    public const int CrcSize = 4;

    /// <summary>
    /// Предел длины <c>votedFor</c> в байтах UTF-8: 128 символов идентификатора, худший случай - 4 байта на символ.
    /// </summary>
    public const int MaxVotedForLength = 128 * 4;

    /// <summary>Размер самой длинной записи.</summary>
    public const int MaxRecordSize = HeaderSize + MaxVotedForLength + CrcSize;

    /// <summary>Сигнатура записи hard state.</summary>
    public static ReadOnlySpan<byte> Magic => "RBHSREC1"u8;
}
