using System.Reflection;

namespace RaftBroker.IntegrationTests;

/// <summary>
/// S0-T03: проверяет, что проект действительно ссылается на <c>Server</c> и <c>Client</c>
/// и что их сборки доезжают до выходного каталога. Настоящие интеграционные тесты
/// (реальный gRPC на loopback) появляются начиная с S4.
/// </summary>
public sealed class SmokeTests
{
    [Theory]
    [InlineData("RaftBroker.Server")]
    [InlineData("RaftBroker.Client")]
    public void ReferencedProjects_AreLoadable(string assemblyName)
    {
        var assembly = Assembly.Load(new AssemblyName(assemblyName));

        assembly.GetName().Name.Should().Be(assemblyName);
    }
}
