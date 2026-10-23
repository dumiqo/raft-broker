using RaftBroker.Core.Randomness;

namespace RaftBroker.UnitTests.Randomness;

/// <summary>
/// Рантайм-источник случайности: у него нет воспроизводимости, но контракт диапазонов
/// <see cref="IRandomSource"/> он обязан соблюдать так же, как сидированный.
/// </summary>
/// <remarks>
/// Проверяется именно контракт, а не значения: <see cref="Random.Shared"/> не даёт
/// воспроизводимой последовательности, и подделывать её здесь значило бы тестировать
/// платформу. Проверка диапазона ловит реальную ошибку - перепутанные границы
/// (<c>Next(min, max)</c> включает минимум и исключает максимум).
/// </remarks>
public sealed class SystemRandomSourceTests
{
    [Fact]
    public void Next_StaysWithinTheHalfOpenRange()
    {
        var source = SystemRandomSource.Instance;

        var values = Enumerable.Range(0, 1000).Select(_ => source.Next(3, 7)).ToArray();

        values.Should().OnlyContain(value => value >= 3 && value < 7);
    }

    [Fact]
    public void NextDouble_StaysWithinTheUnitInterval()
    {
        var source = SystemRandomSource.Instance;

        var values = Enumerable.Range(0, 1000).Select(_ => source.NextDouble()).ToArray();

        values.Should().OnlyContain(value => value >= 0.0 && value < 1.0);
    }

    [Fact]
    public void Instance_IsSharedBecauseRandomSharedIsThreadSafe()
    {
        // Экземпляр-синглтон: таймауты выборов не должны создавать новый Random на каждый
        // вызов, иначе два узла с одинаковым зерном времени разошлись бы предсказуемо.
        SystemRandomSource.Instance.Should().BeSameAs(SystemRandomSource.Instance);
    }
}
