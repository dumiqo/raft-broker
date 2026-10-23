using RaftBroker.Storage.HardState;

namespace RaftBroker.TestKit.State;

/// <summary>
/// Подменное хранилище hard state (S2-T01): считает записи, помнит последнее состояние, умеет падать.
/// </summary>
/// <remarks>
/// Вызов <see cref="BeforeWrite"/> происходит до сохранения значения и до возврата из <see cref="WriteAsync"/>:
/// так тест видит состояние узла в момент, когда запись на диск ещё не завершена, и может проверить
/// правило persist before respond.
/// </remarks>
public sealed class RecordingHardStateStore : IHardStateStore
{
    /// <summary>Создаёт хранилище с заданным начальным состоянием (по умолчанию - пустым).</summary>
    public RecordingHardStateStore(HardState? initial = null) => Stored = initial ?? HardState.Empty;

    /// <summary>Сколько раз читали.</summary>
    public int ReadCount { get; private set; }

    /// <summary>Что сейчас лежит «на диске».</summary>
    public HardState Stored { get; private set; }

    /// <summary>Всё, что записали, по порядку.</summary>
    public List<HardState> Writes { get; } = [];

    /// <summary>Действие, вызываемое перед каждой записью.</summary>
    public Action<HardState>? BeforeWrite { get; set; }

    /// <summary>Если задано, <see cref="WriteAsync"/> бросает это исключение и ничего не пишет.</summary>
    public Exception? WriteFailure { get; set; }

    /// <inheritdoc />
    public ValueTask<HardState> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCount++;

        return ValueTask.FromResult(Stored);
    }

    /// <inheritdoc />
    public ValueTask WriteAsync(HardState state, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BeforeWrite?.Invoke(state);

        if (WriteFailure is not null)
        {
            return ValueTask.FromException(WriteFailure);
        }

        Writes.Add(state);
        Stored = state;

        return ValueTask.CompletedTask;
    }
}
