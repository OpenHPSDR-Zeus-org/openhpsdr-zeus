param(
    [string]$BundleDir = "",

    [string]$BenchmarkPlanPath = "",

    [string]$MetricCatalogPath = "",

    [string]$MetricsPath = "",

    [string]$AudioIndexPath = "",

    [string]$SpectrumIndexPath = "",

    [string]$RuntimeAuditPath = "",

    [string]$ComparisonReportPath = "",

    [string]$ComparisonMarkdownPath = "",

    [string]$SummaryPath = "",

    [string[]]$ScenarioIds = @(),

    [string[]]$ComparisonIds = @("off-baseline", "thetis-parity", "current-zeus", "candidate-under-test", "nr5-spnr"),

    [switch]$IncludeNonFixtureScenarios,

    [switch]$AllowRegression,

    [switch]$AllowRuntimeAuditPreflight,

    [switch]$NoMarkdown,

    [switch]$Force,

    [switch]$JsonOnly
)

$ErrorActionPreference = "Stop"

function Resolve-RepoPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) { return $Path }
    return Join-Path (Get-Location).Path $Path
}

function Resolve-BundlePath {
    param(
        [Parameter(Mandatory = $true)][string]$BundlePath,
        [Parameter(Mandatory = $true)][string]$Path
    )

    if ([System.IO.Path]::IsPathRooted($Path)) { return $Path }
    return Join-Path $BundlePath $Path
}

function Read-JsonFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    try {
        return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        throw "Failed to parse JSON file '$Path': $($_.Exception.Message)"
    }
}

function Write-JsonFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Value
    )

    $parent = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }

    $Value | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $Path -Encoding UTF8
}

