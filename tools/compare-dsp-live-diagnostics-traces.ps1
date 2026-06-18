param(
    [Parameter(Mandatory = $true)]
    [string]$BaselinePath,

    [Parameter(Mandatory = $true)]
    [string]$CandidatePath,

    [string]$BaselineLabel = "current-zeus",

    [string]$CandidateLabel = "candidate-under-test",

    [string]$ReportPath = "",

    [string]$MarkdownPath = "",

    [string]$BundleDir = "",

    [double]$Tolerance = 0.000001,

    [switch]$FailOnRegression,

    [switch]$NoMarkdown,

    [switch]$JsonOnly
)

$ErrorActionPreference = "Stop"

function Get-RepoRoot {
    return (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
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

    $json = $Value | ConvertTo-Json -Depth 48
    Set-Content -LiteralPath $Path -Value $json -Encoding UTF8
}

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (Get-Command Get-FileHash -ErrorAction SilentlyContinue) {
        return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    }

    $resolvedPath = (Resolve-Path -LiteralPath $Path).ProviderPath
    $stream = [System.IO.File]::OpenRead($resolvedPath)
    try {
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            $hash = $sha256.ComputeHash($stream)
            return ([System.BitConverter]::ToString($hash) -replace "-", "").ToLowerInvariant()
        }
        finally {
            $sha256.Dispose()
        }
    }
    finally {
        $stream.Dispose()
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

    foreach ($candidate in @($Object.PSObject.Properties)) {
        if ([string]::Equals($candidate.Name, $Name, [StringComparison]::OrdinalIgnoreCase)) {
            return $candidate.Value
        }
    }

    return $null
}

