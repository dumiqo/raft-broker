using RaftBroker.Core.Primitives;
using RaftBroker.Raft.Elections;
using RaftBroker.Raft.Messages;

namespace RaftBroker.UnitTests.Raft;

/// <summary>
/// Таблица случаев голосования (<c>S2-T04</c>, DoD задачи).
/// </summary>
/// <remarks>
/// Номера строк соответствуют таблице в <c>tasks/S2-T04-request-vote.md</c>: тест читается как она.
/// Случаи проверяются на чистой функции, поэтому каждая строка - это ровно одно правило.
/// </remarks>
public sealed class RequestVoteRulesTests
{
    private static readonly NodeId Candidate = new("candidate");
    private static readonly NodeId Other = new("other");

    [Theory]
    // 1. term меньше: отказ, состояние не меняется.
    [InlineData(4, 5, null, 9, 9, 9, 9, VoteDecision.Deny)]
    // 2. term больше, лог не устарел: step down и голос.
    [InlineData(6, 5, null, 9, 9, 9, 9, VoteDecision.StepDownAndGrant)]
    // 3. term больше, лог устарел: step down и отказ.
    [InlineData(6, 5, null, 8, 9, 9, 9, VoteDecision.StepDownAndDeny)]
    // 4. term равен, голоса нет, лог не устарел: голос.
    [InlineData(5, 5, null, 9, 9, 9, 9, VoteDecision.Grant)]
    // 5. term равен, голоса нет, term кандидата меньше: отказ.
    [InlineData(5, 5, null, 8, 100, 9, 9, VoteDecision.Deny)]
    // 6. term равен, голоса нет, index кандидата меньше: отказ.
    [InlineData(5, 5, null, 9, 8, 9, 9, VoteDecision.Deny)]
    // 7. term равен, голос уже этот: голос повторно.
    [InlineData(5, 5, "candidate", 1, 1, 9, 9, VoteDecision.Grant)]
    // 8. term равен, голос другой: отказ.
    [InlineData(5, 5, "other", 9, 9, 9, 9, VoteDecision.Deny)]
    // 9. term равен, голоса нет, term кандидата больше: голос, term главнее index.
    [InlineData(5, 5, null, 10, 1, 9, 100, VoteDecision.Grant)]
    // 10. пустой лог кандидата против непустого локального: отказ, а не исключение.
    [InlineData(5, 5, null, 0, 0, 9, 9, VoteDecision.Deny)]
    public void Decide_MatchesTheTable(
        long requestTerm,
        long currentTerm,
        string? votedFor,
        long candidateLastLogTerm,
        long candidateLastLogIndex,
        long localLastLogTerm,
        long localLastLogIndex,
        VoteDecision expected)
    {
        // Arrange
        var request = new RequestVoteRequest(
            Candidate,
            new Term(requestTerm),
            new LogIndex(candidateLastLogIndex),
            new Term(candidateLastLogTerm));

        // Act
        var decision = RequestVoteRules.Decide(
            request,
            new Term(currentTerm),
            votedFor is null ? null : new NodeId(votedFor),
            new Term(localLastLogTerm),
            new LogIndex(localLastLogIndex));

        // Assert
        decision.Should().Be(expected);
    }

    [Fact]
    public void Decide_WithAnEmptyCandidateLogAndAnEmptyLocalLog_GrantsTheVote()
    {
        // Arrange: два узла без журнала - это первый запуск кластера, правило up-to-date выполняется.
        var request = new RequestVoteRequest(Candidate, new Term(1), LogIndex.Empty, new Term(0));

        // Act
        var decision = RequestVoteRules.Decide(request, new Term(1), null, new Term(0), LogIndex.Empty);

        // Assert
        decision.Should().Be(VoteDecision.Grant);
    }

    [Theory]
    [InlineData(5, 3, 5, 3, true)]
    [InlineData(5, 3, 5, 2, true)]
    [InlineData(5, 3, 5, 4, false)]
    [InlineData(5, 3, 6, 1, true)]
    [InlineData(5, 3, 4, 100, false)]
    [InlineData(0, 0, 1, 0, false)]
    [InlineData(0, 0, 0, 0, true)]
    public void IsUpToDate_ComparesThePairTermThenIndex(
        long candidateIndex,
        long candidateTerm,
        long localIndex,
        long localTerm,
        bool expected)
    {
        RequestVoteRules.IsUpToDate(
                new LogIndex(candidateIndex),
                new Term(candidateTerm),
                new LogIndex(localIndex),
                new Term(localTerm))
            .Should().Be(expected);
    }
}
