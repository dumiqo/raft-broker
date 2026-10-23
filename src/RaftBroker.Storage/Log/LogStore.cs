using RaftBroker.Core.Primitives;
using RaftBroker.Core.Time;
using RaftBroker.Storage.IO;

namespace RaftBroker.Storage.Log;

/// <summary>
/// Журнал как непрерывная последовательность записей поверх сегментов: границы, rollover,
/// чтение через несколько файлов, усечение конфликтующего хвоста и хранение по снапшоту.
/// </summary>
/// <remarks>
/// <para>
/// Контракт - <see cref="ILogStore"/>, задачи - <c>S1-T06</c>. Класс держит в памяти только то,
/// что нельзя дёшево получить с диска: список <c>{baseIndex, path}</c> плюс три числа Raft
/// (<see cref="LastIndex"/>, <see cref="LastTerm"/>, <see cref="CommittedIndex"/>) и снапшот.
/// Писателя (<see cref="SegmentWriter"/>) держит открытым ровно один, читателей -
/// ни одного: файл открывается на время чтения и закрывается в <c>finally</c>.
/// </para>
/// <para>
/// Так сделано намеренно (ADR <c>0007-log-store-catalog.md</c>): открытый читатель живёт
/// на дескрипторе файла, а <c>TruncateSuffixAsync</c> и <c>CompactAsync</c> удаляют и укорачивают
/// файлы. Кэш открытых читателей пришлось бы инвалидировать при каждом удалении сегмента и
/// синхронизировать с писателем, который дописывает тот же файл, - это больше кода и больше
/// способов соврать, чем один <see cref="SegmentReader.OpenAsync"/> на чтение. Кэш нужен, только
/// если измерение покажет, что открытие файла дороже чтения (тогда - с ADR и тестом).
/// </para>
/// <para>
/// Потокобезопасности нет: экземпляром владеет одна задача (актор узла). Это то же правило, что
/// у <see cref="SegmentWriter"/>, и оно записано в контракте <see cref="ILogStore"/>.
/// </para>
/// </remarks>
public sealed class LogStore : ILogStore
{
    private readonly IStorageFileSystem _fileSystem;
    private readonly IClock _clock;
    private readonly SegmentWriterOptions _options;
    private readonly List<SegmentInfo> _segments = [];

    private SegmentWriter? _writer;
    private int _observedCreatedSegments;
    private LogIndex _snapshotIndex;
    private bool _disposed;

    private LogStore(IStorageFileSystem fileSystem, IClock clock, SegmentWriterOptions options)
    {
        _fileSystem = fileSystem;
        _clock = clock;
        _options = options;
    }

    /// <inheritdoc />
    public LogIndex FirstIndex
    {
        get
        {
            // Снапшот сжимает историю логически, даже если её сегмент ещё лежит на диске:
            // читать записи ниже снапшота нельзя (R4: снапшот плюс хвост лога).
            var afterSnapshot = _snapshotIndex.Next();

            return _segments.Count == 0
                ? afterSnapshot
                : (afterSnapshot > _segments[0].BaseIndex ? afterSnapshot : _segments[0].BaseIndex);
        }
    }

    /// <inheritdoc />
    public LogIndex LastIndex { get; private set; }

    /// <inheritdoc />
    public Term LastTerm { get; private set; }

    /// <inheritdoc />
    public LogIndex CommittedIndex { get; private set; }

    /// <summary>Открывает журнал в каталоге из настроек.</summary>
    /// <param name="fileSystem">Файловая система: рантайм-реализация или тестовый дубль.</param>
    /// <param name="clock">Источник времени для заголовка нового сегмента.</param>
    /// <param name="options">Каталог журнала и параметры сегментов.</param>
    /// <param name="cancellationToken">Отмена между сегментами.</param>
    /// <returns>Открытый журнал либо причина, по которой открывать нельзя.</returns>
    /// <remarks>
    /// <para>
    /// Каталог разбирает <see cref="SegmentCatalog"/>: он же проверяет непрерывность и находит
    /// последний сегмент. Здесь добавляется только то, чего каталог не знает, - писатель,
    /// продолженный на последнем сегменте (<see cref="SegmentWriter.ResumeAsync"/>).
    /// </para>
    /// <para>
    /// Открытый читатель здесь не задерживается ни на одном пути, включая отказ: каталог
    /// закрывает сегмент до возврата, поэтому проверка на утечку дескрипторов при
    /// <see cref="LogReadStatus.Truncated"/> и <see cref="LogReadStatus.Corrupt"/> сводится
    /// к одному месту.
    /// </para>
    /// </remarks>
    /// <exception cref="IOException">
    /// Файл не открылся или не создался: журнал найден, но работать с ним нельзя. Это не
    /// структурный отказ, а сбой ввода-вывода.
    /// </exception>
    public static async ValueTask<LogStoreOpenResult> OpenAsync(
        IStorageFileSystem fileSystem,
        IClock clock,
        SegmentWriterOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);

