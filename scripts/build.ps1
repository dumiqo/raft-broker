# Сборка образа узла (S0-T07).
#
# Идемпотентен: повторный запуск без изменений в коде переиспользует слои Docker
# и завершается успешно.

[CmdletBinding()]
param(
    # Пересобрать без кэша слоёв: нужно, когда подозревается устаревший базовый слой.
    [switch] $NoCache
)

. (Join-Path $PSScriptRoot '_common.ps1')

Assert-DockerDaemon

$composeArguments = [System.Collections.Generic.List[string]]::new()
$composeArguments.Add('build')
$composeArguments.Add('--quiet')

if ($NoCache) {
    $composeArguments.Add('--no-cache')
}

Invoke-RaftCompose -ComposeArguments $composeArguments.ToArray()

Write-Host 'Образ узла собран: raft-broker-node:local' -ForegroundColor Green
Write-Host 'Запуск кластера: pwsh scripts/run-cluster.ps1'
