using RaftBroker.Core.Primitives;

namespace RaftBroker.Raft.State;

/// <summary>
/// Представление лидера о пире (<c>S2-T06</c>): что отправлять дальше и что подтверждено.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="MatchIndex"/> начинается с нуля, а не с <c>LastIndex</c>: совпадение журналов - это
/// подтверждение от пира, а не допущение лидера. Обратное предположение позволяет объявить
/// закоммиченной запись, которой у пира нет.
/// </para>
/// <para>
/// <see cref="LastResponseMs"/> - монотонная метка последнего ответа пира. Сбор здесь, решение -
/// в CheckQuorum (<c>S2-T09</c>): лидер, отрезанный от большинства, обязан сложить полномочия сам.
/// </para>
/// </remarks>
/// <param name="Peer">Пир.</param>
/// <param name="NextIndex">Индекс следующей отправляемой записи.</param>
/// <param name="MatchIndex">Наибольший индекс, подтверждённый пиром.</param>
/// <param name="LastResponseMs">Монотонная метка последнего ответа.</param>
public sealed record PeerProgress(NodeId Peer, LogIndex NextIndex, LogIndex MatchIndex, long LastResponseMs);
