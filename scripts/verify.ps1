# Гейт качества (S0-T08).
#
# Локальный эквивалент CI: тот же скрипт запускается в GitHub Actions (.github/workflows/ci.yml),
# поэтому "локально зелено" и "в CI зелено" - это одна и та же проверка, а не две похожие.
#
# Что проверяется:
#   1. restore в locked-режиме - lock-файл должен совпадать с csproj, а не догоняться молча;
#   2. сборка (в Directory.Build.props TreatWarningsAsErrors=true, то есть предупреждения - это ошибки);
#   3. тесты, включая архитектурный тест границ слоёв (tests/RaftBroker.UnitTests/Architecture/LayerReferenceTests.cs);
#   4. форматирование и стиль (dotnet format --verify-no-changes).
#
# Скрипт намеренно не подключает scripts/_common.ps1: там проверки Docker, а гейт
# должен работать на машине без Docker.

[CmdletBinding()]
param(
    # Конфигурация сборки. Release по умолчанию: гейт проверяет то, что поедет в CI.
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot 'RaftBroker.sln'
$totalStopwatch = [System.Diagnostics.Stopwatch]::StartNew()

if (-not (Test-Path -LiteralPath $solution)) {
    throw "Не найдено решение $solution. Скрипт должен лежать в <repo>/scripts."
}

Set-Location -LiteralPath $repoRoot

function Invoke-DotNet {
    <#
        Запускает dotnet и возвращает код возврата.

        $ErrorActionPreference на время вызова переключается в 'Continue': вывод
        предупреждений в stderr (обычное дело у dotnet) при 'Stop' превращается
        в terminating-ошибку раньше, чем скрипт посмотрит на код возврата.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $Arguments
    )

    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'

    try {
        & dotnet @Arguments
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }

    # Код возврата кладётся в переменную скрипта, а не возвращается через return:
    # return вернул бы вместе с ним весь вывод dotnet, и проверка кода сравнивала бы
    # с нулём список строк. Вывод при этом идёт в консоль live, а не копится в памяти.
    $script:lastDotNetExitCode = $LASTEXITCODE
}

function Invoke-GateStep {
    <#
        Выполняет шаг гейта и падает с понятным текстом, если шаг не прошёл.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,

        [Parameter(Mandatory = $true)]
        [string[]] $Arguments
    )

    Write-Host ''
    Write-Host "=== $Name ===" -ForegroundColor Cyan
    Write-Host "> dotnet $($Arguments -join ' ')" -ForegroundColor DarkGray

    $script:lastDotNetExitCode = -1
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    Invoke-DotNet -Arguments $Arguments
    $exitCode = $script:lastDotNetExitCode
    $stopwatch.Stop()

    if ($exitCode -ne 0) {
        throw "Гейт не пройден на шаге '$Name': dotnet $($Arguments -join ' ') вернул код $exitCode."
    }

    Write-Host ("ok: {0} ({1:0.0} с)" -f $Name, $stopwatch.Elapsed.TotalSeconds) -ForegroundColor Green
}

Write-Host 'Гейт качества raft-broker' -ForegroundColor White
Write-Host "решение: $solution"
Write-Host "конфигурация: $Configuration"
Write-Host "dotnet: $(& dotnet --version)"

# --locked-mode: устаревший packages.lock.json должен валить гейт локально так же,
# как он валит сборку в CI (RestoreLockedMode включается переменной CI=true).
Invoke-GateStep -Name 'restore (locked mode)' -Arguments @('restore', $solution, '--locked-mode')

Invoke-GateStep -Name 'build (warnings as errors)' -Arguments @('build', $solution, '--configuration', $Configuration, '--no-restore')

# --solution обязателен: на SDK 10 без него dotnet test падает с "Specifying a solution
# for 'dotnet test' should be via '--solution'" (см. docs/adr/0001-test-stack.md).
# --no-build: сборка уже прошла шагом выше, второй раз она ничего не добавит.
Invoke-GateStep -Name 'tests' -Arguments @('test', '--solution', $solution, '--configuration', $Configuration, '--no-build')

Invoke-GateStep -Name 'format и стиль' -Arguments @('format', $solution, '--verify-no-changes', '--no-restore')

$totalStopwatch.Stop()

Write-Host ''
Write-Host ("Гейт пройден за {0:0.0} с" -f $totalStopwatch.Elapsed.TotalSeconds) -ForegroundColor Green
