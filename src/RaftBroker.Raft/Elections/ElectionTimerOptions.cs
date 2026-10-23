namespace RaftBroker.Raft.Elections;

/// <summary>
/// Параметры таймера выборов (<c>S2-T03</c>): базовый срок <c>T</c>.
/// </summary>
/// <remarks>
/// <para>
/// Срок рандомизируется в <c>[T, 2T)</c> при каждом <see cref="ElectionTimer.Reset"/>. Нижняя
/// граница - сам <c>T</c>: таймер не должен срабатывать раньше, чем лидер успевает отправить
/// heartbeat, иначе узел уходит в выборы на каждом пакете сети.
/// </para>
/// <para>
/// Значение по умолчанию (<see cref="Default"/>) - предложение спецификации этапа
/// (150-300 мс). Это открытое решение этапа: срок конфигурируем и не зашит в код таймера,
/// а владелец фиксирует финальное значение вместе с окном stickiness (<c>S2-T09</c>).
/// </para>
/// </remarks>
public sealed class ElectionTimerOptions
{
    /// <summary>Создаёт параметры таймера выборов.</summary>
    /// <param name="timeout">Базовый срок <c>T</c>; целые миллисекунды, от 1 до <see cref="int.MaxValue"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">Срок не влезает в допустимый диапазон миллисекунд.</exception>
    public ElectionTimerOptions(TimeSpan timeout)
    {
        var milliseconds = (long)timeout.TotalMilliseconds;

        if (milliseconds < 1 || milliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                $"Базовый срок таймера выборов задаётся целыми миллисекундами в [1, {int.MaxValue}], получено {timeout}.");
        }

        Timeout = timeout;
    }

    /// <summary>Базовый срок <c>T</c>.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>Предложение спецификации этапа: 200 мс.</summary>
    public static ElectionTimerOptions Default { get; } = new(TimeSpan.FromMilliseconds(200));
}
