using RaftBroker.Storage.IO;

namespace RaftBroker.Storage.Log;

/// <summary>
/// Восстановление одного сегмента журнала: оборванный хвост усекается, оборванное создание
/// удаляется, порча останавливает старт.
/// </summary>
/// <remarks>
/// <para>
/// Алгоритм - §9.2, §9.3, §9.4, §9.5: заголовок разбирает <see cref="SegmentReader.OpenAsync"/>,
/// проход по фреймам - <see cref="SegmentReader.ScanAsync"/>, а решение «обрыв или порча» даёт
/// проба продолжения (§9.5 шаг 5) через примитивы <see cref="SegmentReader.ReadFrameLengthAsync"/>
/// и <see cref="SegmentReader.ReadFrameAtAsync"/>. Читатель сегмента вердикта не выносит: ему
/// неоткуда знать, запечатан ли сегмент (§8), а от этого зависит, разрешено ли усечение.
/// </para>
/// <para>
/// Восстановление меняет диск - единственное место в хранилище, которое это делает не по команде
/// приложения. Право отбросить хвост держится на том, что подтверждение клиенту выдаётся только
/// после fsync (<c>S1-T03</c>, инвариант R6): недописанный хвост подтверждённым быть не мог.
/// Порча же означает, что после неё есть валидные данные, и усечение потеряло бы закоммиченную
/// историю, поэтому тут восстановление отказывает и оставляет файл как есть (решение «починить
/// руками» описано не этим документом).
/// </para>
/// <para>
/// Файл читается дважды только при обрыве: заголовок и фреймы один раз, затем проба продолжения
/// (4 байта плюс один фрейм за повреждением). Повторного сканирования после усечения нет: префикс
/// проверен тем же проходом, а пишущих рядом нет - восстановление идёт при старте, когда писатель
/// ещё не создан. Что усечение действительно оставляет валидный префикс, проверяют тесты.
/// </para>
/// <para>
/// Класс без состояния и не потокобезопасен ровно в том же смысле, что и читатель: два
/// одновременных восстановления одного каталога - ошибка вызывающего.
/// </para>
/// </remarks>
public static class SegmentRecovery
{
    /// <summary>
    /// Разбирает сегмент и приводит его к состоянию, пригодному для старта.
    /// </summary>
    /// <param name="fileSystem">Файловая система: рантайм-реализация или тестовый дубль.</param>
    /// <param name="path">Путь сегмента; имя обязано соответствовать <c>baseIndex</c> заголовка (§7).</param>
    /// <param name="kind">Запечатан ли сегмент: усекать хвост разрешено только последнему (§8, §9.2).</param>
    /// <param name="cancellationToken">Отмена между операциями.</param>
    /// <returns>Что сделано с сегментом и где кончается валидный префикс.</returns>
    /// <exception cref="FileNotFoundException">Файла нет: это ошибка вызывающего, а не порча внутри файла.</exception>
    public static async ValueTask<SegmentRecoveryResult> RecoverAsync(
        IStorageFileSystem fileSystem,
        string path,
        SegmentKind kind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var open = await SegmentReader.OpenAsync(fileSystem, path, cancellationToken).ConfigureAwait(false);

        if (!open.IsSuccess)
        {
            return await RecoverUnreadableHeaderAsync(fileSystem, path, kind, open, cancellationToken).ConfigureAwait(false);
        }

        var reader = open.Reader!;
        long length;
        SegmentScanResult scan;
        SegmentRecoveryResult? decision = null;

        try
        {
            scan = await reader.ScanAsync(cancellationToken).ConfigureAwait(false);
            length = reader.Length;

            if (scan.IsComplete)
            {
                decision = SegmentRecoveryResult.Intact(path, scan);
            }
            else if (kind == SegmentKind.Sealed)
            {
                decision = SegmentRecoveryResult.Refused(path, SealedTailReason(scan), scan);
            }
            else if (await HasContinuationAsync(reader, scan, cancellationToken).ConfigureAwait(false))
            {
                decision = SegmentRecoveryResult.Refused(path, MiddleCorruptionReason(scan), scan);
            }
        }
        finally
        {
            await reader.DisposeAsync().ConfigureAwait(false);
        }

        if (decision is { } decided)
        {
            return decided;
        }

        // Усечение идёт после закрытия читателя: работать с файлом под открытым дескриптором -
        // лишняя ставка на режимы разделения, а всё нужное из файла уже прочитано.
        await fileSystem.TruncateAsync(path, scan.ValidEndOffset, cancellationToken).ConfigureAwait(false);

        return SegmentRecoveryResult.TruncatedTail(path, scan.Reason, length - scan.ValidEndOffset, scan);
    }

