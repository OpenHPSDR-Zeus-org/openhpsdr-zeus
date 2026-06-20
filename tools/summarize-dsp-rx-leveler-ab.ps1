param(
    [Parameter(Mandatory = $true)]
    [string]$SummaryPath,

    [string]$ReportPath = "",

    [string]$MarkdownPath = "",

    [string]$BaselineLabel = "current",

    [string]$CandidateLabel = "",

    [string]$BundleDir = "",

    [switch]$AllowInactiveAudio,

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
        ($Value | ConvertTo-Json -Depth 64),
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

function Get-NumericValue {
    param($Value)

    if ($null -eq $Value) {
        return $null
    }
    if ($Value -is [bool]) {
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
    if ([double]::TryParse($text, [ref]$parsed)) {
        return $parsed
    }

    return $null
}

function Get-StatValue {
    param(
        $Object,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if ($null -eq $Object) {
        return $null
    }

    $value = Get-NumericValue (Get-JsonValue $Object $Name)
    if ($null -ne $value) {
        return $value
    }

    if ([string]::Equals($Name, "average", [StringComparison]::OrdinalIgnoreCase)) {
        return Get-NumericValue (Get-JsonValue $Object "avg")
    }

    return $null
}

function ConvertTo-JsonMetricNumber {
    param($Value)

    if ($null -eq $Value) {
        return $null
    }

    $number = [double]$Value
    if ([double]::IsPositiveInfinity($number)) {
        return "Infinity"
    }
    if ([double]::IsNegativeInfinity($number)) {
        return "-Infinity"
    }
    if ([double]::IsNaN($number)) {
        return $null
    }

    return $number
}

function Get-MetricComparison {
    param(
        $Comparison,
        [Parameter(Mandatory = $true)][string]$MetricId
    )

    foreach ($metric in @(Get-JsonValue $Comparison "metricComparisons")) {
        if ([string]::Equals([string](Get-JsonValue $metric "metricId"), $MetricId, [StringComparison]::OrdinalIgnoreCase)) {
            return $metric
        }
    }

    return $null
}

function New-RxLevelerPassbandEvidence {
    param(
        $Comparison,
        [Parameter(Mandatory = $true)][string]$BaselineProfile,
        [Parameter(Mandatory = $true)][string]$CandidateProfile
    )

    $passbandMetricIds = @(
        "passbandPeakSampleCount",
        "passbandActiveAudioPct",
        "passbandFloorAudioPct",
        "passbandAudioAverageDbfs",
        "passbandAudioMovementDb",
        "passbandNoiseSeparationDb"
    )

    $peakMetric = Get-MetricComparison -Comparison $Comparison -MetricId "passbandPeakSampleCount"
    $baselinePeakCount = Get-NumericValue (Get-JsonValue $peakMetric "baselineValue")
    $candidatePeakCount = Get-NumericValue (Get-JsonValue $peakMetric "candidateValue")
    $baselineHasPeak = ($null -ne $baselinePeakCount -and [double]$baselinePeakCount -gt 0.0)
    $candidateHasPeak = ($null -ne $candidatePeakCount -and [double]$candidatePeakCount -gt 0.0)

    $missingProfiles = New-Object System.Collections.Generic.List[string]
    if (-not $baselineHasPeak) {
        $missingProfiles.Add($BaselineProfile) | Out-Null
    }
    if (-not $candidateHasPeak) {
        $missingProfiles.Add($CandidateProfile) | Out-Null
    }

    $missingMetricIds = New-Object System.Collections.Generic.List[string]
    foreach ($metricId in $passbandMetricIds) {
        $metric = Get-MetricComparison -Comparison $Comparison -MetricId $metricId
        if ($null -eq $metric -or [string](Get-JsonValue $metric "verdict") -eq "missing") {
            $missingMetricIds.Add($metricId) | Out-Null
        }
    }

    $ready = ($missingProfiles.Count -eq 0 -and $missingMetricIds.Count -eq 0)
    $status = if ($ready) {
        "ready"
    }
    elseif ($missingProfiles.Count -gt 0) {
        "passband-peaks-missing"
    }
    else {
        "passband-metrics-missing"
    }

    return [ordered]@{
        required = $true
        ready = $ready
        status = $status
        baselineProfile = $BaselineProfile
        candidateProfile = $CandidateProfile
        baselinePassbandPeakSampleCount = $baselinePeakCount
        candidatePassbandPeakSampleCount = $candidatePeakCount
        missingProfiles = @($missingProfiles.ToArray())
        missingMetricIds = @($missingMetricIds.ToArray())
        recommendation = "Use active-audio RX leveler A/B traces as workflow/safety evidence only until frontend tuned passband peaks are present in both current and candidate windows."
    }
}

function New-RxLevelerCandidateControlMemoryEvidence {
    param(
        $CandidateReport,
        [Parameter(Mandatory = $true)][string]$CandidateProfile
    )

    $watch = Get-JsonValue $CandidateReport "rxAudioLevelerWatch"
    $inputStats = Get-JsonValue $CandidateReport "rxAudioLevelerInputRmsDbfs"
    $inputAverageDbfs = Get-StatValue $inputStats "average"
    $inputMinDbfs = Get-StatValue $inputStats "min"
    $inputMaxDbfs = Get-StatValue $inputStats "max"
    $controlRmsValidSampleCount = Get-NumericValue (Get-JsonValue $watch "controlRmsValidSampleCount")
    $experimentalSampleCount = Get-NumericValue (Get-JsonValue $watch "experimentalSampleCount")
    $normalStrengthControlRmsValidSampleCount = Get-NumericValue (Get-JsonValue $watch "normalStrengthControlRmsValidSampleCount")
    $normalStrengthInputThresholdDbfs = Get-NumericValue (Get-JsonValue $watch "normalStrengthControlRmsThresholdDbfs")
    if ($null -eq $controlRmsValidSampleCount) {
        $controlRmsValidSampleCount = 0.0
    }
    if ($null -eq $experimentalSampleCount) {
        $experimentalSampleCount = 0.0
    }
    if ($null -eq $normalStrengthControlRmsValidSampleCount) {
        $normalStrengthControlRmsValidSampleCount = 0.0
    }
    if ($null -eq $normalStrengthInputThresholdDbfs) {
        $normalStrengthInputThresholdDbfs = -24.0
    }

    $normalStrengthInput = ($null -ne $inputAverageDbfs -and [double]$inputAverageDbfs -ge $normalStrengthInputThresholdDbfs)
    $status = "control-memory-inactive"
    $ready = $true

    if ([double]$normalStrengthControlRmsValidSampleCount -gt 0.0) {
        $status = "normal-strength-control-memory-leak"
        $ready = $false
    }
    elseif ([double]$controlRmsValidSampleCount -gt 0.0) {
        if ($null -eq $inputAverageDbfs) {
            $status = "candidate-input-rms-missing"
            $ready = $false
        }
        elseif ($normalStrengthInput) {
            $status = "weak-dip-control-memory-active"
        }
        else {
            $status = "weak-signal-control-memory-active"
        }
    }

    return [ordered]@{
        required = $true
        ready = $ready
        status = $status
        candidateProfile = $CandidateProfile
        normalStrengthInputThresholdDbfs = $normalStrengthInputThresholdDbfs
        normalStrengthInput = $normalStrengthInput
        candidateExperimentalSampleCount = $experimentalSampleCount
        controlRmsValidSampleCount = $controlRmsValidSampleCount
        normalStrengthControlRmsValidSampleCount = $normalStrengthControlRmsValidSampleCount
        inputRmsAverageDbfs = $inputAverageDbfs
        inputRmsMinDbfs = $inputMinDbfs
        inputRmsMaxDbfs = $inputMaxDbfs
        topNormalStrengthControlRmsSamples = @(Get-JsonValue $watch "topNormalStrengthControlRmsSamples")
        recommendation = "Normal-strength blocks must not engage stable-speech control memory; weak dips inside an otherwise normal trace are allowed only when normalStrengthControlRmsValidSampleCount stays zero."
    }
}

function New-RxLevelerOptimizationEvidence {
    param($Comparison)

    $leveler = Get-JsonValue $Comparison "rxAudioLevelerComparison"
    $improvementThresholdDb = 0.5
    $outputMovementDelta = Get-NumericValue (Get-JsonValue $leveler "outputRmsMovementDbDelta")
    $gainMovementDelta = Get-NumericValue (Get-JsonValue $leveler "appliedGainMovementDbDelta")
    $constraintSeverityDelta = Get-NumericValue (Get-JsonValue $leveler "constrainedMaxAbsGainDeltaDbDelta")
    $peakLimitedDelta = Get-NumericValue (Get-JsonValue $leveler "peakLimitedSampleDelta")
    $outputLimitedDelta = Get-NumericValue (Get-JsonValue $leveler "outputLimitedSampleDelta")
    $candidatePeakHeadroomMin = Get-NumericValue (Get-JsonValue $leveler "candidateConstrainedPeakHeadroomMinDb")
    $candidatePreLimitPeakMax = Get-NumericValue (Get-JsonValue $leveler "candidateConstrainedPreLimitPeakMaxDbfs")
    $movementImproved = (
        ($null -ne $outputMovementDelta -and [double]$outputMovementDelta -le -$improvementThresholdDb) -or
        ($null -ne $gainMovementDelta -and [double]$gainMovementDelta -le -$improvementThresholdDb))
    $constraintSeverityBenign = ($null -ne $constraintSeverityDelta -and [double]$constraintSeverityDelta -le 0.5)
    $peakGuardBenign = (
        $movementImproved -and
        $constraintSeverityBenign -and
        $null -ne $peakLimitedDelta -and [double]$peakLimitedDelta -gt 0.0 -and
        ($null -eq $outputLimitedDelta -or [double]$outputLimitedDelta -le 0.0) -and
        $null -ne $candidatePeakHeadroomMin -and [double]$candidatePeakHeadroomMin -ge 1.0 -and
        $null -ne $candidatePreLimitPeakMax -and [double]$candidatePreLimitPeakMax -le -2.0)
    $limiterRegression = (
        ($null -ne $peakLimitedDelta -and [double]$peakLimitedDelta -gt 0.0 -and -not $peakGuardBenign) -or
        ($null -ne $outputLimitedDelta -and [double]$outputLimitedDelta -gt 0.0))
    $constraintCountRegressionThreshold = if ($movementImproved -and $constraintSeverityBenign -and -not $limiterRegression) {
        [double]::PositiveInfinity
    }
    else {
        0.0
    }
    $constraints = @(
        @{ Name = "constrainedSample"; Delta = Get-NumericValue (Get-JsonValue $leveler "constrainedSampleDelta"); ImprovementThreshold = -1.0; RegressionThreshold = $constraintCountRegressionThreshold },
        @{ Name = "constrainedPct"; Delta = Get-NumericValue (Get-JsonValue $leveler "constrainedPctDelta"); ImprovementThreshold = -1.0; RegressionThreshold = if ([double]::IsPositiveInfinity($constraintCountRegressionThreshold)) { [double]::PositiveInfinity } else { 1.0 } },
        @{ Name = "boostSlewLimitedSample"; Delta = Get-NumericValue (Get-JsonValue $leveler "boostSlewLimitedSampleDelta"); ImprovementThreshold = -1.0; RegressionThreshold = $constraintCountRegressionThreshold },
        @{ Name = "constrainedMaxAbsGainDeltaDb"; Delta = $constraintSeverityDelta; ImprovementThreshold = -$improvementThresholdDb; RegressionThreshold = 1.0 },
        @{ Name = "peakLimitedSample"; Delta = $peakLimitedDelta; ImprovementThreshold = -1.0; RegressionThreshold = if ($peakGuardBenign) { [double]::PositiveInfinity } else { 0.0 } },
        @{ Name = "outputLimitedSample"; Delta = $outputLimitedDelta; ImprovementThreshold = -1.0; RegressionThreshold = 0.0 },
        @{ Name = "outputRmsMovementDb"; Delta = $outputMovementDelta; ImprovementThreshold = -$improvementThresholdDb; RegressionThreshold = 1.0 },
        @{ Name = "appliedGainMovementDb"; Delta = $gainMovementDelta; ImprovementThreshold = -$improvementThresholdDb; RegressionThreshold = 1.0 }
    )

    $available = New-Object System.Collections.Generic.List[object]
    $improvements = New-Object System.Collections.Generic.List[object]
    $regressions = New-Object System.Collections.Generic.List[object]
    foreach ($item in $constraints) {
        if ($null -eq $item.Delta) {
            continue
        }

        $record = [ordered]@{
            metric = [string]$item.Name
            delta = [Math]::Round([double]$item.Delta, 3)
            improvementThreshold = ConvertTo-JsonMetricNumber $item.ImprovementThreshold
            regressionThreshold = ConvertTo-JsonMetricNumber $item.RegressionThreshold
        }
        $available.Add($record) | Out-Null
        if ([double]$item.Delta -le [double]$item.ImprovementThreshold) {
            $improvements.Add($record) | Out-Null
        }
        if ([double]$item.Delta -gt [double]$item.RegressionThreshold) {
            $regressions.Add($record) | Out-Null
        }
    }

    $comparisonReady = Test-Truthy (Get-JsonValue $Comparison "readyForReview")
    $ready = ($available.Count -gt 0 -and $improvements.Count -gt 0 -and $regressions.Count -eq 0)
    $status = if ($ready) {
        "optimization-ready"
    }
    elseif ($regressions.Count -gt 0) {
        "leveler-regression"
    }
    elseif ($available.Count -eq 0) {
        "leveler-metrics-missing"
    }
    elseif ($improvements.Count -eq 0) {
        "leveler-optimization-not-proven"
    }
    else {
        "leveler-optimization-not-proven"
    }

    return [ordered]@{
        required = $true
        ready = $ready
        status = $status
        comparisonReadyForReview = $comparisonReady
        materialImprovementThresholdDb = $improvementThresholdDb
        constraintCountRegressionSuppressed = [double]::IsPositiveInfinity($constraintCountRegressionThreshold)
        constraintSeverityBenign = $constraintSeverityBenign
        peakGuardRegressionSuppressed = $peakGuardBenign
        peakGuardHeadroomMinDb = $candidatePeakHeadroomMin
        peakGuardPreLimitPeakMaxDbfs = $candidatePreLimitPeakMax
        limiterRegression = $limiterRegression
        availableMetricCount = $available.Count
        materialImprovementCount = $improvements.Count
        regressionCount = $regressions.Count
        availableMetrics = @($available.ToArray())
        materialImprovements = @($improvements.ToArray())
        regressions = @($regressions.ToArray())
        recommendation = "Require at least one material RX leveler improvement and no leveler safety regression before treating live A/B evidence as optimization proof."
    }
}

function New-RxLevelerCaptureStabilityEvidence {
    param(
        $Comparison,
        $OptimizationEvidence
    )

    $regressions = @(
        @(Get-JsonValue $Comparison "metricComparisons") |
            Where-Object { [string](Get-JsonValue $_ "verdict") -eq "regression" }
    )
    $regressionIds = @($regressions | ForEach-Object { [string](Get-JsonValue $_ "metricId") })
    $agcRegressionIds = @(
        "agcGainMovementDb",
        "agcActiveGainMovementDb",
        "agcVoiceLikeGainMovementDb",
        "agcPumpingRisk"
    )
    $agcRegressionCount = @($regressionIds | Where-Object { $agcRegressionIds -contains $_ }).Count
    $passbandMovementRegression = $regressionIds -contains "passbandAudioMovementDb"
    $optimizationReady = Test-Truthy (Get-JsonValue $OptimizationEvidence "ready")

    $peakMetric = Get-MetricComparison -Comparison $Comparison -MetricId "passbandPeakSampleCount"
    $baselinePeakCount = Get-NumericValue (Get-JsonValue $peakMetric "baselineValue")
    $candidatePeakCount = Get-NumericValue (Get-JsonValue $peakMetric "candidateValue")
    $passbandPeakDelta = $null
    $passbandPeakImbalanceRatio = $null
    $passbandPeakImbalanced = $false
    if ($null -ne $baselinePeakCount -and $null -ne $candidatePeakCount -and
        [double]$baselinePeakCount -gt 0.0 -and [double]$candidatePeakCount -gt 0.0) {
        $passbandPeakDelta = [double]$candidatePeakCount - [double]$baselinePeakCount
        $denom = [Math]::Max(1.0, [Math]::Min([double]$baselinePeakCount, [double]$candidatePeakCount))
        $passbandPeakImbalanceRatio = [Math]::Abs($passbandPeakDelta) / $denom
        $passbandPeakImbalanced = $passbandPeakImbalanceRatio -ge 0.75
    }

    $comparisonReady = Test-Truthy (Get-JsonValue $Comparison "readyForReview")
    $status = if ($comparisonReady) {
        "stable"
    }
    elseif ($optimizationReady -and $regressions.Count -gt 0 -and $regressions.Count -eq $agcRegressionCount) {
        "agc-window-drift"
    }
    elseif ($optimizationReady -and $passbandMovementRegression -and $passbandPeakImbalanced) {
        "passband-window-imbalance"
    }
    elseif ($regressions.Count -gt 0) {
        "comparison-regression"
    }
    else {
        "comparison-not-ready"
    }

    return [ordered]@{
        required = $true
        ready = $comparisonReady
        status = $status
        comparisonReadyForReview = $comparisonReady
        optimizationReady = $optimizationReady
        regressionCount = $regressions.Count
        regressionMetricIds = @($regressionIds)
        agcRegressionCount = $agcRegressionCount
        passbandAudioMovementRegression = $passbandMovementRegression
        baselinePassbandPeakSampleCount = $baselinePeakCount
        candidatePassbandPeakSampleCount = $candidatePeakCount
        passbandPeakDelta = if ($null -eq $passbandPeakDelta) { $null } else { [Math]::Round($passbandPeakDelta, 3) }
        passbandPeakImbalanceRatio = if ($null -eq $passbandPeakImbalanceRatio) { $null } else { [Math]::Round($passbandPeakImbalanceRatio, 3) }
        passbandPeakImbalanced = $passbandPeakImbalanced
        recommendation = "Sequential live A/B windows must be stable enough that AGC motion and passband peak coverage do not dominate the candidate comparison."
    }
}

function Get-CaptureInputPath {
    param(
        $Capture,
        [Parameter(Mandatory = $true)][string]$Role
    )

    if ($null -eq $Capture) {
        throw "RX leveler A/B summary is missing '$Role' capture details."
    }

    $reportPath = [string](Get-JsonValue $Capture "reportPath")
    if (-not [string]::IsNullOrWhiteSpace($reportPath)) {
        return (Resolve-ExistingFilePath -Path $reportPath)
    }

    $jsonlPath = [string](Get-JsonValue $Capture "jsonlPath")
    if (-not [string]::IsNullOrWhiteSpace($jsonlPath)) {
        return (Resolve-ExistingFilePath -Path $jsonlPath)
    }

    throw "RX leveler A/B '$Role' capture does not declare reportPath or jsonlPath."
}

function Resolve-DirectSummary {
    param($RootSummary)

    $tool = [string](Get-JsonValue $RootSummary "tool")
    if ($tool -eq "capture-rx-leveler-ab") {
        return [ordered]@{
            summary = $RootSummary
            wrapperSummaryPath = $null
            directSummaryPath = $resolvedSummaryPath
        }
    }

    if ($tool -eq "capture-g2-frontend-rx-leveler-ab") {
        $directPath = [string](Get-JsonValue $RootSummary "rxLevelerAbSummaryPath")
        if ([string]::IsNullOrWhiteSpace($directPath)) {
            throw "G2 RX leveler wrapper summary does not declare rxLevelerAbSummaryPath."
        }

        return [ordered]@{
            summary = Read-JsonFile -Path $directPath
            wrapperSummaryPath = $resolvedSummaryPath
            directSummaryPath = Resolve-ExistingFilePath -Path $directPath
        }
    }

    throw "Summary '$resolvedSummaryPath' must be generated by capture-rx-leveler-ab or capture-g2-frontend-rx-leveler-ab; found tool '$tool'."
}

$bundlePath = ""
if (-not [string]::IsNullOrWhiteSpace($BundleDir)) {
    $bundlePath = Resolve-ExistingDirectoryPath -Path $BundleDir
}

$resolvedSummaryPath = Resolve-ExistingFilePath -Path $SummaryPath
$rootSummary = Read-JsonFile -Path $resolvedSummaryPath
$resolved = Resolve-DirectSummary -RootSummary $rootSummary
$directSummary = $resolved.summary
$directSummaryPath = [string]$resolved.directSummaryPath
$wrapperSummaryPath = if ($null -eq $resolved.wrapperSummaryPath) { $null } else { [string]$resolved.wrapperSummaryPath }

$activeAudioEvidence = Get-JsonValue $directSummary "activeAudioEvidence"
$activeAudioReady = Test-Truthy (Get-JsonValue $activeAudioEvidence "ready")
if (-not $AllowInactiveAudio -and -not $activeAudioReady) {
    throw "RX leveler A/B summary is missing active audio evidence. Rerun capture with -RequireActiveAudio on an active signal, or pass -AllowInactiveAudio for workflow-only comparison."
}

$currentCapture = Get-JsonValue $directSummary "current"
$candidateCapture = Get-JsonValue $directSummary "candidate"
$baselinePath = Get-CaptureInputPath -Capture $currentCapture -Role "current"
$candidatePath = Get-CaptureInputPath -Capture $candidateCapture -Role "candidate"

if ([string]::IsNullOrWhiteSpace($CandidateLabel)) {
    $CandidateLabel = [string](Get-JsonValue $candidateCapture "profile")
    if ([string]::IsNullOrWhiteSpace($CandidateLabel)) {
        $CandidateLabel = "candidate-under-test"
    }
}

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $summaryDir = Split-Path -Parent $directSummaryPath
    if ([string]::IsNullOrWhiteSpace($summaryDir)) {
        $summaryDir = (Get-Location).Path
    }

    $ReportPath = Join-Path $summaryDir "rx-leveler-ab-live-comparison.json"
}

$compareScript = Join-Path $PSScriptRoot "compare-dsp-live-diagnostics-traces.ps1"
if (-not [System.IO.File]::Exists((ConvertTo-LongFileSystemPath -Path ([System.IO.Path]::GetFullPath($compareScript)))) ) {
    throw "Missing comparison script: $compareScript"
}

$compareArgs = @{
    BaselinePath = $baselinePath
    CandidatePath = $candidatePath
    BaselineLabel = $BaselineLabel
    CandidateLabel = $CandidateLabel
    ReportPath = $ReportPath
    ScenarioId = "rx-audio-leveler-passband"
    JsonOnly = $true
}
if (-not [string]::IsNullOrWhiteSpace($MarkdownPath)) {
    $compareArgs["MarkdownPath"] = $MarkdownPath
}
if (-not [string]::IsNullOrWhiteSpace($bundlePath)) {
    $compareArgs["BundleDir"] = $bundlePath
}
if ($NoMarkdown) {
    $compareArgs["NoMarkdown"] = $true
}

$global:LASTEXITCODE = $null
$comparisonJson = & $compareScript @compareArgs
$compareSucceeded = $?
$compareExitCode = $LASTEXITCODE
if (-not $compareSucceeded -or ($null -ne $compareExitCode -and $compareExitCode -ne 0)) {
    $exitCodeText = if ($null -eq $compareExitCode) { "unknown" } else { [string]$compareExitCode }
    throw "compare-dsp-live-diagnostics-traces failed with exit code $exitCodeText."
}

$comparison = $comparisonJson | ConvertFrom-Json
$candidateReport = Read-JsonFile -Path $candidatePath
$passbandEvidence = New-RxLevelerPassbandEvidence `
    -Comparison $comparison `
    -BaselineProfile $BaselineLabel `
    -CandidateProfile $CandidateLabel
$controlMemoryEvidence = New-RxLevelerCandidateControlMemoryEvidence `
    -CandidateReport $candidateReport `
    -CandidateProfile $CandidateLabel
$optimizationEvidence = New-RxLevelerOptimizationEvidence -Comparison $comparison
$captureStabilityEvidence = New-RxLevelerCaptureStabilityEvidence `
    -Comparison $comparison `
    -OptimizationEvidence $optimizationEvidence
$passbandReady = Test-Truthy (Get-JsonValue $passbandEvidence "ready")
$controlMemoryReady = Test-Truthy (Get-JsonValue $controlMemoryEvidence "ready")
$optimizationReady = Test-Truthy (Get-JsonValue $optimizationEvidence "ready")
$captureStabilityStatus = [string](Get-JsonValue $captureStabilityEvidence "status")
$comparison | Add-Member -NotePropertyName rxLevelerAbSource -NotePropertyValue ([ordered]@{
        summaryPath = ConvertTo-PortablePath -Root $bundlePath -Path $directSummaryPath
        wrapperSummaryPath = ConvertTo-PortablePath -Root $bundlePath -Path $wrapperSummaryPath
        currentInputPath = ConvertTo-PortablePath -Root $bundlePath -Path $baselinePath
        candidateInputPath = ConvertTo-PortablePath -Root $bundlePath -Path $candidatePath
    }) -Force
$comparison | Add-Member -NotePropertyName rxLevelerAbActiveAudioReady -NotePropertyValue $activeAudioReady -Force
$comparison | Add-Member -NotePropertyName rxLevelerAbActiveAudioEvidence -NotePropertyValue $activeAudioEvidence -Force
$comparison | Add-Member -NotePropertyName rxLevelerAbPassbandEvidenceReady -NotePropertyValue $passbandReady -Force
$comparison | Add-Member -NotePropertyName rxLevelerAbPassbandEvidence -NotePropertyValue $passbandEvidence -Force
$comparison | Add-Member -NotePropertyName rxLevelerAbCandidateControlMemoryReady -NotePropertyValue $controlMemoryReady -Force
$comparison | Add-Member -NotePropertyName rxLevelerAbCandidateControlMemoryEvidence -NotePropertyValue $controlMemoryEvidence -Force
$comparison | Add-Member -NotePropertyName rxLevelerAbOptimizationReady -NotePropertyValue $optimizationReady -Force
$comparison | Add-Member -NotePropertyName rxLevelerAbOptimizationEvidence -NotePropertyValue $optimizationEvidence -Force
$comparison | Add-Member -NotePropertyName rxLevelerAbCaptureStabilityReady -NotePropertyValue (Test-Truthy (Get-JsonValue $captureStabilityEvidence "ready")) -Force
$comparison | Add-Member -NotePropertyName rxLevelerAbCaptureStabilityEvidence -NotePropertyValue $captureStabilityEvidence -Force
$promotionReady = [bool]($activeAudioReady -and $passbandReady -and $controlMemoryReady -and $optimizationReady -and (Test-Truthy (Get-JsonValue $comparison "readyForReview")))
$evidenceStatus = if ($promotionReady) {
    "ready"
}
elseif (-not $activeAudioReady) {
    "inactive-audio"
}
elseif (-not $controlMemoryReady) {
    [string](Get-JsonValue $controlMemoryEvidence "status")
}
elseif (-not $passbandReady) {
    "passband-evidence-missing"
}
elseif (-not $optimizationReady) {
    [string](Get-JsonValue $optimizationEvidence "status")
}
elseif ($captureStabilityStatus -eq "agc-window-drift" -or
    $captureStabilityStatus -eq "passband-window-imbalance") {
    $captureStabilityStatus
}
else {
    "comparison-not-ready"
}
$comparison | Add-Member -NotePropertyName rxLevelerAbPromotionReady -NotePropertyValue $promotionReady -Force
$comparison | Add-Member -NotePropertyName rxLevelerAbEvidenceStatus -NotePropertyValue $evidenceStatus -Force

Write-JsonFile -Path $ReportPath -Value $comparison

if ($JsonOnly) {
    $comparison | ConvertTo-Json -Depth 64
}
else {
    Write-Host "RX leveler A/B comparison: $ReportPath"
    Write-Host "Ready for review: $($comparison.readyForReview); active audio ready: $($comparison.rxLevelerAbActiveAudioReady); passband ready: $($comparison.rxLevelerAbPassbandEvidenceReady); control memory ready: $($comparison.rxLevelerAbCandidateControlMemoryReady); optimization ready: $($comparison.rxLevelerAbOptimizationReady); promotion ready: $($comparison.rxLevelerAbPromotionReady)"
}

if ($FailOnNotReady -and -not [bool]$comparison.rxLevelerAbPromotionReady) {
    exit 1
}
