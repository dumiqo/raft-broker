using Microsoft.Extensions.Configuration;
using RaftBroker.Core.Primitives;
using RaftBroker.Server;

namespace RaftBroker.IntegrationTests.Server;

/// <summary>
/// Разбор и проверка конфигурации узла (S0-T07).
/// </summary>
/// <remarks>
/// Тесты живут в интеграционных, а не в модульных, потому что модульные намеренно не
/// ссылаются на RaftBroker.Server (границы слоёв, S0-T08): конфигурация узла - это
/// стык процесса и окружения, а не внутренняя логика ядра.
/// </remarks>
public sealed class NodeOptionsTests
{
    private const string NodeIdKey = "RaftBroker:NodeId";

    private const string PortKey = "RaftBroker:Port";

    private const string PeersKey = "RaftBroker:Peers";

    [Fact]
    public void FromConfiguration_ReadsNodeIdPortAndPeers()
    {
        var options = Read(
            (NodeIdKey, "node-a"),
            (PortKey, "8080"),
            (PeersKey, "node-b=node-b:8080,node-c=node-c:8080"));

        options.NodeId.Value.Should().Be("node-a");
        options.Port.Should().Be(8080);
        options.Peers.Should().HaveCount(2);
        options.Peers[0].NodeId.Value.Should().Be("node-b");
        options.Peers[0].Host.Should().Be("node-b");
        options.Peers[0].Port.Should().Be(8080);
        options.Peers[0].Endpoint.Should().Be("node-b:8080");
        options.Peers[0].ToString().Should().Be("node-b=node-b:8080");
    }

    [Fact]
    public void FromConfiguration_AllowsSingleNodeWithoutPeers()
    {
        var options = Read((NodeIdKey, "node-a"), (PortKey, "8080"));

        options.Peers.Should().BeEmpty();
    }

    [Fact]
    public void FromConfiguration_IgnoresEmptyEntriesInPeerList()
    {
        var options = Read(
            (NodeIdKey, "node-a"),
            (PortKey, "8080"),
            (PeersKey, "node-b=node-b:8080, ,"));

        options.Peers.Should().ContainSingle();
    }

    [Fact]
    public void FromConfiguration_TrimsSpacesAroundPeers()
    {
        var options = Read(
            (NodeIdKey, "node-a"),
            (PortKey, "8080"),
            (PeersKey, "  node-b = node-b:8080  "));

        options.Peers[0].ToString().Should().Be("node-b=node-b:8080");
    }

    [Theory]
    [InlineData(NodeIdKey)]
    [InlineData(PortKey)]
    public void FromConfiguration_RejectsMissingSetting(string missingKey)
    {
        var settings = new List<(string Key, string? Value)>
        {
            (NodeIdKey, "node-a"),
            (PortKey, "8080"),
        };

        settings.RemoveAll(setting => setting.Key == missingKey);

        var act = () => Read([.. settings]);

        act.Should()
            .Throw<NodeConfigurationException>()
            // Имя переменной окружения - часть контракта с docker-compose: переименование
            // настройки должно ломать этот тест, а не запуск кластера.
            .WithMessage($"*{missingKey.Replace(":", "__", StringComparison.Ordinal)}*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("node a")]
    [InlineData(" node-a")]
    [InlineData("node-a ")]
    [InlineData("node\ta")]
    public void FromConfiguration_RejectsBadNodeId(string nodeId)
    {
        var act = () => Read((NodeIdKey, nodeId), (PortKey, "8080"));

        act.Should().Throw<NodeConfigurationException>().WithMessage($"*{NodeIdKey}*");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("65536")]
    [InlineData("8080 ")]
    [InlineData("8_080")]
    [InlineData("8080.0")]
    [InlineData("abc")]
    [InlineData("")]
    public void FromConfiguration_RejectsBadPort(string port)
    {
        var act = () => Read((NodeIdKey, "node-a"), (PortKey, port));

        act.Should().Throw<NodeConfigurationException>().WithMessage($"*{PortKey}*");
    }

    [Fact]
    public void FromConfiguration_RejectsSelfAsPeer()
    {
        var act = () => Read(
            (NodeIdKey, "node-a"),
            (PortKey, "8080"),
            (PeersKey, "node-a=node-a:8080"));

        act.Should().Throw<NodeConfigurationException>().WithMessage("*node-a*");
    }

    [Fact]
    public void FromConfiguration_RejectsDuplicatePeer()
    {
        var act = () => Read(
            (NodeIdKey, "node-a"),
            (PortKey, "8080"),
            (PeersKey, "node-b=node-b:8080,node-b=node-b:8081"));

        act.Should().Throw<NodeConfigurationException>().WithMessage("*node-b*");
    }

    [Theory]
    [InlineData("node-b")]
    [InlineData("node-b=")]
    [InlineData("=node-b:8080")]
    [InlineData("node-b=node-b")]
    [InlineData("node-b=:8080")]
    [InlineData("node-b=node-b:notaport")]
    [InlineData("node-b=node-b:8080:1")]
    [InlineData("node b=node-b:8080")]
    public void FromConfiguration_RejectsMalformedPeerEntry(string peers)
    {
        var act = () => Read((NodeIdKey, "node-a"), (PortKey, "8080"), (PeersKey, peers));

        act.Should().Throw<NodeConfigurationException>().WithMessage($"*{PeersKey}*");
    }

    [Fact]
    public void FromConfiguration_RejectsNullConfiguration()
    {
        var act = () => NodeOptions.FromConfiguration(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void PeerEndpoint_FormatsAddress()
    {
        var peer = new PeerEndpoint(new NodeId("node-b"), "node-b", 8080);

        peer.Endpoint.Should().Be("node-b:8080");
        peer.ToString().Should().Be("node-b=node-b:8080");
    }

    private static NodeOptions Read(params (string Key, string? Value)[] settings) =>
        NodeOptions.FromConfiguration(
            new ConfigurationBuilder()
                .AddInMemoryCollection(settings.Select(setting => new KeyValuePair<string, string?>(setting.Key, setting.Value)))
                .Build());
}
