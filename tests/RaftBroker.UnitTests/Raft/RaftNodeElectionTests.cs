using RaftBroker.Core.Primitives;
using RaftBroker.Raft;
using RaftBroker.Raft.Elections;
using RaftBroker.Raft.Messages;
using RaftBroker.Raft.State;
using RaftBroker.TestKit.Cluster;
using RaftBroker.TestKit.Randomness;
using RaftBroker.TestKit.State;

namespace RaftBroker.UnitTests.Raft;

/// <summary>
/// Кандидат и подсчёт кворума (<c>S2-T05</c>): self-vote, параллельные запросы, кворум из конфигурации.
/// </summary>
/// <remarks>
/// Кластер на виртуальных часах: срок выборов сдвинут так, чтобы первым срабатывал таймер
/// ровно одного узла, иначе все три стали бы кандидатами одновременно и тест проверял бы
/// разброс сроков, а не подсчёт голосов.
/// </remarks>
public sealed class RaftNodeElectionTests
{
    private static readonly NodeId First = RaftCluster.IdAt(0);
    private static readonly NodeId Second = RaftCluster.IdAt(1);
    private static readonly NodeId Third = RaftCluster.IdAt(2);

    private static readonly TimeSpan Timeout = ElectionTimerOptions.Default.Timeout;

    [Fact]
    public async Task StepAsync_WithoutAnExpiredTimeout_ChangesNothing()
    {
        // Arrange
        await using var cluster = await CreateClusterAsync();

        // Act
        await cluster.StepNodesAsync();

        // Assert
        cluster.Nodes.Should().OnlyContain(node => node.Role == RaftRole.Follower);
        cluster.Nodes.Should().OnlyContain(node => node.CurrentTerm == new Term(0));
        cluster.Network.SentCount.Should().Be(0);
    }

    [Fact]
    public async Task StepAsync_OnTimeout_VotesForItselfBeforeSendingAnything()
    {
        // Arrange
        await using var cluster = await CreateClusterAsync();
        var candidate = cluster[First];
        var votedOnDisk = false;

        cluster.Network.Partition(First, Second);
        cluster.Network.Partition(First, Third);

        // Act
        await cluster.AdvanceAsync(Timeout);

        // Assert: голос за себя записан (кандидат без голоса не начинает раунд), кворума нет.
        candidate.Role.Should().Be(RaftRole.Candidate);
        candidate.CurrentTerm.Should().Be(new Term(1));
        candidate.VotedFor.Should().Be(First);
        candidate.LeaderId.Should().BeNull();
        votedOnDisk.Should().BeFalse("проверка момента записи - в тесте ниже");
        cluster.Network.SentCount.Should().Be(2, "запрос уходит обоим пирам, а не по одному");
    }

    [Fact]
    public async Task StepAsync_WithTwoVotesOfThree_BecomesLeader()
    {
        // Arrange: третий узел недоступен, кворум дают собственный голос и один пир.
        await using var cluster = await CreateClusterAsync();
        cluster.Network.Isolate(Third);

        // Act
        await cluster.AdvanceAsync(Timeout);

        // Assert
        cluster[First].Role.Should().Be(RaftRole.Leader);
        cluster[First].LeaderId.Should().Be(First);
        cluster[Second].CurrentTerm.Should().Be(new Term(1));
        cluster[Second].VotedFor.Should().Be(First);
    }

    [Fact]
    public async Task StepAsync_WithNoReachablePeers_StaysCandidateAndRaisesTheTermOnTheNextRound()
    {
        // Arrange
        await using var cluster = await CreateClusterAsync();
        cluster.Network.Isolate(First);

        // Act
        await cluster.AdvanceAsync(Timeout);

        // Assert: остался кандидатом, но кластеру не мешает - ответил на запрос и поднял term в новом раунде.
        cluster[First].Role.Should().Be(RaftRole.Candidate);
        cluster[First].CurrentTerm.Should().Be(new Term(1));

        await cluster.AdvanceAsync(Timeout);

        cluster[First].Role.Should().Be(RaftRole.Candidate);
        cluster[First].CurrentTerm.Should().Be(new Term(2), "новый раунд начинается с term + 1");
    }

    [Fact]
    public async Task StepAsync_WhileCandidate_StillAnswersVoteRequests()
    {
        // Arrange
        await using var cluster = await CreateClusterAsync();
        cluster.Network.Isolate(First);
        await cluster.AdvanceAsync(Timeout);

        // Act: кандидат получает запрос от узла с более свежим term.
        var response = await cluster[First].HandleRequestVoteAsync(
            new RequestVoteRequest(Second, new Term(9), LogIndex.Empty, new Term(0)),
            CancellationToken.None);

        // Assert: узел не заблокирован собственными выборами.
        response.VoteGranted.Should().BeTrue();
        cluster[First].CurrentTerm.Should().Be(new Term(9));
        cluster[First].Role.Should().Be(RaftRole.Follower);
    }

    [Fact]
    public async Task StepAsync_WhenAPeerAnswersWithAHigherTerm_StepsDownImmediately()
    {
        // Arrange: второй узел отвечает отказом, но с большим term.
        await using var cluster = await CreateClusterAsync();
        var stale = new StubPeer(new RequestVoteResponse(new Term(5), VoteGranted: false));
        cluster.Network.Register(Second, stale);

        // Act
        await cluster.AdvanceAsync(Timeout);

        // Assert
        cluster[First].Role.Should().Be(RaftRole.Follower);
        cluster[First].CurrentTerm.Should().Be(new Term(5));
        cluster[First].VotedFor.Should().BeNull("рост term обнуляет голос");
    }

