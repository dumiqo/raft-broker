using RaftBroker.Core.Primitives;
using RaftBroker.Raft;
using RaftBroker.Raft.Elections;
using RaftBroker.Raft.Messages;
using RaftBroker.Raft.State;
using RaftBroker.Storage.Log;
using RaftBroker.TestKit.Cluster;
using RaftBroker.TestKit.Randomness;

namespace RaftBroker.UnitTests.Raft;

/// <summary>
/// Лидер после победы (<c>S2-T06</c>): репликаторы, немедленный heartbeat, no-op запись нового term.
/// </summary>
/// <remarks>
/// Репликации на этом этапе нет: AppendEntries несёт только term и сброс таймера, поэтому тесты
/// проверяют инициализацию и ритм, а не пересылку записей (это <c>S3-T01</c>).
/// </remarks>
public sealed class RaftNodeLeaderTests
{
    private static readonly NodeId First = RaftCluster.IdAt(0);
    private static readonly NodeId Second = RaftCluster.IdAt(1);
    private static readonly NodeId Third = RaftCluster.IdAt(2);
    private static readonly NodeId Outsider = new("outsider");

    private static readonly TimeSpan Timeout = ElectionTimerOptions.Default.Timeout;
    private static readonly TimeSpan Heartbeat = TimeSpan.FromMilliseconds(20);

    [Fact]
    public async Task StepAsync_OnVictory_InitializesProgressForEveryPeerFromZeroConfirmation()
    {
        // Arrange
        await using var cluster = await CreateElectedClusterAsync();

        // Act
        var progress = cluster[First].LeaderProgress;

        // Assert: nextIndex - после no-op записи, matchIndex - ноль: подтверждения ещё не было.
        cluster[First].Role.Should().Be(RaftRole.Leader);
        cluster[First].LeaderId.Should().Be(First);
        progress.Keys.Should().BeEquivalentTo([Second, Third]);
        progress.Values.Should().OnlyContain(peer => peer.NextIndex == new LogIndex(2));
        progress.Values.Should().OnlyContain(peer => peer.MatchIndex == LogIndex.Empty);
    }

    [Fact]
    public async Task StepAsync_OnVictory_AppendsExactlyOneNoopEntryInTheNewTerm()
    {
        // Arrange
        await using var cluster = await CreateElectedClusterAsync();
        var log = cluster.LogOf(First);

        // Act
        var lookup = await log.GetAsync(new LogIndex(1), CancellationToken.None);

        // Assert
        lookup.Record.Should().NotBeNull();
        lookup.Record!.Type.Should().Be(LogRecordType.Noop);
        lookup.Record.Term.Should().Be(new Term(1));
        lookup.Record.Payload.IsEmpty.Should().BeTrue();
        log.LastIndex.Should().Be(new LogIndex(1));
    }

    [Fact]
    public async Task StepAsync_OnVictory_SendsHeartbeatsWithoutWaitingForTheInterval()
    {
        // Arrange
        await using var cluster = await CreateClusterAsync();

        // Act
        await cluster.AdvanceAsync(Timeout);

        // Assert: два запроса голоса и два heartbeat - в том же шаге, без продвижения времени.
        cluster.Network.SentCount.Should().Be(4);
        cluster[Second].LeaderId.Should().Be(First);
        cluster[Third].LeaderId.Should().Be(First);
    }

    [Fact]
    public async Task StepAsync_BeforeTheHeartbeatInterval_SendsNothing()
    {
        // Arrange
        await using var cluster = await CreateElectedClusterAsync();
        var sent = cluster.Network.SentCount;

        // Act
        await cluster.AdvanceAsync(Heartbeat - TimeSpan.FromMilliseconds(1));

        // Assert
        cluster.Network.SentCount.Should().Be(sent);

        await cluster.AdvanceAsync(TimeSpan.FromMilliseconds(1));

        cluster.Network.SentCount.Should().Be(sent + 2, "по интервалу heartbeat уходит всем пирам");
    }

    [Fact]
    public async Task StepAsync_WithHeartbeats_KeepsFollowersFromStartingElections()
    {
        // Arrange: без сброса таймера второй узел пошёл бы на выборы уже через 40 мс.
        await using var cluster = await CreateElectedClusterAsync();

        // Act: три интервала - время, за которое таймер второго узла истёк бы наверняка.
        await cluster.AdvanceAsync(Heartbeat);
        await cluster.AdvanceAsync(Heartbeat);
        await cluster.AdvanceAsync(Heartbeat);

        // Assert
        cluster[Second].Role.Should().Be(RaftRole.Follower);
        cluster[Second].CurrentTerm.Should().Be(new Term(1));
        cluster[Second].VotedFor.Should().Be(First, "голос в выборах остаётся, пока term не сменится");
        cluster[First].Role.Should().Be(RaftRole.Leader);
    }

