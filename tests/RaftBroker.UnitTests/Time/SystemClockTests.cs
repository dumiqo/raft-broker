using RaftBroker.Core.Time;

namespace RaftBroker.UnitTests.Time;

/// <summary>
/// Рантайм-часы: две шкалы времени (монотонная для таймаутов, настенная для меток)
/// и одноразовый таймер поверх пула потоков.
/// </summary>
/// <remarks>
/// Тесты делятся на два вида. Свойства шкал проверяются без ожидания и без допущений
/// о скорости машины. Таймер проверяется ожиданием с щедрым запасом: 50 мс задержки
/// против 10 с таймаута - на любой машине CI разница между «сработал» и «не сработал»
/// на порядки больше.
/// </remarks>
public sealed class SystemClockTests
{
    [Fact]
    public void TimestampMs_DoesNotGoBackwards()
    {
        // Монотонность - условие корректности таймаутов выборов: сдвиг системных часов
        // не должен превращать «ещё не истёк» в «истёк давно» или наоборот.
        var clock = SystemClock.Instance;

        var first = clock.TimestampMs;
        var second = clock.TimestampMs;

        second.Should().BeGreaterThanOrEqualTo(first);
    }

    [Fact]
    public void UnixTimeMs_IsWallClockTimeInMilliseconds()
    {
        var clock = SystemClock.Instance;

        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var actual = clock.UnixTimeMs;
        var after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Метка создания сегмента пишется на диск (S1-T03): секунды вместо миллисекунд
        // сделали бы её бесполезной, а монотонная шкала - несравнимой между узлами.
        actual.Should().BeInRange(before, after);
    }

    [Fact]
    public void Schedule_WithANegativeDelay_Throws()
    {
        var clock = SystemClock.Instance;

        var schedule = () => clock.Schedule(TimeSpan.FromMilliseconds(-1), () => { });

        schedule.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Schedule_WithANullCallback_Throws()
    {
        var clock = SystemClock.Instance;

        var schedule = () => clock.Schedule(TimeSpan.Zero, null!);

        schedule.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Schedule_InvokesTheCallbackOnceAndBecomesInactive()
    {
        var clock = SystemClock.Instance;
        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var timer = clock.Schedule(TimeSpan.FromMilliseconds(20), () => fired.TrySetResult());

        timer.IsActive.Should().BeTrue();

        await fired.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        timer.IsActive.Should().BeFalse("таймер одноразовый: после срабатывания он не переставляется");
        timer.Dispose();
    }

    [Fact]
    public async Task Cancel_BeforeTheDelayElapses_PreventsTheCallback()
    {
        var clock = SystemClock.Instance;
        var calls = 0;

        var timer = clock.Schedule(TimeSpan.FromMilliseconds(50), () => Interlocked.Increment(ref calls));

        timer.Cancel();
        timer.IsActive.Should().BeFalse();

        // Задержка 50 мс: ожидание 500 мс делает вердикт надёжным и не зависит от скорости CI.
        await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);

        Volatile.Read(ref calls).Should().Be(0);
        timer.Dispose();
    }

    [Fact]
    public void Cancel_IsIdempotent()
    {
        // Отмена приходит и из кода отмены, и из Dispose: повторный вызов не должен падать
        // на уже освобождённом таймере.
        var timer = SystemClock.Instance.Schedule(TimeSpan.FromSeconds(30), () => { });

        timer.Cancel();
        timer.Cancel();
        timer.Dispose();

        timer.IsActive.Should().BeFalse();
    }
}
