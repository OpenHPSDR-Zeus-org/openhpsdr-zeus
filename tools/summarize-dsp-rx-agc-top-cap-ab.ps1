param(
    [string]$BaselinePath = "",

    [string]$CandidatePath = "",

    [string]$ReportPath = "",

    [string]$MarkdownPath = "",

    [string]$BundleDir = "",

    [string]$BaselineLabel = "current-agc-top-80db",

    [string]$CandidateLabel = "wdsp-rxa-agc-top-cap-50db-candidate",

    [double]$BaselineAgcTopDb = 80.0,

    [double]$CandidateAgcTopDb = 50.0,

    [double]$MinAgcMovementImprovementDb = 1.0,

    [double]$MaxAudioRmsMovementRegressionDb = 0.5,

    [double]$MinAudioPeakHeadroomDb = 1.0,

    [switch]$RequireActiveAudio,

    [int]$MinActiveAudioSamples = 3,

    [switch]$PlanOnly,

    [switch]$NoMarkdown,

    [switch]$FailOnNotReady,

    [switch]$JsonOnly
)

$ErrorActionPreference = "Stop"

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

function Resolve-ExistingFilePath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not [System.IO.File]::Exists((ConvertTo-LongFileSystemPath -Path $fullPath))) {
        throw "Cannot find path '$Path' because it does not exist."
    }

    return $fullPath
}

function Resolve-ExistingDirectoryPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not [System.IO.Directory]::Exists((ConvertTo-LongFileSystemPath -Path $fullPath))) {
        throw "Cannot find directory '$Path' because it does not exist."
    }

    return $fullPath
}

function Read-JsonFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    $resolved = Resolve-ExistingFilePath -Path $Path
    try {
        return [System.IO.File]::ReadAllText((ConvertTo-LongFileSystemPath -Path $resolved)) | ConvertFrom-Json
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

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $parent = Split-Path -Parent $fullPath
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        [System.IO.Directory]::CreateDirectory((ConvertTo-LongFileSystemPath -Path $parent)) | Out-Null
    }

    [System.IO.File]::WriteAllText(
        (ConvertTo-LongFileSystemPath -Path $fullPath),
        ($Value | ConvertTo-Json -Depth 72),
        [System.Text.Encoding]::UTF8)
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

    foreach ($candidate in @($Object.PSObject.Properties)) {
        if ([string]::Equals($candidate.Name, $Name, [StringComparison]::OrdinalIgnoreCase)) {
            return $candidate.Value
        }
    }

    return $null
}

function Get-NumericValue {
    param($Value)

    if ($null -eq $Value -or $Value -is [bool]) {
        return $null
    }
    if ($Value -is [System.Byte] -or
        $Value -is [System.SByte] -or
        $Value -is [System.Int16] -or
        $Value -is [System.UInt16] -or
        $Value -is [System.Int32] -or
        $Value -is [System.UInt32] -or
        $Value -is [System.Int64] -or
        $Value -is [System.UInt64] -or
        $Value -is [System.Single] -or
        $Value -is [System.Double] -or
        $Value -is [System.Decimal]) {
        return [double]$Value
    }

    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) {
        return $null
    }

    $parsed = 0.0
    if ([double]::TryParse(
            $text,
            [System.Globalization.NumberStyles]::Float,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [ref]$parsed)) {
        return $parsed
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
    return $text -in @("true", "1", "yes", "on", "ready")
}

function Get-StatValue {
    param(
        $Object,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Field
    )

    $stat = Get-JsonValue $Object $Name
    if ($null -eq $stat) {
        return $null
    }

    return Get-NumericValue (Get-JsonValue $stat $Field)
}

function ConvertTo-PortablePath {
    param(
        [string]$Root,
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path) -or [string]::IsNullOrWhiteSpace($Root)) {
        return $Path
    }

    try {
        $rootFull = Resolve-ExistingDirectoryPath -Path $Root
        $pathFull = [System.IO.Path]::GetFullPath($Path)
        if (-not $rootFull.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
            $rootFull = $rootFull + [System.IO.Path]::DirectorySeparatorChar
        }

        $relative = $null
        try {
            $relative = [System.IO.Path]::GetRelativePath($rootFull, $pathFull)
        }
        catch {
            $rootUri = [System.Uri]::new($rootFull)
            $pathUri = [System.Uri]::new($pathFull)
            $relative = [System.Uri]::UnescapeDataString($rootUri.MakeRelativeUri($pathUri).ToString())
        }
        if ([string]::IsNullOrWhiteSpace($relative) -or
            $relative -eq ".." -or
            $relative.StartsWith("../", [StringComparison]::Ordinal) -or
            $relative.StartsWith("..\", [StringComparison]::Ordinal) -or
            [System.IO.Path]::IsPathRooted($relative)) {
            return $Path
        }

        return $relative -replace "\\", "/"
    }
    catch {
        return $Path
    }
}

