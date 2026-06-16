param(
    [Parameter(Mandatory = $true)]
    [string]$BaselineIndexPath,

    [Parameter(Mandatory = $true)]
    [string]$CandidateIndexPath,

    [string]$BaselineRoot = "",

    [string]$CandidateRoot = "",

    [string]$BundleDir = "",

    [string]$BaselineComparisonId = "",

    [string]$CandidateComparisonId = "",

    [string]$BaselineLabel = "current-zeus",

    [string]$CandidateLabel = "candidate-under-test",

    [string]$ReportPath = "",

    [string]$MarkdownPath = "",

    [string]$OutputDir = "",

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

    $json = $Value | ConvertTo-Json -Depth 64
    Set-Content -LiteralPath $Path -Value $json -Encoding UTF8
}

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
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

function ConvertTo-SafeName {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return "trace"
    }

    $safe = $Value.Trim().ToLowerInvariant() -replace "[^a-z0-9._-]+", "-"
    $safe = $safe.Trim("-")
    if ($safe.Length -gt 96) {
        $safe = $safe.Substring(0, 96).Trim("-")
    }
    if ([string]::IsNullOrWhiteSpace($safe)) {
        return "trace"
    }

    return $safe
}

function ConvertTo-ComparisonId {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return ""
    }

    return ($Value.Trim().ToLowerInvariant() -replace "[^a-z0-9]+", "-").Trim("-")
}

function Resolve-IndexRoot {
    param(
        [Parameter(Mandatory = $true)][string]$IndexPath,
        [string]$RootOverride
    )

    if (-not [string]::IsNullOrWhiteSpace($RootOverride)) {
        return (Resolve-Path -LiteralPath $RootOverride).Path
    }

    $indexDir = Split-Path -Parent (Resolve-Path -LiteralPath $IndexPath).Path
    if ([string]::Equals((Split-Path -Leaf $indexDir), "artifacts", [StringComparison]::OrdinalIgnoreCase)) {
        return (Resolve-Path -LiteralPath (Join-Path $indexDir "..")).Path
    }

    return $indexDir
}

function Resolve-ReferencedPath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return ""
    }

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return Join-Path $Root $Path
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

function Get-EntryScenarioIds {
    param($Entry)

    $ids = New-Object System.Collections.Generic.List[string]
    $scenarioId = [string](Get-JsonValue $Entry "scenarioId")
    if (-not [string]::IsNullOrWhiteSpace($scenarioId)) {
        $ids.Add($scenarioId) | Out-Null
    }

    foreach ($value in (Get-JsonArray $Entry "scenarioIds")) {
        $scenario = [string]$value
        if (-not [string]::IsNullOrWhiteSpace($scenario)) {
            $ids.Add($scenario) | Out-Null
        }
    }

    return @($ids.ToArray() | Select-Object -Unique)
}

function Get-EntryComparisonIds {
    param($Entry)

    $ids = New-Object System.Collections.Generic.List[string]
    foreach ($name in @("comparisonId", "comparison", "candidate", "candidateId")) {
        $comparison = ConvertTo-ComparisonId ([string](Get-JsonValue $Entry $name))
        if (-not [string]::IsNullOrWhiteSpace($comparison)) {
            $ids.Add($comparison) | Out-Null
        }
    }

    foreach ($value in (Get-JsonArray $Entry "comparisonIds")) {
        $comparison = ConvertTo-ComparisonId ([string]$value)
        if (-not [string]::IsNullOrWhiteSpace($comparison)) {
            $ids.Add($comparison) | Out-Null
        }
    }

    return @($ids.ToArray() | Select-Object -Unique)
}

function Get-TraceIndexEntries {
    param(
        [Parameter(Mandatory = $true)][string]$IndexPath,
        [string]$RootOverride,
        [string]$ComparisonFilter
    )

    $resolvedIndexPath = (Resolve-Path -LiteralPath $IndexPath).Path
    $root = Resolve-IndexRoot -IndexPath $resolvedIndexPath -RootOverride $RootOverride
    $index = Read-JsonFile $resolvedIndexPath
    $entries = New-Object System.Collections.Generic.List[object]
    $normalizedFilter = ConvertTo-ComparisonId $ComparisonFilter

    foreach ($file in (Get-JsonArray $index "files")) {
        $path = [string](Get-JsonValue $file "path")
        $summaryPath = [string](Get-JsonValue $file "summaryPath")
        $scenarioIds = @(Get-EntryScenarioIds $file)
        $comparisonIds = @(Get-EntryComparisonIds $file)

        if ($scenarioIds.Count -eq 0) {
            continue
        }
        if ($comparisonIds.Count -eq 0) {
            $comparisonIds = @("")
        }
        if (-not [string]::IsNullOrWhiteSpace($normalizedFilter) -and
            -not @($comparisonIds | Where-Object { $_ -eq $normalizedFilter }).Count) {
            continue
        }

        $resolvedTracePath = Resolve-ReferencedPath -Root $root -Path $path
        $resolvedSummaryPath = Resolve-ReferencedPath -Root $root -Path $summaryPath
        $inputPath = if (-not [string]::IsNullOrWhiteSpace($resolvedSummaryPath) -and
            (Test-Path -LiteralPath $resolvedSummaryPath -PathType Leaf)) {
            $resolvedSummaryPath
        }
        else {
            $resolvedTracePath
        }

        foreach ($scenarioId in $scenarioIds) {
            foreach ($comparisonId in $comparisonIds) {
                $entries.Add([ordered]@{
                    scenarioId = [string]$scenarioId
                    comparisonId = [string]$comparisonId
                    tracePath = $resolvedTracePath
                    summaryPath = $resolvedSummaryPath
                    inputPath = $inputPath
                    indexPath = $resolvedIndexPath
                    root = $root
                }) | Out-Null
            }
        }
    }

    return @($entries.ToArray())
}

