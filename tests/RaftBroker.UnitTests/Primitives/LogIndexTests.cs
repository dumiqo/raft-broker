using RaftBroker.Core.Primitives;

namespace RaftBroker.UnitTests.Primitives;

/// <summary>
/// S0-T05: индекс журнала. Ноль - законное значение (пустой журнал), отрицательные - нет.
/// </summary>
public sealed class LogIndexTests
{
    [Fact]
    public void ZeroMeansEmptyLog_AndNegativeIsRejected()
    {
        LogIndex.Empty.Value.Should().Be(0);
        default(LogIndex).Should().Be(LogIndex.Empty);

        var act = () => new LogIndex(-1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Next_AdvancesToTheFollowingEntry()
    {
        new LogIndex(0).Next().Should().Be(new LogIndex(1));
        new LogIndex(10).Next().Value.Should().Be(11);
    }

    [Fact]
    public void Next_AtTheMaximum_ThrowsInsteadOfWrappingAround()
    {
        var act = () => new LogIndex(long.MaxValue).Next();

        act.Should().Throw<OverflowException>();
    }

    [Fact]
    public void Comparison_UsesTheNumericOrder()
    {
        (new LogIndex(1) < new LogIndex(2)).Should().BeTrue();
        (new LogIndex(2) > new LogIndex(1)).Should().BeTrue();
    }

    [Fact]
    public void ToString_IgnoresTheCurrentCulture()
    {
        using var culture = CultureScope.Russian();

        new LogIndex(1234).ToString().Should().Be("1234");
    }

    [Fact]
    public void WritesAndReadsAsPlainNumber()
    {
        JsonPrimitiveAssertions.RoundTrips(new LogIndex(7), "7");
    }

    [Theory]
    [InlineData("-2", "не может быть отрицательным")]
    [InlineData("[]", "ожидается целое число")]
    public void RejectsUnusableJsonWithAnActionableMessage(string json, string reason)
    {
        JsonPrimitiveAssertions.ReadingFails<LogIndex>(json, reason);
    }
}