function Add-FailureIf {
    param(
        [System.Collections.Generic.List[string]]$Failures,
        [bool]$Condition,
        [string]$Failure
    )

    if ($Condition) {
        $Failures.Add($Failure) | Out-Null
    }
}

if ($PlanOnly) {
    [ordered]@{
        schemaVersion = 1
        tool = "summarize-dsp-rx-agc-top-cap-ab"
        mode = "plan-only"
        noWritesByScript = $true
        baselineLabel = $BaselineLabel
        candidateLabel = $CandidateLabel
        baselineAgcTopDb = $BaselineAgcTopDb
        candidateAgcTopDb = $CandidateAgcTopDb
        requireActiveAudio = [bool]$RequireActiveAudio
        minActiveAudioSamples = $MinActiveAudioSamples
        disallowedEndpoints = @("/api/agcGain", "/api/rx/agc", "/api/auto-agc", "/api/connect/p2", "/api/vfo", "/api/radio/lo")
        inputArtifacts = @("baseline watch-dsp-live-diagnostics summary JSON", "candidate watch-dsp-live-diagnostics summary JSON")
        commandSteps = @(
            "Capture the baseline with watch-dsp-live-diagnostics.ps1 while the operator's current AGC-T and mode are active.",
            "Only after explicit operator approval, capture the candidate window after the operator manually selects the candidate AGC-T; this scorer performs no radio writes.",
            "Run this scorer with -BaselinePath and -CandidatePath; require active audio for improvement evidence, not just a quiet workflow trace.",
            "Treat readyForOptInReview as advisory until a non-persisted runtime control and cross-radio proof exist."
        )
        safety = @(
            "No HTTP write endpoints are called by this script.",
            "The existing /api/agcGain endpoint is a real persisted operator AGC-T control and is intentionally not used here.",
            "No connect, tune, MOX, TUN, two-tone, transmit, or PureSignal state is changed.",
            "Promotion remains false until cross-radio validation and a safe opt-in runtime path are reviewed."
        )
    } | ConvertTo-Json -Depth 10
    exit 0
}

if ([string]::IsNullOrWhiteSpace($BaselinePath)) {
    throw "Specify -BaselinePath or use -PlanOnly."
}
if ([string]::IsNullOrWhiteSpace($CandidatePath)) {
    throw "Specify -CandidatePath or use -PlanOnly."
}
if ($CandidateAgcTopDb -ge $BaselineAgcTopDb) {
    throw "CandidateAgcTopDb must be lower than BaselineAgcTopDb for an AGC top-cap candidate."
}
if ($MinActiveAudioSamples -lt 1) {
    throw "MinActiveAudioSamples must be at least 1."
}

$bundlePath = ""
if (-not [string]::IsNullOrWhiteSpace($BundleDir)) {
    $bundlePath = Resolve-ExistingDirectoryPath -Path $BundleDir
}

$baselineResolved = Resolve-ExistingFilePath -Path $BaselinePath
$candidateResolved = Resolve-ExistingFilePath -Path $CandidatePath
$baseline = Read-JsonFile -Path $baselineResolved
$candidate = Read-JsonFile -Path $candidateResolved

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $candidateDir = Split-Path -Parent $candidateResolved
    if ([string]::IsNullOrWhiteSpace($candidateDir)) {
        $candidateDir = (Get-Location).Path
    }
    $ReportPath = Join-Path $candidateDir "rx-agc-top-cap-ab-live-comparison.json"
}
if (-not $NoMarkdown -and [string]::IsNullOrWhiteSpace($MarkdownPath)) {
    $reportDir = Split-Path -Parent $ReportPath
    $reportName = [System.IO.Path]::GetFileNameWithoutExtension($ReportPath)
    $MarkdownPath = Join-Path $reportDir "$reportName.md"
}

