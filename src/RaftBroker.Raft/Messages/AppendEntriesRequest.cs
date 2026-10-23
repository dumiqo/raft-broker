using RaftBroker.Core.Primitives;
using RaftBroker.Storage.Log;

namespace RaftBroker.Raft.Messages;

/// <summary>
/// AppendEntries (<c>S2-T06</c>): heartbeat и репликация одной и той же формой запроса.
/// </summary>
/// <remarks>
/// <para>
/// Heartbeat - это <c>AppendEntries</c> с пустым <paramref name="Entries"/>, а не отдельный RPC:
/// так говорит <c>proto/raft.proto</c>, и второй RPC пришлось бы поддерживать в двух транспортах.
/// </para>
/// <para>
/// Поля <paramref name="PrevLogIndex"/>, <paramref name="PrevLogTerm"/> и <paramref name="LeaderCommit"/>
/// заполняются уже сейчас, потому что это форма сообщения из протокола; проверять их получатель
/// начнёт в <c>S3-T01</c>. До этого узел репликации не ведёт и шлёт только пустые heartbeat.
/// </para>
/// </remarks>
/// <param name="Leader">Лидер, отправивший сообщение: по нему follower запоминает <c>LeaderId</c>.</param>
/// <param name="Term">Term лидера.</param>
/// <param name="PrevLogIndex">Индекс записи непосредственно перед <paramref name="Entries"/>.</param>
/// <param name="PrevLogTerm">Term записи по индексу <paramref name="PrevLogIndex"/>.</param>
/// <param name="Entries">Записи репликации; пусто - heartbeat.</param>
/// <param name="LeaderCommit">Точка коммита лидера.</param>
public readonly record struct AppendEntriesRequest(
    NodeId Leader,
    Term Term,
    LogIndex PrevLogIndex,
    Term PrevLogTerm,
    IReadOnlyList<LogRecord> Entries,
    LogIndex LeaderCommit)
{
    /// <summary>Heartbeat: записей нет, остальное - снимок представления лидера.</summary>
    /// <param name="leader">Лидер.</param>
    /// <param name="term">Term лидера.</param>
    /// <param name="prevLogIndex">Индекс перед пакетом (для пустого пакета - <c>LastIndex</c>).</param>
    /// <param name="prevLogTerm">Term записи по этому индексу.</param>
    /// <param name="leaderCommit">Точка коммита лидера.</param>
    public static AppendEntriesRequest Heartbeat(
        NodeId leader,
        Term term,
        LogIndex prevLogIndex,
        Term prevLogTerm,
        LogIndex leaderCommit)
        => new(leader, term, prevLogIndex, prevLogTerm, [], leaderCommit);
}
