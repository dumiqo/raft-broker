using RaftBroker.Core.Primitives;

namespace RaftBroker.Storage.Snapshot;

/// <summary>
/// Метаданные снапшота: два числа Raft, которых достаточно, чтобы продолжить журнал от снапшота.
/// </summary>
/// <remarks>
/// Больше ничего в метаданных нет намеренно: длина и контрольная сумма состояния - свойства
/// файла, а не вызывающего кода (<c>docs/formats/snapshot.md</c> §4). Момент создания снапшот
/// ставит хранилище по своим часам при записи.
/// </remarks>
/// <param name="LastIncludedIndex">Индекс последней записи, вошедшей в снапшот; больше нуля.</param>
/// <param name="LastIncludedTerm">Терм этой записи; 0, если журнал был пуст.</param>
public readonly record struct SnapshotMetadata(LogIndex LastIncludedIndex, Term LastIncludedTerm)
{
    /// <summary>Имя опубликованного файла этого снапшота (§2).</summary>
    public string FileName => SnapshotFormat.GetFileName(LastIncludedIndex);
}
