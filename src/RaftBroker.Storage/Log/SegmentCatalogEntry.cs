using RaftBroker.Core.Primitives;

namespace RaftBroker.Storage.Log;

/// <summary>
/// Сегмент, каким его видит каталог журнала: файл, вид по §8 и что в нём лежит.
/// </summary>
/// <remarks>
/// <para>
/// Каталог (<see cref="SegmentCatalog"/>) отдаёт именно эти данные, потому что сам он читателей
/// не держит: он открывает каждый сегмент, сканирует его и закрывает. Хранилищу
/// (<see cref="LogStore"/>) нужны только базовый индекс и путь - остальное оно находит
/// сканированием при чтении; восстановлению (<c>S1-T07</c>) нужны <see cref="Kind"/>
/// и итог сканирования, чтобы решить судьбу файла, не читая его второй раз.
/// </para>
/// <para>
/// <see cref="LastIndex"/> и <see cref="LastTerm"/> - <c>null</c> у пустого сегмента (только
/// заголовок): §8 разрешает такой файл последним, и он ничего не добавляет к журналу.
/// </para>
/// </remarks>
public readonly record struct SegmentCatalogEntry
{
    /// <summary>Базовый индекс сегмента: он же закодирован в имени файла (§7).</summary>
    public required LogIndex BaseIndex { get; init; }

    /// <summary>Индекс последней записи; <c>null</c> для пустого сегмента.</summary>
    public required LogIndex? LastIndex { get; init; }

    /// <summary>Терм последней записи; <c>null</c> для пустого сегмента.</summary>
    public required Term? LastTerm { get; init; }

    /// <summary>Длина файла на момент открытия каталога, в байтах.</summary>
    public required long Length { get; init; }

    /// <summary>Путь файла сегмента.</summary>
    public required string Path { get; init; }

    /// <summary>Запечатан ли сегмент (§8): только последний может быть незапечатанным.</summary>
    public required SegmentKind Kind { get; init; }
}
