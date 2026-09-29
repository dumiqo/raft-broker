using System.Net;
using System.Net.Sockets;

namespace RaftBroker.IntegrationTests.Server;

/// <summary>
/// Узел как процесс: стартует, слушает свой порт, пишет различимый лог (S0-T07).
/// </summary>
/// <remarks>
/// Это проверка DoD этапа "docker compose поднимает три процесса, порты не конфликтуют"
/// без Docker: тот же исполняемый файл, те же переменные окружения, только без контейнеров.
/// Несколько процессов сразу - это и есть "три узла" в миниатюре.
/// </remarks>
public sealed class NodeProcessTests
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Node_WithValidConfiguration_BindsItsPortAndLogsItsIdentity()
    {
        var port = GetFreePort();

        await using var node = NodeProcess.Start(port.ToString(), "node-a", "node-b=node-b:8080,node-c=node-c:8080");

        var line = await node.WaitForStandardOutputAsync("node-up", StartupTimeout);

        line.Should().Contain("node_id=node-a").And.Contain($"port={port}");
        line.Should().Contain("node-b=node-b:8080");
        await AssertPortIsOpenAsync(port);

        node.HasExited.Should().BeFalse("узел должен жить после старта, а не завершаться сразу");
    }

    [Fact]
    public async Task Nodes_OnDifferentPorts_DoNotConflict()
    {
        var portA = GetFreePort();
        var portB = GetFreePort();

        await using var nodeA = NodeProcess.Start(portA.ToString(), "node-a", $"node-b=node-b:{portB}");
        await using var nodeB = NodeProcess.Start(portB.ToString(), "node-b", $"node-a=node-a:{portA}");

        await nodeA.WaitForStandardOutputAsync("node-up", StartupTimeout);
        await nodeB.WaitForStandardOutputAsync("node-up", StartupTimeout);

        await AssertPortIsOpenAsync(portA);
        await AssertPortIsOpenAsync(portB);

        nodeA.HasExited.Should().BeFalse();
        nodeB.HasExited.Should().BeFalse();
    }

    [Fact]
    public async Task Node_WithOccupiedPort_FailsInsteadOfPretendingToBeUp()
    {
        var port = GetFreePort();

        await using var first = NodeProcess.Start(port.ToString(), "node-a", string.Empty);
        await first.WaitForStandardOutputAsync("node-up", StartupTimeout);

        await using var second = NodeProcess.Start(port.ToString(), "node-b", string.Empty);
        var exitCode = await second.WaitForExitAsync(ShutdownTimeout);

        // Код возврата 3: второй узел на том же порту обязан свалиться громко,
        // иначе "порты не конфликтуют" превращается в лотерею.
        exitCode.Should().Be(3);
        second.StandardError.Should().Contain("node-start-failed");
    }

    [Fact]
    public async Task Node_WithInvalidConfiguration_ExitsWithClearReason()
    {
        await using var node = NodeProcess.Start("70000", "node-a", string.Empty);

        (await node.WaitForExitAsync(ShutdownTimeout)).Should().Be(2);
        node.StandardError.Should().Contain("RaftBroker:Port").And.Contain("70000");
    }

    [Fact]
    public async Task Node_WithoutNodeId_ExitsWithClearReason()
    {
        await using var node = NodeProcess.Start(GetFreePort().ToString(), string.Empty, string.Empty);

        (await node.WaitForExitAsync(ShutdownTimeout)).Should().Be(2);
        node.StandardError.Should().Contain("RaftBroker__NodeId");
    }

    /// <summary>Свободный порт, который ещё никто не занял.</summary>
    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task AssertPortIsOpenAsync(int port)
    {
        using var client = new TcpClient();

        await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(10));
    }
}
