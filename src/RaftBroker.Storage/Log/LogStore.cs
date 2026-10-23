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
/// Мутации сериализованы внутри: <see cref="AppendAsync"/>, <see cref="FlushToDiskAsync"/>,
/// <see cref="TruncateSuffixAsync"/>, <see cref="MarkCommittedAsync"/>, <see cref="CompactAsync"/>
/// и <see cref="DisposeAsync"/> проходят через одни воротца (<c>SemaphoreSlim</c>), потому что
/// <see cref="SegmentWriter"/> однопоточный по построению (ADR <c>0011-log-store-concurrency.md</c>).
/// Чтения воротца не берут и видят опубликованный снимок каталога: медленный читатель не может
/// задержать запись, а писатель не может показать читателю каталог, который ещё меняется.
/// </para>
/// </remarks>
public sealed class LogStore : ILogStore
{
    private readonly IStorageFileSystem _fileSystem;
    private readonly IClock _clock;
    private readonly SegmentWriterOptions _options;
    private readonly List<SegmentInfo> _segments = [];

    /// <summary>Воротца мутаций: писатель один, поэтому очередь из них не нужна.</summary>
    private readonly SemaphoreSlim _writeGate = new(initialCount: 1, maxCount: 1);

    private volatile LogView _view = LogView.Empty;

    private SegmentWriter? _writer;
    private int _observedCreatedSegments;
    private LogIndex _snapshotIndex;
    private LogIndex _lastIndex;
    private Term _lastTerm;
    private LogIndex _committedIndex;
    private bool _disposed;

    private LogStore(IStorageFileSystem fileSystem, IClock clock, SegmentWriterOptions options)
    {
        _fileSystem = fileSystem;
        _clock = clock;
        _options = options;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Читается из опубликованного снимка: четыре поля журнала согласованы между собой, и брать
    /// их по одному из живых полей значило бы отдавать наружу смесь двух состояний.
    /// </remarks>
    public LogIndex FirstIndex => _view.FirstIndex;

    /// <inheritdoc />
    public LogIndex LastIndex => _view.LastIndex;

    /// <inheritdoc />
    public Term LastTerm => _view.LastTerm;

    /// <inheritdoc />
    public LogIndex CommittedIndex => _view.CommittedIndex;

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
    /// продолженный на последнем сегменте (<see cref="SegmentWriter.ResumeAsync"/>), и ремонт
    /// оборванного последнего сегмента (<c>S1-T07</c>).
    /// </para>
    /// <para>
    /// Ремонт - ровно один проход: <see cref="SegmentRecovery.RecoverAsync"/> получает последний
    /// сегмент, после чего каталог читается заново. Перечитывание, а не переиспользование
    /// частичного списка, потому что <see cref="SegmentCatalogResult.Truncated"/> его не несёт,
    /// а удалённый файл меняет каталог целиком. Второй обрыв на перечитанном каталоге - уже
    /// отказ: ремонт не помог, и повторять его значило бы терять байты вслепую (§9.4).
    /// </para>
    /// <para>
    /// Открытый читатель здесь не задерживается ни на одном пути, включая отказ: и каталог,
    /// и восстановление закрывают сегмент до возврата, поэтому проверка на утечку дескрипторов
    /// при <see cref="LogReadStatus.Truncated"/> и <see cref="LogReadStatus.Corrupt"/> сводится
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

        var repairs = new List<SegmentRecoveryResult>(1);
        var catalog = await SegmentCatalog.ReadAsync(fileSystem, options.Directory, cancellationToken).ConfigureAwait(false);

        if (catalog.Status == LogReadStatus.Truncated)
        {
            var recovery = await SegmentRecovery.RecoverAsync(
                fileSystem,
                catalog.Path,
                SegmentKind.Last,
                cancellationToken).ConfigureAwait(false);

            if (recovery.IsRefused)
            {
                return LogStoreOpenResult.Corrupt(recovery.Path, recovery.Reason);
            }

            repairs.Add(recovery);
            catalog = await SegmentCatalog.ReadAsync(fileSystem, options.Directory, cancellationToken).ConfigureAwait(false);
        }

        if (!catalog.IsSuccess)
        {
            // Сюда же попадает повторный обрыв после ремонта: второй проход значил бы терять
            // байты вслепую, поэтому повторного восстановления нет (§9.4).
            return LogStoreOpenResult.Corrupt(catalog.Path, catalog.Reason);
        }

        var entries = catalog.Entries;

        if (TermBreaksMonotonicity(entries) is { } brokenPath)
        {
            return LogStoreOpenResult.Corrupt(
                brokenPath,
                "терм убывает на границе сегментов: терм вдоль журнала не убывает (§4), "
                    + "и внутри одного файла это проверяет чтение, а стык файлов проверяет только старт.");
        }

        var store = new LogStore(fileSystem, clock, options);

        foreach (var entry in entries)
        {
            store._segments.Add(new SegmentInfo(entry.BaseIndex, entry.Path));
        }

        if (entries.Count == 0)
        {
            store.PublishView();

            return LogStoreOpenResult.Success(store, LogRecoveryReport.For(repairs, store));
        }

        var last = entries[^1];

        if (last.LastIndex is { } lastIndex)
        {
            store._lastIndex = lastIndex;
            store._lastTerm = last.LastTerm!.Value;
        }
        else if (entries.Count > 1)
        {
            // Последний сегмент пуст (только заголовок, §8): журнал кончается предыдущим сегментом,
            // и продолжаем мы именно его данные.
            store._lastIndex = entries[^2].LastIndex!.Value;
            store._lastTerm = entries[^2].LastTerm!.Value;
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
                LastIndex = store._lastIndex,
                LastTerm = store._lastTerm,
                Length = last.Length,
            },
            cancellationToken).ConfigureAwait(false);

