using System.Text.Json;

namespace RaftBroker.UnitTests.Primitives;

/// <summary>
/// Общие проверки JSON-представления примитивов S0-T05: голое число или строка,
/// а не объект с полем <c>value</c>.
/// </summary>
internal static class JsonPrimitiveAssertions
{
    internal static void RoundTrips<T>(T value, string expectedJson)
        where T : notnull
    {
        var json = JsonSerializer.Serialize(value);

        json.Should().Be(expectedJson);
        JsonSerializer.Deserialize<T>(json).Should().Be(value);
    }

    internal static void ReadingFails<T>(string json, string expectedReason)
        where T : notnull
    {
        var act = () => JsonSerializer.Deserialize<T>(json);

        act.Should().Throw<JsonException>().WithMessage($"*{expectedReason}*");
    }
}
