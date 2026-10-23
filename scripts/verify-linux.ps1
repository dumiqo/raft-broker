# Linux-прогон гейта в контейнере (S1-T11 follow-up, ADR 0009).
#
# Зачем отдельный скрипт: переносимость доказывается не матрицей CI, а двумя прогонами,
# которые владелец запускает сам - Windows-хост (`verify.ps1`) и Linux-контейнер (этот
# скрипт). CI остаётся оповещением: он запускается только по пушу в main, то есть уже
# после ревью-гейта, и доказательством считаться не может.
#
# Скрипт не повторяет шаги гейта: внутри контейнера выполняется тот же scripts/verify.ps1.
# Отличаются только среда (образ с SDK 10 и рантаймом 8.0, см. gate.Dockerfile) и оболочка
# (pwsh на Linux вместо Windows PowerShell 5.1).
#
# Что монтируется:
#   * рабочее дерево репозитория - целиком, поэтому отчёт покрытия остаётся в
#     artifacts/coverage/ на хосте, а не исчезает вместе с контейнером;
#   * именованный том под ~/.nuget/packages - иначе каждый прогон качает пакеты заново.
#
# Побочный эффект: сборка внутри контейнера пишет bin/ и obj/ в рабочее дерево от имени
# root. Каталоги в .gitignore, а на Windows-хосте владелец не важен. Если это мешает,
# прогоняйте на чистом клоне.
#
# Скрипт намеренно не подключает scripts/_common.ps1 целиком: там проверка плагина
# docker compose, который контейнерному прогону не нужен.

[CmdletBinding()]
param(
    # Та же конфигурация, что у verify.ps1: гейт проверяет то, что поедет в CI.
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    # Образ собирается из gate.Dockerfile: слой с рантаймом 8.0 кэшируется докером.
    [string] $ImageTag = 'raft-broker-gate:linux',

    # Пустое значение по умолчанию, а не Join-Path в param(): в Windows PowerShell 5.1
    # $PSScriptRoot при связывании параметров ещё не заполнен, и выражение падает.
    [string] $Dockerfile,

    [string] $NuGetVolume = 'raft-broker-nuget'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrEmpty($Dockerfile)) {
    $Dockerfile = Join-Path $PSScriptRoot 'gate.Dockerfile'
}
$solution = Join-Path $repoRoot 'RaftBroker.sln'
$totalStopwatch = [System.Diagnostics.Stopwatch]::StartNew()

if (-not (Test-Path -LiteralPath $solution)) {
    throw "Не найдено решение $solution. Скрипт должен лежать в <repo>/scripts."
}

if (-not (Test-Path -LiteralPath $Dockerfile)) {
    throw "Не найден $Dockerfile - образ гейта собирать не из чего."
}

function Invoke-Docker {
    <#
        Запускает docker и возвращает код возврата.

        $ErrorActionPreference на время вызова переключается в 'Continue' по той же
        причине, что и в verify.ps1: docker пишет обычные сообщения в stderr, и при
        'Stop' это terminating-ошибка раньше, чем скрипт посмотрит на код.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $Arguments
    )

    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'

    try {
        & docker @Arguments
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }

    $script:lastDockerExitCode = $LASTEXITCODE
}

# Docker проверяется до сборки образа: без демона первая же команда падает сообщением
# про named pipe, из которого не следует, что делать.
Invoke-Docker -Arguments @('info')
if ($script:lastDockerExitCode -ne 0) {
    throw 'Docker daemon недоступен. Запустите Docker Desktop и повторите.'
}

Write-Host 'Гейт качества raft-broker: Linux-прогон в контейнере' -ForegroundColor White
Write-Host "решение: $solution"
Write-Host "конфигурация: $Configuration"
Write-Host "образ: $ImageTag (из $Dockerfile)"

Write-Host ''
Write-Host '=== сборка образа гейта ===' -ForegroundColor Cyan
Invoke-Docker -Arguments @('build', '--file', $Dockerfile, '--tag', $ImageTag, $PSScriptRoot)
if ($script:lastDockerExitCode -ne 0) {
    throw "Сборка образа $ImageTag не удалась: docker build вернул код $script:lastDockerExitCode."
}

Write-Host ''
Write-Host '=== гейт в контейнере ===' -ForegroundColor Cyan
Write-Host "> docker run --rm $ImageTag pwsh -File scripts/verify.ps1 -Configuration $Configuration" -ForegroundColor DarkGray

# CI=true включает RestoreLockedMode (Directory.Build.props): устаревший lock-файл должен
# валить и контейнерный прогон тоже, иначе он тихо обновит пакеты и разойдётся с CI.
Invoke-Docker -Arguments @(
    'run', '--rm',
    '--env', 'CI=true',
    '--env', 'DOTNET_NOLOGO=true',
    '--env', 'DOTNET_CLI_TELEMETRY_OPTOUT=true',
    '--env', 'DOTNET_SKIP_FIRST_TIME_EXPERIENCE=true',
    '--volume', "${repoRoot}:/work",
    '--volume', "${NuGetVolume}:/root/.nuget/packages",
    '--workdir', '/work',
    $ImageTag,
    'pwsh', '-NoProfile', '-NonInteractive', '-File', 'scripts/verify.ps1', '-Configuration', $Configuration
)

if ($script:lastDockerExitCode -ne 0) {
    throw "Гейт в Linux-контейнере не пройден: docker run вернул код $script:lastDockerExitCode."
}

$totalStopwatch.Stop()

Write-Host ''
Write-Host ("Гейт пройден в Linux-контейнере за {0:0.0} с" -f $totalStopwatch.Elapsed.TotalSeconds) -ForegroundColor Green
Write-Host "отчёт покрытия: $(Join-Path $repoRoot 'artifacts\coverage\coverage.cobertura.xml')"
