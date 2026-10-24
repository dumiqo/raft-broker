using RaftBroker.Core.Primitives;
using RaftBroker.Raft;
using RaftBroker.Raft.Elections;
using RaftBroker.Raft.Messages;
using RaftBroker.Raft.State;
using RaftBroker.TestKit.Cluster;
using RaftBroker.TestKit.Randomness;

namespace RaftBroker.UnitTests.Raft;

/// <summary>
/// Таблица правил PreVote (<c>S2-T07</c>, правило 4 спецификации этапа).
/// </summary>
public sealed class PreVoteRulesTests
{
    [Theory]
    [InlineData(5, 5, true)]
    [InlineData(6, 5, true)]
    [InlineData(4, 5, false)]
    [InlineData(1, 0, true)]
    public void WouldGrant_ComparesTheCandidatesTerm(int candidateTerm, int localTerm, bool expected)
    {
        // Лог одинаковый, поэтому решает только term кандидата.
        var request = new PreVoteRequest(new NodeId("node-2"), new Term(candidateTerm), new LogIndex(3), new Term(1));

        PreVoteRules
            .WouldGrant(request, new Term(localTerm), new Term(1), new LogIndex(3))
            .Should().Be(expected);
    }

    [Theory]
    [InlineData(3, 3, 3, 3, true)]
    [InlineData(3, 2, 3, 3, false)]
    [InlineData(2, 3, 3, 3, false)]
    [InlineData(4, 3, 3, 3, true)]
    public void WouldGrant_AtAnEqualTerm_RequiresAnUpToDateLog(
        int candidateIndex,
        int candidateTerm,
        int localIndex,
        int localTerm,
        bool expected)
    {
        var request = new PreVoteRequest(
            new NodeId("node-2"),
            new Term(5),
            new LogIndex(candidateIndex),
            new Term(candidateTerm));

        PreVoteRules
            .WouldGrant(request, new Term(5), new Term(localTerm), new LogIndex(localIndex))
            .Should().Be(expected);
    }

    [Fact]
    public void WouldGrant_WithAGreaterTerm_IgnoresTheLog()
    {
        // Кандидат из будущего уйдёт вперёд в любом случае, поэтому лог не спрашивают.
        var request = new PreVoteRequest(new NodeId("node-2"), new Term(9), LogIndex.Empty, new Term(0));

        PreVoteRules.WouldGrant(request, new Term(1), new Term(7), new LogIndex(10)).Should().BeTrue();
    }
}

/// <summary>
/// PreVote в узле (<c>S2-T07</c>): защита от disruption и неизменность состояния при опросе.
/// </summary>
public sealed class RaftNodePreVoteTests
{
    private static readonly NodeId First = RaftCluster.IdAt(0);
    private static readonly NodeId Second = RaftCluster.IdAt(1);
    private static readonly NodeId Third = RaftCluster.IdAt(2);

    private static readonly TimeSpan Timeout = ElectionTimerOptions.Default.Timeout;

    [Fact]
    public async Task StepAsync_WithPreVote_DoesNotRaiseTheTermWhileIsolated()
    {
        // Arrange: узел в сети один, кворума собрать не может.
        await using var cluster = await CreateClusterAsync();
        cluster.Network.Isolate(First);

        // Act: пять сроков подряд.
        for (var round = 0; round < 5; round++)
        {
            await cluster.AdvanceAsync(Timeout);
        }

        // Assert: ни term, ни голос не двигались, на диск не писались.
        cluster[First].Role.Should().Be(RaftRole.Candidate);
        cluster[First].CurrentTerm.Should().Be(new Term(0));
        cluster[First].VotedFor.Should().BeNull();
        cluster.FileSystemOf(First).FlushToDiskCount.Should().Be(0);
    }

