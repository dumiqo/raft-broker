# Общие части скриптов кластера (S0-T07).
#
# Подключается через точку и предполагает, что вызывающий скрипт лежит в scripts/:
#   . (Join-Path $PSScriptRoot '_common.ps1')
# Пути вычисляются один раз при подключении, а не внутри функций: при вызове функции
# $PSScriptRoot уже указывает на каталог вызывающего скрипта.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RaftBrokerRepoRoot = Split-Path -Parent $PSScriptRoot
$RaftBrokerComposeFile = Join-Path $RaftBrokerRepoRoot 'deploy\docker-compose.yml'

function Test-DockerCommand {
    <#
        Проверяет, отрабатывает ли команда docker.

        $ErrorActionPreference переключается только внутри этой функции: stderr docker'а
        в PowerShell - это поток ошибок, и при 'Stop' вместе с 2>&1 он превращается
        в terminating-ошибку раньше, чем скрипт успевает посмотреть $LASTEXITCODE.
        Именно так сторож сам ломался вместо того, чтобы объяснить причину.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $Arguments
    )

    $ErrorActionPreference = 'SilentlyContinue'
    $null = & docker @Arguments 2>&1

    return $LASTEXITCODE -eq 0
}

function Assert-DockerDaemon {
    <#
        Проверяет, что Docker вообще работоспособен, до запуска команд compose.
        Без этого шага первая же команда падает сообщением про named pipe, из которого
        не следует, что делать.
    #>
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        Write-Host 'Не найдена команда docker. Установите Docker Desktop и повторите.' -ForegroundColor Red
        exit 1
    }

    if (-not (Test-DockerCommand -Arguments @('info'))) {
        Write-Host 'Docker daemon недоступен. Запустите Docker Desktop и повторите.' -ForegroundColor Red
        exit 1
    }

    if (-not (Test-DockerCommand -Arguments @('compose', 'version'))) {
        Write-Host 'Не найден плагин docker compose (v2). Скрипты используют формат "docker compose", а не "docker-compose".' -ForegroundColor Red
        exit 1
    }
}

function Invoke-RaftCompose {
    <#
        Запускает docker compose с общим файлом кластера и падает при ненулевом коде возврата.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $ComposeArguments
    )

    Write-Host "> docker compose -f deploy/docker-compose.yml $($ComposeArguments -join ' ')" -ForegroundColor DarkGray

    & docker compose -f $RaftBrokerComposeFile @ComposeArguments

    if ($LASTEXITCODE -ne 0) {
        throw "docker compose $($ComposeArguments -join ' ') завершился с кодом $LASTEXITCODE"
    }
}
