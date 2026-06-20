param(
    [string]$BaseUrl = "https://localhost:6443",

    [int]$Samples = 24,

    [int]$IntervalMs = 500,

    [int]$TimeoutSec = 5,

    [string]$OutputRoot = "",

    [string]$SummaryPath = "",

    [string]$LabelPrefix = "g2-tx-output-headroom-ab",

    [string]$CandidateProfile = "headroom-trim-candidate",

    [string]$WatchScriptPath = "",

    [switch]$SkipCertificateCheck,

    [switch]$ContinueOnError,

    [switch]$PlanOnly,

    [switch]$PreflightOnly,

    [switch]$AllowTransmit,

    [switch]$ExpectPureSignalBypass,

    [switch]$SkipLiveReadyCheck,

    [int]$TuneStepHz = 1000
)

$ErrorActionPreference = "Stop"

function Get-RepoRoot {
    return (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
}

function Normalize-BaseUrl {
    param([Parameter(Mandatory = $true)][string]$Url)
    return $Url.TrimEnd("/")
}

function Get-Timestamp {
    return (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmssZ")
}

function New-Directory {
    param([Parameter(Mandatory = $true)][string]$Path)
    New-Item -ItemType Directory -Force -Path $Path | Out-Null
    return (Resolve-Path -LiteralPath $Path).Path
}

function Resolve-OutputPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $Path))
}

function Ensure-ParentDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    $parent = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }
}

