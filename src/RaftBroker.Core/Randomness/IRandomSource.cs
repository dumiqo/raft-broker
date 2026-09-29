namespace RaftBroker.Core.Randomness;

/// <summary>
/// Единый источник случайности. Нужен для рандомизации таймаутов выборов (S2-T03) и для
/// перемешивания порядка операций в симуляторе (S8-T01); прямой <see cref="Random"/> в
/// бизнес-коде запрещён, иначе прогон нельзя воспроизвести.
/// </summary>
public interface IRandomSource
{
    /// <summary>Случайное целое в диапазоне <c>[minInclusive, maxExclusive)</c>, распределённое равномерно.</summary>
    int Next(int minInclusive, int maxExclusive);

    /// <summary>Случайное число в диапазоне <c>[0.0, 1.0)</c>.</summary>
    double NextDouble();
}
