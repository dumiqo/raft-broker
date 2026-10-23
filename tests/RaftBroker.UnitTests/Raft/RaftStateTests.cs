using System.Reflection;
using RaftBroker.Core.Primitives;
using RaftBroker.Raft.State;
using RaftBroker.Storage.HardState;
using RaftBroker.TestKit.State;

namespace RaftBroker.UnitTests.Raft;

/// <summary>
/// Модель состояния узла (S2-T01): разделение durable и volatile видно компилятору,
/// и изменение durable-поля без записи на диск невозможно.
/// </summary>
public sealed class RaftStateTests
{
    private static readonly NodeId Node1 = new("node-1");
    private static readonly NodeId Node2 = new("node-2");

    [Fact]
    public async Task LoadAsync_WhenNothingIsStored_StartsAsFollowerAtTermZero()
    {
        var store = new RecordingHardStateStore();

        var state = await RaftState.LoadAsync(store, CancellationToken.None);

        store.ReadCount.Should().Be(1);
        state.CurrentTerm.Should().Be(default(Term));
        state.VotedFor.Should().BeNull("«не голосовал» и «голосовал за узел с пустым идентификатором» - разные состояния");
        state.Role.Should().Be(RaftRole.Follower);
        state.LeaderId.Should().BeNull();
        state.CommitIndex.Should().Be(LogIndex.Empty);
        state.LastApplied.Should().Be(LogIndex.Empty);
    }

    [Fact]
    public async Task LoadAsync_TakesDurableFieldsFromTheStore()
    {
        var store = new RecordingHardStateStore(new HardState(new Term(7), Node2));

        var state = await RaftState.LoadAsync(store, CancellationToken.None);

        state.CurrentTerm.Should().Be(new Term(7));
        state.VotedFor.Should().Be(Node2);
        state.Role.Should().Be(RaftRole.Follower, "роль на диск не пишется и пересчитывается при старте");
    }

    [Fact]
    public async Task LoadAsync_WithoutAStore_Throws()
    {
        var act = async () => await RaftState.LoadAsync(null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task PersistAsync_WritesToDiskBeforeChangingMemory()
    {
        var store = new RecordingHardStateStore();
        var state = await RaftState.LoadAsync(store, CancellationToken.None);
        Term? termDuringWrite = null;
        NodeId? votedForDuringWrite = null;
        store.BeforeWrite = _ =>
        {
            termDuringWrite = state.CurrentTerm;
            votedForDuringWrite = state.VotedFor;
        };

        await state.PersistAsync(new Term(3), Node1, CancellationToken.None);

        termDuringWrite.Should().Be(default(Term), "память меняется после записи, а не до неё");
        votedForDuringWrite.Should().BeNull();
        store.Writes.Should().ContainSingle().Which.Should().Be(new HardState(new Term(3), Node1));
        state.CurrentTerm.Should().Be(new Term(3));
        state.VotedFor.Should().Be(Node1);
    }

    [Fact]
    public async Task PersistAsync_WhenTheStoreFails_LeavesDurableFieldsUnchanged()
    {
        var store = new RecordingHardStateStore(new HardState(new Term(5), Node1));
        var state = await RaftState.LoadAsync(store, CancellationToken.None);
        store.WriteFailure = new IOException("диск отказал");

        var act = async () => await state.PersistAsync(new Term(6), null, CancellationToken.None);

        await act.Should().ThrowAsync<IOException>();
        state.CurrentTerm.Should().Be(new Term(5), "term, которого нет на диске, узлу знать нельзя");
        state.VotedFor.Should().Be(Node1);
        store.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task PersistAsync_WithAnOlderTerm_ThrowsAndDoesNotTouchTheDisk()
    {
        var store = new RecordingHardStateStore(new HardState(new Term(5), Node1));
        var state = await RaftState.LoadAsync(store, CancellationToken.None);

        var act = async () => await state.PersistAsync(new Term(4), Node2, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        store.Writes.Should().BeEmpty();
        state.CurrentTerm.Should().Be(new Term(5));
    }

    [Fact]
    public async Task PersistAsync_InTheSameTerm_WritesAgain()
    {
        // Hard state хранит факт «кому отдан голос в этом term», а не запрет записи: повторный ответ
        // на потерянный RPC - нормальный случай, и S2-T04 на него опирается.
        var store = new RecordingHardStateStore(new HardState(new Term(1), Node1));
        var state = await RaftState.LoadAsync(store, CancellationToken.None);

        await state.PersistAsync(new Term(1), Node1, CancellationToken.None);

        store.Writes.Should().ContainSingle();
        state.VotedFor.Should().Be(Node1);
    }

    [Fact]
    public void DurableFields_AreExactlyThePropertiesWithoutPublicSetter()
    {
        // DoD задачи: новое durable-поле нельзя добавить с обычным сеттером, а volatile - без сеттера.
        // Расхождение с таблицей в XML-doc ловится здесь, а не на ревью.
        var withoutSetter = typeof(RaftState)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.SetMethod is not { IsPublic: true })
            .Select(property => property.Name);

        withoutSetter.Should().BeEquivalentTo(
            ["CurrentTerm", "VotedFor"],
            "durable-поля - ровно те, что меняются только через PersistAsync");
    }
}
