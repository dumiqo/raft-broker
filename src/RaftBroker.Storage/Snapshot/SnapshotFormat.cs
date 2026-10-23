using System.Globalization;
using RaftBroker.Core.Primitives;

namespace RaftBroker.Storage.Snapshot;

/// <summary>
/// Константы формата файла снапшота, версия 1.
/// </summary>
/// <remarks>
/// Числа здесь - нормативные значения из <c>docs/formats/snapshot.md</c>, а не настройки:
/// изменение любого из них меняет байты на диске и требует новой магии вместе с миграцией (§9).
/// </remarks>
public static class SnapshotFormat
{
    /// <summary>Размер заголовка файла снапшота в байтах.</summary>
    public const int HeaderSize = 48;

    /// <summary>Размер магии файла в байтах.</summary>
    public const int MagicSize = 8;

    /// <summary>Смещение контрольной суммы состояния внутри заголовка.</summary>
    public const int StateCrcOffset = 40;

    /// <summary>Смещение контрольной суммы заголовка внутри заголовка.</summary>
    public const int HeaderCrcOffset = 44;

    /// <summary>Максимальная длина состояния: 1 ГиБ.</summary>
    public const long MaxStateLength = 1L << 30;

    /// <summary>Префикс имени файла снапшота.</summary>
    public const string FileNamePrefix = "snapshot-";

    /// <summary>Суффикс имени опубликованного снапшота.</summary>
    public const string FileNameSuffix = ".snap";

    /// <summary>Суффикс временного файла: снапшотом не считается никогда (§2).</summary>
    public const string TempFileNameSuffix = ".tmp";

    /// <summary>Число десятичных цифр в имени файла: постоянная длина и лексикографический порядок.</summary>
    public const int FileNameDigits = 20;

    /// <summary>Магия файла снапшота: имя формата и версия в одном месте (§4).</summary>
    public static ReadOnlySpan<byte> Magic => "RBSNAP01"u8;

    /// <summary>Имя опубликованного снапшота: <c>snapshot-{lastIncludedIndex:D20}.snap</c> (§2).</summary>
    /// <param name="lastIncludedIndex">Индекс последней записи, вошедшей в снапшот.</param>
    public static string GetFileName(LogIndex lastIncludedIndex)
        => FileNamePrefix
            + lastIncludedIndex.Value.ToString("D" + FileNameDigits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
            + FileNameSuffix;

    /// <summary>Имя временного файла, в который снапшот пишется до публикации (§7).</summary>
    /// <param name="lastIncludedIndex">Индекс последней записи, вошедшей в снапшот.</param>
    public static string GetTempFileName(LogIndex lastIncludedIndex)
        => GetFileName(lastIncludedIndex) + TempFileNameSuffix;

    /// <summary>Временный ли это файл снапшота (то есть недописанный и неопубликованный).</summary>
    /// <param name="fileName">Имя файла без пути.</param>
    public static bool IsTempFileName(string fileName)
        => fileName.StartsWith(FileNamePrefix, StringComparison.Ordinal)
            && fileName.EndsWith(TempFileNameSuffix, StringComparison.Ordinal);

    /// <summary>Разбирает имя опубликованного снапшота обратно в индекс.</summary>
    /// <param name="fileName">Имя файла без пути.</param>
    /// <param name="lastIncludedIndex">Индекс из имени; 0, если имя снапшотом не является.</param>
    /// <returns><c>true</c>, если имя имеет вид <c>snapshot-{index:D20}.snap</c>.</returns>
    public static bool TryParseFileName(string fileName, out LogIndex lastIncludedIndex)
    {
        lastIncludedIndex = LogIndex.Empty;

        if (!fileName.StartsWith(FileNamePrefix, StringComparison.Ordinal)
            || !fileName.EndsWith(FileNameSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        var digits = fileName.AsSpan(FileNamePrefix.Length, fileName.Length - FileNamePrefix.Length - FileNameSuffix.Length);

        if (digits.Length != FileNameDigits
            || !long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            || index <= 0)
        {
            return false;
        }

        lastIncludedIndex = new LogIndex(index);
        return true;
    }
}
