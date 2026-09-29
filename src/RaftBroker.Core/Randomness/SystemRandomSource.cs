namespace RaftBroker.Core.Randomness;

/// <summary>
/// Рантайм-реализация <see cref="IRandomSource"/> поверх <see cref="Random.Shared"/>.
/// Значения невоспроизводимы - это то, что нужно в бою и не нужно в тестах.
/// </summary>
public sealed class SystemRandomSource : IRandomSource
{
    /// <summary>Общий экземпляр: <see cref="Random.Shared"/> уже потокобезопасен.</summary>
    public static SystemRandomSource Instance { get; } = new();

    /// <inheritdoc />
    public int Next(int minInclusive, int maxExclusive) => Random.Shared.Next(minInclusive, maxExclusive);

    /// <inheritdoc />
    public double NextDouble() => Random.Shared.NextDouble();
}
