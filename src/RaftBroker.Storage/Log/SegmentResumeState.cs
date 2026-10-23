using RaftBroker.Core.Primitives;

namespace RaftBroker.Storage.Log;

/// <summary>
/// Состояние последнего сегмента, к которому писатель должен дописать: чем он кончается и сколько
/// в нём байт.
/// </summary>
/// <remarks>
/// <para>
/// Появляется в <c>S1-T06</c> потому, что журнал продолжает существующий сегмент, а не начинает
/// новый файл: после усечения конфликтующего хвоста (<c>TruncateSuffixAsync</c>) и после
/// перезапуска (<c>S1-T07</c>). Начать новый сегмент на том же месте нельзя - файл с базовым
/// индексом <c>lastIndex + 1</c> уже существует (§7), - а дописать его без знания последнего
/// индекса и терма нельзя: писатель обязан продолжать проверять непрерывность индексов и
/// неубывание терма (§4).
/// </para>
/// <para>
/// Значения берёт вызывающий: он получил их сканированием сегмента, а не догадкой. Проверяются
/// они в <see cref="SegmentWriter.ResumeAsync"/> - писатель не имеет права молча принять
/// противоречивое состояние (например, <see cref="LastIndex"/> меньше <see cref="BaseIndex"/>
/// при непустом файле).
/// </para>
/// </remarks>
public readonly record struct SegmentResumeState
{
    /// <summary>Базовый индекс сегмента: он же закодирован в имени файла.</summary>
    public required LogIndex BaseIndex { get; init; }

    /// <summary>Индекс последней записи сегмента; <c>BaseIndex - 1</c> для пустого сегмента.</summary>
    public required LogIndex LastIndex { get; init; }

    /// <summary>Терм последней записи сегмента; терм 0 для пустого сегмента.</summary>
    public required Term LastTerm { get; init; }

    /// <summary>Текущая длина файла в байтах, включая заголовок.</summary>
    public required long Length { get; init; }
}