    [Fact]
    public async Task StepAsync_WhenAnIsolatedNodeReturns_DoesNotDisturbTheLeader()
    {
        // Arrange: лидер работает, третий узел выпал и крутит выборы в одиночестве.
        await using var cluster = await CreateClusterAsync();
        cluster.Network.Isolate(Third);

        for (var round = 0; round < 3; round++)
        {
            await cluster.AdvanceAsync(Timeout);
        }

        cluster[First].Role.Should().Be(RaftRole.Leader);
        cluster[Third].CurrentTerm.Should().Be(new Term(0), "PreVote не поднимает term изолированному узлу");

        // Act: сеть вернулась, время идёт дальше.
        cluster.Network.Heal();
        await cluster.AdvanceAsync(Timeout);

        // Assert: лидер тот же, term кластера не вырос, второй лидер не появился.
        cluster[First].Role.Should().Be(RaftRole.Leader);
        cluster[First].CurrentTerm.Should().Be(new Term(1));
        cluster.Nodes.Count(node => node.Role == RaftRole.Leader).Should().Be(1);
        cluster[Third].Role.Should().Be(RaftRole.Follower);
        cluster[Third].LeaderId.Should().Be(First);
    }

    [Fact]
    public async Task StepAsync_WithoutPreVote_LetsAnIsolatedNodeInflateTheTerm()
    {
        // Документирующий тест: ровно от этого и защищает PreVote.
        await using var cluster = await CreateClusterAsync(preVote: false);
        cluster.Network.Isolate(First);

        await cluster.AdvanceAsync(Timeout);
        await cluster.AdvanceAsync(Timeout);

        cluster[First].CurrentTerm.Should().Be(new Term(2));
    }

    [Fact]
    public async Task StepAsync_WithAPreVoteQuorum_StartsTheRealRoundAndVotesForItself()
    {
        // Arrange
        await using var cluster = await CreateClusterAsync();

        // Act
        await cluster.AdvanceAsync(Timeout);

        // Assert: term поднялся ровно на единицу, голос за себя записан, узел - лидер.
        cluster[First].CurrentTerm.Should().Be(new Term(1));
        cluster[First].VotedFor.Should().Be(First);
        cluster[First].Role.Should().Be(RaftRole.Leader);
        cluster.FileSystemOf(First).FlushToDiskCount.Should().BePositive();
    }

    [Fact]
    public async Task HandlePreVoteAsync_ChangesNothingOnDiskOrInMemory()
    {
        // Arrange
        await using var cluster = await CreateElectedClusterAsync();
        var node = cluster[Third];
        var flushes = cluster.FileSystemOf(Third).FlushToDiskCount;
        var writes = cluster.FileSystemOf(Third).WriteCount;
        var term = node.CurrentTerm;
        var votedFor = node.VotedFor;

        // Act: согласие и отказ в одном тесте - состояние не меняется ни в одном случае.
        // Окно stickiness истекло: иначе отказ пришёл бы от живого лидера (S2-T09), а не от правил PreVote.
        cluster.Clock.Advance(Timeout + TimeSpan.FromMilliseconds(1));

        var granted = await node.HandlePreVoteAsync(
            new PreVoteRequest(Second, term.Next(), LogIndex.Empty, new Term(0)),
            CancellationToken.None);
        var denied = await node.HandlePreVoteAsync(
            new PreVoteRequest(Second, new Term(0), LogIndex.Empty, new Term(0)),
            CancellationToken.None);

        // Assert
        granted.VoteGranted.Should().BeTrue();
        denied.VoteGranted.Should().BeFalse();
        node.CurrentTerm.Should().Be(term);
        node.VotedFor.Should().Be(votedFor);
        cluster.FileSystemOf(Third).FlushToDiskCount.Should().Be(flushes);
        cluster.FileSystemOf(Third).WriteCount.Should().Be(writes);
    }

