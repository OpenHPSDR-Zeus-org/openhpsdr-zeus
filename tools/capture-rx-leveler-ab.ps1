param(
    [string]$BaseUrl = "https://localhost:6443",

    [int]$Samples = 60,

    [int]$IntervalMs = 500,

    [int]$TimeoutSec = 5,

    [string]$OutputRoot = "",

    [string]$LabelPrefix = "g2-rx-leveler-ab",

    [string]$CandidateProfile = "stable-speech-candidate",

    [string]$WatchScriptPath = "",

    [switch]$SkipCertificateCheck,

    [switch]$ContinueOnError,

    [switch]$PlanOnly,

    [switch]$RequireActiveAudio,

    [int]$MinActiveAudioSamples = 1,

    [switch]$RequirePassbandEvidence,

    [int]$MinPassbandPeakSamples = 1,

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

function ConvertTo-LongFileSystemPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    if ([System.IO.Path]::DirectorySeparatorChar -ne "\") {
        return $Path
    }
    if ($Path.StartsWith("\\?\", [StringComparison]::Ordinal)) {
        return $Path
    }
    if ($Path.StartsWith("\\", [StringComparison]::Ordinal)) {
        return "\\?\UNC\" + $Path.Substring(2)
    }
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return "\\?\" + $Path
    }

    return $Path
}

function Test-FileExists {
    param([Parameter(Mandatory = $true)][string]$Path)
    return [System.IO.File]::Exists((ConvertTo-LongFileSystemPath -Path $Path))
}

function Read-TextFile {
    param([Parameter(Mandatory = $true)][string]$Path)
    return [System.IO.File]::ReadAllText((ConvertTo-LongFileSystemPath -Path $Path))
}

function Write-TextFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Value
    )

    $parent = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        [System.IO.Directory]::CreateDirectory((ConvertTo-LongFileSystemPath -Path $parent)) | Out-Null
    }

    [System.IO.File]::WriteAllText(
        (ConvertTo-LongFileSystemPath -Path $Path),
        $Value,
        [System.Text.Encoding]::UTF8)
}

