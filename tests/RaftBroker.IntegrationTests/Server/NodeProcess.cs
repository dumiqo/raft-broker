using System.Diagnostics;
using System.Text;

namespace RaftBroker.IntegrationTests.Server;

/// <summary>
/// Узел, запущенный отдельным процессом (S0-T07).
/// </summary>
/// <remarks>
/// <para>
/// Тесты запускают <c>RaftBroker.Server.dll</c> так же, как это делает docker-compose:
/// переменными окружения <c>RaftBroker__*</c>, без аргументов командной строки.
/// Это даёт проверку "три процесса, порты не конфликтуют" без Docker: St0-T07 в этой
/// среде проверить через Docker не удалось (демон не запущен), а факт, который требовался
/// в DoD, проверить нужно.
/// </para>
/// <para>
/// Вывод процесса собирается фоновыми читателями: иначе заполненный буфер stdout
/// заблокировал бы узел и тест завис бы на ожидании строки лога.
/// </para>
/// </remarks>
internal sealed class NodeProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _standardOutput = new();
    private readonly StringBuilder _standardError = new();
    private readonly object _gate = new();

    private NodeProcess(Process process)
    {
        _process = process;

        _ = Task.Run(() => ReadAsync(_process.StandardOutput, _standardOutput));
        _ = Task.Run(() => ReadAsync(_process.StandardError, _standardError));
    }

    /// <summary>Читает поток процесса до конца, не давая заполниться его буферу.</summary>
    private async Task ReadAsync(StreamReader reader, StringBuilder destination)
    {
        try
        {
            string? line;

            while ((line = await reader.ReadLineAsync()) is not null)
            {
                lock (_gate)
                {
                    destination.AppendLine(line);
                }
            }
        }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException)
        {
            // Процесс освобождён при завершении теста: читать больше нечего, и это не ошибка теста.
        }
    }

    /// <summary>Запущен ли процесс прямо сейчас.</summary>
    public bool HasExited => _process.HasExited;

    /// <summary>Код возврата. Осмысленен только после завершения процесса.</summary>
    public int ExitCode => _process.ExitCode;

    /// <summary>Всё, что процесс успел написать в stdout.</summary>
    public string StandardOutput
    {
        get
        {
            lock (_gate)
            {
                return _standardOutput.ToString();
            }
        }
    }

    /// <summary>Всё, что процесс успел написать в stderr.</summary>
    public string StandardError
    {
        get
        {
            lock (_gate)
            {
                return _standardError.ToString();
            }
        }
    }

    /// <summary>Запускает узел на указанном порту.</summary>
    /// <param name="port">Значение <c>RaftBroker__Port</c>; задаётся строкой, чтобы тест мог передать и мусор.</param>
    /// <param name="nodeId">Значение <c>RaftBroker__NodeId</c>.</param>
    /// <param name="peers">Значение <c>RaftBroker__Peers</c>.</param>
    public static NodeProcess Start(string port, string nodeId, string peers)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };

        startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "RaftBroker.Server.dll"));
        startInfo.Environment["RaftBroker__NodeId"] = nodeId;
        startInfo.Environment["RaftBroker__Port"] = port;
        startInfo.Environment["RaftBroker__Peers"] = peers;

        // Шум хоста SDK в stderr не должен попадать в проверяемый вывод узла.
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Процесс узла не запустился.");

        return new NodeProcess(process);
    }

    /// <summary>Ждёт строку лога, содержащую <paramref name="marker"/>.</summary>
    /// <exception cref="TimeoutException">Строка не появилась за отведённое время.</exception>
    public async Task<string> WaitForStandardOutputAsync(string marker, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            var line = StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(candidate => candidate.Contains(marker, StringComparison.Ordinal));

            if (line is not null)
            {
                return line;
            }

            if (_process.HasExited)
            {
                throw new InvalidOperationException(
                    $"Процесс узла завершился с кодом {_process.ExitCode} до появления '{marker}'. stderr: {StandardError}");
            }

            await Task.Delay(50);
        }

        throw new TimeoutException(
            $"За {timeout.TotalSeconds:0} с не появилась строка '{marker}'. stdout: {StandardOutput} stderr: {StandardError}");
    }

    /// <summary>Ждёт завершения процесса. Возвращает код возврата.</summary>
    /// <exception cref="TimeoutException">Процесс не завершился за отведённое время.</exception>
    public async Task<int> WaitForExitAsync(TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);

        try
        {
            await _process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"Процесс узла не завершился за {timeout.TotalSeconds:0} с.");
        }

        return _process.ExitCode;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            // Штатное завершение проверяется отдельно от остановки теста: здесь процесс
            // убивается вместе с потомками, чтобы тест не оставлял за собой слушающий порт.
            _process.Kill(entireProcessTree: true);
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            await _process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // Процесс уже убит: ждать больше нечего.
        }

        _process.Dispose();
    }
}
