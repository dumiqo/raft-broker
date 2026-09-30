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
/// </remarks>
public sealed class SegmentWriter : IAsyncDisposable
{
    private readonly IStorageFileSystem _fileSystem;
    private readonly IClock _clock;
    private readonly SegmentWriterOptions _options;

    private IStorageFile? _file;
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

    /// <summary>Индекс последней записанной записи; <c>null</c>, если ещё ничего не записано.</summary>
    public LogIndex? LastIndex { get; private set; }

    /// <summary>Терм последней записанной записи; <c>null</c>, если ещё ничего не записано.</summary>
    public Term? LastTerm { get; private set; }

    /// <summary>Размер текущего сегмента в байтах, включая заголовок; 0 до создания сегмента.</summary>
    public long CurrentSegmentSize { get; private set; }

    /// <summary>Путь текущего сегмента; <c>null</c>, если сегмент ещё не создан.</summary>
    public string? CurrentSegmentPath => _file?.Path;

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

        if (records.Count == 0)
        {
            return;
        }

        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Неизвестный режим долговечности.");
        }

        // Порядок проверяется до первого байта на диске: неверная последовательность не должна
        // оставить часть записей записанными, а часть - нет.
        ValidateSequence(records);

        switch (mode)
        {
            case DurabilityMode.PerEntry:
                foreach (var record in records)
                {
                    await WriteFrameAsync(record, cancellationToken).ConfigureAwait(false);
                    await FlushToDiskAsync(cancellationToken).ConfigureAwait(false);
                }

                break;

            case DurabilityMode.GroupCommit:
                foreach (var record in records)
                {
                    await WriteFrameAsync(record, cancellationToken).ConfigureAwait(false);
                }

                await FlushToDiskAsync(cancellationToken).ConfigureAwait(false);
                break;

            default:
                // Periodic: fsync не вызывается, на устройство данные отправит FlushToDiskAsync.
                foreach (var record in records)
                {
                    await WriteFrameAsync(record, cancellationToken).ConfigureAwait(false);
                }

                break;
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

    /// <summary>Проверяет, что записи продолжают журнал: индекс растёт на 1, терм не убывает.</summary>
    private void ValidateSequence(IReadOnlyList<LogRecord> records)
    {
        var expected = LastIndex is null ? records[0].Index : LastIndex.Value.Next();
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

        var buffer = ArrayPool<byte>.Shared.Rent(frameSize);

        try
        {
            LogFrameCodec.Write(buffer, record);
            await _file!.WriteAsync(buffer.AsMemory(0, frameSize), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        CurrentSegmentSize += frameSize;
        LastIndex = record.Index;
        LastTerm = record.Term;
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
            await file.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        _file = file;
        CurrentSegmentSize = LogSegmentFormat.HeaderSize;
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
        await CreateSegmentAsync(baseIndex, cancellationToken).ConfigureAwait(false);
    }
}
