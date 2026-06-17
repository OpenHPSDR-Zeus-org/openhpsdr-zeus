param(
    [string]$BaseUrl = "http://localhost:6060",

    [int]$PollCount = 30,

    [int]$PollIntervalSec = 2,

    [int]$StablePolls = 3,

    [double]$MinCoherentSnrDb = 12.0,

    [string]$SceneProfilePattern = "voice|speech|phone",

    [int]$MaxCaptures = 4,

    [int]$CaptureSamples = 16,

    [int]$CaptureIntervalMs = 250,

    [int]$TimeoutSec = 8,

    [string]$OutputRoot = "",

    [string]$ReportPath = "",

    [string]$WatchScriptPath = "",

    [string]$Label = "manual-tune-observer",

    [string]$ScenarioId = "g2-manual-tune-observer",

    [string]$ComparisonId = "nr5-spnr",

    [switch]$SkipCertificateCheck,

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
if ($MaxCaptures -lt 0) {
    $MaxCaptures = 0
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
        maxCaptures = $MaxCaptures
        captureSamples = $CaptureSamples
        captureIntervalMs = $CaptureIntervalMs
        scenarioId = $ScenarioId
        comparisonId = $ComparisonId
        watchScriptPath = $resolvedWatchScript
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
                "Traces captured while the operator continues tuning are scouting evidence, not final acceptance proof."
            )
        }
        example = "powershell -NoProfile -ExecutionPolicy Bypass -File tools\watch-dsp-manual-tune-observer.ps1 -BaseUrl $base -PollCount 60 -StablePolls 3 -MaxCaptures 4"
    } | ConvertTo-Json -Depth 16
    exit 0
}

