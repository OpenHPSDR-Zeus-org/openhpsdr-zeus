param(
    [string]$BaseUrl = "http://localhost:6060",

    [int]$PollCount = 30,

    [int]$PollIntervalSec = 2,

    [int]$StablePolls = 3,

    [double]$MinCoherentSnrDb = 12.0,

    [string]$SceneProfilePattern = "voice|speech|phone|dx",

    [switch]$RequireFrontendNearPassband,

    [switch]$RequireNr5CaptureReady,

    [int]$FrontendNearPassbandThresholdHz = 3000,

    [int]$FrontendOffsetMismatchToleranceHz = 25,

    [int]$SuggestedVfoStepHz = 1000,

    [int]$MaxCaptures = 4,

    [int]$MaxCapturesPerVfo = 1,

    [int]$CaptureSamples = 16,

    [int]$CaptureIntervalMs = 250,

    [int]$TimeoutSec = 8,

    [string]$OutputRoot = "",

    [string]$ReportPath = "",

    [string]$BundleDir = "",

    [string]$WatchScriptPath = "",

    [string]$Label = "manual-tune-observer",

    [string]$ScenarioId = "g2-manual-tune-observer",

    [string]$ComparisonId = "nr5-spnr",

    [switch]$SkipCertificateCheck,

    [switch]$AllowStaleSceneCapture,

    [switch]$PlanOnly,

    [switch]$JsonOnly,

    [switch]$ContinueOnError
)

$ErrorActionPreference = "Stop"

function Normalize-BaseUrl {
    param([string]$Url)
    if ([string]::IsNullOrWhiteSpace($Url)) {
        return "http://localhost:6060"
    }

    return $Url.Trim().TrimEnd("/")
}

function Enable-CertificateBypass {
    if (-not ("TrustAllCertsPolicy" -as [type])) {
        Add-Type @"
using System.Net;
using System.Security.Cryptography.X509Certificates;
public sealed class TrustAllCertsPolicy : ICertificatePolicy {
    public bool CheckValidationResult(ServicePoint srvPoint, X509Certificate certificate, WebRequest request, int certificateProblem) {
        return true;
    }
}
"@
    }

    [System.Net.ServicePointManager]::CertificatePolicy = New-Object TrustAllCertsPolicy
    [System.Net.ServicePointManager]::ServerCertificateValidationCallback = {
        param($sender, $certificate, $chain, $sslPolicyErrors)
        return $true
    }
}

function Get-RepoRoot {
    $dir = (Get-Location).Path
    while (-not [string]::IsNullOrWhiteSpace($dir)) {
        if (Test-Path -LiteralPath (Join-Path $dir "tools\watch-dsp-live-diagnostics.ps1")) {
            return $dir
        }

        $parent = [System.IO.Directory]::GetParent($dir)
        if ($null -eq $parent) {
            break
        }

        $dir = $parent.FullName
    }

    return (Get-Location).Path
}

function Invoke-JsonGet {
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [int]$RequestTimeoutSec
    )

    return Invoke-RestMethod -Method Get -Uri $Url -TimeoutSec $RequestTimeoutSec
}

function Get-JsonValue {
    param($Object, [string]$Name)
    if ($null -eq $Object) {
        return $null
    }

    if ($Object -is [System.Collections.IDictionary] -and $Object.Contains($Name)) {
        return $Object[$Name]
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Get-JsonArray {
    param($Object, [string]$Name)
    $value = Get-JsonValue $Object $Name
    if ($null -eq $value) {
        return @()
    }

    if ($value -is [System.Array]) {
        return @($value)
    }

    return @($value)
}

function Get-NullableLongValue {
    param($Value)
    if ($null -eq $Value) {
        return $null
    }

    try {
        return [long]$Value
    }
    catch {
        return $null
    }
}

function Get-NullableDoubleValue {
    param($Value)
    if ($null -eq $Value) {
        return $null
    }

    try {
        return [double]$Value
    }
    catch {
        return $null
    }
}

function Get-IntValue {
    param($Value)
    if ($null -eq $Value) {
        return 0
    }

    try {
        return [int]$Value
    }
    catch {
        return 0
    }
}

function Get-FrontendPeakOffsetEvidence {
    param(
        $Peak,
        $StateVfoHz,
        [int]$MismatchToleranceHz
    )

    $offset = Get-NullableDoubleValue (Get-JsonValue $Peak "offsetHz")
    $frequencyHz = Get-NullableLongValue (Get-JsonValue $Peak "frequencyHz")
    $vfoHz = Get-NullableLongValue $StateVfoHz
    $computedOffset = $null
    $mismatchHz = $null
    $consistent = $false

    if ($null -ne $frequencyHz -and $null -ne $vfoHz) {
        $computedOffset = [double]$frequencyHz - [double]$vfoHz
    }

    if ($null -ne $offset -and $null -ne $computedOffset) {
        $mismatchHz = [Math]::Abs([double]$offset - [double]$computedOffset)
        $consistent = ([double]$mismatchHz -le [double]$MismatchToleranceHz)
    }
    elseif ($null -ne $offset) {
        $consistent = $true
    }
    elseif ($null -ne $computedOffset) {
        $offset = $computedOffset
        $consistent = $true
    }

    return [ordered]@{
        offsetHz = $offset
        computedOffsetHz = $computedOffset
        offsetMismatchHz = $mismatchHz
        offsetConsistent = $consistent
    }
}

function Get-FrontendPeakFilterDistanceHz {
    param(
        $OffsetHz,
        $FilterLowHz,
        $FilterHighHz
    )

    $offset = Get-NullableDoubleValue $OffsetHz
    $low = Get-NullableDoubleValue $FilterLowHz
    $high = Get-NullableDoubleValue $FilterHighHz
    if ($null -eq $offset -or $null -eq $low -or $null -eq $high) {
        return $null
    }

    $passLow = [Math]::Min([double]$low, [double]$high)
    $passHigh = [Math]::Max([double]$low, [double]$high)
    if ($passHigh -le $passLow) {
        return $null
    }

    if ([double]$offset -lt $passLow) {
        return [Math]::Round($passLow - [double]$offset, 3)
    }

    if ([double]$offset -gt $passHigh) {
        return [Math]::Round([double]$offset - $passHigh, 3)
    }

    return 0.0
}

function Get-SteppedFrequencyHz {
    param(
        $FrequencyHz,
        [int]$StepHz
    )

    $frequency = Get-NullableDoubleValue $FrequencyHz
    if ($null -eq $frequency) {
        return $null
    }

    if ($StepHz -le 0) {
        return [long][Math]::Round([double]$frequency)
    }

    return [long]([Math]::Floor(([double]$frequency / [double]$StepHz) + 0.5) * [double]$StepHz)
}

function Get-FrontendTuningHint {
    param(
        $Peak,
        $OffsetHz,
        $FilterDistanceHz,
        $StateVfoHz,
        $FilterLowHz,
        $FilterHighHz,
        [int]$StepHz
    )

    $offset = Get-NullableDoubleValue $OffsetHz
    $distance = Get-NullableDoubleValue $FilterDistanceHz
    $vfo = Get-NullableLongValue $StateVfoHz
    $low = Get-NullableDoubleValue $FilterLowHz
    $high = Get-NullableDoubleValue $FilterHighHz
    if ($null -eq $offset -or $null -eq $distance -or $null -eq $vfo -or $null -eq $low -or $null -eq $high) {
        return $null
    }

    $passLow = [Math]::Min([double]$low, [double]$high)
    $passHigh = [Math]::Max([double]$low, [double]$high)
    if ($passHigh -le $passLow) {
        return $null
    }

    $targetOffset = [Math]::Round(($passLow + $passHigh) / 2.0, 3)
    $exactShiftHz = [Math]::Round([double]$offset - $targetOffset, 3)
    $exactSuggestedVfoHz = [long][Math]::Round([double]$vfo + $exactShiftHz)
    $suggestedVfoHz = Get-SteppedFrequencyHz -FrequencyHz $exactSuggestedVfoHz -StepHz $StepHz
    $suggestedShiftHz = [Math]::Round([double]$suggestedVfoHz - [double]$vfo, 3)
    $reason = if ([double]$distance -le 0.0) {
        "already-in-filter"
    }
    elseif ([double]$offset -lt $passLow) {
        "below-filter"
    }
    else {
        "above-filter"
    }

    return [ordered]@{
        reason = $reason
        peakFrequencyHz = Get-NullableLongValue (Get-JsonValue $Peak "frequencyHz")
        peakOffsetHz = $offset
        peakSnrDb = Get-NullableDoubleValue (Get-JsonValue $Peak "snrDb")
        peakDbfs = Get-NullableDoubleValue (Get-JsonValue $Peak "dbfs")
        peakConfidence = Get-NullableDoubleValue (Get-JsonValue $Peak "confidence")
        filterLowHz = $passLow
        filterHighHz = $passHigh
        filterCenterOffsetHz = $targetOffset
        filterDistanceHz = $distance
        currentVfoHz = $vfo
        suggestedDialShiftHz = $suggestedShiftHz
        suggestedVfoHz = $suggestedVfoHz
        suggestedVfoMhz = [Math]::Round([double]$suggestedVfoHz / 1000000.0, 6)
        suggestedVfoStepHz = $StepHz
        exactSuggestedDialShiftHz = $exactShiftHz
        exactSuggestedVfoHz = $exactSuggestedVfoHz
        exactSuggestedVfoMhz = [Math]::Round([double]$exactSuggestedVfoHz / 1000000.0, 6)
    }
}

function Test-Truthy {
    param($Value)
    if ($null -eq $Value) {
        return $false
    }

    if ($Value -is [bool]) {
        return [bool]$Value
    }

    if ($Value -is [string]) {
        return [string]::Equals($Value, "true", [StringComparison]::OrdinalIgnoreCase)
    }

    try {
        return ([double]$Value) -ne 0.0
    }
    catch {
        return $false
    }
}

function ConvertTo-SafeName {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) {
        return "manual-tune"
    }

    return ([regex]::Replace($Value.Trim(), "[^A-Za-z0-9_.-]+", "-")).Trim("-")
}

function Write-JsonFile {
    param([string]$Path, $Value)
    $parent = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }

    $Value | ConvertTo-Json -Depth 64 | Set-Content -LiteralPath $Path -Encoding UTF8
}

function Format-MhzText {
    param($Hz, $Mhz)

    $mhzValue = Get-NullableDoubleValue $Mhz
    if ($null -eq $mhzValue) {
        $hzValue = Get-NullableLongValue $Hz
        if ($null -ne $hzValue) {
            $mhzValue = [Math]::Round([double]$hzValue / 1000000.0, 6)
        }
    }

    if ($null -eq $mhzValue) {
        return ""
    }

    return "{0:N6} MHz" -f [double]$mhzValue
}

