namespace RaftBroker.Core.Randomness;

/// <summary>
/// Детерминированный <see cref="IRandomSource"/>: одинаковый seed даёт одинаковую
/// последовательность значений на любой машине и в любой версии рантайма.
/// </summary>
/// <remarks>
/// <para>
/// Используется алгоритм xorshift64* (Marsaglia, 2003). Свой генератор, а не
/// <c>new Random(seed)</c>, именно из-за требования воспроизводимости:
/// алгоритм <see cref="Random"/> с seed не является частью контракта рантайма и может
/// измениться между версиями .NET, а тогда воспроизвести прогон симулятора S8 станет
/// невозможно. Здесь последовательность зафиксирована тестом.
/// </para>
/// <para>
/// Класс не потокобезопасен: воспроизводимость и параллельный доступ несовместимы.
/// В симуляторе это и не нужно - там всё идёт в одном потоке по шагам.
/// </para>
/// </remarks>
public sealed class SeededRandomSource : IRandomSource
{
    /// <summary>Ненулевое значение по умолчанию: xorshift не работает на нулевом состоянии.</summary>
    private const ulong DefaultState = 0x9E3779B97F4A7C15UL;

    private ulong _state;

    /// <summary>Создаёт генератор с заданным зерном. Нулевое зерно заменяется на константу.</summary>
    public SeededRandomSource(ulong seed) => _state = seed == 0 ? DefaultState : seed;

    /// <inheritdoc />
    public int Next(int minInclusive, int maxExclusive)
    {
        if (minInclusive >= maxExclusive)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxExclusive),
                maxExclusive,
                $"Верхняя граница ({maxExclusive}) должна быть больше нижней ({minInclusive}).");
        }

        var range = (ulong)((long)maxExclusive - minInclusive);

        // Отбрасывание хвоста убирает смещение остатка: значения, попавшие в неполное
        // последнее "ведро", не используются. Для диапазонов вида 150-300 это в среднем
        // меньше двух итераций.
        var limit = ulong.MaxValue - (ulong.MaxValue % range);
        ulong sample;
        do
        {
            sample = NextUInt64();
        }
        while (sample >= limit);

        return (int)(minInclusive + (long)(sample % range));
    }

    /// <inheritdoc />
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);

    private ulong NextUInt64()
    {
        var x = _state;
        x ^= x << 13;
        x ^= x >> 7;
        x ^= x << 17;
        _state = x;

        return x * 0x2545F4914F6CDD1DUL;
    }
}