function Add-Entry {
    param(
        [hashtable]$Map,
        $Entry
    )

    $scenario = [string]$Entry.scenarioId
    if (-not $Map.ContainsKey($scenario)) {
        $Map[$scenario] = New-Object System.Collections.Generic.List[object]
    }
    $Map[$scenario].Add($Entry) | Out-Null
}

function New-Nr5WeakSignalAccumulator {
    return [ordered]@{
        baselineWeakInputSampleCount = 0.0
        candidateWeakInputSampleCount = 0.0
        baselineWeakRecoveredSampleCount = 0.0
        candidateWeakRecoveredSampleCount = 0.0
        baselineWeakDropoutSampleCount = 0.0
        candidateWeakDropoutSampleCount = 0.0
        baselineHotMakeupSampleCount = 0.0
        candidateHotMakeupSampleCount = 0.0
        baselineOutputMovementDbTotal = 0.0
        candidateOutputMovementDbTotal = 0.0
        outputMovementComparisonCount = 0.0
        baselineMakeupMovementDbTotal = 0.0
        candidateMakeupMovementDbTotal = 0.0
        makeupMovementComparisonCount = 0.0
        baselineMakeupMaxDbMax = $null
        candidateMakeupMaxDbMax = $null
        makeupMaxComparisonCount = 0.0
        baselineRecoveryDriveMovementTotal = 0.0
        candidateRecoveryDriveMovementTotal = 0.0
        recoveryDriveMovementComparisonCount = 0.0
        baselineTextureFillAverageTotal = 0.0
        candidateTextureFillAverageTotal = 0.0
        textureFillComparisonCount = 0.0
        outputMovementRegressionCount = 0
        makeupMovementRegressionCount = 0
        makeupMaxRegressionCount = 0
        recoveryDriveMovementRegressionCount = 0
    }
}

function Add-Nr5NumericPairToAccumulator {
    param(
        [System.Collections.Specialized.OrderedDictionary]$Accumulator,
        $Comparison,
        [Parameter(Mandatory = $true)][string]$BaselineName,
        [Parameter(Mandatory = $true)][string]$CandidateName,
        [Parameter(Mandatory = $true)][string]$BaselineTotalName,
        [Parameter(Mandatory = $true)][string]$CandidateTotalName,
        [Parameter(Mandatory = $true)][string]$CountName
    )

    $baselineValue = Get-NumericValue (Get-JsonValue $Comparison $BaselineName)
    $candidateValue = Get-NumericValue (Get-JsonValue $Comparison $CandidateName)
    if ($null -eq $baselineValue -or $null -eq $candidateValue) {
        return
    }

    $Accumulator[$BaselineTotalName] = [double]$Accumulator[$BaselineTotalName] + $baselineValue
    $Accumulator[$CandidateTotalName] = [double]$Accumulator[$CandidateTotalName] + $candidateValue
    $Accumulator[$CountName] = [double]$Accumulator[$CountName] + 1.0
}

function Add-Nr5NumericMaxPairToAccumulator {
    param(
        [System.Collections.Specialized.OrderedDictionary]$Accumulator,
        $Comparison,
        [Parameter(Mandatory = $true)][string]$BaselineName,
        [Parameter(Mandatory = $true)][string]$CandidateName,
        [Parameter(Mandatory = $true)][string]$BaselineMaxName,
        [Parameter(Mandatory = $true)][string]$CandidateMaxName,
        [Parameter(Mandatory = $true)][string]$CountName
    )

    $baselineValue = Get-NumericValue (Get-JsonValue $Comparison $BaselineName)
    $candidateValue = Get-NumericValue (Get-JsonValue $Comparison $CandidateName)
    if ($null -eq $baselineValue -or $null -eq $candidateValue) {
        return
    }

    if ($null -eq $Accumulator[$BaselineMaxName]) {
        $Accumulator[$BaselineMaxName] = $baselineValue
    }
    else {
        $Accumulator[$BaselineMaxName] = [Math]::Max([double]$Accumulator[$BaselineMaxName], $baselineValue)
    }

    if ($null -eq $Accumulator[$CandidateMaxName]) {
        $Accumulator[$CandidateMaxName] = $candidateValue
    }
    else {
        $Accumulator[$CandidateMaxName] = [Math]::Max([double]$Accumulator[$CandidateMaxName], $candidateValue)
    }

    $Accumulator[$CountName] = [double]$Accumulator[$CountName] + 1.0
}

function Add-Nr5RegressionFlagToAccumulator {
    param(
        [System.Collections.Specialized.OrderedDictionary]$Accumulator,
        $Comparison,
        [Parameter(Mandatory = $true)][string]$FlagName,
        [Parameter(Mandatory = $true)][string]$CountName
    )

    if (Test-Truthy (Get-JsonValue $Comparison $FlagName)) {
        $Accumulator[$CountName] = [int]$Accumulator[$CountName] + 1
    }
}