function Get-JsonValue {
    param(
        $Object,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-JsonArray {
    param(
        $Object,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $value = Get-JsonValue $Object $Name
    if ($null -eq $value) { return @() }
    if ($value -is [System.Array]) { return @($value) }
    return @($value)
}

function Test-Truthy {
    param($Value)

    if ($null -eq $Value) { return $false }
    if ($Value -is [bool]) { return $Value }
    return [bool]$Value
}

function Add-CliArg {
    param(
        [System.Collections.Generic.List[string]]$Arguments,
        [Parameter(Mandatory = $true)][string]$Name,
        [string]$Value = ""
    )

    $Arguments.Add($Name) | Out-Null
    if (-not [string]::IsNullOrWhiteSpace($Value)) {
        $Arguments.Add($Value) | Out-Null
    }
}

function Add-CliValues {
    param(
        [System.Collections.Generic.List[string]]$Arguments,
        [Parameter(Mandatory = $true)][string]$Name,
        [string[]]$Values
    )

    $items = @($Values | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
    if ($items.Count -eq 0) { return }

    $Arguments.Add($Name) | Out-Null
    foreach ($item in $items) {
        $Arguments.Add([string]$item) | Out-Null
    }
}

function Invoke-ToolScript {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [switch]$Quiet
    )

    if ($Quiet) {
        & powershell -NoProfile -ExecutionPolicy Bypass -File $ScriptPath @Arguments | Out-Null
    }
    else {
        & powershell -NoProfile -ExecutionPolicy Bypass -File $ScriptPath @Arguments
    }
    if ($LASTEXITCODE -ne 0) {
        throw "Tool failed with exit code ${LASTEXITCODE}: $ScriptPath"
    }
}

function Normalize-Hash {
    param($Value)

    return ([string]$Value).Trim().ToLowerInvariant()
}

$bundlePath = if ([string]::IsNullOrWhiteSpace($BundleDir)) {
    (Get-Location).Path
}
else {
    Resolve-RepoPath $BundleDir
}
$bundlePath = [System.IO.Path]::GetFullPath($bundlePath)
if (-not (Test-Path -LiteralPath $bundlePath -PathType Container)) {
    throw "Bundle directory not found: $bundlePath"
}

if ([string]::IsNullOrWhiteSpace($BenchmarkPlanPath)) {
    $BenchmarkPlanPath = Join-Path $bundlePath "benchmark-plan.json"
}
else {
    $BenchmarkPlanPath = Resolve-BundlePath $bundlePath $BenchmarkPlanPath
}

if ([string]::IsNullOrWhiteSpace($MetricCatalogPath)) {
    $MetricCatalogPath = Join-Path $bundlePath "benchmark-metric-catalog.json"
}
else {
    $MetricCatalogPath = Resolve-BundlePath $bundlePath $MetricCatalogPath
}

if ([string]::IsNullOrWhiteSpace($MetricsPath)) {
    $MetricsPath = Join-Path $bundlePath "artifacts\offline-fixture-metrics.json"
}
else {
    $MetricsPath = Resolve-BundlePath $bundlePath $MetricsPath
}

if ([string]::IsNullOrWhiteSpace($AudioIndexPath)) {
    $AudioIndexPath = Join-Path $bundlePath "artifacts\audio-render-before-after.json"
}
else {
    $AudioIndexPath = Resolve-BundlePath $bundlePath $AudioIndexPath
}

if ([string]::IsNullOrWhiteSpace($SpectrumIndexPath)) {
    $SpectrumIndexPath = Join-Path $bundlePath "artifacts\spectrum-before-after.json"
}
else {
    $SpectrumIndexPath = Resolve-BundlePath $bundlePath $SpectrumIndexPath
}

if ([string]::IsNullOrWhiteSpace($RuntimeAuditPath)) {
    $RuntimeAuditPath = Join-Path $bundlePath "artifacts\wdsp-runtime-artifact-audit.json"
}
else {
    $RuntimeAuditPath = Resolve-BundlePath $bundlePath $RuntimeAuditPath
}

if ([string]::IsNullOrWhiteSpace($ComparisonReportPath)) {
    $ComparisonReportPath = Join-Path $bundlePath "artifacts\dsp-fixture-metric-comparison.json"
}
else {
    $ComparisonReportPath = Resolve-BundlePath $bundlePath $ComparisonReportPath
}

if (-not $NoMarkdown -and [string]::IsNullOrWhiteSpace($ComparisonMarkdownPath)) {
    $ComparisonMarkdownPath = Join-Path $bundlePath "artifacts\dsp-fixture-metric-comparison.md"
}
elseif (-not [string]::IsNullOrWhiteSpace($ComparisonMarkdownPath)) {
    $ComparisonMarkdownPath = Resolve-BundlePath $bundlePath $ComparisonMarkdownPath
}

if ([string]::IsNullOrWhiteSpace($SummaryPath)) {
    $SummaryPath = Join-Path $bundlePath "artifacts\wdsp-fixture-matrix-summary.json"
}
else {
    $SummaryPath = Resolve-BundlePath $bundlePath $SummaryPath
}

if (-not (Test-Path -LiteralPath $BenchmarkPlanPath -PathType Leaf)) {
    throw "Benchmark plan not found: $BenchmarkPlanPath"
}

$scriptRoot = $PSScriptRoot
$fixtureScript = Join-Path $scriptRoot "run-dsp-wdsp-fixture-evidence.ps1"
$runtimeAuditScript = Join-Path $scriptRoot "audit-wdsp-runtime-artifacts.ps1"
$comparisonScript = Join-Path $scriptRoot "compare-dsp-fixture-metrics.ps1"
foreach ($script in @($fixtureScript, $runtimeAuditScript, $comparisonScript)) {
    if (-not (Test-Path -LiteralPath $script -PathType Leaf)) {
        throw "Required tool script not found: $script"
    }
}

$startedUtc = [DateTimeOffset]::UtcNow

$fixtureArgs = New-Object System.Collections.Generic.List[string]
Add-CliArg $fixtureArgs "-BundleDir" $bundlePath
Add-CliArg $fixtureArgs "-BenchmarkPlanPath" $BenchmarkPlanPath
if (Test-Path -LiteralPath $MetricCatalogPath -PathType Leaf) {
    Add-CliArg $fixtureArgs "-MetricCatalogPath" $MetricCatalogPath
}
Add-CliArg $fixtureArgs "-MetricsPath" $MetricsPath
Add-CliArg $fixtureArgs "-AudioIndexPath" $AudioIndexPath
Add-CliArg $fixtureArgs "-SpectrumIndexPath" $SpectrumIndexPath
Add-CliValues $fixtureArgs "-ScenarioIds" $ScenarioIds
Add-CliValues $fixtureArgs "-ComparisonIds" $ComparisonIds
if ($IncludeNonFixtureScenarios) { Add-CliArg $fixtureArgs "-IncludeNonFixtureScenarios" }
if ($Force) { Add-CliArg $fixtureArgs "-Force" }
if ($JsonOnly) { Add-CliArg $fixtureArgs "-JsonOnly" }
Invoke-ToolScript -ScriptPath $fixtureScript -Arguments @($fixtureArgs.ToArray()) -Quiet:$JsonOnly

$runtimeArgs = New-Object System.Collections.Generic.List[string]
Add-CliArg $runtimeArgs "-ReportPath" $RuntimeAuditPath
if (-not $AllowRuntimeAuditPreflight) {
    Add-CliArg $runtimeArgs "-FailOnMissingWinX64Nr5"
}
if ($JsonOnly) { Add-CliArg $runtimeArgs "-JsonOnly" }
Invoke-ToolScript -ScriptPath $runtimeAuditScript -Arguments @($runtimeArgs.ToArray()) -Quiet:$JsonOnly

$comparisonArgs = New-Object System.Collections.Generic.List[string]
Add-CliArg $comparisonArgs "-BundleDir" $bundlePath
Add-CliArg $comparisonArgs "-MetricsPath" $MetricsPath
Add-CliArg $comparisonArgs "-BenchmarkPlanPath" $BenchmarkPlanPath
if (Test-Path -LiteralPath $MetricCatalogPath -PathType Leaf) {
    Add-CliArg $comparisonArgs "-MetricCatalogPath" $MetricCatalogPath
}
Add-CliArg $comparisonArgs "-ReportPath" $ComparisonReportPath
if ($NoMarkdown) {
    Add-CliArg $comparisonArgs "-NoMarkdown"
}
else {
    Add-CliArg $comparisonArgs "-MarkdownPath" $ComparisonMarkdownPath
}
if ($IncludeNonFixtureScenarios) { Add-CliArg $comparisonArgs "-IncludeNonFixtureScenarios" }
if (-not $AllowRegression) { Add-CliArg $comparisonArgs "-FailOnRegression" }
if ($JsonOnly) { Add-CliArg $comparisonArgs "-JsonOnly" }
Invoke-ToolScript -ScriptPath $comparisonScript -Arguments @($comparisonArgs.ToArray()) -Quiet:$JsonOnly

$metrics = Read-JsonFile $MetricsPath
$runtimeAudit = Read-JsonFile $RuntimeAuditPath
$comparison = Read-JsonFile $ComparisonReportPath

$sourceRuntimeSha = Normalize-Hash (Get-JsonValue $metrics "wdspRuntimeSha256")
$comparisonRuntimeSha = Normalize-Hash (Get-JsonValue $comparison "wdspRuntimeSha256")
$auditRuntimeSha = Normalize-Hash (Get-JsonValue $runtimeAudit "winX64NativeSha256")
$runtimeHashStatus = if ([string]::IsNullOrWhiteSpace($sourceRuntimeSha) -or [string]::IsNullOrWhiteSpace($comparisonRuntimeSha)) {
    "missing"
}
elseif (-not [string]::Equals($sourceRuntimeSha, $comparisonRuntimeSha, [StringComparison]::OrdinalIgnoreCase)) {
    "source-comparison-mismatch"
}
elseif (-not [string]::IsNullOrWhiteSpace($auditRuntimeSha) -and
    -not [string]::Equals($sourceRuntimeSha, $auditRuntimeSha, [StringComparison]::OrdinalIgnoreCase)) {
    "runtime-audit-mismatch"
}
else {
    "match"
}

$completedUtc = [DateTimeOffset]::UtcNow
$summary = [ordered]@{
    schemaVersion = 1
    tool = "run-dsp-wdsp-fixture-matrix"
    generatedUtc = $completedUtc
    startedUtc = $startedUtc
    completedUtc = $completedUtc
    bundleDir = $bundlePath
    benchmarkPlanPath = $BenchmarkPlanPath
    metricCatalogPath = if (Test-Path -LiteralPath $MetricCatalogPath -PathType Leaf) { $MetricCatalogPath } else { $null }
    metricsPath = $MetricsPath
    audioIndexPath = $AudioIndexPath
    spectrumIndexPath = $SpectrumIndexPath
    runtimeAuditPath = $RuntimeAuditPath
    comparisonReportPath = $ComparisonReportPath
    comparisonMarkdownPath = if ($NoMarkdown) { $null } else { $ComparisonMarkdownPath }
    summaryPath = $SummaryPath
    fixtureScenarioScope = [string](Get-JsonValue $metrics "fixtureScenarioScope")
    scenarioCount = [int](Get-JsonValue $metrics "scenarioCount")
    comparisonIds = @(Get-JsonArray $metrics "comparisonIds")
    skippedNonFixtureScenarioIds = @(Get-JsonArray $metrics "skippedNonFixtureScenarioIds")
    evidenceEngine = [string](Get-JsonValue $metrics "evidenceEngine")
    wdspRuntimeRid = [string](Get-JsonValue $metrics "wdspRuntimeRid")
    wdspRuntimeStatus = [string](Get-JsonValue $metrics "wdspRuntimeStatus")
    wdspRuntimeSha256 = $sourceRuntimeSha
    runtimeAuditReadyForWinX64Package = Test-Truthy (Get-JsonValue $runtimeAudit "readyForWinX64Package")
    runtimeAuditWinX64NativeSha256 = $auditRuntimeSha
    runtimeHashStatus = $runtimeHashStatus
    metricComparisonReady = Test-Truthy (Get-JsonValue $comparison "readyForReview")
    metricCoverageReadyForReview = Test-Truthy (Get-JsonValue $comparison "metricCoverageReadyForReview")
    metricComparisonWdspBackedEvidence = Test-Truthy (Get-JsonValue $comparison "wdspBackedEvidence")
    metricComparisonSourceMetricsHash = [string](Get-JsonValue $comparison "metricsSha256")
    metricComparisonRuntimeSha256 = $comparisonRuntimeSha
    regressionCount = [int](Get-JsonValue $comparison "regressionCount")
    gateFailureCount = [int](Get-JsonValue $comparison "gateFailureCount")
    missingScenarioCount = [int](Get-JsonValue $comparison "missingScenarioCount")
    missingCurrentBaselineCount = [int](Get-JsonValue $comparison "missingCurrentBaselineCount")
    missingThetisBaselineCount = [int](Get-JsonValue $comparison "missingThetisBaselineCount")
    missingCandidateCount = [int](Get-JsonValue $comparison "missingCandidateCount")
    missingMetricValueCount = [int](Get-JsonValue $comparison "missingMetricValueCount")
    readyForOfflineReview = ((Test-Truthy (Get-JsonValue $comparison "readyForReview")) -and
        (Test-Truthy (Get-JsonValue $runtimeAudit "readyForWinX64Package")) -and
        [string]::Equals($runtimeHashStatus, "match", [StringComparison]::OrdinalIgnoreCase))
    acceptanceLimitations = @(
        "Offline WDSP fixture evidence does not prove G2, on-air, TX/PureSignal, or cross-radio acceptance.",
        "No default DSP behavior should change until strict bundle validation, G2 evidence, and cross-radio evidence pass.",
        "External DSP/ML engines remain opt-in post-demod candidates unless benchmark evidence proves raw WDSP IQ safety."
    )
}

Write-JsonFile -Path $SummaryPath -Value $summary

if ($JsonOnly) {
    $summary | ConvertTo-Json -Depth 32
}
else {
    Write-Host "WDSP fixture matrix complete."
    Write-Host "Metrics: $MetricsPath"
    Write-Host "Comparison: $ComparisonReportPath"
    Write-Host "Runtime audit: $RuntimeAuditPath"
    Write-Host "Summary: $SummaryPath"
    Write-Host "Ready for offline review: $($summary.readyForOfflineReview)"
    Write-Host "Runtime hash status: $runtimeHashStatus"
}

if (-not [bool]$summary.readyForOfflineReview -and -not $AllowRegression -and -not $AllowRuntimeAuditPreflight) {
    exit 1
}
