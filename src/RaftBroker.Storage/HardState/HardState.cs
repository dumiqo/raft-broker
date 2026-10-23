using RaftBroker.Core.Primitives;

namespace RaftBroker.Storage.HardState;

/// <summary>
/// Долговечная часть состояния Raft: term узла и голос, отданный в этом term (S2-T01).
/// </summary>
/// <remarks>
/// Только эти два поля обязаны пережить падение - всё остальное состояние узла выводится
/// из журнала, снапшота и сообщений. Отсюда отдельный маленький формат, а не запись в журнал:
/// hard state меняется на каждый term и голос, сегмент журнала - на каждую запись.
/// </remarks>
public readonly record struct HardState(Term CurrentTerm, NodeId? VotedFor)
{
    /// <summary>Начальное состояние: term 0, голос не отдан.</summary>
    public static HardState Empty => default;
}