function Add-Nr5WeakSignalComparison {
    param(
        [System.Collections.Specialized.OrderedDictionary]$Accumulator,
        $Comparison
    )

    if ($null -eq $Comparison) {
        return
    }

    foreach ($name in @(
        "baselineWeakInputSampleCount",
        "candidateWeakInputSampleCount",
        "baselineWeakRecoveredSampleCount",
        "candidateWeakRecoveredSampleCount",
        "baselineWeakDropoutSampleCount",
        "candidateWeakDropoutSampleCount",
        "baselineHotMakeupSampleCount",
        "candidateHotMakeupSampleCount"
    )) {
        $value = Get-NumericValue (Get-JsonValue $Comparison $name)
        if ($null -ne $value) {
            $Accumulator[$name] = [double]$Accumulator[$name] + $value
        }
    }

    Add-Nr5NumericPairToAccumulator `
        -Accumulator $Accumulator `
        -Comparison $Comparison `
        -BaselineName "baselineOutputMovementDb" `
        -CandidateName "candidateOutputMovementDb" `
        -BaselineTotalName "baselineOutputMovementDbTotal" `
        -CandidateTotalName "candidateOutputMovementDbTotal" `
        -CountName "outputMovementComparisonCount"
    Add-Nr5NumericPairToAccumulator `
        -Accumulator $Accumulator `
        -Comparison $Comparison `
        -BaselineName "baselineMakeupMovementDb" `
        -CandidateName "candidateMakeupMovementDb" `
        -BaselineTotalName "baselineMakeupMovementDbTotal" `
        -CandidateTotalName "candidateMakeupMovementDbTotal" `
        -CountName "makeupMovementComparisonCount"
    Add-Nr5NumericMaxPairToAccumulator `
        -Accumulator $Accumulator `
        -Comparison $Comparison `
        -BaselineName "baselineMakeupMaxDb" `
        -CandidateName "candidateMakeupMaxDb" `
        -BaselineMaxName "baselineMakeupMaxDbMax" `
        -CandidateMaxName "candidateMakeupMaxDbMax" `
        -CountName "makeupMaxComparisonCount"
    Add-Nr5NumericPairToAccumulator `
        -Accumulator $Accumulator `
        -Comparison $Comparison `
        -BaselineName "baselineRecoveryDriveMovement" `
        -CandidateName "candidateRecoveryDriveMovement" `
        -BaselineTotalName "baselineRecoveryDriveMovementTotal" `
        -CandidateTotalName "candidateRecoveryDriveMovementTotal" `
        -CountName "recoveryDriveMovementComparisonCount"
    Add-Nr5NumericPairToAccumulator `
        -Accumulator $Accumulator `
        -Comparison $Comparison `
        -BaselineName "baselineTextureFillAverage" `
        -CandidateName "candidateTextureFillAverage" `
        -BaselineTotalName "baselineTextureFillAverageTotal" `
        -CandidateTotalName "candidateTextureFillAverageTotal" `
        -CountName "textureFillComparisonCount"

    Add-Nr5RegressionFlagToAccumulator -Accumulator $Accumulator -Comparison $Comparison -FlagName "outputMovementRegression" -CountName "outputMovementRegressionCount"
    Add-Nr5RegressionFlagToAccumulator -Accumulator $Accumulator -Comparison $Comparison -FlagName "makeupMovementRegression" -CountName "makeupMovementRegressionCount"
    Add-Nr5RegressionFlagToAccumulator -Accumulator $Accumulator -Comparison $Comparison -FlagName "makeupMaxRegression" -CountName "makeupMaxRegressionCount"
    Add-Nr5RegressionFlagToAccumulator -Accumulator $Accumulator -Comparison $Comparison -FlagName "recoveryDriveMovementRegression" -CountName "recoveryDriveMovementRegressionCount"
}

function Get-Nr5AccumulatorAverage {
    param(
        [System.Collections.Specialized.OrderedDictionary]$Accumulator,
        [Parameter(Mandatory = $true)][string]$TotalName,
        [Parameter(Mandatory = $true)][string]$CountName
    )

    $count = [double]$Accumulator[$CountName]
    if ($count -le 0.0) {
        return 0.0
    }

    return [Math]::Round([double]$Accumulator[$TotalName] / $count, 3)
}

function Get-Nr5AccumulatorValueOrZero {
    param(
        [System.Collections.Specialized.OrderedDictionary]$Accumulator,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if ($null -eq $Accumulator[$Name]) {
        return 0.0
    }

    return [Math]::Round([double]$Accumulator[$Name], 3)
}

function Complete-Nr5WeakSignalSummary {
    param([System.Collections.Specialized.OrderedDictionary]$Accumulator)

    $baselineWeakInput = [double]$Accumulator["baselineWeakInputSampleCount"]
    $candidateWeakInput = [double]$Accumulator["candidateWeakInputSampleCount"]
    $baselineWeakRecovered = [double]$Accumulator["baselineWeakRecoveredSampleCount"]
    $candidateWeakRecovered = [double]$Accumulator["candidateWeakRecoveredSampleCount"]
    $baselineWeakDropout = [double]$Accumulator["baselineWeakDropoutSampleCount"]
    $candidateWeakDropout = [double]$Accumulator["candidateWeakDropoutSampleCount"]
    $baselineHotMakeup = [double]$Accumulator["baselineHotMakeupSampleCount"]
    $candidateHotMakeup = [double]$Accumulator["candidateHotMakeupSampleCount"]
    $baselineRecoveryPct = if ($baselineWeakInput -le 0.0) { 100.0 } else { [Math]::Round(100.0 * $baselineWeakRecovered / $baselineWeakInput, 3) }
    $candidateRecoveryPct = if ($candidateWeakInput -le 0.0) { 100.0 } else { [Math]::Round(100.0 * $candidateWeakRecovered / $candidateWeakInput, 3) }
    $baselineOutputMovement = Get-Nr5AccumulatorAverage $Accumulator "baselineOutputMovementDbTotal" "outputMovementComparisonCount"
    $candidateOutputMovement = Get-Nr5AccumulatorAverage $Accumulator "candidateOutputMovementDbTotal" "outputMovementComparisonCount"
    $baselineMakeupMovement = Get-Nr5AccumulatorAverage $Accumulator "baselineMakeupMovementDbTotal" "makeupMovementComparisonCount"
    $candidateMakeupMovement = Get-Nr5AccumulatorAverage $Accumulator "candidateMakeupMovementDbTotal" "makeupMovementComparisonCount"
    $baselineMakeupMax = Get-Nr5AccumulatorValueOrZero $Accumulator "baselineMakeupMaxDbMax"
    $candidateMakeupMax = Get-Nr5AccumulatorValueOrZero $Accumulator "candidateMakeupMaxDbMax"
    $baselineRecoveryMovement = Get-Nr5AccumulatorAverage $Accumulator "baselineRecoveryDriveMovementTotal" "recoveryDriveMovementComparisonCount"
    $candidateRecoveryMovement = Get-Nr5AccumulatorAverage $Accumulator "candidateRecoveryDriveMovementTotal" "recoveryDriveMovementComparisonCount"
    $baselineTextureAverage = Get-Nr5AccumulatorAverage $Accumulator "baselineTextureFillAverageTotal" "textureFillComparisonCount"
    $candidateTextureAverage = Get-Nr5AccumulatorAverage $Accumulator "candidateTextureFillAverageTotal" "textureFillComparisonCount"

    return [ordered]@{
        baselineWeakInputSampleCount = [int][Math]::Round($baselineWeakInput)
        candidateWeakInputSampleCount = [int][Math]::Round($candidateWeakInput)
        weakInputSampleDelta = [int][Math]::Round($candidateWeakInput - $baselineWeakInput)
        baselineWeakRecoveredSampleCount = [int][Math]::Round($baselineWeakRecovered)
        candidateWeakRecoveredSampleCount = [int][Math]::Round($candidateWeakRecovered)
        weakRecoveredSampleDelta = [int][Math]::Round($candidateWeakRecovered - $baselineWeakRecovered)
        baselineWeakDropoutSampleCount = [int][Math]::Round($baselineWeakDropout)
        candidateWeakDropoutSampleCount = [int][Math]::Round($candidateWeakDropout)
        weakDropoutSampleDelta = [int][Math]::Round($candidateWeakDropout - $baselineWeakDropout)
        baselineHotMakeupSampleCount = [int][Math]::Round($baselineHotMakeup)
        candidateHotMakeupSampleCount = [int][Math]::Round($candidateHotMakeup)
        hotMakeupSampleDelta = [int][Math]::Round($candidateHotMakeup - $baselineHotMakeup)
        baselineWeakRecoveryPct = $baselineRecoveryPct
        candidateWeakRecoveryPct = $candidateRecoveryPct
        weakRecoveryPctDelta = [Math]::Round($candidateRecoveryPct - $baselineRecoveryPct, 3)
        baselineOutputMovementDbAverage = $baselineOutputMovement
        candidateOutputMovementDbAverage = $candidateOutputMovement
        outputMovementDbDelta = [Math]::Round($candidateOutputMovement - $baselineOutputMovement, 3)
        baselineMakeupMovementDbAverage = $baselineMakeupMovement
        candidateMakeupMovementDbAverage = $candidateMakeupMovement
        makeupMovementDbDelta = [Math]::Round($candidateMakeupMovement - $baselineMakeupMovement, 3)
        baselineMakeupMaxDbMax = $baselineMakeupMax
        candidateMakeupMaxDbMax = $candidateMakeupMax
        makeupMaxDbDelta = [Math]::Round($candidateMakeupMax - $baselineMakeupMax, 3)
        baselineRecoveryDriveMovementAverage = $baselineRecoveryMovement
        candidateRecoveryDriveMovementAverage = $candidateRecoveryMovement
        recoveryDriveMovementDelta = [Math]::Round($candidateRecoveryMovement - $baselineRecoveryMovement, 3)
        baselineTextureFillAverage = $baselineTextureAverage
        candidateTextureFillAverage = $candidateTextureAverage
        textureFillAverageDelta = [Math]::Round($candidateTextureAverage - $baselineTextureAverage, 3)
        dropoutRegression = ($candidateWeakDropout -gt $baselineWeakDropout)
        hotMakeupRegression = ($candidateHotMakeup -gt $baselineHotMakeup)
        recoveryRegression = ($candidateRecoveryPct -lt ($baselineRecoveryPct - 5.0))
        outputMovementRegressionCount = [int]$Accumulator["outputMovementRegressionCount"]
        makeupMovementRegressionCount = [int]$Accumulator["makeupMovementRegressionCount"]
        makeupMaxRegressionCount = [int]$Accumulator["makeupMaxRegressionCount"]
        recoveryDriveMovementRegressionCount = [int]$Accumulator["recoveryDriveMovementRegressionCount"]
        outputMovementRegression = ([int]$Accumulator["outputMovementRegressionCount"] -gt 0)
        makeupMovementRegression = ([int]$Accumulator["makeupMovementRegressionCount"] -gt 0)
        makeupMaxRegression = ([int]$Accumulator["makeupMaxRegressionCount"] -gt 0)
        recoveryDriveMovementRegression = ([int]$Accumulator["recoveryDriveMovementRegressionCount"] -gt 0)
    }
}

function New-DetailSafetyClassCounts {
    param($Items)

    $counts = @{}
    foreach ($item in @($Items)) {
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

function Invoke-TraceComparison {
    param(
        [Parameter(Mandatory = $true)][string]$CompareScript,
        [Parameter(Mandatory = $true)]$BaselineEntry,
        [Parameter(Mandatory = $true)]$CandidateEntry,
        [Parameter(Mandatory = $true)][string]$ScenarioId,
        [Parameter(Mandatory = $true)][string]$ReportPath,
        [Parameter(Mandatory = $true)][string]$BaselineLabel,
        [Parameter(Mandatory = $true)][string]$CandidateLabel,
        [Parameter(Mandatory = $true)][double]$Tolerance,
        [string]$BundleDir = ""
    )

    if (-not (Test-Path -LiteralPath $BaselineEntry.inputPath -PathType Leaf)) {
        throw "Missing baseline trace input for scenario '$ScenarioId': $($BaselineEntry.inputPath)"
    }
    if (-not (Test-Path -LiteralPath $CandidateEntry.inputPath -PathType Leaf)) {
        throw "Missing candidate trace input for scenario '$ScenarioId': $($CandidateEntry.inputPath)"
    }

    $args = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", $CompareScript,
        "-BaselinePath", $BaselineEntry.inputPath,
        "-CandidatePath", $CandidateEntry.inputPath,
        "-BaselineLabel", $BaselineLabel,
        "-CandidateLabel", $CandidateLabel,
        "-ReportPath", $ReportPath,
        "-Tolerance", ([string]::Format([System.Globalization.CultureInfo]::InvariantCulture, "{0:R}", $Tolerance)),
        "-NoMarkdown",
        "-JsonOnly"
    )
    if (-not [string]::IsNullOrWhiteSpace($BundleDir)) {
        $args += @("-BundleDir", $BundleDir)
    }

    $output = & powershell @args 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "compare-dsp-live-diagnostics-traces.ps1 failed for scenario '$ScenarioId': $(($output | Out-String).Trim())"
    }

    $text = ($output | Out-String).Trim()
    if ([string]::IsNullOrWhiteSpace($text)) {
        return Read-JsonFile $ReportPath
    }

    return $text | ConvertFrom-Json
}

function Build-MarkdownReport {
    param($Report)

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# DSP Live Diagnostics Matrix Comparison") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add("- Ready for review: $($Report.readyForReview)") | Out-Null
    $lines.Add("- Candidate: $($Report.candidateLabel)") | Out-Null
    $lines.Add("- Baseline: $($Report.baselineLabel)") | Out-Null
    $lines.Add("- Scenario comparisons: $($Report.candidateComparisonCount)") | Out-Null
    $lines.Add("- Regressions: $($Report.regressionCount)") | Out-Null
    $lines.Add("- Hard constraint regressions: $($Report.hardConstraintRegressionCount)") | Out-Null
    $lines.Add("- Gate failures: $($Report.gateFailureCount)") | Out-Null
    $lines.Add("- Missing values: $($Report.missingMetricValueCount)") | Out-Null
    $lines.Add("- Missing baseline scenarios: $($Report.missingBaselineCount)") | Out-Null
    $lines.Add("- Missing candidate scenarios: $($Report.missingCandidateCount)") | Out-Null
    $lines.Add("") | Out-Null

    $weakSignal = Get-JsonValue $Report "nr5WeakSignalComparisonSummary"
    if ($null -ne $weakSignal -and
        ([int](Get-JsonValue $weakSignal "baselineWeakInputSampleCount") -gt 0 -or
            [int](Get-JsonValue $weakSignal "candidateWeakInputSampleCount") -gt 0)) {
        $lines.Add("## NR5 Weak-Signal Aggregate") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("| Metric | Baseline | Candidate | Delta |") | Out-Null
        $lines.Add("|---|---:|---:|---:|") | Out-Null
        $lines.Add("| Weak input samples | $($weakSignal.baselineWeakInputSampleCount) | $($weakSignal.candidateWeakInputSampleCount) | $($weakSignal.weakInputSampleDelta) |") | Out-Null
        $lines.Add("| Weak recovered samples | $($weakSignal.baselineWeakRecoveredSampleCount) | $($weakSignal.candidateWeakRecoveredSampleCount) | $($weakSignal.weakRecoveredSampleDelta) |") | Out-Null
        $lines.Add("| Weak dropout samples | $($weakSignal.baselineWeakDropoutSampleCount) | $($weakSignal.candidateWeakDropoutSampleCount) | $($weakSignal.weakDropoutSampleDelta) |") | Out-Null
        $lines.Add("| Hot makeup samples | $($weakSignal.baselineHotMakeupSampleCount) | $($weakSignal.candidateHotMakeupSampleCount) | $($weakSignal.hotMakeupSampleDelta) |") | Out-Null
        $lines.Add("| Weak recovery percent | $($weakSignal.baselineWeakRecoveryPct) | $($weakSignal.candidateWeakRecoveryPct) | $($weakSignal.weakRecoveryPctDelta) |") | Out-Null
        $lines.Add("| Output movement dB average | $($weakSignal.baselineOutputMovementDbAverage) | $($weakSignal.candidateOutputMovementDbAverage) | $($weakSignal.outputMovementDbDelta) |") | Out-Null
        $lines.Add("| Makeup movement dB average | $($weakSignal.baselineMakeupMovementDbAverage) | $($weakSignal.candidateMakeupMovementDbAverage) | $($weakSignal.makeupMovementDbDelta) |") | Out-Null
        $lines.Add("| Makeup max dB maximum | $($weakSignal.baselineMakeupMaxDbMax) | $($weakSignal.candidateMakeupMaxDbMax) | $($weakSignal.makeupMaxDbDelta) |") | Out-Null
        $lines.Add("| Recovery-drive movement average | $($weakSignal.baselineRecoveryDriveMovementAverage) | $($weakSignal.candidateRecoveryDriveMovementAverage) | $($weakSignal.recoveryDriveMovementDelta) |") | Out-Null
        $lines.Add("| Texture-fill average | $($weakSignal.baselineTextureFillAverage) | $($weakSignal.candidateTextureFillAverage) | $($weakSignal.textureFillAverageDelta) |") | Out-Null
        $lines.Add("") | Out-Null
    }

    if (@($Report.metricRegressionDetails).Count -gt 0) {
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
        $lines.Add("| Scenario | Metric | Baseline | Candidate | Direction | Delta | Safety |") | Out-Null
        $lines.Add("|---|---|---:|---:|---|---:|---|") | Out-Null
        foreach ($item in @($Report.metricRegressionDetails)) {
            $lines.Add("| $($item.scenarioId) | $($item.label) | $($item.baselineValue) | $($item.candidateValue) | $($item.direction) | $($item.improvementValue) | $($item.safetyClass) |") | Out-Null
        }
        $lines.Add("") | Out-Null
    }

    if (@($Report.hardConstraintRegressionDetails).Count -gt 0) {
        $lines.Add("## Hard Constraint Regressions") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("| Scenario | Constraint | Baseline | Candidate | Delta |") | Out-Null
        $lines.Add("|---|---|---:|---:|---:|") | Out-Null
        foreach ($item in @($Report.hardConstraintRegressionDetails)) {
            $lines.Add("| $($item.scenarioId) | $($item.constraint) | $($item.baselineCount) | $($item.candidateCount) | $($item.delta) |") | Out-Null
        }
        $lines.Add("") | Out-Null
    }

    if (@($Report.gateFailureDetails).Count -gt 0) {
        $lines.Add("## Gate Failures") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("| Scenario | Ready | OK Samples | Failed Samples | Hard Blockers |") | Out-Null
        $lines.Add("|---|---:|---:|---:|---:|") | Out-Null
        foreach ($item in @($Report.gateFailureDetails)) {
            $lines.Add("| $($item.scenarioId) | $($item.candidateReadyForBenchmarkTrace) | $($item.candidateOkSampleCount) | $($item.candidateFailedSampleCount) | $($item.candidateHardBlockerSampleCount) |") | Out-Null
        }
        $lines.Add("") | Out-Null
    }

    $lines.Add("## Scenario Summary") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add("| Scenario | Baseline | Candidate | Ready | Regressions | Gates | Missing | Report |") | Out-Null
    $lines.Add("|---|---|---|---:|---:|---:|---:|---|") | Out-Null
    foreach ($item in @($Report.scenarioComparisons)) {
        $lines.Add("| $($item.scenarioId) | $($item.baselineComparisonId) | $($item.candidateComparisonId) | $($item.readyForReview) | $($item.regressionCount) | $($item.gateFailureCount) | $($item.missingMetricValueCount) | $($item.reportPath) |") | Out-Null
    }

    return @($lines.ToArray())
}

$compareScript = Join-Path (Get-RepoRoot) "tools\compare-dsp-live-diagnostics-traces.ps1"
if (-not (Test-Path -LiteralPath $compareScript -PathType Leaf)) {
    throw "Missing trace comparator: $compareScript"
}

$resolvedBaselineIndex = (Resolve-Path -LiteralPath $BaselineIndexPath).Path
$resolvedCandidateIndex = (Resolve-Path -LiteralPath $CandidateIndexPath).Path
$resolvedBaselineRoot = if (-not [string]::IsNullOrWhiteSpace($BaselineRoot)) { (Resolve-Path -LiteralPath $BaselineRoot).Path } else { "" }
$resolvedCandidateRoot = if (-not [string]::IsNullOrWhiteSpace($CandidateRoot)) { (Resolve-Path -LiteralPath $CandidateRoot).Path } else { "" }
$bundlePath = ""
if (-not [string]::IsNullOrWhiteSpace($BundleDir)) {
    $bundlePath = (Resolve-Path -LiteralPath $BundleDir).Path
}

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    if (-not [string]::IsNullOrWhiteSpace($bundlePath)) {
        $ReportPath = Join-Path $bundlePath "artifacts\live-diagnostics-trace-comparison.json"
    }
    else {
        $candidateDir = Split-Path -Parent $resolvedCandidateIndex
        $ReportPath = Join-Path $candidateDir "dsp-live-diagnostics-trace-comparison.json"
    }
}

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $reportDir = Split-Path -Parent $ReportPath
    $reportName = [System.IO.Path]::GetFileNameWithoutExtension($ReportPath)
    $OutputDir = Join-Path $reportDir "$reportName.scenarios"
}

if (-not $NoMarkdown -and [string]::IsNullOrWhiteSpace($MarkdownPath)) {
    $reportDir = Split-Path -Parent $ReportPath
    $reportName = [System.IO.Path]::GetFileNameWithoutExtension($ReportPath)
    $MarkdownPath = Join-Path $reportDir "$reportName.md"
}

$baselineEntries = @(Get-TraceIndexEntries -IndexPath $resolvedBaselineIndex -RootOverride $BaselineRoot -ComparisonFilter $BaselineComparisonId)
$candidateEntries = @(Get-TraceIndexEntries -IndexPath $resolvedCandidateIndex -RootOverride $CandidateRoot -ComparisonFilter $CandidateComparisonId)

$baselineByScenario = @{}
$candidateByScenario = @{}
foreach ($entry in $baselineEntries) { Add-Entry -Map $baselineByScenario -Entry $entry }
foreach ($entry in $candidateEntries) { Add-Entry -Map $candidateByScenario -Entry $entry }

$scenarioIds = @($baselineByScenario.Keys + $candidateByScenario.Keys | Select-Object -Unique | Sort-Object)
$scenarioComparisons = New-Object System.Collections.Generic.List[object]
$missingPairs = New-Object System.Collections.Generic.List[object]

$comparisonCount = 0
$failedComparisonCount = 0
$regressionCount = 0
$improvementCount = 0
$hardConstraintRegressionCount = 0
$gateFailureCount = 0
$missingMetricValueCount = 0
$missingBaselineCount = 0
$missingCandidateCount = 0
$metricRegressionDetails = New-Object System.Collections.Generic.List[object]
$hardConstraintRegressionDetails = New-Object System.Collections.Generic.List[object]
$missingMetricDetails = New-Object System.Collections.Generic.List[object]
$gateFailureDetails = New-Object System.Collections.Generic.List[object]
$nr5WeakSignalAccumulator = New-Nr5WeakSignalAccumulator

foreach ($scenarioId in $scenarioIds) {
    $baselineForScenario = @()
    $candidateForScenario = @()
    if ($baselineByScenario.ContainsKey($scenarioId)) {
        $baselineForScenario = @($baselineByScenario[$scenarioId].ToArray())
    }
    if ($candidateByScenario.ContainsKey($scenarioId)) {
        $candidateForScenario = @($candidateByScenario[$scenarioId].ToArray())
    }

    if ($baselineForScenario.Count -eq 0) {
        $missingBaselineCount++
        $missingPairs.Add([ordered]@{
            scenarioId = [string]$scenarioId
            missing = "baseline"
            candidateCount = $candidateForScenario.Count
        }) | Out-Null
        continue
    }
    if ($candidateForScenario.Count -eq 0) {
        $missingCandidateCount++
        $missingPairs.Add([ordered]@{
            scenarioId = [string]$scenarioId
            missing = "candidate"
            baselineCount = $baselineForScenario.Count
        }) | Out-Null
        continue
    }

    $baselineEntry = @($baselineForScenario | Sort-Object comparisonId, inputPath)[0]
    foreach ($candidateEntry in @($candidateForScenario | Sort-Object comparisonId, inputPath)) {
        $comparisonCount++
        $safeScenario = ConvertTo-SafeName $scenarioId
        $safeCandidate = ConvertTo-SafeName $candidateEntry.comparisonId
        if ([string]::IsNullOrWhiteSpace($safeCandidate) -or $safeCandidate -eq "trace") {
            $safeCandidate = ConvertTo-SafeName $CandidateLabel
        }

        $scenarioReportPath = Join-Path $OutputDir "$safeScenario.$safeCandidate.trace-comparison.json"
        $scenarioReportDisplayPath = ConvertTo-PortablePath -Root $bundlePath -Path $scenarioReportPath
        $scenarioRecord = [ordered]@{
            scenarioId = [string]$scenarioId
            baselineComparisonId = [string]$baselineEntry.comparisonId
            candidateComparisonId = [string]$candidateEntry.comparisonId
            baselineInputPath = ConvertTo-PortablePath -Root $bundlePath -Path ([string]$baselineEntry.inputPath)
            candidateInputPath = ConvertTo-PortablePath -Root $bundlePath -Path ([string]$candidateEntry.inputPath)
            reportPath = $scenarioReportDisplayPath
            reportSha256 = $null
            readyForReview = $false
            regressionCount = 0
            improvementCount = 0
            hardConstraintRegressionCount = 0
            gateFailureCount = 0
            missingMetricValueCount = 0
            nr5WeakSignalComparison = $null
            error = $null
        }

        try {
            $scenarioReport = Invoke-TraceComparison `
                -CompareScript $compareScript `
                -BaselineEntry $baselineEntry `
                -CandidateEntry $candidateEntry `
                -ScenarioId $scenarioId `
                -ReportPath $scenarioReportPath `
                -BaselineLabel $BaselineLabel `
                -CandidateLabel $CandidateLabel `
                -Tolerance $Tolerance `
                -BundleDir $bundlePath

            if (Test-Path -LiteralPath $scenarioReportPath -PathType Leaf) {
                $scenarioRecord["reportSha256"] = Get-FileSha256 $scenarioReportPath
            }
            $scenarioRecord["readyForReview"] = Test-Truthy (Get-JsonValue $scenarioReport "readyForReview")
            $scenarioRecord["regressionCount"] = [int](Get-JsonValue $scenarioReport "regressionCount")
            $scenarioRecord["improvementCount"] = [int](Get-JsonValue $scenarioReport "improvementCount")
            $scenarioRecord["hardConstraintRegressionCount"] = [int](Get-JsonValue $scenarioReport "hardConstraintRegressionCount")
            $scenarioRecord["gateFailureCount"] = [int](Get-JsonValue $scenarioReport "gateFailureCount")
            $scenarioRecord["missingMetricValueCount"] = [int](Get-JsonValue $scenarioReport "missingMetricValueCount")
            $scenarioRecord["nr5WeakSignalComparison"] = Get-JsonValue $scenarioReport "nr5WeakSignalComparison"
            Add-Nr5WeakSignalComparison -Accumulator $nr5WeakSignalAccumulator -Comparison $scenarioRecord["nr5WeakSignalComparison"]

            foreach ($metric in (Get-JsonArray $scenarioReport "metricComparisons")) {
                $verdict = [string](Get-JsonValue $metric "verdict")
                if ($verdict -eq "regression") {
                    $metricRegressionDetails.Add([ordered]@{
                        scenarioId = [string]$scenarioId
                        baselineComparisonId = [string]$baselineEntry.comparisonId
                        candidateComparisonId = [string]$candidateEntry.comparisonId
                        metricId = [string](Get-JsonValue $metric "metricId")
                        label = [string](Get-JsonValue $metric "label")
                        direction = [string](Get-JsonValue $metric "direction")
                        safetyClass = [string](Get-JsonValue $metric "safetyClass")
                        baselineValue = Get-JsonValue $metric "baselineValue"
                        candidateValue = Get-JsonValue $metric "candidateValue"
                        improvementValue = Get-JsonValue $metric "improvementValue"
                        threshold = Get-JsonValue $metric "threshold"
                        reportPath = $scenarioReportDisplayPath
                    }) | Out-Null
                }
                elseif ($verdict -eq "missing") {
                    $missingMetricDetails.Add([ordered]@{
                        scenarioId = [string]$scenarioId
                        baselineComparisonId = [string]$baselineEntry.comparisonId
                        candidateComparisonId = [string]$candidateEntry.comparisonId
                        metricId = [string](Get-JsonValue $metric "metricId")
                        label = [string](Get-JsonValue $metric "label")
                        safetyClass = [string](Get-JsonValue $metric "safetyClass")
                        reportPath = $scenarioReportDisplayPath
                    }) | Out-Null
                }
            }

            foreach ($constraint in (Get-JsonArray $scenarioReport "hardConstraintComparisons")) {
                if ([string](Get-JsonValue $constraint "verdict") -eq "regression") {
                    $hardConstraintRegressionDetails.Add([ordered]@{
                        scenarioId = [string]$scenarioId
                        baselineComparisonId = [string]$baselineEntry.comparisonId
                        candidateComparisonId = [string]$candidateEntry.comparisonId
                        constraint = [string](Get-JsonValue $constraint "constraint")
                        baselineCount = Get-JsonValue $constraint "baselineCount"
                        candidateCount = Get-JsonValue $constraint "candidateCount"
                        delta = Get-JsonValue $constraint "delta"
                        reportPath = $scenarioReportDisplayPath
                    }) | Out-Null
                }
            }

            if ([int]$scenarioRecord["gateFailureCount"] -gt 0) {
                $gateFailureDetails.Add([ordered]@{
                    scenarioId = [string]$scenarioId
                    baselineComparisonId = [string]$baselineEntry.comparisonId
                    candidateComparisonId = [string]$candidateEntry.comparisonId
                    candidateReadyForBenchmarkTrace = Test-Truthy (Get-JsonValue $scenarioReport "candidateReadyForBenchmarkTrace")
                    candidateOkSampleCount = Get-JsonValue $scenarioReport "candidateOkSampleCount"
                    candidateFailedSampleCount = Get-JsonValue $scenarioReport "candidateFailedSampleCount"
                    candidateHardBlockerSampleCount = Get-JsonValue $scenarioReport "candidateHardBlockerSampleCount"
                    reportPath = $scenarioReportDisplayPath
                }) | Out-Null
            }
        }
        catch {
            $failedComparisonCount++
            $scenarioRecord["error"] = $_.Exception.Message
        }

        $regressionCount += [int]$scenarioRecord["regressionCount"]
        $improvementCount += [int]$scenarioRecord["improvementCount"]
        $hardConstraintRegressionCount += [int]$scenarioRecord["hardConstraintRegressionCount"]
        $gateFailureCount += [int]$scenarioRecord["gateFailureCount"]
        $missingMetricValueCount += [int]$scenarioRecord["missingMetricValueCount"]
        $scenarioComparisons.Add($scenarioRecord) | Out-Null
    }
}

