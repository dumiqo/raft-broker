using RaftBroker.Core.Randomness;
using RaftBroker.Raft.Elections;
using RaftBroker.TestKit.Randomness;
using RaftBroker.TestKit.Time;

namespace RaftBroker.UnitTests.Raft;

/// <summary>
/// Таймер выборов (<c>S2-T03</c>): границы рандомизации, сдвиг срока, остановка.
/// </summary>
/// <remarks>
/// Всё на виртуальных часах: срок проверяется продвижением времени, а не ожиданием.
/// </remarks>
public sealed class ElectionTimerTests
{
    private const int BaseTimeoutMs = 200;

    private static readonly ElectionTimerOptions Options = new(TimeSpan.FromMilliseconds(BaseTimeoutMs));

    [Fact]
    public void Reset_WithTheSmallestRandomValue_FiresExactlyAtTheBaseTimeout()
    {
        // Arrange
        var clock = new TestClock();
        var fired = 0;
        using var timer = new ElectionTimer(clock, new ScriptedRandomSource(0), Options, () => fired++);
        timer.Reset();

        // Act
        clock.Advance(TimeSpan.FromMilliseconds(BaseTimeoutMs - 1));
        var beforeDeadline = fired;
        clock.Advance(TimeSpan.FromMilliseconds(1));

        // Assert
        beforeDeadline.Should().Be(0);
        fired.Should().Be(1);
    }

    [Fact]
    public void Reset_WithTheLargestRandomValue_FiresJustBeforeTwiceTheBaseTimeout()
    {
        // Arrange: верхняя граница - T + (T - 1), потому что Next(0, T) не включает T.
        var clock = new TestClock();
        var fired = 0;
        using var timer = new ElectionTimer(clock, new ScriptedRandomSource(int.MaxValue), Options, () => fired++);
        timer.Reset();

        // Act
        clock.Advance(TimeSpan.FromMilliseconds((2 * BaseTimeoutMs) - 2));
        var beforeDeadline = fired;
        clock.Advance(TimeSpan.FromMilliseconds(1));

        // Assert
        beforeDeadline.Should().Be(0);
        fired.Should().Be(1);
    }

    [Fact]
    public void Reset_InTheMiddleOfTheWindow_MovesTheDeadlineAndTheOldTimerNeverFires()
    {
        // Arrange: первый срок - 200 мс, второй (после сдвига) - 400 мс от момента сброса.
        var clock = new TestClock();
        var fired = 0;
        using var timer = new ElectionTimer(clock, new ScriptedRandomSource(0, 200), Options, () => fired++);
        timer.Reset();

        clock.Advance(TimeSpan.FromMilliseconds(150));

        // Act
        timer.Reset();
        clock.Advance(TimeSpan.FromMilliseconds(BaseTimeoutMs));

        // Assert: 350 мс от старта - старый дедлайн прошёл бы, новый ещё нет.
        fired.Should().Be(0);

        clock.Advance(TimeSpan.FromMilliseconds(200));
        fired.Should().Be(1);
    }

    [Fact]
    public void TwoSeededSourcesWithDifferentSeeds_GiveDifferentDeadlines()
    {
        // Arrange
        var first = new TestClock();
        var second = new TestClock();
        var firstDeadline = 0L;
        var secondDeadline = 0L;

        using var firstTimer = new ElectionTimer(first, new SeededRandomSource(1), Options, () => firstDeadline = first.TimestampMs);
        using var secondTimer = new ElectionTimer(second, new SeededRandomSource(2), Options, () => secondDeadline = second.TimestampMs);

        // Act
        firstTimer.Reset();
        secondTimer.Reset();
        first.Advance(TimeSpan.FromMilliseconds(2 * BaseTimeoutMs));
        second.Advance(TimeSpan.FromMilliseconds(2 * BaseTimeoutMs));

        // Assert: симметрия действительно рвётся, а не только объявлена.
        firstDeadline.Should().NotBe(secondDeadline);
    }

    [Fact]
    public void Stop_CancelsTheTimerAndNothingFires()
    {
        // Arrange
        var clock = new TestClock();
        var fired = 0;
        using var timer = new ElectionTimer(clock, new ScriptedRandomSource(0), Options, () => fired++);
        timer.Reset();

        // Act
        timer.Stop();

        // Assert
        timer.IsActive.Should().BeFalse();
        clock.Advance(TimeSpan.FromMilliseconds(10 * BaseTimeoutMs));
        fired.Should().Be(0);
    }

    [Fact]
    public void Reset_AfterAFiring_SchedulesANewDeadline()
    {
        // Arrange
        var clock = new TestClock();
        var fired = 0;
        using var timer = new ElectionTimer(clock, new ScriptedRandomSource(0, 0), Options, () => fired++);
        timer.Reset();
        clock.Advance(TimeSpan.FromMilliseconds(BaseTimeoutMs));

        // Act
        timer.Reset();
        clock.Advance(TimeSpan.FromMilliseconds(BaseTimeoutMs));

        // Assert
        fired.Should().Be(2);
    }

    [Fact]
    public void IsActive_AfterFiring_IsFalse()
    {
        // Arrange
        var clock = new TestClock();
        using var timer = new ElectionTimer(clock, new ScriptedRandomSource(0), Options, () => { });
        timer.Reset();

        // Act
        clock.Advance(TimeSpan.FromMilliseconds(BaseTimeoutMs));

        // Assert
        timer.IsActive.Should().BeFalse();
    }

    [Fact]
    public void Stop_AfterDispose_IsANoOp()
    {
        // Arrange
        var clock = new TestClock();
        using var timer = new ElectionTimer(clock, new ScriptedRandomSource(0), Options, () => { });

        // Act
        timer.Dispose();

        // Assert
        timer.Stop();
        timer.IsActive.Should().BeFalse();
    }

    [Fact]
    public void Reset_AfterDispose_Throws()
    {
        // Arrange
        using var timer = new ElectionTimer(new TestClock(), new ScriptedRandomSource(0), Options, () => { });
        timer.Dispose();

        // Act
        var act = timer.Reset;

        // Assert
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Dispose_Twice_IsSafe()
    {
        // Arrange
        var timer = new ElectionTimer(new TestClock(), new ScriptedRandomSource(0), Options, () => { });

        // Act
        timer.Dispose();
        timer.Dispose();

        // Assert
        timer.IsActive.Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(0.5)]
    public void Options_WithANonPositiveOrFractionalTimeout_Throws(double milliseconds)
    {
        var act = () => new ElectionTimerOptions(TimeSpan.FromMilliseconds(milliseconds));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Options_Default_IsTheSpecificationProposal()
    {
        ElectionTimerOptions.Default.Timeout.Should().Be(TimeSpan.FromMilliseconds(200));
    }
}