function New-ManualTuneObserverCommandTemplate {
    param(
        [int]$Samples,
        [int]$MaxPerVfo
    )

    $parts = New-Object System.Collections.Generic.List[string]
    $parts.Add("powershell -NoProfile -ExecutionPolicy Bypass -File tools\watch-dsp-manual-tune-observer.ps1") | Out-Null
    $parts.Add("-BaseUrl `"$base`"") | Out-Null
    if (-not [string]::IsNullOrWhiteSpace($bundlePath)) {
        $parts.Add("-BundleDir `"$bundlePath`"") | Out-Null
    }
    elseif (-not [string]::IsNullOrWhiteSpace($OutputRoot)) {
        $parts.Add("-OutputRoot `"$OutputRoot`"") | Out-Null
    }
    $parts.Add("-Label `"$Label`"") | Out-Null
    $parts.Add("-ScenarioId `"$ScenarioId`"") | Out-Null
    $parts.Add("-ComparisonId `"$ComparisonId`"") | Out-Null
    $parts.Add("-RequireFrontendNearPassband") | Out-Null
    $parts.Add("-RequireNr5CaptureReady") | Out-Null
    $parts.Add("-SuggestedVfoStepHz $SuggestedVfoStepHz") | Out-Null
    $parts.Add("-MaxCapturesPerVfo $MaxPerVfo") | Out-Null
    $parts.Add("-CaptureSamples $Samples") | Out-Null
    $parts.Add("-CaptureIntervalMs $CaptureIntervalMs") | Out-Null
    if ($SkipCertificateCheck) {
        $parts.Add("-SkipCertificateCheck") | Out-Null
    }

    return ($parts.ToArray() -join " ")
}

function New-ManualTuneAction {
    param(
        [Parameter(Mandatory = $true)][string]$ActionId,
        [Parameter(Mandatory = $true)][string]$Status,
        [int]$Priority,
        [Parameter(Mandatory = $true)][string]$Summary,
        [string]$ManualAction = "",
        [string]$CommandTemplate = "",
        [string]$FollowUp = "",
        $VfoHz = $null,
        $VfoMhz = $null,
        $SuggestedVfoHz = $null,
        $SuggestedVfoMhz = $null,
        $SuggestedDialShiftHz = $null,
        $ExactSuggestedVfoHz = $null,
        $ExactSuggestedVfoMhz = $null,
        $ExactSuggestedDialShiftHz = $null,
        $SuggestedVfoStepHzValue = $null,
        [string]$SuggestedTuneReason = "",
        [string]$ReportPathValue = "",
        [string]$JsonlPathValue = "",
        $DistanceToStrongThresholdDb = $null,
        [int]$WeakInputSampleCount = 0,
        [int]$StrongInputSampleCount = 0,
        [int]$NearStrongInputSampleCount = 0,
        [string]$ExpectedEvidence = ""
    )

    return [ordered]@{
        actionId = $ActionId
        status = $Status
        priority = $Priority
        summary = $Summary
        manualAction = $ManualAction
        commandTemplate = $CommandTemplate
        followUp = $FollowUp
        vfoHz = Get-NullableLongValue $VfoHz
        vfoMhz = Get-NullableDoubleValue $VfoMhz
        suggestedVfoHz = Get-NullableLongValue $SuggestedVfoHz
        suggestedVfoMhz = Get-NullableDoubleValue $SuggestedVfoMhz
        suggestedDialShiftHz = Get-NullableDoubleValue $SuggestedDialShiftHz
        exactSuggestedVfoHz = Get-NullableLongValue $ExactSuggestedVfoHz
        exactSuggestedVfoMhz = Get-NullableDoubleValue $ExactSuggestedVfoMhz
        exactSuggestedDialShiftHz = Get-NullableDoubleValue $ExactSuggestedDialShiftHz
        suggestedVfoStepHz = Get-NullableLongValue $SuggestedVfoStepHzValue
        suggestedTuneReason = $SuggestedTuneReason
        reportPath = $ReportPathValue
        jsonlPath = $JsonlPathValue
        distanceToStrongThresholdDb = Get-NullableDoubleValue $DistanceToStrongThresholdDb
        weakInputSampleCount = $WeakInputSampleCount
        strongInputSampleCount = $StrongInputSampleCount
        nearStrongInputSampleCount = $NearStrongInputSampleCount
        expectedEvidence = $ExpectedEvidence
    }
}

function ConvertTo-BundleRelativeEvidencePath {
    param([string]$Path, [string]$BundlePath)
    if ([string]::IsNullOrWhiteSpace($Path) -or [string]::IsNullOrWhiteSpace($BundlePath)) {
        return $Path
    }

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullBundlePath = [System.IO.Path]::GetFullPath($BundlePath)
    $separatorChars = [char[]]@([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    $bundleRoot = $fullBundlePath.TrimEnd($separatorChars)
    $bundleRootWithSeparator = $bundleRoot + [System.IO.Path]::DirectorySeparatorChar

    if ([string]::Equals($fullPath, $bundleRoot, [StringComparison]::OrdinalIgnoreCase)) {
        return "."
    }

    if (-not $fullPath.StartsWith($bundleRootWithSeparator, [StringComparison]::OrdinalIgnoreCase)) {
        return $Path
    }

    return $fullPath.Substring($bundleRootWithSeparator.Length).Replace('\', '/')
}

function Test-PortableEvidencePath {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $false
    }

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $false
    }

    return (-not $Path.StartsWith("../", [StringComparison]::Ordinal)) -and
        (-not $Path.StartsWith("..\", [StringComparison]::Ordinal))
}

function Invoke-WatchCapture {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [Parameter(Mandatory = $true)][string]$Base,
        [Parameter(Mandatory = $true)][string]$Scenario,
        [Parameter(Mandatory = $true)][string]$Comparison,
        [Parameter(Mandatory = $true)][string]$CaptureLabel,
        [Parameter(Mandatory = $true)][string]$SummaryPath,
        [Parameter(Mandatory = $true)][string]$JsonlPath,
        [int]$SampleCount,
        [int]$DelayMs,
        [int]$RequestTimeoutSec,
        [switch]$SkipCertificate
    )

    $args = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", $ScriptPath,
        "-BaseUrl", $Base,
        "-Samples", ([string]$SampleCount),
        "-IntervalMs", ([string]$DelayMs),
        "-TimeoutSec", ([string]$RequestTimeoutSec),
        "-Label", $CaptureLabel,
        "-ScenarioId", $Scenario,
        "-ComparisonId", $Comparison,
        "-ReportPath", $SummaryPath,
        "-JsonlPath", $JsonlPath,
        "-JsonOnly"
    )
    if ($SkipCertificate) {
        $args += "-SkipCertificateCheck"
    }

    $output = & powershell @args 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        return [ordered]@{
            ok = $false
            exitCode = $exitCode
            error = ($output -join [Environment]::NewLine)
            report = $null
        }
    }

    return [ordered]@{
        ok = $true
        exitCode = $exitCode
        error = ""
        report = Get-Content -Raw -LiteralPath $SummaryPath | ConvertFrom-Json
    }
}

if ($PollCount -lt 1) {
    $PollCount = 1
}
if ($PollIntervalSec -lt 0) {
    $PollIntervalSec = 0
}
if ($StablePolls -lt 1) {
    $StablePolls = 1
}
if ($FrontendNearPassbandThresholdHz -lt 0) {
    $FrontendNearPassbandThresholdHz = 0
}
if ($FrontendOffsetMismatchToleranceHz -lt 0) {
    $FrontendOffsetMismatchToleranceHz = 0
}
if ($SuggestedVfoStepHz -lt 0) {
    $SuggestedVfoStepHz = 0
}
if ($MaxCaptures -lt 0) {
    $MaxCaptures = 0
}
if ($MaxCapturesPerVfo -lt 1) {
    $MaxCapturesPerVfo = 1
}
if ($CaptureSamples -lt 1) {
    $CaptureSamples = 1
}
if ($CaptureIntervalMs -lt 0) {
    $CaptureIntervalMs = 0
}
if ($TimeoutSec -lt 1) {
    $TimeoutSec = 1
}

$repoRoot = Get-RepoRoot
$base = Normalize-BaseUrl $BaseUrl
if ($SkipCertificateCheck) {
    Enable-CertificateBypass
}
$bundlePath = ""
if (-not [string]::IsNullOrWhiteSpace($BundleDir)) {
    $bundlePath = [System.IO.Path]::GetFullPath($BundleDir)
}
if ([string]::IsNullOrWhiteSpace($WatchScriptPath)) {
    $WatchScriptPath = Join-Path $repoRoot "tools\watch-dsp-live-diagnostics.ps1"
}
$resolvedWatchScript = (Resolve-Path -LiteralPath $WatchScriptPath).Path

if ($PlanOnly) {
    [ordered]@{
        schemaVersion = 1
        tool = "watch-dsp-manual-tune-observer"
        mode = "plan-only"
        baseUrl = $base
        pollCount = $PollCount
        pollIntervalSec = $PollIntervalSec
        stablePolls = $StablePolls
        minCoherentSnrDb = $MinCoherentSnrDb
        sceneProfilePattern = $SceneProfilePattern
        requireFrontendNearPassband = [bool]$RequireFrontendNearPassband
        requireNr5CaptureReady = [bool]$RequireNr5CaptureReady
        frontendNearPassbandThresholdHz = $FrontendNearPassbandThresholdHz
        frontendOffsetMismatchToleranceHz = $FrontendOffsetMismatchToleranceHz
        suggestedVfoStepHz = $SuggestedVfoStepHz
        maxCaptures = $MaxCaptures
        maxCapturesPerVfo = $MaxCapturesPerVfo
        allowStaleSceneCapture = [bool]$AllowStaleSceneCapture
        captureSamples = $CaptureSamples
        captureIntervalMs = $CaptureIntervalMs
        scenarioId = $ScenarioId
        comparisonId = $ComparisonId
        watchScriptPath = $resolvedWatchScript
        bundleDir = $bundlePath
        bundleRelativePaths = (-not [string]::IsNullOrWhiteSpace($bundlePath))
        safety = [ordered]@{
            rxOnly = $true
            readOnly = $true
            apiWrites = $false
            retune = $false
            txEndpointsTouched = $false
            observedEndpoints = @(
                "/api/state",
                "/api/radio/diagnostics/dsp-scene",
                "/api/dsp/live-diagnostics"
            )
            delegatedCapture = "watch-dsp-live-diagnostics.ps1"
            notes = @(
                "This tool is for operator/manual tuning; it never posts VFO, LO, TX, or DSP settings.",
                "It captures a watch-dsp-live-diagnostics window only after the current VFO is stable and the scene looks active.",
                "Use -RequireFrontendNearPassband for acceptance-oriented runs so off-passband frontend peaks do not consume capture slots; strict runs use the signed RX filter window from /api/state when available.",
                "Use -RequireNr5CaptureReady for NR5/SPNR acceptance-oriented runs so child captures are not spent while requested/effective NR mode or diagnostics are not ready.",
                "Operator tune suggestions are snapped to -SuggestedVfoStepHz; exact FFT-bin-derived targets remain in exactSuggested* fields for diagnostics.",
                "Traces captured while the operator continues tuning are scouting evidence, not final acceptance proof."
            )
        }
        example = if ([string]::IsNullOrWhiteSpace($bundlePath)) {
            "powershell -NoProfile -ExecutionPolicy Bypass -File tools\watch-dsp-manual-tune-observer.ps1 -BaseUrl $base -PollCount 60 -StablePolls 3 -MaxCaptures 4 -MaxCapturesPerVfo 2 -RequireFrontendNearPassband -RequireNr5CaptureReady -SuggestedVfoStepHz $SuggestedVfoStepHz -AllowStaleSceneCapture"
        }
        else {
            "powershell -NoProfile -ExecutionPolicy Bypass -File tools\watch-dsp-manual-tune-observer.ps1 -BaseUrl $base -BundleDir `"$bundlePath`" -OutputRoot `"$bundlePath\artifacts\manual-tune-observer`" -ReportPath `"$bundlePath\artifacts\manual-tune-observer-report.json`" -PollCount 60 -StablePolls 3 -MaxCaptures 4 -MaxCapturesPerVfo 2 -RequireFrontendNearPassband -RequireNr5CaptureReady -SuggestedVfoStepHz $SuggestedVfoStepHz -AllowStaleSceneCapture"
        }
    } | ConvertTo-Json -Depth 16
    exit 0
}

$startedUtc = [DateTimeOffset]::UtcNow
$safeLabel = ConvertTo-SafeName $Label
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = if ([string]::IsNullOrWhiteSpace($bundlePath)) {
        Join-Path $repoRoot "captures\dsp-manual-tune-observer"
    }
    else {
        Join-Path $bundlePath "artifacts\manual-tune-observer"
    }
}
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$captureRoot = Join-Path $OutputRoot ("{0}-{1}" -f $startedUtc.ToString("yyyyMMddTHHmmssfffZ"), $safeLabel)
New-Item -ItemType Directory -Force -Path $captureRoot | Out-Null

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = if ([string]::IsNullOrWhiteSpace($bundlePath)) {
        Join-Path $captureRoot "manual-tune-observer-report.json"
    }
    else {
        Join-Path $bundlePath "artifacts\manual-tune-observer-report.json"
    }
}
$ReportPath = [System.IO.Path]::GetFullPath($ReportPath)

$polls = New-Object System.Collections.Generic.List[object]
$captures = New-Object System.Collections.Generic.List[object]
$capturedVfoMap = @{}
$lastVfo = $null
$stableCount = 0
$scanError = ""

try {
    for ($poll = 1; $poll -le $PollCount; $poll++) {
        $pollUtc = [DateTimeOffset]::UtcNow
        $state = Invoke-JsonGet -Url "$base/api/state" -RequestTimeoutSec $TimeoutSec
        $scene = Invoke-JsonGet -Url "$base/api/radio/diagnostics/dsp-scene" -RequestTimeoutSec $TimeoutSec
        $live = Invoke-JsonGet -Url "$base/api/dsp/live-diagnostics" -RequestTimeoutSec $TimeoutSec

        $vfo = Get-NullableLongValue (Get-JsonValue $state "vfoHz")
        if ($null -ne $lastVfo -and $null -ne $vfo -and [long]$lastVfo -eq [long]$vfo) {
            $stableCount++
        }
        else {
            $stableCount = 1
            $lastVfo = $vfo
        }

        $profile = [string](Get-JsonValue $scene "signalProfile")
        $coherentSnr = Get-NullableDoubleValue (Get-JsonValue $scene "coherentMaxSnrDb")
        $sceneFresh = Test-Truthy (Get-JsonValue $scene "fresh")
        $profileMatches = (-not [string]::IsNullOrWhiteSpace($profile)) -and ($profile -match $SceneProfilePattern)
        $snrQualified = ($null -ne $coherentSnr -and [double]$coherentSnr -ge [double]$MinCoherentSnrDb)
        $stableQualified = ($null -ne $vfo -and $stableCount -ge $StablePolls)
        $staleSceneCaptureAllowed = (-not $sceneFresh) -and [bool]$AllowStaleSceneCapture
        $topPeaks = @(Get-JsonArray $scene "topPeaks")
        $frontendNearPassbandTopPeakCount = 0
        $frontendFilterPassbandTopPeakCount = 0
        $frontendOffsetMismatchTopPeakCount = 0
        $frontendNearestTopPeakOffsetHz = $null
        $frontendNearestTopPeakFrequencyHz = $null
        $frontendNearestTopPeakSnrDb = $null
        $frontendNearestTopPeakDbfs = $null
        $frontendNearestTopPeakConfidence = $null
        $frontendNearestFilterPassbandDistanceHz = $null
        $frontendNearestFilterPassbandPeakOffsetHz = $null
        $frontendNearestFilterPassbandPeakFrequencyHz = $null
        $frontendBestTopPeakOffsetHz = $null
        $frontendBestTopPeakFrequencyHz = $null
        $frontendBestTopPeakSnrDb = $null
        $frontendBestTopPeakDbfs = $null
        $frontendBestTopPeakConfidence = $null
        $frontendBestTuningHint = $null
        $filterLowHz = Get-NullableDoubleValue (Get-JsonValue $state "filterLowHz")
        $filterHighHz = Get-NullableDoubleValue (Get-JsonValue $state "filterHighHz")
        $frontendFilterPassbandKnown = ($null -ne $filterLowHz -and $null -ne $filterHighHz)
        foreach ($peak in $topPeaks) {
            $offsetEvidence = Get-FrontendPeakOffsetEvidence `
                -Peak $peak `
                -StateVfoHz $vfo `
                -MismatchToleranceHz $FrontendOffsetMismatchToleranceHz
            $offset = Get-NullableDoubleValue $offsetEvidence["offsetHz"]
            if ($null -eq $offset) {
                continue
            }
            $offsetConsistent = Test-Truthy $offsetEvidence["offsetConsistent"]
            if (-not $offsetConsistent) {
                $frontendOffsetMismatchTopPeakCount++
            }

            $absOffset = [Math]::Abs([double]$offset)
            if ($null -eq $frontendNearestTopPeakOffsetHz -or $absOffset -lt [Math]::Abs([double]$frontendNearestTopPeakOffsetHz)) {
                $frontendNearestTopPeakOffsetHz = $offset
                $frontendNearestTopPeakFrequencyHz = Get-NullableLongValue (Get-JsonValue $peak "frequencyHz")
                $frontendNearestTopPeakSnrDb = Get-NullableDoubleValue (Get-JsonValue $peak "snrDb")
                $frontendNearestTopPeakDbfs = Get-NullableDoubleValue (Get-JsonValue $peak "dbfs")
                $frontendNearestTopPeakConfidence = Get-NullableDoubleValue (Get-JsonValue $peak "confidence")
            }

            $snr = Get-NullableDoubleValue (Get-JsonValue $peak "snrDb")
            if ($null -ne $snr -and ($null -eq $frontendBestTopPeakSnrDb -or [double]$snr -gt [double]$frontendBestTopPeakSnrDb)) {
                $frontendBestTopPeakOffsetHz = $offset
                $frontendBestTopPeakFrequencyHz = Get-NullableLongValue (Get-JsonValue $peak "frequencyHz")
                $frontendBestTopPeakSnrDb = $snr
                $frontendBestTopPeakDbfs = Get-NullableDoubleValue (Get-JsonValue $peak "dbfs")
                $frontendBestTopPeakConfidence = Get-NullableDoubleValue (Get-JsonValue $peak "confidence")
            }
            if ($absOffset -le [double]$FrontendNearPassbandThresholdHz) {
                $frontendNearPassbandTopPeakCount++
            }

            $filterDistance = Get-FrontendPeakFilterDistanceHz `
                -OffsetHz $offset `
                -FilterLowHz $filterLowHz `
                -FilterHighHz $filterHighHz
            if ($null -ne $filterDistance) {
                if ($null -eq $frontendNearestFilterPassbandDistanceHz -or
                    [double]$filterDistance -lt [double]$frontendNearestFilterPassbandDistanceHz) {
                    $frontendNearestFilterPassbandDistanceHz = $filterDistance
                    $frontendNearestFilterPassbandPeakOffsetHz = $offset
                    $frontendNearestFilterPassbandPeakFrequencyHz = Get-NullableLongValue (Get-JsonValue $peak "frequencyHz")
                }

                if ($offsetConsistent -and [double]$filterDistance -le 0.0) {
                    $frontendFilterPassbandTopPeakCount++
                }

                if ($offsetConsistent) {
                    $hint = Get-FrontendTuningHint `
                        -Peak $peak `
                        -OffsetHz $offset `
                        -FilterDistanceHz $filterDistance `
                        -StateVfoHz $vfo `
                        -FilterLowHz $filterLowHz `
                        -FilterHighHz $filterHighHz `
                        -StepHz $SuggestedVfoStepHz
                    if ($null -ne $hint) {
                        $hintDistance = Get-NullableDoubleValue (Get-JsonValue $hint "filterDistanceHz")
                        $bestHintDistance = Get-NullableDoubleValue (Get-JsonValue $frontendBestTuningHint "filterDistanceHz")
                        $hintSnr = Get-NullableDoubleValue (Get-JsonValue $hint "peakSnrDb")
                        $bestHintSnr = Get-NullableDoubleValue (Get-JsonValue $frontendBestTuningHint "peakSnrDb")
                        if ($null -eq $frontendBestTuningHint -or
                            ($null -ne $hintDistance -and ($null -eq $bestHintDistance -or [double]$hintDistance -lt [double]$bestHintDistance)) -or
                            ($null -ne $hintDistance -and $null -ne $bestHintDistance -and [double]$hintDistance -eq [double]$bestHintDistance -and $null -ne $hintSnr -and ($null -eq $bestHintSnr -or [double]$hintSnr -gt [double]$bestHintSnr))) {
                            $frontendBestTuningHint = $hint
                        }
                    }
                }
            }
        }
        $frontendNearPassbandQualified = ($frontendNearPassbandTopPeakCount -gt 0)
        $frontendFilterPassbandQualified = ($frontendFilterPassbandKnown -and $frontendFilterPassbandTopPeakCount -gt 0)
        $frontendPassbandEvidenceQualified = if ($frontendFilterPassbandKnown) {
            $frontendFilterPassbandQualified
        }
        else {
            $false
        }
        $runtime = Get-JsonValue $live "runtimeEvidence"
        $nr5 = Get-JsonValue $live "nr5SpnrDiagnostics"
        $requestedNrMode = [string](Get-JsonValue $live "requestedNrMode")
        $effectiveNrMode = [string](Get-JsonValue $live "effectiveNrMode")
        $readyForNr5Tuning = Test-Truthy (Get-JsonValue $live "readyForNr5Tuning")
        $runtimeFresh = (
            [string]::Equals([string](Get-JsonValue $runtime "status"), "fresh", [StringComparison]::OrdinalIgnoreCase) -or
            [string]::Equals([string](Get-JsonValue $runtime "audioStatus"), "fresh", [StringComparison]::OrdinalIgnoreCase) -or
            (Test-Truthy (Get-JsonValue $runtime "audioFresh")))
        $nr5DiagnosticsPresent = ($null -ne $nr5)
        $nr5DiagnosticRun = Test-Truthy (Get-JsonValue $nr5 "run")
        $nr5CaptureReadinessConstraints = New-Object System.Collections.Generic.List[string]
        if (-not [string]::Equals($requestedNrMode, "Nr5", [StringComparison]::OrdinalIgnoreCase)) {
            $nr5CaptureReadinessConstraints.Add("requested-nr-mode-not-nr5") | Out-Null
        }
        if (-not [string]::Equals($effectiveNrMode, "Nr5", [StringComparison]::OrdinalIgnoreCase)) {
            $nr5CaptureReadinessConstraints.Add("effective-nr-mode-not-nr5") | Out-Null
        }
        if (-not $readyForNr5Tuning) {
            $nr5CaptureReadinessConstraints.Add("nr5-tuning-not-ready") | Out-Null
        }
        if (-not $nr5DiagnosticsPresent) {
            $nr5CaptureReadinessConstraints.Add("nr5-diagnostics-missing") | Out-Null
        }
        elseif (-not $nr5DiagnosticRun) {
            $nr5CaptureReadinessConstraints.Add("nr5-diagnostics-not-running") | Out-Null
        }
        if (-not $runtimeFresh) {
            $nr5CaptureReadinessConstraints.Add("runtime-audio-not-fresh") | Out-Null
        }
        $nr5CaptureReady = ($nr5CaptureReadinessConstraints.Count -eq 0)
        $nr5CaptureReadinessStatus = if ($nr5CaptureReady) {
            "ready"
        }
        elseif ($RequireNr5CaptureReady) {
            "blocked"
        }
        else {
            "advisory"
        }

        $capturePassbandQualified = (-not [bool]$RequireFrontendNearPassband) -or $frontendPassbandEvidenceQualified
        $baseCaptureQualified = ($stableQualified -and ($sceneFresh -or $staleSceneCaptureAllowed) -and $snrQualified -and $profileMatches -and $capturePassbandQualified)
        $captureQualified = ($baseCaptureQualified -and ((-not [bool]$RequireNr5CaptureReady) -or $nr5CaptureReady))

        $pollRecord = [ordered]@{
            poll = $poll
            generatedUtc = $pollUtc.ToString("o")
            vfoHz = $vfo
            radioLoHz = Get-NullableLongValue (Get-JsonValue $state "radioLoHz")
            mode = [string](Get-JsonValue $state "mode")
            stablePollCount = $stableCount
            sceneStatus = [string](Get-JsonValue $scene "status")
            sceneFresh = $sceneFresh
            signalProfile = $profile
            coherentMaxSnrDb = $coherentSnr
            maxSnrDb = Get-NullableDoubleValue (Get-JsonValue $scene "maxSnrDb")
            topPeakCount = $topPeaks.Count
            requireFrontendNearPassband = [bool]$RequireFrontendNearPassband
            frontendNearPassbandThresholdHz = $FrontendNearPassbandThresholdHz
            frontendOffsetMismatchToleranceHz = $FrontendOffsetMismatchToleranceHz
            frontendFilterPassbandKnown = $frontendFilterPassbandKnown
            filterLowHz = $filterLowHz
            filterHighHz = $filterHighHz
            frontendNearPassbandTopPeakCount = $frontendNearPassbandTopPeakCount
            frontendFilterPassbandTopPeakCount = $frontendFilterPassbandTopPeakCount
            frontendOffsetMismatchTopPeakCount = $frontendOffsetMismatchTopPeakCount
            frontendNearestTopPeakOffsetHz = $frontendNearestTopPeakOffsetHz
            frontendNearestTopPeakFrequencyHz = $frontendNearestTopPeakFrequencyHz
            frontendNearestTopPeakSnrDb = $frontendNearestTopPeakSnrDb
            frontendNearestTopPeakDbfs = $frontendNearestTopPeakDbfs
            frontendNearestTopPeakConfidence = $frontendNearestTopPeakConfidence
            frontendNearestFilterPassbandDistanceHz = $frontendNearestFilterPassbandDistanceHz
            frontendNearestFilterPassbandPeakOffsetHz = $frontendNearestFilterPassbandPeakOffsetHz
            frontendNearestFilterPassbandPeakFrequencyHz = $frontendNearestFilterPassbandPeakFrequencyHz
            frontendBestTopPeakOffsetHz = $frontendBestTopPeakOffsetHz
            frontendBestTopPeakFrequencyHz = $frontendBestTopPeakFrequencyHz
            frontendBestTopPeakSnrDb = $frontendBestTopPeakSnrDb
            frontendBestTopPeakDbfs = $frontendBestTopPeakDbfs
            frontendBestTopPeakConfidence = $frontendBestTopPeakConfidence
            frontendTuningHint = $frontendBestTuningHint
            frontendSuggestedDialShiftHz = Get-NullableDoubleValue (Get-JsonValue $frontendBestTuningHint "suggestedDialShiftHz")
            frontendSuggestedVfoHz = Get-NullableLongValue (Get-JsonValue $frontendBestTuningHint "suggestedVfoHz")
            frontendSuggestedVfoMhz = Get-NullableDoubleValue (Get-JsonValue $frontendBestTuningHint "suggestedVfoMhz")
            frontendSuggestedVfoStepHz = Get-NullableLongValue (Get-JsonValue $frontendBestTuningHint "suggestedVfoStepHz")
            frontendExactSuggestedDialShiftHz = Get-NullableDoubleValue (Get-JsonValue $frontendBestTuningHint "exactSuggestedDialShiftHz")
            frontendExactSuggestedVfoHz = Get-NullableLongValue (Get-JsonValue $frontendBestTuningHint "exactSuggestedVfoHz")
            frontendExactSuggestedVfoMhz = Get-NullableDoubleValue (Get-JsonValue $frontendBestTuningHint "exactSuggestedVfoMhz")
            frontendSuggestedPeakOffsetHz = Get-NullableDoubleValue (Get-JsonValue $frontendBestTuningHint "peakOffsetHz")
            frontendSuggestedPeakFrequencyHz = Get-NullableLongValue (Get-JsonValue $frontendBestTuningHint "peakFrequencyHz")
            frontendSuggestedFilterCenterOffsetHz = Get-NullableDoubleValue (Get-JsonValue $frontendBestTuningHint "filterCenterOffsetHz")
            frontendSuggestedFilterDistanceHz = Get-NullableDoubleValue (Get-JsonValue $frontendBestTuningHint "filterDistanceHz")
            frontendSuggestedTuneReason = [string](Get-JsonValue $frontendBestTuningHint "reason")
            frontendNearPassbandQualified = $frontendNearPassbandQualified
            frontendFilterPassbandQualified = $frontendFilterPassbandQualified
            frontendPassbandEvidenceQualified = $frontendPassbandEvidenceQualified
            liveStatus = [string](Get-JsonValue $live "status")
            requestedNrMode = $requestedNrMode
            effectiveNrMode = $effectiveNrMode
            readyForNr5Tuning = $readyForNr5Tuning
            requireNr5CaptureReady = [bool]$RequireNr5CaptureReady
            nr5CaptureReady = $nr5CaptureReady
            nr5CaptureReadinessStatus = $nr5CaptureReadinessStatus
            nr5CaptureReadinessConstraints = @($nr5CaptureReadinessConstraints.ToArray())
            audioStatus = [string](Get-JsonValue $runtime "audioStatus")
            audioRmsDbfs = Get-NullableDoubleValue (Get-JsonValue $runtime "audioRmsDbfs")
            audioPeakDbfs = Get-NullableDoubleValue (Get-JsonValue $runtime "audioPeakDbfs")
            rxAudioLevelerInputRmsDbfs = Get-NullableDoubleValue (Get-JsonValue $runtime "rxAudioLevelerInputRmsDbfs")
            rxAudioLevelerOutputRmsDbfs = Get-NullableDoubleValue (Get-JsonValue $runtime "rxAudioLevelerOutputRmsDbfs")
            rxAudioLevelerDesiredGainDb = Get-NullableDoubleValue (Get-JsonValue $runtime "rxAudioLevelerDesiredGainDb")
            rxAudioLevelerAppliedGainDb = Get-NullableDoubleValue (Get-JsonValue $runtime "rxAudioLevelerAppliedGainDb")
            rxAudioLevelerGainDeltaDb = Get-NullableDoubleValue (Get-JsonValue $runtime "rxAudioLevelerGainDeltaDb")
            rxAudioLevelerNr5SpeechHoldBlocks = Get-IntValue (Get-JsonValue $runtime "rxAudioLevelerNr5SpeechHoldBlocks")
            rxAudioLevelerBoostSlewLimited = Test-Truthy (Get-JsonValue $runtime "rxAudioLevelerBoostSlewLimited")
            rxAudioLevelerOutputLimited = Test-Truthy (Get-JsonValue $runtime "rxAudioLevelerOutputLimited")
            nr5SignalConfidence = Get-NullableDoubleValue (Get-JsonValue $nr5 "signalConfidence")
            nr5SignalProbability = Get-NullableDoubleValue (Get-JsonValue $nr5 "signalProbability")
            nr5AgcGate = Get-NullableDoubleValue (Get-JsonValue $nr5 "agcGate")
            nr5RecoveryDrive = Get-NullableDoubleValue (Get-JsonValue $nr5 "recoveryDrive")
            nr5WeakSignalMemory = Get-NullableDoubleValue (Get-JsonValue $nr5 "weakSignalMemory")
            nr5MaskSmoothing = Get-NullableDoubleValue (Get-JsonValue $nr5 "maskSmoothing")
            nr5InputDbfs = Get-NullableDoubleValue (Get-JsonValue $nr5 "inputDbfs")
            nr5OutputDbfs = Get-NullableDoubleValue (Get-JsonValue $nr5 "outputDbfs")
            nr5OutputPeakDbfs = Get-NullableDoubleValue (Get-JsonValue $nr5 "outputPeakDbfs")
            staleSceneCaptureAllowed = $staleSceneCaptureAllowed
            baseCaptureQualified = $baseCaptureQualified
            captureQualified = $captureQualified
        }
        $polls.Add([pscustomobject]$pollRecord) | Out-Null

        if (-not $JsonOnly) {
            $pollVfoText = Format-MhzText -Hz $vfo -Mhz $null
            $passbandText = if ($frontendPassbandEvidenceQualified) { "in-filter" } elseif ($frontendNearPassbandQualified) { "near" } else { "off" }
            $nr5Text = if ($nr5CaptureReady) { "ready" } elseif ($nr5CaptureReadinessConstraints.Count -gt 0) { ($nr5CaptureReadinessConstraints.ToArray() -join ",") } else { $nr5CaptureReadinessStatus }
            $captureText = if ($captureQualified) { "capture" } elseif ($baseCaptureQualified) { "wait-nr5" } else { "hold/scan" }
            Write-Host ("Poll {0}/{1}: vfo={2}, stable={3}, snr={4}, profile={5}, passband={6}, nr5={7}, action={8}" -f `
                    $poll, $PollCount, $pollVfoText, $stableCount, $coherentSnr, $profile, $passbandText, $nr5Text, $captureText)
        }

        if ($captureQualified -and $captures.Count -lt $MaxCaptures) {
            $vfoKey = [string]$vfo
            if (-not $capturedVfoMap.ContainsKey($vfoKey)) {
                $capturedVfoMap[$vfoKey] = [ordered]@{
                    count = 0
                    ready = $false
                    mixedReady = $false
                    lastStatus = ""
                }
            }

            $vfoRecord = $capturedVfoMap[$vfoKey]
            $vfoCaptureCount = Get-IntValue $vfoRecord["count"]
            $vfoMixedReady = Test-Truthy $vfoRecord["mixedReady"]
            if ($vfoCaptureCount -lt $MaxCapturesPerVfo -and -not $vfoMixedReady) {
                $vfoCaptureIndex = $vfoCaptureCount + 1
                $captureDir = if ($vfoCaptureIndex -le 1) {
                    Join-Path $captureRoot $vfoKey
                }
                else {
                    Join-Path $captureRoot ("{0}-capture-{1:D2}" -f $vfoKey, $vfoCaptureIndex)
                }
                New-Item -ItemType Directory -Force -Path $captureDir | Out-Null
                $summaryPath = Join-Path $captureDir "live-diagnostics-watch.json"
                $jsonlPath = Join-Path $captureDir "live-diagnostics-watch.jsonl"
                $captureLabel = ConvertTo-SafeName ("{0}-{1}-{2:D2}" -f $safeLabel, $vfoKey, $vfoCaptureIndex)
                $watch = Invoke-WatchCapture `
                    -ScriptPath $resolvedWatchScript `
                    -Base $base `
                    -Scenario $ScenarioId `
                    -Comparison $ComparisonId `
                    -CaptureLabel $captureLabel `
                    -SummaryPath $summaryPath `
                    -JsonlPath $jsonlPath `
                    -SampleCount $CaptureSamples `
                    -DelayMs $CaptureIntervalMs `
                    -RequestTimeoutSec $TimeoutSec `
                    -SkipCertificate:$SkipCertificateCheck

                $watchReport = $watch.report
                $weak = Get-JsonValue $watchReport "nr5WeakSignalWatch"
                $agc = Get-JsonValue $watchReport "agcStabilityWatch"
                $captureReadiness = Get-JsonValue $watchReport "captureReadinessWatch"
                $comparisonState = Get-JsonValue $watchReport "comparisonStateReadiness"
                $mixedFocus = Get-JsonValue $weak "mixedWeakStrongTuningFocus"
                $serializedSummaryPath = ConvertTo-BundleRelativeEvidencePath -Path $summaryPath -BundlePath $bundlePath
                $serializedJsonlPath = ConvertTo-BundleRelativeEvidencePath -Path $jsonlPath -BundlePath $bundlePath
                $captureReady = Test-Truthy (Get-JsonValue $watchReport "readyForBenchmarkTrace")
                $captureMixedReady = Test-Truthy (Get-JsonValue $weak "mixedWeakStrongEvidenceReady")
                $captureStatus = [string](Get-JsonValue $weak "mixedWeakStrongEvidenceStatus")
                $captures.Add([pscustomobject][ordered]@{
                    ok = Test-Truthy $watch.ok
                    exitCode = Get-IntValue (Get-JsonValue $watch "exitCode")
                    error = [string](Get-JsonValue $watch "error")
                    vfoHz = $vfo
                    vfoCaptureIndex = $vfoCaptureIndex
                    maxCapturesPerVfo = $MaxCapturesPerVfo
                    recaptureReason = if ($vfoCaptureIndex -le 1) { "first-vfo-capture" } else { "retry-until-mixed-ready" }
                    radioLoHz = Get-NullableLongValue (Get-JsonValue $state "radioLoHz")
                    mode = [string](Get-JsonValue $state "mode")
                    sceneFresh = $sceneFresh
                    staleSceneCapture = $staleSceneCaptureAllowed
                    signalProfile = $profile
                    coherentMaxSnrDb = $coherentSnr
                    topPeakCount = $topPeaks.Count
                    requireFrontendNearPassband = [bool]$RequireFrontendNearPassband
                    frontendNearPassbandThresholdHz = $FrontendNearPassbandThresholdHz
                    frontendOffsetMismatchToleranceHz = $FrontendOffsetMismatchToleranceHz
                    frontendFilterPassbandKnown = $frontendFilterPassbandKnown
                    filterLowHz = $filterLowHz
                    filterHighHz = $filterHighHz
                    frontendNearPassbandTopPeakCount = $frontendNearPassbandTopPeakCount
                    frontendFilterPassbandTopPeakCount = $frontendFilterPassbandTopPeakCount
                    frontendOffsetMismatchTopPeakCount = $frontendOffsetMismatchTopPeakCount
                    frontendNearestTopPeakOffsetHz = $frontendNearestTopPeakOffsetHz
                    frontendNearestTopPeakFrequencyHz = $frontendNearestTopPeakFrequencyHz
                    frontendNearestTopPeakSnrDb = $frontendNearestTopPeakSnrDb
                    frontendNearestTopPeakDbfs = $frontendNearestTopPeakDbfs
                    frontendNearestTopPeakConfidence = $frontendNearestTopPeakConfidence
                    frontendNearestFilterPassbandDistanceHz = $frontendNearestFilterPassbandDistanceHz
                    frontendNearestFilterPassbandPeakOffsetHz = $frontendNearestFilterPassbandPeakOffsetHz
                    frontendNearestFilterPassbandPeakFrequencyHz = $frontendNearestFilterPassbandPeakFrequencyHz
                    frontendBestTopPeakOffsetHz = $frontendBestTopPeakOffsetHz
                    frontendBestTopPeakFrequencyHz = $frontendBestTopPeakFrequencyHz
                    frontendBestTopPeakSnrDb = $frontendBestTopPeakSnrDb
                    frontendBestTopPeakDbfs = $frontendBestTopPeakDbfs
                    frontendBestTopPeakConfidence = $frontendBestTopPeakConfidence
                    frontendTuningHint = $frontendBestTuningHint
                    frontendSuggestedDialShiftHz = Get-NullableDoubleValue (Get-JsonValue $frontendBestTuningHint "suggestedDialShiftHz")
                    frontendSuggestedVfoHz = Get-NullableLongValue (Get-JsonValue $frontendBestTuningHint "suggestedVfoHz")
                    frontendSuggestedVfoMhz = Get-NullableDoubleValue (Get-JsonValue $frontendBestTuningHint "suggestedVfoMhz")
                    frontendSuggestedVfoStepHz = Get-NullableLongValue (Get-JsonValue $frontendBestTuningHint "suggestedVfoStepHz")
                    frontendExactSuggestedDialShiftHz = Get-NullableDoubleValue (Get-JsonValue $frontendBestTuningHint "exactSuggestedDialShiftHz")
                    frontendExactSuggestedVfoHz = Get-NullableLongValue (Get-JsonValue $frontendBestTuningHint "exactSuggestedVfoHz")
                    frontendExactSuggestedVfoMhz = Get-NullableDoubleValue (Get-JsonValue $frontendBestTuningHint "exactSuggestedVfoMhz")
                    frontendSuggestedPeakOffsetHz = Get-NullableDoubleValue (Get-JsonValue $frontendBestTuningHint "peakOffsetHz")
                    frontendSuggestedPeakFrequencyHz = Get-NullableLongValue (Get-JsonValue $frontendBestTuningHint "peakFrequencyHz")
                    frontendSuggestedFilterCenterOffsetHz = Get-NullableDoubleValue (Get-JsonValue $frontendBestTuningHint "filterCenterOffsetHz")
                    frontendSuggestedFilterDistanceHz = Get-NullableDoubleValue (Get-JsonValue $frontendBestTuningHint "filterDistanceHz")
                    frontendSuggestedTuneReason = [string](Get-JsonValue $frontendBestTuningHint "reason")
                    frontendNearPassbandQualified = $frontendNearPassbandQualified
                    frontendFilterPassbandQualified = $frontendFilterPassbandQualified
                    frontendPassbandEvidenceQualified = $frontendPassbandEvidenceQualified
                    trigger = [ordered]@{
                        poll = $poll
                        generatedUtc = $pollUtc.ToString("o")
                        liveStatus = [string](Get-JsonValue $live "status")
                        requestedNrMode = $requestedNrMode
                        effectiveNrMode = $effectiveNrMode
                        readyForNr5Tuning = $readyForNr5Tuning
                        requireNr5CaptureReady = [bool]$RequireNr5CaptureReady
                        nr5CaptureReady = $nr5CaptureReady
                        nr5CaptureReadinessStatus = $nr5CaptureReadinessStatus
                        nr5CaptureReadinessConstraints = @($nr5CaptureReadinessConstraints.ToArray())
                        signalProfile = $profile
                        coherentMaxSnrDb = $coherentSnr
                        frontendNearPassbandTopPeakCount = $frontendNearPassbandTopPeakCount
                        frontendFilterPassbandTopPeakCount = $frontendFilterPassbandTopPeakCount
                        frontendOffsetMismatchTopPeakCount = $frontendOffsetMismatchTopPeakCount
                        frontendPassbandEvidenceQualified = $frontendPassbandEvidenceQualified
                        frontendNearestTopPeakOffsetHz = $frontendNearestTopPeakOffsetHz
                        frontendNearestTopPeakFrequencyHz = $frontendNearestTopPeakFrequencyHz
                        frontendNearestTopPeakSnrDb = $frontendNearestTopPeakSnrDb
                        frontendNearestTopPeakDbfs = $frontendNearestTopPeakDbfs
                        frontendNearestTopPeakConfidence = $frontendNearestTopPeakConfidence
                        frontendNearestFilterPassbandDistanceHz = $frontendNearestFilterPassbandDistanceHz
                        frontendNearestFilterPassbandPeakOffsetHz = $frontendNearestFilterPassbandPeakOffsetHz
                        frontendNearestFilterPassbandPeakFrequencyHz = $frontendNearestFilterPassbandPeakFrequencyHz
                        frontendBestTopPeakOffsetHz = $frontendBestTopPeakOffsetHz
                        frontendBestTopPeakFrequencyHz = $frontendBestTopPeakFrequencyHz
                        frontendBestTopPeakSnrDb = $frontendBestTopPeakSnrDb
                        frontendBestTopPeakDbfs = $frontendBestTopPeakDbfs
                        frontendBestTopPeakConfidence = $frontendBestTopPeakConfidence
                        frontendTuningHint = $frontendBestTuningHint
                        frontendSuggestedDialShiftHz = Get-NullableDoubleValue (Get-JsonValue $frontendBestTuningHint "suggestedDialShiftHz")
                        frontendSuggestedVfoHz = Get-NullableLongValue (Get-JsonValue $frontendBestTuningHint "suggestedVfoHz")
                        frontendSuggestedVfoMhz = Get-NullableDoubleValue (Get-JsonValue $frontendBestTuningHint "suggestedVfoMhz")
                        frontendSuggestedVfoStepHz = Get-NullableLongValue (Get-JsonValue $frontendBestTuningHint "suggestedVfoStepHz")
                        frontendExactSuggestedDialShiftHz = Get-NullableDoubleValue (Get-JsonValue $frontendBestTuningHint "exactSuggestedDialShiftHz")
                        frontendExactSuggestedVfoHz = Get-NullableLongValue (Get-JsonValue $frontendBestTuningHint "exactSuggestedVfoHz")
                        frontendExactSuggestedVfoMhz = Get-NullableDoubleValue (Get-JsonValue $frontendBestTuningHint "exactSuggestedVfoMhz")
                        frontendSuggestedPeakOffsetHz = Get-NullableDoubleValue (Get-JsonValue $frontendBestTuningHint "peakOffsetHz")
                        frontendSuggestedPeakFrequencyHz = Get-NullableLongValue (Get-JsonValue $frontendBestTuningHint "peakFrequencyHz")
                        frontendSuggestedFilterCenterOffsetHz = Get-NullableDoubleValue (Get-JsonValue $frontendBestTuningHint "filterCenterOffsetHz")
                        frontendSuggestedFilterDistanceHz = Get-NullableDoubleValue (Get-JsonValue $frontendBestTuningHint "filterDistanceHz")
                        frontendSuggestedTuneReason = [string](Get-JsonValue $frontendBestTuningHint "reason")
                        audioStatus = [string](Get-JsonValue $runtime "audioStatus")
                        audioRmsDbfs = Get-NullableDoubleValue (Get-JsonValue $runtime "audioRmsDbfs")
                        audioPeakDbfs = Get-NullableDoubleValue (Get-JsonValue $runtime "audioPeakDbfs")
                        rxAudioLevelerInputRmsDbfs = Get-NullableDoubleValue (Get-JsonValue $runtime "rxAudioLevelerInputRmsDbfs")
                        rxAudioLevelerOutputRmsDbfs = Get-NullableDoubleValue (Get-JsonValue $runtime "rxAudioLevelerOutputRmsDbfs")
                        rxAudioLevelerDesiredGainDb = Get-NullableDoubleValue (Get-JsonValue $runtime "rxAudioLevelerDesiredGainDb")
                        rxAudioLevelerAppliedGainDb = Get-NullableDoubleValue (Get-JsonValue $runtime "rxAudioLevelerAppliedGainDb")
                        rxAudioLevelerGainDeltaDb = Get-NullableDoubleValue (Get-JsonValue $runtime "rxAudioLevelerGainDeltaDb")
                        rxAudioLevelerNr5SpeechHoldBlocks = Get-IntValue (Get-JsonValue $runtime "rxAudioLevelerNr5SpeechHoldBlocks")
                        rxAudioLevelerBoostSlewLimited = Test-Truthy (Get-JsonValue $runtime "rxAudioLevelerBoostSlewLimited")
                        rxAudioLevelerOutputLimited = Test-Truthy (Get-JsonValue $runtime "rxAudioLevelerOutputLimited")
                        nr5SignalConfidence = Get-NullableDoubleValue (Get-JsonValue $nr5 "signalConfidence")
                        nr5SignalProbability = Get-NullableDoubleValue (Get-JsonValue $nr5 "signalProbability")
                        nr5AgcGate = Get-NullableDoubleValue (Get-JsonValue $nr5 "agcGate")
                        nr5RecoveryDrive = Get-NullableDoubleValue (Get-JsonValue $nr5 "recoveryDrive")
                        nr5WeakSignalMemory = Get-NullableDoubleValue (Get-JsonValue $nr5 "weakSignalMemory")
                        nr5MaskSmoothing = Get-NullableDoubleValue (Get-JsonValue $nr5 "maskSmoothing")
                        nr5InputDbfs = Get-NullableDoubleValue (Get-JsonValue $nr5 "inputDbfs")
                        nr5OutputDbfs = Get-NullableDoubleValue (Get-JsonValue $nr5 "outputDbfs")
                        nr5OutputPeakDbfs = Get-NullableDoubleValue (Get-JsonValue $nr5 "outputPeakDbfs")
                    }
                    reportPath = $serializedSummaryPath
                    jsonlPath = $serializedJsonlPath
                    readyForBenchmarkTrace = $captureReady
                    trendStatus = [string](Get-JsonValue $watchReport "trendStatus")
                    nr5TuningTraceStatus = [string](Get-JsonValue $watchReport "nr5TuningTraceStatus")
                    nr5TuningReadySampleCount = Get-IntValue (Get-JsonValue $watchReport "nr5TuningReadySampleCount")
                    captureReadinessStatus = [string](Get-JsonValue $captureReadiness "status")
                    captureReadinessHardGatePass = Test-Truthy (Get-JsonValue $captureReadiness "hardGatePass")
                    captureReadinessStrictPreflightPass = Test-Truthy (Get-JsonValue $captureReadiness "strictPreflightPass")
                    captureReadinessTopConstraint = Get-JsonValue $captureReadiness "topConstraint"
                    captureReadinessTopHardConstraint = Get-JsonValue $captureReadiness "topHardConstraint"
                    captureReadinessWatch = $captureReadiness
                    comparisonStateReady = Test-Truthy (Get-JsonValue $comparisonState "ready")
                    comparisonStateStatus = [string](Get-JsonValue $comparisonState "status")
                    comparisonStateNextAction = [string](Get-JsonValue $comparisonState "nextAction")
                    comparisonStateReadiness = $comparisonState
                    weakInputSampleCount = Get-IntValue (Get-JsonValue $weak "weakInputSampleCount")
                    strongInputThresholdDbfs = Get-NullableDoubleValue (Get-JsonValue $weak "strongInputThresholdDbfs")
                    strongInputSampleCount = Get-IntValue (Get-JsonValue $weak "strongInputSampleCount")
                    nearStrongInputThresholdDbfs = Get-NullableDoubleValue (Get-JsonValue $weak "nearStrongInputThresholdDbfs")
                    nearStrongInputSampleCount = Get-IntValue (Get-JsonValue $weak "nearStrongInputSampleCount")
                    mixedWeakStrongEvidenceStatus = $captureStatus
                    mixedWeakStrongEvidenceReady = $captureMixedReady
                    weakStrongOutputGapDb = Get-NullableDoubleValue (Get-JsonValue $weak "weakStrongOutputGapDb")
                    speechQualifiedWeakInputSampleCount = Get-IntValue (Get-JsonValue $weak "speechQualifiedWeakInputSampleCount")
                    speechQualifiedStrongInputSampleCount = Get-IntValue (Get-JsonValue $weak "speechQualifiedStrongInputSampleCount")
                    speechQualifiedNearStrongInputSampleCount = Get-IntValue (Get-JsonValue $weak "speechQualifiedNearStrongInputSampleCount")
                    passbandQualifiedWeakInputSampleCount = Get-IntValue (Get-JsonValue $weak "passbandQualifiedWeakInputSampleCount")
                    passbandQualifiedStrongInputSampleCount = Get-IntValue (Get-JsonValue $weak "passbandQualifiedStrongInputSampleCount")
                    passbandQualifiedNearStrongInputSampleCount = Get-IntValue (Get-JsonValue $weak "passbandQualifiedNearStrongInputSampleCount")
                    topNearStrongInputs = @(Get-JsonArray $weak "topNearStrongInputs")
                    mixedWeakStrongTuningFocus = $mixedFocus
                    mixedWeakStrongTuningStatus = [string](Get-JsonValue $mixedFocus "status")
                    mixedWeakStrongTuningAction = [string](Get-JsonValue $mixedFocus "preferredAction")
                    mixedWeakStrongOutputGapDirection = [string](Get-JsonValue $mixedFocus "outputGapDirection")
                    mixedWeakStrongOutputGapExcessDb = Get-NullableDoubleValue (Get-JsonValue $mixedFocus "outputGapExcessDb")
                    mixedWeakStrongFinalAudioGapDirection = [string](Get-JsonValue $mixedFocus "finalAudioGapDirection")
                    mixedWeakStrongFinalAudioGapExcessDb = Get-NullableDoubleValue (Get-JsonValue $mixedFocus "finalAudioGapExcessDb")
                    agcStabilityStatus = [string](Get-JsonValue $agc "status")
                    agcPumpingRisk = (Test-Truthy (Get-JsonValue $agc "pumpingRisk")) -or (Test-Truthy (Get-JsonValue $agc "agcPumpingRisk"))
                }) | Out-Null

                $vfoRecord["count"] = $vfoCaptureIndex
                $vfoRecord["ready"] = (Test-Truthy $vfoRecord["ready"]) -or $captureReady
                $vfoRecord["mixedReady"] = (Test-Truthy $vfoRecord["mixedReady"]) -or $captureMixedReady
                $vfoRecord["lastStatus"] = $captureStatus
            }
        }

        if ($MaxCaptures -gt 0 -and $captures.Count -ge $MaxCaptures) {
            break
        }

        if ($poll -lt $PollCount -and $PollIntervalSec -gt 0) {
            Start-Sleep -Seconds $PollIntervalSec
        }
    }
}
catch {
    $scanError = $_.Exception.Message
    if (-not $ContinueOnError) {
        throw
    }
}

$completedUtc = [DateTimeOffset]::UtcNow
$captureArray = @($captures.ToArray())
$pollArray = @($polls.ToArray())
$weakTotal = 0
$strongTotal = 0
$nearStrongTotal = 0
$speechWeakTotal = 0
$speechStrongTotal = 0
$passbandWeakTotal = 0
$passbandStrongTotal = 0
$readyCaptureCount = 0
$mixedReadyCount = 0
$pumpingRiskCount = 0
$staleSceneCaptureCount = 0
$baseCaptureQualifiedPollCount = 0
$nr5CaptureReadyPollCount = 0
$nr5CaptureBlockedPollCount = 0
$nr5CaptureAdvisoryPollCount = 0
$mixedWeakStrongStatusCounts = @{}
$missingStrongInputCaptureCount = 0
$missingWeakInputCaptureCount = 0
$missingWeakAndStrongInputCaptureCount = 0
$missingOutputGapCaptureCount = 0
$weakStrongOutputGapWatchCaptureCount = 0
$weakOnlyCaptureCount = 0
$readyWeakOnlyCaptureCount = 0
$strongOnlyCaptureCount = 0
$bestWeakOnlyCapture = $null
$bestWeakOnlyCaptureScore = [double]::NegativeInfinity
$nearStrongPromotionCandidateCaptureCount = 0
$bestNearStrongPromotionCandidateCapture = $null
$bestNearStrongPromotionCandidateScore = [double]::NegativeInfinity
$bestNearStrongPromotionCandidateDistanceToStrongThresholdDb = $null
$vfoCaptureCounts = @{}
foreach ($capture in $captureArray) {
    $captureVfoKey = [string]$capture.vfoHz
    if (-not [string]::IsNullOrWhiteSpace($captureVfoKey)) {
        if (-not $vfoCaptureCounts.ContainsKey($captureVfoKey)) {
            $vfoCaptureCounts[$captureVfoKey] = 0
        }
        $vfoCaptureCounts[$captureVfoKey] = [int]$vfoCaptureCounts[$captureVfoKey] + 1
    }
    $weakTotal += Get-IntValue $capture.weakInputSampleCount
    $strongTotal += Get-IntValue $capture.strongInputSampleCount
    $nearStrongTotal += Get-IntValue $capture.nearStrongInputSampleCount
    $speechWeakTotal += Get-IntValue $capture.speechQualifiedWeakInputSampleCount
    $speechStrongTotal += Get-IntValue $capture.speechQualifiedStrongInputSampleCount
    $passbandWeakTotal += Get-IntValue $capture.passbandQualifiedWeakInputSampleCount
    $passbandStrongTotal += Get-IntValue $capture.passbandQualifiedStrongInputSampleCount
    if (Test-Truthy $capture.readyForBenchmarkTrace) {
        $readyCaptureCount++
    }
    if (Test-Truthy $capture.mixedWeakStrongEvidenceReady) {
        $mixedReadyCount++
    }
    if (Test-Truthy $capture.agcPumpingRisk) {
        $pumpingRiskCount++
    }
    if (Test-Truthy $capture.staleSceneCapture) {
        $staleSceneCaptureCount++
    }

    $captureStatus = [string]$capture.mixedWeakStrongEvidenceStatus
    if ([string]::IsNullOrWhiteSpace($captureStatus)) {
        $captureStatus = "unknown"
    }
    if (-not $mixedWeakStrongStatusCounts.ContainsKey($captureStatus)) {
        $mixedWeakStrongStatusCounts[$captureStatus] = 0
    }
    $mixedWeakStrongStatusCounts[$captureStatus] = [int]$mixedWeakStrongStatusCounts[$captureStatus] + 1
    switch ($captureStatus) {
        "missing-strong-input" { $missingStrongInputCaptureCount++ }
        "missing-weak-input" { $missingWeakInputCaptureCount++ }
        "missing-weak-and-strong-input" { $missingWeakAndStrongInputCaptureCount++ }
        "missing-output-gap" { $missingOutputGapCaptureCount++ }
        "weak-strong-output-gap-watch" { $weakStrongOutputGapWatchCaptureCount++ }
    }

    $captureWeakInput = Get-IntValue $capture.weakInputSampleCount
    $captureStrongInput = Get-IntValue $capture.strongInputSampleCount
    $captureNearStrongInput = Get-IntValue $capture.nearStrongInputSampleCount
    if ($captureWeakInput -gt 0 -and $captureStrongInput -le 0) {
        $weakOnlyCaptureCount++
        if (Test-Truthy $capture.readyForBenchmarkTrace) {
            $readyWeakOnlyCaptureCount++
        }

        $captureReadyBoost = if (Test-Truthy $capture.readyForBenchmarkTrace) { 100000.0 } else { 0.0 }
        $captureWeakOnlyScore =
            $captureReadyBoost +
            ([double]$captureWeakInput * 100.0) +
            ([double](Get-IntValue $capture.passbandQualifiedWeakInputSampleCount) * 50.0) +
            ([double](Get-IntValue $capture.speechQualifiedWeakInputSampleCount) * 25.0) +
            ([double](Get-IntValue $capture.nearStrongInputSampleCount) * 10.0)
        $captureSnr = Get-NullableDoubleValue $capture.coherentMaxSnrDb
        if ($null -ne $captureSnr) {
            $captureWeakOnlyScore += [Math]::Max(0.0, [double]$captureSnr)
        }
        if ($null -eq $bestWeakOnlyCapture -or [double]$captureWeakOnlyScore -gt [double]$bestWeakOnlyCaptureScore) {
            $bestWeakOnlyCapture = $capture
            $bestWeakOnlyCaptureScore = [Math]::Round([double]$captureWeakOnlyScore, 3)
        }
    }
    elseif ($captureStrongInput -gt 0 -and $captureWeakInput -le 0) {
        $strongOnlyCaptureCount++
    }

    if ([string]::Equals($captureStatus, "missing-strong-input", [StringComparison]::OrdinalIgnoreCase) -and
        $captureWeakInput -gt 0 -and
        $captureNearStrongInput -gt 0) {
        $nearStrongPromotionCandidateCaptureCount++
        $candidateDistance = $null
        foreach ($nearStrongInput in @(Get-JsonArray $capture "topNearStrongInputs")) {
            $distance = Get-NullableDoubleValue (Get-JsonValue $nearStrongInput "distanceToStrongThresholdDb")
            if ($null -ne $distance -and ($null -eq $candidateDistance -or [double]$distance -lt [double]$candidateDistance)) {
                $candidateDistance = $distance
            }
        }

        $distanceScore = if ($null -ne $candidateDistance) { 100.0 - [Math]::Min(100.0, [Math]::Max(0.0, [double]$candidateDistance * 10.0)) } else { 0.0 }
        $captureTriggerPoll = Get-IntValue (Get-JsonValue (Get-JsonValue $capture "trigger") "poll")
        $candidateScore =
            ([double](Get-IntValue $capture.passbandQualifiedNearStrongInputSampleCount) * 10000.0) +
            ([double](Get-IntValue $capture.speechQualifiedNearStrongInputSampleCount) * 5000.0) +
            ([double]$captureNearStrongInput * 1000.0) +
            ([double]$captureWeakInput * 100.0) +
            $distanceScore +
            ([double](Get-IntValue $capture.vfoCaptureIndex) * 10.0) +
            ([double]$captureTriggerPoll / 1000.0)

        if ($null -eq $bestNearStrongPromotionCandidateCapture -or
            [double]$candidateScore -gt [double]$bestNearStrongPromotionCandidateScore) {
            $bestNearStrongPromotionCandidateCapture = $capture
            $bestNearStrongPromotionCandidateScore = [Math]::Round([double]$candidateScore, 3)
            $bestNearStrongPromotionCandidateDistanceToStrongThresholdDb = $candidateDistance
        }
    }
}
$mixedWeakStrongStatusCountRecords = @(
    $mixedWeakStrongStatusCounts.GetEnumerator() |
        Sort-Object -Property @{ Expression = { $_.Value }; Descending = $true }, @{ Expression = { $_.Key }; Ascending = $true } |
        ForEach-Object {
            [pscustomobject][ordered]@{
                status = [string]$_.Key
                count = [int]$_.Value
            }
        }
)
$staleScenePollCount = 0
$frontendNearPassbandPollCount = 0
$frontendOffPassbandPollCount = 0
$frontendFilterPassbandPollCount = 0
$frontendFilterOffPassbandPollCount = 0
$frontendOffsetMismatchPollCount = 0
$frontendTuningHintPollCount = 0
$frontendBestTuningHint = $null
$captureQualifiedPollCount = 0
$triggerAudioActivePollCount = 0
$triggerMaxAudioRmsDbfs = $null
$triggerMaxNr5OutputDbfs = $null
$bestTriggerPoll = $null
$observedVfoMap = @{}
$observedVfoOrder = New-Object System.Collections.Generic.List[string]
foreach ($pollRecord in $pollArray) {
    if (-not (Test-Truthy $pollRecord.sceneFresh)) {
        $staleScenePollCount++
    }
    if ((Get-IntValue $pollRecord.frontendNearPassbandTopPeakCount) -gt 0) {
        $frontendNearPassbandPollCount++
    }
    elseif ((Get-IntValue $pollRecord.topPeakCount) -gt 0) {
        $frontendOffPassbandPollCount++
    }
    if ((Get-IntValue $pollRecord.frontendFilterPassbandTopPeakCount) -gt 0) {
        $frontendFilterPassbandPollCount++
    }
    elseif ((Get-IntValue $pollRecord.topPeakCount) -gt 0 -and (Test-Truthy $pollRecord.frontendFilterPassbandKnown)) {
        $frontendFilterOffPassbandPollCount++
    }
    if ((Get-IntValue $pollRecord.frontendOffsetMismatchTopPeakCount) -gt 0) {
        $frontendOffsetMismatchPollCount++
    }
    if (Test-Truthy $pollRecord.captureQualified) {
        $captureQualifiedPollCount++
    }
    if (Test-Truthy $pollRecord.baseCaptureQualified) {
        $baseCaptureQualifiedPollCount++
    }
    if (Test-Truthy $pollRecord.nr5CaptureReady) {
        $nr5CaptureReadyPollCount++
    }
    elseif ([string]::Equals([string]$pollRecord.nr5CaptureReadinessStatus, "blocked", [StringComparison]::OrdinalIgnoreCase)) {
        $nr5CaptureBlockedPollCount++
    }
    elseif ([string]::Equals([string]$pollRecord.nr5CaptureReadinessStatus, "advisory", [StringComparison]::OrdinalIgnoreCase)) {
        $nr5CaptureAdvisoryPollCount++
    }

    $pollTuningHint = Get-JsonValue $pollRecord "frontendTuningHint"
    if ($null -ne $pollTuningHint) {
        $frontendTuningHintPollCount++
        $hintDistance = Get-NullableDoubleValue (Get-JsonValue $pollTuningHint "filterDistanceHz")
        $bestHintDistance = Get-NullableDoubleValue (Get-JsonValue $frontendBestTuningHint "filterDistanceHz")
        $hintShift = Get-NullableDoubleValue (Get-JsonValue $pollTuningHint "suggestedDialShiftHz")
        $bestHintShift = Get-NullableDoubleValue (Get-JsonValue $frontendBestTuningHint "suggestedDialShiftHz")
        $hintSnr = Get-NullableDoubleValue (Get-JsonValue $pollTuningHint "peakSnrDb")
        $bestHintSnr = Get-NullableDoubleValue (Get-JsonValue $frontendBestTuningHint "peakSnrDb")
        if ($null -eq $frontendBestTuningHint -or
            ($null -ne $hintDistance -and ($null -eq $bestHintDistance -or [double]$hintDistance -lt [double]$bestHintDistance)) -or
            ($null -ne $hintDistance -and $null -ne $bestHintDistance -and [double]$hintDistance -eq [double]$bestHintDistance -and $null -ne $hintShift -and $null -ne $bestHintShift -and [Math]::Abs([double]$hintShift) -lt [Math]::Abs([double]$bestHintShift)) -or
            ($null -ne $hintDistance -and $null -ne $bestHintDistance -and [double]$hintDistance -eq [double]$bestHintDistance -and $null -ne $hintSnr -and ($null -eq $bestHintSnr -or [double]$hintSnr -gt [double]$bestHintSnr))) {
            $frontendBestTuningHint = $pollTuningHint
        }
    }

    $observedVfoHz = Get-NullableLongValue $pollRecord.vfoHz
    if ($null -ne $observedVfoHz) {
        $observedVfoKey = [string]$observedVfoHz
        if (-not $observedVfoMap.ContainsKey($observedVfoKey)) {
            $observedVfoMap[$observedVfoKey] = [ordered]@{
                vfoHz = $observedVfoHz
                vfoMhz = [Math]::Round([double]$observedVfoHz / 1000000.0, 6)
                firstPoll = Get-IntValue $pollRecord.poll
                lastPoll = Get-IntValue $pollRecord.poll
                pollCount = 0
                maxStablePollCount = 0
                sceneFreshPollCount = 0
                staleScenePollCount = 0
                topPeakPollCount = 0
                frontendNearPassbandPollCount = 0
                frontendFilterPassbandPollCount = 0
                frontendFilterOffPassbandPollCount = 0
                frontendOffsetMismatchPollCount = 0
                frontendTuningHintPollCount = 0
                captureQualifiedPollCount = 0
                maxCoherentSnrDb = $null
                maxAudioRmsDbfs = $null
                maxNr5OutputDbfs = $null
                bestTuningHint = $null
                frontendSuggestedDialShiftHz = $null
                frontendSuggestedVfoHz = $null
                frontendSuggestedVfoMhz = $null
                frontendSuggestedVfoStepHz = $null
                frontendExactSuggestedDialShiftHz = $null
                frontendExactSuggestedVfoHz = $null
                frontendExactSuggestedVfoMhz = $null
                frontendSuggestedTuneReason = ""
                frontendSuggestedFilterDistanceHz = $null
                status = "observed"
                score = 0.0
            }
            $observedVfoOrder.Add($observedVfoKey) | Out-Null
        }

        $observed = $observedVfoMap[$observedVfoKey]
        $observed["lastPoll"] = Get-IntValue $pollRecord.poll
        $observed["pollCount"] = (Get-IntValue $observed["pollCount"]) + 1
        $observed["maxStablePollCount"] = [Math]::Max((Get-IntValue $observed["maxStablePollCount"]), (Get-IntValue $pollRecord.stablePollCount))
        if (Test-Truthy $pollRecord.sceneFresh) {
            $observed["sceneFreshPollCount"] = (Get-IntValue $observed["sceneFreshPollCount"]) + 1
        }
        else {
            $observed["staleScenePollCount"] = (Get-IntValue $observed["staleScenePollCount"]) + 1
        }
        if ((Get-IntValue $pollRecord.topPeakCount) -gt 0) {
            $observed["topPeakPollCount"] = (Get-IntValue $observed["topPeakPollCount"]) + 1
        }
        if ((Get-IntValue $pollRecord.frontendNearPassbandTopPeakCount) -gt 0) {
            $observed["frontendNearPassbandPollCount"] = (Get-IntValue $observed["frontendNearPassbandPollCount"]) + 1
        }
        if ((Get-IntValue $pollRecord.frontendFilterPassbandTopPeakCount) -gt 0) {
            $observed["frontendFilterPassbandPollCount"] = (Get-IntValue $observed["frontendFilterPassbandPollCount"]) + 1
        }
        elseif ((Get-IntValue $pollRecord.topPeakCount) -gt 0 -and (Test-Truthy $pollRecord.frontendFilterPassbandKnown)) {
            $observed["frontendFilterOffPassbandPollCount"] = (Get-IntValue $observed["frontendFilterOffPassbandPollCount"]) + 1
        }
        if ((Get-IntValue $pollRecord.frontendOffsetMismatchTopPeakCount) -gt 0) {
            $observed["frontendOffsetMismatchPollCount"] = (Get-IntValue $observed["frontendOffsetMismatchPollCount"]) + 1
        }
        if (Test-Truthy $pollRecord.captureQualified) {
            $observed["captureQualifiedPollCount"] = (Get-IntValue $observed["captureQualifiedPollCount"]) + 1
        }

        $observedCoherentSnr = Get-NullableDoubleValue $pollRecord.coherentMaxSnrDb
        if ($null -ne $observedCoherentSnr -and ($null -eq $observed["maxCoherentSnrDb"] -or [double]$observedCoherentSnr -gt [double]$observed["maxCoherentSnrDb"])) {
            $observed["maxCoherentSnrDb"] = $observedCoherentSnr
        }
        $observedAudio = Get-NullableDoubleValue $pollRecord.audioRmsDbfs
        if ($null -ne $observedAudio -and ($null -eq $observed["maxAudioRmsDbfs"] -or [double]$observedAudio -gt [double]$observed["maxAudioRmsDbfs"])) {
            $observed["maxAudioRmsDbfs"] = $observedAudio
        }
        $observedNr5Output = Get-NullableDoubleValue $pollRecord.nr5OutputDbfs
        if ($null -ne $observedNr5Output -and ($null -eq $observed["maxNr5OutputDbfs"] -or [double]$observedNr5Output -gt [double]$observed["maxNr5OutputDbfs"])) {
            $observed["maxNr5OutputDbfs"] = $observedNr5Output
        }

        if ($null -ne $pollTuningHint) {
            $observed["frontendTuningHintPollCount"] = (Get-IntValue $observed["frontendTuningHintPollCount"]) + 1
            $hintDistance = Get-NullableDoubleValue (Get-JsonValue $pollTuningHint "filterDistanceHz")
            $bestHintDistance = Get-NullableDoubleValue (Get-JsonValue $observed["bestTuningHint"] "filterDistanceHz")
            $hintShift = Get-NullableDoubleValue (Get-JsonValue $pollTuningHint "suggestedDialShiftHz")
            $bestHintShift = Get-NullableDoubleValue (Get-JsonValue $observed["bestTuningHint"] "suggestedDialShiftHz")
            $hintSnr = Get-NullableDoubleValue (Get-JsonValue $pollTuningHint "peakSnrDb")
            $bestHintSnr = Get-NullableDoubleValue (Get-JsonValue $observed["bestTuningHint"] "peakSnrDb")
            if ($null -eq $observed["bestTuningHint"] -or
                ($null -ne $hintDistance -and ($null -eq $bestHintDistance -or [double]$hintDistance -lt [double]$bestHintDistance)) -or
                ($null -ne $hintDistance -and $null -ne $bestHintDistance -and [double]$hintDistance -eq [double]$bestHintDistance -and $null -ne $hintShift -and $null -ne $bestHintShift -and [Math]::Abs([double]$hintShift) -lt [Math]::Abs([double]$bestHintShift)) -or
                ($null -ne $hintDistance -and $null -ne $bestHintDistance -and [double]$hintDistance -eq [double]$bestHintDistance -and $null -ne $hintSnr -and ($null -eq $bestHintSnr -or [double]$hintSnr -gt [double]$bestHintSnr))) {
                $observed["bestTuningHint"] = $pollTuningHint
                $observed["frontendSuggestedDialShiftHz"] = Get-NullableDoubleValue (Get-JsonValue $pollTuningHint "suggestedDialShiftHz")
                $observed["frontendSuggestedVfoHz"] = Get-NullableLongValue (Get-JsonValue $pollTuningHint "suggestedVfoHz")
                $observed["frontendSuggestedVfoMhz"] = Get-NullableDoubleValue (Get-JsonValue $pollTuningHint "suggestedVfoMhz")
                $observed["frontendSuggestedVfoStepHz"] = Get-NullableLongValue (Get-JsonValue $pollTuningHint "suggestedVfoStepHz")
                $observed["frontendExactSuggestedDialShiftHz"] = Get-NullableDoubleValue (Get-JsonValue $pollTuningHint "exactSuggestedDialShiftHz")
                $observed["frontendExactSuggestedVfoHz"] = Get-NullableLongValue (Get-JsonValue $pollTuningHint "exactSuggestedVfoHz")
                $observed["frontendExactSuggestedVfoMhz"] = Get-NullableDoubleValue (Get-JsonValue $pollTuningHint "exactSuggestedVfoMhz")
                $observed["frontendSuggestedTuneReason"] = [string](Get-JsonValue $pollTuningHint "reason")
                $observed["frontendSuggestedFilterDistanceHz"] = Get-NullableDoubleValue (Get-JsonValue $pollTuningHint "filterDistanceHz")
            }
        }
    }

    $pollAudio = Get-NullableDoubleValue $pollRecord.audioRmsDbfs
    if ($null -ne $pollAudio) {
        if ([double]$pollAudio -ge -60.0) {
            $triggerAudioActivePollCount++
        }
        if ($null -eq $triggerMaxAudioRmsDbfs -or [double]$pollAudio -gt [double]$triggerMaxAudioRmsDbfs) {
            $triggerMaxAudioRmsDbfs = $pollAudio
            $bestTriggerPoll = $pollRecord
        }
    }

    $pollNr5Output = Get-NullableDoubleValue $pollRecord.nr5OutputDbfs
    if ($null -ne $pollNr5Output -and ($null -eq $triggerMaxNr5OutputDbfs -or [double]$pollNr5Output -gt [double]$triggerMaxNr5OutputDbfs)) {
        $triggerMaxNr5OutputDbfs = $pollNr5Output
    }
}
$uniqueCapturedVfoCount = $vfoCaptureCounts.Count
$recapturedVfoCount = 0
foreach ($entry in $vfoCaptureCounts.GetEnumerator()) {
    if ([int]$entry.Value -gt 1) {
        $recapturedVfoCount++
    }
}

$observedVfoRecords = New-Object System.Collections.Generic.List[object]
$bestObservedVfo = $null
foreach ($observedVfoKey in $observedVfoOrder) {
    $observed = $observedVfoMap[$observedVfoKey]
    $pollCountForScore = Get-IntValue $observed["pollCount"]
    $stableForScore = Get-IntValue $observed["maxStablePollCount"]
    $topPeakForScore = Get-IntValue $observed["topPeakPollCount"]
    $nearForScore = Get-IntValue $observed["frontendNearPassbandPollCount"]
    $filterForScore = Get-IntValue $observed["frontendFilterPassbandPollCount"]
    $hintForScore = Get-IntValue $observed["frontendTuningHintPollCount"]
    $captureForScore = Get-IntValue $observed["captureQualifiedPollCount"]
    $snrForScore = Get-NullableDoubleValue $observed["maxCoherentSnrDb"]
    $audioForScore = Get-NullableDoubleValue $observed["maxAudioRmsDbfs"]
    $hintDistanceForScore = Get-NullableDoubleValue $observed["frontendSuggestedFilterDistanceHz"]

    $score = [double]$pollCountForScore + ([double]$stableForScore * 2.0) + ([double]$topPeakForScore * 5.0) + ([double]$nearForScore * 20.0) + ([double]$filterForScore * 80.0) + ([double]$hintForScore * 10.0) + ([double]$captureForScore * 200.0)
    if ($null -ne $snrForScore) {
        $score += [Math]::Min(60.0, [Math]::Max(0.0, [double]$snrForScore))
    }
    if ($null -ne $audioForScore) {
        $score += [Math]::Max(0.0, ([double]$audioForScore + 90.0) / 4.0)
    }
    if ($null -ne $hintDistanceForScore) {
        $score += [Math]::Max(0.0, (3000.0 - [Math]::Min(3000.0, [double]$hintDistanceForScore)) / 30.0)
    }

    $status = if ($captureForScore -gt 0) {
        "capture-qualified"
    }
    elseif ($filterForScore -gt 0) {
        "filter-passband"
    }
    elseif ($nearForScore -gt 0) {
        "near-dial"
    }
    elseif ($hintForScore -gt 0) {
        "tuning-hint"
    }
    elseif ($topPeakForScore -gt 0) {
        "active-off-filter"
    }
    else {
        "observed"
    }

    $observed["status"] = $status
    $observed["score"] = [Math]::Round($score, 3)
    $observedRecord = [pscustomobject]$observed
    $observedVfoRecords.Add($observedRecord) | Out-Null

    $observedScore = Get-NullableDoubleValue $observed["score"]
    $bestScore = Get-NullableDoubleValue (Get-JsonValue $bestObservedVfo "score")
    if ($null -eq $bestObservedVfo -or
        [double]$observedScore -gt [double]$bestScore -or
        ([double]$observedScore -eq [double]$bestScore -and (Get-IntValue $observed["lastPoll"]) -gt (Get-IntValue (Get-JsonValue $bestObservedVfo "lastPoll")))) {
        $bestObservedVfo = $observedRecord
    }
}
$observedVfoArray = @($observedVfoRecords.ToArray())

$recommendations = New-Object System.Collections.Generic.List[string]
if ($captureArray.Count -le 0) {
    if ($MaxCaptures -le 0 -and $captureQualifiedPollCount -gt 0) {
        $recommendations.Add("Capture is disabled by -MaxCaptures 0; observed $captureQualifiedPollCount capture-qualified poll(s) for scouting without launching child diagnostics.") | Out-Null
    }
    else {
        $recommendations.Add("No stable voice-like manual-tune VFO met the capture threshold; keep tuning manually or lower MinCoherentSnrDb for scouting only.") | Out-Null
    }
    if ($RequireFrontendNearPassband -and $frontendNearPassbandPollCount -le 0 -and $frontendOffPassbandPollCount -gt 0) {
        $recommendations.Add("Frontend peaks were present but none were within $FrontendNearPassbandThresholdHz Hz of the dial/passband; keep manually tuning toward the visible peak before capturing acceptance evidence.") | Out-Null
    }
    if ($RequireFrontendNearPassband -and $frontendFilterPassbandPollCount -le 0 -and $frontendFilterOffPassbandPollCount -gt 0) {
        $recommendations.Add("Frontend peaks were present but none were inside the signed RX filter passband; tune the signal into the active filter window before capturing acceptance evidence.") | Out-Null
    }
    if ($RequireFrontendNearPassband -and $frontendFilterPassbandPollCount -le 0 -and $frontendFilterOffPassbandPollCount -gt 0 -and $null -ne $frontendBestTuningHint) {
        $hintShift = Get-NullableDoubleValue (Get-JsonValue $frontendBestTuningHint "suggestedDialShiftHz")
        $hintVfoMhz = Get-NullableDoubleValue (Get-JsonValue $frontendBestTuningHint "suggestedVfoMhz")
        $hintCenter = Get-NullableDoubleValue (Get-JsonValue $frontendBestTuningHint "filterCenterOffsetHz")
        if ($null -ne $hintShift -and $null -ne $hintVfoMhz -and $null -ne $hintCenter) {
            $recommendations.Add(("Read-only manual tuning hint: shift VFO by {0:+0;-0;0} Hz to about {1:N6} MHz to place the nearest consistent frontend peak at the active filter center ({2:N0} Hz)." -f [double]$hintShift, [double]$hintVfoMhz, [double]$hintCenter)) | Out-Null
        }
    }
    if ($RequireFrontendNearPassband -and $frontendOffsetMismatchPollCount -gt 0) {
        $recommendations.Add("Some frontend peak offsets disagreed with frequencyHz minus VFO beyond $FrontendOffsetMismatchToleranceHz Hz; strict passband capture ignored those inconsistent peaks.") | Out-Null
    }
    if ($RequireNr5CaptureReady -and $baseCaptureQualifiedPollCount -gt 0 -and $captureQualifiedPollCount -le 0) {
        $recommendations.Add("Stable voice-like passband polls were present but NR5 capture readiness was blocked; wait for requested/effective NR5 alignment and complete NR5 diagnostics before spending child capture slots.") | Out-Null
    }
    if ($staleScenePollCount -gt 0 -and -not $AllowStaleSceneCapture) {
        $recommendations.Add("Frontend DSP scene evidence was stale during $staleScenePollCount poll(s); open the frontend scene publisher or rerun with -AllowStaleSceneCapture for scouting-only child diagnostics.") | Out-Null
    }
}
elseif ($mixedReadyCount -gt 0) {
    $recommendations.Add("At least one manual-tune capture has mixed weak+strong evidence; promote that window through live history and strict validation before tuning DSP behavior.") | Out-Null
}
elseif ($weakTotal -gt 0 -and $strongTotal -le 0) {
    if ($null -ne $bestNearStrongPromotionCandidateCapture) {
        $candidateVfoHz = Get-NullableLongValue (Get-JsonValue $bestNearStrongPromotionCandidateCapture "vfoHz")
        $candidateVfoText = if ($null -ne $candidateVfoHz) {
            "{0:N6} MHz" -f ([double]$candidateVfoHz / 1000000.0)
        }
        else {
            "the best near-strong capture"
        }
        $distanceText = if ($null -ne $bestNearStrongPromotionCandidateDistanceToStrongThresholdDb) {
            "{0:N1} dB below the strict strong threshold" -f [double]$bestNearStrongPromotionCandidateDistanceToStrongThresholdDb
        }
        else {
            "near the strict strong threshold"
        }
        $recommendations.Add("Best near-strong promotion candidate is at $candidateVfoText with top near-strong input $distanceText; extend dwell or recapture this VFO before rejecting the window as weak-only.") | Out-Null
    }
    if ($null -ne $bestWeakOnlyCapture) {
        $bestWeakVfoHz = Get-NullableLongValue $bestWeakOnlyCapture.vfoHz
        $bestWeakVfoText = if ($null -ne $bestWeakVfoHz) {
            "{0:N6} MHz" -f ([double]$bestWeakVfoHz / 1000000.0)
        }
        else {
            "the best weak-only VFO"
        }
        $bestWeakCount = Get-IntValue $bestWeakOnlyCapture.weakInputSampleCount
        $bestWeakNearStrong = Get-IntValue $bestWeakOnlyCapture.nearStrongInputSampleCount
        $recommendations.Add("Best weak-only capture is at $bestWeakVfoText with $bestWeakCount weak-input sample(s) and $bestWeakNearStrong near-strong sample(s); keep collecting or recapture that VFO until strict strongInputSampleCount becomes non-zero.") | Out-Null
    }
    if ($MaxCapturesPerVfo -le 1) {
        $recommendations.Add("Manual-tune captures are weak-only by the strict NR5 input threshold; rerun with -MaxCapturesPerVfo 2 or keep collecting active windows until strongInputSampleCount is non-zero.") | Out-Null
    }
    else {
        $recommendations.Add("Manual-tune captures are weak-only by the strict NR5 input threshold after bounded same-VFO recapture; keep collecting active windows until strongInputSampleCount is non-zero.") | Out-Null
    }
}
if ($recapturedVfoCount -gt 0) {
    $recommendations.Add("Same-VFO recapture was used for $recapturedVfoCount VFO(s); prefer the latest ready or mixed-ready capture for promotion.") | Out-Null
}
if (-not $RequireFrontendNearPassband -and $captureArray.Count -gt 0 -and $passbandWeakTotal -le 0 -and $passbandStrongTotal -le 0) {
    $recommendations.Add("Captured windows had no passband-qualified NR5 samples; rerun with -RequireFrontendNearPassband for acceptance-oriented manual-tune capture.") | Out-Null
}
if ($staleSceneCaptureCount -gt 0) {
    $recommendations.Add("Stale-scene fallback captured $staleSceneCaptureCount window(s); treat them as scouting evidence until refreshed scene data or live-history validation confirms the window.") | Out-Null
}
if ($nearStrongTotal -gt 0 -and $strongTotal -le 0) {
    $recommendations.Add("Near-strong samples appeared without strict strong input; inspect per-capture topNearStrongInputs before changing thresholds.") | Out-Null
}
if ($pumpingRiskCount -gt 0) {
    $recommendations.Add("One or more manual-tune captures flagged AGC pumping risk; reject those windows for NR5 tuning promotion.") | Out-Null
}

$serializedOutputRoot = ConvertTo-BundleRelativeEvidencePath -Path $captureRoot -BundlePath $bundlePath
$bundleRelativePaths = (-not [string]::IsNullOrWhiteSpace($bundlePath)) -and (Test-PortableEvidencePath $serializedOutputRoot)
foreach ($capture in $captureArray) {
    if (-not (Test-PortableEvidencePath ([string]$capture.reportPath)) -or -not (Test-PortableEvidencePath ([string]$capture.jsonlPath))) {
        $bundleRelativePaths = $false
        break
    }
}
$bestWeakOnlyCaptureVfoHz = Get-NullableLongValue (Get-JsonValue $bestWeakOnlyCapture "vfoHz")
$bestWeakOnlyCaptureVfoMhz = if ($null -ne $bestWeakOnlyCaptureVfoHz) {
    [Math]::Round([double]$bestWeakOnlyCaptureVfoHz / 1000000.0, 6)
}
else {
    $null
}
$bestNearStrongPromotionCandidateVfoHz = Get-NullableLongValue (Get-JsonValue $bestNearStrongPromotionCandidateCapture "vfoHz")
$bestNearStrongPromotionCandidateVfoMhz = if ($null -ne $bestNearStrongPromotionCandidateVfoHz) {
    [Math]::Round([double]$bestNearStrongPromotionCandidateVfoHz / 1000000.0, 6)
}
else {
    $null
}

$bestMixedReadyCapture = @($captureArray | Where-Object {
        Test-Truthy (Get-JsonValue $_ "mixedWeakStrongEvidenceReady")
    } | Select-Object -First 1)
$observerCommandTemplate = New-ManualTuneObserverCommandTemplate -Samples ([Math]::Max(32, $CaptureSamples)) -MaxPerVfo ([Math]::Max(2, $MaxCapturesPerVfo))
$primaryManualTuneAction = $null
if ($bestMixedReadyCapture.Count -gt 0) {
    $mixedVfoHz = Get-NullableLongValue (Get-JsonValue $bestMixedReadyCapture[0] "vfoHz")
    $mixedVfoMhz = if ($null -ne $mixedVfoHz) { [Math]::Round([double]$mixedVfoHz / 1000000.0, 6) } else { $null }
    $mixedVfoText = Format-MhzText -Hz $mixedVfoHz -Mhz $mixedVfoMhz
    $primaryManualTuneAction = New-ManualTuneAction `
        -ActionId "promote-manual-observer-mixed-weak-strong-capture" `
        -Status "mixed-ready" `
        -Priority 1 `
        -Summary "Mixed weak+strong evidence was captured at $mixedVfoText; promote this watcher window into live history before tuning DSP behavior." `
        -ManualAction "Keep DSP defaults unchanged; promote the captured watcher report into live history and compare current-Zeus/Thetis windows before any opt-in NR5/SPNR tuning." `
        -FollowUp "Run summarize-dsp-live-diagnostics-history.ps1, strict validation, and live trace comparisons against current-Zeus/Thetis evidence." `
        -VfoHz $mixedVfoHz `
        -VfoMhz $mixedVfoMhz `
        -ReportPathValue ([string](Get-JsonValue $bestMixedReadyCapture[0] "reportPath")) `
        -JsonlPathValue ([string](Get-JsonValue $bestMixedReadyCapture[0] "jsonlPath")) `
        -WeakInputSampleCount (Get-IntValue (Get-JsonValue $bestMixedReadyCapture[0] "weakInputSampleCount")) `
        -StrongInputSampleCount (Get-IntValue (Get-JsonValue $bestMixedReadyCapture[0] "strongInputSampleCount")) `
        -NearStrongInputSampleCount (Get-IntValue (Get-JsonValue $bestMixedReadyCapture[0] "nearStrongInputSampleCount")) `
        -ExpectedEvidence "liveDiagnosticsHistoryMixedWeakStrongEvidenceReady=true plus current-Zeus/Thetis comparison evidence"
}
elseif ($null -ne $bestNearStrongPromotionCandidateCapture) {
    $candidateVfoText = Format-MhzText -Hz $bestNearStrongPromotionCandidateVfoHz -Mhz $bestNearStrongPromotionCandidateVfoMhz
    $distanceText = if ($null -ne $bestNearStrongPromotionCandidateDistanceToStrongThresholdDb) {
        "{0:N1} dB below strong threshold" -f [double]$bestNearStrongPromotionCandidateDistanceToStrongThresholdDb
    }
    else {
        "near the strong threshold"
    }
    $primaryManualTuneAction = New-ManualTuneAction `
        -ActionId "recapture-manual-observer-near-strong-vfo" `
        -Status "near-strong-weak-only" `
        -Priority 2 `
        -Summary "Hold or return to $candidateVfoText and recapture; the best weak-only window already had near-strong speech ($distanceText)." `
        -ManualAction "Stay manually tuned near $candidateVfoText and wait for the next active speech burst; do not use retune/VFO-writing tools for this observer pass." `
        -CommandTemplate $observerCommandTemplate `
        -FollowUp "Promote only if strongInputSampleCount becomes non-zero and AGC pumping remains false." `
        -VfoHz $bestNearStrongPromotionCandidateVfoHz `
        -VfoMhz $bestNearStrongPromotionCandidateVfoMhz `
        -ReportPathValue ([string](Get-JsonValue $bestNearStrongPromotionCandidateCapture "reportPath")) `
        -JsonlPathValue ([string](Get-JsonValue $bestNearStrongPromotionCandidateCapture "jsonlPath")) `
        -DistanceToStrongThresholdDb $bestNearStrongPromotionCandidateDistanceToStrongThresholdDb `
        -WeakInputSampleCount (Get-IntValue (Get-JsonValue $bestNearStrongPromotionCandidateCapture "weakInputSampleCount")) `
        -StrongInputSampleCount (Get-IntValue (Get-JsonValue $bestNearStrongPromotionCandidateCapture "strongInputSampleCount")) `
        -NearStrongInputSampleCount (Get-IntValue (Get-JsonValue $bestNearStrongPromotionCandidateCapture "nearStrongInputSampleCount")) `
        -ExpectedEvidence "same-VFO recapture with weakInputSampleCount>0 and strongInputSampleCount>0"
}
elseif ($null -ne $bestWeakOnlyCapture) {
    $bestWeakVfoText = Format-MhzText -Hz $bestWeakOnlyCaptureVfoHz -Mhz $bestWeakOnlyCaptureVfoMhz
    $primaryManualTuneAction = New-ManualTuneAction `
        -ActionId "recapture-manual-observer-weak-only-vfo" `
        -Status "weak-only" `
        -Priority 3 `
        -Summary "Best capture at $bestWeakVfoText is weak-only; keep collecting that VFO until a strict strong-input speech sample appears." `
        -ManualAction "Stay manually tuned near $bestWeakVfoText during active speech and recapture with bounded same-VFO retries." `
        -CommandTemplate $observerCommandTemplate `
        -FollowUp "If the window remains weak-only after recapture, continue manual scanning instead of treating it as mixed evidence." `
        -VfoHz $bestWeakOnlyCaptureVfoHz `
        -VfoMhz $bestWeakOnlyCaptureVfoMhz `
        -ReportPathValue ([string](Get-JsonValue $bestWeakOnlyCapture "reportPath")) `
        -JsonlPathValue ([string](Get-JsonValue $bestWeakOnlyCapture "jsonlPath")) `
        -WeakInputSampleCount (Get-IntValue (Get-JsonValue $bestWeakOnlyCapture "weakInputSampleCount")) `
        -StrongInputSampleCount (Get-IntValue (Get-JsonValue $bestWeakOnlyCapture "strongInputSampleCount")) `
        -NearStrongInputSampleCount (Get-IntValue (Get-JsonValue $bestWeakOnlyCapture "nearStrongInputSampleCount")) `
        -ExpectedEvidence "strongInputSampleCount>0 in a ready watcher capture"
}
elseif ($MaxCaptures -le 0 -and $captureQualifiedPollCount -gt 0 -and $null -ne $bestObservedVfo) {
    $observedVfoHz = Get-NullableLongValue (Get-JsonValue $bestObservedVfo "vfoHz")
    $observedVfoMhz = Get-NullableDoubleValue (Get-JsonValue $bestObservedVfo "vfoMhz")
    $observedText = Format-MhzText -Hz $observedVfoHz -Mhz $observedVfoMhz
    $primaryManualTuneAction = New-ManualTuneAction `
        -ActionId "enable-manual-observer-capture" `
        -Status "capture-disabled" `
        -Priority 4 `
        -Summary "The current manual-tune VFO at $observedText is capture-qualified, but child diagnostics are disabled by -MaxCaptures 0." `
        -ManualAction "Keep the G2 tuned near $observedText and rerun the observer with capture enabled." `
        -CommandTemplate $observerCommandTemplate `
        -FollowUp "Promote only after the child watcher produces ready mixed weak+strong NR5/SPNR evidence." `
        -VfoHz $observedVfoHz `
        -VfoMhz $observedVfoMhz `
        -WeakInputSampleCount 0 `
        -StrongInputSampleCount 0 `
        -NearStrongInputSampleCount 0 `
        -ExpectedEvidence "captureCount>0 and mixedWeakStrongEvidenceReady=true"
}
elseif ([bool]$RequireNr5CaptureReady -and $baseCaptureQualifiedPollCount -gt 0 -and $captureQualifiedPollCount -le 0) {
    $observedVfoHz = Get-NullableLongValue (Get-JsonValue $bestObservedVfo "vfoHz")
    $observedVfoMhz = Get-NullableDoubleValue (Get-JsonValue $bestObservedVfo "vfoMhz")
    $observedText = Format-MhzText -Hz $observedVfoHz -Mhz $observedVfoMhz
    $primaryManualTuneAction = New-ManualTuneAction `
        -ActionId "wait-for-nr5-capture-readiness" `
        -Status "nr5-capture-readiness-blocked" `
        -Priority 4 `
        -Summary "The manual-tune window at $observedText was otherwise capture-qualified, but NR5/SPNR readiness blocked child capture." `
        -ManualAction "Hold the signal if it is still active, reassert NR5/SPNR if needed, and wait until requested/effective NR mode and NR5 diagnostics are ready before recapturing." `
        -CommandTemplate $observerCommandTemplate `
        -FollowUp "Rerun the observer with -RequireNr5CaptureReady; child captures should start only after nr5CaptureReady=true." `
        -VfoHz $observedVfoHz `
        -VfoMhz $observedVfoMhz `
        -WeakInputSampleCount 0 `
        -StrongInputSampleCount 0 `
        -NearStrongInputSampleCount 0 `
        -ExpectedEvidence "nr5CaptureReady=true and ready watcher capture with mixed weak+strong evidence"
}
elseif ($null -ne $bestObservedVfo -and $null -ne (Get-NullableLongValue (Get-JsonValue $bestObservedVfo "frontendSuggestedVfoHz"))) {
    $observedVfoHz = Get-NullableLongValue (Get-JsonValue $bestObservedVfo "vfoHz")
    $observedVfoMhz = Get-NullableDoubleValue (Get-JsonValue $bestObservedVfo "vfoMhz")
    $suggestedVfoHz = Get-NullableLongValue (Get-JsonValue $bestObservedVfo "frontendSuggestedVfoHz")
    $suggestedVfoMhz = Get-NullableDoubleValue (Get-JsonValue $bestObservedVfo "frontendSuggestedVfoMhz")
    $exactSuggestedVfoHz = Get-NullableLongValue (Get-JsonValue $bestObservedVfo "frontendExactSuggestedVfoHz")
    $exactSuggestedVfoMhz = Get-NullableDoubleValue (Get-JsonValue $bestObservedVfo "frontendExactSuggestedVfoMhz")
    $suggestedText = Format-MhzText -Hz $suggestedVfoHz -Mhz $suggestedVfoMhz
    $primaryManualTuneAction = New-ManualTuneAction `
        -ActionId "manual-tune-to-frontend-suggestion" `
        -Status "tuning-hint" `
        -Priority 4 `
        -Summary "Manually tune toward $suggestedText so the strongest frontend peak lands inside the active RX filter." `
        -ManualAction "Tune G2 near $suggestedText, then rerun the observer with capture enabled and frontend passband required." `
        -CommandTemplate $observerCommandTemplate `
        -FollowUp "Capture is still scouting evidence until a ready watcher window has weak and strong NR5/SPNR samples." `
        -VfoHz $observedVfoHz `
        -VfoMhz $observedVfoMhz `
        -SuggestedVfoHz $suggestedVfoHz `
        -SuggestedVfoMhz $suggestedVfoMhz `
        -SuggestedDialShiftHz (Get-NullableDoubleValue (Get-JsonValue $bestObservedVfo "frontendSuggestedDialShiftHz")) `
        -ExactSuggestedVfoHz $exactSuggestedVfoHz `
        -ExactSuggestedVfoMhz $exactSuggestedVfoMhz `
        -ExactSuggestedDialShiftHz (Get-NullableDoubleValue (Get-JsonValue $bestObservedVfo "frontendExactSuggestedDialShiftHz")) `
        -SuggestedVfoStepHzValue (Get-NullableLongValue (Get-JsonValue $bestObservedVfo "frontendSuggestedVfoStepHz")) `
        -SuggestedTuneReason ([string](Get-JsonValue $bestObservedVfo "frontendSuggestedTuneReason")) `
        -ExpectedEvidence "captureQualifiedPollCount>0 followed by a ready mixed weak+strong watcher capture"
}
elseif ($null -ne $bestObservedVfo) {
    $observedVfoHz = Get-NullableLongValue (Get-JsonValue $bestObservedVfo "vfoHz")
    $observedVfoMhz = Get-NullableDoubleValue (Get-JsonValue $bestObservedVfo "vfoMhz")
    $observedText = Format-MhzText -Hz $observedVfoHz -Mhz $observedVfoMhz
    $primaryManualTuneAction = New-ManualTuneAction `
        -ActionId "hold-manual-observed-vfo" `
        -Status ([string](Get-JsonValue $bestObservedVfo "status")) `
        -Priority 5 `
        -Summary "Hold $observedText and keep collecting; this was the best observed manual-tune VFO but no mixed capture was produced." `
        -ManualAction "Stay near $observedText while the signal is active, then rerun the observer with capture enabled." `
        -CommandTemplate $observerCommandTemplate `
        -FollowUp "Do not promote this scouting state until watcher captures are ready and mixed weak+strong evidence is present." `
        -VfoHz $observedVfoHz `
        -VfoMhz $observedVfoMhz `
        -ExpectedEvidence "ready watcher capture with mixedWeakStrongEvidenceReady=true"
}
else {
    $primaryManualTuneAction = New-ManualTuneAction `
        -ActionId "continue-manual-scan" `
        -Status "no-stable-window" `
        -Priority 6 `
        -Summary "No stable manual-tune VFO produced a useful capture or tuning hint; keep scanning active SSB windows." `
        -ManualAction "Continue manual tuning across active 20m phone windows and rerun the observer when speech-like peaks are visible in the passband." `
        -CommandTemplate $observerCommandTemplate `
        -FollowUp "Lower MinCoherentSnrDb only for scouting; acceptance captures still need ready mixed weak+strong evidence." `
        -ExpectedEvidence "captureQualifiedPollCount>0 and mixedWeakStrongEvidenceReady=true"
}

$report = [ordered]@{
    schemaVersion = 2
    tool = "watch-dsp-manual-tune-observer"
    generatedUtc = $completedUtc.ToString("o")
    startedUtc = $startedUtc.ToString("o")
    completedUtc = $completedUtc.ToString("o")
    durationMs = [int]($completedUtc - $startedUtc).TotalMilliseconds
    ok = ([string]::IsNullOrWhiteSpace($scanError))
    scanError = $scanError
    baseUrl = $base
    bundleDir = $bundlePath
    bundleRelativePaths = $bundleRelativePaths
    outputRoot = $serializedOutputRoot
    label = $Label
    scenarioId = $ScenarioId
    comparisonId = $ComparisonId
    pollCount = $PollCount
    pollIntervalSec = $PollIntervalSec
    stablePolls = $StablePolls
    minCoherentSnrDb = $MinCoherentSnrDb
    sceneProfilePattern = $SceneProfilePattern
    requireFrontendNearPassband = [bool]$RequireFrontendNearPassband
    frontendNearPassbandThresholdHz = $FrontendNearPassbandThresholdHz
    frontendOffsetMismatchToleranceHz = $FrontendOffsetMismatchToleranceHz
    maxCaptures = $MaxCaptures
    maxCapturesPerVfo = $MaxCapturesPerVfo
    allowStaleSceneCapture = [bool]$AllowStaleSceneCapture
    captureSamples = $CaptureSamples
    captureIntervalMs = $CaptureIntervalMs
    safety = [ordered]@{
        rxOnly = $true
        readOnly = $true
        apiWrites = $false
        retune = $false
        vfoWriteAttemptCount = 0
        radioLoWriteAttemptCount = 0
        txEndpointsTouched = $false
        delegatedCapture = "watch-dsp-live-diagnostics.ps1"
    }
    pollSampleCount = $pollArray.Count
    observedVfoCount = $observedVfoArray.Count
    observedVfos = @($observedVfoArray)
    bestObservedVfo = $bestObservedVfo
    bestObservedVfoHz = Get-NullableLongValue (Get-JsonValue $bestObservedVfo "vfoHz")
    bestObservedVfoMhz = Get-NullableDoubleValue (Get-JsonValue $bestObservedVfo "vfoMhz")
    bestObservedVfoStatus = [string](Get-JsonValue $bestObservedVfo "status")
    bestObservedVfoScore = Get-NullableDoubleValue (Get-JsonValue $bestObservedVfo "score")
    bestObservedVfoSuggestedVfoHz = Get-NullableLongValue (Get-JsonValue $bestObservedVfo "frontendSuggestedVfoHz")
    bestObservedVfoSuggestedVfoMhz = Get-NullableDoubleValue (Get-JsonValue $bestObservedVfo "frontendSuggestedVfoMhz")
    bestObservedVfoSuggestedDialShiftHz = Get-NullableDoubleValue (Get-JsonValue $bestObservedVfo "frontendSuggestedDialShiftHz")
    bestObservedVfoSuggestedVfoStepHz = Get-NullableLongValue (Get-JsonValue $bestObservedVfo "frontendSuggestedVfoStepHz")
    bestObservedVfoExactSuggestedVfoHz = Get-NullableLongValue (Get-JsonValue $bestObservedVfo "frontendExactSuggestedVfoHz")
    bestObservedVfoExactSuggestedVfoMhz = Get-NullableDoubleValue (Get-JsonValue $bestObservedVfo "frontendExactSuggestedVfoMhz")
    bestObservedVfoExactSuggestedDialShiftHz = Get-NullableDoubleValue (Get-JsonValue $bestObservedVfo "frontendExactSuggestedDialShiftHz")
    bestObservedVfoSuggestedTuneReason = [string](Get-JsonValue $bestObservedVfo "frontendSuggestedTuneReason")
    captureCount = $captureArray.Count
    uniqueCapturedVfoCount = $uniqueCapturedVfoCount
    recapturedVfoCount = $recapturedVfoCount
    staleScenePollCount = $staleScenePollCount
    staleSceneCaptureCount = $staleSceneCaptureCount
    frontendNearPassbandPollCount = $frontendNearPassbandPollCount
    frontendOffPassbandPollCount = $frontendOffPassbandPollCount
    frontendFilterPassbandPollCount = $frontendFilterPassbandPollCount
    frontendFilterOffPassbandPollCount = $frontendFilterOffPassbandPollCount
    frontendOffsetMismatchPollCount = $frontendOffsetMismatchPollCount
    frontendTuningHintPollCount = $frontendTuningHintPollCount
    frontendBestTuningHint = $frontendBestTuningHint
    suggestedVfoStepHz = $SuggestedVfoStepHz
    requireNr5CaptureReady = [bool]$RequireNr5CaptureReady
    baseCaptureQualifiedPollCount = $baseCaptureQualifiedPollCount
    nr5CaptureReadyPollCount = $nr5CaptureReadyPollCount
    nr5CaptureBlockedPollCount = $nr5CaptureBlockedPollCount
    nr5CaptureAdvisoryPollCount = $nr5CaptureAdvisoryPollCount
    captureQualifiedPollCount = $captureQualifiedPollCount
    triggerAudioActivePollCount = $triggerAudioActivePollCount
    triggerMaxAudioRmsDbfs = $triggerMaxAudioRmsDbfs
    triggerMaxNr5OutputDbfs = $triggerMaxNr5OutputDbfs
    bestTriggerPoll = $bestTriggerPoll
    readyCaptureCount = $readyCaptureCount
    mixedWeakStrongReady = ($mixedReadyCount -gt 0)
    mixedWeakStrongReadyCaptureCount = $mixedReadyCount
    mixedWeakStrongEvidenceStatusCounts = @($mixedWeakStrongStatusCountRecords)
    missingStrongInputCaptureCount = $missingStrongInputCaptureCount
    missingWeakInputCaptureCount = $missingWeakInputCaptureCount
    missingWeakAndStrongInputCaptureCount = $missingWeakAndStrongInputCaptureCount
    missingOutputGapCaptureCount = $missingOutputGapCaptureCount
    weakStrongOutputGapWatchCaptureCount = $weakStrongOutputGapWatchCaptureCount
    weakOnlyCaptureCount = $weakOnlyCaptureCount
    readyWeakOnlyCaptureCount = $readyWeakOnlyCaptureCount
    strongOnlyCaptureCount = $strongOnlyCaptureCount
    bestWeakOnlyCapture = $bestWeakOnlyCapture
    bestWeakOnlyCaptureScore = if ($null -ne $bestWeakOnlyCapture) { $bestWeakOnlyCaptureScore } else { $null }
    bestWeakOnlyCaptureVfoHz = $bestWeakOnlyCaptureVfoHz
    bestWeakOnlyCaptureVfoMhz = $bestWeakOnlyCaptureVfoMhz
    bestWeakOnlyCaptureReportPath = [string](Get-JsonValue $bestWeakOnlyCapture "reportPath")
    bestWeakOnlyCaptureWeakInputSampleCount = Get-IntValue (Get-JsonValue $bestWeakOnlyCapture "weakInputSampleCount")
    bestWeakOnlyCaptureNearStrongInputSampleCount = Get-IntValue (Get-JsonValue $bestWeakOnlyCapture "nearStrongInputSampleCount")
    bestWeakOnlyCapturePassbandQualifiedWeakInputSampleCount = Get-IntValue (Get-JsonValue $bestWeakOnlyCapture "passbandQualifiedWeakInputSampleCount")
    bestWeakOnlyCaptureAgcStabilityStatus = [string](Get-JsonValue $bestWeakOnlyCapture "agcStabilityStatus")
    nearStrongPromotionCandidateCaptureCount = $nearStrongPromotionCandidateCaptureCount
    bestNearStrongPromotionCandidateCapture = $bestNearStrongPromotionCandidateCapture
    bestNearStrongPromotionCandidateScore = if ($null -ne $bestNearStrongPromotionCandidateCapture) { $bestNearStrongPromotionCandidateScore } else { $null }
    bestNearStrongPromotionCandidateReportPath = [string](Get-JsonValue $bestNearStrongPromotionCandidateCapture "reportPath")
    bestNearStrongPromotionCandidateVfoHz = $bestNearStrongPromotionCandidateVfoHz
    bestNearStrongPromotionCandidateVfoMhz = $bestNearStrongPromotionCandidateVfoMhz
    bestNearStrongPromotionCandidateDistanceToStrongThresholdDb = $bestNearStrongPromotionCandidateDistanceToStrongThresholdDb
    bestNearStrongPromotionCandidateNearStrongInputSampleCount = Get-IntValue (Get-JsonValue $bestNearStrongPromotionCandidateCapture "nearStrongInputSampleCount")
    bestNearStrongPromotionCandidateSpeechQualifiedNearStrongInputSampleCount = Get-IntValue (Get-JsonValue $bestNearStrongPromotionCandidateCapture "speechQualifiedNearStrongInputSampleCount")
    bestNearStrongPromotionCandidatePassbandQualifiedNearStrongInputSampleCount = Get-IntValue (Get-JsonValue $bestNearStrongPromotionCandidateCapture "passbandQualifiedNearStrongInputSampleCount")
    weakInputSampleCount = $weakTotal
    strongInputSampleCount = $strongTotal
    nearStrongInputSampleCount = $nearStrongTotal
    speechQualifiedWeakInputSampleCount = $speechWeakTotal
    speechQualifiedStrongInputSampleCount = $speechStrongTotal
    passbandQualifiedWeakInputSampleCount = $passbandWeakTotal
    passbandQualifiedStrongInputSampleCount = $passbandStrongTotal
    agcPumpingRiskCaptureCount = $pumpingRiskCount
    primaryManualTuneAction = $primaryManualTuneAction
    primaryManualTuneActionId = [string](Get-JsonValue $primaryManualTuneAction "actionId")
    primaryManualTuneActionStatus = [string](Get-JsonValue $primaryManualTuneAction "status")
    primaryManualTuneActionSummary = [string](Get-JsonValue $primaryManualTuneAction "summary")
    primaryManualTuneActionManualAction = [string](Get-JsonValue $primaryManualTuneAction "manualAction")
    primaryManualTuneActionCommandTemplate = [string](Get-JsonValue $primaryManualTuneAction "commandTemplate")
    primaryManualTuneActionExpectedEvidence = [string](Get-JsonValue $primaryManualTuneAction "expectedEvidence")
    captures = @($captureArray)
    polls = @($pollArray)
    recommendations = @($recommendations.ToArray())
}

Write-JsonFile -Path $ReportPath -Value $report

if ($JsonOnly) {
    $report | ConvertTo-Json -Depth 64
}
else {
    Write-Host "Manual-tune observer report: $ReportPath"
    Write-Host "Captures: $($captureArray.Count), mixed weak+strong ready: $($report.mixedWeakStrongReady), weak samples: $weakTotal, strong samples: $strongTotal, near-strong samples: $nearStrongTotal"
    Write-Host "Primary action: $($report.primaryManualTuneActionId) - $($report.primaryManualTuneActionSummary)"
    if (-not [string]::IsNullOrWhiteSpace($report.primaryManualTuneActionManualAction)) {
        Write-Host "Manual action: $($report.primaryManualTuneActionManualAction)"
    }
    if (-not [string]::IsNullOrWhiteSpace($report.primaryManualTuneActionCommandTemplate)) {
        Write-Host "Next observer command: $($report.primaryManualTuneActionCommandTemplate)"
    }
}

if (-not [string]::IsNullOrWhiteSpace($scanError) -and -not $ContinueOnError) {
    exit 2
}
