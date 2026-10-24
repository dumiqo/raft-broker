using RaftBroker.Core.Primitives;
using RaftBroker.Raft;
using RaftBroker.Raft.Elections;
using RaftBroker.Raft.Messages;
using RaftBroker.Raft.State;
using RaftBroker.TestKit.Cluster;
using RaftBroker.TestKit.Randomness;

namespace RaftBroker.UnitTests.Raft;

/// <summary>
/// CheckQuorum и leader stickiness (<c>S2-T09</c>): лидер без кворума уходит сам, живой лидер не даёт себя сбить.
/// </summary>
/// <remarks>
/// Окно у обоих правил одно - срок выборов <c>T</c> - и меряется оно по <c>IClock.TimestampMs</c>,
/// поэтому граница проверяется точно: «чуть раньше» окна правило действует, «ровно окно» - уже нет.
/// </remarks>
public sealed class RaftNodeCheckQuorumTests
{
    private static readonly NodeId First = RaftCluster.IdAt(0);
    private static readonly NodeId Second = RaftCluster.IdAt(1);
    private static readonly NodeId Third = RaftCluster.IdAt(2);

    private static readonly TimeSpan Timeout = ElectionTimerOptions.Default.Timeout;
    private static readonly TimeSpan Heartbeat = TimeSpan.FromMilliseconds(20);

    [Fact]
    public async Task StepAsync_IsolatedLeader_ResignsExactlyAtTheWindow()
    {
        // Arrange
        await using var cluster = await CreateElectedClusterAsync();
        var leader = cluster[First];
        var votes = leader.VotedFor;

        cluster.Network.Isolate(First);

        var writes = cluster.FileSystemOf(First).WriteCount;

        // Act: за миллисекунду до окна лидер ещё лидер.
        await cluster.AdvanceAsync(Timeout - TimeSpan.FromMilliseconds(1));

        leader.Role.Should().Be(RaftRole.Leader);

        // ...а ровно на границе окна - уже нет.
        await cluster.AdvanceAsync(TimeSpan.FromMilliseconds(1));

        // Assert: уход без терма, без записи на диск и с сохранённым голосом.
        leader.Role.Should().Be(RaftRole.Follower);
        leader.LeaderId.Should().BeNull();
        leader.CurrentTerm.Should().Be(new Term(1));
        leader.VotedFor.Should().Be(votes);
        leader.LeaderProgress.Should().BeEmpty("репликаторы живут ровно столько, сколько роль лидера");
        cluster.FileSystemOf(First).WriteCount.Should().Be(writes);
    }

    [Fact]
    public async Task StepAsync_AfterTheLeaderResigns_TheRestOfTheClusterElectsANewLeader()
    {
        // Arrange
        await using var cluster = await CreateElectedClusterAsync();
        cluster.Network.Isolate(First);

        // Act: окно ухода плюс два срока на новые выборы.
        await cluster.AdvanceAsync(Timeout);
        await cluster.AdvanceAsync(Timeout * 2);

        // Assert: лидер ровно один, и это не отрезанный узел.
        var leaders = cluster.Nodes.Where(node => node.Role == RaftRole.Leader).ToArray();
        leaders.Should().ContainSingle();
        leaders[0].Id.Should().NotBe(First);
        leaders[0].CurrentTerm.Should().Be(new Term(2));
        cluster[First].Role.Should().NotBe(RaftRole.Leader);
    }

    [Fact]
    public async Task StepAsync_WithAQuorumAnswering_StaysLeaderForAnyNumberOfWindows()
    {
        // Arrange
        await using var cluster = await CreateElectedClusterAsync();

        // Act: десять окон подряд.
        for (var window = 0; window < 10; window++)
        {
            await cluster.AdvanceAsync(Timeout);
        }

        // Assert: окно не сработало ни разу, выборов не было.
        cluster[First].Role.Should().Be(RaftRole.Leader);
        cluster[First].CurrentTerm.Should().Be(new Term(1));
        cluster.Nodes.Count(node => node.Role == RaftRole.Leader).Should().Be(1);
    }

    [Fact]
    public async Task StepAsync_WithCheckQuorumDisabled_KeepsTheGhostLeader()
    {
        // Документирующий тест: без CheckQuorum лидер, отрезанный от кластера, остаётся лидером.
        await using var cluster = await CreateElectedClusterAsync(checkQuorum: false);
        cluster.Network.Isolate(First);

        // Act
        await cluster.AdvanceAsync(Timeout * 5);

        // Assert
        cluster[First].Role.Should().Be(RaftRole.Leader);
        cluster[First].CurrentTerm.Should().Be(new Term(1));
    }

    [Fact]
    public async Task StepAsync_ForACandidate_DoesNotApplyCheckQuorum()
    {
        // Arrange: изолированный узел стал кандидатом и кворума собрать не может.
        await using var cluster = await CreateClusterAsync();
        cluster.Network.Isolate(First);
        await cluster.AdvanceAsync(Timeout);

        cluster[First].Role.Should().Be(RaftRole.Candidate);

        // Act: несколько окон кандидатом.
        await cluster.AdvanceAsync(Timeout * 3);

        // Assert: CheckQuorum - правило лидера, кандидата в follower не переводит.
        cluster[First].Role.Should().Be(RaftRole.Candidate);
    }

