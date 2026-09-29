using RaftBroker.Core.Randomness;

namespace RaftBroker.Core.Testing;

/// <summary>
/// Виртуальное время с ручным продвижением. Таймеры срабатывают в порядке дедлайнов,
/// реального ожидания нет: тест "таймер сработал через 150 мс" выполняется мгновенно.
/// </summary>
/// <remarks>
/// <para>
/// Живёт в <c>RaftBroker.Core</c>, а не в тестовом проекте, потому что нужен всем
/// четырём тестовым проектам и детерминированному симулятору S8-T01, а отдельного
/// shared-проекта для тестов в структуре решения нет.
/// </para>
/// <para>
/// Класс намеренно не потокобезопасен и не претендует на это: детерминизм важнее
/// конкурентности, а любой параллельный доступ к виртуальному времени означал бы,
/// что порядок срабатывания таймеров зависит от планировщика.
/// </para>
/// <para>
/// Задержки округляются вверх до целой миллисекунды, поэтому положительная задержка
/// никогда не срабатывает в тот же момент времени. <see cref="Advance"/> и
/// <see cref="AdvanceTo"/> назад по времени запрещены.
/// </para>
/// </remarks>
public sealed class TestClock : Time.IClock
{
    /// <summary>
    /// Предохранитель от бесконечного цикла: колбэк, который ставит новый таймер с нулевой
    /// задержкой, не должен подвешивать тест молча.
    /// </summary>
    private const int MaxFiringsPerAdvance = 1_000_000;

    private readonly List<TestTimer> _timers = [];
    private long _nowMs;
    private long _sequence;
    private bool _isAdvancing;

    /// <summary>Создаёт часы, стоящие на <paramref name="startMs"/>.</summary>
    public TestClock(long startMs = 0)
    {
        if (startMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startMs), startMs, "Начальное время не может быть отрицательным.");
        }

        _nowMs = startMs;
    }

    /// <inheritdoc />
    public long TimestampMs => _nowMs;

    /// <summary>Сколько таймеров ещё ждёт срабатывания.</summary>
    public int PendingTimerCount => _timers.Count(timer => timer.IsActive);

    /// <inheritdoc />
    public Time.ITimer Schedule(TimeSpan delay, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var timer = new TestTimer(this, checked(_nowMs + ToMillisecondsUp(delay)), _sequence++, callback);
        _timers.Add(timer);

        return timer;
    }

    /// <summary>Продвигает время на <paramref name="delta"/> и срабатывает все таймеры, чей дедлайн наступил.</summary>
    public void Advance(TimeSpan delta)
    {
        if (delta < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delta), delta, "Время нельзя двигать назад.");
        }

        AdvanceTo(checked(_nowMs + ToMillisecondsUp(delta)));
    }

    /// <summary>Продвигает время до <paramref name="timestampMs"/> и срабатывает все таймеры, чей дедлайн наступил.</summary>
    /// <remarks>
    /// Таймер, поставленный внутри колбэка и попадающий в тот же интервал, срабатывает
    /// в этом же вызове: так виртуальное время ведёт себя как настоящее, но без ожидания.
    /// </remarks>
    public void AdvanceTo(long timestampMs)
    {
        if (_isAdvancing)
        {
            throw new InvalidOperationException(
                "AdvanceTo нельзя вызывать из колбэка таймера: порядок срабатываний перестал бы быть детерминированным. Поставьте новый таймер через Schedule.");
        }

        if (timestampMs < _nowMs)
        {
            throw new ArgumentOutOfRangeException(nameof(timestampMs), timestampMs, "Время нельзя двигать назад.");
        }

        _isAdvancing = true;

        try
        {
            for (var fired = 0; ; fired++)
            {
                if (fired >= MaxFiringsPerAdvance)
                {
                    throw new InvalidOperationException(
                        $"Больше {MaxFiringsPerAdvance} срабатываний за одно продвижение времени: похоже, таймер ставит сам себя с нулевой задержкой.");
                }

                var next = FindNextDue(timestampMs);
                if (next is null)
                {
                    break;
                }

                _nowMs = next.DeadlineMs;
                next.Fire();
            }

            _nowMs = timestampMs;
        }
        finally
        {
            _isAdvancing = false;
        }
    }

    private static long ToMillisecondsUp(TimeSpan delay)
    {
        if (delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delay), delay, "Задержка таймера не может быть отрицательной.");
        }

        // Округление вверх: положительная задержка не должна срабатывать мгновенно.
        return checked((long)Math.Ceiling(delay.TotalMilliseconds));
    }

    /// <summary>Ближайший к сроку таймер; при равных дедлайнах - тот, что поставили раньше.</summary>
    private TestTimer? FindNextDue(long timestampMs)
    {
        TestTimer? best = null;

        foreach (var timer in _timers)
        {
            if (!timer.IsActive || timer.DeadlineMs > timestampMs)
            {
                continue;
            }

            if (best is null || timer.DeadlineMs < best.DeadlineMs || (timer.DeadlineMs == best.DeadlineMs && timer.Sequence < best.Sequence))
            {
                best = timer;
            }
        }

        return best;
    }

    private sealed class TestTimer : Time.ITimer
    {
        private readonly TestClock _clock;
        private readonly Action _callback;

        internal TestTimer(TestClock clock, long deadlineMs, long sequence, Action callback)
        {
            _clock = clock;
            DeadlineMs = deadlineMs;
            Sequence = sequence;
            _callback = callback;
        }

        internal long DeadlineMs { get; }

        /// <summary>Порядок постановки: разрешает ничьи между одинаковыми дедлайнами.</summary>
        internal long Sequence { get; }

        public bool IsActive { get; private set; } = true;

        public void Cancel() => IsActive = false;

        public void Dispose() => Cancel();

        internal void Fire()
        {
            IsActive = false;
            _clock.CompactTimersIfNeeded();
            _callback();
        }
    }

    /// <summary>Убирает отменённые и сработавшие таймеры, чтобы длинный тест не копил мусор.</summary>
    private void CompactTimersIfNeeded()
    {
        if (_timers.Count <= 16)
        {
            return;
        }

        _timers.RemoveAll(timer => !timer.IsActive);
    }
}
