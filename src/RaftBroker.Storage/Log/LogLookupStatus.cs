namespace RaftBroker.Storage.Log;

/// <summary>
/// Почему запись по индексу не найдена: её ещё нет или она уже вошла в снапшот.
/// </summary>
/// <remarks>
/// Разделение обязательно для Raft: «индекса ещё нет» означает, что репликация должна продолжить
/// с этого места, а «индекс ниже начала доступной истории» - что follower отстал настолько, что
/// ему нужен InstallSnapshot (<c>S3-T02</c>). Один <c>null</c> на оба случая заставил бы
/// вызывающего выводить ответ из <see cref="ILogStore.FirstIndex"/>, и первая же забытая
/// проверка отправила бы снапшот вместо продолжения репликации (или наоборот).
/// </remarks>
public enum LogLookupStatus
{
    /// <summary>Запись есть, она в <see cref="LogLookupResult.Record"/>.</summary>
    Found,

    /// <summary>Записи с таким индексом в журнале ещё нет: индекс больше <see cref="ILogStore.LastIndex"/>.</summary>
    NotYet,

    /// <summary>Запись была, но вошла в снапшот и больше не читается: индекс меньше <see cref="ILogStore.FirstIndex"/>.</summary>
    Compacted,
}
