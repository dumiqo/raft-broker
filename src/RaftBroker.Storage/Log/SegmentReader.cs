using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using RaftBroker.Core.Primitives;
using RaftBroker.Storage.IO;

namespace RaftBroker.Storage.Log;

/// <summary>
/// Читатель одного сегмента журнала: разбор заголовка, проход по фреймам, доступ по индексу.
/// </summary>
/// <remarks>
/// <para>
/// Кодек формата версии 1 плюс физическое чтение файла. Сегмент читается по частям: в память
/// попадает один фрейм, а не файл целиком, - лимит payload 1 МиБ задаёт верхнюю границу буфера.
/// Границы сегментов, разрыв диапазона и решение "усечь или отказать" сюда не входят: это
/// хранилище (<c>S1-T06</c>) и восстановление (<c>S1-T05</c>), а читатель сегмента обязан лишь
/// честно сказать, до какого смещения файл валиден (<see cref="SegmentScanResult"/>).
/// </para>
/// <para>
/// Порядок работы - три шага: <see cref="OpenAsync"/> (заголовок), <see cref="ScanAsync"/> (один
/// проход по фреймам, строит индекс смещений и выносит вердикт), затем доступ - вперёд, назад или
/// по индексу. Доступ без сканирования запрещён намеренно: идти назад или по индексу, не зная
/// смещений, нельзя, а "прочитать, что получилось, и не заметить хвост" - тот самый тихий пропуск
/// данных, который §9.4 запрещает.
/// </para>
/// <para>
/// Индекс смещений занимает 4 байта на фрейм и не содержит ни записей, ни термов: внутри сегмента
/// индексы непрерывны и начинаются с <see cref="SegmentHeader.BaseIndex"/> (§4), поэтому позиция
/// фрейма в индексе и есть его индекс. На минимальном фрейме (17 байт) это ~24% размера сегмента -
/// цена того, что чтение по индексу и итерация назад не требуют повторного сканирования.
/// </para>
/// <para>
/// Читатель не потокобезопасен и не предназначен для чтения сегмента, в который прямо сейчас
/// пишут: длину файла он фиксирует при открытии. Читаются запечатанные сегменты; последний сегмент
/// читает писатель через свой актор (<c>S1-T08</c>), см. §8 формата.
/// </para>
/// </remarks>
public sealed class SegmentReader : IAsyncDisposable
{
    private readonly IStorageReadFile _file;
    private readonly List<int> _frameOffsets = [];

    /// <summary>Буфер под поле <c>frameLength</c>: читатель однопоточный, а поле нужно на каждом фрейме.</summary>
    private readonly byte[] _frameLengthBuffer = new byte[sizeof(uint)];

    private bool _disposed;

    private SegmentReader(IStorageReadFile file, SegmentHeader header)
    {
        _file = file;
        Header = header;
    }

    /// <summary>Заголовок сегмента.</summary>
    public SegmentHeader Header { get; }

    /// <summary>Путь файла: попадает в диагностику и в отчёт о восстановлении.</summary>
    public string Path => _file.Path;

    /// <summary>
    /// Длина файла на момент открытия.
    /// </summary>
    /// <remarks>
    /// Снимок: сегмент, дописанный после открытия, читателю виден не будет, а "прочитан до конца"
    /// означает конец того файла, который читатель открыл.
    /// </remarks>
    public long Length => _file.Length;

    /// <summary>Результат последнего прохода; <c>null</c>, если <see cref="ScanAsync"/> не вызывался.</summary>
    public SegmentScanResult? ScanResult { get; private set; }

    /// <summary>Число валидных фреймов; требует <see cref="ScanAsync"/>.</summary>
    /// <exception cref="InvalidOperationException">Сканирование ещё не выполнялось.</exception>
    public int FrameCount
    {
        get
        {
            EnsureScanned();
            return _frameOffsets.Count;
        }
    }

    /// <summary>Индекс последней валидной записи; <c>null</c> для пустого сегмента.</summary>
    /// <exception cref="InvalidOperationException">Сканирование ещё не выполнялось.</exception>
    public LogIndex? LastIndex => Scan().LastIndex;

    /// <summary>Терм последней валидной записи; <c>null</c> для пустого сегмента.</summary>
    /// <exception cref="InvalidOperationException">Сканирование ещё не выполнялось.</exception>
    public Term? LastTerm => Scan().LastTerm;

