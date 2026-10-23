using RaftBroker.Core.Primitives;

namespace RaftBroker.Storage.Log;

/// <summary>
/// Итог восстановления одного сегмента: что сделано, на каком смещении валидный префикс кончается
/// и что в этом префиксе было.
/// </summary>
/// <remarks>
/// <para>
/// Результат, а не исключение, по той же причине, что у читателя: порча - это факт о файле, и
/// решение «отказать» принимает не код, читающий байты, а восстановление. Исключение остаётся
/// для ошибок вызывающего (нет файла, неверные аргументы).
/// </para>
/// <para>
/// <see cref="ValidEndOffset"/> и <see cref="LastIndex"/> описывают валидный префикс после
/// восстановления, а не то, что было на диске до него: при <see cref="SegmentRecoveryStatus.Refused"/>
/// изменения не вносились, поэтому это просто конец разобранной части. При отказе до сканирования
/// (нечитаемый заголовок) валидного префикса нет вовсе, и поля нулевые: <see cref="Reason"/>
/// говорит, на чём именно восстановление остановилось.
/// </para>
/// </remarks>
public readonly struct SegmentRecoveryResult
{
    private SegmentRecoveryResult(
        SegmentRecoveryStatus status,
        string path,
        string reason,
        long droppedBytes,
        SegmentScanResult? scan)
    {
        Status = status;
        Path = path;
        Reason = reason;
        DroppedBytes = droppedBytes;
        Scan = scan;
    }

    /// <summary>Что восстановление сделало с сегментом.</summary>
    public SegmentRecoveryStatus Status { get; }

    /// <summary>Путь сегмента: попадает в отчёт о восстановлении.</summary>
    public string Path { get; }

    /// <summary>Почему восстановление поступило так, а не иначе; пустая строка при <see cref="SegmentRecoveryStatus.Intact"/>.</summary>
    public string Reason { get; }

    /// <summary>
    /// Итог прохода по валидному префиксу; <c>null</c>, если разбор не начинался
    /// (заголовок нечитаем, валидного префикса нет).
    /// </summary>
    /// <remarks>
    /// Скан хранится целиком, а не копией полей: у <see cref="SegmentScanResult"/> те же
    /// <see cref="SegmentScanResult.ValidEndOffset"/>, <see cref="SegmentScanResult.FrameCount"/>,
    /// <see cref="SegmentScanResult.LastIndex"/> и <see cref="SegmentScanResult.LastTerm"/>,
    /// и две копии одного набора разъехались бы при первом же новом поле.
    /// </remarks>
    public SegmentScanResult? Scan { get; }

    /// <summary>Смещение за последним валидным фреймом: конец валидного префикса.</summary>
    public long ValidEndOffset => Scan?.ValidEndOffset ?? 0;

    /// <summary>
    /// Сколько байт журнала отброшено: длина усечённого хвоста при
    /// <see cref="SegmentRecoveryStatus.TruncatedTail"/> или длина удалённого файла при
    /// <see cref="SegmentRecoveryStatus.Removed"/>. Ноль, если на диске ничего не менялось.
    /// </summary>
    public long DroppedBytes { get; }

    /// <summary>Число валидных записей в префиксе.</summary>
    public int FrameCount => Scan?.FrameCount ?? 0;

    /// <summary>Индекс последней валидной записи; <c>null</c>, если записей нет.</summary>
    public LogIndex? LastIndex => Scan?.LastIndex;

    /// <summary>Терм последней валидной записи; <c>null</c>, если записей нет.</summary>
    public Term? LastTerm => Scan?.LastTerm;

    /// <summary>Старт с этим сегментом запрещён: порча.</summary>
    public bool IsRefused => Status == SegmentRecoveryStatus.Refused;

    /// <summary>Восстановление изменило файл на диске (усекло хвост или удалило файл).</summary>
    public bool ChangedFile => Status is SegmentRecoveryStatus.TruncatedTail or SegmentRecoveryStatus.Removed;

    /// <summary>Сегмент разобран до конца файла.</summary>
    /// <param name="path">Путь сегмента.</param>
    /// <param name="scan">Итог прохода по неповреждённому сегменту.</param>
    internal static SegmentRecoveryResult Intact(string path, SegmentScanResult scan)
        => new(SegmentRecoveryStatus.Intact, path, string.Empty, droppedBytes: 0, scan);

    /// <summary>Оборванный хвост последнего сегмента усечён.</summary>
    /// <param name="path">Путь сегмента.</param>
    /// <param name="reason">Причина остановки разбора.</param>
    /// <param name="droppedBytes">Сколько байт усечено.</param>
    /// <param name="scan">Итог прохода: валидный префикс.</param>
    internal static SegmentRecoveryResult TruncatedTail(string path, string reason, long droppedBytes, SegmentScanResult scan)
        => new(SegmentRecoveryStatus.TruncatedTail, path, reason, droppedBytes, scan);

    /// <summary>Оборванное создание сегмента удалено.</summary>
    /// <param name="path">Путь сегмента.</param>
    /// <param name="reason">Причина: чем файл не устроил.</param>
    /// <param name="fileLength">Длина удалённого файла.</param>
    internal static SegmentRecoveryResult Removed(string path, string reason, long fileLength)
        => new(SegmentRecoveryStatus.Removed, path, reason, fileLength, scan: null);

    /// <summary>Порча после разбора: видно, до какого смещения сегмент был валиден.</summary>
    /// <param name="path">Путь сегмента.</param>
    /// <param name="reason">Что именно не сошлось и почему это порча, а не обрыв.</param>
    /// <param name="scan">Итог прохода до места повреждения.</param>
    internal static SegmentRecoveryResult Refused(string path, string reason, SegmentScanResult scan)
        => new(SegmentRecoveryStatus.Refused, path, reason, droppedBytes: 0, scan);

    /// <summary>Порча до разбора: заголовок нечитаем, валидного префикса нет.</summary>
    /// <param name="path">Путь сегмента.</param>
    /// <param name="reason">Что именно не сошлось.</param>
    internal static SegmentRecoveryResult RefusedBeforeScan(string path, string reason)
        => new(SegmentRecoveryStatus.Refused, path, reason, droppedBytes: 0, scan: null);
}
