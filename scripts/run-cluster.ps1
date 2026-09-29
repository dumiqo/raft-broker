# Поднимает локальный кластер из трёх узлов (S0-T07).
#
# Идемпотентен: если кластер уже поднят, повторный запуск ничего не ломает
# и не оставляет дубликатов контейнеров.

[CmdletBinding()]
param(
    # Пересобрать образ перед запуском (полезно после правок в коде узла).
    [switch] $Rebuild
)

. (Join-Path $PSScriptRoot '_common.ps1')

Assert-DockerDaemon

$composeArguments = [System.Collections.Generic.List[string]]::new()
$composeArguments.Add('up')
$composeArguments.Add('--detach')
# --wait ждёт, пока контейнеры станут healthy: без этого скрипт возвращает управление
# раньше, чем узел успел занять порт, и проверка "три процесса поднялись"
# проверяла бы только факт создания контейнеров.
$composeArguments.Add('--wait')

if ($Rebuild) {
    $composeArguments.Add('--build')
}

Invoke-RaftCompose -ComposeArguments $composeArguments.ToArray()

Invoke-RaftCompose -ComposeArguments @('ps')

Write-Host ''
Write-Host 'Последняя строка лога каждого узла:' -ForegroundColor Green

foreach ($service in @('node-a', 'node-b', 'node-c')) {
    Invoke-RaftCompose -ComposeArguments @('logs', '--tail', '1', $service)
}

Write-Host ''
Write-Host 'Полные логи: docker compose -f deploy/docker-compose.yml logs -f'
Write-Host 'Остановка:   pwsh scripts/stop-cluster.ps1'