function Get-JsonArray {
    param(
        $Object,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $value = Get-JsonValue $Object $Name
    if ($null -eq $value) {
        return @()
    }

    if ($value -is [System.Array]) {
        return @($value)
    }

    return @($value)
}

function Test-Truthy {
    param($Value)

    if ($null -eq $Value) {
        return $false
    }

    if ($Value -is [bool]) {
        return $Value
    }

    return [bool]$Value
}

function ConvertTo-PortablePath {
    param(
        [string]$Root,
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $Path
    }
    if ([string]::IsNullOrWhiteSpace($Root)) {
        return $Path
    }

    try {
        $rootFull = [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Root).Path)
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

function Get-NumericValue {
    param($Value)

    if ($null -eq $Value) {
        return $null
    }

    if ($Value -is [byte] -or $Value -is [int] -or $Value -is [long] -or
        $Value -is [float] -or $Value -is [double] -or $Value -is [decimal]) {
        return [double]$Value
    }

    $parsed = 0.0
    $style = [System.Globalization.NumberStyles]::Float -bor [System.Globalization.NumberStyles]::AllowThousands
    if ([double]::TryParse([string]$Value, $style, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$parsed)) {
        return $parsed
    }

    return $null
}

function Get-StatValue {
    param(
        $Report,
        [Parameter(Mandatory = $true)][string]$Group,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $stats = Get-JsonValue $Report $Group
    if ($null -eq $stats) {
        return $null
    }

    return Get-NumericValue (Get-JsonValue $stats $Name)
}

function Get-Nr5WeakSignalValue {
    param(
        $Report,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $watch = Get-JsonValue $Report "nr5WeakSignalWatch"
    if ($null -eq $watch) {
        return 0.0
    }

    $value = Get-NumericValue (Get-JsonValue $watch $Name)
    if ($null -eq $value) {
        return 0.0
    }

    return $value
}

function Get-Nr5WeakRecoveryPct {
    param($Report)

    $weakInputs = Get-Nr5WeakSignalValue $Report "weakInputSampleCount"
    $weakRecovered = Get-Nr5WeakSignalValue $Report "weakRecoveredSampleCount"
    if ($weakInputs -le 0.0) {
        return 100.0
    }

    return [Math]::Round(100.0 * $weakRecovered / $weakInputs, 3)
}

function Test-CandidateWeakDropoutsRecoveredAudibly {
    param($CandidateReport)

    $strictDropouts = Get-Nr5WeakSignalValue $CandidateReport "weakDropoutSampleCount"
    $candidateLoss = Get-Nr5WeakSignalValue $CandidateReport "weakDropoutCandidateLossSampleCount"
    $finalAudiblePct = Get-Nr5WeakSignalValue $CandidateReport "weakDropoutFinalAudiblePct"
    if ($strictDropouts -le 0.0) {
        return $false
    }

    return ([double]$candidateLoss -le 0.0 -and
        [double]$finalAudiblePct -ge 90.0)
}

function Get-Nr5LowEvidenceLiftValue {
    param(
        $Report,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $nr5Samples = Get-NumericValue (Get-JsonValue $Report "nr5SampleCount")
    if ($null -eq $nr5Samples -or $nr5Samples -le 0.0) {
        return $null
    }

    $watch = Get-JsonValue $Report "nr5LowEvidenceLiftWatch"
    if ($null -eq $watch) {
        return $null
    }

    return Get-NumericValue (Get-JsonValue $watch $Name)
}

function Get-Nr5SampleCount {
    param($Report)

    $value = Get-NumericValue (Get-JsonValue $Report "nr5SampleCount")
    if ($null -eq $value) {
        return 0.0
    }

    return $value
}

function Get-Nr5ArtifactWatchValue {
    param(
        $Report,
        [Parameter(Mandatory = $true)][string]$WatchName,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $nr5Samples = Get-Nr5SampleCount $Report
    if ($nr5Samples -le 0.0) {
        return 0.0
    }

    $watch = Get-JsonValue $Report $WatchName
    if ($null -eq $watch) {
        return $null
    }

    return Get-NumericValue (Get-JsonValue $watch $Name)
}

function Get-Nr5LowEvidenceLiftComparisonValue {
    param(
        $Report,
        [Parameter(Mandatory = $true)][string]$Name
    )

    return Get-Nr5ArtifactWatchValue -Report $Report -WatchName "nr5LowEvidenceLiftWatch" -Name $Name
}

function Get-Nr5AudioAlignmentMismatchPct {
    param($Report)

    return Get-Nr5ArtifactWatchValue -Report $Report -WatchName "nr5AudioAlignmentWatch" -Name "mismatchPct"
}

function Get-Nr5ArtifactRiskScore {
    param($Report)

    $nr5Samples = Get-Nr5SampleCount $Report
    if ($nr5Samples -le 0.0) {
        return 0.0
    }

    $liftedSamples = Get-Nr5LowEvidenceLiftComparisonValue $Report "liftedSampleCount"
    $liftedPct = Get-Nr5LowEvidenceLiftComparisonValue $Report "liftedPct"
    $audioAlignmentMismatchPct = Get-Nr5AudioAlignmentMismatchPct $Report
    $textureFillAverage = Get-StatValue $Report "nr5TextureFill" "average"
    $signalProbabilityAverage = Get-StatValue $Report "nr5SignalProbability" "average"

    $sourceValueCount = @(
        $liftedSamples,
        $liftedPct,
        $audioAlignmentMismatchPct,
        $textureFillAverage,
        $signalProbabilityAverage
    ) | Where-Object { $null -ne $_ } | Measure-Object | Select-Object -ExpandProperty Count
    if ($sourceValueCount -le 0) {
        return $null
    }

    $score = 0.0
    if ($null -ne $liftedSamples -and [double]$liftedSamples -gt 0.0) {
        $score += 1.0
    }
    if ($null -ne $liftedPct -and [double]$liftedPct -gt 5.0) {
        $score += 1.0
    }
    if ($null -ne $audioAlignmentMismatchPct -and [double]$audioAlignmentMismatchPct -gt 10.0) {
        $score += 1.0
    }
    if ($null -ne $textureFillAverage -and
        [double]$textureFillAverage -gt 0.65 -and
        ($null -eq $signalProbabilityAverage -or [double]$signalProbabilityAverage -lt 0.30)) {
        $score += 1.0
    }

    return $score
}

function Get-Nr5ArtifactRiskStatus {
    param($Report)

    $nr5Samples = Get-Nr5SampleCount $Report
    if ($nr5Samples -le 0.0) {
        return "no-nr5-samples"
    }

    $score = Get-Nr5ArtifactRiskScore $Report
    if ($null -eq $score) {
        return "artifact-fields-missing"
    }
    if ([double]$score -gt 0.0) {
        return "artifact-review"
    }

    return "artifact-clear"
}

function Get-RxAudioLevelerWatchValue {
    param(
        $Report,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $watch = Get-JsonValue $Report "rxAudioLevelerWatch"
    if ($null -ne $watch) {
        $value = Get-NumericValue (Get-JsonValue $watch $Name)
        if ($null -ne $value) {
            return $value
        }
    }

    switch ($Name) {
        "boostSlewLimitedSampleCount" { return Get-NumericValue (Get-JsonValue $Report "rxAudioLevelerBoostSlewLimitedSampleCount") }
        "peakLimitedSampleCount" { return Get-NumericValue (Get-JsonValue $Report "rxAudioLevelerPeakLimitedSampleCount") }
        "outputLimitedSampleCount" { return Get-NumericValue (Get-JsonValue $Report "rxAudioLevelerOutputLimitedSampleCount") }
        "diagnosticSampleCount" { return Get-NumericValue (Get-JsonValue $Report "rxAudioLevelerDiagnosticSampleCount") }
        "constrainedSampleCount" {
            $boost = Get-RxAudioLevelerWatchValue $Report "boostSlewLimitedSampleCount"
            $peak = Get-RxAudioLevelerWatchValue $Report "peakLimitedSampleCount"
            $output = Get-RxAudioLevelerWatchValue $Report "outputLimitedSampleCount"
            if ($null -eq $boost -and $null -eq $peak -and $null -eq $output) {
                return $null
            }

            $boostCount = if ($null -eq $boost) { 0.0 } else { [Math]::Max(0.0, [double]$boost) }
            $peakCount = if ($null -eq $peak) { 0.0 } else { [Math]::Max(0.0, [double]$peak) }
            $outputCount = if ($null -eq $output) { 0.0 } else { [Math]::Max(0.0, [double]$output) }
            return [double]($boostCount + $peakCount + $outputCount)
        }
        "constrainedPct" {
            $count = Get-RxAudioLevelerWatchValue $Report "constrainedSampleCount"
            $diagnostics = Get-RxAudioLevelerWatchValue $Report "diagnosticSampleCount"
            return Get-Percent $count $diagnostics
        }
        default { return $null }
    }
}

function Get-RxAudioLevelerWatchSamples {
    param(
        $Report,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $watch = Get-JsonValue $Report "rxAudioLevelerWatch"
    if ($null -eq $watch) {
        return @()
    }

    return @(Get-JsonArray $watch $Name)
}

function Get-AgcStabilityWatchValue {
    param(
        $Report,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $watch = Get-JsonValue $Report "agcStabilityWatch"
    if ($null -eq $watch) {
        return $null
    }

    switch ($Name) {
        "pumpingRisk" {
            $value = Get-JsonValue $watch "pumpingRisk"
            if ($null -eq $value) {
                return $null
            }

            if (Test-Truthy $value) {
                return 1.0
            }

            return 0.0
        }
        "activeAgcMovementDb" { return Get-StatValue $watch "activeAgcGainDb" "movement" }
        "voiceLikeAgcMovementDb" { return Get-StatValue $watch "voiceLikeAgcGainDb" "movement" }
        "quietNoEvidenceAgcMovementDb" { return Get-StatValue $watch "quietNoEvidenceAgcGainDb" "movement" }
        "levelerConstrainedAgcMovementDb" { return Get-StatValue $watch "levelerConstrainedAgcGainDb" "movement" }
        default { return Get-NumericValue (Get-JsonValue $watch $Name) }
    }
}

function Get-PassbandAudioWatchValue {
    param(
        $Report,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $watch = Get-JsonValue $Report "passbandAudioWatch"
    if ($null -eq $watch) {
        return $null
    }

    switch ($Name) {
        "passbandAudioAverageDbfs" { return Get-StatValue $watch "passbandAudioRmsDbfs" "average" }
        "passbandAudioMovementDb" { return Get-StatValue $watch "passbandAudioRmsDbfs" "movement" }
        "offPassbandAudioAverageDbfs" { return Get-StatValue $watch "offPassbandAudioRmsDbfs" "average" }
        "passbandNoiseSeparationDb" {
            $passbandAverage = Get-StatValue $watch "passbandAudioRmsDbfs" "average"
            $offPassbandAverage = Get-StatValue $watch "offPassbandAudioRmsDbfs" "average"
            if ($null -eq $passbandAverage -or $null -eq $offPassbandAverage) {
                return $null
            }

            return [Math]::Round(([double]$passbandAverage - [double]$offPassbandAverage), 3)
        }
        default { return Get-NumericValue (Get-JsonValue $watch $Name) }
    }
}

function Round-NullableMetric {
    param(
        $Value,
        [int]$Digits = 3
    )

    $numeric = Get-NumericValue $Value
    if ($null -eq $numeric) {
        return $null
    }

    return [Math]::Round([double]$numeric, $Digits)
}

function Get-NullableDelta {
    param(
        $CandidateValue,
        $BaselineValue,
        [int]$Digits = 3
    )

    $candidate = Get-NumericValue $CandidateValue
    $baseline = Get-NumericValue $BaselineValue
    if ($null -eq $candidate -or $null -eq $baseline) {
        return $null
    }

    return [Math]::Round(([double]$candidate - [double]$baseline), $Digits)
}

function Test-NullableMetricRegression {
    param(
        $CandidateValue,
        $BaselineValue,
        [double]$Threshold
    )

    $candidate = Get-NumericValue $CandidateValue
    $baseline = Get-NumericValue $BaselineValue
    if ($null -eq $candidate -or $null -eq $baseline) {
        return $false
    }

    return (([double]$candidate - [double]$baseline) -gt $Threshold)
}

function New-Nr5WeakSignalComparison {
    param(
        $BaselineReport,
        $CandidateReport
    )

    $baselineWeakInput = Get-Nr5WeakSignalValue $BaselineReport "weakInputSampleCount"
    $candidateWeakInput = Get-Nr5WeakSignalValue $CandidateReport "weakInputSampleCount"
    $baselineWeakRecovered = Get-Nr5WeakSignalValue $BaselineReport "weakRecoveredSampleCount"
    $candidateWeakRecovered = Get-Nr5WeakSignalValue $CandidateReport "weakRecoveredSampleCount"
    $baselineWeakDropout = Get-Nr5WeakSignalValue $BaselineReport "weakDropoutSampleCount"
    $candidateWeakDropout = Get-Nr5WeakSignalValue $CandidateReport "weakDropoutSampleCount"
    $baselineCandidateWeakLoss = Get-Nr5WeakSignalValue $BaselineReport "weakDropoutCandidateLossSampleCount"
    $candidateCandidateWeakLoss = Get-Nr5WeakSignalValue $CandidateReport "weakDropoutCandidateLossSampleCount"
    $baselineFinalAudibleDropouts = Get-Nr5WeakSignalValue $BaselineReport "weakDropoutFinalAudibleSampleCount"
    $candidateFinalAudibleDropouts = Get-Nr5WeakSignalValue $CandidateReport "weakDropoutFinalAudibleSampleCount"
    $baselineFinalAudiblePct = Get-Nr5WeakSignalValue $BaselineReport "weakDropoutFinalAudiblePct"
    $candidateFinalAudiblePct = Get-Nr5WeakSignalValue $CandidateReport "weakDropoutFinalAudiblePct"
    $baselineHotMakeup = Get-Nr5WeakSignalValue $BaselineReport "hotMakeupSampleCount"
    $candidateHotMakeup = Get-Nr5WeakSignalValue $CandidateReport "hotMakeupSampleCount"
    $baselineRecoveryPct = Get-Nr5WeakRecoveryPct $BaselineReport
    $candidateRecoveryPct = Get-Nr5WeakRecoveryPct $CandidateReport
    $baselineOutputMovement = Get-StatValue $BaselineReport "nr5OutputDbfs" "movement"
    $candidateOutputMovement = Get-StatValue $CandidateReport "nr5OutputDbfs" "movement"
    $baselineMakeupMovement = Get-StatValue $BaselineReport "nr5MakeupGainDb" "movement"
    $candidateMakeupMovement = Get-StatValue $CandidateReport "nr5MakeupGainDb" "movement"
    $baselineMakeupMax = Get-StatValue $BaselineReport "nr5MakeupGainDb" "max"
    $candidateMakeupMax = Get-StatValue $CandidateReport "nr5MakeupGainDb" "max"
    $baselineRecoveryMovement = Get-StatValue $BaselineReport "nr5RecoveryDrive" "movement"
    $candidateRecoveryMovement = Get-StatValue $CandidateReport "nr5RecoveryDrive" "movement"
    $baselineTextureAverage = Get-StatValue $BaselineReport "nr5TextureFill" "average"
    $candidateTextureAverage = Get-StatValue $CandidateReport "nr5TextureFill" "average"
    $baselineLowEvidenceLifted = Get-Nr5LowEvidenceLiftComparisonValue $BaselineReport "liftedSampleCount"
    $candidateLowEvidenceLifted = Get-Nr5LowEvidenceLiftComparisonValue $CandidateReport "liftedSampleCount"
    $baselineLowEvidenceLiftedPct = Get-Nr5LowEvidenceLiftComparisonValue $BaselineReport "liftedPct"
    $candidateLowEvidenceLiftedPct = Get-Nr5LowEvidenceLiftComparisonValue $CandidateReport "liftedPct"
    $baselineAudioAlignmentMismatchPct = Get-Nr5AudioAlignmentMismatchPct $BaselineReport
    $candidateAudioAlignmentMismatchPct = Get-Nr5AudioAlignmentMismatchPct $CandidateReport
    $baselineArtifactRiskScore = Get-Nr5ArtifactRiskScore $BaselineReport
    $candidateArtifactRiskScore = Get-Nr5ArtifactRiskScore $CandidateReport
    $baselineArtifactRiskStatus = Get-Nr5ArtifactRiskStatus $BaselineReport
    $candidateArtifactRiskStatus = Get-Nr5ArtifactRiskStatus $CandidateReport
    $nr5StatComparisonAvailable = ($null -ne $baselineOutputMovement -and
        $null -ne $candidateOutputMovement -and
        $null -ne $baselineMakeupMovement -and
        $null -ne $candidateMakeupMovement -and
        $null -ne $baselineMakeupMax -and
        $null -ne $candidateMakeupMax -and
        $null -ne $baselineRecoveryMovement -and
        $null -ne $candidateRecoveryMovement)

    return [ordered]@{
        nr5StatComparisonAvailable = $nr5StatComparisonAvailable
        baselineWeakInputSampleCount = [int][Math]::Round($baselineWeakInput)
        candidateWeakInputSampleCount = [int][Math]::Round($candidateWeakInput)
        weakInputSampleDelta = [int][Math]::Round($candidateWeakInput - $baselineWeakInput)
        baselineWeakRecoveredSampleCount = [int][Math]::Round($baselineWeakRecovered)
        candidateWeakRecoveredSampleCount = [int][Math]::Round($candidateWeakRecovered)
        weakRecoveredSampleDelta = [int][Math]::Round($candidateWeakRecovered - $baselineWeakRecovered)
        baselineWeakDropoutSampleCount = [int][Math]::Round($baselineWeakDropout)
        candidateWeakDropoutSampleCount = [int][Math]::Round($candidateWeakDropout)
        weakDropoutSampleDelta = [int][Math]::Round($candidateWeakDropout - $baselineWeakDropout)
        baselineCandidateWeakLossSampleCount = [int][Math]::Round($baselineCandidateWeakLoss)
        candidateCandidateWeakLossSampleCount = [int][Math]::Round($candidateCandidateWeakLoss)
        candidateWeakLossSampleDelta = [int][Math]::Round($candidateCandidateWeakLoss - $baselineCandidateWeakLoss)
        baselineFinalAudibleWeakDropoutSampleCount = [int][Math]::Round($baselineFinalAudibleDropouts)
        candidateFinalAudibleWeakDropoutSampleCount = [int][Math]::Round($candidateFinalAudibleDropouts)
        finalAudibleWeakDropoutSampleDelta = [int][Math]::Round($candidateFinalAudibleDropouts - $baselineFinalAudibleDropouts)
        baselineFinalAudibleWeakDropoutPct = Round-NullableMetric $baselineFinalAudiblePct
        candidateFinalAudibleWeakDropoutPct = Round-NullableMetric $candidateFinalAudiblePct
        finalAudibleWeakDropoutPctDelta = Get-NullableDelta $candidateFinalAudiblePct $baselineFinalAudiblePct
        baselineHotMakeupSampleCount = [int][Math]::Round($baselineHotMakeup)
        candidateHotMakeupSampleCount = [int][Math]::Round($candidateHotMakeup)
        hotMakeupSampleDelta = [int][Math]::Round($candidateHotMakeup - $baselineHotMakeup)
        baselineWeakRecoveryPct = $baselineRecoveryPct
        candidateWeakRecoveryPct = $candidateRecoveryPct
        weakRecoveryPctDelta = [Math]::Round($candidateRecoveryPct - $baselineRecoveryPct, 3)
        baselineOutputMovementDb = Round-NullableMetric $baselineOutputMovement
        candidateOutputMovementDb = Round-NullableMetric $candidateOutputMovement
        outputMovementDbDelta = Get-NullableDelta $candidateOutputMovement $baselineOutputMovement
        baselineMakeupMovementDb = Round-NullableMetric $baselineMakeupMovement
        candidateMakeupMovementDb = Round-NullableMetric $candidateMakeupMovement
        makeupMovementDbDelta = Get-NullableDelta $candidateMakeupMovement $baselineMakeupMovement
        baselineMakeupMaxDb = Round-NullableMetric $baselineMakeupMax
        candidateMakeupMaxDb = Round-NullableMetric $candidateMakeupMax
        makeupMaxDbDelta = Get-NullableDelta $candidateMakeupMax $baselineMakeupMax
        baselineRecoveryDriveMovement = Round-NullableMetric $baselineRecoveryMovement
        candidateRecoveryDriveMovement = Round-NullableMetric $candidateRecoveryMovement
        recoveryDriveMovementDelta = Get-NullableDelta $candidateRecoveryMovement $baselineRecoveryMovement
        baselineTextureFillAverage = Round-NullableMetric $baselineTextureAverage
        candidateTextureFillAverage = Round-NullableMetric $candidateTextureAverage
        textureFillAverageDelta = Get-NullableDelta $candidateTextureAverage $baselineTextureAverage
        baselineLowEvidenceLiftedSampleCount = if ($null -eq $baselineLowEvidenceLifted) { $null } else { [int][Math]::Round($baselineLowEvidenceLifted) }
        candidateLowEvidenceLiftedSampleCount = if ($null -eq $candidateLowEvidenceLifted) { $null } else { [int][Math]::Round($candidateLowEvidenceLifted) }
        lowEvidenceLiftedSampleDelta = Get-NullableDelta $candidateLowEvidenceLifted $baselineLowEvidenceLifted
        baselineLowEvidenceLiftedPct = Round-NullableMetric $baselineLowEvidenceLiftedPct
        candidateLowEvidenceLiftedPct = Round-NullableMetric $candidateLowEvidenceLiftedPct
        lowEvidenceLiftedPctDelta = Get-NullableDelta $candidateLowEvidenceLiftedPct $baselineLowEvidenceLiftedPct
        baselineAudioAlignmentMismatchPct = Round-NullableMetric $baselineAudioAlignmentMismatchPct
        candidateAudioAlignmentMismatchPct = Round-NullableMetric $candidateAudioAlignmentMismatchPct
        audioAlignmentMismatchPctDelta = Get-NullableDelta $candidateAudioAlignmentMismatchPct $baselineAudioAlignmentMismatchPct
        baselineArtifactRiskStatus = $baselineArtifactRiskStatus
        candidateArtifactRiskStatus = $candidateArtifactRiskStatus
        baselineArtifactRiskScore = Round-NullableMetric $baselineArtifactRiskScore
        candidateArtifactRiskScore = Round-NullableMetric $candidateArtifactRiskScore
        artifactRiskScoreDelta = Get-NullableDelta $candidateArtifactRiskScore $baselineArtifactRiskScore
        dropoutRegression = ($candidateWeakDropout -gt $baselineWeakDropout)
        candidateWeakLossRegression = ($candidateCandidateWeakLoss -gt $baselineCandidateWeakLoss)
        hotMakeupRegression = ($candidateHotMakeup -gt $baselineHotMakeup)
        recoveryRegression = ($candidateRecoveryPct -lt ($baselineRecoveryPct - 5.0))
        outputMovementRegression = Test-NullableMetricRegression $candidateOutputMovement $baselineOutputMovement 1.0
        makeupMovementRegression = Test-NullableMetricRegression $candidateMakeupMovement $baselineMakeupMovement 1.0
        makeupMaxRegression = Test-NullableMetricRegression $candidateMakeupMax $baselineMakeupMax 1.0
        recoveryDriveMovementRegression = Test-NullableMetricRegression $candidateRecoveryMovement $baselineRecoveryMovement 0.10
        lowEvidenceLiftedRegression = Test-NullableMetricRegression $candidateLowEvidenceLifted $baselineLowEvidenceLifted 0.0
        lowEvidenceLiftedPctRegression = Test-NullableMetricRegression $candidateLowEvidenceLiftedPct $baselineLowEvidenceLiftedPct 5.0
        audioAlignmentMismatchRegression = Test-NullableMetricRegression $candidateAudioAlignmentMismatchPct $baselineAudioAlignmentMismatchPct 10.0
        artifactRiskRegression = Test-NullableMetricRegression $candidateArtifactRiskScore $baselineArtifactRiskScore 0.0
    }
}

function New-RxAudioLevelerComparison {
    param(
        $BaselineReport,
        $CandidateReport
    )

    $baselineDiagnosticCount = Get-RxAudioLevelerWatchValue $BaselineReport "diagnosticSampleCount"
    $candidateDiagnosticCount = Get-RxAudioLevelerWatchValue $CandidateReport "diagnosticSampleCount"
    $baselineConstrainedCount = Get-RxAudioLevelerWatchValue $BaselineReport "constrainedSampleCount"
    $candidateConstrainedCount = Get-RxAudioLevelerWatchValue $CandidateReport "constrainedSampleCount"
    $baselineConstrainedPct = Get-RxAudioLevelerWatchValue $BaselineReport "constrainedPct"
    $candidateConstrainedPct = Get-RxAudioLevelerWatchValue $CandidateReport "constrainedPct"
    $baselineBoostSlewCount = Get-RxAudioLevelerWatchValue $BaselineReport "boostSlewLimitedSampleCount"
    $candidateBoostSlewCount = Get-RxAudioLevelerWatchValue $CandidateReport "boostSlewLimitedSampleCount"
    $baselinePeakLimitedCount = Get-RxAudioLevelerWatchValue $BaselineReport "peakLimitedSampleCount"
    $candidatePeakLimitedCount = Get-RxAudioLevelerWatchValue $CandidateReport "peakLimitedSampleCount"
    $baselineOutputLimitedCount = Get-RxAudioLevelerWatchValue $BaselineReport "outputLimitedSampleCount"
    $candidateOutputLimitedCount = Get-RxAudioLevelerWatchValue $CandidateReport "outputLimitedSampleCount"
    $baselineOutputMovement = Get-StatValue $BaselineReport "rxAudioLevelerOutputRmsDbfs" "movement"
    $candidateOutputMovement = Get-StatValue $CandidateReport "rxAudioLevelerOutputRmsDbfs" "movement"
    $baselineGainMovement = Get-StatValue $BaselineReport "rxAudioLevelerAppliedGainDb" "movement"
    $candidateGainMovement = Get-StatValue $CandidateReport "rxAudioLevelerAppliedGainDb" "movement"

    return [ordered]@{
        baselineDiagnosticSampleCount = Round-NullableMetric $baselineDiagnosticCount
        candidateDiagnosticSampleCount = Round-NullableMetric $candidateDiagnosticCount
        baselineConstrainedSampleCount = Round-NullableMetric $baselineConstrainedCount
        candidateConstrainedSampleCount = Round-NullableMetric $candidateConstrainedCount
        constrainedSampleDelta = Get-NullableDelta $candidateConstrainedCount $baselineConstrainedCount
        baselineConstrainedPct = Round-NullableMetric $baselineConstrainedPct
        candidateConstrainedPct = Round-NullableMetric $candidateConstrainedPct
        constrainedPctDelta = Get-NullableDelta $candidateConstrainedPct $baselineConstrainedPct
        baselineBoostSlewLimitedSampleCount = Round-NullableMetric $baselineBoostSlewCount
        candidateBoostSlewLimitedSampleCount = Round-NullableMetric $candidateBoostSlewCount
        boostSlewLimitedSampleDelta = Get-NullableDelta $candidateBoostSlewCount $baselineBoostSlewCount
        baselinePeakLimitedSampleCount = Round-NullableMetric $baselinePeakLimitedCount
        candidatePeakLimitedSampleCount = Round-NullableMetric $candidatePeakLimitedCount
        peakLimitedSampleDelta = Get-NullableDelta $candidatePeakLimitedCount $baselinePeakLimitedCount
        baselineOutputLimitedSampleCount = Round-NullableMetric $baselineOutputLimitedCount
        candidateOutputLimitedSampleCount = Round-NullableMetric $candidateOutputLimitedCount
        outputLimitedSampleDelta = Get-NullableDelta $candidateOutputLimitedCount $baselineOutputLimitedCount
        baselineOutputRmsMovementDb = Round-NullableMetric $baselineOutputMovement
        candidateOutputRmsMovementDb = Round-NullableMetric $candidateOutputMovement
        outputRmsMovementDbDelta = Get-NullableDelta $candidateOutputMovement $baselineOutputMovement
        baselineAppliedGainMovementDb = Round-NullableMetric $baselineGainMovement
        candidateAppliedGainMovementDb = Round-NullableMetric $candidateGainMovement
        appliedGainMovementDbDelta = Get-NullableDelta $candidateGainMovement $baselineGainMovement
        constrainedRegression = Test-NullableMetricRegression $candidateConstrainedCount $baselineConstrainedCount 0.0
        constrainedPctRegression = Test-NullableMetricRegression $candidateConstrainedPct $baselineConstrainedPct 1.0
        boostSlewRegression = Test-NullableMetricRegression $candidateBoostSlewCount $baselineBoostSlewCount 0.0
        peakLimitedRegression = Test-NullableMetricRegression $candidatePeakLimitedCount $baselinePeakLimitedCount 0.0
        outputLimitedRegression = Test-NullableMetricRegression $candidateOutputLimitedCount $baselineOutputLimitedCount 0.0
        candidateTopBoostSlewLimitedSamples = @(Get-RxAudioLevelerWatchSamples $CandidateReport "topBoostSlewLimitedSamples")
        candidateTopPeakLimitedSamples = @(Get-RxAudioLevelerWatchSamples $CandidateReport "topPeakLimitedSamples")
        candidateTopOutputLimitedSamples = @(Get-RxAudioLevelerWatchSamples $CandidateReport "topOutputLimitedSamples")
    }
}

function Get-Percent {
    param(
        $Numerator,
        $Denominator
    )

    $num = Get-NumericValue $Numerator
    $den = Get-NumericValue $Denominator
    if ($null -eq $num -or $null -eq $den -or $den -le 0.0) {
        return $null
    }

    return [Math]::Round(100.0 * $num / $den, 3)
}

function Get-CountMap {
    param($Items)

    $map = @{}
    foreach ($item in @($Items)) {
        $name = [string](Get-JsonValue $item "name")
        $count = Get-NumericValue (Get-JsonValue $item "count")
        if (-not [string]::IsNullOrWhiteSpace($name) -and $null -ne $count) {
            $map[$name] = [int]$count
        }
    }

    return $map
}

function Get-TraceSeverity {
    param([string]$Status)

    switch ($Status) {
        "endpoint-unreachable" { return 80 }
        "blocked" { return 70 }
        "final-audio-not-fresh" { return 60 }
        "rx-meters-not-fresh" { return 50 }
        "runtime-evidence-missing" { return 50 }
        "nr5-agc-diagnostics-missing" { return 45 }
        "agc-pumping-watch" { return 40 }
        "agc-movement-watch" { return 35 }
        "audio-level-watch" { return 35 }
        "nr5-output-level-watch" { return 35 }
        "nr5-low-evidence-lift-watch" { return 35 }
        "rx-leveler-cap-watch" { return 35 }
        "rx-leveler-settling-watch" { return 35 }
        "rx-leveler-headroom-watch" { return 35 }
        "evidence-trace" { return 20 }
        "ready-trace" { return 0 }
        default { return 25 }
    }
}

function Resolve-InputReport {
    param([Parameter(Mandatory = $true)][string]$Path)

    $resolved = (Resolve-Path -LiteralPath $Path).Path
    $extension = [System.IO.Path]::GetExtension($resolved)
    if ($extension -ieq ".jsonl") {
        $watchScript = Join-Path (Get-RepoRoot) "tools\watch-dsp-live-diagnostics.ps1"
        if (-not (Test-Path -LiteralPath $watchScript -PathType Leaf)) {
            throw "Missing watch script needed to summarize JSONL input: $watchScript"
        }

        $tempReport = Join-Path ([System.IO.Path]::GetTempPath()) ("zeus-live-trace-summary-" + [guid]::NewGuid().ToString("N") + ".json")
        $json = & powershell -NoProfile -ExecutionPolicy Bypass -File $watchScript -InputPath $resolved -ReportPath $tempReport -JsonOnly
        if ($LASTEXITCODE -ne 0) {
            throw "watch-dsp-live-diagnostics.ps1 failed while summarizing '$resolved'."
        }

        return [ordered]@{
            path = $resolved
            summarizedFromJsonl = $true
            report = ($json | ConvertFrom-Json)
            tempReportPath = $tempReport
        }
    }

    $report = Read-JsonFile $resolved
    $tool = [string](Get-JsonValue $report "tool")
    if ($tool -ne "watch-dsp-live-diagnostics") {
        throw "Input '$resolved' must be a watch-dsp-live-diagnostics JSON report or JSONL trace; found tool '$tool'."
    }

    return [ordered]@{
        path = $resolved
        summarizedFromJsonl = $false
        report = $report
        tempReportPath = $null
    }
}

function Get-TraceMetricDefinitions {
    return @(
        [ordered]@{
            id = "failedSampleCount"
            label = "Failed samples"
            direction = "lower"
            threshold = 0.0
            safetyClass = "hard-gate"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "Endpoint failures make trace evidence incomplete."
        },
        [ordered]@{
            id = "hardBlockerSampleCount"
            label = "Hard blocker samples"
            direction = "lower"
            threshold = 0.0
            safetyClass = "hard-gate"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "Hard live diagnostics blockers must not increase under candidate DSP settings."
        },
        [ordered]@{
            id = "readySamplePct"
            label = "Ready sample percent"
            direction = "higher"
            threshold = 1.0
            safetyClass = "readiness"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "Candidate traces should not reduce readiness for G2 live benchmark capture."
        },
        [ordered]@{
            id = "readinessScoreAverage"
            label = "Average readiness score"
            direction = "higher"
            threshold = 1.0
            safetyClass = "readiness"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "Readiness score combines WDSP lifecycle, runtime alignment, live scene, and runtime evidence constraints."
        },
        [ordered]@{
            id = "agcGainMovementDb"
            label = "AGC gain movement dB"
            direction = "lower"
            threshold = 1.0
            safetyClass = "pumping"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "Lower movement reduces the risk of audible AGC pumping during NR/AGC tuning."
        },
        [ordered]@{
            id = "agcActiveGainMovementDb"
            label = "Active-audio AGC gain movement dB"
            direction = "lower"
            threshold = 1.0
            safetyClass = "pumping"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "AGC gain movement during active audio is a direct pumping-risk signal, unlike quiet-to-active transitions."
        },
        [ordered]@{
            id = "agcVoiceLikeGainMovementDb"
            label = "Voice-like AGC gain movement dB"
            direction = "lower"
            threshold = 1.0
            safetyClass = "pumping"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "Voice-like AGC movement should not increase when evaluating speech-preservation and NR5 recovery behavior."
        },
        [ordered]@{
            id = "agcPumpingRisk"
            label = "AGC active pumping risk"
            direction = "lower"
            threshold = 0.0
            safetyClass = "pumping"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "Candidate traces must not introduce the watcher-classified active/voice-like AGC pumping risk."
        },
        [ordered]@{
            id = "passbandPeakSampleCount"
            label = "Passband peak evidence samples"
            direction = "informational"
            threshold = 0.0
            safetyClass = "weak-signal"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "Near-passband frontend peak coverage shows whether a trace contains tuned-signal evidence before comparing audio behavior."
        },
        [ordered]@{
            id = "passbandActiveAudioPct"
            label = "Passband active-audio percent"
            direction = "higher"
            threshold = 10.0
            safetyClass = "weak-signal"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "When the frontend sees tuned passband peaks, candidate DSP should not materially reduce audible tuned-signal occupancy."
        },
        [ordered]@{
            id = "passbandFloorAudioPct"
            label = "Passband floor-audio percent"
            direction = "lower"
            threshold = 10.0
            safetyClass = "weak-signal"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "Tuned passband peak samples should not be pushed down into the final-audio floor."
        },
        [ordered]@{
            id = "passbandAudioAverageDbfs"
            label = "Passband audio average dBFS"
            direction = "higher"
            threshold = 3.0
            safetyClass = "weak-signal"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "Weak tuned speech should stay level-normalized instead of being made materially quieter than the baseline passband audio."
        },
        [ordered]@{
            id = "passbandAudioMovementDb"
            label = "Passband audio movement dB"
            direction = "lower"
            threshold = 2.0
            safetyClass = "pumping"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "Level movement inside tuned passband evidence is the clearest comparator signal for audible fading or pumping."
        },
        [ordered]@{
            id = "offPassbandAudioAverageDbfs"
            label = "Off-passband audio average dBFS"
            direction = "lower"
            threshold = 3.0
            safetyClass = "noise-gate"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "Noise/no-evidence samples outside the tuned passband should be quieter after candidate DSP processing."
        },
        [ordered]@{
            id = "passbandNoiseSeparationDb"
            label = "Passband-to-noise separation dB"
            direction = "higher"
            threshold = 3.0
            safetyClass = "noise-gate"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "Candidate DSP should increase the level gap between tuned passband audio and off-passband/no-evidence audio."
        },
        [ordered]@{
            id = "nr5WeakDropoutSampleCount"
            label = "NR5 strict weak-input dropout samples"
            direction = "lower"
            threshold = 0.0
            safetyClass = "weak-signal"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "Strict NR5 weak-output thresholds flag weak-burst rows for inspection; final-audio-audible rows are not automatically candidate weak loss."
        },
        [ordered]@{
            id = "nr5CandidateWeakLossSampleCount"
            label = "NR5 candidate weak-loss samples"
            direction = "lower"
            threshold = 0.0
            safetyClass = "weak-signal"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "Candidate NR5 live traces must not add weak-input rows that remain below input and are not already final-audio audible or natively lifted."
        },
        [ordered]@{
            id = "nr5WeakRecoveryPct"
            label = "NR5 weak-input recovery percent"
            direction = "higher"
            threshold = 5.0
            safetyClass = "weak-signal"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "Weak-signal preservation should improve or stay within 5 percentage points of the baseline recovery rate."
        },
        [ordered]@{
            id = "nr5HotMakeupSampleCount"
            label = "NR5 hot makeup samples"
            direction = "lower"
            threshold = 0.0
            safetyClass = "pumping"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "Candidate NR5 live traces must not add samples with makeup gain above the watcher hot-makeup threshold."
        },
        [ordered]@{
            id = "nr5LowEvidenceLiftSampleCount"
            label = "NR5 low-evidence lifted samples"
            direction = "lower"
            threshold = 0.0
            safetyClass = "noise-gate"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "Candidate NR5 live traces must not increase low-confidence weak-input samples that are lifted into audible output."
        },
        [ordered]@{
            id = "nr5LowEvidenceLiftedPct"
            label = "NR5 low-evidence lifted percent"
            direction = "lower"
            threshold = 5.0
            safetyClass = "artifact-control"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "Low-evidence lift must stay bounded so speech-artifact review rows cannot hide inside weak-signal recovery gains."
        },
        [ordered]@{
            id = "nr5AudioAlignmentMismatchPct"
            label = "NR5 audio-alignment mismatch percent"
            direction = "lower"
            threshold = 10.0
            safetyClass = "artifact-control"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "NR5 artifact-control evidence is unsafe when candidate output rows diverge from the aligned final-audio window."
        },
        [ordered]@{
            id = "nr5ArtifactRiskScore"
            label = "NR5 artifact-risk score"
            direction = "lower"
            threshold = 0.0
            safetyClass = "artifact-control"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "Candidate traces must not introduce the matrix artifact-review score from low-evidence lift, alignment mismatch, or unsupported texture fill."
        },
        [ordered]@{
            id = "nr5OutputMovementDb"
            label = "NR5 output movement dB"
            direction = "lower"
            threshold = 1.0
            safetyClass = "pumping"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "Candidate NR5 output level should not swing more than the baseline trace; larger movement risks audible level pumping."
        },
        [ordered]@{
            id = "nr5MakeupMovementDb"
            label = "NR5 makeup movement dB"
            direction = "lower"
            threshold = 1.0
            safetyClass = "pumping"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "Large makeup-gain movement is a direct review signal for NR5 output-level watch traces."
        },
        [ordered]@{
            id = "nr5MakeupMaxDb"
            label = "NR5 maximum makeup dB"
            direction = "lower"
            threshold = 1.0
            safetyClass = "pumping"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "Candidate NR5 tuning should not require a higher maximum makeup boost to recover weak content."
        },
        [ordered]@{
            id = "nr5RecoveryDriveMovement"
            label = "NR5 recovery-drive movement"
            direction = "lower"
            threshold = 0.1
            safetyClass = "pumping"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "Recovery-drive movement is the fast control surface behind many NR5 output-level watch traces."
        },
        [ordered]@{
            id = "nr5TextureFillAverage"
            label = "NR5 texture-fill average"
            direction = "informational"
            threshold = 0.01
            safetyClass = "weak-signal"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "Texture fill helps distinguish weak-signal hole-fill from persistent makeup; direction is scenario-dependent."
        },
        [ordered]@{
            id = "nr5PeakReductionMaxDb"
            label = "NR5 maximum peak reduction dB"
            direction = "lower"
            threshold = 1.0
            safetyClass = "clipping"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "Higher NR5 peak-shaper pressure means the candidate is creating or passing larger crests before final audio."
        },
        [ordered]@{
            id = "nr5OutputPeakMaxDbfs"
            label = "NR5 maximum output peak dBFS"
            direction = "lower"
            threshold = 1.0
            safetyClass = "clipping"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "NR5 output peaks should not move closer to clipping before downstream audio processing."
        },
        [ordered]@{
            id = "audioRmsMovementDb"
            label = "Audio RMS movement dB"
            direction = "lower"
            threshold = 1.0
            safetyClass = "pumping"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "Large final-audio RMS swings need fixture/audio review before tuning is accepted."
        },
        [ordered]@{
            id = "audioPeakMaxDbfs"
            label = "Maximum audio peak dBFS"
            direction = "lower"
            threshold = 1.0
            safetyClass = "clipping"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "Higher dBFS peak values are closer to clipping and should not regress."
        },
        [ordered]@{
            id = "rxAudioLevelerOutputRmsMovementDb"
            label = "RX audio leveler output RMS movement dB"
            direction = "lower"
            threshold = 1.0
            safetyClass = "pumping"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "The final RX leveler should reduce loudness movement without adding audible pumping."
        },
        [ordered]@{
            id = "rxAudioLevelerAppliedGainMovementDb"
            label = "RX audio leveler applied gain movement dB"
            direction = "lower"
            threshold = 1.0
            safetyClass = "pumping"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "Large final leveler gain swings indicate downstream loudness pumping even when NR5 output is stable."
        },
        [ordered]@{
            id = "rxAudioLevelerConstrainedSampleCount"
            label = "RX audio leveler constrained samples"
            direction = "lower"
            threshold = 0.0
            safetyClass = "pumping"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "Any increase in constrained final-leveler samples means loudness normalization is still fighting slew or peak limits."
        },
        [ordered]@{
            id = "rxAudioLevelerConstrainedPct"
            label = "RX audio leveler constrained percent"
            direction = "lower"
            threshold = 1.0
            safetyClass = "pumping"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "Constrained-sample percentage normalizes leveler warnings across unequal trace lengths."
        },
        [ordered]@{
            id = "rxAudioLevelerBoostSlewLimitedSampleCount"
            label = "RX audio leveler boost-slew limited samples"
            direction = "lower"
            threshold = 0.0
            safetyClass = "pumping"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "More boost-slew limited samples means weak-signal loudness is still settling rather than fully normalized."
        },
        [ordered]@{
            id = "rxAudioLevelerPeakLimitedSampleCount"
            label = "RX audio leveler peak-limited samples"
            direction = "lower"
            threshold = 0.0
            safetyClass = "clipping"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "More peak-limited samples means final audio headroom is constraining normalization."
        },
        [ordered]@{
            id = "rxAudioLevelerOutputLimitedSampleCount"
            label = "RX audio leveler output-limited blocks"
            direction = "lower"
            threshold = 0.0
            safetyClass = "clipping"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "More final crest-cap blocks means loudness normalization is relying on peak shaping."
        },
        [ordered]@{
            id = "rxAudioLevelerOutputLimitReductionMaxDb"
            label = "RX audio leveler max crest-cap reduction dB"
            direction = "lower"
            threshold = 0.5
            safetyClass = "clipping"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "Higher final crest-cap reduction can indicate audible peak shaping after NR."
        },
        [ordered]@{
            id = "rxAudioLevelerOutputLimitSampleCountMax"
            label = "RX audio leveler max shaped samples per block"
            direction = "lower"
            threshold = 8.0
            safetyClass = "clipping"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "More shaped samples per block means the final limiter is affecting more of the waveform."
        },
        [ordered]@{
            id = "adcHeadroomMinDb"
            label = "Minimum ADC headroom dB"
            direction = "higher"
            threshold = 1.0
            safetyClass = "front-end"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "Candidate evaluation should not consume ADC headroom."
        },
        [ordered]@{
            id = "monitorBacklogMaxSamples"
            label = "Maximum monitor backlog samples"
            direction = "lower"
            threshold = 0.0
            safetyClass = "audio-path"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "Backlog invalidates live audio fidelity evidence."
        },
        [ordered]@{
            id = "audioFreshPct"
            label = "Audio fresh percent"
            direction = "higher"
            threshold = 1.0
            safetyClass = "freshness"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "Fresh final audio is required before judging NR/AGC or external engines."
        },
        [ordered]@{
            id = "rxMetersFreshPct"
            label = "RX meters fresh percent"
            direction = "higher"
            threshold = 1.0
            safetyClass = "freshness"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "Fresh RX meters are needed for AGC/headroom evidence."
        },
        [ordered]@{
            id = "squelchClosedPct"
            label = "Squelch closed percent"
            direction = "informational"
            threshold = 1.0
            safetyClass = "scenario-dependent"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "Higher closed time is good for noise-only gating but unsafe for weak-signal preservation; review per scenario."
        },
        [ordered]@{
            id = "latencyAverageMs"
            label = "Average endpoint latency ms"
            direction = "lower"
            threshold = 5.0
            safetyClass = "tooling"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "Diagnostics overhead should stay bounded during evidence capture."
        },
        [ordered]@{
            id = "traceStatusSeverity"
            label = "Trace status severity"
            direction = "lower"
            threshold = 0.0
            safetyClass = "hard-gate"
            acceptanceScope = "live-diagnostics-trace-comparison"
            rationale = "Candidate trace status should not move to a more severe watch or blocked state."
        }
    )
}

function Get-MetricValue {
    param(
        $Report,
        [Parameter(Mandatory = $true)][string]$MetricId
    )

    switch ($MetricId) {
        "failedSampleCount" { return Get-NumericValue (Get-JsonValue $Report "failedSampleCount") }
        "hardBlockerSampleCount" { return Get-NumericValue (Get-JsonValue $Report "hardBlockerSampleCount") }
        "readySamplePct" { return Get-Percent (Get-JsonValue $Report "readySampleCount") (Get-JsonValue $Report "okSampleCount") }
        "readinessScoreAverage" { return Get-StatValue $Report "readinessScore" "average" }
        "agcGainMovementDb" { return Get-StatValue $Report "agcGainDb" "movement" }
        "agcActiveGainMovementDb" { return Get-AgcStabilityWatchValue $Report "activeAgcMovementDb" }
        "agcVoiceLikeGainMovementDb" { return Get-AgcStabilityWatchValue $Report "voiceLikeAgcMovementDb" }
        "agcPumpingRisk" { return Get-AgcStabilityWatchValue $Report "pumpingRisk" }
        "passbandPeakSampleCount" { return Get-PassbandAudioWatchValue $Report "passbandPeakSampleCount" }
        "passbandActiveAudioPct" { return Get-PassbandAudioWatchValue $Report "passbandActiveAudioPct" }
        "passbandFloorAudioPct" { return Get-PassbandAudioWatchValue $Report "passbandFloorAudioPct" }
        "passbandAudioAverageDbfs" { return Get-PassbandAudioWatchValue $Report "passbandAudioAverageDbfs" }
        "passbandAudioMovementDb" { return Get-PassbandAudioWatchValue $Report "passbandAudioMovementDb" }
        "offPassbandAudioAverageDbfs" { return Get-PassbandAudioWatchValue $Report "offPassbandAudioAverageDbfs" }
        "passbandNoiseSeparationDb" { return Get-PassbandAudioWatchValue $Report "passbandNoiseSeparationDb" }
        "nr5WeakDropoutSampleCount" { return Get-Nr5WeakSignalValue $Report "weakDropoutSampleCount" }
        "nr5CandidateWeakLossSampleCount" { return Get-Nr5WeakSignalValue $Report "weakDropoutCandidateLossSampleCount" }
        "nr5WeakRecoveryPct" { return Get-Nr5WeakRecoveryPct $Report }
        "nr5HotMakeupSampleCount" { return Get-Nr5WeakSignalValue $Report "hotMakeupSampleCount" }
        "nr5LowEvidenceLiftSampleCount" { return Get-Nr5LowEvidenceLiftValue $Report "liftedSampleCount" }
        "nr5LowEvidenceLiftedPct" { return Get-Nr5LowEvidenceLiftComparisonValue $Report "liftedPct" }
        "nr5AudioAlignmentMismatchPct" { return Get-Nr5AudioAlignmentMismatchPct $Report }
        "nr5ArtifactRiskScore" { return Get-Nr5ArtifactRiskScore $Report }
        "nr5OutputMovementDb" { return Get-StatValue $Report "nr5OutputDbfs" "movement" }
        "nr5MakeupMovementDb" { return Get-StatValue $Report "nr5MakeupGainDb" "movement" }
        "nr5MakeupMaxDb" { return Get-StatValue $Report "nr5MakeupGainDb" "max" }
        "nr5RecoveryDriveMovement" { return Get-StatValue $Report "nr5RecoveryDrive" "movement" }
        "nr5TextureFillAverage" { return Get-StatValue $Report "nr5TextureFill" "average" }
        "nr5PeakReductionMaxDb" { return Get-StatValue $Report "nr5PeakReductionDb" "max" }
        "nr5OutputPeakMaxDbfs" { return Get-StatValue $Report "nr5OutputPeakDbfs" "max" }
        "audioRmsMovementDb" { return Get-StatValue $Report "audioRmsDbfs" "movement" }
        "audioPeakMaxDbfs" { return Get-StatValue $Report "audioPeakDbfs" "max" }
        "rxAudioLevelerOutputRmsMovementDb" { return Get-StatValue $Report "rxAudioLevelerOutputRmsDbfs" "movement" }
        "rxAudioLevelerAppliedGainMovementDb" { return Get-StatValue $Report "rxAudioLevelerAppliedGainDb" "movement" }
        "rxAudioLevelerConstrainedSampleCount" { return Get-RxAudioLevelerWatchValue $Report "constrainedSampleCount" }
        "rxAudioLevelerConstrainedPct" { return Get-RxAudioLevelerWatchValue $Report "constrainedPct" }
        "rxAudioLevelerBoostSlewLimitedSampleCount" { return Get-RxAudioLevelerWatchValue $Report "boostSlewLimitedSampleCount" }
        "rxAudioLevelerPeakLimitedSampleCount" { return Get-RxAudioLevelerWatchValue $Report "peakLimitedSampleCount" }
        "rxAudioLevelerOutputLimitedSampleCount" { return Get-RxAudioLevelerWatchValue $Report "outputLimitedSampleCount" }
        "rxAudioLevelerOutputLimitReductionMaxDb" { return Get-StatValue $Report "rxAudioLevelerOutputLimitReductionDb" "max" }
        "rxAudioLevelerOutputLimitSampleCountMax" { return Get-StatValue $Report "rxAudioLevelerOutputLimitSampleCount" "max" }
        "adcHeadroomMinDb" { return Get-StatValue $Report "adcHeadroomDb" "min" }
        "monitorBacklogMaxSamples" { return Get-StatValue $Report "monitorBacklogSamples" "max" }
        "audioFreshPct" { return Get-Percent (Get-JsonValue $Report "audioFreshSampleCount") (Get-JsonValue $Report "runtimeEvidenceSampleCount") }
        "rxMetersFreshPct" { return Get-Percent (Get-JsonValue $Report "rxMetersFreshSampleCount") (Get-JsonValue $Report "runtimeEvidenceSampleCount") }
        "squelchClosedPct" { return Get-NumericValue (Get-JsonValue $Report "squelchClosedPct") }
        "latencyAverageMs" { return Get-StatValue $Report "latencyMs" "average" }
        "traceStatusSeverity" { return [double](Get-TraceSeverity ([string](Get-JsonValue $Report "trendStatus"))) }
        default { return $null }
    }
}

function Get-ComparisonVerdict {
    param(
        [string]$Direction,
        [double]$BaselineValue,
        [double]$CandidateValue,
        [double]$Threshold
    )

    if ($Direction -eq "informational") {
        return "informational"
    }

    $improvement = $CandidateValue - $BaselineValue
    if ($Direction -eq "lower") {
        $improvement = $BaselineValue - $CandidateValue
    }

    if ($improvement -gt $Threshold) {
        return "improvement"
    }

    if ($improvement -lt (-1.0 * $Threshold)) {
        return "regression"
    }

    return "tie"
}

function Get-ReportScenarioId {
    param($Report)

    $scenarioId = [string](Get-JsonValue $Report "scenarioId")
    if ([string]::IsNullOrWhiteSpace($scenarioId)) {
        return ""
    }

    return $scenarioId.Trim()
}

function Get-ComparisonScenarioId {
    param(
        $BaselineReport,
        $CandidateReport
    )

    $baselineScenarioId = Get-ReportScenarioId $BaselineReport
    $candidateScenarioId = Get-ReportScenarioId $CandidateReport
    if ([string]::IsNullOrWhiteSpace($baselineScenarioId) -or
        [string]::IsNullOrWhiteSpace($candidateScenarioId) -or
        $baselineScenarioId -ne $candidateScenarioId) {
        return ""
    }

    return $candidateScenarioId
}

function Test-MetricNotApplicableForScenario {
    param(
        [Parameter(Mandatory = $true)][string]$MetricId,
        [Parameter(Mandatory = $true)][string]$ScenarioId
    )

    if ($ScenarioId -ne "noise-only-gating") {
        return $false
    }

    switch ($MetricId) {
        "agcActiveGainMovementDb" { return $true }
        "agcVoiceLikeGainMovementDb" { return $true }
        "passbandActiveAudioPct" { return $true }
        "passbandFloorAudioPct" { return $true }
        "passbandAudioAverageDbfs" { return $true }
        "passbandAudioMovementDb" { return $true }
        "passbandNoiseSeparationDb" { return $true }
        default { return $false }
    }
}

function Test-SafeCandidateAudioHeadroom {
    param($CandidateReport)

    $audioPeak = Get-MetricValue $CandidateReport "audioPeakMaxDbfs"
    $capBlocks = Get-MetricValue $CandidateReport "rxAudioLevelerOutputLimitedSampleCount"
    $capReduction = Get-MetricValue $CandidateReport "rxAudioLevelerOutputLimitReductionMaxDb"
    if ($null -eq $audioPeak) {
        return $false
    }

    return ([double]$audioPeak -le -2.0 -and
        ($null -eq $capBlocks -or [double]$capBlocks -le 0.0) -and
        ($null -eq $capReduction -or [double]$capReduction -le 0.5))
}

function Test-SafeCandidateNr5OutputPeak {
    param($CandidateReport)

    $nr5OutputPeak = Get-MetricValue $CandidateReport "nr5OutputPeakMaxDbfs"
    $nr5PeakReduction = Get-MetricValue $CandidateReport "nr5PeakReductionMaxDb"
    if ($null -eq $nr5OutputPeak) {
        return $false
    }

    return ([double]$nr5OutputPeak -le -6.0 -and
        ($null -eq $nr5PeakReduction -or [double]$nr5PeakReduction -le 0.5) -and
        (Test-SafeCandidateAudioHeadroom $CandidateReport))
}

function Test-StableCandidateLevelerOutput {
    param($CandidateReport)

    $audioMovement = Get-MetricValue $CandidateReport "audioRmsMovementDb"
    $levelerMovement = Get-MetricValue $CandidateReport "rxAudioLevelerOutputRmsMovementDb"
    $boostSlew = Get-MetricValue $CandidateReport "rxAudioLevelerBoostSlewLimitedSampleCount"
    if ($null -eq $audioMovement -or $null -eq $levelerMovement) {
        return $false
    }

    return ([double]$audioMovement -le 2.0 -and
        [double]$levelerMovement -le 2.0 -and
        ($null -eq $boostSlew -or [double]$boostSlew -le 0.0) -and
        (Test-SafeCandidateAudioHeadroom $CandidateReport))
}

function Test-CandidateLowEvidenceNoiseSuppressed {
    param($CandidateReport)

    $lowEvidence = Get-Nr5LowEvidenceLiftValue $CandidateReport "lowEvidenceSampleCount"
    $lifted = Get-Nr5LowEvidenceLiftValue $CandidateReport "liftedSampleCount"
    $suppressedPct = Get-Nr5LowEvidenceLiftValue $CandidateReport "suppressedPct"
    $audioAverage = Get-StatValue $CandidateReport "audioRmsDbfs" "average"
    $nr5OutputAverage = Get-StatValue $CandidateReport "nr5OutputDbfs" "average"
    if ($null -eq $lowEvidence -or $null -eq $lifted -or $null -eq $suppressedPct -or
        $null -eq $audioAverage -or $null -eq $nr5OutputAverage) {
        return $false
    }

    return ([double]$lowEvidence -ge 10.0 -and
        [double]$lifted -le 1.0 -and
        [double]$suppressedPct -ge 80.0 -and
        [double]$audioAverage -le -45.0 -and
        [double]$nr5OutputAverage -le -55.0)
}

function Test-BothTracesHaveSafeAdcHeadroom {
    param(
        $BaselineReport,
        $CandidateReport
    )

    $baselineHeadroom = Get-StatValue $BaselineReport "adcHeadroomDb" "min"
    $candidateHeadroom = Get-StatValue $CandidateReport "adcHeadroomDb" "min"
    if ($null -eq $baselineHeadroom -or $null -eq $candidateHeadroom) {
        return $false
    }

    return ([double]$baselineHeadroom -ge 30.0 -and [double]$candidateHeadroom -ge 30.0)
}

function Get-AdjustedComparisonVerdict {
    param(
        [Parameter(Mandatory = $true)][string]$MetricId,
        [Parameter(Mandatory = $true)][string]$Verdict,
        $BaselineReport,
        $CandidateReport,
        [double]$CandidateValue
    )

    if ($Verdict -ne "regression") {
        return [ordered]@{
            verdict = $Verdict
            note = $null
        }
    }

    switch ($MetricId) {
        "nr5WeakDropoutSampleCount" {
            if (Test-CandidateWeakDropoutsRecoveredAudibly $CandidateReport) {
                return [ordered]@{
                    verdict = "tie"
                    note = "strict-dropouts-final-audible-no-candidate-loss"
                }
            }
            if (Test-CandidateLowEvidenceNoiseSuppressed $CandidateReport) {
                return [ordered]@{
                    verdict = "tie"
                    note = "low-evidence-noise-suppressed-not-weak-loss"
                }
            }
        }
        "nr5WeakRecoveryPct" {
            if (Test-CandidateWeakDropoutsRecoveredAudibly $CandidateReport) {
                return [ordered]@{
                    verdict = "tie"
                    note = "strict-dropouts-final-audible-no-candidate-loss"
                }
            }
            if (Test-CandidateLowEvidenceNoiseSuppressed $CandidateReport) {
                return [ordered]@{
                    verdict = "tie"
                    note = "low-evidence-noise-suppressed-not-weak-loss"
                }
            }
        }
        "nr5OutputMovementDb" {
            if (Test-CandidateLowEvidenceNoiseSuppressed $CandidateReport) {
                return [ordered]@{
                    verdict = "tie"
                    note = "low-evidence-noise-suppression-intentional-level-drop"
                }
            }
        }
        "audioRmsMovementDb" {
            if (Test-CandidateLowEvidenceNoiseSuppressed $CandidateReport) {
                return [ordered]@{
                    verdict = "tie"
                    note = "low-evidence-noise-suppression-intentional-level-drop"
                }
            }
        }
        "rxAudioLevelerOutputRmsMovementDb" {
            if (Test-CandidateLowEvidenceNoiseSuppressed $CandidateReport) {
                return [ordered]@{
                    verdict = "tie"
                    note = "low-evidence-noise-suppression-intentional-level-drop"
                }
            }
        }
        "nr5OutputPeakMaxDbfs" {
            if (Test-SafeCandidateNr5OutputPeak $CandidateReport) {
                return [ordered]@{
                    verdict = "tie"
                    note = "safe-nr5-output-headroom-no-peak-pressure"
                }
            }
        }
        "audioPeakMaxDbfs" {
            if (Test-SafeCandidateAudioHeadroom $CandidateReport) {
                return [ordered]@{
                    verdict = "tie"
                    note = "safe-audio-headroom-no-output-cap"
                }
            }
        }
        "rxAudioLevelerPeakLimitedSampleCount" {
            if ([double]$CandidateValue -le 2.0 -and (Test-SafeCandidateAudioHeadroom $CandidateReport)) {
                return [ordered]@{
                    verdict = "tie"
                    note = "headroom-limited-sample-with-safe-output"
                }
            }
        }
        "rxAudioLevelerConstrainedSampleCount" {
            if ([double]$CandidateValue -le 2.0 -and (Test-SafeCandidateAudioHeadroom $CandidateReport)) {
                return [ordered]@{
                    verdict = "tie"
                    note = "constrained-sample-with-safe-output-headroom"
                }
            }
        }
        "rxAudioLevelerAppliedGainMovementDb" {
            if (Test-StableCandidateLevelerOutput $CandidateReport) {
                return [ordered]@{
                    verdict = "tie"
                    note = "gain-movement-held-final-audio-stable"
                }
            }
        }
        "adcHeadroomMinDb" {
            if (Test-BothTracesHaveSafeAdcHeadroom $BaselineReport $CandidateReport) {
                return [ordered]@{
                    verdict = "tie"
                    note = "adc-headroom-both-safe"
                }
            }
        }
    }

    return [ordered]@{
        verdict = $Verdict
        note = $null
    }
}

function Compare-Metrics {
    param(
        $BaselineReport,
        $CandidateReport,
        [double]$GlobalTolerance,
        $MetricDefinitions = $null,
        [string]$ScenarioId = ""
    )

    $definitions = if ($null -eq $MetricDefinitions) { @(Get-TraceMetricDefinitions) } else { @($MetricDefinitions) }
    $comparisons = New-Object System.Collections.Generic.List[object]
    foreach ($metric in $definitions) {
        $metricId = [string]$metric.id
        $baselineValue = Get-MetricValue $BaselineReport $metricId
        $candidateValue = Get-MetricValue $CandidateReport $metricId
        $definitionThreshold = [double]$metric.threshold
        $threshold = [Math]::Max($definitionThreshold, $GlobalTolerance)
        $verdict = "missing"
        $improvementValue = $null
        $verdictNote = $null

        if ($null -ne $baselineValue -and $null -ne $candidateValue) {
            $verdict = Get-ComparisonVerdict `
                -Direction ([string]$metric.direction) `
                -BaselineValue ([double]$baselineValue) `
                -CandidateValue ([double]$candidateValue) `
                -Threshold $threshold
            $adjusted = Get-AdjustedComparisonVerdict `
                -MetricId $metricId `
                -Verdict $verdict `
                -BaselineReport $BaselineReport `
                -CandidateReport $CandidateReport `
                -CandidateValue ([double]$candidateValue)
            $verdict = [string]$adjusted.verdict
            $verdictNote = $adjusted.note
            $improvementValue = [double]$candidateValue - [double]$baselineValue
            if ([string]$metric.direction -eq "lower") {
                $improvementValue = [double]$baselineValue - [double]$candidateValue
            }
            $improvementValue = [Math]::Round($improvementValue, 3)
        }
        elseif (-not [string]::IsNullOrWhiteSpace($ScenarioId) -and
            (Test-MetricNotApplicableForScenario -MetricId $metricId -ScenarioId $ScenarioId)) {
            $verdict = "not-applicable"
            $verdictNote = "$ScenarioId-no-active-passband-metric"
        }

        $comparisons.Add([ordered]@{
            metricId = $metricId
            label = [string]$metric.label
            direction = [string]$metric.direction
            definitionThreshold = $definitionThreshold
            threshold = $threshold
            safetyClass = [string]$metric.safetyClass
            acceptanceScope = [string]$metric.acceptanceScope
            baselineValue = if ($null -eq $baselineValue) { $null } else { [Math]::Round([double]$baselineValue, 3) }
            candidateValue = if ($null -eq $candidateValue) { $null } else { [Math]::Round([double]$candidateValue, 3) }
            improvementValue = $improvementValue
            verdict = $verdict
            verdictNote = $verdictNote
            ignoredForReadiness = ($verdict -eq "not-applicable")
            rationale = [string]$metric.rationale
        }) | Out-Null
    }

    return @($comparisons.ToArray())
}

function Compare-HardConstraints {
    param(
        $BaselineReport,
        $CandidateReport
    )

    $baseline = Get-CountMap (Get-JsonArray $BaselineReport "hardConstraintCounts")
    $candidate = Get-CountMap (Get-JsonArray $CandidateReport "hardConstraintCounts")
    $keys = @($baseline.Keys + $candidate.Keys | Select-Object -Unique | Sort-Object)
    $comparisons = New-Object System.Collections.Generic.List[object]

    foreach ($key in $keys) {
        $baselineCount = 0
        $candidateCount = 0
        if ($baseline.ContainsKey($key)) { $baselineCount = [int]$baseline[$key] }
        if ($candidate.ContainsKey($key)) { $candidateCount = [int]$candidate[$key] }

        $verdict = "tie"
        if ($candidateCount -gt $baselineCount) {
            $verdict = "regression"
        }
        elseif ($candidateCount -lt $baselineCount) {
            $verdict = "improvement"
        }

        $comparisons.Add([ordered]@{
            constraint = [string]$key
            baselineCount = $baselineCount
            candidateCount = $candidateCount
            delta = $candidateCount - $baselineCount
            verdict = $verdict
        }) | Out-Null
    }

    return @($comparisons.ToArray())
}

function Get-CaptureReadinessWatch {
    param($Report)

    $watch = Get-JsonValue $Report "captureReadinessWatch"
    if ($null -ne $watch) {
        return $watch
    }

    $hardBlockers = Get-NumericValue (Get-JsonValue $Report "hardBlockerSampleCount")
    $failedSamples = Get-NumericValue (Get-JsonValue $Report "failedSampleCount")
    $ready = Test-Truthy (Get-JsonValue $Report "readyForBenchmarkTrace")
    $status = if ($null -ne $failedSamples -and [int]$failedSamples -gt 0) {
        "sample-failures"
    }
    elseif ($null -ne $hardBlockers -and [int]$hardBlockers -gt 0) {
        "blocked-hard-gate"
    }
    elseif ($ready) {
        "ready"
    }
    else {
        "preflight-review"
    }

    return [ordered]@{
        status = $status
        preflightReady = $ready
        hardGatePass = (($null -eq $failedSamples -or [int]$failedSamples -eq 0) -and ($null -eq $hardBlockers -or [int]$hardBlockers -eq 0))
        strictPreflightPass = (($null -eq $failedSamples -or [int]$failedSamples -eq 0) -and ($null -eq $hardBlockers -or [int]$hardBlockers -eq 0) -and $ready)
        topConstraint = $null
        topHardConstraint = $null
        topStatus = $null
    }
}

function Get-ReadinessItemName {
    param($Item)
    return [string](Get-JsonValue $Item "name")
}

function Get-ReadinessItemCount {
    param($Item)
    $value = Get-NumericValue (Get-JsonValue $Item "count")
    if ($null -eq $value) {
        return 0
    }
    return [int]$value
}

function New-CaptureReadinessComparison {
    param(
        $BaselineReport,
        $CandidateReport
    )

    $baseline = Get-CaptureReadinessWatch $BaselineReport
    $candidate = Get-CaptureReadinessWatch $CandidateReport
    $baselineTopConstraint = Get-JsonValue $baseline "topConstraint"
    $candidateTopConstraint = Get-JsonValue $candidate "topConstraint"
    $baselineTopHard = Get-JsonValue $baseline "topHardConstraint"
    $candidateTopHard = Get-JsonValue $candidate "topHardConstraint"
    $baselineTopStatus = Get-JsonValue $baseline "topStatus"
    $candidateTopStatus = Get-JsonValue $candidate "topStatus"

    $baselineHardGatePass = Test-Truthy (Get-JsonValue $baseline "hardGatePass")
    $candidateHardGatePass = Test-Truthy (Get-JsonValue $candidate "hardGatePass")
    $baselineStrictPass = Test-Truthy (Get-JsonValue $baseline "strictPreflightPass")
    $candidateStrictPass = Test-Truthy (Get-JsonValue $candidate "strictPreflightPass")

    return [ordered]@{
        baselineStatus = [string](Get-JsonValue $baseline "status")
        candidateStatus = [string](Get-JsonValue $candidate "status")
        baselineHardGatePass = $baselineHardGatePass
        candidateHardGatePass = $candidateHardGatePass
        hardGatePassDelta = ([int]$candidateHardGatePass - [int]$baselineHardGatePass)
        baselineStrictPreflightPass = $baselineStrictPass
        candidateStrictPreflightPass = $candidateStrictPass
        strictPreflightPassDelta = ([int]$candidateStrictPass - [int]$baselineStrictPass)
        baselineTopConstraintName = Get-ReadinessItemName $baselineTopConstraint
        candidateTopConstraintName = Get-ReadinessItemName $candidateTopConstraint
        baselineTopConstraintCount = Get-ReadinessItemCount $baselineTopConstraint
        candidateTopConstraintCount = Get-ReadinessItemCount $candidateTopConstraint
        topConstraintCountDelta = (Get-ReadinessItemCount $candidateTopConstraint) - (Get-ReadinessItemCount $baselineTopConstraint)
        baselineTopHardConstraintName = Get-ReadinessItemName $baselineTopHard
        candidateTopHardConstraintName = Get-ReadinessItemName $candidateTopHard
        baselineTopHardConstraintCount = Get-ReadinessItemCount $baselineTopHard
        candidateTopHardConstraintCount = Get-ReadinessItemCount $candidateTopHard
        topHardConstraintCountDelta = (Get-ReadinessItemCount $candidateTopHard) - (Get-ReadinessItemCount $baselineTopHard)
        baselineTopStatusName = Get-ReadinessItemName $baselineTopStatus
        candidateTopStatusName = Get-ReadinessItemName $candidateTopStatus
        baselineTopStatusCount = Get-ReadinessItemCount $baselineTopStatus
        candidateTopStatusCount = Get-ReadinessItemCount $candidateTopStatus
    }
}

function New-MetricSafetyClassCounts {
    param(
        $Items,
        [string]$Verdict = ""
    )

    $counts = @{}
    foreach ($item in @($Items)) {
        if (-not [string]::IsNullOrWhiteSpace($Verdict) -and
            [string](Get-JsonValue $item "verdict") -ne $Verdict) {
            continue
        }

        $safetyClass = [string](Get-JsonValue $item "safetyClass")
        if ([string]::IsNullOrWhiteSpace($safetyClass)) {
            $safetyClass = "unknown"
        }

        if (-not $counts.ContainsKey($safetyClass)) {
            $counts[$safetyClass] = 0
        }
        $counts[$safetyClass] = [int]$counts[$safetyClass] + 1
    }

    $result = New-Object System.Collections.Generic.List[object]
    foreach ($safetyClass in @($counts.Keys | Sort-Object)) {
        $result.Add([ordered]@{
            safetyClass = [string]$safetyClass
            count = [int]$counts[$safetyClass]
        }) | Out-Null
    }

    return @($result.ToArray())
}

function Build-MarkdownReport {
    param($Report)

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# DSP Live Diagnostics Trace Comparison") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add("- Ready for review: $($Report.readyForReview)") | Out-Null
    $lines.Add("- Candidate: $($Report.candidateLabel)") | Out-Null
    $lines.Add("- Baseline: $($Report.baselineLabel)") | Out-Null
    $lines.Add("- Regressions: $($Report.regressionCount)") | Out-Null
    $lines.Add("- Gate failures: $($Report.gateFailureCount)") | Out-Null
    $lines.Add("- Missing values: $($Report.missingMetricValueCount)") | Out-Null
    $lines.Add("- Not applicable values: $($Report.notApplicableMetricValueCount)") | Out-Null
    $lines.Add("") | Out-Null

    $captureReadiness = Get-JsonValue $Report "captureReadinessComparison"
    if ($null -ne $captureReadiness) {
        $lines.Add("## Capture Readiness Summary") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("| Metric | Baseline | Candidate | Delta |") | Out-Null
        $lines.Add("|---|---|---|---:|") | Out-Null
        $lines.Add("| Status | $($captureReadiness.baselineStatus) | $($captureReadiness.candidateStatus) | |") | Out-Null
        $lines.Add("| Hard-gate pass | $($captureReadiness.baselineHardGatePass) | $($captureReadiness.candidateHardGatePass) | $($captureReadiness.hardGatePassDelta) |") | Out-Null
        $lines.Add("| Strict preflight pass | $($captureReadiness.baselineStrictPreflightPass) | $($captureReadiness.candidateStrictPreflightPass) | $($captureReadiness.strictPreflightPassDelta) |") | Out-Null
        $lines.Add("| Top constraint | $($captureReadiness.baselineTopConstraintName) ($($captureReadiness.baselineTopConstraintCount)) | $($captureReadiness.candidateTopConstraintName) ($($captureReadiness.candidateTopConstraintCount)) | $($captureReadiness.topConstraintCountDelta) |") | Out-Null
        $lines.Add("| Top hard gate | $($captureReadiness.baselineTopHardConstraintName) ($($captureReadiness.baselineTopHardConstraintCount)) | $($captureReadiness.candidateTopHardConstraintName) ($($captureReadiness.candidateTopHardConstraintCount)) | $($captureReadiness.topHardConstraintCountDelta) |") | Out-Null
        $lines.Add("| Top status | $($captureReadiness.baselineTopStatusName) ($($captureReadiness.baselineTopStatusCount)) | $($captureReadiness.candidateTopStatusName) ($($captureReadiness.candidateTopStatusCount)) | |") | Out-Null
        $lines.Add("") | Out-Null
    }

    $weakSignal = Get-JsonValue $Report "nr5WeakSignalComparison"
    if ($null -ne $weakSignal -and
        ([int](Get-JsonValue $weakSignal "baselineWeakInputSampleCount") -gt 0 -or
            [int](Get-JsonValue $weakSignal "candidateWeakInputSampleCount") -gt 0 -or
            [double](Get-NumericValue (Get-JsonValue $weakSignal "baselineArtifactRiskScore")) -gt 0.0 -or
            [double](Get-NumericValue (Get-JsonValue $weakSignal "candidateArtifactRiskScore")) -gt 0.0)) {
        $lines.Add("## NR5 Weak-Signal Summary") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("| Metric | Baseline | Candidate | Delta |") | Out-Null
        $lines.Add("|---|---:|---:|---:|") | Out-Null
        $lines.Add("| Weak input samples | $($weakSignal.baselineWeakInputSampleCount) | $($weakSignal.candidateWeakInputSampleCount) | $($weakSignal.weakInputSampleDelta) |") | Out-Null
        $lines.Add("| Weak recovered samples | $($weakSignal.baselineWeakRecoveredSampleCount) | $($weakSignal.candidateWeakRecoveredSampleCount) | $($weakSignal.weakRecoveredSampleDelta) |") | Out-Null
        $lines.Add("| Strict weak dropout samples | $($weakSignal.baselineWeakDropoutSampleCount) | $($weakSignal.candidateWeakDropoutSampleCount) | $($weakSignal.weakDropoutSampleDelta) |") | Out-Null
        $lines.Add("| Candidate weak-loss samples | $($weakSignal.baselineCandidateWeakLossSampleCount) | $($weakSignal.candidateCandidateWeakLossSampleCount) | $($weakSignal.candidateWeakLossSampleDelta) |") | Out-Null
        $lines.Add("| Final-audible strict dropouts | $($weakSignal.baselineFinalAudibleWeakDropoutSampleCount) | $($weakSignal.candidateFinalAudibleWeakDropoutSampleCount) | $($weakSignal.finalAudibleWeakDropoutSampleDelta) |") | Out-Null
        $lines.Add("| Final-audible strict dropout percent | $($weakSignal.baselineFinalAudibleWeakDropoutPct) | $($weakSignal.candidateFinalAudibleWeakDropoutPct) | $($weakSignal.finalAudibleWeakDropoutPctDelta) |") | Out-Null
        $lines.Add("| Hot makeup samples | $($weakSignal.baselineHotMakeupSampleCount) | $($weakSignal.candidateHotMakeupSampleCount) | $($weakSignal.hotMakeupSampleDelta) |") | Out-Null
        $lines.Add("| Weak recovery percent | $($weakSignal.baselineWeakRecoveryPct) | $($weakSignal.candidateWeakRecoveryPct) | $($weakSignal.weakRecoveryPctDelta) |") | Out-Null
        $lines.Add("| Output movement dB | $($weakSignal.baselineOutputMovementDb) | $($weakSignal.candidateOutputMovementDb) | $($weakSignal.outputMovementDbDelta) |") | Out-Null
        $lines.Add("| Makeup movement dB | $($weakSignal.baselineMakeupMovementDb) | $($weakSignal.candidateMakeupMovementDb) | $($weakSignal.makeupMovementDbDelta) |") | Out-Null
        $lines.Add("| Makeup max dB | $($weakSignal.baselineMakeupMaxDb) | $($weakSignal.candidateMakeupMaxDb) | $($weakSignal.makeupMaxDbDelta) |") | Out-Null
        $lines.Add("| Recovery-drive movement | $($weakSignal.baselineRecoveryDriveMovement) | $($weakSignal.candidateRecoveryDriveMovement) | $($weakSignal.recoveryDriveMovementDelta) |") | Out-Null
        $lines.Add("| Texture-fill average | $($weakSignal.baselineTextureFillAverage) | $($weakSignal.candidateTextureFillAverage) | $($weakSignal.textureFillAverageDelta) |") | Out-Null
        $lines.Add("| Low-evidence lifted samples | $($weakSignal.baselineLowEvidenceLiftedSampleCount) | $($weakSignal.candidateLowEvidenceLiftedSampleCount) | $($weakSignal.lowEvidenceLiftedSampleDelta) |") | Out-Null
        $lines.Add("| Low-evidence lifted percent | $($weakSignal.baselineLowEvidenceLiftedPct) | $($weakSignal.candidateLowEvidenceLiftedPct) | $($weakSignal.lowEvidenceLiftedPctDelta) |") | Out-Null
        $lines.Add("| Audio-alignment mismatch percent | $($weakSignal.baselineAudioAlignmentMismatchPct) | $($weakSignal.candidateAudioAlignmentMismatchPct) | $($weakSignal.audioAlignmentMismatchPctDelta) |") | Out-Null
        $lines.Add("| Artifact-risk score | $($weakSignal.baselineArtifactRiskScore) | $($weakSignal.candidateArtifactRiskScore) | $($weakSignal.artifactRiskScoreDelta) |") | Out-Null
        $lines.Add("") | Out-Null
    }

    $leveler = Get-JsonValue $Report "rxAudioLevelerComparison"
    if ($null -ne $leveler -and
        ($null -ne (Get-JsonValue $leveler "baselineDiagnosticSampleCount") -or
            $null -ne (Get-JsonValue $leveler "candidateDiagnosticSampleCount"))) {
        $lines.Add("## RX Audio Leveler Summary") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("| Metric | Baseline | Candidate | Delta |") | Out-Null
        $lines.Add("|---|---:|---:|---:|") | Out-Null
        $lines.Add("| Diagnostic samples | $($leveler.baselineDiagnosticSampleCount) | $($leveler.candidateDiagnosticSampleCount) | |") | Out-Null
        $lines.Add("| Constrained samples | $($leveler.baselineConstrainedSampleCount) | $($leveler.candidateConstrainedSampleCount) | $($leveler.constrainedSampleDelta) |") | Out-Null
        $lines.Add("| Constrained percent | $($leveler.baselineConstrainedPct) | $($leveler.candidateConstrainedPct) | $($leveler.constrainedPctDelta) |") | Out-Null
        $lines.Add("| Boost-slew limited samples | $($leveler.baselineBoostSlewLimitedSampleCount) | $($leveler.candidateBoostSlewLimitedSampleCount) | $($leveler.boostSlewLimitedSampleDelta) |") | Out-Null
        $lines.Add("| Peak-limited samples | $($leveler.baselinePeakLimitedSampleCount) | $($leveler.candidatePeakLimitedSampleCount) | $($leveler.peakLimitedSampleDelta) |") | Out-Null
        $lines.Add("| Output-limited samples | $($leveler.baselineOutputLimitedSampleCount) | $($leveler.candidateOutputLimitedSampleCount) | $($leveler.outputLimitedSampleDelta) |") | Out-Null
        $lines.Add("| Output RMS movement dB | $($leveler.baselineOutputRmsMovementDb) | $($leveler.candidateOutputRmsMovementDb) | $($leveler.outputRmsMovementDbDelta) |") | Out-Null
        $lines.Add("| Applied gain movement dB | $($leveler.baselineAppliedGainMovementDb) | $($leveler.candidateAppliedGainMovementDb) | $($leveler.appliedGainMovementDbDelta) |") | Out-Null
        $lines.Add("") | Out-Null

        $topBoost = @(Get-JsonArray $leveler "candidateTopBoostSlewLimitedSamples")
        $topPeak = @(Get-JsonArray $leveler "candidateTopPeakLimitedSamples")
        $topOutput = @(Get-JsonArray $leveler "candidateTopOutputLimitedSamples")
        if (($topBoost.Count + $topPeak.Count + $topOutput.Count) -gt 0) {
            $lines.Add("### Candidate Leveler Constrained Samples") | Out-Null
            $lines.Add("") | Out-Null
            $lines.Add("| Type | Sample | Gain delta dB | Peak headroom dB | Output RMS dBFS | Output peak dBFS |") | Out-Null
            $lines.Add("|---|---:|---:|---:|---:|---:|") | Out-Null
            foreach ($sample in $topBoost) {
                $lines.Add("| Boost slew | $($sample.sampleIndex) | $($sample.gainDeltaDb) | $($sample.peakHeadroomDb) | $($sample.outputRmsDbfs) | $($sample.outputPeakDbfs) |") | Out-Null
            }
            foreach ($sample in $topPeak) {
                $lines.Add("| Peak headroom | $($sample.sampleIndex) | $($sample.gainDeltaDb) | $($sample.peakHeadroomDb) | $($sample.outputRmsDbfs) | $($sample.outputPeakDbfs) |") | Out-Null
            }
            foreach ($sample in $topOutput) {
                $lines.Add("| Output cap | $($sample.sampleIndex) | $($sample.gainDeltaDb) | $($sample.peakHeadroomDb) | $($sample.outputRmsDbfs) | $($sample.outputPeakDbfs) |") | Out-Null
            }
            $lines.Add("") | Out-Null
        }
    }

    $regressions = @($Report.metricComparisons | Where-Object { $_.verdict -eq "regression" })
    if ($regressions.Count -gt 0) {
        $safetyCounts = @(Get-JsonArray $Report "metricRegressionSafetyClassCounts")
        if ($safetyCounts.Count -gt 0) {
            $lines.Add("## Regression Safety Classes") | Out-Null
            $lines.Add("") | Out-Null
            $lines.Add("| Safety class | Regressions |") | Out-Null
            $lines.Add("|---|---:|") | Out-Null
            foreach ($item in $safetyCounts) {
                $lines.Add("| $($item.safetyClass) | $($item.count) |") | Out-Null
            }
            $lines.Add("") | Out-Null
        }

        $lines.Add("## Metric Regressions") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("| Metric | Baseline | Candidate | Direction | Delta |") | Out-Null
        $lines.Add("|---|---:|---:|---|---:|") | Out-Null
        foreach ($item in $regressions) {
            $lines.Add("| $($item.label) | $($item.baselineValue) | $($item.candidateValue) | $($item.direction) | $($item.improvementValue) |") | Out-Null
        }
        $lines.Add("") | Out-Null
    }

    $constraintRegressions = @($Report.hardConstraintComparisons | Where-Object { $_.verdict -eq "regression" })
    if ($constraintRegressions.Count -gt 0) {
        $lines.Add("## Hard Constraint Regressions") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("| Constraint | Baseline | Candidate | Delta |") | Out-Null
        $lines.Add("|---|---:|---:|---:|") | Out-Null
        foreach ($item in $constraintRegressions) {
            $lines.Add("| $($item.constraint) | $($item.baselineCount) | $($item.candidateCount) | $($item.delta) |") | Out-Null
        }
        $lines.Add("") | Out-Null
    }

    $lines.Add("## Metric Summary") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add("| Metric | Baseline | Candidate | Direction | Verdict |") | Out-Null
    $lines.Add("|---|---:|---:|---|---|") | Out-Null
    foreach ($item in @($Report.metricComparisons)) {
        $lines.Add("| $($item.label) | $($item.baselineValue) | $($item.candidateValue) | $($item.direction) | $($item.verdict) |") | Out-Null
    }

    return @($lines.ToArray())
}

$baselineInput = Resolve-InputReport $BaselinePath
$candidateInput = Resolve-InputReport $CandidatePath
$baselineReport = $baselineInput.report
$candidateReport = $candidateInput.report

$captureReadinessComparison = New-CaptureReadinessComparison $baselineReport $candidateReport
$nr5WeakSignalComparison = New-Nr5WeakSignalComparison $baselineReport $candidateReport
$rxAudioLevelerComparison = New-RxAudioLevelerComparison $baselineReport $candidateReport
$traceMetricDefinitions = @(Get-TraceMetricDefinitions)
$baselineScenarioId = Get-ReportScenarioId $baselineReport
$candidateScenarioId = Get-ReportScenarioId $candidateReport
$comparisonScenarioId = Get-ComparisonScenarioId $baselineReport $candidateReport
$metricComparisons = Compare-Metrics $baselineReport $candidateReport $Tolerance $traceMetricDefinitions $comparisonScenarioId
$hardConstraintComparisons = Compare-HardConstraints $baselineReport $candidateReport

$regressionCount = @($metricComparisons | Where-Object { $_.verdict -eq "regression" }).Count
$improvementCount = @($metricComparisons | Where-Object { $_.verdict -eq "improvement" }).Count
$tieCount = @($metricComparisons | Where-Object { $_.verdict -eq "tie" }).Count
$informationalCount = @($metricComparisons | Where-Object { $_.verdict -eq "informational" }).Count
$missingCount = @($metricComparisons | Where-Object { $_.verdict -eq "missing" }).Count
$notApplicableCount = @($metricComparisons | Where-Object { $_.verdict -eq "not-applicable" }).Count
$hardConstraintRegressionCount = @($hardConstraintComparisons | Where-Object { $_.verdict -eq "regression" }).Count
$metricRegressionSafetyClassCounts = New-MetricSafetyClassCounts -Items $metricComparisons -Verdict "regression"
$metricMissingSafetyClassCounts = New-MetricSafetyClassCounts -Items $metricComparisons -Verdict "missing"
$metricNotApplicableSafetyClassCounts = New-MetricSafetyClassCounts -Items $metricComparisons -Verdict "not-applicable"

$candidateOkSamples = [int](Get-NumericValue (Get-JsonValue $candidateReport "okSampleCount"))
$candidateFailedSamples = [int](Get-NumericValue (Get-JsonValue $candidateReport "failedSampleCount"))
$candidateHardBlockers = [int](Get-NumericValue (Get-JsonValue $candidateReport "hardBlockerSampleCount"))
$candidateReadyTrace = Test-Truthy (Get-JsonValue $candidateReport "readyForBenchmarkTrace")

$gateFailureCount = 0
if ($candidateOkSamples -le 0) { $gateFailureCount++ }
if ($candidateFailedSamples -gt 0) { $gateFailureCount++ }
if ($candidateHardBlockers -gt 0) { $gateFailureCount++ }
if (-not $candidateReadyTrace) { $gateFailureCount++ }

$readyForReview = ($regressionCount -eq 0 -and
    $hardConstraintRegressionCount -eq 0 -and
    $gateFailureCount -eq 0 -and
    $missingCount -eq 0)

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $candidateDir = Split-Path -Parent $candidateInput.path
    if ([string]::IsNullOrWhiteSpace($candidateDir)) {
        $candidateDir = (Get-Location).Path
    }
    $ReportPath = Join-Path $candidateDir "dsp-live-diagnostics-trace-comparison.json"
}

if (-not $NoMarkdown -and [string]::IsNullOrWhiteSpace($MarkdownPath)) {
    $reportDir = Split-Path -Parent $ReportPath
    $reportName = [System.IO.Path]::GetFileNameWithoutExtension($ReportPath)
    $MarkdownPath = Join-Path $reportDir "$reportName.md"
}

$bundlePath = ""
if (-not [string]::IsNullOrWhiteSpace($BundleDir)) {
    $bundlePath = (Resolve-Path -LiteralPath $BundleDir).Path
}

$recommendations = if ($readyForReview -and $notApplicableCount -gt 0) {
    @(
        "Store this comparison as scenario-specific noise-only gating evidence; not-applicable active/passband metrics still require separate weak/strong passband traces before any DSP default change.",
        "Pair this report with passband speech, adjacent-signal, AGC-pumping, and operator/on-air evidence before graduating the candidate beyond opt-in use."
    )
}
elseif ($readyForReview) {
    @("Store this comparison with the candidate trace, offline fixture metrics, audio renders, spectrum captures, and operator notes before considering any DSP default change.")
}
elseif ($regressionCount -gt 0 -or $hardConstraintRegressionCount -gt 0 -or $gateFailureCount -gt 0) {
    $items = New-Object System.Collections.Generic.List[string]
    if (-not (Test-Truthy (Get-JsonValue $captureReadinessComparison "candidateHardGatePass"))) {
        $topHardName = [string](Get-JsonValue $captureReadinessComparison "candidateTopHardConstraintName")
        if (-not [string]::IsNullOrWhiteSpace($topHardName)) {
            $items.Add("Candidate capture is hard-gated by '$topHardName'; clear that setup issue before treating DSP metric regressions as algorithm behavior.") | Out-Null
        }
    }
    elseif (-not (Test-Truthy (Get-JsonValue $captureReadinessComparison "candidateStrictPreflightPass"))) {
        $topConstraintName = [string](Get-JsonValue $captureReadinessComparison "candidateTopConstraintName")
        if (-not [string]::IsNullOrWhiteSpace($topConstraintName)) {
            $items.Add("Candidate capture has preflight advisory '$topConstraintName'; clear or document it before final acceptance review.") | Out-Null
        }
    }
    $items.Add("Do not graduate this DSP candidate; resolve live diagnostics trace regressions or gate failures before on-air acceptance.") | Out-Null
    $items.Add("Pair this report with offline fixture metrics to distinguish real DSP regressions from capture setup problems.") | Out-Null
    @($items.ToArray())
}
elseif ($missingCount -gt 0) {
    @(
        "No live diagnostics regressions or gate failures were found, but the comparison is not graduation-ready because one side does not expose every metric.",
        "Use this as cross-mode evidence only; pair it with same-mode candidate/baseline traces before accepting a DSP default change."
    )
}
else {
    @("Review this comparison with offline fixture metrics and operator notes before considering any DSP default change.")
}

$report = [ordered]@{
    schemaVersion = 1
    tool = "compare-dsp-live-diagnostics-traces"
    generatedUtc = [DateTimeOffset]::UtcNow
    baselineLabel = $BaselineLabel
    candidateLabel = $CandidateLabel
    bundleRelativePaths = (-not [string]::IsNullOrWhiteSpace($bundlePath))
    baselinePath = ConvertTo-PortablePath -Root $bundlePath -Path $baselineInput.path
    baselineInputSha256 = Get-FileSha256 $baselineInput.path
    candidatePath = ConvertTo-PortablePath -Root $bundlePath -Path $candidateInput.path
    candidateInputSha256 = Get-FileSha256 $candidateInput.path
    baselineSummarizedFromJsonl = [bool]$baselineInput.summarizedFromJsonl
    candidateSummarizedFromJsonl = [bool]$candidateInput.summarizedFromJsonl
    reportPath = ConvertTo-PortablePath -Root $bundlePath -Path $ReportPath
    markdownPath = if ($NoMarkdown) { $null } else { ConvertTo-PortablePath -Root $bundlePath -Path $MarkdownPath }
    tolerance = $Tolerance
    readyForReview = $readyForReview
    baselineScenarioId = $baselineScenarioId
    candidateScenarioId = $candidateScenarioId
    comparisonScenarioId = $comparisonScenarioId
    baselineTrendStatus = [string](Get-JsonValue $baselineReport "trendStatus")
    candidateTrendStatus = [string](Get-JsonValue $candidateReport "trendStatus")
    candidateReadyForBenchmarkTrace = $candidateReadyTrace
    candidateOkSampleCount = $candidateOkSamples
    candidateFailedSampleCount = $candidateFailedSamples
    candidateHardBlockerSampleCount = $candidateHardBlockers
    metricDefinitionSource = "compare-dsp-live-diagnostics-traces"
    metricDefinitionCount = @($traceMetricDefinitions).Count
    metricDefinitions = @($traceMetricDefinitions)
    metricComparisonCount = @($metricComparisons).Count
    improvementCount = $improvementCount
    regressionCount = $regressionCount
    hardConstraintRegressionCount = $hardConstraintRegressionCount
    gateFailureCount = $gateFailureCount
    tieCount = $tieCount
    informationalMetricCount = $informationalCount
    missingMetricValueCount = $missingCount
    notApplicableMetricValueCount = $notApplicableCount
    captureReadinessComparison = $captureReadinessComparison
    nr5WeakSignalComparison = $nr5WeakSignalComparison
    rxAudioLevelerComparison = $rxAudioLevelerComparison
    metricRegressionSafetyClassCounts = @($metricRegressionSafetyClassCounts)
    metricMissingSafetyClassCounts = @($metricMissingSafetyClassCounts)
    metricNotApplicableSafetyClassCounts = @($metricNotApplicableSafetyClassCounts)
    metricComparisons = @($metricComparisons)
    hardConstraintComparisons = @($hardConstraintComparisons)
    recommendations = @($recommendations)
}

Write-JsonFile -Path $ReportPath -Value $report

if (-not $NoMarkdown) {
    $markdown = Build-MarkdownReport $report
    Set-Content -LiteralPath $MarkdownPath -Value $markdown -Encoding UTF8
}

if ($JsonOnly) {
    $report | ConvertTo-Json -Depth 48
}
else {
    if ($readyForReview) {
        Write-Host "DSP live diagnostics trace comparison passed: $CandidatePath"
    }
    else {
        Write-Host "DSP live diagnostics trace comparison found issues: $CandidatePath"
    }
    Write-Host "Report: $ReportPath"
    if (-not $NoMarkdown) {
        Write-Host "Markdown: $MarkdownPath"
    }
    Write-Host "Regressions: $regressionCount, Hard constraint regressions: $hardConstraintRegressionCount, Gate failures: $gateFailureCount, Missing values: $missingCount"
}

if ($FailOnRegression -and -not $readyForReview) {
    exit 1
}