function Get-Timestamp {
    return (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmssZ")
}

function New-Directory {
    param([Parameter(Mandatory = $true)][string]$Path)
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    [System.IO.Directory]::CreateDirectory((ConvertTo-LongFileSystemPath -Path $fullPath)) | Out-Null
    return $fullPath
}

function Invoke-LevelerProfileApi {
    param(
        [Parameter(Mandatory = $true)][string]$Base,
        [Parameter(Mandatory = $true)][ValidateSet("GET", "PUT")][string]$Method,
        [string]$Profile = ""
    )

    $requestArgs = @{
        Uri = "$Base/api/dsp/rx-audio-leveler-profile"
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
        throw "RX audio leveler profile API is not reachable at $($requestArgs["Uri"]). Start the updated WDSP v2 backend before running A/B capture. $($_.Exception.Message)"
    }
}

function Wait-LevelerProfile {
    param(
        [Parameter(Mandatory = $true)][string]$Base,
        [Parameter(Mandatory = $true)][string]$Profile,
        [int]$TimeoutMs = 8000
    )

    $deadline = [DateTimeOffset]::UtcNow.AddMilliseconds($TimeoutMs)
    $last = $null
    do {
        $last = Invoke-LevelerProfileApi -Base $Base -Method GET
        $profileAligned = [string]::Equals([string]$last.profile, $Profile, [StringComparison]::OrdinalIgnoreCase)
        $activeProfileAligned = [string]::Equals([string]$last.activeProfile, $Profile, [StringComparison]::OrdinalIgnoreCase)
        if ($profileAligned -and $activeProfileAligned) {
            return [ordered]@{
                ready = $true
                profile = [string]$last.profile
                activeProfile = [string]$last.activeProfile
                profileAligned = $profileAligned
                activeProfileAligned = $activeProfileAligned
            }
        }

        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    $profileAligned = [string]::Equals([string]$last.profile, $Profile, [StringComparison]::OrdinalIgnoreCase)
    $activeProfileAligned = [string]::Equals([string]$last.activeProfile, $Profile, [StringComparison]::OrdinalIgnoreCase)
    return [ordered]@{
        ready = $false
        profile = [string]$last.profile
        activeProfile = [string]$last.activeProfile
        profileAligned = $profileAligned
        activeProfileAligned = $activeProfileAligned
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
        throw "watch-dsp-live-diagnostics failed for profile '$Profile' with exit code $exitCodeText"
    }

    $missingPaths = @()
    if (-not (Test-FileExists -Path $jsonlPath)) {
        $missingPaths += $jsonlPath
    }
    if (-not (Test-FileExists -Path $reportPath)) {
        $missingPaths += $reportPath
    }
    if ($missingPaths.Count -gt 0) {
        throw "watch-dsp-live-diagnostics did not write required outputs for profile '$Profile': $($missingPaths -join '; ')"
    }

    $summary = Read-TextFile -Path $reportPath | ConvertFrom-Json
    $activeAudioSampleCount = 0
    if ($null -ne $summary.signalOccupancyWatch -and $null -ne $summary.signalOccupancyWatch.activeAudioSampleCount) {
        $activeAudioSampleCount = [int]$summary.signalOccupancyWatch.activeAudioSampleCount
    }
    $passbandAudioWatch = $summary.passbandAudioWatch
    $passbandPeakSampleCount = 0
    $filterPassbandPeakSampleCount = 0
    $nearPassbandPeakSampleCount = 0
    $passbandAudioStatus = ""
    if ($null -ne $passbandAudioWatch) {
        $passbandAudioStatus = [string]$passbandAudioWatch.status
        if ($null -ne $passbandAudioWatch.passbandPeakSampleCount) {
            $passbandPeakSampleCount = [int]$passbandAudioWatch.passbandPeakSampleCount
        }
        if ($null -ne $passbandAudioWatch.filterPassbandPeakSampleCount) {
            $filterPassbandPeakSampleCount = [int]$passbandAudioWatch.filterPassbandPeakSampleCount
        }
        if ($null -ne $passbandAudioWatch.legacyNearPassbandPeakSampleCount) {
            $nearPassbandPeakSampleCount = [int]$passbandAudioWatch.legacyNearPassbandPeakSampleCount
        }
    }
    $profileCounts = @()
    $experimentalSampleCount = 0
    $controlRmsValidSampleCount = 0
    if ($null -ne $summary.rxAudioLevelerWatch) {
        if ($null -ne $summary.rxAudioLevelerWatch.profileCounts) {
            $profileCounts = @($summary.rxAudioLevelerWatch.profileCounts)
        }
        if ($null -ne $summary.rxAudioLevelerWatch.experimentalSampleCount) {
            $experimentalSampleCount = [int]$summary.rxAudioLevelerWatch.experimentalSampleCount
        }
        if ($null -ne $summary.rxAudioLevelerWatch.controlRmsValidSampleCount) {
            $controlRmsValidSampleCount = [int]$summary.rxAudioLevelerWatch.controlRmsValidSampleCount
        }
    }

    return [ordered]@{
        profile = $Profile
        jsonlPath = $jsonlPath
        reportPath = $reportPath
        trendStatus = [string]$summary.trendStatus
        readyForBenchmarkTrace = [bool]$summary.readyForBenchmarkTrace
        okSampleCount = [int]$summary.okSampleCount
        activeAudioSampleCount = $activeAudioSampleCount
        passbandAudioStatus = $passbandAudioStatus
        passbandPeakSampleCount = $passbandPeakSampleCount
        filterPassbandPeakSampleCount = $filterPassbandPeakSampleCount
        nearPassbandPeakSampleCount = $nearPassbandPeakSampleCount
        passbandAudioWatch = $passbandAudioWatch
        profileCounts = @($profileCounts)
        experimentalSampleCount = $experimentalSampleCount
        controlRmsValidSampleCount = $controlRmsValidSampleCount
    }
}

function Get-ActiveAudioSampleCount {
    param($Capture)

    if ($null -eq $Capture) {
        return 0
    }

    if ($Capture -is [System.Collections.IDictionary] -and
        $Capture.Contains("activeAudioSampleCount") -and
        $null -ne $Capture["activeAudioSampleCount"]) {
        return [int]$Capture["activeAudioSampleCount"]
    }

    $property = $Capture.PSObject.Properties["activeAudioSampleCount"]
    if ($null -eq $property -or $null -eq $property.Value) {
        return 0
    }

    return [int]$property.Value
}

function Get-PassbandPeakSampleCount {
    param($Capture)

    if ($null -eq $Capture) {
        return 0
    }

    if ($Capture -is [System.Collections.IDictionary] -and
        $Capture.Contains("passbandPeakSampleCount") -and
        $null -ne $Capture["passbandPeakSampleCount"]) {
        return [int]$Capture["passbandPeakSampleCount"]
    }

    $property = $Capture.PSObject.Properties["passbandPeakSampleCount"]
    if ($null -eq $property -or $null -eq $property.Value) {
        return 0
    }

    return [int]$property.Value
}

function New-ActiveAudioEvidence {
    param(
        $CurrentCapture,
        $CandidateCapture,
        [bool]$Required,
        [int]$MinimumSamples
    )

    $currentCount = Get-ActiveAudioSampleCount -Capture $CurrentCapture
    $candidateCount = Get-ActiveAudioSampleCount -Capture $CandidateCapture
    $missingProfiles = @()
    if ($currentCount -lt $MinimumSamples) {
        $missingProfiles += "current"
    }
    if ($candidateCount -lt $MinimumSamples) {
        $missingProfiles += $CandidateProfile
    }

    [ordered]@{
        required = $Required
        minActiveAudioSamples = $MinimumSamples
        ready = (-not $Required) -or $missingProfiles.Count -eq 0
        currentActiveAudioSampleCount = $currentCount
        candidateActiveAudioSampleCount = $candidateCount
        missingProfiles = @($missingProfiles)
        recommendation = if ($Required) {
            "Tune to an active signal and rerun capture; silent traces are workflow proof only, not RX leveler improvement evidence."
        }
        else {
            "Use -RequireActiveAudio when collecting promotion evidence from an active RX signal."
        }
    }
}

function New-PassbandEvidence {
    param(
        $CurrentCapture,
        $CandidateCapture,
        [bool]$Required,
        [int]$MinimumSamples
    )

    $currentCount = Get-PassbandPeakSampleCount -Capture $CurrentCapture
    $candidateCount = Get-PassbandPeakSampleCount -Capture $CandidateCapture
    $missingProfiles = @()
    if ($currentCount -lt $MinimumSamples) {
        $missingProfiles += "current"
    }
    if ($candidateCount -lt $MinimumSamples) {
        $missingProfiles += $CandidateProfile
    }

    [ordered]@{
        required = $Required
        minPassbandPeakSamples = $MinimumSamples
        ready = (-not $Required) -or $missingProfiles.Count -eq 0
        currentPassbandPeakSampleCount = $currentCount
        candidatePassbandPeakSampleCount = $candidateCount
        missingProfiles = @($missingProfiles)
        recommendation = if ($Required) {
            "Tune so frontend peaks land inside the RX filter passband before collecting promotion-grade RX leveler A/B evidence."
        }
        else {
            "Use -RequirePassbandEvidence when collecting promotion-grade RX leveler evidence from a tuned signal."
        }
    }
}

if ($MinActiveAudioSamples -lt 1) {
    throw "MinActiveAudioSamples must be at least 1."
}
if ($MinPassbandPeakSamples -lt 1) {
    throw "MinPassbandPeakSamples must be at least 1."
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

$plannedRunRoot = Join-Path $OutputRoot ("rx-leveler-ab-" + (Get-Timestamp))
$runRoot = if ($PlanOnly) { $plannedRunRoot } else { New-Directory $plannedRunRoot }
$summaryPath = Join-Path $runRoot "rx-leveler-ab-summary.json"
$plan = [ordered]@{
    schemaVersion = 1
    tool = "capture-rx-leveler-ab"
    baseUrl = $base
    samples = $Samples
    intervalMs = $IntervalMs
    currentProfile = "current"
    candidateProfile = $CandidateProfile
    outputRoot = $runRoot
    summaryPath = $summaryPath
    watcher = $script:ResolvedWatcherPath
    requireActiveAudio = [bool]$RequireActiveAudio
    minActiveAudioSamples = $MinActiveAudioSamples
    requirePassbandEvidence = [bool]$RequirePassbandEvidence
    minPassbandPeakSamples = $MinPassbandPeakSamples
    tuneStepHz = $TuneStepHz
}

if ($PlanOnly) {
    $plan | ConvertTo-Json -Depth 8
    exit 0
}

$before = Invoke-LevelerProfileApi -Base $base -Method GET
$currentReady = $null
$candidateReady = $null
$currentCapture = $null
$candidateCapture = $null
$reset = $null
$errorText = $null

try {
    Invoke-LevelerProfileApi -Base $base -Method PUT -Profile "current" | Out-Null
    $currentReady = Wait-LevelerProfile -Base $base -Profile "current"
    $currentCapture = Invoke-WatcherCapture -Base $base -Profile "current" -RunRoot $runRoot

    Invoke-LevelerProfileApi -Base $base -Method PUT -Profile $CandidateProfile | Out-Null
    $candidateReady = Wait-LevelerProfile -Base $base -Profile $CandidateProfile
    $candidateCapture = Invoke-WatcherCapture -Base $base -Profile $CandidateProfile -RunRoot $runRoot
}
catch {
    $errorText = $_.Exception.Message
}
finally {
    try {
        Invoke-LevelerProfileApi -Base $base -Method PUT -Profile "current" | Out-Null
        $reset = Wait-LevelerProfile -Base $base -Profile "current" -TimeoutMs 4000
    }
    catch {
        $reset = [ordered]@{
            ready = $false
            error = $_.Exception.Message
        }
    }
}

$resetAligned = $false
if ($null -ne $reset) {
    $resetAligned =
        [bool]$reset.ready -and
        [bool]$reset.profileAligned -and
        [bool]$reset.activeProfileAligned -and
        [string]$reset.profile -eq "current" -and
        [string]$reset.activeProfile -eq "current"
}

$activeAudioEvidence = New-ActiveAudioEvidence `
    -CurrentCapture $currentCapture `
    -CandidateCapture $candidateCapture `
    -Required ([bool]$RequireActiveAudio) `
    -MinimumSamples $MinActiveAudioSamples
$passbandEvidence = New-PassbandEvidence `
    -CurrentCapture $currentCapture `
    -CandidateCapture $candidateCapture `
    -Required ([bool]$RequirePassbandEvidence) `
    -MinimumSamples $MinPassbandPeakSamples

if ($RequireActiveAudio -and -not [bool]$activeAudioEvidence.ready -and [string]::IsNullOrWhiteSpace($errorText)) {
    $missingProfileText = @($activeAudioEvidence.missingProfiles) -join ", "
    $errorText = "Active RX audio evidence is required but missing for profile(s): $missingProfileText. Minimum required active-audio samples per profile: $MinActiveAudioSamples."
}
if ($RequirePassbandEvidence -and -not [bool]$passbandEvidence.ready -and [string]::IsNullOrWhiteSpace($errorText)) {
    $missingProfileText = @($passbandEvidence.missingProfiles) -join ", "
    $errorText = "Tuned passband evidence is required but missing for profile(s): $missingProfileText. Minimum required passband peak samples per profile: $MinPassbandPeakSamples."
}

$ok = [string]::IsNullOrWhiteSpace($errorText) -and
    $null -ne $currentCapture -and
    $null -ne $candidateCapture -and
    $resetAligned -and
    [bool]$activeAudioEvidence.ready -and
    [bool]$passbandEvidence.ready

$result = [ordered]@{
    schemaVersion = 1
    tool = "capture-rx-leveler-ab"
    generatedUtc = (Get-Date).ToUniversalTime().ToString("o")
    ok = $ok
    error = $errorText
    baseUrl = $base
    outputRoot = $runRoot
    summaryPath = $summaryPath
    profileBefore = $before
    requireActiveAudio = [bool]$RequireActiveAudio
    minActiveAudioSamples = $MinActiveAudioSamples
    activeAudioEvidence = $activeAudioEvidence
    requirePassbandEvidence = [bool]$RequirePassbandEvidence
    minPassbandPeakSamples = $MinPassbandPeakSamples
    passbandEvidence = $passbandEvidence
    currentProfileReady = $currentReady
    candidateProfileReady = $candidateReady
    current = $currentCapture
    candidate = $candidateCapture
    resetToCurrent = $reset
    recommendation = "Use the paired JSON summaries plus audio/render evidence before promoting RX leveler changes; no default behavior is changed by this capture."
}

$json = $result | ConvertTo-Json -Depth 12
Write-TextFile -Path $summaryPath -Value $json
$json

if (-not $ok -and -not $ContinueOnError) {
    exit 1
}
