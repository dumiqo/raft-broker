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
    /// <param name="heartbeatInterval">
    /// Интервал heartbeat лидера; по умолчанию треть <c>T</c> - запас, при котором потеря одного
    /// heartbeat не запускает выборы.
    /// </param>
    /// <param name="preVote">
    /// Включён ли PreVote (<c>S2-T07</c>); по умолчанию да - защита от disruption нужна всегда,
    /// а выключение остаётся для сравнения поведения в симуляторе (<c>S8</c>).
    /// </param>
    /// <param name="checkQuorum">
    /// Включён ли CheckQuorum и leader stickiness (<c>S2-T09</c>); по умолчанию да, как и PreVote.
    /// Выключение оставляет «лидера-призрака» и разрешает сбивать живого лидера - это нужно только
    /// для сравнения поведения в симуляторе (<c>S8</c>), а не для боя.
    /// </param>
    public RaftNodeOptions(
        NodeId id,
        ElectionTimerOptions election,
        TimeSpan? heartbeatInterval = null,
        bool preVote = true,
        bool checkQuorum = true)
    {
        ArgumentNullException.ThrowIfNull(election);

        var interval = heartbeatInterval ?? election.Timeout / 3;

        if (interval <= TimeSpan.Zero || interval >= election.Timeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(heartbeatInterval),
                interval,
                "Интервал heartbeat обязан быть больше нуля и заметно меньше срока выборов.");
        }

        Id = id;
        Election = election;
        HeartbeatInterval = interval;
        PreVote = preVote;
        CheckQuorum = checkQuorum;
    }

    /// <summary>Идентификатор узла.</summary>
    public NodeId Id { get; }

    /// <summary>Базовый срок таймера выборов.</summary>
    public ElectionTimerOptions Election { get; }

    /// <summary>Интервал heartbeat лидера (<c>S2-T06</c>); меньше <c>T</c> обязательно.</summary>
    public TimeSpan HeartbeatInterval { get; }

    /// <summary>Предварять ли настоящий раунд выборов запросом PreVote (<c>S2-T07</c>).</summary>
    public bool PreVote { get; }

    /// <summary>Уходить ли лидеру без кворума и держать ли окно stickiness (<c>S2-T09</c>).</summary>
    public bool CheckQuorum { get; }
}