    /// <summary>Разбирает случай, когда заголовок прочитать не удалось (§9.3).</summary>
    private static async ValueTask<SegmentRecoveryResult> RecoverUnreadableHeaderAsync(
        IStorageFileSystem fileSystem,
        string path,
        SegmentKind kind,
        SegmentOpenResult open,
        CancellationToken cancellationToken)
    {
        // §9.3: заголовок записывается и fsync-ается до первого фрейма, поэтому в коротком файле
        // подтверждённых записей быть не может. Удаление, а не игнорирование: оставленный файл
        // ломал бы непрерывность индексов при каждом следующем старте.
        if (kind == SegmentKind.Last && open.Status == LogReadStatus.Truncated)
        {
            await fileSystem.DeleteAsync(path, cancellationToken).ConfigureAwait(false);

            return SegmentRecoveryResult.Removed(
                path,
                $"файл короче заголовка ({open.Length} байт): оборванное создание сегмента (§9.3). {open.Reason}",
                open.Length);
        }

        var reason = open.Status == LogReadStatus.Truncated
            ? $"файл короче заголовка ({open.Length} байт), но сегмент запечатан: "
                + $"запечатанный сегмент обязан быть полным (§9.3). {open.Reason}"
            : kind == SegmentKind.Last
                ? "заголовок последнего сегмента нечитаем: отказ от старта, а не удаление, - "
                    + $"на смещении {LogSegmentFormat.HeaderSize} могли остаться валидные фреймы (§9.3). {open.Reason}"
                : $"заголовок запечатанного сегмента нечитаем: порча (§9.4). {open.Reason}";

        return SegmentRecoveryResult.RefusedBeforeScan(path, reason);
    }

    /// <summary>
    /// Проба продолжения (§9.5 шаг 5): есть ли за повреждением полный CRC-валидный фрейм.
    /// </summary>
    /// <remarks>
    /// Проверяется ровно то, что требует документ, - читаемость фрейма на смещении
    /// <c>p + 4 + L + 4</c>. Непрерывность индекса у этого фрейма не проверяется сознательно:
    /// лишнее требование сделало бы пробу строже и превращало бы настоящую порчу в усечение,
    /// то есть в потерю данных (§9.6).
    /// </remarks>
    private static async ValueTask<bool> HasContinuationAsync(
        SegmentReader reader,
        SegmentScanResult scan,
        CancellationToken cancellationToken)
    {
        var frameLength = await reader.ReadFrameLengthAsync(scan.ValidEndOffset, cancellationToken).ConfigureAwait(false);

        if (frameLength is not { } length)
        {
            // Прочитать длину нельзя или она вне границ: продолжения за повреждением быть не может.
            return false;
        }

        var nextOffset = scan.ValidEndOffset + sizeof(uint) + length + LogSegmentFormat.FrameCrcSize;
        var next = await reader.ReadFrameAtAsync(nextOffset, cancellationToken).ConfigureAwait(false);

        return next.IsSuccess;
    }

    private static string SealedTailReason(SegmentScanResult scan)
        => "запечатанный сегмент обязан разбираться от заголовка до конца файла ровно на границе фрейма "
            + $"(§9.5 шаг 6), а разбор остановился на смещении {scan.ValidEndOffset}: {scan.Reason}";

    private static string MiddleCorruptionReason(SegmentScanResult scan)
        => $"порча в середине: за повреждением на смещении {scan.ValidEndOffset} есть полный CRC-валидный фрейм "
            + $"(§9.4, §9.5 шаг 5), поэтому оборванным хвостом это быть не может: {scan.Reason}";
}
