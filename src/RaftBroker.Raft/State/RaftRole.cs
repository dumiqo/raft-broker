namespace RaftBroker.Raft.State;

/// <summary>
/// Роль узла Raft. Не сохраняется на диск: при старте узел всегда <see cref="Follower"/>,
/// а роль пересчитывается из term, голоса и сообщений (S2-T01).
/// </summary>
public enum RaftRole
{
    /// <summary>Пассивный узел: голосует, принимает heartbeat, ждёт таймаут выборов.</summary>
    Follower = 0,

    /// <summary>Собирает голоса в своём term (S2-T05).</summary>
    Candidate = 1,

    /// <summary>Отправляет heartbeat и реплицирует записи (S2-T06).</summary>
    Leader = 2,
}
