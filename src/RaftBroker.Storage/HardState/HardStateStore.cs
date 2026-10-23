using System.Text;
using RaftBroker.Storage.IO;

namespace RaftBroker.Storage.HardState;

/// <summary>
/// Файл hard state: append-only записи с CRC, чтение - последняя целая (S2-T02).
/// </summary>
/// <remarks>
/// <para>
/// Формат - <c>docs/formats/hard-state.md</c>, обоснование выбора - ADR
/// <c>0014-hard-state-file.md</c>. Порядок записи один: дописать запись, fsync, и только потом
/// вернуть управление. Вызывающий отвечает на RPC после возврата из <see cref="WriteAsync"/>
/// (правило persist before respond).
/// </para>
/// <para>
/// Состояние кэшируется в памяти: запись без кэша читала бы файл целиком на каждый term.
/// Источник истины - файл: <see cref="ReadAsync"/> после рестарта читает его сканированием,
/// а не `.state`-файл рядом.
/// </para>
/// <para>
/// Обрыв записи и порча различаются: неполная запись в конце файла - обрыв, она усекается
/// при открытии; целая по длине запись с плохим CRC - порча, и она останавливает открытие.
/// Второе важнее первого: «взять предыдущий терм» после порчи означало бы разрешить второй
/// голос в том же term (R6).
/// </para>
/// </remarks>
public sealed class HardStateStore : IHardStateStore
{
    private readonly IStorageFileSystem _fileSystem;
    private readonly string _directory;

    // Одна операция за раз: чтение при открытии, запись и восстановление хвоста не должны
    // перемешиваться (та же модель, что у LogStore, ADR 0011).
    private readonly SemaphoreSlim _gate = new(1, 1);

    private HardState _state = HardState.Empty;
    private long _sequence;
    private long _length;
    private bool _loaded;

    /// <summary>Следующий append должен сначала убрать след неудавшейся записи.</summary>
    private bool _needsRepair;

    /// <summary>Создаёт хранилище hard state в каталоге узла.</summary>
    /// <param name="fileSystem">Файловая система: рантайм-реализация или тестовый дубль.</param>
    /// <param name="directory">Каталог; создаётся при первой записи.</param>
    public HardStateStore(IStorageFileSystem fileSystem, string directory)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        _fileSystem = fileSystem;
        _directory = directory;
    }

    /// <summary>Полный путь файла состояния.</summary>
    public string FilePath => Path.Combine(_directory, HardStateFormat.FileName);

    /// <inheritdoc />
    public async ValueTask<HardState> ReadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

            return _state;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException">Term меньше сохранённого: терм не убывает.</exception>
    public async ValueTask WriteAsync(HardState state, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

            if (state.CurrentTerm < _state.CurrentTerm)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(state),
                    state.CurrentTerm,
                    $"Term не убывает: в файле {_state.CurrentTerm}.");
            }

            var sequence = _sequence + 1;
            var record = new byte[HardStateRecordCodec.GetRecordSize(
                state.VotedFor is null ? 0 : Encoding.UTF8.GetByteCount(state.VotedFor.Value.Value))];
            HardStateRecordCodec.Write(record, sequence, state);

            try
            {
                await using var file = await _fileSystem.OpenAppendAsync(FilePath, cancellationToken).ConfigureAwait(false);
                await file.WriteAsync(record, cancellationToken).ConfigureAwait(false);
                await file.FlushToDiskAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Запись могла остаться недописанной: следующий append обязан сначала убрать её,
                // иначе она окажется в середине файла и чтение остановится на порче.
                _needsRepair = true;
                throw;
            }

            _state = state;
            _sequence = sequence;
            _length += record.Length;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Читает файл один раз: сканирование, усечение оборванного хвоста, кэш текущего состояния.</summary>
    private async ValueTask EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded && !_needsRepair)
        {
            return;
        }

        if (_needsRepair)
        {
            // Откат к последней подтверждённой длине, а не повторное сканирование: запись, на которой
            // упал fsync, могла оказаться на диске целиком, но подтверждённой не была, и принимать её
            // за состояние - значит поверить в голос, который после краха может исчезнуть (R6).
            await _fileSystem.TruncateAsync(FilePath, _length, cancellationToken).ConfigureAwait(false);
            _needsRepair = false;

            return;
        }

        IStorageReadFile? file;

        try
        {
            file = await _fileSystem.OpenReadAsync(FilePath, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            await using var created = await _fileSystem.CreateAsync(FilePath, cancellationToken).ConfigureAwait(false);
            _loaded = true;

            return;
        }

        await using (file)
        {
            var length = file.Length;
            var offset = 0L;
            var state = HardState.Empty;
            var sequence = 0L;
            var header = new byte[HardStateFormat.HeaderSize];

            while (length - offset >= HardStateFormat.HeaderSize)
            {
                if (!await ReadExactlyAsync(file, offset, header, cancellationToken).ConfigureAwait(false))
                {
                    break;
                }

                var size = HardStateRecordCodec.ReadRecordSize(header);

                if (length - offset < size)
                {
                    break;
                }

                var record = new byte[size];

                if (!await ReadExactlyAsync(file, offset, record, cancellationToken).ConfigureAwait(false))
                {
                    break;
                }

                var parsed = HardStateRecordCodec.Read(record);
                state = parsed.State;
                sequence = parsed.Sequence;
                offset += size;
            }

            if (offset < length)
            {
                // Оборванная запись в конце: целиком на устройство она не попала, подтверждённой не была.
                await _fileSystem.TruncateAsync(FilePath, offset, cancellationToken).ConfigureAwait(false);
            }

            _state = state;
            _sequence = sequence;
            _length = offset;
            _loaded = true;
        }
    }

    /// <summary>Дочитывает ровно <paramref name="buffer"/>.Length байт; <c>false</c> - файл кончился раньше.</summary>
    private static async ValueTask<bool> ReadExactlyAsync(
        IStorageReadFile file,
        long offset,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var read = 0;

        while (read < buffer.Length)
        {
            var count = await file.ReadAsync(offset + read, buffer[read..], cancellationToken).ConfigureAwait(false);

            if (count == 0)
            {
                return false;
            }

            read += count;
        }

        return true;
    }
}