    [Fact]
    public async Task HandleAppendEntriesAsync_WithALowerTerm_RejectsWithoutTouchingTheDisk()
    {
        // Arrange
        await using var cluster = await CreateElectedClusterAsync();
        var leader = cluster[First];
        var writes = cluster.FileSystemOf(First).WriteCount;

        // Act
        var response = await leader.HandleAppendEntriesAsync(Heartbeat2(Second, term: 0), CancellationToken.None);

        // Assert
        response.Success.Should().BeFalse();
        response.Term.Should().Be(new Term(1));
        leader.Role.Should().Be(RaftRole.Leader);
        cluster.FileSystemOf(First).WriteCount.Should().Be(writes);
    }

    [Fact]
    public async Task HandleAppendEntriesAsync_WithAHigherTerm_StepsDownAndClearsTheVote()
    {
        // Arrange
        await using var cluster = await CreateElectedClusterAsync();
        var leader = cluster[First];

        // Act
        var response = await leader.HandleAppendEntriesAsync(Heartbeat2(Second, term: 5), CancellationToken.None);

        // Assert
        response.Success.Should().BeTrue();
        leader.Role.Should().Be(RaftRole.Follower);
        leader.CurrentTerm.Should().Be(new Term(5));
        leader.VotedFor.Should().BeNull();
        leader.LeaderId.Should().Be(Second);
        leader.LeaderProgress.Should().BeEmpty("репликаторы живут ровно столько, сколько роль лидера");
    }

    [Fact]
    public async Task StepAsync_AfterSteppingDown_StopsSendingHeartbeats()
    {
        // Arrange
        await using var cluster = await CreateElectedClusterAsync();
        await cluster[First].HandleRequestVoteAsync(
            new RequestVoteRequest(Second, new Term(9), LogIndex.Empty, new Term(0)),
            CancellationToken.None);
        var sent = cluster.Network.SentCount;

        // Act
        await cluster.AdvanceAsync(Heartbeat);
        await cluster.AdvanceAsync(Heartbeat);

        // Assert
        cluster[First].Role.Should().Be(RaftRole.Follower);
        cluster.Network.SentCount.Should().Be(sent);
    }

    [Fact]
    public async Task StepAsync_OnASecondVictory_AppendsAnotherNoopEntry()
    {
        // Arrange: лидер ушёл в follower по большему term, затем снова выиграл выборы.
        await using var cluster = await CreateElectedClusterAsync();
        await cluster[First].HandleRequestVoteAsync(
            new RequestVoteRequest(Second, new Term(2), LogIndex.Empty, new Term(1)),
            CancellationToken.None);

        // Act
        await cluster.AdvanceAsync(Timeout);

        // Assert
        var log = cluster.LogOf(First);
        log.LastIndex.Should().Be(new LogIndex(2));
        cluster[First].CurrentTerm.Should().Be(new Term(3));
        (await log.GetAsync(new LogIndex(2), CancellationToken.None)).Record!.Term.Should().Be(new Term(3));
    }

    [Fact]
    public async Task StepAsync_WhenAHeartbeatIsAnsweredWithAHigherTerm_StepsDown()
    {
        // Arrange: пир знает больший term - лидер обязан уйти, не дожидаясь CheckQuorum.
        await using var cluster = await CreateElectedClusterAsync();
        cluster.Network.Register(Third, new HeartbeatPeer(new AppendEntriesResponse(new Term(5), Success: false)));

        // Act
        await cluster.AdvanceAsync(Heartbeat);

        // Assert
        cluster[First].Role.Should().Be(RaftRole.Follower);
        cluster[First].CurrentTerm.Should().Be(new Term(5));
    }