$compareScript = Join-Path $PSScriptRoot "compare-dsp-live-diagnostics-traces.ps1"
if (-not [System.IO.File]::Exists((ConvertTo-LongFileSystemPath -Path ([System.IO.Path]::GetFullPath($compareScript))))) {
    throw "Missing comparison script: $compareScript"
}

$compareReportPath = Join-Path (Split-Path -Parent ([System.IO.Path]::GetFullPath($ReportPath))) "rx-agc-top-cap-trace-comparison.json"
$compareArgs = @{
    BaselinePath = $baselineResolved
    CandidatePath = $candidateResolved
    BaselineLabel = $BaselineLabel
    CandidateLabel = $CandidateLabel
    ReportPath = $compareReportPath
    ScenarioId = "agc-level-step"
    JsonOnly = $true
    NoMarkdown = $true
}
if (-not [string]::IsNullOrWhiteSpace($bundlePath)) {
    $compareArgs["BundleDir"] = $bundlePath
}

$global:LASTEXITCODE = $null
$comparisonJson = & $compareScript @compareArgs
$compareSucceeded = $?
$compareExitCode = $LASTEXITCODE
if (-not $compareSucceeded -or ($null -ne $compareExitCode -and $compareExitCode -ne 0)) {
    $exitCodeText = if ($null -eq $compareExitCode) { "unknown" } else { [string]$compareExitCode }
    throw "compare-dsp-live-diagnostics-traces failed with exit code $exitCodeText."
}
$comparison = ([string]::Join([Environment]::NewLine, @($comparisonJson))) | ConvertFrom-Json

$baselineAgcMovement = Get-StatValue $baseline "agcGainDb" "movement"
$candidateAgcMovement = Get-StatValue $candidate "agcGainDb" "movement"
$baselineAudioRmsMovement = Get-StatValue $baseline "audioRmsDbfs" "movement"
$candidateAudioRmsMovement = Get-StatValue $candidate "audioRmsDbfs" "movement"
$candidateAudioPeakMax = Get-StatValue $candidate "audioPeakDbfs" "max"

$baselineAgcWatch = Get-JsonValue $baseline "agcStabilityWatch"
$candidateAgcWatch = Get-JsonValue $candidate "agcStabilityWatch"
$baselineActiveAgcMovement = Get-StatValue $baselineAgcWatch "activeAgcGainDb" "movement"
$candidateActiveAgcMovement = Get-StatValue $candidateAgcWatch "activeAgcGainDb" "movement"
$baselineVoiceAgcMovement = Get-StatValue $baselineAgcWatch "voiceLikeAgcGainDb" "movement"
$candidateVoiceAgcMovement = Get-StatValue $candidateAgcWatch "voiceLikeAgcGainDb" "movement"
$candidatePumpingRisk = Test-Truthy (Get-JsonValue $candidateAgcWatch "pumpingRisk")

$baselineOccupancy = Get-JsonValue $baseline "signalOccupancyWatch"
$candidateOccupancy = Get-JsonValue $candidate "signalOccupancyWatch"
$baselineActiveAudioSamples = [int](Get-NumericValue (Get-JsonValue $baselineOccupancy "activeAudioSampleCount"))
$candidateActiveAudioSamples = [int](Get-NumericValue (Get-JsonValue $candidateOccupancy "activeAudioSampleCount"))

$baselineOkSamples = [int](Get-NumericValue (Get-JsonValue $baseline "okSampleCount"))
$candidateOkSamples = [int](Get-NumericValue (Get-JsonValue $candidate "okSampleCount"))
$baselineFailedSamples = [int](Get-NumericValue (Get-JsonValue $baseline "failedSampleCount"))
$candidateFailedSamples = [int](Get-NumericValue (Get-JsonValue $candidate "failedSampleCount"))
$baselineHardBlockers = [int](Get-NumericValue (Get-JsonValue $baseline "hardBlockerSampleCount"))
$candidateHardBlockers = [int](Get-NumericValue (Get-JsonValue $candidate "hardBlockerSampleCount"))
$baselineReadyTrace = Test-Truthy (Get-JsonValue $baseline "readyForBenchmarkTrace")
$candidateReadyTrace = Test-Truthy (Get-JsonValue $candidate "readyForBenchmarkTrace")