$readyForReview = ($comparisonCount -gt 0 -and
    $failedComparisonCount -eq 0 -and
    $missingBaselineCount -eq 0 -and
    $missingCandidateCount -eq 0 -and
    $regressionCount -eq 0 -and
    $hardConstraintRegressionCount -eq 0 -and
    $gateFailureCount -eq 0 -and
    $missingMetricValueCount -eq 0)

$nr5WeakSignalComparisonSummary = Complete-Nr5WeakSignalSummary $nr5WeakSignalAccumulator
$metricRegressionSafetyClassCounts = New-DetailSafetyClassCounts -Items @($metricRegressionDetails.ToArray())
$metricMissingSafetyClassCounts = New-DetailSafetyClassCounts -Items @($missingMetricDetails.ToArray())

$recommendations = if ($readyForReview) {
    @("Store this matrix comparison with the baseline/candidate trace indexes, offline fixture metrics, audio renders, spectrum captures, and operator notes before considering any DSP default change.")
}
else {
    @(
        "Do not graduate this DSP candidate; resolve live matrix comparison regressions or missing scenario pairs before on-air acceptance.",
        "Use the per-scenario trace comparison reports to separate DSP regressions from capture setup problems."
    )
}

$report = [ordered]@{
    schemaVersion = 1
    tool = "compare-dsp-live-diagnostics-matrix"
    compatibleTool = "compare-dsp-live-diagnostics-traces"
    generatedUtc = [DateTimeOffset]::UtcNow
    baselineLabel = $BaselineLabel
    candidateLabel = $CandidateLabel
    baselineComparisonId = ConvertTo-ComparisonId $BaselineComparisonId
    candidateComparisonId = ConvertTo-ComparisonId $CandidateComparisonId
    bundleRelativePaths = (-not [string]::IsNullOrWhiteSpace($bundlePath))
    baselineIndexPath = ConvertTo-PortablePath -Root $bundlePath -Path $resolvedBaselineIndex
    baselineIndexSha256 = Get-FileSha256 $resolvedBaselineIndex
    baselineRootPath = if ([string]::IsNullOrWhiteSpace($resolvedBaselineRoot)) { $null } else { ConvertTo-PortablePath -Root $bundlePath -Path $resolvedBaselineRoot }
    candidateIndexPath = ConvertTo-PortablePath -Root $bundlePath -Path $resolvedCandidateIndex
    candidateIndexSha256 = Get-FileSha256 $resolvedCandidateIndex
    candidateRootPath = if ([string]::IsNullOrWhiteSpace($resolvedCandidateRoot)) { $null } else { ConvertTo-PortablePath -Root $bundlePath -Path $resolvedCandidateRoot }
    reportPath = ConvertTo-PortablePath -Root $bundlePath -Path $ReportPath
    markdownPath = if ($NoMarkdown) { $null } else { ConvertTo-PortablePath -Root $bundlePath -Path $MarkdownPath }
    outputDir = ConvertTo-PortablePath -Root $bundlePath -Path $OutputDir
    tolerance = $Tolerance
    readyForReview = $readyForReview
    candidateComparisonCount = $comparisonCount
    failedComparisonCount = $failedComparisonCount
    missingBaselineCount = $missingBaselineCount
    missingCandidateCount = $missingCandidateCount
    regressionCount = $regressionCount
    improvementCount = $improvementCount
    hardConstraintRegressionCount = $hardConstraintRegressionCount
    gateFailureCount = $gateFailureCount
    missingMetricValueCount = $missingMetricValueCount
    nr5WeakSignalComparisonSummary = $nr5WeakSignalComparisonSummary
    metricRegressionSafetyClassCounts = @($metricRegressionSafetyClassCounts)
    metricMissingSafetyClassCounts = @($metricMissingSafetyClassCounts)
    metricRegressionDetails = @($metricRegressionDetails.ToArray())
    hardConstraintRegressionDetails = @($hardConstraintRegressionDetails.ToArray())
    gateFailureDetails = @($gateFailureDetails.ToArray())
    missingMetricDetails = @($missingMetricDetails.ToArray())
    scenarioComparisons = @($scenarioComparisons.ToArray())
    missingScenarioPairs = @($missingPairs.ToArray())
    recommendations = $recommendations
}

Write-JsonFile -Path $ReportPath -Value $report

if (-not $NoMarkdown) {
    $markdown = Build-MarkdownReport $report
    Set-Content -LiteralPath $MarkdownPath -Value $markdown -Encoding UTF8
}

if ($JsonOnly) {
    $report | ConvertTo-Json -Depth 64
}
else {
    if ($readyForReview) {
        Write-Host "DSP live diagnostics matrix comparison passed: $CandidateIndexPath"
    }
    else {
        Write-Host "DSP live diagnostics matrix comparison found issues: $CandidateIndexPath"
    }
    Write-Host "Report: $ReportPath"
    if (-not $NoMarkdown) {
        Write-Host "Markdown: $MarkdownPath"
    }
    Write-Host "Scenarios: $comparisonCount, Regressions: $regressionCount, Hard constraint regressions: $hardConstraintRegressionCount, Gate failures: $gateFailureCount, Missing values: $missingMetricValueCount"
}

if ($FailOnRegression -and -not $readyForReview) {
    exit 1
}
