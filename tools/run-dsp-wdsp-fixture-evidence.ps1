param(
    [string]$BundleDir = "",

    [string]$BenchmarkPlanPath = "",

    [string]$MetricCatalogPath = "",

    [string]$MetricsPath = "",

    [string]$AudioIndexPath = "",

    [string]$SpectrumIndexPath = "",

    [string[]]$ScenarioIds = @(),

    [string[]]$ComparisonIds = @("off-baseline", "thetis-parity", "current-zeus", "candidate-under-test", "candidate-external-engine-opt-in"),

    [switch]$IncludeNonFixtureScenarios,

    [switch]$Force,

    [switch]$JsonOnly
)

$ErrorActionPreference = "Stop"

$projectPath = Join-Path $PSScriptRoot "dsp-fixture-evidence\dsp-fixture-evidence.csproj"
if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "WDSP fixture evidence project not found: $projectPath"
}

$toolArgs = New-Object System.Collections.Generic.List[string]

function Add-ToolArg {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [string]$Value = ""
    )

    $toolArgs.Add($Name) | Out-Null
    if (-not [string]::IsNullOrWhiteSpace($Value)) {
        $toolArgs.Add($Value) | Out-Null
    }
}

if (-not [string]::IsNullOrWhiteSpace($BundleDir)) {
    Add-ToolArg "--bundle-dir" $BundleDir
}
if (-not [string]::IsNullOrWhiteSpace($BenchmarkPlanPath)) {
    Add-ToolArg "--benchmark-plan-path" $BenchmarkPlanPath
}
if (-not [string]::IsNullOrWhiteSpace($MetricCatalogPath)) {
    Add-ToolArg "--metric-catalog-path" $MetricCatalogPath
}
if (-not [string]::IsNullOrWhiteSpace($MetricsPath)) {
    Add-ToolArg "--metrics-path" $MetricsPath
}
if (-not [string]::IsNullOrWhiteSpace($AudioIndexPath)) {
    Add-ToolArg "--audio-index-path" $AudioIndexPath
}
if (-not [string]::IsNullOrWhiteSpace($SpectrumIndexPath)) {
    Add-ToolArg "--spectrum-index-path" $SpectrumIndexPath
}

foreach ($scenarioId in $ScenarioIds) {
    if (-not [string]::IsNullOrWhiteSpace($scenarioId)) {
        Add-ToolArg "--scenario-id" $scenarioId
    }
}
foreach ($comparisonId in $ComparisonIds) {
    if (-not [string]::IsNullOrWhiteSpace($comparisonId)) {
        Add-ToolArg "--comparison-id" $comparisonId
    }
}

if ($IncludeNonFixtureScenarios) {
    Add-ToolArg "--include-non-fixture-scenarios"
}
if ($Force) {
    Add-ToolArg "--force"
}
if ($JsonOnly) {
    Add-ToolArg "--json-only"
}

& dotnet run --project $projectPath -- @($toolArgs.ToArray())
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
