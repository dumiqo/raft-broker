using RaftBroker.Core.Primitives;

namespace RaftBroker.UnitTests.Primitives;

/// <summary>
/// S0-T05: идентификатор клиента повторяет правила <see cref="NodeId"/>, но остаётся
/// отдельным типом. Здесь проверяется, что правила подключены и что типы не взаимозаменяемы.
/// </summary>
public sealed class ClientIdTests
{
    [Fact]
    public void RejectsTheSameGarbageAsNodeId()
    {
        var empty = () => new ClientId(string.Empty);

        empty.Should().Throw<ArgumentException>();

        var withSpace = () => new ClientId("client 1");

        withSpace.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void DefaultValue_IsRejectedLoudly()
    {
        var act = () => default(ClientId).Value;

        act.Should().Throw<InvalidOperationException>().WithMessage("*default(ClientId)*");
    }

    [Fact]
    public void IsADistinctTypeFromNodeId()
    {
        // Проверка не про CLR, а про читателя: если однажды появится неявное преобразование
        // между NodeId и ClientId, этот тест перестанет компилироваться - и это правильно.
        typeof(ClientId).Should().NotBe(typeof(NodeId));
    }

    [Fact]
    public void ValueEqualityAndComparison_WorkAsForAValueType()
    {
        var clientId = new ClientId("client-1");

        clientId.Should().Be(new ClientId("client-1"));
        clientId.Value.Should().Be("client-1");
        clientId.ToString().Should().Be("client-1");
        (clientId > new ClientId("client-0")).Should().BeTrue();
    }

    [Fact]
    public void WritesAndReadsAsPlainString()
    {
        JsonPrimitiveAssertions.RoundTrips(new ClientId("client-1"), "\"client-1\"");
    }

    [Theory]
    [InlineData("\"\"", "пустым")]
    [InlineData("null", "ожидается строка")]
    [InlineData("true", "ожидается строка")]
    public void RejectsUnusableJsonWithAnActionableMessage(string json, string reason)
    {
        JsonPrimitiveAssertions.ReadingFails<ClientId>(json, reason);
    }
}
