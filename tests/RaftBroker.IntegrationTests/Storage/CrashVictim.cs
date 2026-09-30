using System.Diagnostics;
using System.Text;

namespace RaftBroker.IntegrationTests.Storage;

/// <summary>
/// Процесс-жертва краш-теста (<c>S1-T05</c>): запускает <c>RaftBroker.CrashHarness.dll</c>,
/// дожидается метки <c>READY</c> и убивает процесс.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="KillAsync"/> - это <c>TerminateProcess</c> на Windows и <c>SIGKILL</c> на Linux:
/// у процесса не остаётся ни одного шанса что-то дописать, сбросить или убрать за собой.
/// Именно поэтому краш-тест проверяет настоящее завершение процесса, а не модель "байты в дубле",
/// которой живут тесты на <c>FakeStorageFileSystem</c> (§9.6).
/// </para>
/// <para>
/// Вывод собирается фоновым читателем: ждать метку, читая поток по строкам, было бы гонкой
/// между строкой и убийством, а заполненный буфер stderr подвесил бы жертву.
/// </para>
/// </remarks>
internal sealed class CrashVictim : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _standardOutput = new();
    private readonly StringBuilder _standardError = new();
    private readonly object _gate = new();

    private CrashVictim(Process process)
    {
        _process = process;

        _ = Task.Run(() => ReadAsync(_process.StandardOutput, _standardOutput));
        _ = Task.Run(() => ReadAsync(_process.StandardError, _standardError));
    }

    /// <summary>Всё, что жертва успела написать в stdout.</summary>
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

    /// <summary>Всё, что жертва успела написать в stderr: там оказываются и её собственные ошибки.</summary>
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

    /// <summary>Запускает жертву с аргументами командной строки.</summary>
    /// <param name="arguments">Режим и его параметры, например <c>torn-frame</c>, каталог и числа.</param>
    public static CrashVictim Start(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };

        startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "RaftBroker.CrashHarness.dll"));

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Шум хоста SDK не должен попадать в проверяемый вывод жертвы.
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Процесс-жертва краш-теста не запустился.");

        return new CrashVictim(process);
    }

    /// <summary>Ждёт строку, содержащую <paramref name="marker"/>.</summary>
    /// <exception cref="TimeoutException">Строка не появилась за отведённое время.</exception>
    public async Task WaitForMarkerAsync(string marker, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (StandardOutput.Contains(marker, StringComparison.Ordinal))
            {
                return;
            }

            if (_process.HasExited)
            {
                throw new InvalidOperationException(
                    $"Жертва завершилась с кодом {_process.ExitCode} до появления '{marker}'. stderr: {StandardError}");
            }

            await Task.Delay(50);
        }

        throw new TimeoutException(
            $"За {timeout.TotalSeconds:0} с не появилась метка '{marker}'. stdout: {StandardOutput} stderr: {StandardError}");
    }

    /// <summary>Убивает процесс вместе с потомками и ждёт его фактической смерти.</summary>
    public async Task KillAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            await _process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("Жертва не умерла за 30 с после Kill.");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            // Уборка теста: жертва не имеет права пережить упавший тест и держать файл открытым.
            _process.Kill(entireProcessTree: true);
        }

        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // Процесс уже убит: ждать больше нечего.
        }

        _process.Dispose();
    }

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
}