    /// <summary>Открывает сегмент и разбирает заголовок.</summary>
    /// <param name="fileSystem">Файловая система: рантайм-реализация или тестовый дубль.</param>
    /// <param name="path">Путь сегмента; имя обязано соответствовать <c>baseIndex</c> заголовка (§7, §9.1).</param>
    /// <param name="cancellationToken">Отмена до открытия файла.</param>
    /// <returns>Читатель либо причина, по которой сегмент открыть нельзя.</returns>
    /// <exception cref="FileNotFoundException">Файла нет.</exception>
    public static async ValueTask<SegmentOpenResult> OpenAsync(
        IStorageFileSystem fileSystem,
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var file = await fileSystem.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
        var result = await ReadHeaderAsync(file, path, cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            await file.DisposeAsync().ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Разбирает фрейм по абсолютному смещению, ничего не запоминая.
    /// </summary>
    /// <param name="offset">Смещение начала фрейма от начала файла.</param>
    /// <param name="cancellationToken">Отмена до начала чтения.</param>
    /// <returns>
    /// Успех с записью и числом занятых байт, <see cref="LogReadStatus.Truncated"/>, если фрейм
    /// не помещается до конца файла, или <see cref="LogReadStatus.Corrupt"/>, если фрейм есть
    /// целиком, но нарушает формат.
    /// </returns>
    /// <remarks>
    /// Примитив для пробы продолжения (<c>S1-T05</c>, §9.5 шаг 5): она ищет валидный фрейм на
    /// смещении, которого нет в индексе, и потому не может опираться на сканирование.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Смещение отрицательно.</exception>
    public async ValueTask<LogFrameReadResult> ReadFrameAtAsync(long offset, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        var lengthBuffer = _frameLengthBuffer;
        var lengthBytes = await ReadExactlyAsync(_file, offset, lengthBuffer, cancellationToken).ConfigureAwait(false);

        if (lengthBytes < lengthBuffer.Length)
        {
            return LogFrameReadResult.Truncated(
                $"на смещении {offset} до конца файла {lengthBytes} байт из {lengthBuffer.Length}: поля frameLength нет.");
        }

        uint frameLength = BinaryPrimitives.ReadUInt32LittleEndian(lengthBuffer);

        if (frameLength < LogSegmentFormat.MinFrameLength || frameLength > LogSegmentFormat.MaxFrameLength)
        {
            return LogFrameReadResult.Corrupt(
                $"на смещении {offset} frameLength {frameLength} вне границ "
                + $"[{LogSegmentFormat.MinFrameLength}, {LogSegmentFormat.MaxFrameLength}].");
        }

        var totalSize = sizeof(uint) + (int)frameLength + LogSegmentFormat.FrameCrcSize;
        var buffer = ArrayPool<byte>.Shared.Rent(totalSize);

        try
        {
            var frameBytes = await ReadExactlyAsync(_file, offset, buffer.AsMemory(0, totalSize), cancellationToken)
                .ConfigureAwait(false);

            if (frameBytes < totalSize)
            {
                return LogFrameReadResult.Truncated(
                    $"на смещении {offset} фрейм занимает {totalSize} байт, до конца файла {frameBytes}.");
            }

            return LogFrameCodec.Read(buffer.AsSpan(0, totalSize));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Читает поле <c>frameLength</c> по смещению, не читая фрейм целиком.
    /// </summary>
    /// <param name="offset">Смещение начала фрейма.</param>
    /// <param name="cancellationToken">Отмена до начала чтения.</param>
    /// <returns>
    /// Значение <c>frameLength</c>, если 4 байта есть и значение в границах формата; иначе <c>null</c>.
    /// </returns>
    /// <remarks>
    /// Проба продолжения (§9.5 шаг 5) отличает порчу в середине от torn write так: зная длину
    /// повреждённого фрейма <c>L</c>, она ищет валидный фрейм на смещении <c>p + 4 + L + 4</c>.
    /// Если прочитать <c>L</c> нельзя или он вне границ, продолжения за повреждением быть не может.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Смещение отрицательно.</exception>
    public async ValueTask<int?> ReadFrameLengthAsync(long offset, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        var lengthBuffer = _frameLengthBuffer;
        var lengthBytes = await ReadExactlyAsync(_file, offset, lengthBuffer, cancellationToken).ConfigureAwait(false);

        if (lengthBytes < lengthBuffer.Length)
        {
            return null;
        }

        uint frameLength = BinaryPrimitives.ReadUInt32LittleEndian(lengthBuffer);

        return frameLength is >= LogSegmentFormat.MinFrameLength and <= LogSegmentFormat.MaxFrameLength
            ? (int)frameLength
            : null;
    }

    /// <summary>
    /// Проходит сегмент один раз: проверяет каждый фрейм, строит индекс смещений и фиксирует,
    /// до какого смещения файл валиден.
    /// </summary>
    /// <param name="cancellationToken">Отмена между фреймами.</param>
    /// <returns>Итог прохода: полный сегмент или остановка на хвосте.</returns>
    /// <remarks>
    /// Повторный вызов не читает файл заново: результат и индекс смещений кэшируются. Файл,
    /// изменившийся после сканирования, обнаружится при чтении фрейма, потому что читатель
    /// работает со снимком длины, сделанным при открытии.
    /// </remarks>
    public async ValueTask<SegmentScanResult> ScanAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (ScanResult is { } cached)
        {
            return cached;
        }

        _frameOffsets.Clear();

        var expectedIndex = Header.BaseIndex;
        Term? previousTerm = null;
        LogIndex? lastIndex = null;
        Term? lastTerm = null;
        long offset = LogSegmentFormat.HeaderSize;
        var result = SegmentScanResult.Complete(offset, frameCount: 0, lastIndex: null, lastTerm: null);

        while (offset < _file.Length)
        {
            var frame = await ReadFrameAtAsync(offset, cancellationToken).ConfigureAwait(false);
            var reason = frame.IsSuccess
                ? ContinuityProblem(frame.Record!, expectedIndex, previousTerm)
                : frame.Reason;

            if (reason is not null)
            {
                result = SegmentScanResult.TailProblem(reason, offset, _frameOffsets.Count, lastIndex, lastTerm);
                break;
            }

            var record = frame.Record!;

            // int, а не long: размер сегмента ограничен int.MaxValue (SegmentWriterOptions.Validate),
            // поэтому смещение внутри файла в 32 бита помещается с запасом.
            _frameOffsets.Add((int)offset);
            expectedIndex = expectedIndex.Next();
            previousTerm = record.Term;
            lastIndex = record.Index;
            lastTerm = record.Term;
            offset += frame.BytesConsumed;

            result = SegmentScanResult.Complete(offset, _frameOffsets.Count, lastIndex, lastTerm);
        }

        ScanResult = result;
        return result;
    }

    /// <summary>Итерирует валидные записи в порядке возрастания индекса.</summary>
    /// <param name="cancellationToken">Отмена между фреймами.</param>
    /// <returns>Записи сегмента; пустая последовательность для пустого сегмента.</returns>
    /// <remarks>
    /// Итерируется только валидный префикс: хвост сегмента в перечисление не попадает, а причина
    /// остановки - в <see cref="SegmentScanResult"/> после <see cref="ScanAsync"/>.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Сканирование ещё не выполнялось.</exception>
    public async IAsyncEnumerable<LogRecord> ReadForwardAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureScanned();

        for (var position = 0; position < _frameOffsets.Count; position++)
        {
            yield return await ReadFrameByPositionAsync(position, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Итерирует валидные записи в порядке убывания индекса.</summary>
    /// <param name="cancellationToken">Отмена между фреймами.</param>
    /// <returns>Записи сегмента от последней к первой; пустая последовательность для пустого сегмента.</returns>
    /// <exception cref="InvalidOperationException">Сканирование ещё не выполнялось.</exception>
    public async IAsyncEnumerable<LogRecord> ReadBackwardAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureScanned();

        for (var position = _frameOffsets.Count - 1; position >= 0; position--)
        {
            yield return await ReadFrameByPositionAsync(position, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Читает запись по абсолютному индексу.</summary>
    /// <param name="index">Индекс записи.</param>
    /// <param name="cancellationToken">Отмена до начала чтения.</param>
    /// <returns>Запись либо <c>null</c>, если такого индекса в сегменте нет.</returns>
    /// <exception cref="InvalidOperationException">Сканирование ещё не выполнялось.</exception>
    public async ValueTask<LogRecord?> ReadAtAsync(LogIndex index, CancellationToken cancellationToken)
    {
        EnsureScanned();

        var position = index.Value - Header.BaseIndex.Value;

        if (position < 0 || position >= _frameOffsets.Count)
        {
            return null;
        }

        return await ReadFrameByPositionAsync((int)position, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Смещение начала фрейма записи <paramref name="index"/> от начала файла.</summary>
    /// <param name="index">Индекс записи.</param>
    /// <returns>Смещение в байтах либо <c>null</c>, если такой записи в сегменте нет.</returns>
    /// <remarks>
    /// Добавлено в <c>S1-T06</c>: усечение конфликтующего хвоста (<c>TruncateSuffixAsync</c>)
    /// умеет только укорачивать файл, поэтому ему нужна не позиция записи в индексе, а граница
    /// в байтах. Смещение берётся из уже построенного индекса, файл не перечитывается.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Сканирование ещё не выполнялось.</exception>
    public long? GetFrameOffset(LogIndex index)
    {
        EnsureScanned();

        var position = index.Value - Header.BaseIndex.Value;

        return position >= 0 && position < _frameOffsets.Count ? _frameOffsets[(int)position] : null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _file.DisposeAsync().ConfigureAwait(false);
    }

    private static async ValueTask<SegmentOpenResult> ReadHeaderAsync(
        IStorageReadFile file,
        string path,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[LogSegmentFormat.HeaderSize];
        var bytesRead = await ReadExactlyAsync(file, 0, buffer, cancellationToken).ConfigureAwait(false);
        var header = SegmentHeaderCodec.Read(buffer.AsSpan(0, bytesRead));

        if (!header.IsSuccess)
        {
            return header.Status == LogReadStatus.Truncated
                ? SegmentOpenResult.Truncated(file.Length, header.Reason)
                : SegmentOpenResult.Corrupt(file.Length, header.Reason);
        }

        // §9.1 правило 3: baseIndex живёт в двух местах - в заголовке и в имени файла, и они
        // обязаны совпадать. Имя, из которого индекс не разбирается, тоже отказ: посторонний
        // файл в каталоге лога не должен получить индекс "на глазок".
        if (!LogSegmentFormat.TryParseFileName(path, out var fileNameIndex))
        {
            return SegmentOpenResult.Corrupt(
                file.Length,
                $"имя файла '{System.IO.Path.GetFileName(path)}' не имеет вида "
                + $"{{baseIndex:D{LogSegmentFormat.FileNameDigits}}}{LogSegmentFormat.FileNameSuffix} (§7).");
        }

        if (fileNameIndex != header.Header!.BaseIndex)
        {
            return SegmentOpenResult.Corrupt(
                file.Length,
                $"baseIndex заголовка {header.Header.BaseIndex} не совпадает с именем файла "
                + $"'{System.IO.Path.GetFileName(path)}' ({fileNameIndex}): §9.1 правило 3.");
        }

        return SegmentOpenResult.Success(new SegmentReader(file, header.Header));
    }

    /// <summary>Дочитывает буфер до конца или до конца файла.</summary>
    /// <remarks>
    /// Цикл обязателен: <see cref="IStorageReadFile.ReadAsync"/> вправе вернуть меньше
    /// запрошенного, и это не ошибка. Ноль означает конец файла и завершает дочитывание.
    /// </remarks>
    private static async ValueTask<int> ReadExactlyAsync(
        IStorageReadFile file,
        long offset,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var total = 0;

        while (total < destination.Length)
        {
            var bytesRead = await file.ReadAsync(offset + total, destination[total..], cancellationToken)
                .ConfigureAwait(false);

            if (bytesRead == 0)
            {
                break;
            }

            total += bytesRead;
        }

        return total;
    }

    /// <summary>Проверяет инварианты последовательности внутри сегмента (§4, §9.1 правило 4).</summary>
    /// <returns>Причина нарушения либо <c>null</c>, если запись продолжает последовательность.</returns>
    private static string? ContinuityProblem(LogRecord record, LogIndex expectedIndex, Term? previousTerm)
    {
        if (record.Index != expectedIndex)
        {
            return $"ожидался индекс {expectedIndex}, в файле {record.Index}: внутри сегмента индексы идут подряд (§4).";
        }

        if (previousTerm is { } term && record.Term < term)
        {
            return $"терм {record.Term} меньше терма предыдущей записи {term}: терм вдоль журнала не убывает (§4).";
        }

        return null;
    }

    /// <summary>Читает фрейм, про который сканирование уже знает, что он валиден.</summary>
    private async ValueTask<LogRecord> ReadFrameByPositionAsync(int position, CancellationToken cancellationToken)
    {
        var frame = await ReadFrameAtAsync(_frameOffsets[position], cancellationToken).ConfigureAwait(false);

        if (!frame.IsSuccess)
        {
            throw new InvalidDataException(
                $"Сегмент '{Path}': фрейм на смещении {_frameOffsets[position]} стал нечитаем после сканирования: {frame.Reason}");
        }

        return frame.Record!;
    }

    private SegmentScanResult Scan()
    {
        EnsureScanned();
        return ScanResult!.Value;
    }

    private void EnsureScanned()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (ScanResult is null)
        {
            throw new InvalidOperationException(
                "Доступ к записям требует сканирования: вызовите ScanAsync, чтобы построить индекс смещений.");
        }
    }
}
