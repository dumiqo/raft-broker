# Останавливает локальный кластер (S0-T07).
#
# Идемпотентен: если кластер не запущен, скрипт просто ничего не делает.
# Данные узлов по умолчанию сохраняются: с S1 у узла появляется диск, и потеря
# данных не должна быть побочным эффектом остановки.

[CmdletBinding()]
param(
    # Удалить данные узлов вместе с контейнерами.
    [switch] $RemoveData
)

. (Join-Path $PSScriptRoot '_common.ps1')

Assert-DockerDaemon

$composeArguments = [System.Collections.Generic.List[string]]::new()
$composeArguments.Add('down')
# --remove-orphans убирает контейнеры от прежних версий compose-файла, иначе они
# продолжают занимать порты и мешают следующему запуску.
$composeArguments.Add('--remove-orphans')

if ($RemoveData) {
    $composeArguments.Add('--volumes')
}

Invoke-RaftCompose -ComposeArguments $composeArguments.ToArray()

if ($RemoveData) {
    Write-Host 'Кластер остановлен, данные узлов удалены.' -ForegroundColor Yellow
}
else {
    Write-Host 'Кластер остановлен, данные узлов сохранены.' -ForegroundColor Green
}
