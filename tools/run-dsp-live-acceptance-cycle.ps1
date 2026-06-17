param(
    [string]$BaseUrl = "http://localhost:6060",

    [string]$BundleDir = "",

    [string[]]$ScenarioIds = @(),

    [int]$Samples = 60,

    [int]$IntervalMs = 1000,

    [int]$TimeoutSec = 5,

    [string]$OffBaselineComparisonId = "off-baseline",

    [string]$ThetisComparisonId = "thetis-parity",

    [string]$BaselineComparisonId = "current-zeus",

    [string]$CandidateComparisonId = "nr5-spnr",

    [string]$SummaryPath = "",

    [switch]$PlanOnly,

    [switch]$SkipCertificateCheck,

    [switch]$AllowCapturePreflight,

    [switch]$AllowRegression,

    [switch]$AllowValidationPreflight,

    [switch]$JsonOnly
)

$ErrorActionPreference = "Stop"

function Get-RepoRoot {
    return (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
}

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

function Normalize-BaseUrl {
    param([Parameter(Mandatory = $true)][string]$Url)
    return $Url.TrimEnd("/")
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

function Read-OptionalJsonFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $null
    }

    return Read-JsonFile $Path
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

    $Value | ConvertTo-Json -Depth 64 | Set-Content -LiteralPath $Path -Encoding UTF8
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

function ConvertTo-PortablePath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return ""
    }

    try {
        $rootFull = [System.IO.Path]::GetFullPath($Root)
        $pathFull = [System.IO.Path]::GetFullPath($Path)
        $relative = [System.IO.Path]::GetRelativePath($rootFull, $pathFull)
        if (-not $relative.StartsWith("..")) {
            return ($relative -replace "\\", "/")
        }
    }
    catch {
        # Fall through to the original value if the path cannot be normalized.
    }

    return $Path
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
        [switch]$Quiet,
        [switch]$AllowNonZeroExit
    )

    $output = & powershell -NoProfile -ExecutionPolicy Bypass -File $ScriptPath @Arguments 2>&1
    if (-not $Quiet) {
        foreach ($line in @($output)) {
            Write-Host $line
        }
    }

    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0 -and -not $AllowNonZeroExit) {
        throw "Tool failed with exit code ${exitCode}: $ScriptPath"
    }

    return $exitCode
}

function Get-AcceptanceArtifacts {
    return @(
        "artifacts/live-diagnostics-trace-index.off-baseline.json",
        "artifacts/live-diagnostics-matrix-report.off-baseline.json",
        "artifacts/live-diagnostics-trace-index.thetis-parity.json",
        "artifacts/live-diagnostics-matrix-report.thetis-parity.json",
        "artifacts/live-diagnostics-trace-index.baseline.json",
        "artifacts/live-diagnostics-matrix-report.baseline.json",
        "artifacts/live-diagnostics-trace-index.candidate.json",
        "artifacts/live-diagnostics-matrix-report.candidate.json",
        "artifacts/live-diagnostics-history.json",
        "artifacts/live-diagnostics-trace-comparison.json",
        "artifacts/live-diagnostics-trace-comparison.thetis-parity.json",
        "artifact-manifest.json",
        "validation-report.json",
        "validation-triage-report.json",
        "validation-triage-report.md",
        "artifacts/live-acceptance-cycle-summary.json"
    )
}

function Get-PlanCommandSteps {
    param(
        [Parameter(Mandatory = $true)][string]$BundleReference,
        [Parameter(Mandatory = $true)][string]$Base,
        [Parameter(Mandatory = $true)][string]$OffComparison,
        [Parameter(Mandatory = $true)][string]$ThetisComparison,
        [Parameter(Mandatory = $true)][string]$BaselineComparison,
        [Parameter(Mandatory = $true)][string]$CandidateComparison,
        [string]$ScenarioArgument = "",
        [int]$SampleCount,
        [int]$SampleIntervalMs
    )

    return @(
        "powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-live-diagnostics-matrix.ps1 -BaseUrl $Base -BundleDir $BundleReference$ScenarioArgument -ComparisonId $OffComparison -IndexPath $BundleReference\artifacts\live-diagnostics-trace-index.off-baseline.json -ReportPath $BundleReference\artifacts\live-diagnostics-matrix-report.off-baseline.json -Samples $SampleCount -IntervalMs $SampleIntervalMs",
        "powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-live-diagnostics-matrix.ps1 -BaseUrl $Base -BundleDir $BundleReference$ScenarioArgument -ComparisonId $ThetisComparison -IndexPath $BundleReference\artifacts\live-diagnostics-trace-index.thetis-parity.json -ReportPath $BundleReference\artifacts\live-diagnostics-matrix-report.thetis-parity.json -Samples $SampleCount -IntervalMs $SampleIntervalMs",
        "powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-live-diagnostics-matrix.ps1 -BaseUrl $Base -BundleDir $BundleReference$ScenarioArgument -ComparisonId $BaselineComparison -IndexPath $BundleReference\artifacts\live-diagnostics-trace-index.baseline.json -ReportPath $BundleReference\artifacts\live-diagnostics-matrix-report.baseline.json -Samples $SampleCount -IntervalMs $SampleIntervalMs",
        "powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-live-diagnostics-matrix.ps1 -BaseUrl $Base -BundleDir $BundleReference$ScenarioArgument -ComparisonId $CandidateComparison -IndexPath $BundleReference\artifacts\live-diagnostics-trace-index.candidate.json -ReportPath $BundleReference\artifacts\live-diagnostics-matrix-report.candidate.json -Samples $SampleCount -IntervalMs $SampleIntervalMs",
        "powershell -NoProfile -ExecutionPolicy Bypass -File tools\summarize-dsp-live-diagnostics-history.ps1 -BundleDir $BundleReference -ReportPath $BundleReference\artifacts\live-diagnostics-history.json",
        "powershell -NoProfile -ExecutionPolicy Bypass -File tools\compare-dsp-live-diagnostics-matrix.ps1 -BundleDir $BundleReference -BaselineIndexPath $BundleReference\artifacts\live-diagnostics-trace-index.baseline.json -CandidateIndexPath $BundleReference\artifacts\live-diagnostics-trace-index.candidate.json -BaselineComparisonId $BaselineComparison -CandidateComparisonId $CandidateComparison -ReportPath $BundleReference\artifacts\live-diagnostics-trace-comparison.json -FailOnRegression",
        "powershell -NoProfile -ExecutionPolicy Bypass -File tools\compare-dsp-live-diagnostics-matrix.ps1 -BundleDir $BundleReference -BaselineIndexPath $BundleReference\artifacts\live-diagnostics-trace-index.thetis-parity.json -CandidateIndexPath $BundleReference\artifacts\live-diagnostics-trace-index.candidate.json -BaselineComparisonId $ThetisComparison -CandidateComparisonId $CandidateComparison -ReportPath $BundleReference\artifacts\live-diagnostics-trace-comparison.thetis-parity.json -FailOnRegression",
        "powershell -NoProfile -ExecutionPolicy Bypass -File tools\new-dsp-artifact-manifest.ps1 -BundleDir $BundleReference -AcceptanceManifest -RequireLiveAcceptanceArtifacts -Force",
        "powershell -NoProfile -ExecutionPolicy Bypass -File tools\validate-dsp-modernization-bundle.ps1 -BundleDir $BundleReference -RequireArtifactFiles -ReportPath $BundleReference\validation-report.json",
        "powershell -NoProfile -ExecutionPolicy Bypass -File tools\summarize-dsp-modernization-validation-report.ps1 -BundleDir $BundleReference -ReportPath $BundleReference\validation-triage-report.json -MarkdownPath $BundleReference\validation-triage-report.md -FailOnIssues"
    )
}

