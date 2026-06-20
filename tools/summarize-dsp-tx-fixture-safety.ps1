param(
    [string]$BundleDir = "",

    [string]$MetricsPath = "",

    [string]$ReportPath = "",

    [string[]]$ScenarioIds = @("tx-two-tone", "tx-voice-like"),

    [string[]]$RequiredComparisonIds = @("current-zeus", "thetis-parity"),

    [int]$MaxClippingCount = 0,

    [double]$MaxOutputPeakDbfs = -0.25,

    [double]$MaxAlcGainReductionDb = 12.0,

    [double]$MaxCfcGainReductionDb = 12.0,

    [double]$MaxLevelerGainReductionDb = 20.0,

    [switch]$Force,

    [switch]$FailOnGate,

    [switch]$JsonOnly,

    [switch]$PlanOnly
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

function Expand-StringList {
    param([string[]]$Values)

    $expanded = New-Object System.Collections.Generic.List[string]
    foreach ($value in @($Values)) {
        if ([string]::IsNullOrWhiteSpace($value)) { continue }
        foreach ($part in ([string]$value -split ",")) {
            $trimmed = $part.Trim()
            if (-not [string]::IsNullOrWhiteSpace($trimmed)) {
                $expanded.Add($trimmed) | Out-Null
            }
        }
    }

    return @($expanded.ToArray())
}

function Get-JsonValue {
    param(
        $Object,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if ($null -eq $Object) { return $null }
    if ($Object -is [System.Collections.IDictionary]) {
        if ($Object.Contains($Name)) { return $Object[$Name] }
        foreach ($key in @($Object.Keys)) {
            if ([string]::Equals([string]$key, $Name, [StringComparison]::OrdinalIgnoreCase)) {
                return $Object[$key]
            }
        }
        return $null
    }
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

function Normalize-Id {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) { return "" }
    return ($Value.Trim().ToLowerInvariant() -replace "[^a-z0-9]+", "")
}

function Get-NumericValue {
    param($Value)

    if ($null -eq $Value) { return $null }
    if ($Value -is [double]) {
        if (-not [double]::IsNaN([double]$Value) -and -not [double]::IsInfinity([double]$Value)) { return [double]$Value }
        return $null
    }
    if ($Value -is [float]) {
        if (-not [float]::IsNaN([float]$Value) -and -not [float]::IsInfinity([float]$Value)) { return [double]$Value }
        return $null
    }
    if ($Value -is [int] -or $Value -is [long] -or $Value -is [decimal]) { return [double]$Value }

    $text = [string]$Value
    $parsed = 0.0
    if ([double]::TryParse(
            $text,
            [System.Globalization.NumberStyles]::Float,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [ref]$parsed) -and -not [double]::IsNaN($parsed) -and -not [double]::IsInfinity($parsed)) {
        return $parsed
    }

    return $null
}

function Get-NumericOrDefault {
    param(
        $Value,
        [double]$Default = 0.0
    )

    $number = Get-NumericValue $Value
    if ($null -eq $number) { return $Default }
    return $number
}

function Get-MetricValue {
    param(
        $Metrics,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if ($null -eq $Metrics) { return $null }
    $wanted = Normalize-Id $Name
    foreach ($property in @($Metrics.PSObject.Properties)) {
        if ((Normalize-Id $property.Name) -eq $wanted) {
            return Get-NumericValue $property.Value
        }
    }

    return $null
}

function Get-TxMeterValue {
    param(
        $Meters,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if ($null -eq $Meters) { return $null }
    foreach ($property in @($Meters.PSObject.Properties)) {
        if ([string]::Equals($property.Name, $Name, [StringComparison]::OrdinalIgnoreCase)) {
            return Get-NumericValue $property.Value
        }
    }

    return $null
}

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream = [System.IO.File]::OpenRead($Path)
        try {
            return ([System.BitConverter]::ToString($sha.ComputeHash($stream))).Replace("-", "").ToLowerInvariant()
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $sha.Dispose()
    }
}

function Add-Gate {
    param(
        [System.Collections.Generic.List[object]]$Gates,
        [Parameter(Mandatory = $true)][string]$Id,
        [Parameter(Mandatory = $true)][bool]$Passed,
        [Parameter(Mandatory = $true)][string]$Message,
        [object]$Observed = $null,
        [object]$Limit = $null
    )

    $record = [ordered]@{
        id = $Id
        passed = $Passed
        status = if ($Passed) { "pass" } else { "fail" }
        message = $Message
    }
    if ($null -ne $Observed) { $record["observed"] = $Observed }
    if ($null -ne $Limit) { $record["limit"] = $Limit }
    $Gates.Add($record) | Out-Null
}

$bundlePath = if ([string]::IsNullOrWhiteSpace($BundleDir)) {
    (Get-Location).Path
}
else {
    Resolve-RepoPath $BundleDir
}
$bundlePath = [System.IO.Path]::GetFullPath($bundlePath)

if ([string]::IsNullOrWhiteSpace($MetricsPath)) {
    $MetricsPath = Join-Path $bundlePath "artifacts\offline-fixture-metrics.json"
}
else {
    $MetricsPath = Resolve-BundlePath $bundlePath $MetricsPath
}

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path $bundlePath "artifacts\tx-fixture-safety-report.json"
}
else {
    $ReportPath = Resolve-BundlePath $bundlePath $ReportPath
}

$scenarioScope = @(Expand-StringList $ScenarioIds | Select-Object -Unique)
if ($scenarioScope.Count -eq 0) {
    throw "At least one TX scenario id is required."
}

$comparisonScope = @(Expand-StringList $RequiredComparisonIds | Select-Object -Unique)
if ($comparisonScope.Count -eq 0) {
    throw "At least one required comparison id is required."
}

if ($PlanOnly) {
    [ordered]@{
        schemaVersion = 1
        tool = "summarize-dsp-tx-fixture-safety"
        mode = "plan-only"
        evidenceKind = "tx-fixture-safety-report-json"
        metricsPath = $MetricsPath
        reportPath = $ReportPath
        scenarioIds = @($scenarioScope)
        requiredComparisonIds = @($comparisonScope)
        thresholds = [ordered]@{
            maxClippingCount = $MaxClippingCount
            maxOutputPeakDbfs = $MaxOutputPeakDbfs
            maxAlcGainReductionDb = $MaxAlcGainReductionDb
            maxCfcGainReductionDb = $MaxCfcGainReductionDb
            maxLevelerGainReductionDb = $MaxLevelerGainReductionDb
        }
        safety = @(
            "offline-fixture-metrics-only",
            "does not key MOX or TUN",
            "does not connect to radio hardware",
            "does not toggle PureSignal",
            "does not change operator defaults"
        )
        actions = @(
            "read WDSP-backed offline-fixture-metrics.json",
            "summarize tx-two-tone and tx-voice-like comparison metrics",
            "require TX stage meter snapshots for each comparison",
            "fail on clipping, excessive output peak, excessive ALC/CFC/leveler gain reduction, missing scenarios, missing required comparisons, or non-WDSP evidence"
        )
    } | ConvertTo-Json -Depth 16
    exit 0
}

if (-not (Test-Path -LiteralPath $MetricsPath -PathType Leaf)) {
    throw "offline-fixture-metrics file not found: $MetricsPath"
}

if ((Test-Path -LiteralPath $ReportPath -PathType Leaf) -and -not $Force) {
    throw "Report already exists: $ReportPath (use -Force to overwrite)"
}

$metrics = Read-JsonFile $MetricsPath
$metricsSha256 = Get-FileSha256 $MetricsPath
$evidenceEngine = [string](Get-JsonValue $metrics "evidenceEngine")
$evidenceTool = [string](Get-JsonValue $metrics "tool")
$wdspBacked = [string]::Equals($evidenceEngine, "wdsp", [StringComparison]::OrdinalIgnoreCase)
$runtimeRid = [string](Get-JsonValue $metrics "wdspRuntimeRid")
$runtimeSha256 = ([string](Get-JsonValue $metrics "wdspRuntimeSha256")).Trim().ToLowerInvariant()
$runtimeStatus = [string](Get-JsonValue $metrics "wdspRuntimeStatus")

$scenarioRecords = New-Object System.Collections.Generic.List[object]
$missingScenarioIds = New-Object System.Collections.Generic.List[string]
$missingComparisonIds = New-Object System.Collections.Generic.List[string]
$gateFailureCount = 0
$comparisonCount = 0
$clippingCountTotal = 0
$maxOutputPeak = -400.0
$maxAlcGr = 0.0
$maxCfcGr = 0.0
$maxLevelerGr = 0.0
$maxProcessingElapsed = 0.0
$minThroughputRatio = $null

foreach ($scenarioId in $scenarioScope) {
    $scenario = $null
    foreach ($candidate in @(Get-JsonArray $metrics "scenarios")) {
        if ([string]::Equals([string](Get-JsonValue $candidate "scenarioId"), $scenarioId, [StringComparison]::Ordinal)) {
            $scenario = $candidate
            break
        }
    }

    if ($null -eq $scenario) {
        $missingScenarioIds.Add($scenarioId) | Out-Null
        continue
    }

    $comparisonRecords = New-Object System.Collections.Generic.List[object]
    $scenarioGateFailureCount = 0
    $scenarioComparisonIds = New-Object System.Collections.Generic.HashSet[string]([StringComparer]::Ordinal)

    foreach ($comparison in @(Get-JsonArray $scenario "comparisons")) {
        $comparisonId = [string](Get-JsonValue $comparison "comparisonId")
        if ([string]::IsNullOrWhiteSpace($comparisonId)) { continue }
        if (-not $comparisonScope.Contains($comparisonId)) { continue }
        [void]$scenarioComparisonIds.Add($comparisonId)

        $metricsObject = Get-JsonValue $comparison "metrics"
        $txMeters = Get-JsonValue $comparison "txStageMeters"
        $candidateDiagnostics = Get-JsonValue $comparison "candidateDiagnostics"
        $gates = New-Object System.Collections.Generic.List[object]

        $clippingCount = [int](Get-NumericOrDefault (Get-MetricValue $metricsObject "clipping count") 0.0)
        $rawOutputPeak = Get-TxMeterValue $txMeters "rawOutPkDbfs"
        if ($null -eq $rawOutputPeak) { $rawOutputPeak = Get-TxMeterValue $txMeters "outPkDbfs" }
        if ($null -eq $rawOutputPeak) { $rawOutputPeak = Get-MetricValue $metricsObject "TX output peak" }
        $rawOutputAverage = Get-TxMeterValue $txMeters "rawOutAvDbfs"
        if ($null -eq $rawOutputAverage) { $rawOutputAverage = Get-TxMeterValue $txMeters "outAvDbfs" }
        if ($null -eq $rawOutputAverage) { $rawOutputAverage = Get-MetricValue $metricsObject "TX output average" }
        $effectiveOutputPeak = Get-TxMeterValue $txMeters "effectiveOutPkDbfs"
        $effectiveOutputAverage = Get-TxMeterValue $txMeters "effectiveOutAvDbfs"
        $panelGainTrimDb = Get-TxMeterValue $txMeters "txPanelGainTrimDb"
        $outputTrimDb = Get-TxMeterValue $txMeters "txOutputTrimDb"
        $untrimmedEstimatedOutputPeak = Get-TxMeterValue $txMeters "untrimmedEstimatedOutPkDbfs"
        $untrimmedEstimatedOutputAverage = Get-TxMeterValue $txMeters "untrimmedEstimatedOutAvDbfs"
        $outputPeak = if ($null -ne $effectiveOutputPeak) { $effectiveOutputPeak } else { $rawOutputPeak }
        $outputAverage = if ($null -ne $effectiveOutputAverage) { $effectiveOutputAverage } else { $rawOutputAverage }
        $alcGr = Get-TxMeterValue $txMeters "alcGainReductionDb"
        if ($null -eq $alcGr) { $alcGr = Get-MetricValue $metricsObject "TX ALC gain reduction" }
        $cfcGr = Get-TxMeterValue $txMeters "cfcGainReductionDb"
        if ($null -eq $cfcGr) { $cfcGr = Get-MetricValue $metricsObject "TX CFC gain reduction" }
        $levelerGr = Get-TxMeterValue $txMeters "levelerGainReductionDb"
        if ($null -eq $levelerGr) { $levelerGr = Get-MetricValue $metricsObject "TX leveler gain reduction" }
        $processingElapsed = Get-MetricValue $metricsObject "processing elapsed ms"
        $throughputRatio = Get-MetricValue $metricsObject "throughput ratio"
        $intermodulationProxy = Get-MetricValue $metricsObject "intermodulation proxy"
        $crestFactor = Get-MetricValue $metricsObject "crest factor"
        $peak = Get-MetricValue $metricsObject "peak"
        $rms = Get-MetricValue $metricsObject "RMS"

        $stageMetersPresent = $null -ne $txMeters -and $null -ne $rawOutputPeak -and $rawOutputPeak -gt -399.0
        Add-Gate $gates "tx-stage-meters-present" $stageMetersPresent "TX stage meters must be captured from WDSP for TX fixture safety review." $rawOutputPeak "finite raw outPkDbfs"
        Add-Gate $gates "tx-clipping-clear" ($clippingCount -le $MaxClippingCount) "TX fixture output must not clip." $clippingCount $MaxClippingCount
        Add-Gate $gates "tx-output-peak-headroom" ($null -ne $outputPeak -and $outputPeak -le $MaxOutputPeakDbfs) "TX output peak must leave headroom below 0 dBFS; candidate profiles may report an explicit effective post-trim peak beside the raw WDSP meter." $outputPeak $MaxOutputPeakDbfs
        Add-Gate $gates "tx-alc-gr-bounded" ($null -ne $alcGr -and $alcGr -le $MaxAlcGainReductionDb) "TX ALC gain reduction must stay bounded before density tuning." $alcGr $MaxAlcGainReductionDb
        Add-Gate $gates "tx-cfc-gr-bounded" ($null -ne $cfcGr -and $cfcGr -le $MaxCfcGainReductionDb) "TX CFC gain reduction must stay bounded." $cfcGr $MaxCfcGainReductionDb
        Add-Gate $gates "tx-leveler-gr-bounded" ($null -ne $levelerGr -and $levelerGr -le $MaxLevelerGainReductionDb) "TX leveler gain reduction must stay bounded." $levelerGr $MaxLevelerGainReductionDb

        $comparisonGateFailureCount = @($gates | Where-Object { -not (Get-JsonValue $_ "passed") }).Count
        $scenarioGateFailureCount += $comparisonGateFailureCount
        $gateFailureCount += $comparisonGateFailureCount
        $comparisonCount++
        $clippingCountTotal += $clippingCount
        if ($null -ne $outputPeak) { $maxOutputPeak = [Math]::Max($maxOutputPeak, [double]$outputPeak) }
        if ($null -ne $alcGr) { $maxAlcGr = [Math]::Max($maxAlcGr, [double]$alcGr) }
        if ($null -ne $cfcGr) { $maxCfcGr = [Math]::Max($maxCfcGr, [double]$cfcGr) }
        if ($null -ne $levelerGr) { $maxLevelerGr = [Math]::Max($maxLevelerGr, [double]$levelerGr) }
        if ($null -ne $processingElapsed) { $maxProcessingElapsed = [Math]::Max($maxProcessingElapsed, [double]$processingElapsed) }
        if ($null -ne $throughputRatio) {
            $minThroughputRatio = if ($null -eq $minThroughputRatio) { [double]$throughputRatio } else { [Math]::Min([double]$minThroughputRatio, [double]$throughputRatio) }
        }

        $comparisonRecords.Add([ordered]@{
                comparisonId = $comparisonId
                gateFailureCount = $comparisonGateFailureCount
                clippingCount = $clippingCount
                txOutputPeakDbfs = $outputPeak
                txOutputAverageDbfs = $outputAverage
                rawTxOutputPeakDbfs = $rawOutputPeak
                rawTxOutputAverageDbfs = $rawOutputAverage
                effectiveTxOutputPeakDbfs = $effectiveOutputPeak
                effectiveTxOutputAverageDbfs = $effectiveOutputAverage
                txPanelGainTrimDb = $panelGainTrimDb
                txOutputTrimDb = $outputTrimDb
                untrimmedEstimatedTxOutputPeakDbfs = $untrimmedEstimatedOutputPeak
                untrimmedEstimatedTxOutputAverageDbfs = $untrimmedEstimatedOutputAverage
                candidateDiagnostics = $candidateDiagnostics
                txAlcGainReductionDb = $alcGr
                txCfcGainReductionDb = $cfcGr
                txLevelerGainReductionDb = $levelerGr
                processingElapsedMs = $processingElapsed
                throughputRatio = $throughputRatio
                intermodulationProxy = $intermodulationProxy
                crestFactorDb = $crestFactor
                peak = $peak
                rms = $rms
                gates = @($gates.ToArray())
            }) | Out-Null
    }

    foreach ($requiredComparisonId in $comparisonScope) {
        if (-not $scenarioComparisonIds.Contains($requiredComparisonId)) {
            $missingComparisonIds.Add("$scenarioId/$requiredComparisonId") | Out-Null
            $scenarioGateFailureCount++
            $gateFailureCount++
        }
    }

    $scenarioRecords.Add([ordered]@{
            scenarioId = $scenarioId
            scenarioName = [string](Get-JsonValue $scenario "scenarioName")
            signalPath = [string](Get-JsonValue $scenario "signalPath")
            comparisonCount = $comparisonRecords.Count
            requiredComparisonIds = @($comparisonScope)
            missingComparisonIds = @($comparisonScope | Where-Object { -not $scenarioComparisonIds.Contains([string]$_) })
            gateFailureCount = $scenarioGateFailureCount
            comparisons = @($comparisonRecords.ToArray())
        }) | Out-Null
}

if (-not $wdspBacked) { $gateFailureCount++ }
if ([string]::IsNullOrWhiteSpace($runtimeSha256) -or $runtimeSha256 -notmatch "^[0-9a-f]{64}$") { $gateFailureCount++ }
if (-not [string]::Equals($runtimeStatus, "found", [StringComparison]::OrdinalIgnoreCase)) { $gateFailureCount++ }
if ($missingScenarioIds.Count -gt 0) { $gateFailureCount += $missingScenarioIds.Count }

$readyForReview = ($wdspBacked -and
    $runtimeSha256 -match "^[0-9a-f]{64}$" -and
    [string]::Equals($runtimeStatus, "found", [StringComparison]::OrdinalIgnoreCase) -and
    $missingScenarioIds.Count -eq 0 -and
    $missingComparisonIds.Count -eq 0 -and
    $gateFailureCount -eq 0)
$status = if ($readyForReview) {
    "ready"
}
elseif (-not $wdspBacked) {
    "not-wdsp-backed"
}
elseif ($missingScenarioIds.Count -gt 0 -or $missingComparisonIds.Count -gt 0) {
    "coverage-incomplete"
}
else {
    "tx-fixture-gates-failed"
}

$report = [ordered]@{
    schemaVersion = 1
    tool = "summarize-dsp-tx-fixture-safety"
    evidenceKind = "tx-fixture-safety-report-json"
    generatedUtc = [DateTimeOffset]::UtcNow
    bundleDir = $bundlePath
    metricsPath = $MetricsPath
    metricsSha256 = $metricsSha256
    reportPath = $ReportPath
    readyForReview = $readyForReview
    status = $status
    evidenceEngine = $evidenceEngine
    evidenceTool = $evidenceTool
    wdspBackedEvidence = $wdspBacked
    wdspRuntimeRid = $runtimeRid
    wdspRuntimeSha256 = $runtimeSha256
    wdspRuntimeStatus = $runtimeStatus
    scenarioIds = @($scenarioScope)
    requiredComparisonIds = @($comparisonScope)
    scenarioCount = $scenarioRecords.Count
    comparisonCount = $comparisonCount
    missingScenarioCount = $missingScenarioIds.Count
    missingScenarioIds = @($missingScenarioIds.ToArray())
    missingComparisonCount = $missingComparisonIds.Count
    missingComparisonIds = @($missingComparisonIds.ToArray())
    gateFailureCount = $gateFailureCount
    clippingCountTotal = $clippingCountTotal
    maxTxOutputPeakDbfs = [Math]::Round($maxOutputPeak, 6)
    maxTxAlcGainReductionDb = [Math]::Round($maxAlcGr, 6)
    maxTxCfcGainReductionDb = [Math]::Round($maxCfcGr, 6)
    maxTxLevelerGainReductionDb = [Math]::Round($maxLevelerGr, 6)
    maxProcessingElapsedMs = [Math]::Round($maxProcessingElapsed, 6)
    minThroughputRatio = if ($null -eq $minThroughputRatio) { $null } else { [Math]::Round([double]$minThroughputRatio, 6) }
    thresholds = [ordered]@{
        maxClippingCount = $MaxClippingCount
        maxOutputPeakDbfs = $MaxOutputPeakDbfs
        maxAlcGainReductionDb = $MaxAlcGainReductionDb
        maxCfcGainReductionDb = $MaxCfcGainReductionDb
        maxLevelerGainReductionDb = $MaxLevelerGainReductionDb
    }
    defaultBehaviorChanged = $false
    limitations = @(
        "Offline TX fixture safety summarizes WdspDspEngine TXA output and stage meters only.",
        "It does not key radio hardware, prove RF linearity, or replace G2 PureSignal disabled/enabled bench evidence.",
        "Default DSP behavior changes remain blocked without explicit approval and live/cross-radio evidence."
    )
    scenarios = @($scenarioRecords.ToArray())
}

Write-JsonFile -Path $ReportPath -Value $report

if ($JsonOnly) {
    $report | ConvertTo-Json -Depth 32
}
else {
    if ($readyForReview) {
        Write-Host "TX fixture safety report ready: $ReportPath"
    }
    else {
        Write-Host "TX fixture safety report not ready: $ReportPath"
    }
    Write-Host "Status: $status"
    Write-Host "Gate failures: $gateFailureCount, clipping: $clippingCountTotal, max TX out peak: $([Math]::Round($maxOutputPeak, 3)) dBFS"
}

if ($FailOnGate -and -not $readyForReview) {
    exit 1
}
