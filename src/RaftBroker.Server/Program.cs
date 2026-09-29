using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using RaftBroker.Server;

// Точка входа узла (S0-T07). Задача этапа - три процесса в docker-compose с различимым
// логом; консенсус начинается на S2, gRPC-хост на S4.

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
});

// Предупреждения и ошибки идут в stderr: docker logs разделяет потоки, и сбой узла
// должен быть виден как ошибка, а не тонуть в обычном выводе.
builder.Services.Configure<ConsoleLoggerOptions>(options =>
    options.LogToStandardErrorThreshold = LogLevel.Warning);

NodeOptions options;

try
{
    options = NodeOptions.FromConfiguration(builder.Configuration);
}
catch (NodeConfigurationException exception)
{
    // Конфигурация проверяется до старта хоста: узел с плохим id или пирами не должен
    // открывать порт и выглядеть живым.
    await Console.Error.WriteLineAsync($"Конфигурация узла не принята: {exception.Message}");

    return 2;
}

builder.Services.AddSingleton(options);
builder.Services.AddHostedService<NodeHost>();

using var host = builder.Build();

try
{
    await host.RunAsync();
}
catch (SocketException exception)
{
    // Занятый порт доходит сюда из NodeHost.StartAsync: причина уже написана в лог
    // строкой node-start-failed, здесь добавляется код возврата. Без этого
    // исключение из фоновой задачи хост логирует и узел завершается успешно.
    await Console.Error.WriteLineAsync($"Узел не запустился: {exception.Message}");

    return 3;
}

return 0;
