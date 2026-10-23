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
    /// <remarks>Журнал и hard state остаются на своих местах - именно они переживают падение.</remarks>
    public async ValueTask StopAsync(NodeId id)
    {
        var index = _nodes.FindIndex(node => node.Id == id);

        await _nodes[index].DisposeAsync().ConfigureAwait(false);
        _nodes.RemoveAt(index);
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
            .LoadAsync(new HardStateStore(fileSystem, Path.Combine(id.Value, "state")), CancellationToken.None)
            .ConfigureAwait(false);

        var node = new RaftNode(
            new RaftNodeOptions(id, _options.Election, _options.HeartbeatInterval),
            state,
            open.Store!,
            Network.CreateTransport(id),
            Configuration,
            Clock,
            _options.RandomFor(_nodes.Count));

        _fileSystems[id] = fileSystem;
        _logs[id] = open.Store!;
        _nodes.Add(node);
        Network.Register(id, node);
        node.Start();
    }
}

/// <summary>Параметры кластера (<c>S2-T05</c>).</summary>
public sealed class RaftClusterOptions
{
    /// <summary>Число узлов; по умолчанию три - минимальный кластер с настоящим кворумом.</summary>
    public int NodeCount { get; set; } = 3;

    /// <summary>Базовый срок таймера выборов.</summary>
    public ElectionTimerOptions Election { get; set; } = ElectionTimerOptions.Default;

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
