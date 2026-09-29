using RaftBroker.Core.Primitives;

namespace RaftBroker.UnitTests.Primitives;

/// <summary>
/// S0-T05: терм - счётчик эпох. Его нельзя сделать отрицательным, он растёт на единицу
/// и переполняется громко, а не в отрицательное значение.
/// </summary>
public sealed class TermTests
{
    [Fact]
    public void StartsAtZeroAndRejectsNegativeValues()
    {
        new Term(0).Value.Should().Be(0);

        var act = () => new Term(-1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Next_IncrementsByOne()
    {
        new Term(4).Next().Should().Be(new Term(5));
    }

    [Fact]
    public void Next_AtTheMaximum_ThrowsInsteadOfWrappingAround()
    {
        var act = () => new Term(long.MaxValue).Next();

        act.Should().Throw<OverflowException>();
    }

    [Fact]
    public void Comparison_UsesTheNumericOrder()
    {
        var older = new Term(3);
        var newer = new Term(7);

        (older < newer).Should().BeTrue();
        (newer > older).Should().BeTrue();
        (older <= new Term(3)).Should().BeTrue();
        (older >= new Term(3)).Should().BeTrue();
        older.CompareTo(newer).Should().BeNegative();
    }

    [Fact]
    public void ValueEquality_WorksAsForAValueType()
    {
        new Term(5).Should().Be(new Term(5));
        new Term(5).Should().NotBe(new Term(6));
    }

    [Fact]
    public void ToString_IgnoresTheCurrentCulture()
    {
        using var culture = CultureScope.Russian();

        new Term(1234).ToString().Should().Be("1234", "разделитель разрядов в терме - это ошибка чтения лога");
    }

    [Fact]
    public void WritesAndReadsAsPlainNumber()
    {
        JsonPrimitiveAssertions.RoundTrips(new Term(42), "42");
    }

    [Theory]
    [InlineData("-1", "не может быть отрицательным")]
    [InlineData("1.5", "ожидается целое число")]
    [InlineData("\"5\"", "ожидается целое число")]
    [InlineData("null", "ожидается целое число")]
    public void RejectsUnusableJsonWithAnActionableMessage(string json, string reason)
    {
        JsonPrimitiveAssertions.ReadingFails<Term>(json, reason);
    }
}
