param(
    [string]$BundleDir = "",

    [string]$BenchmarkPlanPath = "",

    [string]$MetricCatalogPath = "",

    [string]$MetricsPath = "",

    [string]$AudioIndexPath = "",

    [string]$SpectrumIndexPath = "",

    [string]$RuntimeAuditPath = "",

    [string]$StageTimingReportPath = "",

    [string]$ComparisonReportPath = "",

    [string]$ComparisonMarkdownPath = "",

    [string]$SummaryPath = "",

    [string]$ValidationReportPath = "",

    [string[]]$ScenarioIds = @(),

    [string[]]$ComparisonIds = @("off-baseline", "thetis-parity", "current-zeus", "candidate-under-test", "candidate-external-engine-opt-in"),

    [switch]$IncludeNonFixtureScenarios,

    [switch]$AllowRegression,

    [switch]$AllowRuntimeAuditPreflight,

    [switch]$ValidateBundle,

    [switch]$AllowValidationPreflight,

    [switch]$RequireArtifactFiles,

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

function Add-ReadinessBlocker {
    param(
        [Parameter(Mandatory = $true)]$Blockers,
        [Parameter(Mandatory = $true)][string]$Code,
        [Parameter(Mandatory = $true)][string]$Message
    )

    $Blockers.Add([ordered]@{
            code = $Code
            message = $Message
        }) | Out-Null
}

function Test-ReferencedFileProblem {
    param($Record)

    if ($null -eq $Record) {
        return $false
    }
    if (-not (Test-Truthy (Get-JsonValue $Record "ok"))) {
        return $true
    }

    foreach ($field in @("hashStatus", "summaryHashStatus", "summaryTracePathStatus", "sourceStatus", "jsonlHashStatus", "captureReadinessStatus")) {
        $value = [string](Get-JsonValue $Record $field)
        if ([string]::IsNullOrWhiteSpace($value)) {
            continue
        }
        if ($value -in @("mismatch", "missing", "file-missing", "path-missing", "json-invalid", "tool-invalid", "conversion-invalid", "content-mismatch")) {
            return $true
        }
    }

    return $false
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

    if ($Quiet) {
        & powershell -NoProfile -ExecutionPolicy Bypass -File $ScriptPath @Arguments | Out-Null
    }
    else {
        & powershell -NoProfile -ExecutionPolicy Bypass -File $ScriptPath @Arguments
    }
    if ($LASTEXITCODE -ne 0 -and -not $AllowNonZeroExit) {
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

if ([string]::IsNullOrWhiteSpace($StageTimingReportPath)) {
    $StageTimingReportPath = Join-Path $bundlePath "artifacts\native-stage-timing-report.json"
}
else {
    $StageTimingReportPath = Resolve-BundlePath $bundlePath $StageTimingReportPath
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

if ([string]::IsNullOrWhiteSpace($ValidationReportPath)) {
    $ValidationReportPath = Join-Path $bundlePath "validation-report.json"
}
else {
    $ValidationReportPath = Resolve-BundlePath $bundlePath $ValidationReportPath
}

if (-not (Test-Path -LiteralPath $BenchmarkPlanPath -PathType Leaf)) {
    throw "Benchmark plan not found: $BenchmarkPlanPath"
}

$scriptRoot = $PSScriptRoot
$fixtureScript = Join-Path $scriptRoot "run-dsp-wdsp-fixture-evidence.ps1"
$runtimeAuditScript = Join-Path $scriptRoot "audit-wdsp-runtime-artifacts.ps1"
$stageTimingScript = Join-Path $scriptRoot "summarize-dsp-native-stage-timing.ps1"
$comparisonScript = Join-Path $scriptRoot "compare-dsp-fixture-metrics.ps1"
$validationScript = Join-Path $scriptRoot "validate-dsp-modernization-bundle.ps1"
$requiredScripts = @($fixtureScript, $runtimeAuditScript, $stageTimingScript, $comparisonScript)
if ($ValidateBundle) {
    $requiredScripts += $validationScript
}
foreach ($script in $requiredScripts) {
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

$stageTimingArgs = New-Object System.Collections.Generic.List[string]
Add-CliArg $stageTimingArgs "-BundleDir" $bundlePath
Add-CliArg $stageTimingArgs "-MetricsPath" $MetricsPath
Add-CliArg $stageTimingArgs "-ReportPath" $StageTimingReportPath
if ($Force) { Add-CliArg $stageTimingArgs "-Force" }
if ($JsonOnly) { Add-CliArg $stageTimingArgs "-JsonOnly" }
Invoke-ToolScript -ScriptPath $stageTimingScript -Arguments @($stageTimingArgs.ToArray()) -Quiet:$JsonOnly

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
$stageTiming = Read-JsonFile $StageTimingReportPath
$runtimeAudit = Read-JsonFile $RuntimeAuditPath
$comparison = Read-JsonFile $ComparisonReportPath

$validation = $null
if ($ValidateBundle) {
    $validationArgs = New-Object System.Collections.Generic.List[string]
    Add-CliArg $validationArgs "-BundleDir" $bundlePath
    Add-CliArg $validationArgs "-ReportPath" $ValidationReportPath
    if ($AllowValidationPreflight) { Add-CliArg $validationArgs "-AllowPreflight" }
    if ($RequireArtifactFiles) { Add-CliArg $validationArgs "-RequireArtifactFiles" }
    if ($JsonOnly) { Add-CliArg $validationArgs "-JsonOnly" }
    Invoke-ToolScript -ScriptPath $validationScript -Arguments @($validationArgs.ToArray()) -Quiet:$JsonOnly -AllowNonZeroExit:$AllowValidationPreflight
    if (Test-Path -LiteralPath $ValidationReportPath -PathType Leaf) {
        $validation = Read-JsonFile $ValidationReportPath
    }
    else {
        throw "Validation report was not written: $ValidationReportPath"
    }
}

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
$stageTimingReady = Test-Truthy (Get-JsonValue $stageTiming "readyForReview")
$readyForOfflineReview = ((Test-Truthy (Get-JsonValue $comparison "readyForReview")) -and
    (Test-Truthy (Get-JsonValue $runtimeAudit "readyForWinX64Package")) -and
    $stageTimingReady -and
    [string]::Equals($runtimeHashStatus, "match", [StringComparison]::OrdinalIgnoreCase))
$validationOk = if ($null -ne $validation) { Test-Truthy (Get-JsonValue $validation "ok") } else { $null }
$validationMetricComparisonReady = if ($null -ne $validation) { Test-Truthy (Get-JsonValue $validation "metricComparisonReady") } else { $null }
$validationSourceHashStatus = if ($null -ne $validation) { [string](Get-JsonValue $validation "metricComparisonSourceMetricsHashStatus") } else { $null }
$validationRuntimeHashStatus = if ($null -ne $validation) { [string](Get-JsonValue $validation "metricComparisonWdspRuntimeHashStatus") } else { $null }
$validationRuntimeArtifactHashStatus = if ($null -ne $validation) { [string](Get-JsonValue $validation "offlineFixtureMetricsRuntimeArtifactHashStatus") } else { $null }
$validationNativeStageTimingReady = if ($null -ne $validation) { Test-Truthy (Get-JsonValue $validation "nativeStageTimingReportReady") } else { $null }
$validationNativeStageTimingStatus = if ($null -ne $validation) { [string](Get-JsonValue $validation "nativeStageTimingReportStatus") } else { $null }
$validationRequireArtifactFiles = if ($null -ne $validation) { Test-Truthy (Get-JsonValue $validation "requireArtifactFiles") } else { $false }
$validationReferencedFileRecords = if ($null -ne $validation) { @(Get-JsonArray $validation "artifactReferencedFiles") } else { @() }
$validationReferencedFileProblemCount = @($validationReferencedFileRecords | Where-Object { Test-ReferencedFileProblem $_ }).Count
$strictBundleValidationReady = if ($null -ne $validation) {
    ($validationOk -and $validationMetricComparisonReady)
} else {
    $null
}
$acceptanceEvidenceBlockers = New-Object System.Collections.Generic.List[object]
if (-not $readyForOfflineReview) {
    Add-ReadinessBlocker -Blockers $acceptanceEvidenceBlockers -Code "offline-review-not-ready" -Message "Fixture comparison, runtime audit, and runtime hashes are not all ready for offline review."
}
if (-not $stageTimingReady) {
    Add-ReadinessBlocker -Blockers $acceptanceEvidenceBlockers -Code "native-stage-timing-not-ready" -Message "Native stage timing/allocation report is not ready for review."
}
if (-not $ValidateBundle) {
    Add-ReadinessBlocker -Blockers $acceptanceEvidenceBlockers -Code "strict-validation-not-run" -Message "Run with -ValidateBundle before treating fixture output as acceptance evidence."
}
elseif ($null -eq $validation) {
    Add-ReadinessBlocker -Blockers $acceptanceEvidenceBlockers -Code "strict-validation-report-missing" -Message "Validation was requested but no validation report was loaded."
}
else {
    if (-not $validationOk) {
        Add-ReadinessBlocker -Blockers $acceptanceEvidenceBlockers -Code "strict-validation-failed" -Message "Strict bundle validation did not pass."
    }
    if (-not $validationMetricComparisonReady) {
        Add-ReadinessBlocker -Blockers $acceptanceEvidenceBlockers -Code "metric-comparison-validation-not-ready" -Message "Strict validation did not accept the fixture metric comparison evidence."
    }
    if ($null -ne $validationNativeStageTimingReady -and -not $validationNativeStageTimingReady) {
        Add-ReadinessBlocker -Blockers $acceptanceEvidenceBlockers -Code "native-stage-timing-validation-not-ready" -Message "Strict validation did not accept the native stage timing/allocation report."
    }
    if (-not $validationRequireArtifactFiles -or -not $RequireArtifactFiles) {
        Add-ReadinessBlocker -Blockers $acceptanceEvidenceBlockers -Code "artifact-file-validation-not-required" -Message "Run with -RequireArtifactFiles so artifact paths, hashes, and referenced evidence are checked."
    }
    if ($validationReferencedFileProblemCount -gt 0) {
        Add-ReadinessBlocker -Blockers $acceptanceEvidenceBlockers -Code "artifact-referenced-file-problems" -Message "Strict validation found $validationReferencedFileProblemCount referenced artifact provenance problem(s)."
    }
    foreach ($hashSpec in @(
            @{ Code = "source-metrics-hash-not-matched"; Name = "source metrics"; Status = $validationSourceHashStatus },
            @{ Code = "comparison-runtime-hash-not-matched"; Name = "comparison WDSP runtime"; Status = $validationRuntimeHashStatus },
            @{ Code = "runtime-artifact-hash-not-matched"; Name = "runtime artifact"; Status = $validationRuntimeArtifactHashStatus }
        )) {
        $statusValue = [string]$hashSpec.Status
        if (-not [string]::IsNullOrWhiteSpace($statusValue) -and
            -not [string]::Equals($statusValue, "match", [StringComparison]::OrdinalIgnoreCase)) {
            Add-ReadinessBlocker -Blockers $acceptanceEvidenceBlockers -Code ([string]$hashSpec.Code) -Message "$($hashSpec.Name) hash status is '$statusValue', not 'match'."
        }
    }
}
$acceptanceEvidenceReady = ($readyForOfflineReview -and $acceptanceEvidenceBlockers.Count -eq 0)
$acceptanceEvidenceStatus = if ($acceptanceEvidenceReady) {
    "acceptance-evidence-ready"
}
elseif (-not $readyForOfflineReview) {
    "blocked-offline-review"
}
elseif (-not $ValidateBundle) {
    "review-only-validation-not-run"
}
elseif (-not $RequireArtifactFiles -or -not $validationRequireArtifactFiles) {
    "review-only-artifact-files-not-required"
}
elseif ($validationReferencedFileProblemCount -gt 0) {
    "blocked-artifact-provenance"
}
elseif ($null -ne $validation -and -not $validationOk) {
    "blocked-strict-validation"
}
else {
    "not-acceptance-ready"
}

$summary = [ordered]@{
    schemaVersion = 2
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
    stageTimingReportPath = $StageTimingReportPath
    comparisonReportPath = $ComparisonReportPath
    comparisonMarkdownPath = if ($NoMarkdown) { $null } else { $ComparisonMarkdownPath }
    summaryPath = $SummaryPath
    validationRan = [bool]$ValidateBundle
    validationReportPath = if ($ValidateBundle) { $ValidationReportPath } else { $null }
    validationOk = $validationOk
    validationErrorCount = if ($null -ne $validation) { [int](Get-JsonValue $validation "errorCount") } else { $null }
    validationWarningCount = if ($null -ne $validation) { [int](Get-JsonValue $validation "warningCount") } else { $null }
    validationRequireArtifactFiles = $validationRequireArtifactFiles
    validationMetricComparisonReady = $validationMetricComparisonReady
    validationNativeStageTimingReady = $validationNativeStageTimingReady
    validationNativeStageTimingStatus = $validationNativeStageTimingStatus
    validationMetricComparisonSourceHashStatus = $validationSourceHashStatus
    validationMetricComparisonRuntimeHashStatus = $validationRuntimeHashStatus
    validationRuntimeArtifactHashStatus = $validationRuntimeArtifactHashStatus
    validationReferencedFileCount = $validationReferencedFileRecords.Count
    validationReferencedFileProblemCount = $validationReferencedFileProblemCount
    strictBundleValidationReady = $strictBundleValidationReady
    fixtureScenarioScope = [string](Get-JsonValue $metrics "fixtureScenarioScope")
    scenarioCount = [int](Get-JsonValue $metrics "scenarioCount")
    comparisonIds = @(Get-JsonArray $metrics "comparisonIds")
    skippedNonFixtureScenarioIds = @(Get-JsonArray $metrics "skippedNonFixtureScenarioIds")
    evidenceEngine = [string](Get-JsonValue $metrics "evidenceEngine")
    wdspRuntimeRid = [string](Get-JsonValue $metrics "wdspRuntimeRid")
    wdspRuntimeStatus = [string](Get-JsonValue $metrics "wdspRuntimeStatus")
    wdspRuntimeSha256 = $sourceRuntimeSha
    runtimeAuditReadyForWinX64Package = Test-Truthy (Get-JsonValue $runtimeAudit "readyForWinX64Package")
    nativeStageTimingReadyForReview = Test-Truthy (Get-JsonValue $stageTiming "readyForReview")
    nativeStageTimingStatus = [string](Get-JsonValue $stageTiming "status")
    nativeStageTimingRunCount = [int](Get-JsonValue $stageTiming "runCount")
    nativeStageTimingStageRecordCount = [int](Get-JsonValue $stageTiming "stageRecordCount")
    nativeStageTimingBudgetFailureCount = [int](Get-JsonValue $stageTiming "budgetFailureCount")
    nativeStageTimingMissingStageRunCount = [int](Get-JsonValue $stageTiming "missingStageTimingRunCount")
    nativeStageTimingMissingAllocationRunCount = [int](Get-JsonValue $stageTiming "missingAllocationProbeRunCount")
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
    readyForOfflineReview = $readyForOfflineReview
    acceptanceEvidenceReady = $acceptanceEvidenceReady
    acceptanceEvidenceStatus = $acceptanceEvidenceStatus
    acceptanceEvidenceBlockerCount = $acceptanceEvidenceBlockers.Count
    acceptanceEvidenceBlockers = @($acceptanceEvidenceBlockers.ToArray())
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
    Write-Host "Stage timing: $StageTimingReportPath"
    Write-Host "Comparison: $ComparisonReportPath"
    Write-Host "Runtime audit: $RuntimeAuditPath"
    Write-Host "Summary: $SummaryPath"
    if ($ValidateBundle) {
        Write-Host "Validation: $ValidationReportPath"
    }
    Write-Host "Ready for offline review: $($summary.readyForOfflineReview)"
    Write-Host "Acceptance evidence ready: $($summary.acceptanceEvidenceReady) ($($summary.acceptanceEvidenceStatus))"
    Write-Host "Runtime hash status: $runtimeHashStatus"
}

if (-not [bool]$summary.readyForOfflineReview -and -not $AllowRegression -and -not $AllowRuntimeAuditPreflight) {
    exit 1
}
