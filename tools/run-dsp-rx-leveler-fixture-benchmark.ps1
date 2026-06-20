param(
    [string]$OutputPath = "",

    [switch]$JsonOnly
)

$ErrorActionPreference = "Stop"

$projectPath = Join-Path $PSScriptRoot "rx-leveler-fixture-benchmark\rx-leveler-fixture-benchmark.csproj"
if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "RX leveler fixture benchmark project not found: $projectPath"
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $OutputPath = Join-Path $repoRoot "tmp\wdsp-v2-fixtures\rx-audio-leveler-fixture-benchmark.json"
}

$toolArgs = New-Object System.Collections.Generic.List[string]
$toolArgs.Add("--output-path") | Out-Null
$toolArgs.Add($OutputPath) | Out-Null

if ($JsonOnly) {
    $toolArgs.Add("--json-only") | Out-Null
}

& dotnet run --project $projectPath -- @($toolArgs.ToArray())
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
