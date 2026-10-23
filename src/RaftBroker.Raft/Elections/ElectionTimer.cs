using RaftBroker.Core.Randomness;
using RaftBroker.Core.Time;
using ITimer = RaftBroker.Core.Time.ITimer;

namespace RaftBroker.Raft.Elections;

/// <summary>
/// Таймер выборов с рандомизацией срока в <c>[T, 2T)</c> (<c>S2-T03</c>).
/// </summary>
/// <remarks>
/// <para>
/// Узлы с одинаковым сроком становятся кандидатами одновременно, делят голоса и уходят в
/// следующий раунд вместе - кластер не выбирает лидера. Разброс рвёт симметрию за один раунд,
/// поэтому срок берётся из <see cref="IRandomSource"/> (воспроизводимо по сиду), а не из
/// <see cref="Random"/>.
/// </para>
/// <para>
/// Таймер ничего не делает сам: он только зовёт колбэк, а колбэк лишь сигналит узлу о событии.
/// Никакой асинхронной работы и блокировок внутри: <see cref="ITimer"/> зовёт его из своего
/// потока, а переходы ролей идут тем же путём, что и обработка RPC (<c>S2-T08</c>).
/// </para>
/// <para>
/// <see cref="Reset"/> - это отмена текущего таймера плюс назначение нового: у <see cref="ITimer"/>
/// нет <c>Change</c>, и сдвиг срока делается только так. Отменённый таймер не срабатывает,
/// поэтому старый дедлайн не может «выстрелить» после продления.
/// </para>
/// </remarks>
public sealed class ElectionTimer : IDisposable
{
    private readonly IClock _clock;
    private readonly IRandomSource _random;
    private readonly Action _onTimeout;
    private readonly int _timeoutMs;

    private ITimer? _timer;
    private bool _disposed;

    /// <summary>Создаёт таймер выборов.</summary>
    /// <param name="clock">Источник времени: виртуальные часы в тестах, системные в бою.</param>
    /// <param name="random">Источник разброса срока.</param>
    /// <param name="options">Базовый срок <c>T</c>.</param>
    /// <param name="onTimeout">Что делать по срабатыванию: только сигнал, без асинхронной работы.</param>
    public ElectionTimer(IClock clock, IRandomSource random, ElectionTimerOptions options, Action onTimeout)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(random);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(onTimeout);

        _clock = clock;
        _random = random;
        _onTimeout = onTimeout;
        _timeoutMs = (int)(long)options.Timeout.TotalMilliseconds;
    }

    /// <summary>Идёт ли сейчас отсчёт до выборов.</summary>
    public bool IsActive => _timer?.IsActive ?? false;

    /// <summary>Запускает отсчёт заново со свежим случайным сроком <c>T + [0, T)</c>.</summary>
    /// <exception cref="ObjectDisposedException">Таймер уже освобождён.</exception>
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        CancelCurrent();

        // Миллисекунды целые: доля от double накопила бы разницу в сроке между узлами.
        var delayMs = _timeoutMs + _random.Next(0, _timeoutMs);
        _timer = _clock.Schedule(TimeSpan.FromMilliseconds(delayMs), OnTimeout);
    }

    /// <summary>Останавливает отсчёт, не назначая новый: узел стал лидером или уходит с поста.</summary>
    /// <remarks>После <see cref="Dispose"/> - no-op: остановка уже мёртвого таймера не ошибка.</remarks>
    public void Stop()
    {
        if (_disposed)
        {
            return;
        }

        CancelCurrent();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelCurrent();
    }

    private void CancelCurrent()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private void OnTimeout()
    {
        if (_disposed)
        {
            return;
        }

        // Таймер одноразовый: после срабатывания отсчёта нет, пока его не запустят заново.
        _timer = null;
        _onTimeout();
    }
}
