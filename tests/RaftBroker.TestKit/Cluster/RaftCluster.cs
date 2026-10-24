using RaftBroker.Core.Primitives;
using RaftBroker.Core.Randomness;
using RaftBroker.Core.Time;
using RaftBroker.Raft;
using RaftBroker.Raft.Elections;
using RaftBroker.Raft.State;
using RaftBroker.Storage.HardState;
using RaftBroker.Storage.Log;
using RaftBroker.TestKit.IO;
using RaftBroker.TestKit.Randomness;
using RaftBroker.TestKit.Time;

namespace RaftBroker.TestKit.Cluster;

/// <summary>
/// Кластер узлов Raft на виртуальном времени (<c>S2-T05</c>): общие часы, in-proc сеть, отдельные хранилища.
/// </summary>
/// <remarks>
/// <para>
/// У каждого узла свои файловая система, журнал и hard state: узел переживает «рестарт» только
/// тем, что записал, поэтому общее хранилище сделало бы тесты выборов бессмысленными.
/// </para>
/// <para>
/// Детерминизм держится на трёх вещах: одни часы на всех, синхронный транспорт и фиксированный
/// порядок обхода узлов. Разброс сроков задаётся <see cref="RaftClusterOptions.RandomFor"/>:
/// тест, которому нужно, чтобы таймер сработал ровно у одного узла, даёт ему наименьшее значение.
/// </para>
/// </remarks>
public sealed class RaftCluster : IAsyncDisposable
{
    private readonly List<RaftNode> _nodes = [];
    private readonly List<NodeId> _slots = [];
    private readonly Dictionary<NodeId, FakeStorageFileSystem> _fileSystems = [];
    private readonly Dictionary<NodeId, ILogStore> _logs = [];
    private readonly RaftClusterOptions _options;

    private RaftCluster(RaftClusterOptions options)
    {
        _options = options;
        Clock = new TestClock();
        Network = new InProcCluster();
        Configuration = options.Configuration ?? new StaticClusterConfiguration(Ids(options.NodeCount));
    }

    /// <summary>Общие виртуальные часы кластера.</summary>
    public TestClock Clock { get; }

    /// <summary>Сеть, через которую узлы обмениваются RPC.</summary>
    public InProcCluster Network { get; }

    /// <summary>Применяемая конфигурация: источник кворума для всех узлов.</summary>
    public IClusterConfiguration Configuration { get; }

    /// <summary>Узлы в порядке создания.</summary>
    public IReadOnlyList<RaftNode> Nodes => _nodes;

    /// <summary>Узел по идентификатору.</summary>
    public RaftNode this[NodeId id] => _nodes.Single(node => node.Id == id);

    /// <summary>Журнал узла: нужен тестам, которые собирают узел вручную.</summary>
    public ILogStore LogOf(NodeId id) => _logs[id];

    /// <summary>Файловая система узла: по ней тест проверяет, что запись на диск была или её не было.</summary>
    public FakeStorageFileSystem FileSystemOf(NodeId id) => _fileSystems[id];

    /// <summary>Идентификатор узла по порядковому номеру.</summary>
    public static NodeId IdAt(int index) => new($"node-{index + 1}");

    /// <summary>Создаёт кластер: узлы открывают свои хранилища и начинают отсчёт выборов.</summary>
    /// <param name="options">Параметры; по умолчанию - три узла и срок 200 мс.</param>
    public static async ValueTask<RaftCluster> CreateAsync(RaftClusterOptions? options = null)
    {
        var cluster = new RaftCluster(options ?? new RaftClusterOptions());

        for (var index = 0; index < cluster._options.NodeCount; index++)
        {
            await cluster.AddNodeAsync(IdAt(index)).ConfigureAwait(false);
        }

        return cluster;
    }

    /// <summary>Продвигает время и выполняет отложенную работу всех узлов.</summary>
    /// <param name="delta">На сколько продвинуть виртуальное время.</param>
    public async ValueTask AdvanceAsync(TimeSpan delta)
    {
        Clock.Advance(delta);
        await StepNodesAsync().ConfigureAwait(false);
    }

