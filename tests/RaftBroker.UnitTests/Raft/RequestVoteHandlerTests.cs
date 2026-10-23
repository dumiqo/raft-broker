using RaftBroker.Core.Primitives;
using RaftBroker.Raft.Elections;
using RaftBroker.Raft.Messages;
using RaftBroker.Raft.State;
using RaftBroker.Storage.HardState;
using RaftBroker.TestKit.State;

namespace RaftBroker.UnitTests.Raft;

/// <summary>
/// Обработчик <c>RequestVote</c> (<c>S2-T04</c>): fsync до ответа, смена term и роли.
/// </summary>
/// <remarks>
/// Проверяется не только ответ, но и момент записи: <see cref="RecordingHardStateStore.BeforeWrite"/>
/// видит узел в тот момент, когда на диск ещё ничего не легло.
/// </remarks>
public sealed class RequestVoteHandlerTests
{
    private static readonly NodeId Candidate = new("candidate");
    private static readonly NodeId Other = new("other");

    [Fact]
    public async Task HandleAsync_WhenTheVoteIsGranted_PersistsBeforeResponding()
    {
        // Arrange
        var store = new RecordingHardStateStore(new HardState(new Term(5), null));
        var state = await RaftState.LoadAsync(store, CancellationToken.None);

        // Act
        var response = await RequestVoteHandler.HandleAsync(
            new RequestVoteRequest(Candidate, new Term(5), new LogIndex(3), new Term(2)),
            state,
            new Term(2),
            new LogIndex(3),
            CancellationToken.None);

        // Assert
        response.Should().Be(new RequestVoteResponse(new Term(5), VoteGranted: true));
        state.VotedFor.Should().Be(Candidate);
        store.Writes.Should().Equal([new HardState(new Term(5), Candidate)]);
    }

    [Fact]
    public async Task HandleAsync_WhenTheVoteIsGranted_TheDiskHoldsItBeforeTheNodeBelievesIt()
    {
        // Arrange
        var store = new RecordingHardStateStore(new HardState(new Term(5), null));
        var state = await RaftState.LoadAsync(store, CancellationToken.None);
        NodeId? votedForOnDisk = null;
        NodeId? votedForInMemory = null;

        store.BeforeWrite = _ =>
        {
            votedForOnDisk = state.VotedFor;
            votedForInMemory = state.VotedFor;
        };

        // Act
        await RequestVoteHandler.HandleAsync(
            new RequestVoteRequest(Candidate, new Term(5), LogIndex.Empty, new Term(0)),
            state,
            new Term(0),
            LogIndex.Empty,
            CancellationToken.None);

        // Assert: в момент записи память ещё не знает о голосе, диск - уже пишется.
        votedForOnDisk.Should().BeNull();
        votedForInMemory.Should().BeNull();
        state.VotedFor.Should().Be(Candidate);
    }

    [Fact]
    public async Task HandleAsync_WithAStaleTerm_DeniesWithoutTouchingTheDisk()
    {
        // Arrange
        var store = new RecordingHardStateStore(new HardState(new Term(5), Candidate));
        var state = await RaftState.LoadAsync(store, CancellationToken.None);

        // Act
        var response = await RequestVoteHandler.HandleAsync(
            new RequestVoteRequest(Other, new Term(4), new LogIndex(9), new Term(9)),
            state,
            new Term(9),
            new LogIndex(9),
            CancellationToken.None);

        // Assert
        response.Should().Be(new RequestVoteResponse(new Term(5), VoteGranted: false));
        store.Writes.Should().BeEmpty("состояние не изменилось - писать нечего");
    }

