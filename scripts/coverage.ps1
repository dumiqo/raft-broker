# Измерение покрытия строк (S1-T06).
#
# Порог 85% - не пожелание, а условие: непокрытый код в хранилище и Raft означает
# непроверенное поведение на диске. Порог проверяется по всему src разом, а не по
# каждому файлу: пустой обработчик отказа может честно жить без теста, но слой,
# который наполовину не покрыт, - нет.
#
# Скрипт запускает тесты ещё раз, уже под инструментом сбора: обычный прогон
# (шаг 'tests' в verify.ps1) остаётся быстрым и не зависит от установленного
# dotnet-coverage, а этот шаг отвечает только за цифру.

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [double] $MinimumLineCoverage = 85,

    # Отчёт в формате Cobertura: его читают и CI-скрипты, и глазами тоже.
    [string] $ReportPath = 'artifacts/coverage/coverage.cobertura.xml'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot 'RaftBroker.sln'

Set-Location -LiteralPath $repoRoot

Write-Host ''
Write-Host '=== покрытие строк ===' -ForegroundColor Cyan

# Локальный инструмент из dotnet-tools.json: версия зафиксирована в репозитории,
# поэтому цифра не зависит от того, что стоит на машине.
dotnet tool restore | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "dotnet tool restore вернул код ${LASTEXITCODE}."
}

$reportDirectory = Split-Path -Parent $ReportPath
if (-not [string]::IsNullOrEmpty($reportDirectory)) {
    New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
}

$testCommand = "dotnet test --solution `"$solution`" --configuration $Configuration --no-build"

dotnet dotnet-coverage collect $testCommand --output-format cobertura --output $ReportPath
if ($LASTEXITCODE -ne 0) {
    throw "dotnet-coverage вернул код ${LASTEXITCODE}: тесты или сбор отчёта не прошли."
}

[xml] $report = Get-Content -LiteralPath $ReportPath -Raw

$sourceRoot = ((Join-Path $repoRoot 'src') -replace '\\', '/') + '/'

# Строки собираются из <line> по всем <class>: Cobertura дублирует одну и ту же
# строку для каждого класса файла, поэтому попадание берётся по максимуму.
$lines = @{}
$prefixes = [System.Collections.Generic.List[string]]::new()

foreach ($package in $report.coverage.packages.package) {
    foreach ($class in $package.classes.class) {
        $fileName = $class.filename
        if (-not $fileName) { continue }

        # Только код самого решения: в отчёт попадают и исходники сторонних пакетов
        # (FsCheck, FSharp.Core), их покрытие к порогу отношения не имеет.
        $normalized = ($fileName -replace '\\', '/')
        if (-not $normalized.StartsWith($sourceRoot, [StringComparison]::Ordinal)) { continue }

        $relative = $normalized.Substring($sourceRoot.Length)
        if ($relative.Contains('/obj/')) { continue }

        $assembly = $relative.Split('/')[0]
        if (-not $prefixes.Contains($assembly)) { $prefixes.Add($assembly) }

        foreach ($line in $class.lines.line) {
            # Ключ - файл и номер строки: одинаковый номер в разных файлах одной сборки
            # это разные строки, и складывать их значило бы занижать цифру.
            $key = "${normalized}:$($line.number)"
            $hits = [int] $line.hits
            if ($lines.ContainsKey($key) -and $lines[$key] -ge $hits) { continue }

            $lines[$key] = $hits
        }
    }
}

if ($lines.Count -eq 0) {
    throw "В отчёте $ReportPath нет строк из src: сбор покрытия не сработал."
}

$covered = 0
foreach ($hits in $lines.Values) {
    if ($hits -gt 0) { $covered++ }
}

$total = $lines.Count
$percent = 100.0 * $covered / $total

# Разбивка по сборкам печатается всегда: по ней видно, где именно просело,
# даже когда общий порог пройден.
foreach ($assembly in $prefixes) {
    $assemblyPrefix = "$sourceRoot$assembly/"
    $assemblyLines = @($lines.Keys | Where-Object { $_.StartsWith($assemblyPrefix, [StringComparison]::Ordinal) })
    $assemblyCovered = @($assemblyLines | Where-Object { $lines[$_] -gt 0 }).Count
    $assemblyPercent = if ($assemblyLines.Count -eq 0) { 0 } else { 100.0 * $assemblyCovered / $assemblyLines.Count }

    $color = if ($assemblyPercent -ge $MinimumLineCoverage) { 'Green' } else { 'Yellow' }
    Write-Host ("  {0,-24} {1,5:0.0}%  ({2}/{3})" -f $assembly, $assemblyPercent, $assemblyCovered, $assemblyLines.Count) -ForegroundColor $color
}

Write-Host ("итого: {0:0.0}% ({1}/{2}), порог {3:0.0}%" -f $percent, $covered, $total, $MinimumLineCoverage)

if ($percent -lt $MinimumLineCoverage) {
    throw "Покрытие строк $(('{0:0.0}' -f $percent))% ниже порога $(('{0:0.0}' -f $MinimumLineCoverage))%. См. $ReportPath."
}
