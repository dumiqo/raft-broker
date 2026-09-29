using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RaftBroker.Server;

/// <summary>
/// Хост узла на этапе S0: занимает порт, пишет в лог, кто он и с кем собирается говорить,
/// и живёт до остановки процесса.
/// </summary>
/// <remarks>
/// <para>
/// Порт занимает <see cref="TcpListener"/>, а не Kestrel: gRPC-сервис из <c>proto/raft.proto</c>
/// подключается на S4-T01. Слушатель нужен уже сейчас, потому что DoD этапа требует
/// "три процесса, порты не конфликтуют".
/// </para>
/// <para>
/// Порт занимается в <see cref="StartAsync"/>, а не в фоновой задаче. Исключение из
/// <see cref="StartAsync"/> валит запуск хоста и процесс, а исключение внутри
/// <see cref="BackgroundService.ExecuteAsync"/> хост при настройке по умолчанию
/// (<c>StopHost</c>) логирует и проглатывает - узел, которому не досталось порта,
/// завершался бы с кодом 0, то есть выглядел бы успешно запустившимся.
/// </para>
/// </remarks>
internal sealed class NodeHost(NodeOptions options, ILogger<NodeHost> logger) : BackgroundService
{
    private TcpListener? _listener;

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = logger.BeginScope("node={NodeId}", options.NodeId.Value);

        var listener = new TcpListener(IPAddress.Any, options.Port);

        try
        {
            listener.Start();
        }
        catch (SocketException exception)
        {
            // Без объекта исключения: это ожидаемый отказ (порт занят), а не сбой,
            // для которого нужна трассировка - стек всё равно попадёт в лог хоста.
            logger.LogCritical(
                "node-start-failed port={Port} error={Error}: порт занят или недоступен",
                options.Port,
                exception.SocketErrorCode);

            listener.Stop();

            throw;
        }

        _listener = listener;

        logger.LogInformation(
            "node-up node_id={NodeId} port={Port} peers=[{Peers}] stage=S0 (gRPC появится на S4-T01)",
            options.NodeId.Value,
            options.Port,
            string.Join(",", options.Peers.Select(peer => peer.ToString())));

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var scope = logger.BeginScope("node={NodeId}", options.NodeId.Value);

        var listener = _listener
            ?? throw new InvalidOperationException($"{nameof(NodeHost)}.StartAsync не был вызван.");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Входящие соединения принимаются и сразу закрываются: до S4 на этом порту
                // нет протокола, но так порт можно проверить пробой (healthcheck в compose).
                using var connection = await listener.AcceptTcpClientAsync(stoppingToken);
                logger.LogDebug("probe-accepted remote={Remote}", connection.Client.RemoteEndPoint);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
        {
            // Штатная остановка: docker stop шлёт SIGTERM, хост отменяет токен,
            // а StopAsync снимает слушателя.
        }
        finally
        {
            logger.LogInformation("node-down node_id={NodeId}", options.NodeId.Value);
        }
    }

    /// <inheritdoc />
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        // Порт снимается до ожидания фоновой задачи: иначе ожидающий Accept не завершится.
        _listener?.Stop();

        return base.StopAsync(cancellationToken);
    }
}
