using System.Buffers;
using RaftBroker.Core.Primitives;
using RaftBroker.Core.Time;
using RaftBroker.Storage.IO;

namespace RaftBroker.Storage.Log;

/// <summary>
/// Писатель сегментов журнала: кодек формата версии 1 плюс физическая запись на диск.
/// </summary>
/// <remarks>
/// <para>
/// Ответственность - три вещи: собрать фрейм, решить о rollover по размеру и выполнить fsync
/// по выбранной политике (<see cref="DurabilityMode"/>). Логика Raft, индекс сегментов и
/// порядок конкурентных записей сюда не входят: писателя ровно один (writer-actor этапа S1-T08),
/// поэтому класс не потокобезопасен и не пытается им быть. Читатели не блокируются: они читают
/// запечатанные сегменты, пока писатель пишет последний (§8).
/// </para>
/// <para>
/// Обязательный порядок rollover - из <c>docs/formats/log-segment.md</c>, §8: fsync текущего
/// сегмента, затем создание и fsync заголовка нового, затем запись фрейма. Без первого шага
/// запечатанный сегмент мог бы остаться с неотфлашенным хвостом, и восстановление не смогло бы
/// отличить это от порчи (§9.4).
/// </para>
/// <para>
/// Заголовок нового сегмента fsync-ается до первого фрейма (§9.3): сегмент, у которого заголовок
/// нечитаем, заведомо не содержит подтверждённых записей. Первый сегмент создаётся лениво при
/// первом append - индекс его первой записи берётся из самой записи.
/// </para>
/// <para>
/// Существующий сегмент можно не создавать, а продолжить (<see cref="ResumeAsync"/>, <c>S1-T06</c>):
/// так журнал ведёт себя после усечения конфликтующего хвоста и после перезапуска. Файл открывается
/// сразу, при создании писателя, - тогда все дальнейшие решения о rollover одинаковы для нового
/// и унаследованного сегмента, а отсутствие файла видно вызывающему сразу, а не на первом append.
/// </para>
/// </remarks>
public sealed class SegmentWriter : IAsyncDisposable
{
    private readonly IStorageFileSystem _fileSystem;
    private readonly IClock _clock;
    private readonly SegmentWriterOptions _options;

    private readonly List<LogIndex> _createdSegments = [];

    private IStorageFile? _file;
    private string? _broken;
    private bool _disposed;

