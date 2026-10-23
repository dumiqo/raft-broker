using RaftBroker.Core.Primitives;
using RaftBroker.Raft.Elections;

namespace RaftBroker.Raft;

/// <summary>Параметры узла Raft (<c>S2-T05</c>).</summary>
/// <remarks>
/// Значения по умолчанию для <c>T</c> и окон - открытые решения этапа (спецификация S2,
/// открытые вопросы 1-3); здесь их можно задать явно, но нельзя забыть: параметры обязательны.
/// </remarks>
public sealed class RaftNodeOptions
{
    /// <summary>Создаёт параметры узла.</summary>
    /// <param name="id">Идентификатор узла; обязан входить в конфигурацию кластера.</param>
    /// <param name="election">Базовый срок таймера выборов <c>T</c>.</param>
    public RaftNodeOptions(NodeId id, ElectionTimerOptions election)
    {
        ArgumentNullException.ThrowIfNull(election);

        Id = id;
        Election = election;
    }

    /// <summary>Идентификатор узла.</summary>
    public NodeId Id { get; }

    /// <summary>Базовый срок таймера выборов; интервал heartbeat - его треть (<c>S2-T06</c>).</summary>
    public ElectionTimerOptions Election { get; }
}
