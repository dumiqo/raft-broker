using RaftBroker.Core.Primitives;
using RaftBroker.Raft;

namespace RaftBroker.UnitTests.Raft;

/// <summary>
/// Статическая конфигурация (<c>S2-T05</c>): большинство, чужие узлы и повторы.
/// </summary>
public sealed class StaticClusterConfigurationTests
{
    private static readonly NodeId A = new("node-1");
    private static readonly NodeId B = new("node-2");
    private static readonly NodeId C = new("node-3");

    [Fact]
    public void Voters_AreDeduplicatedAndOrdered()
    {
        var configuration = new StaticClusterConfiguration([C, A, B, A]);

        configuration.Voters.Should().Equal([A, B, C]);
    }

    [Fact]
    public void Constructor_WithoutVoters_Throws()
    {
        var act = () => new StaticClusterConfiguration([]);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(new[] { 0 }, false)]
    [InlineData(new[] { 1 }, false)]
    [InlineData(new[] { 0, 1 }, true)]
    [InlineData(new[] { 0, 1, 2 }, true)]
    public void IsQuorum_ForThreeVoters_NeedsTwo(int[] acknowledgers, bool expected)
    {
        var configuration = new StaticClusterConfiguration([A, B, C]);
        var nodes = acknowledgers.Select(index => new[] { A, B, C }[index]).ToArray();

        configuration.IsQuorum(nodes).Should().Be(expected);
    }

    [Fact]
    public void IsQuorum_IgnoresNodesOutsideTheConfigurationAndDuplicates()
    {
        var configuration = new StaticClusterConfiguration([A, B, C]);

        configuration.IsQuorum([A, A, new NodeId("outsider")]).Should().BeFalse();
    }

    [Fact]
    public void IsQuorum_ForASingleVoter_IsItsOwnAnswer()
    {
        var configuration = new StaticClusterConfiguration([A]);

        configuration.IsQuorum([A]).Should().BeTrue();
        configuration.IsQuorum([]).Should().BeFalse();
    }
}
