using System.Reflection;

namespace RaftBroker.SimulationTests;

/// <summary>
/// S0-T03: симметрично остальным тестовым проектам - проверяем, что ссылки на
/// <c>Storage</c>, <c>Raft</c> и <c>StateMachine</c> живы и сборки доступны.
/// Детерминированный симулятор кластера появляется на S8-T01.
/// </summary>
public sealed class SmokeTests
{
    [Theory]
    [InlineData("RaftBroker.Storage")]
    [InlineData("RaftBroker.Raft")]
    [InlineData("RaftBroker.StateMachine")]
    public void ReferencedProjects_AreLoadable(string assemblyName)
    {
        var assembly = Assembly.Load(new AssemblyName(assemblyName));

        assembly.GetName().Name.Should().Be(assemblyName);
    }
}
