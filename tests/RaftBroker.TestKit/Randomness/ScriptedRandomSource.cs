using RaftBroker.Core.Randomness;

namespace RaftBroker.TestKit.Randomness;

/// <summary>
/// Источник случайности с заданной последовательностью значений (<c>S2-T03</c>).
/// </summary>
/// <remarks>
/// Нужен там, где тест проверяет не распределение, а конкретную ветку: «срок оказался ровно
/// <c>T</c>» или «ровно <c>2T - 1</c>». Значения выдаются по порядку и зажимаются в запрошенный
/// диапазон, поэтому тест говорит, что вернуть, не повторяя арифметику вызывающего кода.
/// Кончились значения - это ошибка теста, а не тихий ноль: иначе проверка границы молча
/// превратилась бы в проверку середины.
/// </remarks>
public sealed class ScriptedRandomSource : IRandomSource
{
    private readonly Queue<int> _values;

    /// <summary>Создаёт источник из последовательности значений.</summary>
    /// <param name="values">Значения по порядку; каждое зажимается в <c>[min, max)</c>.</param>
    public ScriptedRandomSource(params int[] values)
    {
        _values = new Queue<int>(values);
    }

    /// <summary>Сколько значений ещё не выдано.</summary>
    public int Remaining => _values.Count;

    /// <inheritdoc />
    public int Next(int minInclusive, int maxExclusive)
    {
        if (!_values.TryDequeue(out var value))
        {
            throw new InvalidOperationException("Скрипт случайности исчерпан: тесту не хватило значений.");
        }

        return Math.Clamp(value, minInclusive, maxExclusive - 1);
    }

    /// <inheritdoc />
    public double NextDouble() => 0.5;
}
