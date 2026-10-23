using RaftBroker.Core.Primitives;
using RaftBroker.Raft;
using RaftBroker.Raft.Elections;
using RaftBroker.Raft.Messages;
using RaftBroker.Raft.State;
using RaftBroker.Storage.HardState;
using RaftBroker.Storage.Log;
using RaftBroker.TestKit.Cluster;
using RaftBroker.TestKit.IO;
using RaftBroker.TestKit.Randomness;
using RaftBroker.TestKit.Time;

namespace RaftBroker.UnitTests.Raft;

/// <summary>
/// Матрица переходов по терму (<c>S2-T08</c>): роль × (терм меньше, равен, больше).
/// </summary>
/// <remarks>
/// Ячейки - данные, а не девять методов: иначе новая роль или новый вид сообщения добавляют
/// забытую ветку, а не строку таблицы. Проверяется одна точка - обработчик AppendEntries,
/// который проходит через <c>RaftRoleTransitions.Decide</c>; остальные пути меняются только
/// через <c>StepDownAsync</c>, и это закреплено отдельными тестами.
/// </remarks>
public sealed class RaftRoleTransitionTests
{
    private static readonly NodeId Self = new("node-1");
    private static readonly NodeId Leader = new("node-2");
    private static readonly NodeId Third = new("node-3");

    private static readonly Term Current = new(3);
    private static readonly Term SelfVoteTerm = Current;

    public static TheoryData<RaftRole, long, RaftRole, long, bool, bool, int> Matrix => new()
    {
        // роль, терм входящего, ожидаемая роль, ожидаемый терм, ожидаемый голос за себя, согласие, записей на диск
        { RaftRole.Follower, 2, RaftRole.Follower, 3, false, false, 0 },
        { RaftRole.Follower, 3, RaftRole.Follower, 3, false, true, 0 },
        { RaftRole.Follower, 4, RaftRole.Follower, 4, false, true, 1 },
        { RaftRole.Candidate, 2, RaftRole.Candidate, 3, true, false, 0 },
        { RaftRole.Candidate, 3, RaftRole.Follower, 3, true, true, 0 },
        { RaftRole.Candidate, 4, RaftRole.Follower, 4, false, true, 1 },
        { RaftRole.Leader, 2, RaftRole.Leader, 3, true, false, 0 },
        { RaftRole.Leader, 3, RaftRole.Follower, 3, true, true, 0 },
        { RaftRole.Leader, 4, RaftRole.Follower, 4, false, true, 1 },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task AppendEntries_FollowsTheTransitionMatrix(
        RaftRole role,
        long incomingTerm,
        RaftRole expectedRole,
        long expectedTerm,
        bool expectedSelfVote,
        bool expectedSuccess,
        int expectedWrites)
    {
        // Arrange: узел в заданной роли и терме, с записанным голосом за себя.
        await using var subject = await Subject.CreateAsync(role, Current);
        var writes = subject.FileSystem.WriteCount;

        // Act
        var response = await subject.Node.HandleAppendEntriesAsync(
            AppendEntriesRequest.Heartbeat(Leader, new Term(incomingTerm), LogIndex.Empty, new Term(0), LogIndex.Empty),
            CancellationToken.None);

        // Assert
        response.Success.Should().Be(expectedSuccess);
        subject.Node.Role.Should().Be(expectedRole);
        subject.Node.CurrentTerm.Should().Be(new Term(expectedTerm));
        (subject.Node.VotedFor == Self).Should().Be(expectedSelfVote, "голос сбрасывается только при росте терма");
        subject.FileSystem.WriteCount.Should().Be(
            writes + expectedWrites,
            "запись на диск нужна только когда терм вырос");
    }

    [Fact]
    public async Task AppendEntries_AfterAStepDown_ClearsTheLeaderProgress()
    {
        // Arrange: лидер с репликаторами уходит в follower по большему терму.
        await using var subject = await Subject.CreateAsync(RaftRole.Leader, Current);

        // Act
        await subject.Node.HandleAppendEntriesAsync(
            AppendEntriesRequest.Heartbeat(Leader, Current.Next(), LogIndex.Empty, new Term(0), LogIndex.Empty),
            CancellationToken.None);

        // Assert
        subject.Node.LeaderProgress.Should().BeEmpty();
        subject.Node.LeaderId.Should().Be(Leader);
    }

    [Fact]
    public async Task StepDown_IsIdempotentAndWritesOnce()
    {
        // Arrange
        await using var subject = await Subject.CreateAsync(RaftRole.Leader, Current);
        var writes = subject.FileSystem.WriteCount;

        // Act: одно и то же сообщение дважды.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await subject.Node.HandleAppendEntriesAsync(
                AppendEntriesRequest.Heartbeat(Leader, Current.Next(), LogIndex.Empty, new Term(0), LogIndex.Empty),
                CancellationToken.None);
        }

