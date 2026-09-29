namespace RaftBroker.Core.Time;

/// <summary>
/// Рантайм-реализация <see cref="IClock"/>: монотонное время процесса и таймеры на пуле потоков.
/// </summary>
/// <remarks>
/// <see cref="TimestampMs"/> берётся из <see cref="Environment.TickCount64"/> (монотонные
/// миллисекунды с запуска системы), а не из настенных часов: сдвиг системных часов не должен
/// приводить к тому, что таймаут выборов "уже прошёл" или "никогда не пройдёт".
/// </remarks>
public sealed class SystemClock : IClock
{
    /// <summary>Общий экземпляр: у класса нет состояния.</summary>
    public static SystemClock Instance { get; } = new();

    /// <inheritdoc />
    public long TimestampMs => Environment.TickCount64;

    /// <inheritdoc />
    public ITimer Schedule(TimeSpan delay, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        if (delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delay), delay, "Задержка таймера не может быть отрицательной.");
        }

        return new SystemTimer(delay, callback);
    }

    /// <summary>
    /// Таймер поверх <see cref="System.Threading.Timer"/>. Одноразовый: после срабатывания
    /// период не переставляется.
    /// </summary>
    private sealed class SystemTimer : ITimer
    {
        private readonly System.Threading.Timer _timer;
        private readonly Action _callback;
        private bool _isActive = true;

        internal SystemTimer(TimeSpan delay, Action callback)
        {
            _callback = callback;
            _timer = new System.Threading.Timer(static state => ((SystemTimer)state!).Fire(), this, delay, Timeout.InfiniteTimeSpan);
        }

        public bool IsActive => _isActive;

        public void Cancel()
        {
            if (_isActive)
            {
                _isActive = false;
                _timer.Dispose();
            }
        }

        public void Dispose() => Cancel();

        private void Fire()
        {
            // Колбэк может выполняться параллельно с Cancel: если отмену успели сделать
            // до входа сюда, колбэк не вызывается.
            if (!_isActive)
            {
                return;
            }

            _isActive = false;
            _timer.Dispose();
            _callback();
        }
    }
}