function Get-JsonValue {
    param(
        $Object,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if ($null -eq $Object) {
        return $null
    }

    if ($Object -is [System.Collections.IDictionary]) {
        if ($Object.Contains($Name)) {
            return $Object[$Name]
        }

        foreach ($key in @($Object.Keys)) {
            if ([string]::Equals([string]$key, $Name, [StringComparison]::OrdinalIgnoreCase)) {
                return $Object[$key]
            }
        }

        return $null
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -ne $property) {
        return $property.Value
    }

    return $null
}

function Test-Truthy {
    param($Value)

    if ($null -eq $Value) {
        return $false
    }

    if ($Value -is [bool]) {
        return [bool]$Value
    }

    $text = ([string]$Value).Trim().ToLowerInvariant()
    return $text -in @("true", "1", "yes", "on", "enabled", "active")
}

function Invoke-LiveDiagnosticsApi {
    param([Parameter(Mandatory = $true)][string]$Base)

    $requestArgs = @{
        Uri = "$Base/api/dsp/live-diagnostics"
        Method = "GET"
        TimeoutSec = $TimeoutSec
    }

    if ($SkipCertificateCheck -and (Get-Command Invoke-RestMethod).Parameters.ContainsKey("SkipCertificateCheck")) {
        $requestArgs["SkipCertificateCheck"] = $true
    }

    try {
        return Invoke-RestMethod @requestArgs
    }
    catch {
        throw "Live diagnostics endpoint is not reachable at $($requestArgs["Uri"]). Open the updated frontend scene and connect the G2 before TX A/B capture, or pass -SkipLiveReadyCheck only for scripted tests. $($_.Exception.Message)"
    }
}

function Get-LiveReadiness {
    param($Diagnostics)

    $runtime = Get-JsonValue $Diagnostics "runtimeEvidence"
    $status = [string](Get-JsonValue $Diagnostics "status")
    $runtimeStatus = [string](Get-JsonValue $runtime "status")
    $readyForLiveBenchmark = Test-Truthy (Get-JsonValue $Diagnostics "readyForLiveBenchmark")
    $wdspActive = Test-Truthy (Get-JsonValue $Diagnostics "wdspActive")
    $frontendSceneFresh = Test-Truthy (Get-JsonValue $Diagnostics "frontendSceneFresh")
    $runtimeFresh = [string]::Equals($runtimeStatus, "fresh", [StringComparison]::OrdinalIgnoreCase)

    $failureReasons = New-Object System.Collections.Generic.List[string]
    if (-not $readyForLiveBenchmark) { $failureReasons.Add("readyForLiveBenchmark=false") | Out-Null }
    if (-not $wdspActive) { $failureReasons.Add("wdspActive=false") | Out-Null }
    if (-not $frontendSceneFresh) { $failureReasons.Add("frontendSceneFresh=false") | Out-Null }
    if (-not $runtimeFresh) { $failureReasons.Add("runtimeEvidence.status=$runtimeStatus") | Out-Null }

    return [ordered]@{
        ready = ($readyForLiveBenchmark -and $wdspActive -and $frontendSceneFresh -and $runtimeFresh)
        status = $status
        readyForLiveBenchmark = $readyForLiveBenchmark
        wdspActive = $wdspActive
        frontendSceneFresh = $frontendSceneFresh
        runtimeStatus = $runtimeStatus
        radioVfoHz = Get-JsonValue $Diagnostics "radioVfoHz"
        radioMode = [string](Get-JsonValue $Diagnostics "radioMode")
        generatedUtc = [string](Get-JsonValue $Diagnostics "generatedUtc")
        failureReasons = @($failureReasons.ToArray())
    }
}

function Invoke-HeadroomProfileApi {
    param(
        [Parameter(Mandatory = $true)][string]$Base,
        [Parameter(Mandatory = $true)][ValidateSet("GET", "PUT")][string]$Method,
        [string]$Profile = ""
    )

    $requestArgs = @{
        Uri = "$Base/api/dsp/tx-output-headroom-profile"
        Method = $Method
        TimeoutSec = $TimeoutSec
    }

    if ($SkipCertificateCheck -and (Get-Command Invoke-RestMethod).Parameters.ContainsKey("SkipCertificateCheck")) {
        $requestArgs["SkipCertificateCheck"] = $true
    }

    if ($Method -eq "PUT") {
        $requestArgs["ContentType"] = "application/json"
        $requestArgs["Body"] = (@{ profile = $Profile } | ConvertTo-Json -Compress)
    }

    try {
        return Invoke-RestMethod @requestArgs
    }
    catch {
        throw "TX output headroom profile API is not reachable at $($requestArgs["Uri"]). Start the updated WDSP v2 backend before running TX A/B capture. $($_.Exception.Message)"
    }
}

function Wait-HeadroomProfile {
    param(
        [Parameter(Mandatory = $true)][string]$Base,
        [Parameter(Mandatory = $true)][string]$Profile,
        [string]$ExpectedActiveProfile = "",
        [bool]$ExpectedPureSignalBypass = $false,
        [int]$TimeoutMs = 8000
    )

    if ([string]::IsNullOrWhiteSpace($ExpectedActiveProfile)) {
        $ExpectedActiveProfile = $Profile
    }

    $deadline = [DateTimeOffset]::UtcNow.AddMilliseconds($TimeoutMs)
    $last = $null
    do {
        $last = Invoke-HeadroomProfileApi -Base $Base -Method GET
        $profileAligned = [string]::Equals([string]$last.profile, $Profile, [StringComparison]::OrdinalIgnoreCase)
        $activeProfileAligned = [string]::Equals([string]$last.activeProfile, $ExpectedActiveProfile, [StringComparison]::OrdinalIgnoreCase)
        $bypassAligned = ([bool]$last.pureSignalBypassActive) -eq $ExpectedPureSignalBypass
        if ($profileAligned -and $activeProfileAligned -and $bypassAligned) {
            return [ordered]@{
                ready = $true
                profile = [string]$last.profile
                activeProfile = [string]$last.activeProfile
                profileAligned = $profileAligned
                activeProfileAligned = $activeProfileAligned
                pureSignalBypassActive = [bool]$last.pureSignalBypassActive
                pureSignalBypassAligned = $bypassAligned
                trimDb = $last.trimDb
            }
        }

        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    $profileAligned = [string]::Equals([string]$last.profile, $Profile, [StringComparison]::OrdinalIgnoreCase)
    $activeProfileAligned = [string]::Equals([string]$last.activeProfile, $ExpectedActiveProfile, [StringComparison]::OrdinalIgnoreCase)
    $bypassAligned = ($null -ne $last -and ([bool]$last.pureSignalBypassActive) -eq $ExpectedPureSignalBypass)
    return [ordered]@{
        ready = $false
        profile = [string]$last.profile
        activeProfile = [string]$last.activeProfile
        profileAligned = $profileAligned
        activeProfileAligned = $activeProfileAligned
        pureSignalBypassActive = if ($null -ne $last) { [bool]$last.pureSignalBypassActive } else { $false }
        pureSignalBypassAligned = $bypassAligned
        trimDb = if ($null -ne $last) { $last.trimDb } else { $null }
    }
}

function Invoke-WatcherCapture {
    param(
        [Parameter(Mandatory = $true)][string]$Base,
        [Parameter(Mandatory = $true)][string]$Profile,
        [Parameter(Mandatory = $true)][string]$RunRoot
    )

    $watcher = $script:ResolvedWatcherPath
    if (-not (Test-Path -LiteralPath $watcher)) {
        throw "Watcher script not found: $watcher"
    }

    $captureRoot = New-Directory (Join-Path $RunRoot $Profile)
    $jsonlPath = Join-Path $captureRoot "live-diagnostics-trace.jsonl"
    $reportPath = Join-Path $captureRoot "live-diagnostics-summary.json"

    $watcherArgs = @{
        BaseUrl = $Base
        Samples = $Samples
        IntervalMs = $IntervalMs
        TimeoutSec = $TimeoutSec
        JsonlPath = $jsonlPath
        ReportPath = $reportPath
        Label = "$LabelPrefix-$Profile"
        TuneStepHz = $TuneStepHz
        JsonOnly = $true
    }

    if ($SkipCertificateCheck) {
        $watcherArgs["SkipCertificateCheck"] = $true
    }
    if ($ContinueOnError) {
        $watcherArgs["ContinueOnError"] = $true
    }

    $global:LASTEXITCODE = $null
    & $watcher @watcherArgs | Out-Null
    $watcherSucceeded = $?
    $watcherExitCode = $LASTEXITCODE
    if (-not $watcherSucceeded -or ($null -ne $watcherExitCode -and $watcherExitCode -ne 0)) {
        $exitCodeText = if ($null -eq $watcherExitCode) { "unknown" } else { [string]$watcherExitCode }
        throw "watch-dsp-live-diagnostics failed for TX headroom profile '$Profile' with exit code $exitCodeText"
    }

    $summary = Get-Content -Raw -LiteralPath $reportPath | ConvertFrom-Json
    $headroomWatch = $summary.txOutputHeadroomWatch
    $requestedProfileCounts = @()
    $activeProfileCounts = @()
    $experimentalSampleCount = 0
    $pureSignalBypassedSampleCount = 0
    if ($null -ne $headroomWatch) {
        if ($null -ne $headroomWatch.requestedProfileCounts) {
            $requestedProfileCounts = @($headroomWatch.requestedProfileCounts)
        }
        if ($null -ne $headroomWatch.activeProfileCounts) {
            $activeProfileCounts = @($headroomWatch.activeProfileCounts)
        }
        if ($null -ne $headroomWatch.experimentalSampleCount) {
            $experimentalSampleCount = [int]$headroomWatch.experimentalSampleCount
        }
        if ($null -ne $headroomWatch.pureSignalBypassedSampleCount) {
            $pureSignalBypassedSampleCount = [int]$headroomWatch.pureSignalBypassedSampleCount
        }
    }

    return [ordered]@{
        profile = $Profile
        jsonlPath = $jsonlPath
        reportPath = $reportPath
        trendStatus = [string]$summary.trendStatus
        readyForBenchmarkTrace = [bool]$summary.readyForBenchmarkTrace
        okSampleCount = [int]$summary.okSampleCount
        runtimeEvidenceSampleCount = [int]$summary.runtimeEvidenceSampleCount
        txMonitorSampleCount = [int]$summary.txMonitorSampleCount
        txOutputHeadroomWatch = $headroomWatch
        requestedProfileCounts = @($requestedProfileCounts)
        activeProfileCounts = @($activeProfileCounts)
        experimentalSampleCount = $experimentalSampleCount
        pureSignalBypassedSampleCount = $pureSignalBypassedSampleCount
    }
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

$base = Normalize-BaseUrl $BaseUrl
$repoRoot = Get-RepoRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path (Join-Path $repoRoot "tmp") "wdsp-v2-live"
}

$script:ResolvedWatcherPath = if ([string]::IsNullOrWhiteSpace($WatchScriptPath)) {
    Join-Path $PSScriptRoot "watch-dsp-live-diagnostics.ps1"
}
else {
    (Resolve-Path -LiteralPath $WatchScriptPath).Path
}

$plannedRunRoot = Join-Path $OutputRoot ("tx-output-headroom-ab-" + (Get-Timestamp))
$runRoot = if ($PlanOnly) { $plannedRunRoot } else { New-Directory $plannedRunRoot }
$summaryPath = if ([string]::IsNullOrWhiteSpace($SummaryPath)) {
    Join-Path $runRoot "tx-output-headroom-ab-summary.json"
}
else {
    Resolve-OutputPath $SummaryPath
}
$candidateExpectedActiveProfile = if ($ExpectPureSignalBypass) { "current" } else { $CandidateProfile }
$plan = [ordered]@{
    schemaVersion = 1
    tool = "capture-tx-output-headroom-ab"
    mode = "plan-only"
    baseUrl = $base
    samples = $Samples
    intervalMs = $IntervalMs
    currentProfile = "current"
    candidateProfile = $CandidateProfile
    candidateExpectedActiveProfile = $candidateExpectedActiveProfile
    expectPureSignalBypass = [bool]$ExpectPureSignalBypass
    allowTransmit = [bool]$AllowTransmit
    noKeyingByScript = $true
    requiresLiveReady = (-not [bool]$SkipLiveReadyCheck)
    liveDiagnosticsEndpoint = "$base/api/dsp/live-diagnostics"
    outputRoot = $runRoot
    summaryPath = $summaryPath
    watcher = $script:ResolvedWatcherPath
    tuneStepHz = $TuneStepHz
    manualAction = "Use -PlanOnly first. For execution, pass -AllowTransmit only when the operator is ready to manually key a controlled TX voice test; this script never keys MOX, TUN, or two-tone and resets the profile to current in finally."
}

if ($PlanOnly) {
    $plan | ConvertTo-Json -Depth 8
    exit 0
}

if ($PreflightOnly) {
    $liveReadiness = $null
    if (-not $SkipLiveReadyCheck) {
        $liveReadiness = Get-LiveReadiness (Invoke-LiveDiagnosticsApi -Base $base)
    }

    $profilePreflight = Invoke-HeadroomProfileApi -Base $base -Method GET
    $supportedProfiles = @()
    if ($null -ne $profilePreflight.supportedProfiles) {
        $supportedProfiles = @($profilePreflight.supportedProfiles | ForEach-Object { [string]$_ })
    }

    $profileCurrent = [string]::Equals([string]$profilePreflight.profile, "current", [StringComparison]::OrdinalIgnoreCase)
    $activeCurrent = [string]::Equals([string]$profilePreflight.activeProfile, "current", [StringComparison]::OrdinalIgnoreCase)
    $candidateSupported = @($supportedProfiles | Where-Object { [string]::Equals($_, $CandidateProfile, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0
    $liveReady = $SkipLiveReadyCheck -or ($null -ne $liveReadiness -and [bool]$liveReadiness.ready)

    $preflightFailures = New-Object System.Collections.Generic.List[string]
    if (-not $liveReady) {
        $preflightFailures.Add("live-diagnostics-not-ready") | Out-Null
    }
    if (-not $profileCurrent) {
        $preflightFailures.Add("requested-profile-not-current") | Out-Null
    }
    if (-not $activeCurrent) {
        $preflightFailures.Add("active-profile-not-current") | Out-Null
    }
    if (-not $candidateSupported) {
        $preflightFailures.Add("candidate-profile-not-supported") | Out-Null
    }

    $preflightResult = [ordered]@{
        schemaVersion = 1
        tool = "capture-tx-output-headroom-ab"
        mode = "preflight-only"
        generatedUtc = (Get-Date).ToUniversalTime().ToString("o")
        baseUrl = $base
        outputRoot = $runRoot
        summaryPath = $summaryPath
        allowTransmit = [bool]$AllowTransmit
        noKeyingByScript = $true
        requiresLiveReady = (-not [bool]$SkipLiveReadyCheck)
        ready = ($liveReady -and $profileCurrent -and $activeCurrent -and $candidateSupported)
        failures = @($preflightFailures.ToArray())
        liveReadiness = $liveReadiness
        profile = $profilePreflight
        candidateProfile = $CandidateProfile
        manualAction = "Preflight only: no TX profile selection, no MOX/TUN/two-tone keying. If ready=true, run the full capture only under manual operator TX control with -AllowTransmit."
    }

    $json = $preflightResult | ConvertTo-Json -Depth 12
    Ensure-ParentDirectory -Path $summaryPath
    Set-Content -LiteralPath $summaryPath -Value $json -Encoding UTF8
    $json

    if (-not [bool]$preflightResult.ready) {
        exit 2
    }

    exit 0
}

if (-not $AllowTransmit) {
    throw "Refusing to select the TX output headroom candidate without -AllowTransmit. Run -PlanOnly first; this script never keys TX, but candidate selection can affect manual keyed mic/voice RF."
}

$liveReadinessBefore = $null
if (-not $SkipLiveReadyCheck) {
    $liveReadinessBefore = Get-LiveReadiness (Invoke-LiveDiagnosticsApi -Base $base)
    if (-not [bool]$liveReadinessBefore.ready) {
        $reason = if ($liveReadinessBefore.failureReasons.Count -gt 0) {
            [string]::Join(", ", @($liveReadinessBefore.failureReasons))
        }
        else {
            "unknown"
        }

        throw "Refusing TX output headroom A/B capture because live diagnostics are not benchmark-ready ($reason). Open the frontend scene, connect the G2, and wait for WDSP/runtime evidence before selecting the TX candidate; use -SkipLiveReadyCheck only for scripted tests."
    }
}

$before = Invoke-HeadroomProfileApi -Base $base -Method GET
$currentReady = $null
$candidateReady = $null
$currentCapture = $null
$candidateCapture = $null
$reset = $null

try {
    Invoke-HeadroomProfileApi -Base $base -Method PUT -Profile "current" | Out-Null
    $currentReady = Wait-HeadroomProfile -Base $base -Profile "current" -ExpectedActiveProfile "current" -ExpectedPureSignalBypass $false
    $currentCapture = Invoke-WatcherCapture -Base $base -Profile "current" -RunRoot $runRoot

    Invoke-HeadroomProfileApi -Base $base -Method PUT -Profile $CandidateProfile | Out-Null
    $candidateReady = Wait-HeadroomProfile `
        -Base $base `
        -Profile $CandidateProfile `
        -ExpectedActiveProfile $candidateExpectedActiveProfile `
        -ExpectedPureSignalBypass ([bool]$ExpectPureSignalBypass)
    $candidateCapture = Invoke-WatcherCapture -Base $base -Profile $CandidateProfile -RunRoot $runRoot
}
finally {
    try {
        Invoke-HeadroomProfileApi -Base $base -Method PUT -Profile "current" | Out-Null
        $reset = Wait-HeadroomProfile -Base $base -Profile "current" -ExpectedActiveProfile "current" -ExpectedPureSignalBypass $false -TimeoutMs 4000
    }
    catch {
        $reset = [ordered]@{
            ready = $false
            error = $_.Exception.Message
        }
    }
}

$result = [ordered]@{
    schemaVersion = 1
    tool = "capture-tx-output-headroom-ab"
    generatedUtc = (Get-Date).ToUniversalTime().ToString("o")
    baseUrl = $base
    outputRoot = $runRoot
    summaryPath = $summaryPath
    allowTransmit = [bool]$AllowTransmit
    noKeyingByScript = $true
    expectPureSignalBypass = [bool]$ExpectPureSignalBypass
    liveReadinessBefore = $liveReadinessBefore
    profileBefore = $before
    currentProfileReady = $currentReady
    candidateProfileReady = $candidateReady
    current = $currentCapture
    candidate = $candidateCapture
    resetToCurrent = $reset
    recommendation = "Use this paired trace as TX output headroom evidence only after confirming manual operator TX conditions, TX meters, PureSignal bypass state, and off-air/on-air audio/spectrum review; no default behavior is changed by this capture."
}

$json = $result | ConvertTo-Json -Depth 12
Ensure-ParentDirectory -Path $summaryPath
Set-Content -LiteralPath $summaryPath -Value $json -Encoding UTF8
$json