    [Fact]
    public async Task StepAsync_WhenTheHeartbeatIsCancelled_LetsTheCancellationOut()
    {
        // Arrange
        await using var cluster = await CreateElectedClusterAsync();
        cluster.Network.Register(Third, new HeartbeatPeer(cancelled: true));

        // Act
        var act = async () => await cluster.AdvanceAsync(Heartbeat);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task StepAsync_WhenTheHeartbeatHandlerThrows_LetsTheExceptionOut()
    {
        // Arrange: сбой в обработчике пира - баг, а не недоступный пир: лидер не должен
        // принимать его за «нет ответа» и уходить в follower по CheckQuorum на пустом месте.
        await using var cluster = await CreateElectedClusterAsync();
        cluster.Network.Register(Third, new HeartbeatPeer(broken: true));

        // Act
        var act = async () => await cluster.AdvanceAsync(Heartbeat);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task HandleAppendEntriesAsync_WithTheSameTerm_TurnsACandidateIntoAFollowerWithoutWriting()
    {
        // Arrange: первый узел сам объявил выборы в term 1, но кворума не собрал.
        await using var cluster = await CreateClusterAsync(count: 3, first: 1);
        cluster.Network.Isolate(First);
        await cluster.AdvanceAsync(Timeout);
        await cluster.AdvanceAsync(Timeout);

        cluster[First].Role.Should().Be(RaftRole.Candidate);
        cluster[Second].Role.Should().Be(RaftRole.Leader);
        var writes = cluster.FileSystemOf(First).WriteCount;

        // Act
        var response = await cluster[First].HandleAppendEntriesAsync(
            AppendEntriesRequest.Heartbeat(Second, new Term(1), LogIndex.Empty, new Term(0), LogIndex.Empty),
            CancellationToken.None);

        // Assert: term тот же, значит и на диск писать нечего.
        response.Success.Should().BeTrue();
        cluster[First].Role.Should().Be(RaftRole.Follower);
        cluster[First].LeaderId.Should().Be(Second);
        cluster.FileSystemOf(First).WriteCount.Should().Be(writes);
    }

    [Fact]
    public async Task Constructor_WithAHeartbeatIntervalNotSmallerThanTheTimeout_Throws()
    {
        // Arrange: heartbeat обязан успевать до истечения срока выборов.
        var act = () => new RaftNodeOptions(First, ElectionTimerOptions.Default, Timeout);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void HeartbeatInterval_DefaultsToAThirdOfTheElectionTimeout()
    {
        var options = new RaftNodeOptions(First, ElectionTimerOptions.Default);

        options.HeartbeatInterval.Should().Be(Timeout / 3);
    }

    /// <summary>Пир, который отвечает на heartbeat заданным значением, отменой или падением.</summary>
    private sealed class HeartbeatPeer(AppendEntriesResponse? response = null, bool cancelled = false, bool broken = false) : IRaftMessageHandler
    {
        public ValueTask<RequestVoteResponse> HandleRequestVoteAsync(
            RequestVoteRequest request,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(new RequestVoteResponse(request.Term, VoteGranted: true));

        public ValueTask<PreVoteResponse> HandlePreVoteAsync(
            PreVoteRequest request,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(new PreVoteResponse(new Term(0), VoteGranted: true));

        public ValueTask<AppendEntriesResponse> HandleAppendEntriesAsync(
            AppendEntriesRequest request,
            CancellationToken cancellationToken)
        {
            if (broken)
            {
                return ValueTask.FromException<AppendEntriesResponse>(new InvalidOperationException("сбой в обработчике"));
            }

            return cancelled
                ? ValueTask.FromException<AppendEntriesResponse>(new OperationCanceledException())
                : ValueTask.FromResult(response ?? new AppendEntriesResponse(request.Term, Success: true));
        }
    }

    private static AppendEntriesRequest Heartbeat2(NodeId leader, long term)
        => AppendEntriesRequest.Heartbeat(leader, new Term(term), LogIndex.Empty, new Term(0), LogIndex.Empty);

    private static async ValueTask<RaftCluster> CreateElectedClusterAsync()
    {
        var cluster = await CreateClusterAsync();
        await cluster.AdvanceAsync(Timeout);

        cluster[First].Role.Should().Be(RaftRole.Leader);

        return cluster;
    }

    private static ValueTask<RaftCluster> CreateClusterAsync(int count = 3, int first = 0)
        => RaftCluster.CreateAsync(new RaftClusterOptions
        {
            NodeCount = count,
            HeartbeatInterval = Heartbeat,
            PreVote = false,
            // Раньше всех срабатывает таймер узла с номером first: остальные проигрывают выборы.
            RandomFor = index => new ScriptedRandomSource((index + count - first) % count * 20),
        });
}
