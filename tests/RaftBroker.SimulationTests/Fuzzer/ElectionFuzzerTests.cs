using System.Diagnostics;
using RaftBroker.SimulationTests.Fuzzer;

namespace RaftBroker.SimulationTests;

/// <summary>
/// Приёмочные тесты фаззера выборов (<c>S2-T10</c>): тысяча прогонов без нарушений R1, детерминизм
/// по сиду и мутационный прогон, который обязан упасть.
/// </summary>
public sealed class ElectionFuzzerTests
{
    /// <summary>Потолок времени прогона: гейт не должен начать ползти незаметно.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    /// <summary>Диапазон сидов основного прогона: <c>0..999</c>.</summary>
    private static readonly int[] Seeds = [.. Enumerable.Range(0, 1000)];

    [Fact]
    public async Task Fuzzer_OverThousandSeeds_ViolatesNoElectionSafety()
    {
        var stopwatch = Stopwatch.StartNew();
        var elected = 0;
        var crashes = 0;
        var restarts = 0;

        foreach (var seed in Seeds)
        {
            var result = await ElectionFuzzer.RunAsync(seed);

            if (result.LeaderChanges > 0)
            {
                elected++;
            }

            crashes += result.Crashes;
            restarts += result.Restarts;
        }

        stopwatch.Stop();

        elected.Should().Be(Seeds.Length, "прогон без выборов не проверяет R1");
        crashes.Should().BePositive("сценарий без падений не проверяет R6 после рестарта");
        restarts.Should().BePositive();
        stopwatch.Elapsed.Should().BeLessThan(Budget);
    }

    [Fact]
    public async Task Fuzzer_WithFiveNodes_ViolatesNoElectionSafety()
    {
        // Кворум 5 узлов - 3 узла: счётчик кворума не зашит под три.
        foreach (var seed in Enumerable.Range(0, 200))
        {
            var result = await ElectionFuzzer.RunAsync(seed, new ElectionFuzzerOptions { NodeCount = 5 });

            result.LeaderChanges.Should().BePositive($"сид {seed} обязан кого-то выбрать");
        }
    }

    [Fact]
    public async Task Fuzzer_WithoutPreVote_ViolatesNoElectionSafety()
    {
        var withoutPreVote = new ElectionFuzzerOptions { PreVote = false };

        foreach (var seed in Enumerable.Range(0, 200))
        {
            var result = await ElectionFuzzer.RunAsync(seed, withoutPreVote);

            result.LeaderChanges.Should().BePositive($"сид {seed} обязан кого-то выбрать");
        }
    }

    [Fact]
    public async Task Fuzzer_WithoutPreVoteAndCheckQuorum_ViolatesNoElectionSafety()
    {
        // Классический Raft без расширений: безопасность выборов не должна держаться на PreVote.
        var classic = new ElectionFuzzerOptions { PreVote = false, CheckQuorum = false };

        foreach (var seed in Enumerable.Range(0, 200))
        {
            await ElectionFuzzer.RunAsync(seed, classic);
        }
    }

    [Fact]
    public async Task Fuzzer_WithTheSameSeed_ProducesTheSameTimeline()
    {
        var first = await ElectionFuzzer.RunAsync(7);
        var second = await ElectionFuzzer.RunAsync(7);

        second.Timeline.Should().Equal(first.Timeline);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public async Task Fuzzer_ByExplicitSeed_ReplaysTheRun(int seed)
    {
        // Режим одиночного прогона: из сообщения о падении сид вставляется сюда как InlineData.
        var result = await ElectionFuzzer.RunAsync(seed);

        result.Seed.Should().Be(seed);
        result.LeaderChanges.Should().BePositive();
    }

    [Fact]
    public async Task Fuzzer_WithSecondVoteInTerm_FailsOnElectionSafety()
    {
        // Мутация: избиратель подтверждает второй голос в том же term. Два кандидата в одном term
        // собирают кворум - значит проверка R1 обязана это поймать, иначе фаззер ничего не ловит.
        var mutation = new ElectionFuzzerOptions { PreVote = false, LyingVoter = true };
        var violations = new List<string>();

        foreach (var seed in Enumerable.Range(0, 20))
        {
            try
            {
                await ElectionFuzzer.RunAsync(seed, mutation);
            }
            catch (ElectionSafetyViolationException violation)
            {
                violations.Add(violation.Message);
            }
        }

        violations.Should().NotBeEmpty("сломанный шаг обязан ронять фаззер");
        violations.Should().Contain(message => message.Contains("R1", StringComparison.Ordinal));
        violations[0].Should().Contain("Повторить: ElectionFuzzer.RunAsync(");
    }
}