$agcImprovement = if ($null -ne $baselineAgcMovement -and $null -ne $candidateAgcMovement) {
    [Math]::Round($baselineAgcMovement - $candidateAgcMovement, 6)
}
else { $null }
$activeAgcImprovement = if ($null -ne $baselineActiveAgcMovement -and $null -ne $candidateActiveAgcMovement) {
    [Math]::Round($baselineActiveAgcMovement - $candidateActiveAgcMovement, 6)
}
else { $null }
$voiceAgcImprovement = if ($null -ne $baselineVoiceAgcMovement -and $null -ne $candidateVoiceAgcMovement) {
    [Math]::Round($baselineVoiceAgcMovement - $candidateVoiceAgcMovement, 6)
}
else { $null }
$audioRmsImprovement = if ($null -ne $baselineAudioRmsMovement -and $null -ne $candidateAudioRmsMovement) {
    [Math]::Round($baselineAudioRmsMovement - $candidateAudioRmsMovement, 6)
}
else { $null }

$activeAudioReady = (-not [bool]$RequireActiveAudio) -or
    ($baselineActiveAudioSamples -ge $MinActiveAudioSamples -and $candidateActiveAudioSamples -ge $MinActiveAudioSamples)
$traceReady = $baselineReadyTrace -and $candidateReadyTrace -and
    $baselineOkSamples -gt 0 -and $candidateOkSamples -gt 0 -and
    $baselineFailedSamples -eq 0 -and $candidateFailedSamples -eq 0 -and
    $baselineHardBlockers -eq 0 -and $candidateHardBlockers -eq 0
$agcImprovementReady = $null -ne $agcImprovement -and $agcImprovement -ge $MinAgcMovementImprovementDb
$activeAgcNotWorse = $null -ne $activeAgcImprovement -and $activeAgcImprovement -ge -0.000001
$voiceAgcNotWorse = $null -eq $voiceAgcImprovement -or $voiceAgcImprovement -ge -0.000001
$audioRmsNotWorse = $null -ne $audioRmsImprovement -and $audioRmsImprovement -ge (-1.0 * $MaxAudioRmsMovementRegressionDb)
$noClipping = $null -ne $candidateAudioPeakMax -and $candidateAudioPeakMax -le (-1.0 * $MinAudioPeakHeadroomDb)
$runtimeSafe = $true

$failures = [System.Collections.Generic.List[string]]::new()
Add-FailureIf $failures (-not $traceReady) "trace-not-ready"
Add-FailureIf $failures (-not $activeAudioReady) "active-audio-missing"
Add-FailureIf $failures (-not $agcImprovementReady) "agc-movement-not-improved"
Add-FailureIf $failures (-not $activeAgcNotWorse) "active-agc-movement-regression"
Add-FailureIf $failures (-not $voiceAgcNotWorse) "voice-like-agc-movement-regression"
Add-FailureIf $failures (-not $audioRmsNotWorse) "audio-rms-movement-regression"
Add-FailureIf $failures (-not $noClipping) "audio-peak-headroom-missing"
Add-FailureIf $failures $candidatePumpingRisk "candidate-pumping-risk"

$readyForOptInReview = $traceReady -and $activeAudioReady -and $agcImprovementReady -and
    $activeAgcNotWorse -and $voiceAgcNotWorse -and $audioRmsNotWorse -and $noClipping -and
    (-not $candidatePumpingRisk) -and $runtimeSafe
$promotionReady = $false
$evidenceStatus = if ($readyForOptInReview) {
    "ready-for-opt-in-review"
}
elseif (-not $traceReady) {
    "trace-not-ready"
}
elseif (-not $activeAudioReady) {
    "active-audio-missing"
}
elseif (-not $agcImprovementReady) {
    "agc-movement-not-improved"
}
elseif ($candidatePumpingRisk) {
    "candidate-pumping-risk"
}
else {
    "not-ready"
}