function Invoke-MatrixCapture {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [Parameter(Mandatory = $true)][string]$BundlePath,
        [Parameter(Mandatory = $true)][string]$Base,
        [Parameter(Mandatory = $true)][string]$ComparisonId,
        [Parameter(Mandatory = $true)][string]$IndexPath,
        [Parameter(Mandatory = $true)][string]$ReportPath
    )

    $args = New-Object System.Collections.Generic.List[string]
    Add-CliArg $args "-BaseUrl" $Base
    Add-CliArg $args "-BundleDir" $BundlePath
    Add-CliValues $args "-ScenarioIds" $ScenarioIds
    Add-CliArg $args "-ComparisonId" $ComparisonId
    Add-CliArg $args "-IndexPath" $IndexPath
    Add-CliArg $args "-ReportPath" $ReportPath
    Add-CliArg $args "-Samples" ([string]$Samples)
    Add-CliArg $args "-IntervalMs" ([string]$IntervalMs)
    Add-CliArg $args "-TimeoutSec" ([string]$TimeoutSec)
    if ($SkipCertificateCheck) { Add-CliArg $args "-SkipCertificateCheck" }
    Add-CliArg $args "-ContinueOnError"
    if ($JsonOnly) { Add-CliArg $args "-JsonOnly" }
    Invoke-ToolScript -ScriptPath $ScriptPath -Arguments @($args.ToArray()) -Quiet:$JsonOnly -AllowNonZeroExit
}

if ($Samples -lt 1) {
    throw "Samples must be at least 1."
}
if ($IntervalMs -lt 0) {
    throw "IntervalMs must be greater than or equal to 0."
}
if ($TimeoutSec -lt 1) {
    throw "TimeoutSec must be at least 1."
}