        options.Validate();

        var catalog = await SegmentCatalog.ReadAsync(fileSystem, options.Directory, cancellationToken).ConfigureAwait(false);

        if (!catalog.IsSuccess)
        {
            return catalog.Status == LogReadStatus.Truncated
                ? LogStoreOpenResult.Truncated(catalog.Path, catalog.Reason)
                : LogStoreOpenResult.Corrupt(catalog.Path, catalog.Reason);
        }

        var store = new LogStore(fileSystem, clock, options);
        var entries = catalog.Entries;

        foreach (var entry in entries)
        {
            store._segments.Add(new SegmentInfo(entry.BaseIndex, entry.Path));
        }

        if (entries.Count == 0)
        {
            return LogStoreOpenResult.Success(store);
        }

        var last = entries[^1];

        if (last.LastIndex is { } lastIndex)
        {
            store.LastIndex = lastIndex;
            store.LastTerm = last.LastTerm!.Value;
        }
        else if (entries.Count > 1)
        {
            // Последний сегмент пуст (только заголовок, §8): журнал кончается предыдущим сегментом,
            // и продолжаем мы именно его данные.
            store.LastIndex = entries[^2].LastIndex!.Value;
            store.LastTerm = entries[^2].LastTerm!.Value;
        }

        // Иначе журнал пуст: последний (и единственный) сегмент - пустой файл с базовым индексом
        // первого возможного индекса, и последняя запись ещё не написана.
        store._writer = await SegmentWriter.ResumeAsync(
            fileSystem,
            clock,
            options,
            new SegmentResumeState
            {
                BaseIndex = last.BaseIndex,
                LastIndex = store.LastIndex,
                LastTerm = store.LastTerm,
                Length = last.Length,
            },
            cancellationToken).ConfigureAwait(false);

        store._observedCreatedSegments = store._writer.CreatedSegmentBaseIndexes.Count;

