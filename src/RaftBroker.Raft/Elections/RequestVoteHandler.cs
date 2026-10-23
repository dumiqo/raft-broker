using RaftBroker.Core.Primitives;
using RaftBroker.Raft.Messages;
using RaftBroker.Raft.State;

namespace RaftBroker.Raft.Elections;

/// <summary>
/// Обработка <c>RequestVote</c> (<c>S2-T04</c>): решение, запись hard state, ответ.
/// </summary>
/// <remarks>
/// <para>
/// Обработчик - это <see cref="RequestVoteRules.Decide"/> плюс побочные эффекты. Порядок жёсткий:
/// сначала <see cref="RaftState.PersistAsync"/> (fsync), потом смена роли и только потом ответ.
/// Узел, ответивший «голос дан» до записи, после краха голосует второй раз в том же term, и в
/// кластере появляются два лидера в одном term (R6).
/// </para>
/// <para>
/// Счётчика голосов здесь нет: считает кворум кандидат (<c>S2-T05</c>), причём через внедрённый
/// источник конфигурации, а не по числу пиров (правило 7 спецификации этапа).
/// </para>
/// </remarks>
public static class RequestVoteHandler
{
    /// <summary>Обрабатывает запрос голоса.</summary>
    /// <param name="request">Запрос кандидата.</param>
    /// <param name="state">Состояние узла: term, голос и роль меняются здесь.</param>
    /// <param name="lastLogTerm">Term последней записи локального журнала; 0 - журнал пуст.</param>
    /// <param name="lastLogIndex">Индекс последней записи локального журнала; 0 - журнал пуст.</param>
    /// <param name="cancellationToken">Отмена: прерывание до fsync означает, что голос не считается данным.</param>
    /// <returns>Ответ кандидату; term в нём - текущий term узла после обработки.</returns>
    public static async ValueTask<RequestVoteResponse> HandleAsync(
        RequestVoteRequest request,
        RaftState state,
        Term lastLogTerm,
        LogIndex lastLogIndex,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);

        switch (RequestVoteRules.Decide(request, state.CurrentTerm, state.VotedFor, lastLogTerm, lastLogIndex))
        {
            case VoteDecision.Grant:
                await state.PersistAsync(state.CurrentTerm, request.Candidate, cancellationToken).ConfigureAwait(false);

                return new RequestVoteResponse(state.CurrentTerm, VoteGranted: true);

            case VoteDecision.StepDownAndGrant:
                await state.PersistAsync(request.Term, request.Candidate, cancellationToken).ConfigureAwait(false);
                BecomeFollower(state);

                return new RequestVoteResponse(request.Term, VoteGranted: true);

            case VoteDecision.StepDownAndDeny:
                await state.PersistAsync(request.Term, votedFor: null, cancellationToken).ConfigureAwait(false);
                BecomeFollower(state);

                return new RequestVoteResponse(request.Term, VoteGranted: false);

            default:
                // Меньший или чужой term: состояние не меняется, диск не трогаем вовсе.
                return new RequestVoteResponse(state.CurrentTerm, VoteGranted: false);
        }
    }

    private static void BecomeFollower(RaftState state)
    {
        state.Role = RaftRole.Follower;
        state.LeaderId = null;
    }
}
