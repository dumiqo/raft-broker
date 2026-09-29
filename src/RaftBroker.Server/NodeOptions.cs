using System.Globalization;
using Microsoft.Extensions.Configuration;
using RaftBroker.Core.Primitives;

namespace RaftBroker.Server;

/// <summary>
/// Конфигурация узла: кто мы, на каком порту слушаем и кто наши пиры.
/// </summary>
/// <remarks>
/// <para>
/// S0-T07 читает эти значения только из переменных окружения с префиксом
/// <c>RaftBroker__</c> (то есть <c>RaftBroker__NodeId</c>, <c>RaftBroker__Port</c>,
/// <c>RaftBroker__Peers</c>). Формат JSON-файла конфигурации введён здесь же, в
/// <c>deploy/node-config.example.json</c>, но загрузка файла и приоритет
/// "файл, поверх него env" - это S4-T05.
/// </para>
/// <para>
/// Ошибочная конфигурация не должна доходить до открытия порта: идентификатор
/// проверяется типом <see cref="NodeId"/>, порт - диапазоном, список пиров - разбором
/// с проверкой дублей и ссылки на самого себя.
/// </para>
/// </remarks>
public sealed record NodeOptions(NodeId NodeId, int Port, IReadOnlyList<PeerEndpoint> Peers)
{
    internal const string NodeIdKey = "RaftBroker:NodeId";

    internal const string PortKey = "RaftBroker:Port";

    internal const string PeersKey = "RaftBroker:Peers";

    /// <summary>Читает и проверяет конфигурацию узла.</summary>
    /// <exception cref="NodeConfigurationException">Настройка не задана, не разбирается или противоречива.</exception>
    public static NodeOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var rawNodeId = Require(configuration, NodeIdKey);
        NodeId nodeId;

        try
        {
            nodeId = new NodeId(rawNodeId);
        }
        catch (ArgumentException exception)
        {
            throw new NodeConfigurationException($"{NodeIdKey}='{rawNodeId}' не годится: {exception.Message}", exception);
        }

        var port = ParsePort(Require(configuration, PortKey), PortKey);
        var peers = ParsePeers(configuration[PeersKey]);

        foreach (var peer in peers)
        {
            if (peer.NodeId == nodeId)
            {
                throw new NodeConfigurationException(
                    $"Узел '{nodeId}' указан в списке собственных пиров ({PeersKey}): узел не должен считать себя пиром.");
            }
        }

        var duplicate = peers.GroupBy(peer => peer.NodeId).FirstOrDefault(group => group.Count() > 1);

        if (duplicate is not null)
        {
            throw new NodeConfigurationException($"Пир '{duplicate.Key}' указан в {PeersKey} больше одного раза.");
        }

        return new NodeOptions(nodeId, port, peers);
    }

    private static string Require(IConfiguration configuration, string key)
    {
        var value = configuration[key];

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new NodeConfigurationException(
                $"Не задана настройка '{key}'. В контейнере она приходит переменной окружения '{ToEnvironmentVariableName(key)}'.");
        }

        return value;
    }

    /// <summary>Имя переменной окружения для ключа конфигурации: <c>RaftBroker:NodeId</c> -> <c>RaftBroker__NodeId</c>.</summary>
    internal static string ToEnvironmentVariableName(string key) => key.Replace(":", "__", StringComparison.Ordinal);

    private static int ParsePort(string raw, string key)
    {
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            throw new NodeConfigurationException($"{key}='{raw}' не является числом в диапазоне 1..65535.");
        }

        return port;
    }

    /// <summary>Разбирает список вида <c>node-b=node-b:8080,node-c=node-c:8080</c>.</summary>
    private static IReadOnlyList<PeerEndpoint> ParsePeers(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return [.. raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(ParsePeer)];
    }

    private static PeerEndpoint ParsePeer(string entry)
    {
        var parts = entry.Split('=', StringSplitOptions.TrimEntries);

        if (parts.Length != 2 || parts[0].Length == 0)
        {
            throw new NodeConfigurationException($"Пир '{entry}' в {PeersKey} записан не как 'id=host:port'.");
        }

        var address = parts[1].Split(':');

        if (address.Length != 2 || address[0].Length == 0)
        {
            throw new NodeConfigurationException($"Адрес пира '{parts[1]}' в {PeersKey} записан не как 'host:port'.");
        }

        NodeId nodeId;

        try
        {
            nodeId = new NodeId(parts[0]);
        }
        catch (ArgumentException exception)
        {
            throw new NodeConfigurationException($"Идентификатор пира '{parts[0]}' в {PeersKey} не годится: {exception.Message}", exception);
        }

        return new PeerEndpoint(nodeId, address[0], ParsePort(address[1], PeersKey));
    }
}

/// <summary>Адрес пира: идентификатор узла и его сетевой адрес.</summary>
public sealed record PeerEndpoint(NodeId NodeId, string Host, int Port)
{
    /// <summary>Адрес в виде <c>host:port</c>.</summary>
    public string Endpoint => $"{Host}:{Port}";

    /// <inheritdoc />
    public override string ToString() => $"{NodeId}={Endpoint}";
}