    [Fact]
    public async Task HandleRequestVoteAsync_WithinTheStickinessWindow_DeniesTheVote()
    {
        // Arrange: лидер жив, узел слышал его полсрока назад.
        await using var cluster = await CreateElectedClusterAsync();
        var voter = cluster[Third];
        cluster.Clock.Advance(Timeout / 2);

        // Act: свежий кандидат с большим term и не устаревшим логом.
        var response = await voter.HandleRequestVoteAsync(NewCandidateRequest(Second, voter.CurrentTerm.Next()), CancellationToken.None);

        // Assert: голоса нет, но уход по большему term состоялся - stickiness не отменяет правило 2.
        response.VoteGranted.Should().BeFalse();
        response.Term.Should().Be(new Term(2));
        voter.Role.Should().Be(RaftRole.Follower);
        voter.CurrentTerm.Should().Be(new Term(2));
        voter.VotedFor.Should().BeNull();
        cluster[First].Role.Should().Be(RaftRole.Leader, "сбить живого лидера через одного избирателя нельзя");
    }

    [Fact]
    public async Task HandleRequestVoteAsync_AtTheWindowBoundary_GrantsTheVote()
    {
        // Arrange: ровно срок после heartbeat - окно истекло, лидер считается потерянным.
        await using var cluster = await CreateElectedClusterAsync();
        var voter = cluster[Third];
        cluster.Clock.Advance(Timeout);

        // Act
        var response = await voter.HandleRequestVoteAsync(NewCandidateRequest(Second, voter.CurrentTerm.Next()), CancellationToken.None);

        // Assert
        response.VoteGranted.Should().BeTrue();
        voter.CurrentTerm.Should().Be(new Term(2));
        voter.VotedFor.Should().Be(Second);
    }

    [Fact]
    public async Task HandlePreVoteAsync_WithinTheStickinessWindow_DeniesWithoutTouchingTheState()
    {
        // Arrange
        await using var cluster = await CreateElectedClusterAsync();
        var voter = cluster[Third];
        var writes = cluster.FileSystemOf(Third).WriteCount;
        var flushes = cluster.FileSystemOf(Third).FlushToDiskCount;
        cluster.Clock.Advance(Timeout / 2);

        // Act
        var response = await voter.HandlePreVoteAsync(
            new PreVoteRequest(Second, voter.CurrentTerm.Next(), LogIndex.Empty, new Term(0)),
            CancellationToken.None);

        // Assert: отказ и ничего больше - ни term, ни голоса, ни диска.
        response.VoteGranted.Should().BeFalse();
        voter.CurrentTerm.Should().Be(new Term(1));
        voter.VotedFor.Should().Be(First);
        cluster.FileSystemOf(Third).WriteCount.Should().Be(writes);
        cluster.FileSystemOf(Third).FlushToDiskCount.Should().Be(flushes);
    }

    [Fact]
    public async Task HandlePreVoteAsync_AtTheWindowBoundary_GrantsThePreVote()
    {
        // Arrange
        await using var cluster = await CreateElectedClusterAsync();
        var voter = cluster[Third];
        cluster.Clock.Advance(Timeout);

        // Act
        var response = await voter.HandlePreVoteAsync(
            new PreVoteRequest(Second, voter.CurrentTerm.Next(), LogIndex.Empty, new Term(0)),
            CancellationToken.None);

        // Assert
        response.VoteGranted.Should().BeTrue();
        voter.CurrentTerm.Should().Be(new Term(1), "PreVote не меняет term ни при согласии, ни при отказе");
    }

    [Fact]
    public async Task HandleAppendEntriesAsync_WithinTheStickinessWindow_StillStepsDownOnAHigherTerm()
    {
        // Arrange: stickiness держит голоса, но не мешает обработке heartbeat.
        await using var cluster = await CreateElectedClusterAsync();

        // Act
        var response = await cluster[Third].HandleAppendEntriesAsync(
            AppendEntriesRequest.Heartbeat(Second, new Term(5), LogIndex.Empty, new Term(0), LogIndex.Empty),
            CancellationToken.None);

        // Assert
        response.Success.Should().BeTrue();
        cluster[Third].CurrentTerm.Should().Be(new Term(5));
        cluster[Third].Role.Should().Be(RaftRole.Follower);
        cluster[Third].LeaderId.Should().Be(Second);
    }

    [Fact]
    public void Options_EnableCheckQuorumByDefault()
    {
        new RaftNodeOptions(First, ElectionTimerOptions.Default).CheckQuorum.Should().BeTrue();
    }

    private static RequestVoteRequest NewCandidateRequest(NodeId candidate, Term term)
        => new(candidate, term, LogIndex.Empty, new Term(0));

    private static async ValueTask<RaftCluster> CreateElectedClusterAsync(bool checkQuorum = true)
    {
        var cluster = await CreateClusterAsync(checkQuorum);
        await cluster.AdvanceAsync(Timeout);

        cluster[First].Role.Should().Be(RaftRole.Leader);

        return cluster;
    }

    private static ValueTask<RaftCluster> CreateClusterAsync(bool checkQuorum = true)
        => RaftCluster.CreateAsync(new RaftClusterOptions
        {
            NodeCount = 3,
            HeartbeatInterval = Heartbeat,
            PreVote = false,
            CheckQuorum = checkQuorum,
            // Раньше всех срабатывает таймер первого узла: остальные проигрывают выборы.
            RandomFor = index => new ScriptedRandomSource(index * 20),
        });
}