    /// <summary>Создаёт писатель сегментов.</summary>
    /// <param name="fileSystem">Файловая система: рантайм-реализация или тестовый дубль.</param>
    /// <param name="clock">Источник настенного времени для информационного поля <c>createdAtUnixMs</c> заголовка.</param>
    /// <param name="options">Настройки: каталог, максимум сегмента, режим по умолчанию.</param>
    /// <exception cref="ArgumentException">Настройки некорректны.</exception>
    public SegmentWriter(IStorageFileSystem fileSystem, IClock clock, SegmentWriterOptions options)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);

        options.Validate();

        _fileSystem = fileSystem;
        _clock = clock;
        _options = options;
    }

    private SegmentWriter(
        IStorageFileSystem fileSystem,
        IClock clock,
        SegmentWriterOptions options,
        IStorageFile file,
        SegmentResumeState resume)
        : this(fileSystem, clock, options)
    {
        _file = file;
        LastIndex = resume.LastIndex.Value == 0 ? null : resume.LastIndex;
        LastTerm = resume.LastIndex.Value == 0 ? null : resume.LastTerm;
        CurrentSegmentSize = resume.Length;
        CurrentSegmentBaseIndex = resume.BaseIndex;
    }

    /// <summary>
    /// Создаёт писатель, который продолжит существующий последний сегмент дозаписью.
    /// </summary>
    /// <param name="fileSystem">Файловая система: рантайм-реализация или тестовый дубль.</param>
    /// <param name="clock">Источник настенного времени для заголовка будущих сегментов.</param>
    /// <param name="options">Настройки: каталог, максимум сегмента, режим по умолчанию.</param>
    /// <param name="resume">Чем кончается последний сегмент и сколько в нём байт.</param>
    /// <param name="cancellationToken">Отмена до открытия файла.</param>
    /// <returns>Писатель, готовый дописать следующую запись в унаследованный сегмент.</returns>
    /// <remarks>
    /// Файл открывается сразу: унаследованный сегмент обязан существовать, а решение о rollover
    /// должно быть тем же, что и для сегмента, только что созданного этим же писателем (§8).
    /// </remarks>
    /// <exception cref="ArgumentException">Состояние сегмента противоречиво или настройки некорректны.</exception>
    /// <exception cref="FileNotFoundException">Файла последнего сегмента нет.</exception>
    public static async ValueTask<SegmentWriter> ResumeAsync(
        IStorageFileSystem fileSystem,
        IClock clock,
        SegmentWriterOptions options,
        SegmentResumeState resume,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);

        ValidateResumeState(resume);

        var path = Path.Combine(options.Directory, LogSegmentFormat.GetFileName(resume.BaseIndex));
        var file = await fileSystem.OpenAppendAsync(path, cancellationToken).ConfigureAwait(false);

        return new SegmentWriter(fileSystem, clock, options, file, resume);
    }

    /// <summary>Индекс последней записанной записи; <c>null</c>, если журнал ещё пуст.</summary>
    /// <remarks>
    /// Для продолжаемого сегмента (<see cref="ResumeAsync"/>) это последняя запись журнала
    /// до этой дописки; у пустого унаследованного сегмента - последняя запись предыдущего,
    /// потому что именно от неё проверяется непрерывность (§4).
    /// </remarks>
    public LogIndex? LastIndex { get; private set; }

    /// <summary>Терм последней записанной записи; <c>null</c>, если журнал ещё пуст.</summary>
    public Term? LastTerm { get; private set; }

    /// <summary>Размер текущего сегмента в байтах, включая заголовок; 0 до создания сегмента.</summary>
    public long CurrentSegmentSize { get; private set; }

    /// <summary>Путь текущего сегмента; <c>null</c>, если сегмент ещё не создан.</summary>
    public string? CurrentSegmentPath => _file?.Path;

    /// <summary>
    /// Базовый индекс текущего сегмента; <c>null</c>, если сегмент ещё не создан.
    /// </summary>
    /// <remarks>
    /// Появляется в <c>S1-T06</c>: хранилище узнаёт о rollover именно здесь - базовый индекс
    /// сменился, значит предыдущий сегмент запечатан. Читать его из имени файла хранилищу не
    /// приходится, а предыдущий сегмент кончается на <c>новый базовый - 1</c> по непрерывности
    /// индексов (§4), поэтому сканировать запечатанный сегмент после rollover тоже не нужно.
    /// </remarks>
    public LogIndex? CurrentSegmentBaseIndex { get; private set; }

    /// <summary>
    /// Базовые индексы сегментов, созданных этим писателем, в порядке создания.
    /// </summary>
    /// <remarks>
    /// Появляется в <c>S1-T06</c>: хранилище узнаёт о rollover отсюда, а не из каталога
    /// (перечитывать каталог на каждый append нельзя). Сегмент, созданный унаследованным
    /// (<see cref="ResumeAsync"/>), здесь не появляется: его уже знает тот, кто вызвал
    /// продолжение. Список только растёт - один <see cref="LogIndex"/> на сегмент, то есть
    /// байты по сравнению с 64 МиБ самого сегмента; удалённые усечением файлы из списка
    /// не убираются, потому что хранилище само знает, какие сегменты у него остались.
    /// </remarks>
    public IReadOnlyList<LogIndex> CreatedSegmentBaseIndexes => _createdSegments;

    /// <summary>Дописывает записи в журнал, применяя режим долговечности по умолчанию.</summary>
    /// <param name="records">Записи в порядке возрастания индекса без дыр.</param>
    /// <param name="cancellationToken">Отмена между записями и до fsync.</param>
    public ValueTask AppendAsync(IReadOnlyList<LogRecord> records, CancellationToken cancellationToken)
        => AppendAsync(records, _options.DefaultDurabilityMode, cancellationToken);

    /// <summary>Дописывает записи в журнал с указанным режимом долговечности.</summary>
    /// <param name="records">Записи в порядке возрастания индекса без дыр.</param>
    /// <param name="mode">Политика fsync.</param>
    /// <param name="cancellationToken">Отмена между записями и до fsync.</param>
    /// <exception cref="InvalidOperationException">
    /// Нарушен порядок индексов или убывает терм, либо фрейм больше максимального размера сегмента.
    /// </exception>
    public async ValueTask AppendAsync(IReadOnlyList<LogRecord> records, DurabilityMode mode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureUsable();

        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Неизвестный режим долговечности.");
        }

        if (records.Count == 0)
        {
            return;
        }

        // Порядок проверяется до первого байта на диске: неверная последовательность не должна
        // оставить часть записей записанными, а часть - нет.
        ValidateSequence(records);

        foreach (var record in records)
        {
            await WriteFrameAsync(record, cancellationToken).ConfigureAwait(false);

            // PerEntry - fsync на каждую запись; GroupCommit и Periodic - один fsync на вызов
            // (у Periodic его нет вовсе: на устройство данные отправит FlushToDiskAsync).
            if (mode == DurabilityMode.PerEntry)
            {
                await FlushToDiskAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        if (mode == DurabilityMode.GroupCommit)
        {
            await FlushToDiskAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Отправляет на устройство всё, что записано в текущий сегмент (fsync).
    /// </summary>
    /// <remarks>
    /// Единственный способ сделать записи режима <see cref="DurabilityMode.Periodic"/>
    /// долговечными. Для остальных режимов вызов безвреден и нужен на остановке узла.
    /// </remarks>
    /// <param name="cancellationToken">Отмена до вызова fsync.</param>
    public async ValueTask FlushToDiskAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureUsable();

        if (_file is null)
        {
            return;
        }

        await _file.FlushToDiskAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Закрывает текущий сегмент, не выполняя fsync: долговечность - явный вызов <see cref="FlushToDiskAsync"/>.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_file is not null)
        {
            var file = _file;
            _file = null;
            await file.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Проверяет состояние унаследованного сегмента до открытия файла.</summary>
    /// <remarks>
    /// Противоречивое состояние - ошибка вызывающего: писатель не обязан угадывать, чем кончается
    /// сегмент. Проверки ровно те, которые видны из самих чисел: базовый индекс существует, длина
    /// не меньше заголовка, последний индекс либо принадлежит сегменту, либо на единицу меньше
    /// базового (пустой сегмент, §8), и у сегмента с записями есть хотя бы один фрейм.
    /// </remarks>
    private static void ValidateResumeState(SegmentResumeState resume)
    {
        if (resume.BaseIndex.Value < 1)
        {
            throw new ArgumentException(
                $"Базовый индекс сегмента равен {resume.BaseIndex}: записи нумеруются с единицы (§7).",
                nameof(resume));
        }

        if (resume.Length < LogSegmentFormat.HeaderSize)
        {
            throw new ArgumentException(
                $"Длина сегмента {resume.Length} байт меньше заголовка {LogSegmentFormat.HeaderSize}: "
                + "продолжать нечего, такой файл отбрасывает восстановление (§9.3).",
                nameof(resume));
        }

        if (resume.LastIndex.Value < resume.BaseIndex.Value - 1)
        {
            throw new ArgumentException(
                $"Последний индекс {resume.LastIndex} меньше {resume.BaseIndex} минус один: сегмент с таким "
                + "базовым индексом не мог бы продолжать журнал (непрерывность индексов, §4).",
                nameof(resume));
        }

        if (resume.LastIndex >= resume.BaseIndex && resume.Length == LogSegmentFormat.HeaderSize)
        {
            throw new ArgumentException(
                $"В сегменте (базовый индекс {resume.BaseIndex}) заявлена запись {resume.LastIndex}, "
                + "но файл состоит из одного заголовка: у сегмента с записями есть хотя бы один фрейм (§8).",
                nameof(resume));
        }
    }

    /// <summary>Проверяет, что записи продолжают журнал: индекс растёт на 1, терм не убывает.</summary>
    private void ValidateSequence(IReadOnlyList<LogRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records[0], nameof(records));

        // У унаследованного пустого сегмента (<see cref="ResumeAsync"/>) LastIndex ещё null,
        // но журнал не пуст: первая запись обязана попасть в базу сегмента, иначе файл
        // с заголовком baseIndex начнётся с другого индекса и перестанет читаться (§4).
        var expected = LastIndex?.Next() ?? CurrentSegmentBaseIndex ?? records[0].Index;
        var previousTerm = LastTerm;

        foreach (var record in records)
        {
            ArgumentNullException.ThrowIfNull(record);

            if (record.Index != expected)
            {
                throw new InvalidOperationException(
                    $"Нарушен порядок записей: ожидался индекс {expected}, получен {record.Index}. Дыры, повторы и перестановки внутри журнала запрещены форматом (docs/formats/log-segment.md, §4).");
            }

            if (previousTerm is not null && record.Term < previousTerm.Value)
            {
                throw new InvalidOperationException(
                    $"Терм записи {record.Index} равен {record.Term}, а терм предыдущей записи {previousTerm}. Терм вдоль журнала не убывает (§4).");
            }

            expected = expected.Next();
            previousTerm = record.Term;
        }
    }

    /// <summary>Пишет один фрейм, при необходимости создавая сегмент или выполняя rollover.</summary>
    private async ValueTask WriteFrameAsync(LogRecord record, CancellationToken cancellationToken)
    {
        var frameSize = LogFrameCodec.GetFrameSize(record.Payload.Length);

        if (frameSize > _options.MaxSegmentSize)
        {
            throw new InvalidOperationException(
                $"Фрейм записи {record.Index} занимает {frameSize} байт и больше максимального размера сегмента {_options.MaxSegmentSize}: фрейм между сегментами не разрывается (§8).");
        }

        if (_file is null)
        {
            await CreateSegmentAsync(record.Index, cancellationToken).ConfigureAwait(false);
        }
        else if (CurrentSegmentSize + frameSize > _options.MaxSegmentSize)
        {
            await RolloverAsync(record.Index, cancellationToken).ConfigureAwait(false);
        }

        var sizeBefore = CurrentSegmentSize;
        var buffer = ArrayPool<byte>.Shared.Rent(frameSize);

        try
        {
            LogFrameCodec.Write(buffer, record);

            try
            {
                await _file!.WriteAsync(buffer.AsMemory(0, frameSize), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Незавершённая запись фрейма оставила в конце сегмента мусор: возвращаем файл
                // к последней целой границе, иначе следующая запись допишется после мусора
                // и сегмент перестанет читаться (§9.4).
                await RollBackAsync(sizeBefore).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        CurrentSegmentSize += frameSize;
        LastIndex = record.Index;
        LastTerm = record.Term;
    }

    /// <summary>Укорачивает сегмент до последней целой границы после неудачной записи фрейма.</summary>
    /// <remarks>
    /// Отказ усечения не подменяет исходную ошибку: он делает писатель непригодным
    /// (<see cref="EnsureUsable"/>), и это видно на следующей же операции. Молча продолжать
    /// писать в сегмент с мусором нельзя.
    /// </remarks>
    private async ValueTask RollBackAsync(long size)
    {
        if (_file is not { } file)
        {
            return;
        }

        try
        {
            // Отмена не участвует: возврат к целой границе обязан произойти до выхода из метода.
            await file.TruncateAsync(size, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _broken = $"сегмент '{file.Path}' не удалось вернуть к длине {size} байт после неудачной записи фрейма: {exception.Message}";
        }
    }

    /// <summary>Отказывает в работе, если предыдущая неудачная запись оставила сегмент оборванным.</summary>
    /// <exception cref="InvalidOperationException">Сегмент оборван, нужно восстановление.</exception>
    private void EnsureUsable()
    {
        if (_broken is { } reason)
        {
            throw new InvalidOperationException(
                $"Писатель непригоден: {reason}. Нужно восстановление сегмента (docs/formats/log-segment.md, §9.2, S1-T05).");
        }
    }

    /// <summary>Создаёт сегмент и fsync-ает его заголовок до первого фрейма (§9.3).</summary>
    private async ValueTask CreateSegmentAsync(LogIndex baseIndex, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_options.Directory, LogSegmentFormat.GetFileName(baseIndex));
        var file = await _fileSystem.CreateAsync(path, cancellationToken).ConfigureAwait(false);

        try
        {
            var header = new SegmentHeader(baseIndex, _clock.UnixTimeMs);
            var buffer = new byte[LogSegmentFormat.HeaderSize];

            SegmentHeaderCodec.Write(buffer, header);
            await file.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            await file.FlushToDiskAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await file.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Отказ закрытия не подменяет исходную ошибку: она и есть причина, по которой
                // создание сегмента не состоялось.
            }

            throw;
        }

        _file = file;
        CurrentSegmentSize = LogSegmentFormat.HeaderSize;
        CurrentSegmentBaseIndex = baseIndex;
        _createdSegments.Add(baseIndex);
    }

    /// <summary>Запечатывает текущий сегмент в обязательном порядке §8 и открывает следующий.</summary>
    private async ValueTask RolloverAsync(LogIndex baseIndex, CancellationToken cancellationToken)
    {
        var sealedFile = _file!;

        // Шаг 1: fsync запечатываемого сегмента - иначе после краха он остался бы с
        // неотфлашенным хвостом, а это неотличимо от порчи.
        await sealedFile.FlushToDiskAsync(cancellationToken).ConfigureAwait(false);

        _file = null;
        CurrentSegmentSize = 0;

        await sealedFile.DisposeAsync().ConfigureAwait(false);

        // Шаг 2: создать новый файл и fsync его заголовок. Шаг 3 (запись фрейма) - у вызывающего.
        // Отказ здесь оставляет писатель без файла, а журнал - с запечатанным сегментом: продолжать
        // молча нельзя (следующий append упёрся бы в недописанный файл), поэтому писатель
        // помечается непригодным, как и после неудачного отката записи фрейма.
        try
        {
            await CreateSegmentAsync(baseIndex, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _broken = $"rollover в сегмент с базовым индексом {baseIndex} не завершён: {exception.Message}";
            throw;
        }
    }
}
