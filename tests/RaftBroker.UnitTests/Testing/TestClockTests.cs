using System.Diagnostics;
using RaftBroker.Core.Testing;

namespace RaftBroker.UnitTests.Testing;

/// <summary>
/// S0-T04: доказательство, что время подменяемо и что таймеры на <see cref="TestClock"/>
/// срабатывают детерминированно, без <c>Thread.Sleep</c> и без ожидания настенных
/// миллисекунд.
/// </summary>
public sealed class TestClockTests
{
    [Fact]
    public void Timer_FiresExactlyAtItsVirtualDeadline()
    {
        var clock = new TestClock();
        var fired = false;
        clock.Schedule(TimeSpan.FromMilliseconds(150), () => fired = true);

        clock.Advance(TimeSpan.FromMilliseconds(149));

        fired.Should().BeFalse("до дедлайна остался 1 мс виртуального времени");
        clock.TimestampMs.Should().Be(149);

        clock.Advance(TimeSpan.FromMilliseconds(1));

        fired.Should().BeTrue();
        clock.TimestampMs.Should().Be(150);
    }

    [Fact]
    public void TenMinutesOfVirtualTime_CostNoRealWaiting()
    {
        var clock = new TestClock();
        var firings = 0;
        clock.Schedule(TimeSpan.FromMinutes(10), () => firings++);

        var stopwatch = Stopwatch.StartNew();
        clock.Advance(TimeSpan.FromMinutes(10));
        stopwatch.Stop();

        firings.Should().Be(1);
        stopwatch.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(1),
            "виртуальные 10 минут не имеют права стоить реального времени");
    }

    [Fact]
    public void Timers_FireInDeadlineOrder_NotInSchedulingOrder()
    {
        var clock = new TestClock();
        var order = new List<string>();

        clock.Schedule(TimeSpan.FromMilliseconds(300), () => order.Add("third"));
        clock.Schedule(TimeSpan.FromMilliseconds(100), () => order.Add("first"));
        clock.Schedule(TimeSpan.FromMilliseconds(200), () => order.Add("second"));

        clock.Advance(TimeSpan.FromSeconds(1));

        order.Should().Equal("first", "second", "third");
    }

    [Fact]
    public void Timers_WithEqualDeadlines_FireInSchedulingOrder()
    {
        var clock = new TestClock();
        var order = new List<string>();

        clock.Schedule(TimeSpan.FromMilliseconds(50), () => order.Add("a"));
        clock.Schedule(TimeSpan.FromMilliseconds(50), () => order.Add("b"));
        clock.Schedule(TimeSpan.FromMilliseconds(50), () => order.Add("c"));

        clock.Advance(TimeSpan.FromMilliseconds(50));

        order.Should().Equal("a", "b", "c");
    }

    [Fact]
    public void Timer_ScheduledInsideCallback_FiresInTheSameAdvance()
    {
        var clock = new TestClock();
        var order = new List<string>();

        clock.Schedule(TimeSpan.FromMilliseconds(10), () =>
        {
            order.Add("outer");
            clock.Schedule(TimeSpan.FromMilliseconds(5), () => order.Add("inner"));
        });

        clock.Advance(TimeSpan.FromMilliseconds(100));

        order.Should().Equal("outer", "inner");
        clock.TimestampMs.Should().Be(100);
    }

    [Fact]
    public void CancelledTimer_NeverFires()
    {
        var clock = new TestClock();
        var fired = false;
        var timer = clock.Schedule(TimeSpan.FromMilliseconds(10), () => fired = true);

        timer.IsActive.Should().BeTrue();
        clock.PendingTimerCount.Should().Be(1);

        timer.Cancel();

        timer.IsActive.Should().BeFalse();
        clock.PendingTimerCount.Should().Be(0);

        clock.Advance(TimeSpan.FromSeconds(1));

        fired.Should().BeFalse();
    }

    [Fact]
    public void FiredTimer_BecomesInactive_AndCancellingItLaterIsHarmless()
    {
        var clock = new TestClock();
        var timer = clock.Schedule(TimeSpan.FromMilliseconds(10), () => { });

        clock.Advance(TimeSpan.FromMilliseconds(10));

        timer.IsActive.Should().BeFalse();
        timer.Cancel();
        clock.PendingTimerCount.Should().Be(0);
    }

    [Fact]
    public void SubMillisecondDelay_RoundsUp_SoItNeverFiresImmediately()
    {
        var clock = new TestClock();
        var fired = false;
        clock.Schedule(TimeSpan.FromTicks(1), () => fired = true);

        clock.Advance(TimeSpan.Zero);
        fired.Should().BeFalse();

        clock.Advance(TimeSpan.FromMilliseconds(1));
        fired.Should().BeTrue();
    }

    [Fact]
    public void Schedule_WithNegativeDelay_IsRejected()
    {
        var clock = new TestClock();

        var act = () => clock.Schedule(TimeSpan.FromMilliseconds(-1), () => { });

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Schedule_WithZeroDelay_FiresOnTheNextAdvanceOfAnySize()
    {
        var clock = new TestClock();
        var fired = false;
        clock.Schedule(TimeSpan.Zero, () => fired = true);

        fired.Should().BeFalse("нулевая задержка срабатывает при продвижении времени, а не сразу");

        clock.Advance(TimeSpan.Zero);

        fired.Should().BeTrue();
    }

    [Fact]
    public void TimeNeverMovesBackwards()
    {
        var clock = new TestClock(startMs: 1_000);
        clock.Advance(TimeSpan.FromMilliseconds(10));

        var backwards = () => clock.Advance(TimeSpan.FromMilliseconds(-1));
        backwards.Should().Throw<ArgumentOutOfRangeException>();

        var toThePast = () => clock.AdvanceTo(999);
        toThePast.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void AdvanceFromInsideCallback_IsRejected()
    {
        var clock = new TestClock();
        clock.Schedule(TimeSpan.FromMilliseconds(1), () => clock.Advance(TimeSpan.FromMilliseconds(1)));

        var act = () => clock.Advance(TimeSpan.FromMilliseconds(1));

        act.Should().Throw<InvalidOperationException>().WithMessage("*нельзя вызывать из колбэка*");
    }

    [Fact]
    public void Clock_StartsAtConfiguredInstant_AndRejectsNegativStart()
    {
        new TestClock(startMs: 1_700_000_000_000).TimestampMs.Should().Be(1_700_000_000_000);

        var act = () => _ = new TestClock(startMs: -1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void InactiveTimers_DoNotAccumulate()
    {
        var clock = new TestClock();

        for (var i = 0; i < 100; i++)
        {
            clock.Schedule(TimeSpan.FromMilliseconds(1), () => { }).Cancel();
        }

        clock.PendingTimerCount.Should().Be(0);

        var fired = 0;
        clock.Schedule(TimeSpan.FromMilliseconds(1), () => fired++);

        clock.Advance(TimeSpan.FromMilliseconds(1));

        fired.Should().Be(1);
    }
}
