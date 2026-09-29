namespace RaftBroker.Core.Time;

/// <summary>
/// Единый источник времени. Прямое обращение к настенным часам в бизнес-коде запрещено:
/// и таймауты выборов, и метки времени должны быть подменяемы, иначе тесты S2 и S8
/// будут ждать реального времени и станут недетерминированными.
/// </summary>
/// <remarks>
/// Реализации: <see cref="SystemClock"/> в рантайме, <c>TestClock</c> из проекта
/// <c>RaftBroker.TestKit</c> в тестах и симуляторе. Значение <see cref="TimestampMs"/> монотонно: перевод системных часов
/// назад не должен ломать логику таймаутов.
/// </remarks>
public interface IClock
{
    /// <summary>Логическое "сейчас" в миллисекундах. Монотонно и не убывает.</summary>
    long TimestampMs { get; }

    /// <summary>
    /// Ставит одноразовый таймер: <paramref name="callback"/> вызывается один раз
    /// после <paramref name="delay"/>. Возвращённый <see cref="ITimer"/> отменяет его.
    /// </summary>
    /// <param name="delay">Неотрицательная задержка.</param>
    /// <param name="callback">Что вызвать. Исключение из колбэка - забота вызывающего.</param>
    ITimer Schedule(TimeSpan delay, Action callback);
}
