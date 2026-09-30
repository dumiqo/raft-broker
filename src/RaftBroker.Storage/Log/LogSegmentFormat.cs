using System.Globalization;
using RaftBroker.Core.Primitives;

namespace RaftBroker.Storage.Log;

/// <summary>
/// Константы формата сегмента журнала, версия 1.
/// </summary>
/// <remarks>
/// Числа здесь - не настройки, а нормативные значения из <c>docs/formats/log-segment.md</c>:
/// изменение любого из них меняет байты на диске и требует повышения
/// <see cref="FormatVersion"/> вместе с миграцией (см. §10 документа).
/// </remarks>
public static class LogSegmentFormat
{
    /// <summary>Версия формата, которую пишет и понимает эта реализация.</summary>
    public const int FormatVersion = 1;

    /// <summary>Размер заголовка сегмента в байтах.</summary>
    public const int HeaderSize = 30;

    /// <summary>Размер магии файла в байтах.</summary>
    public const int MagicSize = 8;

    /// <summary>Максимальная длина payload одной записи: 1 МиБ.</summary>
    public const int MaxPayloadLength = 1_048_576;

    /// <summary>Размер части фрейма до payload: <c>frameLength</c>, тип, терм, индекс.</summary>
    public const int FrameHeaderSize = 21;

    /// <summary>Размер контрольной суммы фрейма в байтах.</summary>
    public const int FrameCrcSize = 4;

    /// <summary>Накладные расходы фрейма: всё, кроме payload.</summary>
    public const int FrameOverhead = FrameHeaderSize + FrameCrcSize;

    /// <summary>
    /// Минимальная длина <c>frameLength</c>: тип записи, терм и индекс без payload.
    /// Ноль и меньшие значения запрещены форматом.
    /// </summary>
    public const int MinFrameLength = 1 + 8 + 8;

    /// <summary>Максимальная длина <c>frameLength</c>: минимальная плюс лимит payload.</summary>
    public const int MaxFrameLength = MinFrameLength + MaxPayloadLength;

    /// <summary>Номинальный максимум размера сегмента: 64 МиБ.</summary>
    public const long MaxSegmentSize = 67_108_864;

    /// <summary>Суффикс имени файла сегмента.</summary>
    public const string FileNameSuffix = ".seg";

    /// <summary>Число десятичных цифр в имени файла: постоянная длина и лексикографический порядок.</summary>
    public const int FileNameDigits = 20;

    /// <summary>Магия файла сегмента. Версии в ней нет: версия живёт только в заголовке.</summary>
    public static ReadOnlySpan<byte> Magic => "RBSEGLOG"u8;

    /// <summary>
    /// Имя файла сегмента по индексу его первой записи: <c>{baseIndex:D20}.seg</c> (§7).
    /// </summary>
    /// <param name="baseIndex">Индекс первой записи сегмента.</param>
    /// <returns>Имя файла без каталога.</returns>
    /// <remarks>
    /// Двадцать цифр выбраны так, чтобы длина имени была постоянной, а лексикографический
    /// порядок имён совпадал с числовым (<see cref="long"/> занимает не больше 19 цифр).
    /// Культура инвариантная: разделителей групп в имени быть не должно ни при каких настройках.
    /// </remarks>
    public static string GetFileName(LogIndex baseIndex)
        => baseIndex.Value.ToString("D" + FileNameDigits, CultureInfo.InvariantCulture) + FileNameSuffix;
}
