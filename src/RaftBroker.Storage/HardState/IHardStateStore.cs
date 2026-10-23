namespace RaftBroker.Storage.HardState;

/// <summary>
/// Хранилище hard state: атомарная запись пары (term, votedFor) с fsync (S2-T01, реализация - S2-T02).
/// </summary>
/// <remarks>
/// Контракт живёт в <c>Storage</c>, а не в <c>Raft</c>: долговечная запись - свойство хранилища,
/// и обратная ссылка сломала бы ранги слоёв. Обе операции - <see cref="ValueTask"/>, потому что
/// запись идёт через <see cref="IO.IStorageFileSystem"/> и на пути RPC не должна аллоцировать.
/// </remarks>
public interface IHardStateStore
{
    /// <summary>
    /// Читает сохранённое состояние. Файла нет - начальное состояние, а не ошибка; порча файла -
    /// ошибка, а не «начать с нуля» (иначе голос может быть отдан дважды в одном term).
    /// </summary>
    ValueTask<HardState> ReadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Записывает состояние с fsync. Возврат означает «на диске», а не «в буфере»: ответ на RPC
    /// отправляется только после этого (правило persist before respond).
    /// </summary>
    ValueTask WriteAsync(HardState state, CancellationToken cancellationToken);
}