        return LogStoreOpenResult.Success(store);
    }

    /// <inheritdoc />
    public ValueTask AppendAsync(IReadOnlyList<LogRecord> records, CancellationToken cancellationToken)
        => AppendAsync(records, _options.DefaultDurabilityMode, cancellationToken);

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// Индекс первой записи не продолжает журнал (дыра или повтор) либо убывает терм.
    /// </exception>
    public async ValueTask AppendAsync(
        IReadOnlyList<LogRecord> records,
        DurabilityMode mode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Неизвестный режим долговечности.");
        }

        if (records.Count == 0)
        {
            return;
        }

        ValidateAppendStart(records);

        // Писатель создаётся лениво: у открытого непустого журнала он уже есть (ResumeAsync),
        // а у пустого каталога первый сегмент появляется только с первой записью.
        _writer ??= new SegmentWriter(_fileSystem, _clock, _options);

        await _writer.AppendAsync(records, mode, cancellationToken).ConfigureAwait(false);

        TrackCreatedSegments();
        LastIndex = _writer.LastIndex ?? LastIndex;
        LastTerm = _writer.LastTerm ?? LastTerm;
    }

    /// <inheritdoc />
    public ValueTask FlushToDiskAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _writer is null ? ValueTask.CompletedTask : _writer.FlushToDiskAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <exception cref="InvalidDataException">
    /// Сегмент перестал читаться после успешного открытия журнала: файл изменили в обход хранилища.
    /// </exception>
    public async ValueTask<LogLookupResult> GetAsync(LogIndex index, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Порядок проверок важен: сначала «это уже снапшот», потом «этого ещё нет».
        // Для индекса ниже журнала оба ответа были бы верны, и выбор между ними решает,
        // получит follower снапшот или продолжение репликации.
        if (index.Value < FirstIndex.Value)
        {
            return LogLookupResult.Compacted();
        }

        if (index > LastIndex)
        {
            return LogLookupResult.NotYet();
        }

        var position = FindSegmentPosition(index);

        if (position < 0)
        {
            return LogLookupResult.NotYet();
        }

        var reader = await OpenScannedAsync(_segments[position].Path, cancellationToken).ConfigureAwait(false);

        try
        {
            var record = await reader.ReadAtAsync(index, cancellationToken).ConfigureAwait(false);

            return record is null ? LogLookupResult.NotYet() : LogLookupResult.Found(record);
        }
        finally
        {
            await reader.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxCount"/> не положителен или <paramref name="from"/> ниже
    /// <see cref="FirstIndex"/>: сжатые снапшотом записи не читаются.
    /// </exception>
    public async ValueTask<IReadOnlyList<LogRecord>> ReadRangeAsync(
        LogIndex from,
        int maxCount,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCount);

        if (from.Value < FirstIndex.Value)
        {
            throw new ArgumentOutOfRangeException(
                nameof(from),
                from.Value,
                $"Начало доступной истории - {FirstIndex}: записи ниже вошли в снапшот и не читаются.");
        }

        var position = FindSegmentPosition(from);

        if (position < 0)
        {
            return [];
        }

        var records = new List<LogRecord>(maxCount);

        for (; position < _segments.Count && records.Count < maxCount; position++)
        {
            var segment = _segments[position];
            var lower = from > segment.BaseIndex ? from : segment.BaseIndex;

            // Конец сегмента - это начало следующего минус один; у последнего сегмента конец
            // журнала. Пустой последний сегмент даёт верх меньше нижней границы: читать нечего.
            var upper = position + 1 < _segments.Count
                ? new LogIndex(_segments[position + 1].BaseIndex.Value - 1)
                : LastIndex;

            if (lower > upper)
            {
                continue;
            }

            var reader = await OpenScannedAsync(segment.Path, cancellationToken).ConfigureAwait(false);

            try
            {
                for (var index = lower; index <= upper && records.Count < maxCount; index = index.Next())
                {
                    var record = await reader.ReadAtAsync(index, cancellationToken).ConfigureAwait(false);

                    if (record is null)
                    {
                        // Индекс попал в диапазон сегмента, но записи в файле нет: так выглядит
                        // только пустой последний сегмент, и он ничего не добавляет к результату.
                        break;
                    }

                    records.Add(record);
                }
            }
            finally
            {
                await reader.DisposeAsync().ConfigureAwait(false);
            }
        }

        return records;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// <paramref name="from"/> не выше <see cref="CommittedIndex"/>: R4 запрещает удалять
    /// закоммиченную запись.
    /// </exception>
    public async ValueTask TruncateSuffixAsync(LogIndex from, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var immutable = CommittedIndex > _snapshotIndex ? CommittedIndex : _snapshotIndex;

        if (from <= immutable)
        {
            throw new InvalidOperationException(
                $"Обрезка с индекса {from} затрагивает неизменяемую историю: точка коммита {CommittedIndex}, снапшот {_snapshotIndex}. Закоммиченная запись не удаляется (R4).");
        }

        if (from > LastIndex)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Писатель обязан быть закрыт до удаления файлов и укорачивания сегмента: открытый
        // пишущий дескриптор на Windows не даёт усечь файл, а на Unix писал бы в удалённый inode.
        await CloseWriterAsync().ConfigureAwait(false);

        var position = FindSegmentPosition(from);
        var segment = _segments[position];

        // Дальше идут разрушающие действия подряд: отмена проверена выше, потому что
        // полуобрезанный журнал хуже, чем необрезанный.
        for (var later = _segments.Count - 1; later > position; later--)
        {
            await _fileSystem.DeleteAsync(_segments[later].Path, CancellationToken.None).ConfigureAwait(false);
            _segments.RemoveAt(later);
        }

        if (from == segment.BaseIndex)
        {
            // Обрезаем по границе сегмента: файл уходит целиком, журнал продолжается предыдущим.
            await _fileSystem.DeleteAsync(segment.Path, CancellationToken.None).ConfigureAwait(false);
            _segments.RemoveAt(position);

            if (_segments.Count == 0)
            {
                LastIndex = LogIndex.Empty;
                LastTerm = default;
            }
            else
            {
                var tail = await ReadTailStateAsync(CancellationToken.None).ConfigureAwait(false);
                LastIndex = tail.LastIndex;
                LastTerm = tail.LastTerm;
                _writer = await ResumeWriterAsync(tail, CancellationToken.None).ConfigureAwait(false);
            }
        }
        else
        {
            var reader = await OpenScannedAsync(segment.Path, CancellationToken.None).ConfigureAwait(false);

            try
            {
                var offset = reader.GetFrameOffset(from) ?? throw new InvalidOperationException(
                    $"Запись {from} не найдена в сегменте '{segment.Path}' при обрезке: индекс журнала разошёлся с содержимым файла.");

                // Последняя уцелевшая запись даёт новое состояние журнала: её индекс и терм
                // продолжит следующий append.
                var survivor = await reader.ReadAtAsync(new LogIndex(from.Value - 1), CancellationToken.None).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        $"Сегмент '{segment.Path}' не содержит записи {from.Value - 1}, хотя обрезка начинается с {from}.");

                LastIndex = survivor.Index;
                LastTerm = survivor.Term;

                await _fileSystem.TruncateAsync(segment.Path, offset, CancellationToken.None).ConfigureAwait(false);

                _writer = await ResumeWriterAsync(
                    new SegmentResumeState
                    {
                        BaseIndex = segment.BaseIndex,
                        LastIndex = survivor.Index,
                        LastTerm = survivor.Term,
                        Length = offset,
                    },
                    CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                await reader.DisposeAsync().ConfigureAwait(false);
            }
        }

        _observedCreatedSegments = _writer?.CreatedSegmentBaseIndexes.Count ?? 0;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// Точка коммита не ходит назад: R4 запрещает объявлять закоммиченное незакоммиченным.
    /// </exception>
    public ValueTask MarkCommittedAsync(LogIndex index, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (index < CommittedIndex)
        {
            throw new InvalidOperationException(
                $"Точка коммита не ходит назад: текущая {CommittedIndex}, получено {index}.");
        }

        if (index > LastIndex)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index),
                index.Value,
                $"Точка коммита {index} больше последней записи журнала {LastIndex}: коммитить нечего.");
        }

        CommittedIndex = index;

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="snapshotIndex"/> ниже текущего снапшота (снапшот не ходит назад) или выше
    /// <see cref="LastIndex"/> (снапшот не может содержать несуществующих записей).
    /// </exception>
    public async ValueTask CompactAsync(LogIndex snapshotIndex, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (snapshotIndex < _snapshotIndex)
        {
            throw new ArgumentOutOfRangeException(
                nameof(snapshotIndex),
                snapshotIndex.Value,
                $"Снапшот не ходит назад: текущий {_snapshotIndex}.");
        }

        if (snapshotIndex > LastIndex)
        {
            throw new ArgumentOutOfRangeException(
                nameof(snapshotIndex),
                snapshotIndex.Value,
                $"Снапшот на индекс {snapshotIndex} больше последней записи {LastIndex}.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Удаляется сегмент, только если он запечатан и его конец покрыт снапшотом. Последний
        // сегмент не удаляется никогда: он продолжает журнал (нужен для prevLogTerm).
        var removable = 0;

        while (removable + 1 < _segments.Count
            && new LogIndex(_segments[removable + 1].BaseIndex.Value - 1) <= snapshotIndex)
        {
            removable++;
        }

        for (var position = 0; position < removable; position++)
        {
            await _fileSystem.DeleteAsync(_segments[position].Path, CancellationToken.None).ConfigureAwait(false);
        }

        _segments.RemoveRange(0, removable);
        _snapshotIndex = snapshotIndex;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await CloseWriterAsync().ConfigureAwait(false);
    }

    /// <summary>Проверяет, что первая запись продолжает журнал.</summary>
    /// <remarks>
    /// Дыру и повтор в середине пачки проверяет <see cref="SegmentWriter"/>; здесь - стык между
    /// пачками и между сегментами, включая случай, когда журнал начинается со снапшота.
    /// </remarks>
    private void ValidateAppendStart(IReadOnlyList<LogRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records[0], nameof(records));

        var expected = LastIndex.Value > 0 ? LastIndex.Next() : FirstIndex;

        if (records[0].Index != expected)
        {
            throw new InvalidOperationException(
                $"Журнал продолжается с индекса {expected}, получена запись {records[0].Index}: дыры и повторы запрещены (R2).");
        }
    }

    /// <summary>Добавляет в список сегментов те, что создал писатель, начиная с ещё не учтённых.</summary>
    /// <remarks>
    /// <para>
    /// Источник истины - писатель, а не перечитывание каталога: rollover случается внутри append,
    /// и узнавать о нём отдельным обходом каталога на каждую пачку значило бы платить за это
    /// в горячем пути.
    /// </para>
    /// <para>
    /// Повторов в списке сегментов быть не может, и проверять это не нужно: усечение и удаление
    /// всегда заменяют писателя новым (или обнуляют его), а у нового список созданных сегментов
    /// пуст. По этой же причине базовые индексы в <see cref="_segments"/> только растут.
    /// </para>
    /// </remarks>
    private void TrackCreatedSegments()
    {
        var created = _writer!.CreatedSegmentBaseIndexes;

        for (var position = _observedCreatedSegments; position < created.Count; position++)
        {
            var baseIndex = created[position];
            _segments.Add(new SegmentInfo(baseIndex, Path.Combine(_options.Directory, LogSegmentFormat.GetFileName(baseIndex))));
        }

        _observedCreatedSegments = created.Count;
    }

    /// <summary>Позиция последнего сегмента с базовым индексом не выше указанного; -1, если таких нет.</summary>
    private int FindSegmentPosition(LogIndex index)
    {
        var low = 0;
        var high = _segments.Count - 1;
        var found = -1;

        while (low <= high)
        {
            var middle = low + ((high - low) / 2);

            if (_segments[middle].BaseIndex <= index)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return found;
    }

    /// <summary>Открывает сегмент и проверяет, что он всё ещё разбирается до конца файла.</summary>
    /// <remarks>
    /// Сканирование при каждом чтении кажется избыточным, но именно оно делает чтение безопасным
    /// без кэша: индекс фреймов строится по снимку длины файла, и порча на диске обнаруживается
    /// как <see cref="InvalidDataException"/>, а не как «записи нет». Открытие файла и один
    /// последовательный проход - цена одного чтения; кэш индекса появится только по измерению
    /// (ADR <c>0007-log-store-catalog.md</c>).
    /// </remarks>
    private async ValueTask<SegmentReader> OpenScannedAsync(string path, CancellationToken cancellationToken)
    {
        var open = await SegmentReader.OpenAsync(_fileSystem, path, cancellationToken).ConfigureAwait(false);

        if (!open.IsSuccess)
        {
            throw new InvalidDataException(
                $"Сегмент '{path}' перестал открываться после успешного открытия журнала: {open.Reason}");
        }

        var reader = open.Reader!;

        try
        {
            var scan = await reader.ScanAsync(cancellationToken).ConfigureAwait(false);

            if (!scan.IsComplete)
            {
                throw new InvalidDataException(
                    $"Сегмент '{path}' перестал разбираться до конца файла: {scan.Reason}");
            }

            return reader;
        }
        catch
        {
            await reader.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Состояние последнего оставшегося сегмента: чем продолжается журнал.</summary>
    private async ValueTask<SegmentResumeState> ReadTailStateAsync(CancellationToken cancellationToken)
    {
        var last = _segments[^1];
        var reader = await OpenScannedAsync(last.Path, cancellationToken).ConfigureAwait(false);

        try
        {
            // Последний сегмент заведомо не пуст: пустой мог быть только последним, а его
            // обрезка удалила.
            var lastIndex = reader.LastIndex ?? throw new InvalidOperationException(
                $"Сегмент '{last.Path}' оказался пустым, хотя продолжает журнал.");

            return new SegmentResumeState
            {
                BaseIndex = last.BaseIndex,
                LastIndex = lastIndex,
                LastTerm = reader.LastTerm!.Value,
                Length = reader.Length,
            };
        }
        finally
        {
            await reader.DisposeAsync().ConfigureAwait(false);
        }
    }

    private ValueTask<SegmentWriter> ResumeWriterAsync(SegmentResumeState state, CancellationToken cancellationToken)
        => SegmentWriter.ResumeAsync(_fileSystem, _clock, _options, state, cancellationToken);

    private async ValueTask CloseWriterAsync()
    {
        if (_writer is null)
        {
            return;
        }

        var writer = _writer;
        _writer = null;
        await writer.DisposeAsync().ConfigureAwait(false);
    }

    private readonly record struct SegmentInfo(LogIndex BaseIndex, string Path);
}
