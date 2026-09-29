namespace RaftBroker.UnitTests;

/// <summary>
/// S0-T03: единственное, что можно проверить до появления типов в <c>Core</c>, - что
/// тестовый проект собирается, подхватывается <c>dotnet test</c> и что выбранная
/// ADR 0001 связка "xunit.v3 + AwesomeAssertions" действительно работает.
/// С S0-T04 сюда приходят настоящие тесты, а этот класс можно удалить.
/// </summary>
public sealed class SmokeTests
{
    [Fact]
    public void TestStack_RunsAndAsserts()
    {
        var assembly = typeof(SmokeTests).Assembly.GetName();

        assembly.Name.Should().Be("RaftBroker.UnitTests");
        assembly.Version.Should().NotBeNull();
    }
}
