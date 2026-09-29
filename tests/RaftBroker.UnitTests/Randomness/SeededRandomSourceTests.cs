using RaftBroker.Core.Randomness;

namespace RaftBroker.UnitTests.Randomness;

/// <summary>
/// S0-T04: сидированный источник случайности обязан быть воспроизводимым - от него
/// зависит воспроизводимость выборов в тестах S2 и прогонов симулятора S8.
/// </summary>
public sealed class SeededRandomSourceTests
{
    [Fact]
    public void SameSeed_ProducesTheSameSequence()
    {
        var first = new SeededRandomSource(seed: 12345);
        var second = new SeededRandomSource(seed: 12345);

        var firstSequence = Enumerable.Range(0, 100).Select(_ => first.Next(0, 1000)).ToArray();
        var secondSequence = Enumerable.Range(0, 100).Select(_ => second.Next(0, 1000)).ToArray();

        secondSequence.Should().Equal(firstSequence);
    }

    [Fact]
    public void SameSeed_IsReproducibleAcrossRunsOfTheProcess()
    {
        // Значения зафиксированы вручную: тест ловит не "случайность", а незаметную смену
        // алгоритма. Если он упал, это значит, что воспроизвести старые прогоны больше нельзя.
        var source = new SeededRandomSource(seed: 42);

        var actual = Enumerable.Range(0, 8).Select(_ => source.Next(0, 1000)).ToArray();

        actual.Should().Equal(794, 163, 130, 456, 530, 383, 231, 754);
    }

    [Fact]
    public void DifferentSeeds_ProduceDifferentSequences()
    {
        var first = new SeededRandomSource(seed: 1);
        var second = new SeededRandomSource(seed: 2);

        var firstSequence = Enumerable.Range(0, 50).Select(_ => first.Next(0, 1_000_000)).ToArray();
        var secondSequence = Enumerable.Range(0, 50).Select(_ => second.Next(0, 1_000_000)).ToArray();

        secondSequence.Should().NotEqual(firstSequence);
    }

    [Fact]
    public void ZeroSeed_IsAllowedAndStillHitsTheWholeRange()
    {
        var source = new SeededRandomSource(seed: 0);

        var values = Enumerable.Range(0, 1000).Select(_ => source.Next(0, 10)).ToArray();

        values.Distinct().Should().HaveCount(10, "нулевое зерно не должно вырождать генератор в константу");
    }

    [Fact]
    public void Next_NeverLeavesTheRequestedRange()
    {
        var source = new SeededRandomSource(seed: 7);

        var values = Enumerable.Range(0, 20_000).Select(_ => source.Next(150, 300)).ToArray();

        values.Should().OnlyContain(value => value >= 150 && value < 300);
        values.Distinct().Count().Should().Be(150, "все значения диапазона достижимы");
    }

    [Fact]
    public void Next_DistributesOverTheRangeWithoutVisibleBias()
    {
        var source = new SeededRandomSource(seed: 2026);
        var buckets = new int[10];

        for (var i = 0; i < 100_000; i++)
        {
            buckets[source.Next(0, 10)]++;
        }

        // Ожидание - 10 000 на ведро. Широкий коридор 8 000...12 000 ловит настоящую
        // неисправность (залипание, смещение остатка), не зависая на статистике.
        buckets.Should().OnlyContain(count => count > 8_000 && count < 12_000);
    }

    [Fact]
    public void Next_HandlesTheFullIntRangeWithoutOverflow()
    {
        var source = new SeededRandomSource(seed: 99);

        var values = Enumerable.Range(0, 10_000).Select(_ => source.Next(int.MinValue, int.MaxValue)).ToArray();

        values.Should().OnlyContain(value => value >= int.MinValue && value < int.MaxValue);
        values.Distinct().Count().Should().BeGreaterThan(9_000);
    }

    [Theory]
    [InlineData(5, 5)]
    [InlineData(5, 4)]
    [InlineData(int.MaxValue, int.MinValue)]
    public void Next_WithEmptyOrInvertedRange_IsRejected(int minInclusive, int maxExclusive)
    {
        var source = new SeededRandomSource(seed: 1);

        var act = () => source.Next(minInclusive, maxExclusive);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void NextDouble_StaysInUnitInterval_AndIsNotConstant()
    {
        var source = new SeededRandomSource(seed: 3);

        var values = Enumerable.Range(0, 10_000).Select(_ => source.NextDouble()).ToArray();

        values.Should().OnlyContain(value => value >= 0.0 && value < 1.0);
        values.Distinct().Count().Should().BeGreaterThan(9_000);
        values.Max().Should().BeGreaterThan(0.9);
        values.Min().Should().BeLessThan(0.1);
    }
}