$report = [ordered]@{
    schemaVersion = 1
    tool = "summarize-dsp-rx-agc-top-cap-ab"
    generatedUtc = [DateTimeOffset]::UtcNow
    noWritesByScript = $true
    disallowedEndpoints = @("/api/agcGain", "/api/rx/agc", "/api/auto-agc", "/api/connect/p2", "/api/vfo", "/api/radio/lo")
    baselineLabel = $BaselineLabel
    candidateLabel = $CandidateLabel
    baselineAgcTopDb = $BaselineAgcTopDb
    candidateAgcTopDb = $CandidateAgcTopDb
    defaultBehaviorChanged = $false
    requiresRuntimeOptIn = $true
    runtimeApiAvailable = $false
    manualOperatorCapture = $true
    readyForOptInReview = $readyForOptInReview
    promotionReady = $promotionReady
    promotionStatus = "requires-safe-runtime-api-and-cross-radio-proof"
    evidenceStatus = $evidenceStatus
    failures = @($failures.ToArray())
    baselinePath = ConvertTo-PortablePath -Root $bundlePath -Path $baselineResolved
    candidatePath = ConvertTo-PortablePath -Root $bundlePath -Path $candidateResolved
    reportPath = ConvertTo-PortablePath -Root $bundlePath -Path ([System.IO.Path]::GetFullPath($ReportPath))
    traceComparisonPath = ConvertTo-PortablePath -Root $bundlePath -Path ([System.IO.Path]::GetFullPath($compareReportPath))
    requireActiveAudio = [bool]$RequireActiveAudio
    minActiveAudioSamples = $MinActiveAudioSamples
    activeAudioReady = $activeAudioReady
    baselineActiveAudioSampleCount = $baselineActiveAudioSamples
    candidateActiveAudioSampleCount = $candidateActiveAudioSamples
    traceReady = $traceReady
    baselineOkSampleCount = $baselineOkSamples
    candidateOkSampleCount = $candidateOkSamples
    baselineAgcMovementDb = $baselineAgcMovement
    candidateAgcMovementDb = $candidateAgcMovement
    agcMovementImprovementDb = $agcImprovement
    minAgcMovementImprovementDb = $MinAgcMovementImprovementDb
    baselineActiveAgcMovementDb = $baselineActiveAgcMovement
    candidateActiveAgcMovementDb = $candidateActiveAgcMovement
    activeAgcMovementImprovementDb = $activeAgcImprovement
    baselineVoiceLikeAgcMovementDb = $baselineVoiceAgcMovement
    candidateVoiceLikeAgcMovementDb = $candidateVoiceAgcMovement
    voiceLikeAgcMovementImprovementDb = $voiceAgcImprovement
    baselineAudioRmsMovementDb = $baselineAudioRmsMovement
    candidateAudioRmsMovementDb = $candidateAudioRmsMovement
    audioRmsMovementImprovementDb = $audioRmsImprovement
    maxAudioRmsMovementRegressionDb = $MaxAudioRmsMovementRegressionDb
    candidateAudioPeakMaxDbfs = $candidateAudioPeakMax
    minAudioPeakHeadroomDb = $MinAudioPeakHeadroomDb
    candidatePumpingRisk = $candidatePumpingRisk
    traceComparison = $comparison
    recommendation = if ($readyForOptInReview) {
        "Store this as AGC top-cap opt-in review evidence only; promotion still requires a safe non-persisted runtime path plus G2 and cross-radio validation."
    }
    else {
        "Do not promote the AGC top-cap candidate; collect active live traces with lower AGC movement, no audio movement regression, no clipping, and no pumping risk."
    }
}

Write-JsonFile -Path $ReportPath -Value $report

if (-not $NoMarkdown) {
    $markdown = @(
        "# RX AGC Top-Cap A/B Evidence",
        "",
        "- Status: $evidenceStatus",
        "- Ready for opt-in review: $readyForOptInReview",
        "- Promotion ready: $promotionReady",
        "- AGC movement: $baselineAgcMovement -> $candidateAgcMovement dB",
        "- Audio RMS movement: $baselineAudioRmsMovement -> $candidateAudioRmsMovement dB",
        "",
        $report.recommendation
    )
    $markdownFullPath = [System.IO.Path]::GetFullPath($MarkdownPath)
    $markdownParent = Split-Path -Parent $markdownFullPath
    if (-not [string]::IsNullOrWhiteSpace($markdownParent)) {
        [System.IO.Directory]::CreateDirectory((ConvertTo-LongFileSystemPath -Path $markdownParent)) | Out-Null
    }
    [System.IO.File]::WriteAllText(
        (ConvertTo-LongFileSystemPath -Path $markdownFullPath),
        ([string]::Join([Environment]::NewLine, $markdown)),
        [System.Text.Encoding]::UTF8)
}

$json = $report | ConvertTo-Json -Depth 72
$json

if ($FailOnNotReady -and -not $readyForOptInReview) {
    exit 2
}