$repoRoot = Get-RepoRoot
$base = Normalize-BaseUrl $BaseUrl
$bundleReference = if ([string]::IsNullOrWhiteSpace($BundleDir)) { "captures\dsp-modernization\<timestamp>" } else { $BundleDir }
$scenarioArgument = if (@($ScenarioIds).Count -gt 0) { " -ScenarioIds " + ((@($ScenarioIds) | ForEach-Object { [string]$_ }) -join " ") } else { "" }
$acceptanceCommandSteps = @(Get-PlanCommandSteps `
        -BundleReference $bundleReference `
        -Base $base `
        -OffComparison $OffBaselineComparisonId `
        -ThetisComparison $ThetisComparisonId `
        -BaselineComparison $BaselineComparisonId `
        -CandidateComparison $CandidateComparisonId `
        -ScenarioArgument $scenarioArgument `
        -SampleCount $Samples `
        -SampleIntervalMs $IntervalMs)
$acceptanceArtifacts = @(Get-AcceptanceArtifacts)

if ($PlanOnly) {
    [ordered]@{
        schemaVersion = 7
        tool = "run-dsp-live-acceptance-cycle"
        mode = "plan-only"
        baseUrl = $base
        bundleDir = $bundleReference
        scenarioIds = @($ScenarioIds)
        samples = $Samples
        intervalMs = $IntervalMs
        timeoutSec = $TimeoutSec
        comparisonIds = @($OffBaselineComparisonId, $ThetisComparisonId, $BaselineComparisonId, $CandidateComparisonId)
        requiredEvidenceGates = @(
            "g2-hardware",
            "live-matrix-captures",
            "live-matrix-comparison",
            "thetis-parity-live-comparison",
            "live-trace-metric-catalog-alignment",
            "live-history-agc-stability",
            "live-history-mixed-weak-strong",
            "strict-bundle-validation",
            "required-live-artifacts"
        )
        advisoryEvidenceSignals = @(
            "live-matrix-mixed-weak-strong-hunt",
            "live-matrix-artifact-control",
            "live-history-artifact-control"
        )
        acceptanceCommandStepCount = $acceptanceCommandSteps.Count
        acceptanceCommandSteps = @($acceptanceCommandSteps)
        acceptanceExpectedArtifacts = @($acceptanceArtifacts)
        executionExample = "powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-live-acceptance-cycle.ps1 -BundleDir $bundleReference -Samples $Samples -IntervalMs $IntervalMs"
        notes = @(
            "Run capture-dsp-modernization-bundle.ps1 first; this tool fills live acceptance artifacts inside that existing bundle.",
            "G2 hardware evidence is an explicit readiness gate; preflight validation cannot promote exploratory captures into acceptance evidence.",
            "Default execution is strict: capture preflight failures, matrix regressions, and strict validation failures keep liveAcceptanceEvidenceReady=false and return a non-zero exit after the wrapper writes its summary.",
            "Matrix child runs use -ContinueOnError so failed windows are captured into reports and the wrapper can summarize all four comparisons before deciding final status.",
            "Schema v4 copies the best mixed weak/strong matrix hunt evidence into the acceptance summary as advisory triage for the next G2 live-history target.",
            "Schema v5 copies validation-triage primaryAcceptance* fields into the wrapper summary so a blocked one-command run carries its next action.",
            "Schema v6 copies liveDiagnosticsHistoryArtifactControlSignalCount into the wrapper summary as an advisory speech-artifact review signal.",
            "Schema v7 copies liveMatrixArtifactControl* into the wrapper summary so matrix speech-artifact advisories stay visible in blocked one-command runs.",
            "Use -AllowCapturePreflight, -AllowRegression, or -AllowValidationPreflight only for exploratory evidence, not acceptance claims.",
            "No DSP runtime behavior or operator defaults are changed by this tool."
        )
    } | ConvertTo-Json -Depth 32
    exit 0
}

if ([string]::IsNullOrWhiteSpace($BundleDir)) {
    throw "Specify -BundleDir for execution, or use -PlanOnly to print the live acceptance cycle."
}

$bundlePath = [System.IO.Path]::GetFullPath((Resolve-RepoPath $BundleDir))
if (-not (Test-Path -LiteralPath $bundlePath -PathType Container)) {
    throw "Bundle directory not found: $bundlePath"
}

if ([string]::IsNullOrWhiteSpace($SummaryPath)) {
    $SummaryPath = Join-Path $bundlePath "artifacts\live-acceptance-cycle-summary.json"
}
else {
    $SummaryPath = Resolve-BundlePath $bundlePath $SummaryPath
}

$artifactsDir = Join-Path $bundlePath "artifacts"
New-Item -ItemType Directory -Force -Path $artifactsDir | Out-Null

$matrixScript = Join-Path $PSScriptRoot "run-dsp-live-diagnostics-matrix.ps1"
$historyScript = Join-Path $PSScriptRoot "summarize-dsp-live-diagnostics-history.ps1"
$comparisonScript = Join-Path $PSScriptRoot "compare-dsp-live-diagnostics-matrix.ps1"
$manifestScript = Join-Path $PSScriptRoot "new-dsp-artifact-manifest.ps1"
$validationScript = Join-Path $PSScriptRoot "validate-dsp-modernization-bundle.ps1"
$triageScript = Join-Path $PSScriptRoot "summarize-dsp-modernization-validation-report.ps1"
$requiredScripts = @($matrixScript, $historyScript, $comparisonScript, $manifestScript, $validationScript, $triageScript)
foreach ($script in $requiredScripts) {
    if (-not (Test-Path -LiteralPath $script -PathType Leaf)) {
        throw "Required tool script not found: $script"
    }
}

$offBaselineIndexPath = Join-Path $artifactsDir "live-diagnostics-trace-index.off-baseline.json"
$offBaselineReportPath = Join-Path $artifactsDir "live-diagnostics-matrix-report.off-baseline.json"
$thetisIndexPath = Join-Path $artifactsDir "live-diagnostics-trace-index.thetis-parity.json"
$thetisReportPath = Join-Path $artifactsDir "live-diagnostics-matrix-report.thetis-parity.json"
$baselineIndexPath = Join-Path $artifactsDir "live-diagnostics-trace-index.baseline.json"
$baselineReportPath = Join-Path $artifactsDir "live-diagnostics-matrix-report.baseline.json"
$candidateIndexPath = Join-Path $artifactsDir "live-diagnostics-trace-index.candidate.json"
$candidateReportPath = Join-Path $artifactsDir "live-diagnostics-matrix-report.candidate.json"
$historyReportPath = Join-Path $artifactsDir "live-diagnostics-history.json"
$comparisonReportPath = Join-Path $artifactsDir "live-diagnostics-trace-comparison.json"
$thetisComparisonReportPath = Join-Path $artifactsDir "live-diagnostics-trace-comparison.thetis-parity.json"
$manifestPath = Join-Path $bundlePath "artifact-manifest.json"
$validationReportPath = Join-Path $bundlePath "validation-report.json"
$triageReportPath = Join-Path $bundlePath "validation-triage-report.json"
$triageMarkdownPath = Join-Path $bundlePath "validation-triage-report.md"

$startedUtc = [DateTimeOffset]::UtcNow

$offBaselineExitCode = Invoke-MatrixCapture -ScriptPath $matrixScript -BundlePath $bundlePath -Base $base -ComparisonId $OffBaselineComparisonId -IndexPath $offBaselineIndexPath -ReportPath $offBaselineReportPath
$thetisExitCode = Invoke-MatrixCapture -ScriptPath $matrixScript -BundlePath $bundlePath -Base $base -ComparisonId $ThetisComparisonId -IndexPath $thetisIndexPath -ReportPath $thetisReportPath
$baselineExitCode = Invoke-MatrixCapture -ScriptPath $matrixScript -BundlePath $bundlePath -Base $base -ComparisonId $BaselineComparisonId -IndexPath $baselineIndexPath -ReportPath $baselineReportPath
$candidateExitCode = Invoke-MatrixCapture -ScriptPath $matrixScript -BundlePath $bundlePath -Base $base -ComparisonId $CandidateComparisonId -IndexPath $candidateIndexPath -ReportPath $candidateReportPath

$historyArgs = New-Object System.Collections.Generic.List[string]
Add-CliArg $historyArgs "-BundleDir" $bundlePath
Add-CliArg $historyArgs "-ReportPath" $historyReportPath
if ($JsonOnly) { Add-CliArg $historyArgs "-JsonOnly" }
Invoke-ToolScript -ScriptPath $historyScript -Arguments @($historyArgs.ToArray()) -Quiet:$JsonOnly | Out-Null

$comparisonArgs = New-Object System.Collections.Generic.List[string]
Add-CliArg $comparisonArgs "-BundleDir" $bundlePath
Add-CliArg $comparisonArgs "-BaselineIndexPath" $baselineIndexPath
Add-CliArg $comparisonArgs "-CandidateIndexPath" $candidateIndexPath
Add-CliArg $comparisonArgs "-BaselineComparisonId" $BaselineComparisonId
Add-CliArg $comparisonArgs "-CandidateComparisonId" $CandidateComparisonId
Add-CliArg $comparisonArgs "-ReportPath" $comparisonReportPath
if (-not $AllowRegression) { Add-CliArg $comparisonArgs "-FailOnRegression" }
if ($JsonOnly) { Add-CliArg $comparisonArgs "-JsonOnly" }
$comparisonExitCode = Invoke-ToolScript -ScriptPath $comparisonScript -Arguments @($comparisonArgs.ToArray()) -Quiet:$JsonOnly -AllowNonZeroExit

$thetisComparisonArgs = New-Object System.Collections.Generic.List[string]
Add-CliArg $thetisComparisonArgs "-BundleDir" $bundlePath
Add-CliArg $thetisComparisonArgs "-BaselineIndexPath" $thetisIndexPath
Add-CliArg $thetisComparisonArgs "-CandidateIndexPath" $candidateIndexPath
Add-CliArg $thetisComparisonArgs "-BaselineComparisonId" $ThetisComparisonId
Add-CliArg $thetisComparisonArgs "-CandidateComparisonId" $CandidateComparisonId
Add-CliArg $thetisComparisonArgs "-ReportPath" $thetisComparisonReportPath
if (-not $AllowRegression) { Add-CliArg $thetisComparisonArgs "-FailOnRegression" }
if ($JsonOnly) { Add-CliArg $thetisComparisonArgs "-JsonOnly" }
$thetisComparisonExitCode = Invoke-ToolScript -ScriptPath $comparisonScript -Arguments @($thetisComparisonArgs.ToArray()) -Quiet:$JsonOnly -AllowNonZeroExit

$manifestArgs = New-Object System.Collections.Generic.List[string]
Add-CliArg $manifestArgs "-BundleDir" $bundlePath
Add-CliArg $manifestArgs "-OutputPath" $manifestPath
Add-CliArg $manifestArgs "-AcceptanceManifest"
Add-CliArg $manifestArgs "-RequireLiveAcceptanceArtifacts"
Add-CliArg $manifestArgs "-Force"
Invoke-ToolScript -ScriptPath $manifestScript -Arguments @($manifestArgs.ToArray()) -Quiet:$JsonOnly | Out-Null

$validationArgs = New-Object System.Collections.Generic.List[string]
Add-CliArg $validationArgs "-BundleDir" $bundlePath
Add-CliArg $validationArgs "-ReportPath" $validationReportPath
Add-CliArg $validationArgs "-RequireArtifactFiles"
if ($AllowValidationPreflight) { Add-CliArg $validationArgs "-AllowPreflight" }
if ($JsonOnly) { Add-CliArg $validationArgs "-JsonOnly" }
$validationExitCode = Invoke-ToolScript -ScriptPath $validationScript -Arguments @($validationArgs.ToArray()) -Quiet:$JsonOnly -AllowNonZeroExit

$triageArgs = New-Object System.Collections.Generic.List[string]
Add-CliArg $triageArgs "-BundleDir" $bundlePath
Add-CliArg $triageArgs "-ReportPath" $triageReportPath
Add-CliArg $triageArgs "-MarkdownPath" $triageMarkdownPath
Add-CliArg $triageArgs "-FailOnIssues"
if ($JsonOnly) { Add-CliArg $triageArgs "-JsonOnly" }
$triageExitCode = Invoke-ToolScript -ScriptPath $triageScript -Arguments @($triageArgs.ToArray()) -Quiet:$JsonOnly -AllowNonZeroExit

$completedUtc = [DateTimeOffset]::UtcNow
$offReport = Read-OptionalJsonFile $offBaselineReportPath
$thetisReport = Read-OptionalJsonFile $thetisReportPath
$baselineReport = Read-OptionalJsonFile $baselineReportPath
$candidateReport = Read-OptionalJsonFile $candidateReportPath
$comparisonReport = Read-OptionalJsonFile $comparisonReportPath
$thetisComparisonReport = Read-OptionalJsonFile $thetisComparisonReportPath
$validationReport = Read-OptionalJsonFile $validationReportPath
$triageReport = Read-OptionalJsonFile $triageReportPath

$matrixReports = @($offReport, $thetisReport, $baselineReport, $candidateReport)
$matrixReadyCount = @($matrixReports | Where-Object { $null -ne $_ -and (Test-Truthy (Get-JsonValue $_ "acceptanceReady")) }).Count
$matrixFailedRunCount = 0
$matrixNotReadyTraceCount = 0
$matrixHardBlockerRunCount = 0
foreach ($report in $matrixReports) {
    if ($null -eq $report) { continue }
    $matrixFailedRunCount += [int](Get-JsonValue $report "failedRunCount")
    $matrixNotReadyTraceCount += [int](Get-JsonValue $report "notReadyTraceCount")
    $matrixHardBlockerRunCount += [int](Get-JsonValue $report "hardBlockerRunCount")
}

$matrixAcceptanceReady = ($matrixReadyCount -eq 4)
$matrixExitCodes = @($offBaselineExitCode, $thetisExitCode, $baselineExitCode, $candidateExitCode)
$matrixNonZeroExitCount = @($matrixExitCodes | Where-Object { [int]$_ -ne 0 }).Count
$comparisonReady = Test-Truthy (Get-JsonValue $comparisonReport "readyForReview")
$thetisComparisonReady = Test-Truthy (Get-JsonValue $thetisComparisonReport "readyForReview")
$validationOk = Test-Truthy (Get-JsonValue $validationReport "ok")
$comparisonMetricCatalogAlignmentReady = if ($null -ne $validationReport) { Test-Truthy (Get-JsonValue $validationReport "liveTraceComparisonMetricCatalogAlignmentReady") } else { $false }
$comparisonMetricCatalogAlignmentStatus = if ($null -ne $validationReport) { [string](Get-JsonValue $validationReport "liveTraceComparisonMetricCatalogAlignmentStatus") } else { "" }
$comparisonMetricDefinitionCount = if ($null -ne $validationReport) { [int](Get-JsonValue $validationReport "liveTraceComparisonMetricDefinitionCount") } else { $null }
$comparisonMetricCatalogMissingMetricCount = if ($null -ne $validationReport) { [int](Get-JsonValue $validationReport "liveTraceComparisonMetricCatalogMissingMetricCount") } else { $null }
$comparisonMetricCatalogMismatchCount = if ($null -ne $validationReport) {
    [int](Get-JsonValue $validationReport "liveTraceComparisonMetricCatalogDirectionMismatchCount") +
    [int](Get-JsonValue $validationReport "liveTraceComparisonMetricCatalogThresholdMismatchCount") +
    [int](Get-JsonValue $validationReport "liveTraceComparisonMetricCatalogComparatorMismatchCount") +
    [int](Get-JsonValue $validationReport "liveTraceComparisonMetricCatalogSafetyClassMismatchCount") +
    [int](Get-JsonValue $validationReport "liveTraceComparisonMetricCatalogScopeMismatchCount")
}
else {
    $null
}
$thetisComparisonMetricCatalogAlignmentReady = if ($null -ne $validationReport) { Test-Truthy (Get-JsonValue $validationReport "liveTraceThetisComparisonMetricCatalogAlignmentReady") } else { $false }
$thetisComparisonMetricCatalogAlignmentStatus = if ($null -ne $validationReport) { [string](Get-JsonValue $validationReport "liveTraceThetisComparisonMetricCatalogAlignmentStatus") } else { "" }
$thetisComparisonMetricDefinitionCount = if ($null -ne $validationReport) { [int](Get-JsonValue $validationReport "liveTraceThetisComparisonMetricDefinitionCount") } else { $null }
$thetisComparisonMetricCatalogMissingMetricCount = if ($null -ne $validationReport) { [int](Get-JsonValue $validationReport "liveTraceThetisComparisonMetricCatalogMissingMetricCount") } else { $null }
$thetisComparisonMetricCatalogMismatchCount = if ($null -ne $validationReport) {
    [int](Get-JsonValue $validationReport "liveTraceThetisComparisonMetricCatalogDirectionMismatchCount") +
    [int](Get-JsonValue $validationReport "liveTraceThetisComparisonMetricCatalogThresholdMismatchCount") +
    [int](Get-JsonValue $validationReport "liveTraceThetisComparisonMetricCatalogComparatorMismatchCount") +
    [int](Get-JsonValue $validationReport "liveTraceThetisComparisonMetricCatalogSafetyClassMismatchCount") +
    [int](Get-JsonValue $validationReport "liveTraceThetisComparisonMetricCatalogScopeMismatchCount")
}
else {
    $null
}
$hardwareEvidenceStatus = if ($null -ne $validationReport) { [string](Get-JsonValue $validationReport "hardwareEvidenceStatus") } else { "" }
$hardwareEvidenceReady = [string]::Equals($hardwareEvidenceStatus, "g2-hardware-evidence-ready", [StringComparison]::OrdinalIgnoreCase)
$hardwareTarget = if ($null -ne $validationReport) { [string](Get-JsonValue $validationReport "hardwareTarget") } else { "" }
$captureHardwareTarget = if ($null -ne $validationReport) { [string](Get-JsonValue $validationReport "captureHardwareTarget") } else { "" }
$hardwareDiagnosticsPresent = if ($null -ne $validationReport) { Test-Truthy (Get-JsonValue $validationReport "hardwareDiagnosticsPresent") } else { $false }
$liveHistoryAgcStabilityStatus = if ($null -ne $validationReport) { [string](Get-JsonValue $validationReport "liveDiagnosticsHistoryAgcStabilityStatus") } else { "" }
$liveHistoryAgcStabilityReady = [string]::Equals($liveHistoryAgcStabilityStatus, "agc-stability-ready", [StringComparison]::OrdinalIgnoreCase)
$liveHistoryAgcStabilityTraceCount = if ($null -ne $validationReport) { [int](Get-JsonValue $validationReport "liveDiagnosticsHistoryAgcStabilityTraceCount") } else { $null }
$liveHistoryAgcStabilityMissingTraceCount = if ($null -ne $validationReport) { [int](Get-JsonValue $validationReport "liveDiagnosticsHistoryAgcStabilityMissingTraceCount") } else { $null }
$liveHistoryAgcPumpingRiskTraceCount = if ($null -ne $validationReport) { [int](Get-JsonValue $validationReport "liveDiagnosticsHistoryAgcPumpingRiskTraceCount") } else { $null }
$liveHistoryAgcActivePumpingSignalCount = if ($null -ne $validationReport) { [int](Get-JsonValue $validationReport "liveDiagnosticsHistoryAgcActivePumpingSignalCount") } else { $null }
$liveHistoryAgcVoiceLikePumpingSignalCount = if ($null -ne $validationReport) { [int](Get-JsonValue $validationReport "liveDiagnosticsHistoryAgcVoiceLikePumpingSignalCount") } else { $null }
$liveHistoryArtifactControlSignalCount = if ($null -ne $validationReport) { [int](Get-JsonValue $validationReport "liveDiagnosticsHistoryArtifactControlSignalCount") } else { $null }
$liveHistoryMixedWeakStrongStatus = if ($null -ne $validationReport) { [string](Get-JsonValue $validationReport "liveDiagnosticsHistoryMixedWeakStrongEvidenceStatus") } else { "" }
$liveHistoryMixedWeakStrongReady = [string]::Equals($liveHistoryMixedWeakStrongStatus, "ready", [StringComparison]::OrdinalIgnoreCase)
$liveHistoryMixedWeakStrongTraceCount = if ($null -ne $validationReport) { [int](Get-JsonValue $validationReport "liveDiagnosticsHistoryMixedWeakStrongTraceCount") } else { $null }
$liveHistoryMixedWeakStrongReadyTraceCount = if ($null -ne $validationReport) { [int](Get-JsonValue $validationReport "liveDiagnosticsHistoryMixedWeakStrongReadyTraceCount") } else { $null }
$liveHistoryMixedWeakStrongMissingTraceCount = if ($null -ne $validationReport) { [int](Get-JsonValue $validationReport "liveDiagnosticsHistoryMixedWeakStrongMissingTraceCount") } else { $null }
$liveHistoryMixedWeakStrongGapWatchTraceCount = if ($null -ne $validationReport) { [int](Get-JsonValue $validationReport "liveDiagnosticsHistoryMixedWeakStrongGapWatchTraceCount") } else { $null }
$liveMatrixMixedWeakStrongHuntReady = if ($null -ne $validationReport) { Test-Truthy (Get-JsonValue $validationReport "liveMatrixMixedWeakStrongHuntReady") } else { $false }
$liveMatrixMixedWeakStrongStatus = if ($null -ne $validationReport) { [string](Get-JsonValue $validationReport "liveMatrixMixedWeakStrongStatus") } else { "" }
$liveMatrixMixedWeakStrongReportCount = if ($null -ne $validationReport) { [int](Get-JsonValue $validationReport "liveMatrixMixedWeakStrongReportCount") } else { $null }
$liveMatrixMixedWeakStrongSchemaV2ReportCount = if ($null -ne $validationReport) { [int](Get-JsonValue $validationReport "liveMatrixMixedWeakStrongSchemaV2ReportCount") } else { $null }
$liveMatrixMixedWeakStrongReadyReportCount = if ($null -ne $validationReport) { [int](Get-JsonValue $validationReport "liveMatrixMixedWeakStrongReadyReportCount") } else { $null }
$liveMatrixMixedWeakStrongTraceCount = if ($null -ne $validationReport) { [int](Get-JsonValue $validationReport "liveMatrixMixedWeakStrongTraceCount") } else { $null }
$liveMatrixMixedWeakStrongReadyTraceCount = if ($null -ne $validationReport) { [int](Get-JsonValue $validationReport "liveMatrixMixedWeakStrongReadyTraceCount") } else { $null }
$liveMatrixMixedWeakStrongMissingRunCount = if ($null -ne $validationReport) { [int](Get-JsonValue $validationReport "liveMatrixMixedWeakStrongMissingRunCount") } else { $null }
$liveMatrixMixedWeakStrongGapWatchRunCount = if ($null -ne $validationReport) { [int](Get-JsonValue $validationReport "liveMatrixMixedWeakStrongGapWatchRunCount") } else { $null }
$liveMatrixMixedWeakStrongWeakInputSampleCount = if ($null -ne $validationReport) { [int](Get-JsonValue $validationReport "liveMatrixMixedWeakStrongWeakInputSampleCount") } else { $null }
$liveMatrixMixedWeakStrongStrongInputSampleCount = if ($null -ne $validationReport) { [int](Get-JsonValue $validationReport "liveMatrixMixedWeakStrongStrongInputSampleCount") } else { $null }
$liveMatrixMixedWeakStrongStatusCounts = if ($null -ne $validationReport) { @(Get-JsonArray $validationReport "liveMatrixMixedWeakStrongStatusCounts") } else { @() }
$liveMatrixMixedWeakStrongBestRun = if ($null -ne $validationReport) { Get-JsonValue $validationReport "liveMatrixMixedWeakStrongBestRun" } else { $null }
$liveMatrixArtifactControlStatus = if ($null -ne $validationReport) { [string](Get-JsonValue $validationReport "liveMatrixArtifactControlStatus") } else { "" }
$liveMatrixArtifactControlReportCount = if ($null -ne $validationReport) { [int](Get-JsonValue $validationReport "liveMatrixArtifactControlReportCount") } else { $null }
$liveMatrixArtifactControlSchemaV3ReportCount = if ($null -ne $validationReport) { [int](Get-JsonValue $validationReport "liveMatrixArtifactControlSchemaV3ReportCount") } else { $null }
$liveMatrixArtifactControlReviewRunCount = if ($null -ne $validationReport) { [int](Get-JsonValue $validationReport "liveMatrixArtifactControlReviewRunCount") } else { $null }
$liveMatrixArtifactControlRiskScoreMax = if ($null -ne $validationReport) { Get-JsonValue $validationReport "liveMatrixArtifactControlRiskScoreMax" } else { $null }
$liveMatrixArtifactControlLowEvidenceLiftedSampleCount = if ($null -ne $validationReport) { [int](Get-JsonValue $validationReport "liveMatrixArtifactControlLowEvidenceLiftedSampleCount") } else { $null }
$liveMatrixArtifactControlLowEvidenceLiftedPctMax = if ($null -ne $validationReport) { Get-JsonValue $validationReport "liveMatrixArtifactControlLowEvidenceLiftedPctMax" } else { $null }
$liveMatrixArtifactControlAudioAlignmentMismatchPctMax = if ($null -ne $validationReport) { Get-JsonValue $validationReport "liveMatrixArtifactControlAudioAlignmentMismatchPctMax" } else { $null }
$liveMatrixArtifactControlStatusCounts = if ($null -ne $validationReport) { @(Get-JsonArray $validationReport "liveMatrixArtifactControlStatusCounts") } else { @() }
$requiredLiveProblemCount = if ($null -ne $triageReport) { [int](Get-JsonValue $triageReport "requiredLiveAcceptanceArtifactProblemCount") } else { $null }
$triageActionCount = if ($null -ne $triageReport) { [int](Get-JsonValue $triageReport "acceptanceActionPlanCount") } else { $null }
$triageRequiredActionCount = if ($null -ne $triageReport) { [int](Get-JsonValue $triageReport "acceptanceRequiredActionCount") } else { $null }
$triageManualActionCount = if ($null -ne $triageReport) { [int](Get-JsonValue $triageReport "acceptanceManualActionCount") } else { $null }
$triageActionCategoryCounts = if ($null -ne $triageReport) { @(Get-JsonArray $triageReport "acceptanceActionCategoryCounts") } else { @() }
$triagePrimaryAcceptanceActionId = if ($null -ne $triageReport) { [string](Get-JsonValue $triageReport "primaryAcceptanceActionId") } else { "" }
$triagePrimaryAcceptanceActionPriority = if ($null -ne $triageReport) { Get-JsonValue $triageReport "primaryAcceptanceActionPriority" } else { $null }
$triagePrimaryAcceptanceActionStageId = if ($null -ne $triageReport) { [string](Get-JsonValue $triageReport "primaryAcceptanceActionStageId") } else { "" }
$triagePrimaryAcceptanceActionGateId = if ($null -ne $triageReport) { [string](Get-JsonValue $triageReport "primaryAcceptanceActionGateId") } else { "" }
$triagePrimaryAcceptanceActionCategory = if ($null -ne $triageReport) { [string](Get-JsonValue $triageReport "primaryAcceptanceActionCategory") } else { "" }
$triagePrimaryAcceptanceActionRequired = if ($null -ne $triageReport) { Test-Truthy (Get-JsonValue $triageReport "primaryAcceptanceActionRequired") } else { $false }
$triagePrimaryAcceptanceActionManual = if ($null -ne $triageReport) { Test-Truthy (Get-JsonValue $triageReport "primaryAcceptanceActionManual") } else { $false }
$triagePrimaryAcceptanceCommandTemplate = if ($null -ne $triageReport) { [string](Get-JsonValue $triageReport "primaryAcceptanceCommandTemplate") } else { "" }
$triagePrimaryAcceptanceCommandStepCount = if ($null -ne $triageReport) { [int](Get-JsonValue $triageReport "primaryAcceptanceCommandStepCount") } else { $null }
$triagePrimaryAcceptanceCommandSteps = if ($null -ne $triageReport) { @(Get-JsonArray $triageReport "primaryAcceptanceCommandSteps") } else { @() }
$triagePrimaryAcceptanceManualAction = if ($null -ne $triageReport) { [string](Get-JsonValue $triageReport "primaryAcceptanceManualAction") } else { "" }
$triagePrimaryAcceptanceExpectedArtifact = if ($null -ne $triageReport) { [string](Get-JsonValue $triageReport "primaryAcceptanceExpectedArtifact") } else { "" }
$triagePrimaryAcceptanceExpectedArtifactCount = if ($null -ne $triageReport) { [int](Get-JsonValue $triageReport "primaryAcceptanceExpectedArtifactCount") } else { $null }
$triagePrimaryAcceptanceExpectedArtifacts = if ($null -ne $triageReport) { @(Get-JsonArray $triageReport "primaryAcceptanceExpectedArtifacts") } else { @() }
$triagePrimaryAcceptanceFollowUp = if ($null -ne $triageReport) { [string](Get-JsonValue $triageReport "primaryAcceptanceFollowUp") } else { "" }
$liveAcceptanceEvidenceReady = ($matrixAcceptanceReady -and $comparisonReady -and $thetisComparisonReady -and $comparisonMetricCatalogAlignmentReady -and $thetisComparisonMetricCatalogAlignmentReady -and $validationOk -and $requiredLiveProblemCount -eq 0 -and $hardwareEvidenceReady -and $liveHistoryAgcStabilityReady -and $liveHistoryMixedWeakStrongReady)
$blockers = New-Object System.Collections.Generic.List[object]
if (-not $matrixAcceptanceReady) {
    $blockers.Add([ordered]@{ code = "matrix-capture-not-acceptance-ready"; message = "One or more live matrix captures had failed runs, hard blockers, or not-ready traces." }) | Out-Null
}
if (-not $hardwareEvidenceReady) {
    $blockers.Add([ordered]@{ code = "g2-hardware-evidence-not-ready"; message = "Validation did not confirm connected G2 hardware evidence; hardwareEvidenceStatus='$hardwareEvidenceStatus'." }) | Out-Null
}
if (-not $comparisonReady) {
    $blockers.Add([ordered]@{ code = "live-matrix-comparison-not-ready"; message = "Baseline/candidate live matrix comparison is not ready for review." }) | Out-Null
}
if (-not $thetisComparisonReady) {
    $blockers.Add([ordered]@{ code = "thetis-parity-live-matrix-comparison-not-ready"; message = "Thetis-parity/candidate live matrix comparison is not ready for review." }) | Out-Null
}
if (-not $comparisonMetricCatalogAlignmentReady) {
    $blockers.Add([ordered]@{ code = "live-trace-metric-catalog-not-aligned"; message = "Live trace comparison metric definitions are not aligned with benchmark-metric-catalog.json; status='$comparisonMetricCatalogAlignmentStatus'." }) | Out-Null
}
if (-not $thetisComparisonMetricCatalogAlignmentReady) {
    $blockers.Add([ordered]@{ code = "thetis-parity-live-trace-metric-catalog-not-aligned"; message = "Thetis-parity live trace comparison metric definitions are not aligned with benchmark-metric-catalog.json; status='$thetisComparisonMetricCatalogAlignmentStatus'." }) | Out-Null
}
if (-not $validationOk) {
    $blockers.Add([ordered]@{ code = "strict-validation-failed"; message = "Strict bundle validation did not pass with -RequireArtifactFiles." }) | Out-Null
}
if (-not $liveHistoryAgcStabilityReady) {
    $agcBlockerCode = if ([string]::Equals($liveHistoryAgcStabilityStatus, "agc-stability-missing", [StringComparison]::OrdinalIgnoreCase)) {
        "live-history-agc-stability-missing"
    }
    else {
        "live-history-agc-stability-not-ready"
    }
    $blockers.Add([ordered]@{ code = $agcBlockerCode; message = "Validation did not confirm schema-v14 live history AGC stability evidence; liveDiagnosticsHistoryAgcStabilityStatus='$liveHistoryAgcStabilityStatus', missingTraceCount=$liveHistoryAgcStabilityMissingTraceCount." }) | Out-Null
}
if (-not $liveHistoryMixedWeakStrongReady) {
    $mixedBlockerCode = if ([string]::Equals($liveHistoryMixedWeakStrongStatus, "missing-mixed-weak-strong", [StringComparison]::OrdinalIgnoreCase)) {
        "live-history-mixed-weak-strong-missing"
    }
    elseif ([string]::Equals($liveHistoryMixedWeakStrongStatus, "weak-strong-output-gap-watch", [StringComparison]::OrdinalIgnoreCase)) {
        "live-history-mixed-weak-strong-gap-watch"
    }
    else {
        "live-history-mixed-weak-strong-not-ready"
    }
    $blockers.Add([ordered]@{ code = $mixedBlockerCode; message = "Validation did not confirm schema-v14 mixed weak/strong live history evidence; liveDiagnosticsHistoryMixedWeakStrongEvidenceStatus='$liveHistoryMixedWeakStrongStatus', mixedTraceCount=$liveHistoryMixedWeakStrongTraceCount, readyTraceCount=$liveHistoryMixedWeakStrongReadyTraceCount, missingTraceCount=$liveHistoryMixedWeakStrongMissingTraceCount, gapWatchTraceCount=$liveHistoryMixedWeakStrongGapWatchTraceCount." }) | Out-Null
}
if ($requiredLiveProblemCount -gt 0) {
    $blockers.Add([ordered]@{ code = "required-live-artifact-problems"; message = "Validation triage found $requiredLiveProblemCount required live acceptance artifact problem(s)." }) | Out-Null
}

$summary = [ordered]@{
    schemaVersion = 7
    tool = "run-dsp-live-acceptance-cycle"
    generatedUtc = $completedUtc
    startedUtc = $startedUtc
    completedUtc = $completedUtc
    durationMs = [int]($completedUtc - $startedUtc).TotalMilliseconds
    baseUrl = $base
    bundleDir = $bundlePath
    scenarioIds = @($ScenarioIds)
    samples = $Samples
    intervalMs = $IntervalMs
    timeoutSec = $TimeoutSec
    comparisonIds = @($OffBaselineComparisonId, $ThetisComparisonId, $BaselineComparisonId, $CandidateComparisonId)
    acceptanceCommandStepCount = $acceptanceCommandSteps.Count
    acceptanceExpectedArtifacts = @($acceptanceArtifacts)
    matrixReportPaths = @(
        (ConvertTo-PortablePath -Root $bundlePath -Path $offBaselineReportPath),
        (ConvertTo-PortablePath -Root $bundlePath -Path $thetisReportPath),
        (ConvertTo-PortablePath -Root $bundlePath -Path $baselineReportPath),
        (ConvertTo-PortablePath -Root $bundlePath -Path $candidateReportPath)
    )
    matrixAcceptanceReady = $matrixAcceptanceReady
    matrixAcceptanceReadyCount = $matrixReadyCount
    matrixExitCodes = @($matrixExitCodes)
    matrixNonZeroExitCount = $matrixNonZeroExitCount
    matrixFailedRunCount = $matrixFailedRunCount
    matrixNotReadyTraceCount = $matrixNotReadyTraceCount
    matrixHardBlockerRunCount = $matrixHardBlockerRunCount
    historyReportPath = ConvertTo-PortablePath -Root $bundlePath -Path $historyReportPath
    comparisonReportPath = ConvertTo-PortablePath -Root $bundlePath -Path $comparisonReportPath
    comparisonExitCode = $comparisonExitCode
    comparisonReadyForReview = $comparisonReady
    comparisonRegressionCount = if ($null -ne $comparisonReport) { [int](Get-JsonValue $comparisonReport "regressionCount") } else { $null }
    comparisonGateFailureCount = if ($null -ne $comparisonReport) { [int](Get-JsonValue $comparisonReport "gateFailureCount") } else { $null }
    comparisonMetricCatalogAlignmentReady = $comparisonMetricCatalogAlignmentReady
    comparisonMetricCatalogAlignmentStatus = $comparisonMetricCatalogAlignmentStatus
    comparisonMetricDefinitionCount = $comparisonMetricDefinitionCount
    comparisonMetricCatalogMissingMetricCount = $comparisonMetricCatalogMissingMetricCount
    comparisonMetricCatalogMismatchCount = $comparisonMetricCatalogMismatchCount
    thetisComparisonReportPath = ConvertTo-PortablePath -Root $bundlePath -Path $thetisComparisonReportPath
    thetisComparisonExitCode = $thetisComparisonExitCode
    thetisComparisonReadyForReview = $thetisComparisonReady
    thetisComparisonRegressionCount = if ($null -ne $thetisComparisonReport) { [int](Get-JsonValue $thetisComparisonReport "regressionCount") } else { $null }
    thetisComparisonGateFailureCount = if ($null -ne $thetisComparisonReport) { [int](Get-JsonValue $thetisComparisonReport "gateFailureCount") } else { $null }
    thetisComparisonMetricCatalogAlignmentReady = $thetisComparisonMetricCatalogAlignmentReady
    thetisComparisonMetricCatalogAlignmentStatus = $thetisComparisonMetricCatalogAlignmentStatus
    thetisComparisonMetricDefinitionCount = $thetisComparisonMetricDefinitionCount
    thetisComparisonMetricCatalogMissingMetricCount = $thetisComparisonMetricCatalogMissingMetricCount
    thetisComparisonMetricCatalogMismatchCount = $thetisComparisonMetricCatalogMismatchCount
    artifactManifestPath = ConvertTo-PortablePath -Root $bundlePath -Path $manifestPath
    validationReportPath = ConvertTo-PortablePath -Root $bundlePath -Path $validationReportPath
    validationExitCode = $validationExitCode
    validationOk = $validationOk
    validationErrorCount = if ($null -ne $validationReport) { [int](Get-JsonValue $validationReport "errorCount") } else { $null }
    validationWarningCount = if ($null -ne $validationReport) { [int](Get-JsonValue $validationReport "warningCount") } else { $null }
    validationRequireArtifactFiles = if ($null -ne $validationReport) { Test-Truthy (Get-JsonValue $validationReport "requireArtifactFiles") } else { $null }
    hardwareEvidenceReady = $hardwareEvidenceReady
    hardwareEvidenceStatus = $hardwareEvidenceStatus
    hardwareTarget = $hardwareTarget
    captureHardwareTarget = $captureHardwareTarget
    hardwareDiagnosticsPresent = $hardwareDiagnosticsPresent
    liveDiagnosticsHistoryAgcStabilityReady = $liveHistoryAgcStabilityReady
    liveDiagnosticsHistoryAgcStabilityStatus = $liveHistoryAgcStabilityStatus
    liveDiagnosticsHistoryAgcStabilityTraceCount = $liveHistoryAgcStabilityTraceCount
    liveDiagnosticsHistoryAgcStabilityMissingTraceCount = $liveHistoryAgcStabilityMissingTraceCount
    liveDiagnosticsHistoryAgcPumpingRiskTraceCount = $liveHistoryAgcPumpingRiskTraceCount
    liveDiagnosticsHistoryAgcActivePumpingSignalCount = $liveHistoryAgcActivePumpingSignalCount
    liveDiagnosticsHistoryAgcVoiceLikePumpingSignalCount = $liveHistoryAgcVoiceLikePumpingSignalCount
    liveDiagnosticsHistoryArtifactControlSignalCount = $liveHistoryArtifactControlSignalCount
    liveDiagnosticsHistoryMixedWeakStrongEvidenceReady = $liveHistoryMixedWeakStrongReady
    liveDiagnosticsHistoryMixedWeakStrongEvidenceStatus = $liveHistoryMixedWeakStrongStatus
    liveDiagnosticsHistoryMixedWeakStrongTraceCount = $liveHistoryMixedWeakStrongTraceCount
    liveDiagnosticsHistoryMixedWeakStrongReadyTraceCount = $liveHistoryMixedWeakStrongReadyTraceCount
    liveDiagnosticsHistoryMixedWeakStrongMissingTraceCount = $liveHistoryMixedWeakStrongMissingTraceCount
    liveDiagnosticsHistoryMixedWeakStrongGapWatchTraceCount = $liveHistoryMixedWeakStrongGapWatchTraceCount
    liveMatrixMixedWeakStrongHuntReady = $liveMatrixMixedWeakStrongHuntReady
    liveMatrixMixedWeakStrongStatus = $liveMatrixMixedWeakStrongStatus
    liveMatrixMixedWeakStrongReportCount = $liveMatrixMixedWeakStrongReportCount
    liveMatrixMixedWeakStrongSchemaV2ReportCount = $liveMatrixMixedWeakStrongSchemaV2ReportCount
    liveMatrixMixedWeakStrongReadyReportCount = $liveMatrixMixedWeakStrongReadyReportCount
    liveMatrixMixedWeakStrongTraceCount = $liveMatrixMixedWeakStrongTraceCount
    liveMatrixMixedWeakStrongReadyTraceCount = $liveMatrixMixedWeakStrongReadyTraceCount
    liveMatrixMixedWeakStrongMissingRunCount = $liveMatrixMixedWeakStrongMissingRunCount
    liveMatrixMixedWeakStrongGapWatchRunCount = $liveMatrixMixedWeakStrongGapWatchRunCount
    liveMatrixMixedWeakStrongWeakInputSampleCount = $liveMatrixMixedWeakStrongWeakInputSampleCount
    liveMatrixMixedWeakStrongStrongInputSampleCount = $liveMatrixMixedWeakStrongStrongInputSampleCount
    liveMatrixMixedWeakStrongStatusCounts = @($liveMatrixMixedWeakStrongStatusCounts)
    liveMatrixMixedWeakStrongBestRun = $liveMatrixMixedWeakStrongBestRun
    liveMatrixArtifactControlStatus = $liveMatrixArtifactControlStatus
    liveMatrixArtifactControlReportCount = $liveMatrixArtifactControlReportCount
    liveMatrixArtifactControlSchemaV3ReportCount = $liveMatrixArtifactControlSchemaV3ReportCount
    liveMatrixArtifactControlReviewRunCount = $liveMatrixArtifactControlReviewRunCount
    liveMatrixArtifactControlRiskScoreMax = $liveMatrixArtifactControlRiskScoreMax
    liveMatrixArtifactControlLowEvidenceLiftedSampleCount = $liveMatrixArtifactControlLowEvidenceLiftedSampleCount
    liveMatrixArtifactControlLowEvidenceLiftedPctMax = $liveMatrixArtifactControlLowEvidenceLiftedPctMax
    liveMatrixArtifactControlAudioAlignmentMismatchPctMax = $liveMatrixArtifactControlAudioAlignmentMismatchPctMax
    liveMatrixArtifactControlStatusCounts = @($liveMatrixArtifactControlStatusCounts)
    triageReportPath = ConvertTo-PortablePath -Root $bundlePath -Path $triageReportPath
    triageMarkdownPath = ConvertTo-PortablePath -Root $bundlePath -Path $triageMarkdownPath
    triageExitCode = $triageExitCode
    triageAcceptanceActionPlanCount = $triageActionCount
    triageAcceptanceRequiredActionCount = $triageRequiredActionCount
    triageAcceptanceManualActionCount = $triageManualActionCount
    triageAcceptanceActionCategoryCounts = @($triageActionCategoryCounts)
    triagePrimaryAcceptanceActionId = $triagePrimaryAcceptanceActionId
    triagePrimaryAcceptanceActionPriority = $triagePrimaryAcceptanceActionPriority
    triagePrimaryAcceptanceActionStageId = $triagePrimaryAcceptanceActionStageId
    triagePrimaryAcceptanceActionGateId = $triagePrimaryAcceptanceActionGateId
    triagePrimaryAcceptanceActionCategory = $triagePrimaryAcceptanceActionCategory
    triagePrimaryAcceptanceActionRequired = $triagePrimaryAcceptanceActionRequired
    triagePrimaryAcceptanceActionManual = $triagePrimaryAcceptanceActionManual
    triagePrimaryAcceptanceCommandTemplate = $triagePrimaryAcceptanceCommandTemplate
    triagePrimaryAcceptanceCommandStepCount = $triagePrimaryAcceptanceCommandStepCount
    triagePrimaryAcceptanceCommandSteps = @($triagePrimaryAcceptanceCommandSteps)
    triagePrimaryAcceptanceManualAction = $triagePrimaryAcceptanceManualAction
    triagePrimaryAcceptanceExpectedArtifact = $triagePrimaryAcceptanceExpectedArtifact
    triagePrimaryAcceptanceExpectedArtifactCount = $triagePrimaryAcceptanceExpectedArtifactCount
    triagePrimaryAcceptanceExpectedArtifacts = @($triagePrimaryAcceptanceExpectedArtifacts)
    triagePrimaryAcceptanceFollowUp = $triagePrimaryAcceptanceFollowUp
    requiredLiveAcceptanceArtifactProblemCount = $requiredLiveProblemCount
    liveAcceptanceEvidenceReady = $liveAcceptanceEvidenceReady
    liveAcceptanceEvidenceStatus = if ($liveAcceptanceEvidenceReady) { "ready" } else { "blocked" }
    liveAcceptanceEvidenceBlockerCount = $blockers.Count
    liveAcceptanceEvidenceBlockers = @($blockers.ToArray())
    acceptanceLimitations = @(
        "This proves only the captured live G2 acceptance cycle; it does not approve DSP defaults by itself.",
        "Cross-radio validation remains required before default DSP behavior changes.",
        "External DSP/ML engines remain opt-in candidates until licensing, packaging, latency, CPU, fallback, and benchmark safety are proven."
    )
}

Write-JsonFile -Path $SummaryPath -Value $summary

if ($JsonOnly) {
    $summary | ConvertTo-Json -Depth 64
}
else {
    Write-Host "DSP live acceptance cycle complete."
    Write-Host "Summary: $SummaryPath"
    Write-Host "Comparison: $comparisonReportPath"
    Write-Host "Thetis comparison: $thetisComparisonReportPath"
    Write-Host "Validation: $validationReportPath"
    Write-Host "Triage: $triageReportPath"
    Write-Host "Live acceptance evidence ready: $($summary.liveAcceptanceEvidenceReady) ($($summary.liveAcceptanceEvidenceStatus))"
    Write-Host "Live history artifact-control advisory signals: $($summary.liveDiagnosticsHistoryArtifactControlSignalCount)"
    Write-Host "Live matrix artifact-control advisory status/review runs: $($summary.liveMatrixArtifactControlStatus) / $($summary.liveMatrixArtifactControlReviewRunCount)"
    if (-not [string]::IsNullOrWhiteSpace([string]$summary.triagePrimaryAcceptanceActionId)) {
        Write-Host "Primary triage action: $($summary.triagePrimaryAcceptanceActionId) [$($summary.triagePrimaryAcceptanceActionCategory)]"
        $primaryCommandOrManual = [string]$summary.triagePrimaryAcceptanceCommandTemplate
        if ([string]::IsNullOrWhiteSpace($primaryCommandOrManual)) {
            $primaryCommandOrManual = [string]$summary.triagePrimaryAcceptanceManualAction
        }
        if (-not [string]::IsNullOrWhiteSpace($primaryCommandOrManual)) {
            Write-Host "Primary triage command/manual action: $primaryCommandOrManual"
        }
        if (-not [string]::IsNullOrWhiteSpace([string]$summary.triagePrimaryAcceptanceFollowUp)) {
            Write-Host "Primary triage follow-up: $($summary.triagePrimaryAcceptanceFollowUp)"
        }
    }
}

if ((-not $matrixAcceptanceReady -and -not $AllowCapturePreflight) -or
    ((-not $comparisonReady -or -not $thetisComparisonReady) -and -not $AllowRegression) -or
    ((-not $validationOk -or $requiredLiveProblemCount -gt 0) -and -not $AllowValidationPreflight)) {
    exit 1
}