$startedUtc = [DateTimeOffset]::UtcNow
$safeLabel = ConvertTo-SafeName $Label
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "captures\dsp-manual-tune-observer"
}
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$captureRoot = Join-Path $OutputRoot ("{0}-{1}" -f $startedUtc.ToString("yyyyMMddTHHmmssfffZ"), $safeLabel)
New-Item -ItemType Directory -Force -Path $captureRoot | Out-Null

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path $captureRoot "manual-tune-observer-report.json"
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
        $captureQualified = ($stableQualified -and $sceneFresh -and $snrQualified -and $profileMatches)

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
            topPeakCount = @(Get-JsonValue $scene "topPeaks").Count
            liveStatus = [string](Get-JsonValue $live "status")
            requestedNrMode = [string](Get-JsonValue $live "requestedNrMode")
            effectiveNrMode = [string](Get-JsonValue $live "effectiveNrMode")
            readyForNr5Tuning = Test-Truthy (Get-JsonValue $live "readyForNr5Tuning")
            captureQualified = $captureQualified
        }
        $polls.Add([pscustomobject]$pollRecord) | Out-Null

        if ($captureQualified -and $captures.Count -lt $MaxCaptures) {
            $vfoKey = [string]$vfo
            if (-not $capturedVfoMap.ContainsKey($vfoKey)) {
                $capturedVfoMap[$vfoKey] = $true
                $captureDir = Join-Path $captureRoot $vfoKey
                New-Item -ItemType Directory -Force -Path $captureDir | Out-Null
                $summaryPath = Join-Path $captureDir "live-diagnostics-watch.json"
                $jsonlPath = Join-Path $captureDir "live-diagnostics-watch.jsonl"
                $captureLabel = ConvertTo-SafeName ("{0}-{1}" -f $safeLabel, $vfoKey)
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
                $captures.Add([pscustomobject][ordered]@{
                    ok = Test-Truthy $watch.ok
                    exitCode = Get-IntValue (Get-JsonValue $watch "exitCode")
                    error = [string](Get-JsonValue $watch "error")
                    vfoHz = $vfo
                    radioLoHz = Get-NullableLongValue (Get-JsonValue $state "radioLoHz")
                    mode = [string](Get-JsonValue $state "mode")
                    signalProfile = $profile
                    coherentMaxSnrDb = $coherentSnr
                    reportPath = $summaryPath
                    jsonlPath = $jsonlPath
                    readyForBenchmarkTrace = Test-Truthy (Get-JsonValue $watchReport "readyForBenchmarkTrace")
                    trendStatus = [string](Get-JsonValue $watchReport "trendStatus")
                    weakInputSampleCount = Get-IntValue (Get-JsonValue $weak "weakInputSampleCount")
                    strongInputSampleCount = Get-IntValue (Get-JsonValue $weak "strongInputSampleCount")
                    nearStrongInputSampleCount = Get-IntValue (Get-JsonValue $weak "nearStrongInputSampleCount")
                    mixedWeakStrongEvidenceStatus = [string](Get-JsonValue $weak "mixedWeakStrongEvidenceStatus")
                    mixedWeakStrongEvidenceReady = Test-Truthy (Get-JsonValue $weak "mixedWeakStrongEvidenceReady")
                    weakStrongOutputGapDb = Get-NullableDoubleValue (Get-JsonValue $weak "weakStrongOutputGapDb")
                    speechQualifiedWeakInputSampleCount = Get-IntValue (Get-JsonValue $weak "speechQualifiedWeakInputSampleCount")
                    speechQualifiedStrongInputSampleCount = Get-IntValue (Get-JsonValue $weak "speechQualifiedStrongInputSampleCount")
                    passbandQualifiedWeakInputSampleCount = Get-IntValue (Get-JsonValue $weak "passbandQualifiedWeakInputSampleCount")
                    passbandQualifiedStrongInputSampleCount = Get-IntValue (Get-JsonValue $weak "passbandQualifiedStrongInputSampleCount")
                    agcStabilityStatus = [string](Get-JsonValue $agc "status")
                    agcPumpingRisk = Test-Truthy (Get-JsonValue $agc "agcPumpingRisk")
                }) | Out-Null
            }
        }

        if ($captures.Count -ge $MaxCaptures) {
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
foreach ($capture in $captureArray) {
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
}

$recommendations = New-Object System.Collections.Generic.List[string]
if ($captureArray.Count -le 0) {
    $recommendations.Add("No stable voice-like manual-tune VFO met the capture threshold; keep tuning manually or lower MinCoherentSnrDb for scouting only.") | Out-Null
}
elseif ($mixedReadyCount -gt 0) {
    $recommendations.Add("At least one manual-tune capture has mixed weak+strong evidence; promote that window through live history and strict validation before tuning DSP behavior.") | Out-Null
}
elseif ($weakTotal -gt 0 -and $strongTotal -le 0) {
    $recommendations.Add("Manual-tune captures are weak-only by the strict NR5 input threshold; keep collecting active windows until strongInputSampleCount is non-zero.") | Out-Null
}
if ($nearStrongTotal -gt 0 -and $strongTotal -le 0) {
    $recommendations.Add("Near-strong samples appeared without strict strong input; inspect per-capture topNearStrongInputs before changing thresholds.") | Out-Null
}
if ($pumpingRiskCount -gt 0) {
    $recommendations.Add("One or more manual-tune captures flagged AGC pumping risk; reject those windows for NR5 tuning promotion.") | Out-Null
}

$report = [ordered]@{
    schemaVersion = 1
    tool = "watch-dsp-manual-tune-observer"
    generatedUtc = $completedUtc.ToString("o")
    startedUtc = $startedUtc.ToString("o")
    completedUtc = $completedUtc.ToString("o")
    durationMs = [int]($completedUtc - $startedUtc).TotalMilliseconds
    ok = ([string]::IsNullOrWhiteSpace($scanError))
    scanError = $scanError
    baseUrl = $base
    outputRoot = $captureRoot
    label = $Label
    scenarioId = $ScenarioId
    comparisonId = $ComparisonId
    pollCount = $PollCount
    pollIntervalSec = $PollIntervalSec
    stablePolls = $StablePolls
    minCoherentSnrDb = $MinCoherentSnrDb
    sceneProfilePattern = $SceneProfilePattern
    maxCaptures = $MaxCaptures
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
    captureCount = $captureArray.Count
    readyCaptureCount = $readyCaptureCount
    mixedWeakStrongReady = ($mixedReadyCount -gt 0)
    mixedWeakStrongReadyCaptureCount = $mixedReadyCount
    weakInputSampleCount = $weakTotal
    strongInputSampleCount = $strongTotal
    nearStrongInputSampleCount = $nearStrongTotal
    speechQualifiedWeakInputSampleCount = $speechWeakTotal
    speechQualifiedStrongInputSampleCount = $speechStrongTotal
    passbandQualifiedWeakInputSampleCount = $passbandWeakTotal
    passbandQualifiedStrongInputSampleCount = $passbandStrongTotal
    agcPumpingRiskCaptureCount = $pumpingRiskCount
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
}

if (-not [string]::IsNullOrWhiteSpace($scanError) -and -not $ContinueOnError) {
    exit 2
}
