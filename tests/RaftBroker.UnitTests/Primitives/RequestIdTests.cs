using RaftBroker.Core.Primitives;

namespace RaftBroker.UnitTests.Primitives;

/// <summary>
/// S0-T05: идентификатор запроса. Значение приходит от клиента, поэтому ограничений на него
/// нет, кроме размера: важен только полный диапазон <see cref="ulong"/> без потери точности.
/// </summary>
public sealed class RequestIdTests
{
    [Fact]
    public void AcceptsTheWholeUnsignedRange()
    {
        new RequestId(0).Value.Should().Be(0);
        new RequestId(ulong.MaxValue).Value.Should().Be(ulong.MaxValue);
    }

    [Fact]
    public void Comparison_UsesTheNumericOrder()
    {
        (new RequestId(1) < new RequestId(2)).Should().BeTrue();
        (new RequestId(ulong.MaxValue) > new RequestId(0)).Should().BeTrue();
    }

    [Fact]
    public void ValueEquality_WorksAsForAValueType()
    {
        new RequestId(7).Should().Be(new RequestId(7));
        new RequestId(7).Should().NotBe(new RequestId(8));
    }

    [Fact]
    public void ToString_IgnoresTheCurrentCulture()
    {
        using var culture = CultureScope.Russian();

        new RequestId(1234).ToString().Should().Be("1234");
    }

    [Fact]
    public void WritesAndReadsAsPlainNumber()
    {
        JsonPrimitiveAssertions.RoundTrips(new RequestId(18446744073709551615UL), "18446744073709551615");
    }

    [Theory]
    [InlineData("-1", "неотрицательное целое")]
    [InlineData("1.5", "неотрицательное целое")]
    [InlineData("\"7\"", "неотрицательное целое")]
    public void RejectsUnusableJsonWithAnActionableMessage(string json, string reason)
    {
        JsonPrimitiveAssertions.ReadingFails<RequestId>(json, reason);
    }
}
