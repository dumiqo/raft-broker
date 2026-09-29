using RaftBroker.Core.Primitives;

namespace RaftBroker.UnitTests.Primitives;

/// <summary>
/// S0-T05: смещение в потоке состояния. Отдельный тип от <see cref="LogIndex"/>,
/// хотя правила те же: перепутать позицию в журнале Raft и позицию в потоке приложения
/// - это ошибка, которую должен ловить компилятор.
/// </summary>
public sealed class OffsetTests
{
    [Fact]
    public void BeginningIsZero_AndNegativeIsRejected()
    {
        Offset.Beginning.Value.Should().Be(0);
        default(Offset).Should().Be(Offset.Beginning);

        var act = () => new Offset(-1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Next_AdvancesByOneEntry()
    {
        new Offset(3).Next().Should().Be(new Offset(4));
    }

    [Fact]
    public void Next_AtTheMaximum_ThrowsInsteadOfWrappingAround()
    {
        var act = () => new Offset(long.MaxValue).Next();

        act.Should().Throw<OverflowException>();
    }

    [Fact]
    public void Comparison_UsesTheNumericOrder()
    {
        (Offset.Beginning < new Offset(1)).Should().BeTrue();
        (new Offset(1) > Offset.Beginning).Should().BeTrue();
    }

    [Fact]
    public void ToString_IgnoresTheCurrentCulture()
    {
        using var culture = CultureScope.Russian();

        new Offset(1234).ToString().Should().Be("1234");
    }

    [Fact]
    public void WritesAndReadsAsPlainNumber()
    {
        JsonPrimitiveAssertions.RoundTrips(new Offset(4096), "4096");
    }

    [Theory]
    [InlineData("-1", "не может быть отрицательным")]
    [InlineData("{}", "ожидается целое число")]
    public void RejectsUnusableJsonWithAnActionableMessage(string json, string reason)
    {
        JsonPrimitiveAssertions.ReadingFails<Offset>(json, reason);
    }

    [Fact]
    public void IsADistinctTypeFromLogIndex()
    {
        typeof(Offset).Should().NotBe(typeof(LogIndex));
    }
}