    [Fact]
    public async Task HandleAsync_WithAHigherTermAndAnUpToDateLog_StepsDownAndGrants()
    {
        // Arrange: узел был кандидатом, лидер ему известен - оба признака обязаны исчезнуть.
        var store = new RecordingHardStateStore(new HardState(new Term(5), Other));
        var state = await RaftState.LoadAsync(store, CancellationToken.None);
        state.Role = RaftRole.Candidate;
        state.LeaderId = Other;

        // Act
        var response = await RequestVoteHandler.HandleAsync(
            new RequestVoteRequest(Candidate, new Term(6), new LogIndex(3), new Term(3)),
            state,
            new Term(3),
            new LogIndex(3),
            CancellationToken.None);

        // Assert
        response.Should().Be(new RequestVoteResponse(new Term(6), VoteGranted: true));
        state.CurrentTerm.Should().Be(new Term(6));
        state.VotedFor.Should().Be(Candidate, "больший term обнуляет прежний голос, а не наследует его");
        state.Role.Should().Be(RaftRole.Follower);
        state.LeaderId.Should().BeNull();
        store.Writes.Should().Equal([new HardState(new Term(6), Candidate)]);
    }

    [Fact]
    public async Task HandleAsync_WithAHigherTermAndAStaleLog_StepsDownAndDenies()
    {
        // Arrange
        var store = new RecordingHardStateStore(new HardState(new Term(5), Other));
        var state = await RaftState.LoadAsync(store, CancellationToken.None);
        state.Role = RaftRole.Leader;

        // Act
        var response = await RequestVoteHandler.HandleAsync(
            new RequestVoteRequest(Candidate, new Term(6), new LogIndex(1), new Term(2)),
            state,
            new Term(3),
            new LogIndex(9),
            CancellationToken.None);

        // Assert
        response.Should().Be(new RequestVoteResponse(new Term(6), VoteGranted: false));
        state.CurrentTerm.Should().Be(new Term(6));
        state.VotedFor.Should().BeNull();
        state.Role.Should().Be(RaftRole.Follower);
        store.Writes.Should().Equal([new HardState(new Term(6), null)]);
    }

    [Fact]
    public async Task HandleAsync_WhenTheVoteIsAlreadyGivenToTheSameCandidate_GrantsAgainWithoutAWiderLog()
    {
        // Arrange: повторный запрос после потерянного ответа.
        var store = new RecordingHardStateStore(new HardState(new Term(5), Candidate));
        var state = await RaftState.LoadAsync(store, CancellationToken.None);

        // Act
        var response = await RequestVoteHandler.HandleAsync(
            new RequestVoteRequest(Candidate, new Term(5), LogIndex.Empty, new Term(0)),
            state,
            new Term(9),
            new LogIndex(9),
            CancellationToken.None);

        // Assert
        response.VoteGranted.Should().BeTrue();
        state.VotedFor.Should().Be(Candidate);
    }

    [Fact]
    public async Task HandleAsync_WhenTheVoteIsGivenToAnotherCandidate_KeepsTheTermAndTheVote()
    {
        // Arrange
        var store = new RecordingHardStateStore(new HardState(new Term(5), Other));
        var state = await RaftState.LoadAsync(store, CancellationToken.None);

        // Act
        var response = await RequestVoteHandler.HandleAsync(
            new RequestVoteRequest(Candidate, new Term(5), new LogIndex(9), new Term(9)),
            state,
            new Term(9),
            new LogIndex(9),
            CancellationToken.None);

        // Assert
        response.VoteGranted.Should().BeFalse();
        state.VotedFor.Should().Be(Other);
        store.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WhenThePersistFails_DoesNotChangeRoleAndDoesNotGrant()
    {
        // Arrange
        var store = new RecordingHardStateStore(new HardState(new Term(5), null))
        {
            WriteFailure = new IOException("fsync не удался."),
        };
        var state = await RaftState.LoadAsync(store, CancellationToken.None);
        state.Role = RaftRole.Candidate;

        // Act
        var act = async () => await RequestVoteHandler.HandleAsync(
            new RequestVoteRequest(Candidate, new Term(6), new LogIndex(3), new Term(3)),
            state,
            new Term(3),
            new LogIndex(3),
            CancellationToken.None);

        // Assert: голос без записи не выдаётся - иначе после краха он выдастся второй раз.
        await act.Should().ThrowAsync<IOException>();
        state.CurrentTerm.Should().Be(new Term(5));
        state.VotedFor.Should().BeNull();
        state.Role.Should().Be(RaftRole.Candidate);
    }
}