    [Fact]
    public async Task HandlePreVoteAsync_DoesNotResetTheElectionTimer()
    {
        // Arrange: опрос не должен позволять глушить выборы кластера.
        await using var cluster = await CreateClusterAsync();
        cluster.Network.Isolate(First);
        await cluster[First].HandlePreVoteAsync(
            new PreVoteRequest(Second, new Term(1), LogIndex.Empty, new Term(0)),
            CancellationToken.None);

        // Act
        await cluster.AdvanceAsync(Timeout);

        // Assert: таймер узла не сброшен опросом - срок истёк, раунд начался.
        cluster[First].Role.Should().Be(RaftRole.Candidate);
    }

    [Fact]
    public async Task StepAsync_WhenAPreVoteReplyCarriesAHigherTerm_StepsDown()
    {
        // Arrange: пир живёт в большем term, значит term узла устарел.
        await using var cluster = await CreateClusterAsync();
        cluster.Network.Register(Second, new PreVotePeer(new PreVoteResponse(new Term(4), VoteGranted: false)));
        cluster.Network.Register(Third, new PreVotePeer(new PreVoteResponse(new Term(4), VoteGranted: false)));

        // Act
        await cluster.AdvanceAsync(Timeout);

        // Assert
        cluster[First].Role.Should().Be(RaftRole.Follower);
        cluster[First].CurrentTerm.Should().Be(new Term(4));
        cluster[First].VotedFor.Should().BeNull();
    }

    [Fact]
    public async Task StepAsync_WithAPreVoteQuorumAndNoVoteQuorum_StaysCandidateWithTheRaisedTerm()
    {
        // Arrange: PreVote дают все, настоящий голос - только один пир.
        await using var cluster = await CreateClusterAsync();
        cluster.Network.Register(Second, new VoteDenyingPeer());
        cluster.Network.Register(Third, new VoteDenyingPeer());

        // Act
        await cluster.AdvanceAsync(Timeout);

        // Assert: раунд состоялся, кворума нет - узел остаётся кандидатом своего term.
        cluster[First].CurrentTerm.Should().Be(new Term(1));
        cluster[First].Role.Should().Be(RaftRole.Candidate);
        cluster[First].VotedFor.Should().Be(First);
    }

    private static async ValueTask<RaftCluster> CreateElectedClusterAsync()
    {
        var cluster = await CreateClusterAsync();
        await cluster.AdvanceAsync(Timeout);

        cluster[First].Role.Should().Be(RaftRole.Leader);

        return cluster;
    }

    private static ValueTask<RaftCluster> CreateClusterAsync(bool preVote = true)
        => RaftCluster.CreateAsync(new RaftClusterOptions
        {
            PreVote = preVote,
            // Таймер первого узла срабатывает раньше остальных.
            RandomFor = index => new ScriptedRandomSource(index * 20),
        });

    /// <summary>Пир, который отвечает на PreVote заданным значением и отказывает в настоящем голосе.</summary>
    private sealed class PreVotePeer(PreVoteResponse response) : IRaftMessageHandler
    {
        public ValueTask<PreVoteResponse> HandlePreVoteAsync(
            PreVoteRequest request,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(response);

        public ValueTask<RequestVoteResponse> HandleRequestVoteAsync(
            RequestVoteRequest request,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(new RequestVoteResponse(request.Term, VoteGranted: false));

        public ValueTask<AppendEntriesResponse> HandleAppendEntriesAsync(
            AppendEntriesRequest request,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(new AppendEntriesResponse(request.Term, Success: true));
    }

    /// <summary>Пир, который PreVote даёт, а настоящий голос - нет.</summary>
    private sealed class VoteDenyingPeer : IRaftMessageHandler
    {
        public ValueTask<PreVoteResponse> HandlePreVoteAsync(
            PreVoteRequest request,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(new PreVoteResponse(new Term(0), VoteGranted: true));

        public ValueTask<RequestVoteResponse> HandleRequestVoteAsync(
            RequestVoteRequest request,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(new RequestVoteResponse(request.Term, VoteGranted: false));

        public ValueTask<AppendEntriesResponse> HandleAppendEntriesAsync(
            AppendEntriesRequest request,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(new AppendEntriesResponse(request.Term, Success: true));
    }
}
