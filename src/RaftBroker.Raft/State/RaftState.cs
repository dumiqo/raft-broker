using RaftBroker.Core.Primitives;
using RaftBroker.Storage.HardState;

namespace RaftBroker.Raft.State;

/// <summary>
/// Состояние узла Raft (S2-T01): durable-поля плюс volatile, которые выводятся заново.
/// </summary>
/// <remarks>
/// <para>
/// Таблица полей (спецификация этапа S2, «Модель состояния»):
/// </para>
/// <list type="table">
/// <item><term>CurrentTerm</term><description>durable: fsync до ответа на RPC, иначе повторное голосование в том же term (R6)</description></item>
/// <item><term>VotedFor</term><description>durable: по той же причине; <c>null</c> означает «голос не отдан»</description></item>
/// <item><term>CommitIndex</term><description>volatile: выводится из кворума и текущего term</description></item>
/// <item><term>LastApplied</term><description>volatile: истина в снапшоте, восстанавливается реплеем</description></item>
/// <item><term>Role</term><description>volatile: при старте всегда <see cref="RaftRole.Follower"/></description></item>
/// <item><term>LeaderId</term><description>volatile: подсказка, а не истина</description></item>
/// </list>
/// <para>
/// Разделение видно компилятору, а не только комментарию: у durable-полей нет публичных сеттеров,
/// единственный путь их изменить - <see cref="PersistAsync"/>, который сначала пишет на диск.
/// Соответствие таблицы и кода проверяет тест (рефлексией), поэтому новое durable-поле нельзя
/// добавить с обычным сеттером.
/// </para>
/// <para>
/// <c>NextIndex</c>/<c>MatchIndex</c> появляются в S2-T06 вместе с репликаторами: это состояние лидера
/// по каждому пиру, и без транспорта его не к чему привязать.
/// </para>
/// </remarks>
public sealed class RaftState
{
    private readonly IHardStateStore _hardState;

    private RaftState(IHardStateStore hardState, HardState stored)
    {
        _hardState = hardState;
        CurrentTerm = stored.CurrentTerm;
        VotedFor = stored.VotedFor;
    }

    /// <summary>Текущий term узла. Durable: меняется только через <see cref="PersistAsync"/>.</summary>
    public Term CurrentTerm { get; private set; }

    /// <summary>Кому отдан голос в <see cref="CurrentTerm"/>, <c>null</c> - никому. Durable.</summary>
    public NodeId? VotedFor { get; private set; }

    /// <summary>Индекс последней закоммиченной записи. Volatile: выводится из кворума.</summary>
    public LogIndex CommitIndex { get; set; }

    /// <summary>Индекс последней применённой к состоянию записи. Volatile: восстанавливается из снапшота.</summary>
    public LogIndex LastApplied { get; set; }

    /// <summary>Роль узла. Volatile: при старте <see cref="RaftRole.Follower"/>.</summary>
    public RaftRole Role { get; set; } = RaftRole.Follower;

    /// <summary>Лидер, от которого слышали heartbeat, <c>null</c> - неизвестен. Подсказка, не истина.</summary>
    public NodeId? LeaderId { get; set; }

    /// <summary>
    /// Загружает состояние узла: durable-часть с диска, volatile - начальные значения.
    /// </summary>
    public static async ValueTask<RaftState> LoadAsync(IHardStateStore hardState, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hardState);

        return new RaftState(hardState, await hardState.ReadAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Единственный путь изменить durable-поля: сначала запись с fsync, потом значение в памяти.
    /// Если запись не удалась, память остаётся прежней - иначе узел поведёт себя по term, которого нет на диске.
    /// </summary>
    /// <param name="term">Новый term.</param>
    /// <param name="votedFor">Кому отдан голос в новом term, <c>null</c> - никому.</param>
    /// <param name="cancellationToken">Отмена.</param>
    /// <exception cref="ArgumentOutOfRangeException">Новый term меньше текущего: терм не убывает.</exception>
    public async ValueTask PersistAsync(Term term, NodeId? votedFor, CancellationToken cancellationToken)
    {
        if (term < CurrentTerm)
        {
            throw new ArgumentOutOfRangeException(nameof(term), term, $"Терм не убывает: текущий - {CurrentTerm}.");
        }

        await _hardState.WriteAsync(new HardState(term, votedFor), cancellationToken).ConfigureAwait(false);
        CurrentTerm = term;
        VotedFor = votedFor;
    }
}
