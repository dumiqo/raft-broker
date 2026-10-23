using RaftBroker.Core.Primitives;

namespace RaftBroker.Raft;

/// <summary>
/// Статическая конфигурация кластера (<c>S2-T05</c>): фиксированный список избирателей, кворум - большинство.
/// </summary>
/// <remarks>
/// Заменяет на этапе S2 применяемую конфигурацию из журнала: менять состав умеет только
/// joint consensus (<c>S7-T02</c>), а до него конфигурация одна и та же на всё время жизни узла.
/// </remarks>
public sealed class StaticClusterConfiguration : IClusterConfiguration
{
    private readonly HashSet<NodeId> _voters;

    /// <summary>Создаёт конфигурацию из списка избирателей.</summary>
    /// <param name="voters">Узлы кластера; порядок не важен, повторы отбрасываются.</param>
    /// <exception cref="ArgumentException">Список пуст.</exception>
    public StaticClusterConfiguration(IEnumerable<NodeId> voters)
    {
        ArgumentNullException.ThrowIfNull(voters);

        _voters = [.. voters];

        if (_voters.Count == 0)
        {
            throw new ArgumentException("Конфигурация без избирателей не бывает.", nameof(voters));
        }

        Voters = [.. _voters.Order()];
    }

    /// <inheritdoc />
    public IReadOnlyCollection<NodeId> Voters { get; }

    /// <inheritdoc />
    /// <remarks>Чужие узлы не считаются: «ответил кто-то» и «ответил член кворума» - разные вещи.</remarks>
    public bool IsQuorum(IReadOnlyCollection<NodeId> acknowledgers)
    {
        ArgumentNullException.ThrowIfNull(acknowledgers);

        var votes = acknowledgers.Where(_voters.Contains).Distinct().Count();

        return votes >= (_voters.Count / 2) + 1;
    }
}