        // Assert
        subject.Node.CurrentTerm.Should().Be(Current.Next());
        subject.FileSystem.WriteCount.Should().Be(writes + 1);
    }

    [Fact]
    public async Task StartFromTermZero_WithAHeartbeatOfTermZero_WritesNothing()
    {
        // Arrange: узел только что поднялся, терм нулевой.
        await using var subject = await Subject.CreateAsync(RaftRole.Follower, new Term(0));
        var writes = subject.FileSystem.WriteCount;

        // Act
        var response = await subject.Node.HandleAppendEntriesAsync(
            AppendEntriesRequest.Heartbeat(Leader, new Term(0), LogIndex.Empty, new Term(0), LogIndex.Empty),
            CancellationToken.None);

        // Assert
        response.Success.Should().BeTrue();
        subject.Node.Role.Should().Be(RaftRole.Follower);
        subject.Node.CurrentTerm.Should().Be(new Term(0));
        subject.FileSystem.WriteCount.Should().Be(writes);
    }

    /// <summary>Один узел с собственной файловой системой: по ней видно, была ли запись на диск.</summary>
    private sealed class Subject : IAsyncDisposable
    {
        private Subject(RaftNode node, FakeStorageFileSystem fileSystem, ILogStore log)
        {
            Node = node;
            FileSystem = fileSystem;
            Log = log;
        }

        public RaftNode Node { get; }

        public FakeStorageFileSystem FileSystem { get; }

        public ILogStore Log { get; }

        public static async ValueTask<Subject> CreateAsync(RaftRole role, Term term)
        {
            var fileSystem = new FakeStorageFileSystem();
            var clock = new TestClock();

            var open = await LogStore.OpenAsync(
                fileSystem,
                clock,
                new SegmentWriterOptions { Directory = "log" },
                CancellationToken.None).ConfigureAwait(false);

            var hardState = new HardStateStore(fileSystem, "state");
            var state = await RaftState.LoadAsync(hardState, CancellationToken.None).ConfigureAwait(false);

            if (term > new Term(0))
            {
                // Follower голосовал за третьего, кандидат и лидер - за себя: так в матрице видно,
                // сбрасывается ли голос именно при росте терма.
                var votedFor = role == RaftRole.Follower ? Third : Self;
                await state.PersistAsync(term, votedFor, CancellationToken.None).ConfigureAwait(false);
            }

            state.Role = role;

            var configuration = new StaticClusterConfiguration([Self, Leader, Third]);
            var node = new RaftNode(
                new RaftNodeOptions(Self, ElectionTimerOptions.Default, preVote: false),
                state,
                open.Store!,
                new InProcCluster().CreateTransport(Self),
                configuration,
                clock,
                new ScriptedRandomSource(0));

            node.Start();

            return new Subject(node, fileSystem, open.Store!);
        }

        public async ValueTask DisposeAsync()
        {
            await Node.DisposeAsync().ConfigureAwait(false);
            await Log.DisposeAsync().ConfigureAwait(false);
        }
    }
}
