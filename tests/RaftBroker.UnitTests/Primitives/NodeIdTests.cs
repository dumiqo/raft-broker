using RaftBroker.Core.Primitives;

namespace RaftBroker.UnitTests.Primitives;

/// <summary>
/// S0-T05: идентификатор узла должен быть пригоден для конфига, лога и метаданных gRPC,
/// то есть отвергать мусор сразу, а не превращать его в другой идентификатор.
/// </summary>
public sealed class NodeIdTests
{
    [Theory]
    [InlineData("node-1")]
    [InlineData("a")]
    [InlineData("NODE-1")]
    [InlineData("узел-1")]
    public void AcceptsReadableIdentifiers(string raw)
    {
        var nodeId = new NodeId(raw);

        nodeId.Value.Should().Be(raw);
        nodeId.ToString().Should().Be(raw);
    }

    [Fact]
    public void AcceptsTheMaximumLength()
    {
        var raw = new string('x', 128);

        new NodeId(raw).Value.Should().HaveLength(128);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" node-1")]
    [InlineData("node-1 ")]
    [InlineData("node 1")]
    [InlineData("node\t1")]
    [InlineData("node\n1")]
    public void RejectsEmptyOrWhitespaceDirtyIdentifiers(string raw)
    {
        var act = () => new NodeId(raw);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void RejectsIdentifiersLongerThan128Characters()
    {
        var raw = new string('x', 129);

        var act = () => new NodeId(raw);

        act.Should().Throw<ArgumentException>().WithMessage("*128*");
    }

    [Fact]
    public void DefaultValue_IsRejectedLoudlyInsteadOfReturningNull()
    {
        // default(NodeId) существует как значение любого struct, и он нарушает инвариант
        // "Value никогда не null". Поэтому геттер падает, а не отдаёт null в лог и на провод.
        var act = () => default(NodeId).Value;

        act.Should().Throw<InvalidOperationException>().WithMessage("*default(NodeId)*");
    }

    [Fact]
    public void ValueEquality_WorksAsForAValueType()
    {
        var nodeId = new NodeId("node-1");

        nodeId.Should().Be(new NodeId("node-1"));
        nodeId.GetHashCode().Should().Be(new NodeId("node-1").GetHashCode());
        nodeId.Should().NotBe(new NodeId("node-2"));
    }

    [Fact]
    public void Comparison_IsOrdinalSoThatLogsAndTestsAreDeterministic()
    {
        var ten = new NodeId("node-10");
        var two = new NodeId("node-2");

        // Порядковое сравнение сравнивает символы, а не числа: '1' меньше '2', поэтому
        // "node-10" идёт раньше "node-2". Это ровно то, что нужно для детерминированной
        // сортировки, и именно поэтому порядок узлов в Raft не должен зависеть от имен.
        (ten < two).Should().BeTrue();
        (two > ten).Should().BeTrue();
        ten.CompareTo(two).Should().BeNegative();
    }

    [Fact]
    public void WritesAndReadsAsPlainString()
    {
        JsonPrimitiveAssertions.RoundTrips(new NodeId("node-1"), "\"node-1\"");
    }

    [Theory]
    [InlineData("\"\"", "Идентификатор не может быть пустым")]
    [InlineData("\" node-1\"", "пробелы")]
    [InlineData("42", "ожидается строка")]
    [InlineData("null", "ожидается строка")]
    public void RejectsUnusableJsonWithAnActionableMessage(string json, string reason)
    {
        JsonPrimitiveAssertions.ReadingFails<NodeId>(json, reason);
    }
}