    /// <summary>Выполняет отложенную работу всех узлов в фиксированном порядке.</summary>
    public async ValueTask StepNodesAsync()
    {
        foreach (var node in _nodes)
        {
            await node.StepAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Останавливает узел, не освобождая его хранилища: процесс «упал».</summary>
    /// <param name="id">Кого остановить.</param>
    /// <remarks>
    /// Журнал и hard state остаются на своих местах - именно они переживают падение.
    /// Узел снимается с регистрации в сети: сообщение упавшему узлу - это недоставка, а не вызов
    /// метода освобождённого объекта.
    /// </remarks>
    public async ValueTask StopAsync(NodeId id)
    {
        var index = _nodes.FindIndex(node => node.Id == id);

        await _nodes[index].DisposeAsync().ConfigureAwait(false);
        _nodes.RemoveAt(index);
        Network.Unregister(id);
    }

    /// <summary>
    /// Перезапускает остановленный узел (<c>S2-T10</c>): те же файлы, новый объект узла.
    /// </summary>
    /// <param name="id">Кого вернуть в кластер.</param>
    /// <returns>Живой узел с состоянием, прочитанным с диска.</returns>
    /// <remarks>
    /// Term и голос берутся из hard state, а не из памяти: именно это и проверяет фаззер -
    /// голос обязан пережить падение (R6). Журнал переиспользуется открытым: на S2 в него пишется
    /// только no-op запись, а восстановление журнала - предмет <c>S1-T07</c>.
    /// </remarks>
    public async ValueTask<RaftNode> RestartAsync(NodeId id)
    {
        if (_nodes.Exists(node => node.Id == id))
        {
            throw new InvalidOperationException($"Узел '{id}' уже работает: перезапускать нечего.");
        }

        if (!_fileSystems.TryGetValue(id, out var fileSystem))
        {
            throw new ArgumentException($"Узел '{id}' в этом кластере не создавался.", nameof(id));
        }

        var state = await RaftState
            .LoadAsync(new HardStateStore(fileSystem, HardStateDirectory(id)), CancellationToken.None)
            .ConfigureAwait(false);

        return AddLiveNode(id, state, fileSystem);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var node in _nodes)
        {
            await node.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var log in _logs.Values)
        {
            await log.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static NodeId[] Ids(int count)
    {
        var ids = new NodeId[count];

        for (var index = 0; index < count; index++)
        {
            ids[index] = IdAt(index);
        }

        return ids;
    }

    private async ValueTask AddNodeAsync(NodeId id)
    {
        var fileSystem = new FakeStorageFileSystem();
        var open = await LogStore.OpenAsync(
            fileSystem,
            Clock,
            new SegmentWriterOptions { Directory = Path.Combine(id.Value, "log") },
            CancellationToken.None).ConfigureAwait(false);

        if (!open.IsSuccess)
        {
            throw new InvalidOperationException($"Журнал узла '{id}' не открылся: {open.Reason}");
        }

        var state = await RaftState
            .LoadAsync(new HardStateStore(fileSystem, HardStateDirectory(id)), CancellationToken.None)
            .ConfigureAwait(false);

        _fileSystems[id] = fileSystem;
        _logs[id] = open.Store!;
        _slots.Add(id);

        AddLiveNode(id, state, fileSystem);
    }

    private static string HardStateDirectory(NodeId id) => Path.Combine(id.Value, "state");

    /// <summary>Создаёт узел поверх готового хранилища и ставит его в слот своего идентификатора.</summary>
    /// <remarks>
    /// Порядок в <see cref="Nodes"/> - это порядок шагов, поэтому перезапущенный узел встаёт
    /// на место своего слота, а не в конец: иначе один и тот же сид давал бы разные прогоны.
    /// </remarks>
    private RaftNode AddLiveNode(NodeId id, RaftState state, FakeStorageFileSystem fileSystem)
    {
        var slot = _slots.IndexOf(id);

        var node = new RaftNode(
            new RaftNodeOptions(id, _options.Election, _options.HeartbeatInterval, _options.PreVote, _options.CheckQuorum),
            state,
            _logs[id],
            Network.CreateTransport(id),
            Configuration,
            Clock,
            _options.RandomFor(slot));

        var at = _nodes.FindIndex(existing => _slots.IndexOf(existing.Id) > slot);
        _nodes.Insert(at < 0 ? _nodes.Count : at, node);
        Network.Register(id, node);
        node.Start();

        return node;
    }
}

/// <summary>Параметры кластера (<c>S2-T05</c>).</summary>
public sealed class RaftClusterOptions
{
    /// <summary>Число узлов; по умолчанию три - минимальный кластер с настоящим кворумом.</summary>
    public int NodeCount { get; set; } = 3;

    /// <summary>Базовый срок таймера выборов.</summary>
    public ElectionTimerOptions Election { get; set; } = ElectionTimerOptions.Default;

    /// <summary>Включён ли PreVote у узлов кластера.</summary>
    public bool PreVote { get; set; } = true;

    /// <summary>Включён ли CheckQuorum и leader stickiness у узлов кластера (<c>S2-T09</c>).</summary>
    public bool CheckQuorum { get; set; } = true;

    /// <summary>Интервал heartbeat; по умолчанию треть срока выборов.</summary>
    public TimeSpan? HeartbeatInterval { get; set; }

    /// <summary>Применяемая конфигурация; по умолчанию - статическая из всех узлов.</summary>
    public IClusterConfiguration? Configuration { get; set; }

    /// <summary>
    /// Источник разброса срока выборов по индексу узла: по умолчанию воспроизводимый сид.
    /// Меньшее значение сдвигает таймер узла раньше остальных.
    /// </summary>
    public Func<int, IRandomSource> RandomFor { get; set; } = index => new SeededRandomSource((ulong)(index + 1));
}