    [Fact]
    public async Task StepAsync_WhenTheRoundBecameStaleWhileTheRequestsWereInFlight_IgnoresTheReplies()
    {
        // Arrange: пир, который во время ответа переводит узел в follower чужим term.
        await using var cluster = await CreateClusterAsync();
        var candidate = cluster[First];
        var saboteur = new StubPeer(
            new RequestVoteResponse(new Term(1), VoteGranted: true),
            onRequest: async () => await candidate.HandleRequestVoteAsync(
                new RequestVoteRequest(Third, new Term(7), LogIndex.Empty, new Term(0)),
                CancellationToken.None));
        cluster.Network.Register(Second, saboteur);
        cluster.Network.Register(Third, saboteur);

        // Act
        await cluster.AdvanceAsync(Timeout);

        // Assert: ответы прошлого раунда не делают узел лидером.
        candidate.Role.Should().Be(RaftRole.Follower);
        candidate.CurrentTerm.Should().Be(new Term(7));
        candidate.LeaderId.Should().BeNull();
    }

    [Fact]
    public async Task StepAsync_CountsTheQuorumFromTheInjectedConfiguration()
    {
        // Arrange: конфигурация требует согласия всех трёх - двух голосов недостаточно.
        await using var cluster = await CreateClusterAsync(options =>
            options.Configuration = new UnanimousConfiguration([First, Second, Third]));
        cluster.Network.Isolate(Third);

        // Act
        await cluster.AdvanceAsync(Timeout);

        // Assert
        cluster[First].Role.Should().Be(RaftRole.Candidate, "состав кворума пришёл извне, а не из числа пиров");
    }

    [Fact]
    public async Task StepAsync_WithASingleNodeCluster_BecomesLeaderAlone()
    {
        // Arrange: кворум из одного узла - законная конфигурация.
        await using var cluster = await CreateClusterAsync(options => options.NodeCount = 1);

        // Act
        await cluster.AdvanceAsync(Timeout);

        // Assert
        cluster[First].Role.Should().Be(RaftRole.Leader);
        cluster.Network.SentCount.Should().Be(0);
    }

    [Fact]
    public async Task Constructor_WhenTheNodeIsNotInTheConfiguration_Throws()
    {
        // Arrange: узел вне кворума не может считать себя его частью.
        await using var cluster = await CreateClusterAsync();
        var state = await RaftState.LoadAsync(new RecordingHardStateStore(), CancellationToken.None);

        // Act
        var act = () => new RaftNode(
            new RaftNodeOptions(new NodeId("outsider"), ElectionTimerOptions.Default),
            state,
            cluster.LogOf(First),
            cluster.Network.CreateTransport(First),
            cluster.Configuration,
            cluster.Clock,
            new ScriptedRandomSource(0));

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task StepAsync_WhenTheTransportIsCancelled_LetsTheCancellationOut()
    {
        // Arrange: отмена - это не «пир не ответил», её нельзя проглатывать.
        await using var cluster = await CreateClusterAsync();
        cluster.Network.Register(Second, new CancellingPeer());
        cluster.Network.Register(Third, new CancellingPeer());

        // Act
        var act = async () => await cluster.AdvanceAsync(Timeout);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        cluster[First].Role.Should().Be(RaftRole.Candidate, "раунд прерван, но голос за себя уже записан");
    }

    private static ValueTask<RaftCluster> CreateClusterAsync(Action<RaftClusterOptions>? configure = null)
    {
        // Таймер срабатывает первым только у первого узла: 200 мс против 300 и 350.
        var options = new RaftClusterOptions
        {
            RandomFor = index => new ScriptedRandomSource(index * 100),
        };

        configure?.Invoke(options);

        return RaftCluster.CreateAsync(options);
    }

    /// <summary>Пир, который отвечает заданным значением и не является настоящим узлом.</summary>
    private sealed class StubPeer(RequestVoteResponse response, Func<ValueTask>? onRequest = null) : IRaftMessageHandler
    {
        public async ValueTask<RequestVoteResponse> HandleRequestVoteAsync(
            RequestVoteRequest request,
            CancellationToken cancellationToken)
        {
            if (onRequest is not null)
            {
                await onRequest().ConfigureAwait(false);
            }

            return response;
        }

        public ValueTask<AppendEntriesResponse> HandleAppendEntriesAsync(
            AppendEntriesRequest request,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(new AppendEntriesResponse(request.Term, Success: true));
    }

    /// <summary>Пир, отвечающий отменой: так выглядит истёкший deadline в настоящем транспорте.</summary>
    private sealed class CancellingPeer : IRaftMessageHandler
    {
        public ValueTask<RequestVoteResponse> HandleRequestVoteAsync(
            RequestVoteRequest request,
            CancellationToken cancellationToken)
            => ValueTask.FromException<RequestVoteResponse>(new OperationCanceledException());

        public ValueTask<AppendEntriesResponse> HandleAppendEntriesAsync(
            AppendEntriesRequest request,
            CancellationToken cancellationToken)
            => ValueTask.FromException<AppendEntriesResponse>(new OperationCanceledException());
    }

    /// <summary>Конфигурация, требующая согласия всех: число голосов не выводится из размера списка.</summary>
    private sealed class UnanimousConfiguration(NodeId[] voters) : IClusterConfiguration
    {
        public IReadOnlyCollection<NodeId> Voters { get; } = voters;

        public bool IsQuorum(IReadOnlyCollection<NodeId> acknowledgers)
            => voters.All(acknowledgers.Contains);
    }
}