        store._observedCreatedSegments = store._writer.CreatedSegmentBaseIndexes.Count;
        store.PublishView();

        return LogStoreOpenResult.Success(store, LogRecoveryReport.For(repairs, store));
    }

    /// <summary>Ищет границу сегментов, где терм убывает; <c>null</c>, если терм не убывает нигде.</summary>
    /// <remarks>
    /// Сравниваются последние термы соседних сегментов. Это необходимое, но не достаточное
    /// условие: до первой записи следующего сегмента его терм не виден, поэтому терм внутри
    /// целого сегмента проверяет чтение (§4), а стык - только здесь. Пустой последний сегмент
    /// терма не имеет и ничего к сравнению не добавляет (§8).
    /// </remarks>
    private static string? TermBreaksMonotonicity(IReadOnlyList<SegmentCatalogEntry> entries)
    {
        for (var position = 1; position < entries.Count; position++)
        {
            if (entries[position - 1].LastTerm is not { } previous
                || entries[position].LastTerm is not { } current)
            {
                continue;
            }

            if (current < previous)
            {
                return entries[position].Path;
            }
        }

        return null;
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

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ValidateAppendStart(records);

            // Писатель создаётся лениво: у открытого непустого журнала он уже есть (ResumeAsync),
            // а у пустого каталога первый сегмент появляется только с первой записью.
            _writer ??= new SegmentWriter(_fileSystem, _clock, _options);

            await _writer.AppendAsync(records, mode, cancellationToken).ConfigureAwait(false);

            TrackCreatedSegments();
            _lastIndex = _writer.LastIndex ?? _lastIndex;
            _lastTerm = _writer.LastTerm ?? _lastTerm;
            PublishView();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Тоже под воротцами: <see cref="SegmentWriter"/> не потокобезопасен, а flush пишет в тот же
    /// дескриптор, что и append. Снимок каталога flush не меняет - он не двигает ни индексов, ни термов.
    /// </remarks>
    public async ValueTask FlushToDiskAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_writer is not null)
            {
                await _writer.FlushToDiskAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _writeGate.Release();
        }
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
        // Снимок берётся один раз: границы, сегменты и позиция обязаны быть из одного состояния,
        // иначе поиск сегмента может попасть в список, который уже укорочен обрезкой.
        var view = _view;

        if (index.Value < view.FirstIndex.Value)
        {
            return LogLookupResult.Compacted();
        }

        if (index > view.LastIndex)
        {
            return LogLookupResult.NotYet();
        }

        var position = FindSegmentPosition(view.Segments, index);

        if (position < 0)
        {
            return LogLookupResult.NotYet();
        }

        var reader = await OpenScannedAsync(view.Segments[position].Path, cancellationToken).ConfigureAwait(false);

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

        var view = _view;

        if (from.Value < view.FirstIndex.Value)
        {
            throw new ArgumentOutOfRangeException(
                nameof(from),
                from.Value,
                $"Начало доступной истории - {view.FirstIndex}: записи ниже вошли в снапшот и не читаются.");
        }

        var position = FindSegmentPosition(view.Segments, from);

        if (position < 0)
        {
            return [];
        }

        var records = new List<LogRecord>(maxCount);

        for (; position < view.Segments.Length && records.Count < maxCount; position++)
        {
            var segment = view.Segments[position];
            var lower = from > segment.BaseIndex ? from : segment.BaseIndex;

            // Конец сегмента - это начало следующего минус один; у последнего сегмента конец
            // журнала. Пустой последний сегмент даёт верх меньше нижней границы: читать нечего.
            var upper = position + 1 < view.Segments.Length
                ? new LogIndex(view.Segments[position + 1].BaseIndex.Value - 1)
                : view.LastIndex;

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

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await TruncateSuffixCoreAsync(from).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Обрезка хвоста; вызывается только под воротцами мутаций.</summary>
    private async ValueTask TruncateSuffixCoreAsync(LogIndex from)
    {
        var immutable = _committedIndex > _snapshotIndex ? _committedIndex : _snapshotIndex;

        if (from <= immutable)
        {
            throw new InvalidOperationException(
                $"Обрезка с индекса {from} затрагивает неизменяемую историю: точка коммита {_committedIndex}, снапшот {_snapshotIndex}. Закоммиченная запись не удаляется (R4).");
        }

        if (from > _lastIndex)
        {
            return;
        }

        // Писатель обязан быть закрыт до удаления файлов и укорачивания сегмента: открытый
        // пишущий дескриптор на Windows не даёт усечь файл, а на Unix писал бы в удалённый inode.
        await CloseWriterAsync().ConfigureAwait(false);

        var position = FindSegmentPosition(_segments, from);
        var segment = _segments[position];

        // Дальше идут разрушающие действия подряд: отмена проверена воротцами, потому что
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
                _lastIndex = LogIndex.Empty;
                _lastTerm = default;
            }
            else
            {
                var tail = await ReadTailStateAsync(CancellationToken.None).ConfigureAwait(false);
                _lastIndex = tail.LastIndex;
                _lastTerm = tail.LastTerm;
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

                _lastIndex = survivor.Index;
                _lastTerm = survivor.Term;

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
        PublishView();
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// Точка коммита не ходит назад: R4 запрещает объявлять закоммиченное незакоммиченным.
    /// </exception>
    public async ValueTask MarkCommittedAsync(LogIndex index, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (index < _committedIndex)
            {
                throw new InvalidOperationException(
                    $"Точка коммита не ходит назад: текущая {_committedIndex}, получено {index}.");
            }

            if (index > _lastIndex)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index),
                    index.Value,
                    $"Точка коммита {index} больше последней записи журнала {_lastIndex}: коммитить нечего.");
            }

            _committedIndex = index;
            PublishView();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="snapshotIndex"/> ниже текущего снапшота (снапшот не ходит назад) или выше
    /// <see cref="LastIndex"/> (снапшот не может содержать несуществующих записей).
    /// </exception>
    public async ValueTask CompactAsync(LogIndex snapshotIndex, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (snapshotIndex < _snapshotIndex)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(snapshotIndex),
                    snapshotIndex.Value,
                    $"Снапшот не ходит назад: текущий {_snapshotIndex}.");
            }

            if (snapshotIndex > _lastIndex)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(snapshotIndex),
                    snapshotIndex.Value,
                    $"Снапшот на индекс {snapshotIndex} больше последней записи {_lastIndex}.");
            }

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
            PublishView();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Воротца и здесь: закрывать писатель, пока append пишет в него, нельзя.
        await _writeGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);

        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await CloseWriterAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Проверяет, что первая запись продолжает журнал.</summary>
    /// <remarks>
    /// Дыру и повтор в середине пачки проверяет <see cref="SegmentWriter"/>; здесь - стык между
    /// пачками и между сегментами, включая случай, когда журнал начинается со снапшота.
    /// </remarks>
    private void ValidateAppendStart(IReadOnlyList<LogRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records[0], nameof(records));

        var expected = _lastIndex.Value > 0 ? _lastIndex.Next() : ComputeFirstIndex(_segments);

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
    /// <remarks>
    /// Список сегментов передаётся снаружи: писатель ищет по живому списку, читатель - по
    /// опубликованному снимку, и снимок не должен меняться под ногами.
    /// </remarks>
    private static int FindSegmentPosition(IReadOnlyList<SegmentInfo> segments, LogIndex index)
    {
        var low = 0;
        var high = segments.Count - 1;
        var found = -1;

        while (low <= high)
        {
            var middle = low + ((high - low) / 2);

            if (segments[middle].BaseIndex <= index)
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

    /// <summary>Пересобирает снимок каталога и публикует его читателям; вызывается под воротцами.</summary>
    /// <remarks>
    /// Массив копируется, а не публикуется живой список: читатель держит снимок на время чтения
    /// файлов, а обрезка и удаление меняют список. Копия из одного-двух элементов дешевле любой
    /// блокировки и не даёт читателю увидеть половину изменения.
    /// </remarks>
    private void PublishView()
    {
        var segments = _segments.ToArray();

        _view = new LogView(segments, ComputeFirstIndex(segments), _lastIndex, _lastTerm, _committedIndex);
    }

    /// <summary>Первый читаемый индекс журнала с учётом снапшота и первого сегмента.</summary>
    private LogIndex ComputeFirstIndex(IReadOnlyList<SegmentInfo> segments)
    {
        // Снапшот сжимает историю логически, даже если её сегмент ещё лежит на диске:
        // читать записи ниже снапшота нельзя (R4: снапшот плюс хвост лога).
        var afterSnapshot = _snapshotIndex.Next();

        return segments.Count == 0
            ? afterSnapshot
            : (afterSnapshot > segments[0].BaseIndex ? afterSnapshot : segments[0].BaseIndex);
    }

    /// <summary>Согласованный снимок состояния журнала для читателей.</summary>
    private sealed record LogView(
        SegmentInfo[] Segments,
        LogIndex FirstIndex,
        LogIndex LastIndex,
        Term LastTerm,
        LogIndex CommittedIndex)
    {
        /// <summary>Пустой журнал: до открытия читать нечего.</summary>
        public static LogView Empty { get; } = new([], LogIndex.Empty.Next(), LogIndex.Empty, default, LogIndex.Empty);
    }

    private readonly record struct SegmentInfo(LogIndex BaseIndex, string Path);
}
