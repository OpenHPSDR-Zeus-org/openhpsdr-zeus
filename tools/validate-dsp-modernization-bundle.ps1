param(
    [Parameter(Mandatory = $true)]
    [string]$BundleDir,

    [string]$ReportPath = "",

    [switch]$AllowPreflight,

    [string]$ArtifactManifestPath = "",

    [switch]$RequireArtifactFiles,

    [switch]$JsonOnly
)

$ErrorActionPreference = "Stop"

function Read-JsonFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    try {
        return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        throw "Failed to parse JSON file '$Path': $($_.Exception.Message)"
    }
}

function Add-ValidationIssue {
    param(
        [System.Collections.Generic.List[object]]$Target,
        [Parameter(Mandatory = $true)][string]$Severity,
        [Parameter(Mandatory = $true)][string]$Code,
        [Parameter(Mandatory = $true)][string]$Message
    )

    $Target.Add([ordered]@{
        severity = $Severity
        code = $Code
        message = $Message
    }) | Out-Null
}

function Add-AcceptanceIssue {
    param(
        [System.Collections.Generic.List[object]]$Errors,
        [System.Collections.Generic.List[object]]$Warnings,
        [switch]$AllowPreflight,
        [Parameter(Mandatory = $true)][string]$Code,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if ($AllowPreflight) {
        Add-ValidationIssue $Warnings "warning" $Code $Message
    }
    else {
        Add-ValidationIssue $Errors "error" $Code $Message
    }
}

function Add-ArtifactIssue {
    param(
        [System.Collections.Generic.List[object]]$Errors,
        [System.Collections.Generic.List[object]]$Warnings,
        [Parameter(Mandatory = $true)][bool]$Required,
        [Parameter(Mandatory = $true)][string]$Code,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if ($Required) {
        Add-ValidationIssue $Errors "error" $Code $Message
    }
    else {
        Add-ValidationIssue $Warnings "warning" $Code $Message
    }
}

function Get-EndpointById {
    param(
        [Parameter(Mandatory = $true)]$Index,
        [Parameter(Mandatory = $true)][string]$Id
    )

    foreach ($endpoint in @($Index.endpoints)) {
        if ($endpoint.id -eq $Id) {
            return $endpoint
        }
    }
    return $null
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
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
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

function Get-NumericValueOrDefault {
    param(
        $Value,
        [double]$Default = 0.0
    )

    $numeric = Get-NumericValue $Value
    if ($null -eq $numeric) {
        return $Default
    }

    return $numeric
}

function Get-DateTimeOffsetValue {
    param($Value)

    if ($null -eq $Value) {
        return $null
    }

    if ($Value -is [DateTimeOffset]) {
        return $Value
    }

    if ($Value -is [DateTime]) {
        return [DateTimeOffset]$Value
    }

    $parsed = [DateTimeOffset]::MinValue
    if ([DateTimeOffset]::TryParse([string]$Value, [ref]$parsed)) {
        return $parsed
    }

    return $null
}

function Format-DateTimeOffsetUtcString {
    param($Value)

    $date = Get-DateTimeOffsetValue $Value
    if ($null -eq $date) {
        return ""
    }

    return $date.UtcDateTime.ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
}

function Get-RegressionCountFromSummary {
    param(
        $Summary,
        [Parameter(Mandatory = $true)][string]$CountName,
        [Parameter(Mandatory = $true)][string]$FlagName
    )

    $count = Get-NumericValue (Get-JsonValue $Summary $CountName)
    if ($null -ne $count) {
        return [int][Math]::Round($count)
    }

    if (Test-Truthy (Get-JsonValue $Summary $FlagName)) {
        return 1
    }

    return 0
}

function New-SafetyClassCountsFromItems {
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

function Get-LiveTraceSafetyClassCounts {
    param(
        $ArtifactJson,
        [Parameter(Mandatory = $true)][string]$CountsName,
        [Parameter(Mandatory = $true)][string]$DetailsName,
        [string]$MetricComparisonsVerdict = ""
    )

    $counts = @(Get-JsonArray $ArtifactJson $CountsName)
    if ($counts.Count -gt 0) {
        return @($counts)
    }

    $details = @(Get-JsonArray $ArtifactJson $DetailsName)
    if ($details.Count -gt 0) {
        return @(New-SafetyClassCountsFromItems -Items $details)
    }

    if (-not [string]::IsNullOrWhiteSpace($MetricComparisonsVerdict)) {
        return @(New-SafetyClassCountsFromItems `
            -Items @(Get-JsonArray $ArtifactJson "metricComparisons") `
            -Verdict $MetricComparisonsVerdict)
    }

    return @()
}

function Get-SafetyClassCount {
    param(
        $Counts,
        [Parameter(Mandatory = $true)][string]$SafetyClass
    )

    foreach ($item in @($Counts)) {
        if ([string]::Equals([string](Get-JsonValue $item "safetyClass"), $SafetyClass, [StringComparison]::OrdinalIgnoreCase)) {
            $count = Get-NumericValue (Get-JsonValue $item "count")
            if ($null -ne $count) {
                return [int][Math]::Round($count)
            }
        }
    }

    return 0
}

function Get-TotalSafetyClassCount {
    param($Counts)

    $total = 0
    foreach ($item in @($Counts)) {
        $count = Get-NumericValue (Get-JsonValue $item "count")
        if ($null -ne $count) {
            $total += [int][Math]::Round($count)
        }
    }

    return $total
}

function Get-LiveHistorySignalUnit {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        $Value,
        $Threshold
    )

    if ($Name -match "(^|-)db(fs)?($|-)") {
        return "dB"
    }
    if ($Name -match "(^|-)pct($|-)") {
        return "percent"
    }
    if ($Name -match "(^|-)samples($|-)|dropouts|blockers") {
        return "samples"
    }
    if ($Name -match "(^|-)count($|-)") {
        return "count"
    }
    if ($Value -is [bool] -or $Threshold -is [bool]) {
        return "boolean"
    }
    if ($Name -match "drive|movement") {
        return "unitless"
    }

    return "value"
}

function Get-LiveHistorySignalReadiness {
    param($Signal)

    $name = [string](Get-JsonValue $Signal "name")
    if ([string]::IsNullOrWhiteSpace($name)) {
        $name = [string](Get-JsonValue $Signal "code")
    }

    $value = Get-JsonValue $Signal "value"
    $threshold = Get-JsonValue $Signal "threshold"
    $valueNumber = Get-NumericValue $value
    $thresholdNumber = Get-NumericValue $threshold
    $direction = [string](Get-JsonValue $Signal "thresholdDirection")
    if ([string]::IsNullOrWhiteSpace($direction)) {
        if ($null -ne $valueNumber -and $null -ne $thresholdNumber) {
            if ($valueNumber -gt $thresholdNumber) {
                $direction = "maximum"
            }
            elseif ($valueNumber -lt $thresholdNumber) {
                $direction = "minimum"
            }
            else {
                $direction = "equals"
            }
        }
        else {
            $direction = "equals"
        }
    }

    $unit = [string](Get-JsonValue $Signal "unit")
    if ([string]::IsNullOrWhiteSpace($unit)) {
        $unit = [string](Get-JsonValue $Signal "readinessGapUnit")
    }
    if ([string]::IsNullOrWhiteSpace($unit)) {
        $unit = Get-LiveHistorySignalUnit -Name $name -Value $value -Threshold $threshold
    }

    $comparison = "mismatch"
    $gap = $null
    $margin = $null
    if ($direction -eq "maximum" -and $null -ne $valueNumber -and $null -ne $thresholdNumber) {
        $comparison = if ($valueNumber -gt $thresholdNumber) { "greater-than-maximum" } else { "within-threshold" }
        $gap = [Math]::Round([Math]::Max(0.0, $valueNumber - $thresholdNumber), 3)
        $margin = [Math]::Round($thresholdNumber - $valueNumber, 3)
    }
    elseif ($direction -eq "minimum" -and $null -ne $valueNumber -and $null -ne $thresholdNumber) {
        $comparison = if ($valueNumber -lt $thresholdNumber) { "less-than-minimum" } else { "within-threshold" }
        $gap = [Math]::Round([Math]::Max(0.0, $thresholdNumber - $valueNumber), 3)
        $margin = [Math]::Round($valueNumber - $thresholdNumber, 3)
    }
    elseif ($direction -eq "equals" -and ($value -is [bool] -or $threshold -is [bool])) {
        $mismatch = ([bool]$value -ne [bool]$threshold)
        $comparison = if ($mismatch) { "boolean-mismatch" } else { "equals-threshold" }
        $gap = if ($mismatch) { 1.0 } else { 0.0 }
        $margin = -1.0 * $gap
    }
    elseif ($direction -eq "equals" -and $null -ne $valueNumber -and $null -ne $thresholdNumber) {
        $gap = [Math]::Round([Math]::Abs($valueNumber - $thresholdNumber), 3)
        $margin = -1.0 * $gap
        $comparison = if ($gap -gt 0.001) { "numeric-mismatch" } else { "equals-threshold" }
    }
    else {
        $mismatch = ([string]$value -ne [string]$threshold)
        $comparison = if ($mismatch) { "mismatch" } else { "equals-threshold" }
        $gap = if ($mismatch) { 1.0 } else { 0.0 }
        $margin = -1.0 * $gap
    }

    return [ordered]@{
        thresholdDirection = $direction
        unit = $unit
        readinessGap = $gap
        readinessMargin = $margin
        readinessGapScore = $gap
        comparison = $comparison
    }
}

function Add-LiveHistoryReadinessBucket {
    param(
        [Parameter(Mandatory = $true)]$Buckets,
        [Parameter(Mandatory = $true)][string]$SafetyClass,
        [Parameter(Mandatory = $true)][string]$Unit,
        $Gap
    )

    $key = "$SafetyClass|$Unit"
    if (-not $Buckets.ContainsKey($key)) {
        $Buckets[$key] = [ordered]@{
            safetyClass = $SafetyClass
            readinessGapUnit = $Unit
            signalCount = 0
            numericSignalCount = 0
            nonNumericSignalCount = 0
            readinessGapTotal = 0.0
            readinessGapMax = $null
        }
    }

    $bucket = $Buckets[$key]
    $bucket.signalCount = [int]$bucket.signalCount + 1
    $numericGap = Get-NumericValue $Gap
    if ($null -eq $numericGap) {
        $bucket.nonNumericSignalCount = [int]$bucket.nonNumericSignalCount + 1
        return
    }

    $bucket.numericSignalCount = [int]$bucket.numericSignalCount + 1
    $bucket.readinessGapTotal = [Math]::Round([double]$bucket.readinessGapTotal + $numericGap, 3)
    if ($null -eq $bucket.readinessGapMax -or $numericGap -gt [double]$bucket.readinessGapMax) {
        $bucket.readinessGapMax = [Math]::Round($numericGap, 3)
    }
}

function New-LiveHistorySafetyClassCountsFromSignals {
    param($Signals)

    $counts = @{}
    foreach ($signal in @($Signals)) {
        $safetyClass = [string](Get-JsonValue $signal "safetyClass")
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

function New-LiveHistoryReadinessSummaryFromSignals {
    param($Signals)

    $buckets = @{}
    foreach ($signal in @($Signals)) {
        $safetyClass = [string](Get-JsonValue $signal "safetyClass")
        if ([string]::IsNullOrWhiteSpace($safetyClass)) {
            $safetyClass = "unknown"
        }

        $readiness = Get-LiveHistorySignalReadiness $signal
        Add-LiveHistoryReadinessBucket -Buckets $buckets -SafetyClass $safetyClass -Unit ([string]$readiness.unit) -Gap $readiness.readinessGap
    }

    $result = New-Object System.Collections.Generic.List[object]
    foreach ($key in @($buckets.Keys | Sort-Object)) {
        $bucket = $buckets[$key]
        if ([int]$bucket.numericSignalCount -le 0) {
            $bucket.readinessGapTotal = $null
            $bucket.readinessGapMax = $null
        }
        else {
            $bucket.readinessGapTotal = [Math]::Round([double]$bucket.readinessGapTotal, 3)
            $bucket.readinessGapMax = [Math]::Round([double]$bucket.readinessGapMax, 3)
        }
        $result.Add($bucket) | Out-Null
    }

    return @($result.ToArray())
}

function Get-LiveHistorySummaryEntry {
    param(
        $Summary,
        [Parameter(Mandatory = $true)][string]$SafetyClass,
        [Parameter(Mandatory = $true)][string]$Unit
    )

    foreach ($item in @($Summary)) {
        if ([string]::Equals([string](Get-JsonValue $item "safetyClass"), $SafetyClass, [StringComparison]::OrdinalIgnoreCase) -and
            [string]::Equals([string](Get-JsonValue $item "readinessGapUnit"), $Unit, [StringComparison]::OrdinalIgnoreCase)) {
            return $item
        }
    }

    return $null
}

function Test-NumericClose {
    param(
        $Actual,
        $Expected,
        [double]$Epsilon = 0.001
    )

    $actualNumber = Get-NumericValue $Actual
    $expectedNumber = Get-NumericValue $Expected
    if ($null -eq $actualNumber -and $null -eq $expectedNumber) {
        return $true
    }
    if ($null -eq $actualNumber -or $null -eq $expectedNumber) {
        return $false
    }

    return ([Math]::Abs($actualNumber - $expectedNumber) -le $Epsilon)
}

function Test-LiveHistoryReadinessSummaryMatches {
    param(
        $Actual,
        $Expected
    )

    $actualItems = @($Actual | Where-Object {
        $null -ne $_ -and
        -not [string]::IsNullOrWhiteSpace([string](Get-JsonValue $_ "safetyClass")) -and
        -not [string]::IsNullOrWhiteSpace([string](Get-JsonValue $_ "readinessGapUnit"))
    })
    $expectedItems = @($Expected | Where-Object {
        $null -ne $_ -and
        -not [string]::IsNullOrWhiteSpace([string](Get-JsonValue $_ "safetyClass")) -and
        -not [string]::IsNullOrWhiteSpace([string](Get-JsonValue $_ "readinessGapUnit"))
    })

    if ($actualItems.Count -ne $expectedItems.Count) {
        return $false
    }

    foreach ($expectedItem in @($expectedItems)) {
        $safetyClass = [string](Get-JsonValue $expectedItem "safetyClass")
        $unit = [string](Get-JsonValue $expectedItem "readinessGapUnit")
        $actualItem = Get-LiveHistorySummaryEntry -Summary $actualItems -SafetyClass $safetyClass -Unit $unit
        if ($null -eq $actualItem) {
            return $false
        }

        foreach ($field in @("signalCount", "numericSignalCount", "nonNumericSignalCount")) {
            if ([int](Get-NumericValueOrDefault (Get-JsonValue $actualItem $field) -1) -ne [int](Get-NumericValueOrDefault (Get-JsonValue $expectedItem $field) -1)) {
                return $false
            }
        }
        foreach ($field in @("readinessGapTotal", "readinessGapMax")) {
            if (-not (Test-NumericClose -Actual (Get-JsonValue $actualItem $field) -Expected (Get-JsonValue $expectedItem $field))) {
                return $false
            }
        }
    }

    return $true
}

function Test-LiveHistorySafetyClassCountsMatch {
    param(
        $Actual,
        $Expected
    )

    $actualItems = @($Actual | Where-Object {
        $null -ne $_ -and -not [string]::IsNullOrWhiteSpace([string](Get-JsonValue $_ "safetyClass"))
    })
    $expectedItems = @($Expected | Where-Object {
        $null -ne $_ -and -not [string]::IsNullOrWhiteSpace([string](Get-JsonValue $_ "safetyClass"))
    })

    if ($actualItems.Count -ne $expectedItems.Count) {
        return $false
    }

    foreach ($expectedItem in @($expectedItems)) {
        $safetyClass = [string](Get-JsonValue $expectedItem "safetyClass")
        if ((Get-SafetyClassCount -Counts $actualItems -SafetyClass $safetyClass) -ne (Get-SafetyClassCount -Counts $expectedItems -SafetyClass $safetyClass)) {
            return $false
        }
    }

    return $true
}

function Get-LiveHistoryReadinessSummarySignalCount {
    param($Summary)

    $total = 0
    foreach ($item in @($Summary)) {
        $total += [int](Get-NumericValueOrDefault (Get-JsonValue $item "signalCount"))
    }

    return $total
}

function Get-LiveHistoryReadinessSummaryNumericSignalCount {
    param($Summary)

    $total = 0
    foreach ($item in @($Summary)) {
        $total += [int](Get-NumericValueOrDefault (Get-JsonValue $item "numericSignalCount"))
    }

    return $total
}

function Get-LiveHistoryReadinessSummaryMax {
    param($Summary)

    $max = $null
    foreach ($item in @($Summary)) {
        $value = Get-NumericValue (Get-JsonValue $item "readinessGapMax")
        if ($null -ne $value -and ($null -eq $max -or $value -gt $max)) {
            $max = $value
        }
    }

    return $max
}

function Get-LiveHistoryTrendSignalKey {
    param($Signal)

    $safetyClass = [string](Get-JsonValue $Signal "safetyClass")
    if ([string]::IsNullOrWhiteSpace($safetyClass)) {
        $safetyClass = "unknown"
    }

    $unit = [string](Get-JsonValue $Signal "readinessGapUnit")
    if ([string]::IsNullOrWhiteSpace($unit)) {
        $unit = [string](Get-JsonValue $Signal "unit")
    }
    if ([string]::IsNullOrWhiteSpace($unit)) {
        $unit = "value"
    }

    $name = [string](Get-JsonValue $Signal "name")
    if ([string]::IsNullOrWhiteSpace($name)) {
        $name = [string](Get-JsonValue $Signal "code")
    }
    if ([string]::IsNullOrWhiteSpace($name)) {
        $name = "safety-signal"
    }

    return "$($safetyClass.ToLowerInvariant())|$($unit.ToLowerInvariant())|$($name.ToLowerInvariant())"
}

function New-LiveHistoryTrendSignalMap {
    param($Signals)

    $map = @{}
    foreach ($signal in @($Signals)) {
        $map[(Get-LiveHistoryTrendSignalKey $signal)] = $signal
    }

    return ,$map
}

function Get-LiveHistoryTrendOverallStatus {
    param(
        [int]$ImprovedCount,
        [int]$RegressedCount,
        [int]$ClearedCount,
        [int]$NewCount,
        [int]$UnchangedCount
    )

    $goodCount = $ImprovedCount + $ClearedCount
    $badCount = $RegressedCount + $NewCount
    if ($badCount -gt 0 -and $goodCount -gt 0) {
        return "mixed"
    }
    if ($badCount -gt 0) {
        return "regressed"
    }
    if ($goodCount -gt 0) {
        return "improved"
    }
    if ($UnchangedCount -gt 0) {
        return "unchanged"
    }

    return "no-signals"
}

function Add-LiveHistoryTrendClassBucket {
    param(
        [Parameter(Mandatory = $true)]$Buckets,
        [Parameter(Mandatory = $true)]$Detail
    )

    $safetyClass = [string](Get-JsonValue $Detail "safetyClass")
    if ([string]::IsNullOrWhiteSpace($safetyClass)) {
        $safetyClass = "unknown"
    }
    $unit = [string](Get-JsonValue $Detail "readinessGapUnit")
    if ([string]::IsNullOrWhiteSpace($unit)) {
        $unit = "value"
    }

    $key = "$safetyClass|$unit"
    if (-not $Buckets.ContainsKey($key)) {
        $Buckets[$key] = [ordered]@{
            safetyClass = $safetyClass
            readinessGapUnit = $unit
            signalCount = 0
            improvedSignalCount = 0
            regressedSignalCount = 0
            unchangedSignalCount = 0
            clearedSignalCount = 0
            newSignalCount = 0
            narrowedSignalCount = 0
            widenedSignalCount = 0
            resolvedSignalCount = 0
            previousReadinessGapTotal = 0.0
            latestReadinessGapTotal = 0.0
            readinessGapDeltaTotal = 0.0
            readinessGapDeltaMin = $null
            readinessGapDeltaMax = $null
        }
    }

    $bucket = $Buckets[$key]
    $status = [string](Get-JsonValue $Detail "trendStatus")
    if ([string]::IsNullOrWhiteSpace($status)) {
        $status = [string](Get-JsonValue $Detail "status")
    }
    $bucket.signalCount = [int]$bucket.signalCount + 1
    switch ($status) {
        "gap-narrowed" {
            $bucket.narrowedSignalCount = [int]$bucket.narrowedSignalCount + 1
            $bucket.improvedSignalCount = [int]$bucket.improvedSignalCount + 1
        }
        "gap-widened" {
            $bucket.widenedSignalCount = [int]$bucket.widenedSignalCount + 1
            $bucket.regressedSignalCount = [int]$bucket.regressedSignalCount + 1
        }
        "unchanged" { $bucket.unchangedSignalCount = [int]$bucket.unchangedSignalCount + 1 }
        "resolved-gap" {
            $bucket.resolvedSignalCount = [int]$bucket.resolvedSignalCount + 1
            $bucket.clearedSignalCount = [int]$bucket.clearedSignalCount + 1
            $bucket.improvedSignalCount = [int]$bucket.improvedSignalCount + 1
        }
        "new-gap" {
            $bucket.newSignalCount = [int]$bucket.newSignalCount + 1
            $bucket.regressedSignalCount = [int]$bucket.regressedSignalCount + 1
        }
    }

    $previousGap = Get-NumericValue (Get-JsonValue $Detail "previousReadinessGap")
    $latestGap = Get-NumericValue (Get-JsonValue $Detail "latestReadinessGap")
    $delta = Get-NumericValue (Get-JsonValue $Detail "readinessGapDelta")
    if ($null -ne $previousGap) {
        $bucket.previousReadinessGapTotal = [Math]::Round([double]$bucket.previousReadinessGapTotal + $previousGap, 3)
    }
    if ($null -ne $latestGap) {
        $bucket.latestReadinessGapTotal = [Math]::Round([double]$bucket.latestReadinessGapTotal + $latestGap, 3)
    }
    if ($null -ne $delta) {
        $bucket.readinessGapDeltaTotal = [Math]::Round([double]$bucket.readinessGapDeltaTotal + $delta, 3)
        if ($null -eq $bucket.readinessGapDeltaMin -or $delta -lt [double]$bucket.readinessGapDeltaMin) {
            $bucket.readinessGapDeltaMin = [Math]::Round($delta, 3)
        }
        if ($null -eq $bucket.readinessGapDeltaMax -or $delta -gt [double]$bucket.readinessGapDeltaMax) {
            $bucket.readinessGapDeltaMax = [Math]::Round($delta, 3)
        }
    }
}

function New-LiveHistoryReadinessTrendClassSummary {
    param($Details)

    $buckets = @{}
    foreach ($detail in @($Details)) {
        Add-LiveHistoryTrendClassBucket -Buckets $buckets -Detail $detail
    }

    $result = New-Object System.Collections.Generic.List[object]
    foreach ($key in @($buckets.Keys | Sort-Object)) {
        $bucket = $buckets[$key]
        $bucket.previousReadinessGapTotal = [Math]::Round([double]$bucket.previousReadinessGapTotal, 3)
        $bucket.latestReadinessGapTotal = [Math]::Round([double]$bucket.latestReadinessGapTotal, 3)
        $bucket.readinessGapDeltaTotal = [Math]::Round([double]$bucket.readinessGapDeltaTotal, 3)
        if ($null -ne $bucket.readinessGapDeltaMin) {
            $bucket.readinessGapDeltaMin = [Math]::Round([double]$bucket.readinessGapDeltaMin, 3)
        }
        if ($null -ne $bucket.readinessGapDeltaMax) {
            $bucket.readinessGapDeltaMax = [Math]::Round([double]$bucket.readinessGapDeltaMax, 3)
        }
        $bucket["status"] = Get-LiveHistoryTrendOverallStatus `
            -ImprovedCount ([int]$bucket.improvedSignalCount) `
            -RegressedCount ([int]$bucket.regressedSignalCount) `
            -ClearedCount ([int]$bucket.resolvedSignalCount) `
            -NewCount ([int]$bucket.newSignalCount) `
            -UnchangedCount ([int]$bucket.unchangedSignalCount)
        $result.Add($bucket) | Out-Null
    }

    return @($result.ToArray())
}

function New-LiveHistoryReadinessTrend {
    param(
        $Latest,
        $Previous
    )

    if ($null -eq $Latest) {
        return [ordered]@{
            status = "no-nr5-history"
            latestTraceId = ""
            previousTraceId = if ($null -eq $Previous) { "" } else { [string](Get-JsonValue $Previous "traceId") }
            latestReadinessGapSignalCount = 0
            previousReadinessGapSignalCount = 0
            readinessGapSignalCountDelta = 0
            latestReadinessGapMax = $null
            previousReadinessGapMax = $null
            readinessGapMaxDelta = $null
            improvedSignalCount = 0
            regressedSignalCount = 0
            narrowedSignalCount = 0
            widenedSignalCount = 0
            unchangedSignalCount = 0
            clearedSignalCount = 0
            resolvedSignalCount = 0
            newSignalCount = 0
            comparedSignalCount = 0
            incomparableSignalCount = 0
            persistentSignalCount = 0
            detailCount = 0
            signalCount = 0
            safetyClassUnitTrend = @()
            readinessGapTrendBuckets = @()
            details = @()
            readinessGapTrendDetails = @()
        }
    }

    $latestSignals = @(Get-JsonArray $Latest "safetySignals")
    $previousSignals = if ($null -eq $Previous) { @() } else { @(Get-JsonArray $Previous "safetySignals") }
    $latestMap = New-LiveHistoryTrendSignalMap -Signals $latestSignals
    $previousMap = New-LiveHistoryTrendSignalMap -Signals $previousSignals
    $keys = New-Object System.Collections.Generic.List[string]
    foreach ($key in @($latestMap.Keys)) {
        $keys.Add([string]$key) | Out-Null
    }
    foreach ($key in @($previousMap.Keys)) {
        if (-not $latestMap.ContainsKey($key)) {
            $keys.Add([string]$key) | Out-Null
        }
    }

    $details = New-Object System.Collections.Generic.List[object]
    $narrowedCount = 0
    $widenedCount = 0
    $unchangedCount = 0
    $resolvedCount = 0
    $newCount = 0
    foreach ($key in @($keys.ToArray() | Sort-Object)) {
        $latestSignal = if ($latestMap.ContainsKey($key)) { $latestMap[$key] } else { $null }
        $previousSignal = if ($previousMap.ContainsKey($key)) { $previousMap[$key] } else { $null }
        $signal = if ($null -ne $latestSignal) { $latestSignal } else { $previousSignal }
        $latestGap = if ($null -eq $latestSignal) { 0.0 } else { Get-NumericValue (Get-JsonValue $latestSignal "readinessGap") }
        $previousGap = if ($null -eq $previousSignal) { 0.0 } else { Get-NumericValue (Get-JsonValue $previousSignal "readinessGap") }
        if ($null -eq $latestGap) {
            $latestGap = 0.0
        }
        if ($null -eq $previousGap) {
            $previousGap = 0.0
        }

        $delta = [Math]::Round($latestGap - $previousGap, 3)
        $status = "unchanged"
        if ($null -eq $previousSignal -and $null -ne $latestSignal) {
            $status = "new-gap"
            $newCount++
        }
        elseif ($null -ne $previousSignal -and $null -eq $latestSignal) {
            $status = "resolved-gap"
            $resolvedCount++
        }
        elseif ([Math]::Abs($delta) -le 0.001) {
            $status = "unchanged"
            $unchangedCount++
        }
        elseif ($delta -lt 0.0) {
            $status = "gap-narrowed"
            $narrowedCount++
        }
        else {
            $status = "gap-widened"
            $widenedCount++
        }

        $safetyClass = [string](Get-JsonValue $signal "safetyClass")
        if ([string]::IsNullOrWhiteSpace($safetyClass)) {
            $safetyClass = "unknown"
        }
        $name = [string](Get-JsonValue $signal "name")
        if ([string]::IsNullOrWhiteSpace($name)) {
            $name = "safety-signal"
        }
        $unit = [string](Get-JsonValue $signal "readinessGapUnit")
        if ([string]::IsNullOrWhiteSpace($unit)) {
            $unit = [string](Get-JsonValue $signal "unit")
        }
        if ([string]::IsNullOrWhiteSpace($unit)) {
            $unit = "value"
        }

        $details.Add([ordered]@{
            key = $key
            safetyClass = $safetyClass
            name = $name
            readinessGapUnit = $unit
            status = $status
            trendStatus = $status
            previousReadinessGap = [Math]::Round($previousGap, 3)
            latestReadinessGap = [Math]::Round($latestGap, 3)
            readinessGapDelta = $delta
            previousValue = if ($null -eq $previousSignal) { $null } else { Get-JsonValue $previousSignal "value" }
            latestValue = if ($null -eq $latestSignal) { $null } else { Get-JsonValue $latestSignal "value" }
            threshold = Get-JsonValue $signal "threshold"
            previousThreshold = if ($null -eq $previousSignal) { $null } else { Get-JsonValue $previousSignal "threshold" }
            latestThreshold = if ($null -eq $latestSignal) { $null } else { Get-JsonValue $latestSignal "threshold" }
            thresholdDirection = Get-JsonValue $signal "thresholdDirection"
        }) | Out-Null
    }

    $latestSummary = @(Get-JsonArray $Latest "readinessGapSummary")
    $previousSummary = if ($null -eq $Previous) { @() } else { @(Get-JsonArray $Previous "readinessGapSummary") }
    $latestMax = Get-LiveHistoryReadinessSummaryMax -Summary $latestSummary
    $previousMax = if ($null -eq $Previous) { $null } else { Get-LiveHistoryReadinessSummaryMax -Summary $previousSummary }
    $maxDelta = if ($null -eq $latestMax -or $null -eq $previousMax) { $null } else { [Math]::Round($latestMax - $previousMax, 3) }
    $improvedCount = $narrowedCount + $resolvedCount
    $regressedCount = $widenedCount + $newCount
    $statusValue = if ($null -eq $Previous) {
        "no-previous-nr5-trace"
    }
    else {
        Get-LiveHistoryTrendOverallStatus -ImprovedCount $improvedCount -RegressedCount $regressedCount -ClearedCount $resolvedCount -NewCount $newCount -UnchangedCount $unchangedCount
    }
    $buckets = @(New-LiveHistoryReadinessTrendClassSummary -Details @($details.ToArray()))

    return [ordered]@{
        status = $statusValue
        latestTraceId = [string](Get-JsonValue $Latest "traceId")
        previousTraceId = if ($null -eq $Previous) { "" } else { [string](Get-JsonValue $Previous "traceId") }
        latestReadinessGapSignalCount = $latestMap.Count
        previousReadinessGapSignalCount = $previousMap.Count
        readinessGapSignalCountDelta = $latestMap.Count - $previousMap.Count
        latestReadinessGapMax = $latestMax
        previousReadinessGapMax = $previousMax
        readinessGapMaxDelta = $maxDelta
        improvedSignalCount = $improvedCount
        regressedSignalCount = $regressedCount
        narrowedSignalCount = $narrowedCount
        widenedSignalCount = $widenedCount
        unchangedSignalCount = $unchangedCount
        clearedSignalCount = $resolvedCount
        resolvedSignalCount = $resolvedCount
        newSignalCount = $newCount
        comparedSignalCount = $narrowedCount + $widenedCount + $unchangedCount
        incomparableSignalCount = 0
        persistentSignalCount = $narrowedCount + $widenedCount + $unchangedCount
        detailCount = $details.Count
        signalCount = $details.Count
        safetyClassUnitTrend = @($buckets)
        readinessGapTrendBuckets = @($buckets)
        details = @($details.ToArray())
        readinessGapTrendDetails = @($details.ToArray())
    }
}

function Add-LiveHistoryReadinessTrendContext {
    param(
        [Parameter(Mandatory = $true)]$Trend,
        [Parameter(Mandatory = $true)][string]$ComparisonScope,
        [string]$LatestTraceId = "",
        [string]$ReferenceTraceId = "",
        [string]$ReferenceTraceRole = "",
        [string]$RecommendedTraceId = ""
    )

    $Trend["comparisonScope"] = $ComparisonScope
    $Trend["referenceTraceId"] = $ReferenceTraceId
    $Trend["referenceTraceRole"] = $ReferenceTraceRole
    $Trend["latestIsReference"] = (-not [string]::IsNullOrWhiteSpace($LatestTraceId) -and [string]::Equals($LatestTraceId, $ReferenceTraceId, [StringComparison]::OrdinalIgnoreCase))
    $Trend["referenceIsRecommended"] = (-not [string]::IsNullOrWhiteSpace($RecommendedTraceId) -and [string]::Equals($RecommendedTraceId, $ReferenceTraceId, [StringComparison]::OrdinalIgnoreCase))
    return $Trend
}

function Get-LiveHistoryTraceRecordById {
    param(
        $Records,
        [string]$TraceId
    )

    if ([string]::IsNullOrWhiteSpace($TraceId)) {
        return $null
    }

    foreach ($record in @($Records)) {
        if ([string]::Equals([string](Get-JsonValue $record "traceId"), $TraceId, [StringComparison]::OrdinalIgnoreCase)) {
            return $record
        }
    }

    return $null
}

function Select-LiveHistoryTraceForSummary {
    param(
        $Records,
        [Parameter(Mandatory = $true)][string]$Mode
    )

    $items = @($Records | Where-Object { [int](Get-NumericValueOrDefault (Get-JsonValue $_ "nr5SampleCount")) -gt 0 })
    if ($items.Count -eq 0) {
        return $null
    }

    switch ($Mode) {
        "latest" {
            return @($items | Sort-Object `
                @{ Expression = {
                    $sequence = Get-DateTimeOffsetValue (Get-JsonValue $_ "traceSequenceUtc")
                    if ($null -eq $sequence) { [Int64]::MinValue } else { $sequence.UtcDateTime.Ticks }
                }; Descending = $true },
                @{ Expression = { [string](Get-JsonValue $_ "traceId") }; Descending = $true },
                @{ Expression = { [string](Get-JsonValue $_ "path") }; Descending = $true })[0]
        }
        "best-weak" {
            return @($items | Sort-Object `
                @{ Expression = { Get-NumericValue (Get-JsonValue $_ "weakDropoutSampleCount") }; Ascending = $true },
                @{ Expression = { $value = Get-NumericValue (Get-JsonValue $_ "weakRecoveryPct"); if ($null -eq $value) { -1.0 } else { $value } }; Ascending = $false },
                @{ Expression = { $value = Get-NumericValue (Get-JsonValue $_ "nr5OutputMovementDb"); if ($null -eq $value) { 999.0 } else { $value } }; Ascending = $true },
                @{ Expression = { Get-NumericValue (Get-JsonValue $_ "safetyRiskScore") }; Ascending = $true })[0]
        }
        "lowest-pumping" {
            return @($items | Sort-Object `
                @{ Expression = { Get-SafetyClassCount -Counts (Get-JsonArray $_ "safetyClassCounts") -SafetyClass "pumping" }; Ascending = $true },
                @{ Expression = { $value = Get-NumericValue (Get-JsonValue $_ "nr5OutputMovementDb"); if ($null -eq $value) { 999.0 } else { $value } }; Ascending = $true },
                @{ Expression = { $value = Get-NumericValue (Get-JsonValue $_ "audioRmsMovementDb"); if ($null -eq $value) { 999.0 } else { $value } }; Ascending = $true })[0]
        }
        default {
            return @($items | Sort-Object `
                @{ Expression = { Get-NumericValue (Get-JsonValue $_ "safetyRiskScore") }; Ascending = $true },
                @{ Expression = { Get-NumericValue (Get-JsonValue $_ "weakDropoutSampleCount") }; Ascending = $true },
                @{ Expression = { $value = Get-NumericValue (Get-JsonValue $_ "weakRecoveryPct"); if ($null -eq $value) { -1.0 } else { $value } }; Ascending = $false },
                @{ Expression = { $value = Get-NumericValue (Get-JsonValue $_ "nr5OutputMovementDb"); if ($null -eq $value) { 999.0 } else { $value } }; Ascending = $true })[0]
        }
    }
}

function Get-LiveHistoryTraceIdForRole {
    param(
        [string]$Role,
        $LatestTrace,
        $BestBalancedTrace,
        $BestWeakSignalTrace,
        $LowestPumpingTrace
    )

    switch ($Role) {
        "latest" { return [string](Get-JsonValue $LatestTrace "traceId") }
        "best-balanced" { return [string](Get-JsonValue $BestBalancedTrace "traceId") }
        "best-weak-signal" { return [string](Get-JsonValue $BestWeakSignalTrace "traceId") }
        "lowest-pumping" { return [string](Get-JsonValue $LowestPumpingTrace "traceId") }
        default { return "" }
    }
}

function Get-LiveHistoryTraceDelta {
    param(
        $Current,
        $Previous,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if ($null -eq $Current -or $null -eq $Previous) {
        return $null
    }

    $currentValue = Get-NumericValue (Get-JsonValue $Current $Name)
    $previousValue = Get-NumericValue (Get-JsonValue $Previous $Name)
    if ($null -eq $currentValue -or $null -eq $previousValue) {
        return $null
    }

    return [Math]::Round($currentValue - $previousValue, 3)
}

function New-LiveHistoryExpectedLatestDelta {
    param(
        $Latest,
        $Previous
    )

    return [ordered]@{
        weakRecoveryPct = Get-LiveHistoryTraceDelta -Current $Latest -Previous $Previous -Name "weakRecoveryPct"
        weakDropoutSampleCount = Get-LiveHistoryTraceDelta -Current $Latest -Previous $Previous -Name "weakDropoutSampleCount"
        nr5OutputMovementDb = Get-LiveHistoryTraceDelta -Current $Latest -Previous $Previous -Name "nr5OutputMovementDb"
        nr5MakeupMovementDb = Get-LiveHistoryTraceDelta -Current $Latest -Previous $Previous -Name "nr5MakeupMovementDb"
        nr5RecoveryDriveMovement = Get-LiveHistoryTraceDelta -Current $Latest -Previous $Previous -Name "nr5RecoveryDriveMovement"
        safetyRiskScore = Get-LiveHistoryTraceDelta -Current $Latest -Previous $Previous -Name "safetyRiskScore"
    }
}

function New-LiveHistoryExpectedThresholds {
    return [ordered]@{
        weakRecoveryPctMinimum = 95.0
        nr5OutputMovementDbMaximum = 6.0
        audioRmsMovementDbMaximum = 6.0
        nr5MakeupMovementDbMaximum = 6.0
        nr5RecoveryDriveMovementMaximum = 0.5
        nr5MakeupMaxDbMaximum = 12.0
        audioPeakMaxDbfsMaximum = -1.0
        adcHeadroomMinDbMinimum = 6.0
    }
}

function New-LiveHistoryExpectedReviewStatusCounts {
    param($Records)

    $counts = @{}
    foreach ($record in @($Records)) {
        $status = [string](Get-JsonValue $record "reviewStatus")
        if ([string]::IsNullOrWhiteSpace($status)) {
            $status = "unknown"
        }

        if (-not $counts.ContainsKey($status)) {
            $counts[$status] = 0
        }
        $counts[$status] = [int]$counts[$status] + 1
    }

    $result = New-Object System.Collections.Generic.List[object]
    foreach ($status in @($counts.Keys | Sort-Object)) {
        $result.Add([ordered]@{
            status = [string]$status
            count = [int]$counts[$status]
        }) | Out-Null
    }

    return @($result.ToArray())
}

function Get-LiveHistoryReviewStatusCount {
    param(
        $Counts,
        [Parameter(Mandatory = $true)][string]$Status
    )

    foreach ($item in @($Counts)) {
        if ([string]::Equals([string](Get-JsonValue $item "status"), $Status, [StringComparison]::OrdinalIgnoreCase)) {
            return [int](Get-NumericValueOrDefault (Get-JsonValue $item "count"))
        }
    }

    return 0
}

function Test-LiveHistoryReviewStatusCountsMatch {
    param(
        $Actual,
        $Expected
    )

    $actualItems = @($Actual | Where-Object {
        $null -ne $_ -and -not [string]::IsNullOrWhiteSpace([string](Get-JsonValue $_ "status"))
    })
    $expectedItems = @($Expected | Where-Object {
        $null -ne $_ -and -not [string]::IsNullOrWhiteSpace([string](Get-JsonValue $_ "status"))
    })

    if ($actualItems.Count -ne $expectedItems.Count) {
        return $false
    }

    foreach ($expectedItem in @($expectedItems)) {
        $status = [string](Get-JsonValue $expectedItem "status")
        if ((Get-LiveHistoryReviewStatusCount -Counts $actualItems -Status $status) -ne (Get-LiveHistoryReviewStatusCount -Counts $expectedItems -Status $status)) {
            return $false
        }
    }

    return $true
}

function New-LiveHistoryExpectedRecommendations {
    param(
        $Latest,
        $BestBalanced,
        $BestWeak,
        $LowestPumping
    )

    $items = New-Object System.Collections.Generic.List[string]
    if ($null -eq $Latest) {
        $items.Add("No NR5 live diagnostics summaries were found; capture G2 NR5 and baseline windows before tuning.") | Out-Null
        return @($items.ToArray())
    }

    $latestPumping = Get-SafetyClassCount -Counts (Get-JsonArray $Latest "safetyClassCounts") -SafetyClass "pumping"
    $latestWeak = Get-SafetyClassCount -Counts (Get-JsonArray $Latest "safetyClassCounts") -SafetyClass "weak-signal"
    if ($latestPumping -gt 0 -and $latestWeak -gt 0) {
        $items.Add("Latest NR5 trace has both pumping and weak-signal safety signals; tune recovery/makeup coupling before raising global makeup or mask fill.") | Out-Null
    }
    elseif ($latestPumping -gt 0) {
        $items.Add("Latest NR5 trace is primarily pumping-limited; inspect output movement, makeup movement, and recovery-drive movement before changing weak-fill thresholds.") | Out-Null
    }
    elseif ($latestWeak -gt 0) {
        $items.Add("Latest NR5 trace is primarily weak-signal-limited; inspect top weak dropouts before increasing broad makeup.") | Out-Null
    }

    $latestTraceId = [string](Get-JsonValue $Latest "traceId")
    $bestBalancedTraceId = [string](Get-JsonValue $BestBalanced "traceId")
    $bestWeakTraceId = [string](Get-JsonValue $BestWeak "traceId")
    $lowestPumpingTraceId = [string](Get-JsonValue $LowestPumping "traceId")
    if ($null -ne $BestBalanced -and -not [string]::Equals($bestBalancedTraceId, $latestTraceId, [StringComparison]::OrdinalIgnoreCase)) {
        $items.Add("Best balanced trace is '$bestBalancedTraceId'; compare it against the latest trace before accepting the newest tuning direction.") | Out-Null
    }
    if ($null -ne $BestWeak -and -not [string]::Equals($bestWeakTraceId, $latestTraceId, [StringComparison]::OrdinalIgnoreCase)) {
        $items.Add("Best weak-signal trace is '$bestWeakTraceId'; preserve its recovery behavior when reducing pumping.") | Out-Null
    }
    if ($null -ne $LowestPumping -and -not [string]::Equals($lowestPumpingTraceId, $latestTraceId, [StringComparison]::OrdinalIgnoreCase)) {
        $items.Add("Lowest-pumping trace is '$lowestPumpingTraceId'; use it as a level-stability reference.") | Out-Null
    }

    if ($items.Count -eq 0) {
        $items.Add("No high-priority live-history safety signals were found; pair this history with fixture metrics before considering any behavior change.") | Out-Null
    }

    return @($items.ToArray())
}

function New-LiveHistoryExpectedPromotionDecision {
    param(
        $Latest,
        $BestBalanced,
        $BestWeak,
        $LowestPumping
    )

    $blockers = New-Object System.Collections.Generic.List[object]
    if ($null -eq $Latest) {
        $blockers.Add([ordered]@{
            code = "no-nr5-history"
            safetyClass = "tooling"
            traceId = ""
            message = "No NR5 live diagnostics summaries were found."
            value = 0
            threshold = 1
            thresholdDirection = "minimum"
            thresholdComparator = "at-or-above"
            comparison = "less-than-minimum"
            unit = "value"
            readinessGap = 1.0
            readinessMargin = -1.0
            readinessGapUnit = "value"
            readinessGapScore = 1.0
        }) | Out-Null

        $blockerGapSummary = @(New-LiveHistoryReadinessSummaryFromSignals -Signals @($blockers.ToArray()))
        return [ordered]@{
            promotionScope = "candidate-comparison-only"
            status = "no-nr5-history"
            candidatePromotionReady = $false
            defaultBehaviorChangeReady = $false
            latestTraceId = ""
            recommendedTraceId = ""
            recommendedTraceRole = ""
            referenceTraceId = ""
            referenceTraceRole = ""
            latestIsBestBalanced = $false
            blockerCount = $blockers.Count
            blockerClasses = @("tooling")
            blockerClassCounts = @(New-LiveHistorySafetyClassCountsFromSignals -Signals @($blockers.ToArray()))
            safetyClassReadiness = @($blockerGapSummary)
            blockerGapSummary = @($blockerGapSummary)
            blockers = @($blockers.ToArray())
            nextAction = "Capture G2 NR5 live diagnostics before selecting a candidate comparison window."
        }
    }

    $latestTraceId = [string](Get-JsonValue $Latest "traceId")
    foreach ($signal in @(Get-JsonArray $Latest "safetySignals")) {
        $safetyClass = [string](Get-JsonValue $signal "safetyClass")
        $name = [string](Get-JsonValue $signal "name")
        if ([string]::IsNullOrWhiteSpace($safetyClass)) {
            $safetyClass = "unknown"
        }
        if ([string]::IsNullOrWhiteSpace($name)) {
            $name = "safety-signal"
        }

        $blockers.Add([ordered]@{
            code = $name
            safetyClass = $safetyClass
            traceId = $latestTraceId
            message = [string](Get-JsonValue $signal "message")
            value = Get-JsonValue $signal "value"
            threshold = Get-JsonValue $signal "threshold"
            thresholdDirection = Get-JsonValue $signal "thresholdDirection"
            thresholdComparator = Get-JsonValue $signal "thresholdComparator"
            comparison = Get-JsonValue $signal "comparison"
            unit = Get-JsonValue $signal "unit"
            readinessGap = Get-JsonValue $signal "readinessGap"
            readinessMargin = Get-JsonValue $signal "readinessMargin"
            readinessGapUnit = Get-JsonValue $signal "readinessGapUnit"
            readinessGapScore = Get-JsonValue $signal "readinessGapScore"
        }) | Out-Null
    }

    $latestSafetyClassCounts = @(Get-JsonArray $Latest "safetyClassCounts")
    $hardGateCount = Get-SafetyClassCount -Counts $latestSafetyClassCounts -SafetyClass "hard-gate"
    $weakSignalCount = Get-SafetyClassCount -Counts $latestSafetyClassCounts -SafetyClass "weak-signal"
    $pumpingCount = Get-SafetyClassCount -Counts $latestSafetyClassCounts -SafetyClass "pumping"
    $clippingCount = Get-SafetyClassCount -Counts $latestSafetyClassCounts -SafetyClass "clipping"
    $freshnessCount = Get-SafetyClassCount -Counts $latestSafetyClassCounts -SafetyClass "freshness"
    $frontEndCount = Get-SafetyClassCount -Counts $latestSafetyClassCounts -SafetyClass "front-end"
    $audioPathCount = Get-SafetyClassCount -Counts $latestSafetyClassCounts -SafetyClass "audio-path"

    $status = "ready-for-candidate-comparison"
    $nextAction = "Use the latest trace as the next candidate comparison window, then compare against current-Zeus and Thetis-parity evidence before any behavior change."
    if (-not (Test-Truthy (Get-JsonValue $Latest "readyForBenchmarkTrace")) -or $hardGateCount -gt 0) {
        $status = "blocked-hard-gate"
        $nextAction = "Clear hard gates and recapture before using this trace for candidate comparison."
    }
    elseif ($clippingCount -gt 0) {
        $status = "blocked-clipping"
        $nextAction = "Reduce level or makeup risk and recapture before candidate comparison."
    }
    elseif ($freshnessCount -gt 0) {
        $status = "blocked-diagnostic-freshness"
        $nextAction = "Fix diagnostic freshness before trusting the live history for tuning decisions."
    }
    elseif ($frontEndCount -gt 0 -or $audioPathCount -gt 0) {
        $status = "blocked-runtime-evidence"
        $nextAction = "Resolve front-end or audio-path evidence issues before selecting a candidate trace."
    }
    elseif ($weakSignalCount -gt 0 -and $pumpingCount -gt 0) {
        $status = "blocked-weak-and-pumping"
        $nextAction = "Use the best-balanced trace as the reference and tune weak recovery together with output-level movement."
    }
    elseif ($pumpingCount -gt 0) {
        $status = "blocked-pumping"
        $nextAction = "Use the lowest-pumping trace as the level-stability reference before increasing weak-signal rescue."
    }
    elseif ($weakSignalCount -gt 0) {
        $status = "blocked-weak-signal"
        $nextAction = "Use the best weak-signal trace as the recovery reference before changing makeup or mask-fill behavior."
    }
    elseif ($blockers.Count -gt 0) {
        $status = "blocked-safety-signals"
        $nextAction = "Resolve the remaining safety signals before candidate comparison."
    }

    $candidatePromotionReady = ($status -eq "ready-for-candidate-comparison")
    $recommendedTrace = if ($candidatePromotionReady) { $Latest } elseif ($null -ne $BestBalanced) { $BestBalanced } elseif ($null -ne $BestWeak) { $BestWeak } elseif ($null -ne $LowestPumping) { $LowestPumping } else { $Latest }
    $recommendedTraceId = if ($null -eq $recommendedTrace) { "" } else { [string](Get-JsonValue $recommendedTrace "traceId") }
    $recommendedRole = if ($null -eq $recommendedTrace) {
        ""
    }
    elseif ([string]::Equals($recommendedTraceId, $latestTraceId, [StringComparison]::OrdinalIgnoreCase)) {
        "latest"
    }
    elseif ($null -ne $BestBalanced -and [string]::Equals($recommendedTraceId, [string](Get-JsonValue $BestBalanced "traceId"), [StringComparison]::OrdinalIgnoreCase)) {
        "best-balanced"
    }
    elseif ($null -ne $BestWeak -and [string]::Equals($recommendedTraceId, [string](Get-JsonValue $BestWeak "traceId"), [StringComparison]::OrdinalIgnoreCase)) {
        "best-weak-signal"
    }
    elseif ($null -ne $LowestPumping -and [string]::Equals($recommendedTraceId, [string](Get-JsonValue $LowestPumping "traceId"), [StringComparison]::OrdinalIgnoreCase)) {
        "lowest-pumping"
    }
    else {
        "reference"
    }

    $referenceTrace = if ($null -ne $BestBalanced) { $BestBalanced } elseif ($null -ne $BestWeak) { $BestWeak } elseif ($null -ne $LowestPumping) { $LowestPumping } else { $Latest }
    $bestBalancedTraceId = if ($null -eq $BestBalanced) { "" } else { [string](Get-JsonValue $BestBalanced "traceId") }
    $latestIsBestBalanced = ($null -ne $BestBalanced -and [string]::Equals($bestBalancedTraceId, $latestTraceId, [StringComparison]::OrdinalIgnoreCase))
    $blockerClassCounts = @(New-LiveHistorySafetyClassCountsFromSignals -Signals @($blockers.ToArray()))
    $blockerClasses = @($blockerClassCounts | ForEach-Object { [string](Get-JsonValue $_ "safetyClass") })
    $blockerGapSummary = @(New-LiveHistoryReadinessSummaryFromSignals -Signals @($blockers.ToArray()))

    return [ordered]@{
        promotionScope = "candidate-comparison-only"
        status = $status
        promotable = $candidatePromotionReady
        candidatePromotionReady = $candidatePromotionReady
        candidateComparisonReady = $candidatePromotionReady
        defaultBehaviorChangeReady = $false
        latestTraceId = $latestTraceId
        recommendedTraceId = $recommendedTraceId
        recommendedTraceRole = $recommendedRole
        referenceTraceId = if ($null -eq $referenceTrace) { "" } else { [string](Get-JsonValue $referenceTrace "traceId") }
        referenceTraceRole = if ($null -ne $BestBalanced) { "best-balanced" } elseif ($null -ne $BestWeak) { "best-weak-signal" } elseif ($null -ne $LowestPumping) { "lowest-pumping" } else { "latest" }
        latestIsBestBalanced = $latestIsBestBalanced
        latestSafetyRiskScore = [int](Get-NumericValueOrDefault (Get-JsonValue $Latest "safetyRiskScore"))
        bestBalancedSafetyRiskScore = if ($null -eq $BestBalanced) { $null } else { [int](Get-NumericValueOrDefault (Get-JsonValue $BestBalanced "safetyRiskScore")) }
        riskScoreDeltaVsBestBalanced = Get-LiveHistoryTraceDelta -Current $Latest -Previous $BestBalanced -Name "safetyRiskScore"
        blockerCount = $blockers.Count
        blockerClasses = @($blockerClasses)
        blockerClassCounts = @($blockerClassCounts)
        safetyClassReadiness = @($blockerGapSummary)
        blockerGapSummary = @($blockerGapSummary)
        blockers = @($blockers.ToArray())
        nextAction = $nextAction
    }
}

function Test-LiveHistoryScalarEquivalent {
    param(
        $Actual,
        $Expected
    )

    if ($null -eq $Actual -and $null -eq $Expected) {
        return $true
    }
    if ($null -eq $Actual -or $null -eq $Expected) {
        return $false
    }

    $actualNumber = Get-NumericValue $Actual
    $expectedNumber = Get-NumericValue $Expected
    if ($null -ne $actualNumber -and $null -ne $expectedNumber) {
        return (Test-NumericClose -Actual $actualNumber -Expected $expectedNumber)
    }

    if ($Actual -is [bool] -or $Expected -is [bool]) {
        return ((Test-Truthy $Actual) -eq (Test-Truthy $Expected))
    }

    return [string]::Equals([string]$Actual, [string]$Expected, [StringComparison]::Ordinal)
}

function Test-LiveHistoryPromotionBlockersMatch {
    param(
        $Actual,
        $Expected
    )

    $actualBlockers = @($Actual)
    $expectedBlockers = @($Expected)
    if ($actualBlockers.Count -ne $expectedBlockers.Count) {
        return $false
    }

    for ($i = 0; $i -lt $expectedBlockers.Count; $i++) {
        $actualBlocker = $actualBlockers[$i]
        $expectedBlocker = $expectedBlockers[$i]
        foreach ($fieldName in @("code", "safetyClass", "traceId", "message", "thresholdDirection", "thresholdComparator", "comparison", "unit", "readinessGapUnit")) {
            if (-not [string]::Equals([string](Get-JsonValue $actualBlocker $fieldName), [string](Get-JsonValue $expectedBlocker $fieldName), [StringComparison]::Ordinal)) {
                return $false
            }
        }
        foreach ($fieldName in @("value", "threshold", "readinessGap", "readinessMargin", "readinessGapScore")) {
            if (-not (Test-LiveHistoryScalarEquivalent -Actual (Get-JsonValue $actualBlocker $fieldName) -Expected (Get-JsonValue $expectedBlocker $fieldName))) {
                return $false
            }
        }
    }

    return $true
}

function Get-LiveHistoryNumericOrNull {
    param(
        $Object,
        [Parameter(Mandatory = $true)][string]$Name
    )

    return Get-NumericValue (Get-JsonValue $Object $Name)
}

function Get-LiveHistoryStatValue {
    param(
        $Report,
        [Parameter(Mandatory = $true)][string]$Group,
        [Parameter(Mandatory = $true)][string]$Name
    )

    return Get-LiveHistoryNumericOrNull (Get-JsonValue $Report $Group) $Name
}

function Get-LiveHistoryPercent {
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

function Resolve-LiveHistoryOptionalFilePath {
    param(
        [string]$Path,
        [string]$BaseDirectory = ""
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return ""
    }

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    if (-not [string]::IsNullOrWhiteSpace($BaseDirectory)) {
        $candidate = Join-Path $BaseDirectory $Path
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }

    return $Path
}

function Get-LiveHistoryFileSha256IfPresent {
    param(
        [string]$Path,
        [string]$BaseDirectory = ""
    )

    $resolvedPath = Resolve-LiveHistoryOptionalFilePath -Path $Path -BaseDirectory $BaseDirectory
    if ([string]::IsNullOrWhiteSpace($resolvedPath) -or -not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
        return ""
    }

    return (Get-FileHash -LiteralPath $resolvedPath -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-LiveHistoryDateSortKey {
    param($Value)

    $date = Get-DateTimeOffsetValue $Value
    if ($null -ne $date) {
        return $date
    }

    return [DateTimeOffset]::MinValue
}

function Get-LiveHistoryTraceIdSortKey {
    param([string]$TraceId)

    if ([string]::IsNullOrWhiteSpace($TraceId)) {
        return $null
    }

    if ($TraceId -notmatch "^(?<stamp>\d{8}T\d{9}Z)") {
        return $null
    }

    $stamp = $Matches["stamp"]
    $parsed = [DateTimeOffset]::MinValue
    $styles = [System.Globalization.DateTimeStyles]::AssumeUniversal -bor [System.Globalization.DateTimeStyles]::AdjustToUniversal
    if ([DateTimeOffset]::TryParseExact($stamp, "yyyyMMdd'T'HHmmssfff'Z'", [System.Globalization.CultureInfo]::InvariantCulture, $styles, [ref]$parsed)) {
        return $parsed.ToUniversalTime()
    }

    return $null
}

function ConvertTo-LiveHistoryTraceIdSegment {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return ""
    }

    $segment = $Value.Trim().ToLowerInvariant() -replace "[^a-z0-9]+", "-"
    return $segment.Trim("-")
}

function ConvertTo-LiveHistoryPortablePath {
    param(
        [string]$Root,
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path) -or [string]::IsNullOrWhiteSpace($Root)) {
        return $Path
    }

    try {
        $rootFull = [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Root).Path)
        $pathFull = [System.IO.Path]::GetFullPath($Path)
        $trimChars = [char[]]@([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
        if ([string]::Equals($rootFull.TrimEnd($trimChars), $pathFull.TrimEnd($trimChars), [StringComparison]::OrdinalIgnoreCase)) {
            return "."
        }

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

        return ($relative -replace "\\", "/")
    }
    catch {
        return $Path
    }
}

function Get-LiveHistoryRelativePathSegments {
    param(
        [string]$Root,
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Root) -or [string]::IsNullOrWhiteSpace($Path)) {
        return @()
    }

    $relative = ConvertTo-LiveHistoryPortablePath -Root $Root -Path $Path
    if ([string]::IsNullOrWhiteSpace($relative) -or
        $relative -eq "." -or
        $relative -eq ".." -or
        $relative.StartsWith("../", [StringComparison]::Ordinal) -or
        $relative.StartsWith("..\", [StringComparison]::Ordinal) -or
        [System.IO.Path]::IsPathRooted($relative)) {
        return @()
    }

    return @($relative -split "[\\/]+" | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
}

function Get-LiveHistoryTraceIdentity {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [string]$PortableRoot
    )

    $resolvedPath = (Resolve-Path -LiteralPath $Path).Path
    $parentPath = Split-Path -Parent $resolvedPath
    $rootPath = ""
    if (-not [string]::IsNullOrWhiteSpace($PortableRoot) -and (Test-Path -LiteralPath $PortableRoot -PathType Container)) {
        $rootPath = (Resolve-Path -LiteralPath $PortableRoot).Path
    }

    $timestampPath = ""
    $timestampName = ""
    $timestampSortKey = $null
    $cursor = $parentPath
    while (-not [string]::IsNullOrWhiteSpace($cursor)) {
        $leaf = Split-Path -Leaf $cursor
        $candidateSortKey = Get-LiveHistoryTraceIdSortKey $leaf
        if ($null -ne $candidateSortKey) {
            $timestampPath = $cursor
            $timestampName = $leaf
            $timestampSortKey = $candidateSortKey
            break
        }

        if (-not [string]::IsNullOrWhiteSpace($rootPath) -and
            [string]::Equals($cursor, $rootPath, [StringComparison]::OrdinalIgnoreCase)) {
            break
        }

        $next = Split-Path -Parent $cursor
        if ([string]::IsNullOrWhiteSpace($next) -or [string]::Equals($next, $cursor, [StringComparison]::OrdinalIgnoreCase)) {
            break
        }

        $cursor = $next
    }

    $genericSegments = @("artifacts", "live-diagnostics-traces")
    $traceSegments = New-Object System.Collections.Generic.List[string]
    $scenarioId = ""
    $comparisonId = ""
    if (-not [string]::IsNullOrWhiteSpace($timestampPath)) {
        $relativeSegments = @(Get-LiveHistoryRelativePathSegments -Root $timestampPath -Path $parentPath)
        for ($i = 0; $i -lt $relativeSegments.Count; $i++) {
            $safeSegment = ConvertTo-LiveHistoryTraceIdSegment ([string]$relativeSegments[$i])
            if ([string]::Equals($safeSegment, "live-diagnostics-traces", [StringComparison]::OrdinalIgnoreCase)) {
                if ($i + 1 -lt $relativeSegments.Count) {
                    $scenarioId = ConvertTo-LiveHistoryTraceIdSegment ([string]$relativeSegments[$i + 1])
                }
                if ($i + 2 -lt $relativeSegments.Count) {
                    $comparisonId = ConvertTo-LiveHistoryTraceIdSegment ([string]$relativeSegments[$i + 2])
                }
            }
        }

        foreach ($segment in $relativeSegments) {
            $safeSegment = ConvertTo-LiveHistoryTraceIdSegment $segment
            if ([string]::IsNullOrWhiteSpace($safeSegment) -or $genericSegments -contains $safeSegment) {
                continue
            }

            $traceSegments.Add($safeSegment) | Out-Null
        }

        $traceId = $timestampName
        if ($traceSegments.Count -gt 0) {
            $traceId = "$timestampName-$($traceSegments.ToArray() -join '-')"
        }

        return [ordered]@{
            traceId = $traceId
            traceDirectory = $timestampName
            traceDirectorySortKey = $timestampSortKey
            scenarioId = $scenarioId
            comparisonId = $comparisonId
        }
    }

    $fallbackRoot = if (-not [string]::IsNullOrWhiteSpace($rootPath)) { $rootPath } else { Split-Path -Parent $parentPath }
    $fallbackSegments = @(Get-LiveHistoryRelativePathSegments -Root $fallbackRoot -Path $parentPath)
    for ($i = 0; $i -lt $fallbackSegments.Count; $i++) {
        $safeSegment = ConvertTo-LiveHistoryTraceIdSegment ([string]$fallbackSegments[$i])
        if ([string]::Equals($safeSegment, "live-diagnostics-traces", [StringComparison]::OrdinalIgnoreCase)) {
            if ($i + 1 -lt $fallbackSegments.Count) {
                $scenarioId = ConvertTo-LiveHistoryTraceIdSegment ([string]$fallbackSegments[$i + 1])
            }
            if ($i + 2 -lt $fallbackSegments.Count) {
                $comparisonId = ConvertTo-LiveHistoryTraceIdSegment ([string]$fallbackSegments[$i + 2])
            }
        }
    }

    foreach ($segment in $fallbackSegments) {
        $safeSegment = ConvertTo-LiveHistoryTraceIdSegment $segment
        if ([string]::IsNullOrWhiteSpace($safeSegment) -or $genericSegments -contains $safeSegment) {
            continue
        }

        $traceSegments.Add($safeSegment) | Out-Null
    }

    if ($traceSegments.Count -eq 0) {
        $fileSegment = ConvertTo-LiveHistoryTraceIdSegment ([System.IO.Path]::GetFileNameWithoutExtension($resolvedPath))
        if (-not [string]::IsNullOrWhiteSpace($fileSegment)) {
            $traceSegments.Add($fileSegment) | Out-Null
        }
    }

    return [ordered]@{
        traceId = if ($traceSegments.Count -gt 0) { $traceSegments.ToArray() -join "-" } else { [System.IO.Path]::GetFileNameWithoutExtension($resolvedPath) }
        traceDirectory = ""
        traceDirectorySortKey = $null
        scenarioId = $scenarioId
        comparisonId = $comparisonId
    }
}

function New-LiveHistorySafetySignal {
    param(
        [Parameter(Mandatory = $true)][string]$SafetyClass,
        [Parameter(Mandatory = $true)][string]$Name,
        $Value,
        $Threshold,
        [Parameter(Mandatory = $true)][string]$Message
    )

    $valueNumber = Get-NumericValue $Value
    $thresholdNumber = Get-NumericValue $Threshold
    $comparison = "mismatch"
    $thresholdDirection = "equals"
    $thresholdComparator = "equals"
    $readinessGap = $null
    $readinessMargin = $null

    if ($null -ne $valueNumber -and $null -ne $thresholdNumber) {
        if ($valueNumber -gt $thresholdNumber) {
            $comparison = "greater-than-maximum"
            $thresholdDirection = "maximum"
            $thresholdComparator = "at-or-below"
            $readinessGap = [Math]::Round($valueNumber - $thresholdNumber, 3)
            $readinessMargin = [Math]::Round($thresholdNumber - $valueNumber, 3)
        }
        elseif ($valueNumber -lt $thresholdNumber) {
            $comparison = "less-than-minimum"
            $thresholdDirection = "minimum"
            $thresholdComparator = "at-or-above"
            $readinessGap = [Math]::Round($thresholdNumber - $valueNumber, 3)
            $readinessMargin = [Math]::Round($valueNumber - $thresholdNumber, 3)
        }
        else {
            $comparison = "equals-threshold"
            $readinessGap = 0.0
            $readinessMargin = 0.0
        }
    }
    elseif (($Value -is [bool] -or $Threshold -is [bool]) -and ([bool]$Value -ne [bool]$Threshold)) {
        $comparison = "boolean-mismatch"
        $readinessGap = 1.0
        $readinessMargin = -1.0
    }

    $unit = Get-LiveHistorySignalUnit -Name $Name -Value $Value -Threshold $Threshold
    return [ordered]@{
        safetyClass = $SafetyClass
        name = $Name
        value = $Value
        threshold = $Threshold
        thresholdDirection = $thresholdDirection
        thresholdComparator = $thresholdComparator
        comparison = $comparison
        unit = $unit
        readinessGap = $readinessGap
        readinessMargin = $readinessMargin
        readinessGapUnit = $unit
        readinessGapScore = $readinessGap
        message = $Message
    }
}

function Add-LiveHistorySafetySignalIf {
    param(
        [System.Collections.Generic.List[object]]$Signals,
        [bool]$Condition,
        [Parameter(Mandatory = $true)][string]$SafetyClass,
        [Parameter(Mandatory = $true)][string]$Name,
        $Value,
        $Threshold,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if ($Condition) {
        $Signals.Add((New-LiveHistorySafetySignal -SafetyClass $SafetyClass -Name $Name -Value $Value -Threshold $Threshold -Message $Message)) | Out-Null
    }
}

function Get-LiveHistorySafetyRiskScore {
    param($Counts)

    return ((Get-SafetyClassCount -Counts $Counts -SafetyClass "hard-gate") * 1000) +
        ((Get-SafetyClassCount -Counts $Counts -SafetyClass "weak-signal") * 120) +
        ((Get-SafetyClassCount -Counts $Counts -SafetyClass "pumping") * 80) +
        ((Get-SafetyClassCount -Counts $Counts -SafetyClass "clipping") * 120) +
        ((Get-SafetyClassCount -Counts $Counts -SafetyClass "freshness") * 80) +
        ((Get-SafetyClassCount -Counts $Counts -SafetyClass "front-end") * 40) +
        ((Get-SafetyClassCount -Counts $Counts -SafetyClass "audio-path") * 80) +
        ((Get-SafetyClassCount -Counts $Counts -SafetyClass "tooling") * 20)
}

function Get-LiveHistoryCandidateBlockingSafetyClassCounts {
    param($Counts)

    $result = New-Object System.Collections.Generic.List[object]
    foreach ($safetyClass in @("hard-gate", "weak-signal", "pumping", "clipping", "freshness", "front-end", "audio-path", "tooling")) {
        $count = Get-SafetyClassCount -Counts $Counts -SafetyClass $safetyClass
        if ($count -gt 0) {
            $result.Add([ordered]@{
                safetyClass = $safetyClass
                count = $count
            }) | Out-Null
        }
    }

    return @($result.ToArray())
}

function Get-LiveHistoryTraceStatus {
    param(
        [bool]$ReadyForBenchmark,
        [int]$HardGateCount,
        [int]$WeakSignalCount,
        [int]$PumpingCount
    )

    if ($HardGateCount -gt 0 -or -not $ReadyForBenchmark) {
        return "blocked"
    }
    if ($WeakSignalCount -gt 0 -and $PumpingCount -gt 0) {
        return "weak-and-pumping-watch"
    }
    if ($WeakSignalCount -gt 0) {
        return "weak-signal-watch"
    }
    if ($PumpingCount -gt 0) {
        return "pumping-watch"
    }

    return "candidate"
}

function New-LiveHistoryExpectedTraceRecordFromSummary {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Report,
        [string]$PortableRoot
    )

    $tool = [string](Get-JsonValue $Report "tool")
    if ($tool -ne "watch-dsp-live-diagnostics") {
        throw "Input '$Path' must be a watch-dsp-live-diagnostics summary; found tool '$tool'."
    }

    $weak = Get-JsonValue $Report "nr5WeakSignalWatch"
    $weakInput = Get-LiveHistoryNumericOrNull $weak "weakInputSampleCount"
    $weakRecovered = Get-LiveHistoryNumericOrNull $weak "weakRecoveredSampleCount"
    $weakDropout = Get-LiveHistoryNumericOrNull $weak "weakDropoutSampleCount"
    $hotMakeup = Get-LiveHistoryNumericOrNull $weak "hotMakeupSampleCount"
    $weakRecoveryPct = Get-LiveHistoryNumericOrNull $weak "weakRecoveryPct"
    if ($null -eq $weakRecoveryPct) {
        $weakRecoveryPct = Get-LiveHistoryPercent $weakRecovered $weakInput
    }

    $okSampleCount = Get-LiveHistoryNumericOrNull $Report "okSampleCount"
    $sampleCount = Get-LiveHistoryNumericOrNull $Report "sampleCount"
    $failedSampleCount = Get-LiveHistoryNumericOrNull $Report "failedSampleCount"
    $hardBlockerSampleCount = Get-LiveHistoryNumericOrNull $Report "hardBlockerSampleCount"
    $runtimeEvidenceSampleCount = Get-LiveHistoryNumericOrNull $Report "runtimeEvidenceSampleCount"
    $audioFreshPct = Get-LiveHistoryPercent (Get-JsonValue $Report "audioFreshSampleCount") $runtimeEvidenceSampleCount
    $rxMetersFreshPct = Get-LiveHistoryPercent (Get-JsonValue $Report "rxMetersFreshSampleCount") $runtimeEvidenceSampleCount
    $nr5SampleCount = Get-LiveHistoryNumericOrNull $Report "nr5SampleCount"
    $readyForBenchmark = Test-Truthy (Get-JsonValue $Report "readyForBenchmarkTrace")
    $signals = New-Object System.Collections.Generic.List[object]

    Add-LiveHistorySafetySignalIf -Signals $signals -Condition ($null -ne $failedSampleCount -and $failedSampleCount -gt 0) -SafetyClass "hard-gate" -Name "failed-samples" -Value $failedSampleCount -Threshold 0 -Message "Endpoint failures make the trace incomplete."
    Add-LiveHistorySafetySignalIf -Signals $signals -Condition ($null -ne $hardBlockerSampleCount -and $hardBlockerSampleCount -gt 0) -SafetyClass "hard-gate" -Name "hard-blockers" -Value $hardBlockerSampleCount -Threshold 0 -Message "Hard blockers must be cleared before using this trace as evidence."
    Add-LiveHistorySafetySignalIf -Signals $signals -Condition (-not $readyForBenchmark) -SafetyClass "hard-gate" -Name "not-ready" -Value $readyForBenchmark -Threshold $true -Message "Trace is not benchmark-ready."

    if ($null -ne $weakInput -and $weakInput -gt 0) {
        Add-LiveHistorySafetySignalIf -Signals $signals -Condition ($null -ne $weakDropout -and $weakDropout -gt 0) -SafetyClass "weak-signal" -Name "weak-dropouts" -Value $weakDropout -Threshold 0 -Message "Weak-input samples dropped below input level."
        Add-LiveHistorySafetySignalIf -Signals $signals -Condition ($null -ne $weakRecoveryPct -and $weakRecoveryPct -lt 95.0) -SafetyClass "weak-signal" -Name "weak-recovery-pct" -Value $weakRecoveryPct -Threshold 95.0 -Message "Weak-input recovery is below the live tuning target."
    }

    $nr5OutputMovement = Get-LiveHistoryStatValue $Report "nr5OutputDbfs" "movement"
    $audioRmsMovement = Get-LiveHistoryStatValue $Report "audioRmsDbfs" "movement"
    $makeupMovement = Get-LiveHistoryStatValue $Report "nr5MakeupGainDb" "movement"
    $makeupMax = Get-LiveHistoryStatValue $Report "nr5MakeupGainDb" "max"
    $recoveryMovement = Get-LiveHistoryStatValue $Report "nr5RecoveryDrive" "movement"
    $audioPeakMax = Get-LiveHistoryStatValue $Report "audioPeakDbfs" "max"
    $adcHeadroomMin = Get-LiveHistoryStatValue $Report "adcHeadroomDb" "min"
    $monitorBacklogMax = Get-LiveHistoryStatValue $Report "monitorBacklogSamples" "max"
    $latencyAverage = Get-LiveHistoryStatValue $Report "latencyMs" "average"

    Add-LiveHistorySafetySignalIf -Signals $signals -Condition ($null -ne $nr5OutputMovement -and $nr5OutputMovement -gt 6.0) -SafetyClass "pumping" -Name "nr5-output-movement-db" -Value $nr5OutputMovement -Threshold 6.0 -Message "NR5 output movement is high enough to need pumping review."
    Add-LiveHistorySafetySignalIf -Signals $signals -Condition ($null -ne $audioRmsMovement -and $audioRmsMovement -gt 6.0) -SafetyClass "pumping" -Name "audio-rms-movement-db" -Value $audioRmsMovement -Threshold 6.0 -Message "Final-audio RMS movement is high enough to need listening review."
    Add-LiveHistorySafetySignalIf -Signals $signals -Condition ($null -ne $makeupMovement -and $makeupMovement -gt 6.0) -SafetyClass "pumping" -Name "nr5-makeup-movement-db" -Value $makeupMovement -Threshold 6.0 -Message "NR5 makeup gain movement is high."
    Add-LiveHistorySafetySignalIf -Signals $signals -Condition ($null -ne $recoveryMovement -and $recoveryMovement -gt 0.5) -SafetyClass "pumping" -Name "nr5-recovery-drive-movement" -Value $recoveryMovement -Threshold 0.5 -Message "Recovery-drive movement is high."
    Add-LiveHistorySafetySignalIf -Signals $signals -Condition ($null -ne $makeupMax -and $makeupMax -gt 12.0) -SafetyClass "pumping" -Name "nr5-makeup-max-db" -Value $makeupMax -Threshold 12.0 -Message "NR5 makeup peak exceeded the hot-makeup threshold."
    Add-LiveHistorySafetySignalIf -Signals $signals -Condition ($null -ne $hotMakeup -and $hotMakeup -gt 0) -SafetyClass "pumping" -Name "hot-makeup-samples" -Value $hotMakeup -Threshold 0 -Message "Hot-makeup samples appeared in the trace."
    Add-LiveHistorySafetySignalIf -Signals $signals -Condition ($null -ne $audioPeakMax -and $audioPeakMax -gt -1.0) -SafetyClass "clipping" -Name "audio-peak-max-dbfs" -Value $audioPeakMax -Threshold -1.0 -Message "Audio peak is close to clipping."
    Add-LiveHistorySafetySignalIf -Signals $signals -Condition ($null -ne $adcHeadroomMin -and $adcHeadroomMin -lt 6.0) -SafetyClass "front-end" -Name "adc-headroom-min-db" -Value $adcHeadroomMin -Threshold 6.0 -Message "ADC headroom is low."
    Add-LiveHistorySafetySignalIf -Signals $signals -Condition ($null -ne $monitorBacklogMax -and $monitorBacklogMax -gt 0) -SafetyClass "audio-path" -Name "monitor-backlog-max-samples" -Value $monitorBacklogMax -Threshold 0 -Message "Monitor backlog invalidates live audio evidence."
    Add-LiveHistorySafetySignalIf -Signals $signals -Condition ($null -ne $audioFreshPct -and $audioFreshPct -lt 100.0) -SafetyClass "freshness" -Name "audio-fresh-pct" -Value $audioFreshPct -Threshold 100.0 -Message "Final-audio diagnostics were not fresh for every runtime sample."
    Add-LiveHistorySafetySignalIf -Signals $signals -Condition ($null -ne $rxMetersFreshPct -and $rxMetersFreshPct -lt 100.0) -SafetyClass "freshness" -Name "rx-meters-fresh-pct" -Value $rxMetersFreshPct -Threshold 100.0 -Message "RX meter diagnostics were not fresh for every runtime sample."

    $counts = @(New-LiveHistorySafetyClassCountsFromSignals -Signals @($signals.ToArray()))
    $readinessGapSummary = @(New-LiveHistoryReadinessSummaryFromSignals -Signals @($signals.ToArray()))
    $hardGateCount = Get-SafetyClassCount -Counts $counts -SafetyClass "hard-gate"
    $weakSignalCount = Get-SafetyClassCount -Counts $counts -SafetyClass "weak-signal"
    $pumpingCount = Get-SafetyClassCount -Counts $counts -SafetyClass "pumping"
    $candidateBlockingCounts = @(Get-LiveHistoryCandidateBlockingSafetyClassCounts -Counts $counts)
    $candidateBlockerCount = Get-TotalSafetyClassCount -Counts $candidateBlockingCounts
    $candidateComparisonReady = ($readyForBenchmark -and
        $candidateBlockerCount -eq 0 -and
        $null -ne $nr5SampleCount -and
        $nr5SampleCount -gt 0)
    $reviewStatus = Get-LiveHistoryTraceStatus `
        -ReadyForBenchmark $readyForBenchmark `
        -HardGateCount $hardGateCount `
        -WeakSignalCount $weakSignalCount `
        -PumpingCount $pumpingCount

    $traceIdentity = Get-LiveHistoryTraceIdentity -Path $Path -PortableRoot $PortableRoot
    $traceScenarioId = ConvertTo-LiveHistoryTraceIdSegment ([string](Get-JsonValue $Report "scenarioId"))
    $traceComparisonId = ConvertTo-LiveHistoryTraceIdSegment ([string](Get-JsonValue $Report "comparisonId"))
    if ([string]::IsNullOrWhiteSpace($traceScenarioId)) {
        $traceScenarioId = [string](Get-JsonValue $traceIdentity "scenarioId")
    }
    if ([string]::IsNullOrWhiteSpace($traceComparisonId)) {
        $traceComparisonId = [string](Get-JsonValue $traceIdentity "comparisonId")
    }
    $traceId = [string](Get-JsonValue $traceIdentity "traceId")
    $completedSortKey = Get-LiveHistoryDateSortKey (Get-JsonValue $Report "completedUtc")
    $traceDirectorySortKey = Get-JsonValue $traceIdentity "traceDirectorySortKey"
    $jsonlSourcePath = [string](Get-JsonValue $Report "jsonlPath")
    $sourceBaseDirectory = Split-Path -Parent $Path
    $sortKey = $completedSortKey
    $sortKeySource = "completedUtc"
    if ($null -ne $traceDirectorySortKey) {
        $sortKey = $traceDirectorySortKey
        $sortKeySource = "trace-directory-timestamp"
    }
    elseif ($sortKey -eq [DateTimeOffset]::MinValue) {
        $item = Get-Item -LiteralPath $Path
        $sortKey = [DateTimeOffset]::new($item.LastWriteTimeUtc)
        $sortKeySource = "file-last-write-utc"
    }

    return [ordered]@{
        traceId = $traceId
        traceSequenceUtc = $sortKey.UtcDateTime.ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
        sortKeySource = $sortKeySource
        scenarioId = $traceScenarioId
        comparisonId = $traceComparisonId
        label = [string](Get-JsonValue $Report "label")
        path = ConvertTo-LiveHistoryPortablePath -Root $PortableRoot -Path $Path
        summarySha256 = Get-LiveHistoryFileSha256IfPresent -Path $Path
        jsonlPath = ConvertTo-LiveHistoryPortablePath -Root $PortableRoot -Path $jsonlSourcePath
        jsonlSha256 = Get-LiveHistoryFileSha256IfPresent -Path $jsonlSourcePath -BaseDirectory $sourceBaseDirectory
        endpoint = [string](Get-JsonValue $Report "endpoint")
        sourceMode = [string](Get-JsonValue $Report "sourceMode")
        startedUtc = Get-JsonValue $Report "startedUtc"
        completedUtc = Get-JsonValue $Report "completedUtc"
        generatedUtc = Get-JsonValue $Report "generatedUtc"
        sampleCount = if ($null -eq $sampleCount) { 0 } else { [int][Math]::Round($sampleCount) }
        okSampleCount = if ($null -eq $okSampleCount) { 0 } else { [int][Math]::Round($okSampleCount) }
        failedSampleCount = if ($null -eq $failedSampleCount) { 0 } else { [int][Math]::Round($failedSampleCount) }
        hardBlockerSampleCount = if ($null -eq $hardBlockerSampleCount) { 0 } else { [int][Math]::Round($hardBlockerSampleCount) }
        readyForBenchmarkTrace = $readyForBenchmark
        trendStatus = [string](Get-JsonValue $Report "trendStatus")
        nr5TuningReadyTrace = Test-Truthy (Get-JsonValue $Report "nr5TuningReadyTrace")
        nr5TuningTraceStatus = [string](Get-JsonValue $Report "nr5TuningTraceStatus")
        nr5SampleCount = if ($null -eq $nr5SampleCount) { 0 } else { [int][Math]::Round($nr5SampleCount) }
        nr5ProbabilityDiagnosticSampleCount = [int](Get-NumericValue (Get-JsonValue $Report "nr5ProbabilityDiagnosticSampleCount"))
        nr5AgcDiagnosticSampleCount = [int](Get-NumericValue (Get-JsonValue $Report "nr5AgcDiagnosticSampleCount"))
        weakInputSampleCount = if ($null -eq $weakInput) { 0 } else { [int][Math]::Round($weakInput) }
        weakRecoveredSampleCount = if ($null -eq $weakRecovered) { 0 } else { [int][Math]::Round($weakRecovered) }
        weakDropoutSampleCount = if ($null -eq $weakDropout) { 0 } else { [int][Math]::Round($weakDropout) }
        hotMakeupSampleCount = if ($null -eq $hotMakeup) { 0 } else { [int][Math]::Round($hotMakeup) }
        weakRecoveryPct = if ($null -eq $weakRecoveryPct) { $null } else { [Math]::Round([double]$weakRecoveryPct, 3) }
        weakStrongOutputGapDb = Get-LiveHistoryNumericOrNull $weak "weakStrongOutputGapDb"
        nr5OutputMovementDb = $nr5OutputMovement
        audioRmsMovementDb = $audioRmsMovement
        nr5MakeupMovementDb = $makeupMovement
        nr5MakeupMaxDb = $makeupMax
        nr5RecoveryDriveMovement = $recoveryMovement
        nr5TextureFillAverage = Get-LiveHistoryStatValue $Report "nr5TextureFill" "average"
        nr5SignalProbabilityAverage = Get-LiveHistoryStatValue $Report "nr5SignalProbability" "average"
        audioPeakMaxDbfs = $audioPeakMax
        adcHeadroomMinDb = $adcHeadroomMin
        monitorBacklogMaxSamples = $monitorBacklogMax
        latencyAverageMs = $latencyAverage
        audioFreshPct = $audioFreshPct
        rxMetersFreshPct = $rxMetersFreshPct
        safetySignals = @($signals.ToArray())
        safetyClassCounts = @($counts)
        safetyClassReadiness = @($readinessGapSummary)
        readinessGapSummary = @($readinessGapSummary)
        readinessGapSignalCount = Get-LiveHistoryReadinessSummarySignalCount -Summary $readinessGapSummary
        readinessGapNumericSignalCount = Get-LiveHistoryReadinessSummaryNumericSignalCount -Summary $readinessGapSummary
        readinessGapMax = Get-LiveHistoryReadinessSummaryMax -Summary $readinessGapSummary
        safetyRiskScore = Get-LiveHistorySafetyRiskScore -Counts $counts
        candidateComparisonReady = $candidateComparisonReady
        promotable = $candidateComparisonReady
        promotionBlockerCount = $candidateBlockerCount
        promotionBlockerClasses = @($candidateBlockingCounts | ForEach-Object { [string](Get-JsonValue $_ "safetyClass") })
        promotionBlockerClassCounts = @($candidateBlockingCounts)
        reviewStatus = $reviewStatus
        recommendations = @(Get-JsonArray $Report "recommendations")
    }
}

function Test-LiveHistorySafetySignalsMatch {
    param(
        $Actual,
        $Expected
    )

    $actualSignals = @($Actual | Where-Object { $null -ne $_ })
    $expectedSignals = @($Expected | Where-Object { $null -ne $_ })
    if ($actualSignals.Count -ne $expectedSignals.Count) {
        return $false
    }

    for ($i = 0; $i -lt $expectedSignals.Count; $i++) {
        $actualSignal = $actualSignals[$i]
        $expectedSignal = $expectedSignals[$i]
        foreach ($fieldName in @("safetyClass", "name", "thresholdDirection", "thresholdComparator", "comparison", "unit", "readinessGapUnit", "message")) {
            if (-not [string]::Equals([string](Get-JsonValue $actualSignal $fieldName), [string](Get-JsonValue $expectedSignal $fieldName), [StringComparison]::Ordinal)) {
                return $false
            }
        }
        foreach ($fieldName in @("value", "threshold", "readinessGap", "readinessMargin", "readinessGapScore")) {
            if (-not (Test-LiveHistoryScalarEquivalent -Actual (Get-JsonValue $actualSignal $fieldName) -Expected (Get-JsonValue $expectedSignal $fieldName))) {
                return $false
            }
        }
    }

    return $true
}

function Test-LiveHistoryDateEquivalent {
    param(
        $Actual,
        $Expected
    )

    $actualDate = Get-DateTimeOffsetValue $Actual
    $expectedDate = Get-DateTimeOffsetValue $Expected
    if ($null -ne $actualDate -and $null -ne $expectedDate) {
        return ($actualDate.UtcDateTime.Ticks -eq $expectedDate.UtcDateTime.Ticks)
    }

    return (Test-LiveHistoryScalarEquivalent -Actual $Actual -Expected $Expected)
}

function Get-LiveHistoryTraceSourceTextMismatches {
    param(
        $Actual,
        $Expected,
        [string[]]$Fields
    )

    $mismatches = New-Object System.Collections.Generic.List[string]
    foreach ($fieldName in @($Fields)) {
        if (-not [string]::Equals([string](Get-JsonValue $Actual $fieldName), [string](Get-JsonValue $Expected $fieldName), [StringComparison]::Ordinal)) {
            $mismatches.Add($fieldName) | Out-Null
        }
    }

    return @($mismatches.ToArray())
}

function Get-LiveHistoryTraceSourceScalarMismatches {
    param(
        $Actual,
        $Expected,
        [string[]]$Fields
    )

    $mismatches = New-Object System.Collections.Generic.List[string]
    foreach ($fieldName in @($Fields)) {
        if (-not (Test-LiveHistoryScalarEquivalent -Actual (Get-JsonValue $Actual $fieldName) -Expected (Get-JsonValue $Expected $fieldName))) {
            $mismatches.Add($fieldName) | Out-Null
        }
    }

    return @($mismatches.ToArray())
}

function Get-LiveHistoryTraceSourceDateMismatches {
    param(
        $Actual,
        $Expected,
        [string[]]$Fields
    )

    $mismatches = New-Object System.Collections.Generic.List[string]
    foreach ($fieldName in @($Fields)) {
        if (-not (Test-LiveHistoryDateEquivalent -Actual (Get-JsonValue $Actual $fieldName) -Expected (Get-JsonValue $Expected $fieldName))) {
            $mismatches.Add($fieldName) | Out-Null
        }
    }

    return @($mismatches.ToArray())
}

function ConvertTo-LiveHistoryScenarioId {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return ""
    }

    return (($Value.Trim().ToLowerInvariant() -replace "[^a-z0-9]+", "-").Trim("-"))
}

function Get-NormalizedStringArray {
    param(
        $Object,
        [Parameter(Mandatory = $true)][string]$Name,
        [switch]$ComparisonIds
    )

    $values = New-Object System.Collections.Generic.List[string]
    foreach ($item in @(Get-JsonArray $Object $Name)) {
        $value = if ($ComparisonIds) { ConvertTo-ComparisonId ([string]$item) } else { ConvertTo-LiveHistoryScenarioId ([string]$item) }
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            $values.Add($value) | Out-Null
        }
    }

    return @($values.ToArray() | Select-Object -Unique | Sort-Object)
}

function Test-StringArraySame {
    param(
        $Actual,
        $Expected
    )

    $actualItems = @($Actual | ForEach-Object { [string]$_ } | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | Select-Object -Unique | Sort-Object)
    $expectedItems = @($Expected | ForEach-Object { [string]$_ } | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | Select-Object -Unique | Sort-Object)
    if ($actualItems.Count -ne $expectedItems.Count) {
        return $false
    }

    for ($i = 0; $i -lt $expectedItems.Count; $i++) {
        if (-not [string]::Equals($actualItems[$i], $expectedItems[$i], [StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
    }

    return $true
}

function Test-StringArrayContains {
    param(
        $Values,
        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $false
    }

    foreach ($item in @($Values)) {
        if ([string]::Equals([string]$item, $Value, [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

function New-LiveHistoryExperimentCoverage {
    param(
        $Plan,
        $Records
    )

    if ($null -eq $Plan) {
        return [ordered]@{
            coverageScope = "latest-live-experiment-plan"
            status = "missing-plan"
            scenarioCount = 0
            coveredScenarioCount = 0
            requiredComparisonCount = 0
            coveredComparisonCount = 0
            missingComparisonCount = 0
            scenarioCoverage = @()
            missingComparisons = @()
        }
    }

    $scenarioCoverage = New-Object System.Collections.Generic.List[object]
    $missingComparisons = New-Object System.Collections.Generic.List[object]
    $coveredScenarioCount = 0
    $requiredComparisonCount = 0
    $coveredComparisonCount = 0

    foreach ($scenario in @(Get-JsonArray $Plan "scenarios")) {
        $scenarioId = ConvertTo-LiveHistoryScenarioId ([string](Get-JsonValue $scenario "scenarioId"))
        if ([string]::IsNullOrWhiteSpace($scenarioId)) {
            continue
        }

        $requiredComparisons = @(Get-NormalizedStringArray -Object $scenario -Name "requiredComparisons" -ComparisonIds)
        if ($requiredComparisons.Count -eq 0) {
            $requiredComparisons = @(Get-NormalizedStringArray -Object $Plan -Name "recommendedComparisons" -ComparisonIds)
        }

        $coveredComparisons = New-Object System.Collections.Generic.List[string]
        $scenarioTraceIds = New-Object System.Collections.Generic.List[string]
        $readyTraceCount = 0
        $nr5TraceCount = 0

        foreach ($record in @($Records)) {
            $recordScenarioId = ConvertTo-LiveHistoryScenarioId ([string](Get-JsonValue $record "scenarioId"))
            if (-not [string]::Equals($recordScenarioId, $scenarioId, [StringComparison]::OrdinalIgnoreCase)) {
                continue
            }

            $comparisonId = ConvertTo-ComparisonId ([string](Get-JsonValue $record "comparisonId"))
            if ([string]::IsNullOrWhiteSpace($comparisonId)) {
                continue
            }

            $scenarioTraceIds.Add([string](Get-JsonValue $record "traceId")) | Out-Null
            if ([int](Get-NumericValueOrDefault (Get-JsonValue $record "nr5SampleCount")) -gt 0) {
                $nr5TraceCount++
            }
            if (Test-Truthy (Get-JsonValue $record "readyForBenchmarkTrace")) {
                $readyTraceCount++
                if ($requiredComparisons -contains $comparisonId -and $coveredComparisons -notcontains $comparisonId) {
                    $coveredComparisons.Add($comparisonId) | Out-Null
                }
            }
        }

        $covered = @($coveredComparisons.ToArray() | Select-Object -Unique | Sort-Object)
        $missing = @($requiredComparisons | Where-Object { $covered -notcontains $_ } | Sort-Object)
        $requiredComparisonCount += $requiredComparisons.Count
        $coveredComparisonCount += $covered.Count
        if ($missing.Count -eq 0 -and $requiredComparisons.Count -gt 0) {
            $coveredScenarioCount++
        }
        foreach ($comparisonId in $missing) {
            $missingComparisons.Add([ordered]@{
                scenarioId = $scenarioId
                comparisonId = $comparisonId
            }) | Out-Null
        }

        $scenarioCoverage.Add([ordered]@{
            scenarioId = $scenarioId
            status = if ($requiredComparisons.Count -eq 0) { "no-required-comparisons" } elseif ($missing.Count -eq 0) { "complete" } elseif ($covered.Count -gt 0) { "partial" } else { "missing" }
            requiredComparisons = @($requiredComparisons)
            coveredComparisons = @($covered)
            missingComparisons = @($missing)
            readyTraceCount = $readyTraceCount
            nr5TraceCount = $nr5TraceCount
            traceIds = @($scenarioTraceIds.ToArray() | Select-Object -Unique | Sort-Object)
        }) | Out-Null
    }

    $scenarioCount = $scenarioCoverage.Count
    $missingComparisonCount = $missingComparisons.Count
    $status = if ($scenarioCount -le 0) {
        "missing-plan-scenarios"
    }
    elseif ($missingComparisonCount -eq 0) {
        "complete"
    }
    elseif ($coveredComparisonCount -gt 0) {
        "partial"
    }
    else {
        "not-started"
    }

    return [ordered]@{
        coverageScope = "latest-live-experiment-plan"
        status = $status
        scenarioCount = $scenarioCount
        coveredScenarioCount = $coveredScenarioCount
        requiredComparisonCount = $requiredComparisonCount
        coveredComparisonCount = $coveredComparisonCount
        missingComparisonCount = $missingComparisonCount
        scenarioCoverage = @($scenarioCoverage.ToArray())
        missingComparisons = @($missingComparisons.ToArray() | Sort-Object @{ Expression = { [string](Get-JsonValue $_ "scenarioId") } }, @{ Expression = { [string](Get-JsonValue $_ "comparisonId") } })
    }
}

function Test-LiveHistoryExperimentCoverageMatches {
    param(
        $Actual,
        $Expected
    )

    if ($null -eq $Actual -or $null -eq $Expected) {
        return ($null -eq $Actual -and $null -eq $Expected)
    }

    foreach ($field in @("coverageScope", "status")) {
        if (-not [string]::Equals([string](Get-JsonValue $Actual $field), [string](Get-JsonValue $Expected $field), [StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
    }
    foreach ($field in @("scenarioCount", "coveredScenarioCount", "requiredComparisonCount", "coveredComparisonCount", "missingComparisonCount")) {
        if ([int](Get-NumericValueOrDefault (Get-JsonValue $Actual $field) -1) -ne [int](Get-NumericValueOrDefault (Get-JsonValue $Expected $field) -1)) {
            return $false
        }
    }

    $actualScenarios = @(Get-JsonArray $Actual "scenarioCoverage")
    $expectedScenarios = @(Get-JsonArray $Expected "scenarioCoverage")
    if ($actualScenarios.Count -ne $expectedScenarios.Count) {
        return $false
    }

    foreach ($expectedScenario in @($expectedScenarios)) {
        $scenarioId = [string](Get-JsonValue $expectedScenario "scenarioId")
        $actualScenario = $null
        foreach ($item in @($actualScenarios)) {
            if ([string]::Equals([string](Get-JsonValue $item "scenarioId"), $scenarioId, [StringComparison]::OrdinalIgnoreCase)) {
                $actualScenario = $item
                break
            }
        }
        if ($null -eq $actualScenario) {
            return $false
        }
        if (-not [string]::Equals([string](Get-JsonValue $actualScenario "status"), [string](Get-JsonValue $expectedScenario "status"), [StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
        foreach ($field in @("readyTraceCount", "nr5TraceCount")) {
            if ([int](Get-NumericValueOrDefault (Get-JsonValue $actualScenario $field) -1) -ne [int](Get-NumericValueOrDefault (Get-JsonValue $expectedScenario $field) -1)) {
                return $false
            }
        }
        foreach ($field in @("requiredComparisons", "coveredComparisons", "missingComparisons", "traceIds")) {
            if (-not (Test-StringArraySame -Actual (Get-JsonArray $actualScenario $field) -Expected (Get-JsonArray $expectedScenario $field))) {
                return $false
            }
        }
    }

    $actualMissing = @(Get-JsonArray $Actual "missingComparisons" | ForEach-Object { "$(Get-JsonValue $_ "scenarioId")||$(Get-JsonValue $_ "comparisonId")" } | Sort-Object)
    $expectedMissing = @(Get-JsonArray $Expected "missingComparisons" | ForEach-Object { "$(Get-JsonValue $_ "scenarioId")||$(Get-JsonValue $_ "comparisonId")" } | Sort-Object)
    return (Test-StringArraySame -Actual $actualMissing -Expected $expectedMissing)
}

function Get-LiveHistoryTuningControlFamilyForSignal {
    param(
        [string]$SafetyClass,
        [string]$SignalName
    )

    switch -Regex ($SignalName) {
        "recovery-drive" { return "nr5-recovery-drive-damping" }
        "makeup" { return "nr5-makeup-gain-cap-and-slew" }
        "output-movement|audio-rms" { return "nr5-output-level-stability" }
        "weak-recovery|weak-dropout|weak-dropouts" { return "weak-signal-rescue-gating" }
        "audio-peak|clipping" { return "audio-peak-headroom" }
        "fresh" { return "diagnostic-freshness" }
        "headroom|adc" { return "front-end-headroom" }
        "backlog|audio-path" { return "audio-path-runtime" }
    }

    switch ($SafetyClass) {
        "pumping" { return "nr5-level-stability" }
        "weak-signal" { return "weak-signal-preservation" }
        "clipping" { return "level-headroom" }
        "freshness" { return "diagnostic-freshness" }
        "front-end" { return "front-end-headroom" }
        "audio-path" { return "audio-path-runtime" }
        "hard-gate" { return "capture-readiness" }
        default { return "dsp-review" }
    }
}

function Get-LiveHistoryTuningGuardrailForSafetyClass {
    param([string]$SafetyClass)

    switch ($SafetyClass) {
        "pumping" { return "Do not improve weak-signal recovery by increasing output movement, makeup movement, recovery-drive movement, hot makeup, or audible pumping." }
        "weak-signal" { return "Do not reduce weak-input recovery, increase weak dropouts, or bury faint speech/CW while lowering noise." }
        "clipping" { return "Do not allow final audio peaks or TX/monitor paths to approach clipping." }
        "freshness" { return "Do not tune from traces with stale diagnostics; recapture before accepting evidence." }
        "front-end" { return "Do not treat front-end overload or low ADC headroom as DSP improvement." }
        "audio-path" { return "Do not accept traces with monitor backlog or invalid audio-path timing." }
        "hard-gate" { return "Clear endpoint failures and hard blockers before using the trace as tuning evidence." }
        default { return "Keep the change opt-in until live, fixture, and reference evidence agree." }
    }
}

function Get-LiveHistoryTuningRationaleForSafetyClass {
    param(
        [string]$SafetyClass,
        [string]$SignalName
    )

    switch ($SafetyClass) {
        "pumping" { return "Latest NR5 evidence is level-stability limited; inspect output movement, makeup movement, and recovery-drive movement before changing weak-fill thresholds." }
        "weak-signal" { return "Latest NR5 evidence is weak-signal limited; preserve recovery and reduce dropouts before tightening noise suppression." }
        "clipping" { return "Latest NR5 evidence is level-headroom limited; reduce peak or makeup risk before candidate comparison." }
        "freshness" { return "Diagnostics were not fresh enough to support tuning; fix capture fidelity before interpreting NR behavior." }
        "front-end" { return "Front-end evidence is limiting the trace; correct RF/ADC headroom before attributing symptoms to DSP." }
        "audio-path" { return "Audio-path runtime evidence is limiting the trace; fix monitor or latency evidence before tuning." }
        "hard-gate" { return "The trace has hard blockers or is not benchmark-ready; clear capture failures before using it as evidence." }
        default { return "Resolve the remaining safety signal before advancing this tuning candidate." }
    }
}

function Select-LiveHistoryWorstSafetySignal {
    param(
        $Signals,
        [string]$SafetyClass = ""
    )

    $items = @($Signals | Where-Object {
        [string]::IsNullOrWhiteSpace($SafetyClass) -or
            [string]::Equals([string](Get-JsonValue $_ "safetyClass"), $SafetyClass, [StringComparison]::OrdinalIgnoreCase)
    })
    if ($items.Count -eq 0) {
        return $null
    }

    return @($items | Sort-Object `
        @{ Expression = { $value = Get-NumericValue (Get-JsonValue $_ "readinessGap"); if ($null -eq $value) { -1.0 } else { $value } }; Descending = $true },
        @{ Expression = { [string](Get-JsonValue $_ "name") }; Ascending = $true })[0]
}

function Select-LiveHistoryWorstTrendDetail {
    param($Trend)

    $details = @(Get-JsonArray $Trend "readinessGapTrendDetails" | Where-Object {
        $status = [string](Get-JsonValue $_ "trendStatus")
        $status -eq "new-gap" -or $status -eq "gap-widened"
    })
    if ($details.Count -eq 0) {
        return $null
    }

    return @($details | Sort-Object `
        @{ Expression = { $value = Get-NumericValue (Get-JsonValue $_ "readinessGapDelta"); if ($null -eq $value) { 0.0 } else { [Math]::Abs($value) } }; Descending = $true },
        @{ Expression = { [string](Get-JsonValue $_ "name") }; Ascending = $true })[0]
}

function Add-LiveHistoryExpectedTuningAction {
    param(
        [System.Collections.Generic.List[object]]$Actions,
        [Parameter(Mandatory = $true)][string]$ActionId,
        [Parameter(Mandatory = $true)][string]$SafetyClass,
        [string]$SignalName = "",
        [string]$TrendSource = "",
        $LatestReadinessGap = $null,
        [string]$ReadinessGapUnit = "",
        $PreviousDelta = $null,
        $ReferenceDelta = $null,
        [string]$ReferenceTraceId = "",
        [string]$ReferenceTraceRole = "",
        [string]$Rationale = ""
    )

    foreach ($action in @($Actions.ToArray())) {
        if ([string]::Equals([string](Get-JsonValue $action "actionId"), $ActionId, [StringComparison]::OrdinalIgnoreCase)) {
            return
        }
    }

    if ([string]::IsNullOrWhiteSpace($SignalName)) {
        $SignalName = "safety-signal"
    }
    if ([string]::IsNullOrWhiteSpace($ReadinessGapUnit)) {
        $ReadinessGapUnit = "value"
    }
    if ([string]::IsNullOrWhiteSpace($Rationale)) {
        $Rationale = Get-LiveHistoryTuningRationaleForSafetyClass -SafetyClass $SafetyClass -SignalName $SignalName
    }

    $Actions.Add([ordered]@{
        priority = $Actions.Count + 1
        actionId = $ActionId
        safetyClass = $SafetyClass
        signalName = $SignalName
        controlFamily = Get-LiveHistoryTuningControlFamilyForSignal -SafetyClass $SafetyClass -SignalName $SignalName
        trendSource = $TrendSource
        latestReadinessGap = $LatestReadinessGap
        readinessGapUnit = $ReadinessGapUnit
        latestVsPreviousGapDelta = $PreviousDelta
        latestVsReferenceGapDelta = $ReferenceDelta
        referenceTraceId = $ReferenceTraceId
        referenceTraceRole = $ReferenceTraceRole
        rationale = $Rationale
        guardrail = Get-LiveHistoryTuningGuardrailForSafetyClass -SafetyClass $SafetyClass
    }) | Out-Null
}

function New-LiveHistoryExpectedTuningActionPlan {
    param(
        $Latest,
        $PromotionDecision,
        $LatestVsPreviousTrend,
        $LatestVsReferenceTrend
    )

    $actions = New-Object System.Collections.Generic.List[object]
    if ($null -eq $Latest) {
        Add-LiveHistoryExpectedTuningAction -Actions $actions -ActionId "capture-nr5-live-history" -SafetyClass "tooling" -SignalName "no-nr5-history" -Rationale "No NR5 live history exists; capture G2 NR5 and reference windows before tuning."
        return [ordered]@{
            planScope = "candidate-comparison-only"
            status = "no-nr5-history"
            directionStatus = "no-nr5-history"
            promotionStatus = ""
            latestTraceId = ""
            referenceTraceId = ""
            referenceTraceRole = ""
            latestVsPreviousStatus = ""
            latestVsReferenceStatus = ""
            primarySafetyClass = "tooling"
            primarySignalName = "no-nr5-history"
            topControlFamily = "capture-readiness"
            actionCount = $actions.Count
            actions = @($actions.ToArray())
        }
    }

    $promotionStatus = [string](Get-JsonValue $PromotionDecision "status")
    $candidateReady = Test-Truthy (Get-JsonValue $PromotionDecision "candidatePromotionReady")
    $latestTraceId = [string](Get-JsonValue $Latest "traceId")
    $referenceTraceId = [string](Get-JsonValue $PromotionDecision "referenceTraceId")
    $referenceTraceRole = [string](Get-JsonValue $PromotionDecision "referenceTraceRole")
    $latestVsPreviousStatus = [string](Get-JsonValue $LatestVsPreviousTrend "status")
    $latestVsReferenceStatus = [string](Get-JsonValue $LatestVsReferenceTrend "status")
    $directionStatus = if ($candidateReady) {
        "candidate-ready"
    }
    elseif ($latestVsReferenceStatus -eq "regressed" -or $latestVsReferenceStatus -eq "mixed") {
        "latest-regressed-vs-reference"
    }
    elseif ($latestVsPreviousStatus -eq "regressed" -or $latestVsPreviousStatus -eq "mixed") {
        "latest-regressed-vs-previous"
    }
    elseif ($latestVsReferenceStatus -eq "improved") {
        "latest-improved-vs-reference"
    }
    else {
        "blocked"
    }

    $latestSignals = @(Get-JsonArray $Latest "safetySignals")
    $primaryClass = ""
    switch ($promotionStatus) {
        "ready-for-candidate-comparison" { $primaryClass = "candidate-review" }
        "blocked-hard-gate" { $primaryClass = "hard-gate" }
        "blocked-clipping" { $primaryClass = "clipping" }
        "blocked-diagnostic-freshness" { $primaryClass = "freshness" }
        "blocked-runtime-evidence" { $primaryClass = "front-end" }
        "blocked-weak-and-pumping" { $primaryClass = "pumping" }
        "blocked-pumping" { $primaryClass = "pumping" }
        "blocked-weak-signal" { $primaryClass = "weak-signal" }
        default {
            $primaryClass = [string](Get-JsonValue (Select-LiveHistoryWorstSafetySignal -Signals $latestSignals) "safetyClass")
            if ([string]::IsNullOrWhiteSpace($primaryClass)) {
                $primaryClass = "dsp-review"
            }
        }
    }

    if ($candidateReady) {
        Add-LiveHistoryExpectedTuningAction `
            -Actions $actions `
            -ActionId "run-candidate-comparison" `
            -SafetyClass "candidate-review" `
            -SignalName "candidate-comparison" `
            -TrendSource "promotion" `
            -ReferenceTraceId $referenceTraceId `
            -ReferenceTraceRole $referenceTraceRole `
            -Rationale "Latest trace is clean enough for candidate comparison; compare against current-Zeus and Thetis-parity evidence before changing defaults."
    }
    else {
        $signal = Select-LiveHistoryWorstSafetySignal -Signals $latestSignals -SafetyClass $primaryClass
        if ($null -eq $signal) {
            $signal = Select-LiveHistoryWorstSafetySignal -Signals $latestSignals
        }
        $signalName = [string](Get-JsonValue $signal "name")
        $gap = Get-JsonValue $signal "readinessGap"
        $unit = [string](Get-JsonValue $signal "readinessGapUnit")
        $actionId = switch ($primaryClass) {
            "hard-gate" { "clear-hard-gates" }
            "clipping" { "restore-audio-headroom" }
            "freshness" { "restore-diagnostic-freshness" }
            "front-end" { "restore-front-end-headroom" }
            "audio-path" { "restore-audio-path-runtime" }
            "weak-signal" { "restore-weak-signal-recovery" }
            "pumping" { if ($promotionStatus -eq "blocked-weak-and-pumping") { "balance-weak-recovery-and-pumping" } else { "reduce-nr5-pumping" } }
            default { "resolve-latest-safety-signal" }
        }
        Add-LiveHistoryExpectedTuningAction `
            -Actions $actions `
            -ActionId $actionId `
            -SafetyClass $primaryClass `
            -SignalName $signalName `
            -TrendSource "latest-blocker" `
            -LatestReadinessGap $gap `
            -ReadinessGapUnit $unit `
            -ReferenceTraceId $referenceTraceId `
            -ReferenceTraceRole $referenceTraceRole
    }

    $referenceDetail = Select-LiveHistoryWorstTrendDetail -Trend $LatestVsReferenceTrend
    if ($null -ne $referenceDetail) {
        Add-LiveHistoryExpectedTuningAction `
            -Actions $actions `
            -ActionId "restore-reference-gap-regression" `
            -SafetyClass ([string](Get-JsonValue $referenceDetail "safetyClass")) `
            -SignalName ([string](Get-JsonValue $referenceDetail "name")) `
            -TrendSource "latest-vs-reference" `
            -LatestReadinessGap (Get-JsonValue $referenceDetail "latestReadinessGap") `
            -ReadinessGapUnit ([string](Get-JsonValue $referenceDetail "readinessGapUnit")) `
            -ReferenceDelta (Get-JsonValue $referenceDetail "readinessGapDelta") `
            -ReferenceTraceId $referenceTraceId `
            -ReferenceTraceRole $referenceTraceRole `
            -Rationale "Latest trace regressed against the selected reference; restore the reference behavior before treating this tuning direction as progress."
    }

    $previousDetail = Select-LiveHistoryWorstTrendDetail -Trend $LatestVsPreviousTrend
    if ($null -ne $previousDetail) {
        Add-LiveHistoryExpectedTuningAction `
            -Actions $actions `
            -ActionId "undo-latest-step-regression" `
            -SafetyClass ([string](Get-JsonValue $previousDetail "safetyClass")) `
            -SignalName ([string](Get-JsonValue $previousDetail "name")) `
            -TrendSource "latest-vs-previous" `
            -LatestReadinessGap (Get-JsonValue $previousDetail "latestReadinessGap") `
            -ReadinessGapUnit ([string](Get-JsonValue $previousDetail "readinessGapUnit")) `
            -PreviousDelta (Get-JsonValue $previousDetail "readinessGapDelta") `
            -ReferenceTraceId ([string](Get-JsonValue $LatestVsPreviousTrend "referenceTraceId")) `
            -ReferenceTraceRole "previous-nr5" `
            -Rationale "Latest trace introduced or widened a gap against the immediately previous NR5 trace; isolate this last tuning step before further changes."
    }

    $topAction = if ($actions.Count -gt 0) { $actions[0] } else { $null }
    return [ordered]@{
        planScope = "candidate-comparison-only"
        status = $promotionStatus
        directionStatus = $directionStatus
        promotionStatus = $promotionStatus
        latestTraceId = $latestTraceId
        referenceTraceId = $referenceTraceId
        referenceTraceRole = $referenceTraceRole
        latestVsPreviousStatus = $latestVsPreviousStatus
        latestVsReferenceStatus = $latestVsReferenceStatus
        primarySafetyClass = if ($null -eq $topAction) { $primaryClass } else { [string](Get-JsonValue $topAction "safetyClass") }
        primarySignalName = if ($null -eq $topAction) { "" } else { [string](Get-JsonValue $topAction "signalName") }
        topControlFamily = if ($null -eq $topAction) { "" } else { [string](Get-JsonValue $topAction "controlFamily") }
        actionCount = $actions.Count
        actions = @($actions.ToArray())
    }
}

function Get-LiveHistoryExperimentRequiredComparisons {
    return @("current-zeus", "nr5-spnr")
}

function Get-LiveHistoryExperimentRequiredEvidence {
    return @(
        "current-Zeus baseline live diagnostics trace index",
        "NR5/SPNR opt-in candidate live diagnostics trace index",
        "matrix comparison report with no regressions",
        "operator notes for speech texture and pumping"
    )
}

function Get-LiveHistoryExperimentGatesForControlFamily {
    param(
        [string]$ControlFamily,
        [string]$SafetyClass
    )

    switch ($ControlFamily) {
        "nr5-output-level-stability" {
            return @(
                "No nr5OutputMovementDb regression against current-Zeus or reference trace.",
                "No nr5MakeupMovementDb, nr5MakeupMaxDb, or nr5RecoveryDriveMovement regression.",
                "No weak-signal recovery loss while reducing level movement.",
                "No audio peak max above -1 dBFS."
            )
        }
        "nr5-weak-signal-recovery" {
            return @(
                "Weak recovery stays at or above 95 percent when weak input is present.",
                "No new weak-input dropouts.",
                "No hot makeup samples.",
                "No output-level pumping regression while improving recovery."
            )
        }
        "nr5-recovery-makeup-bounds" {
            return @(
                "No makeup gain movement or max-gain regression.",
                "No recovery-drive movement regression.",
                "Weak recovery and dropout counters stay neutral or improve.",
                "No audio peak max above -1 dBFS."
            )
        }
        "nr5-mask-fill-texture" {
            return @(
                "Texture-fill movement does not create audible burbling or speech loss.",
                "Noise-only windows remain closed.",
                "Weak recovery stays neutral or improves.",
                "Output and makeup movement do not regress."
            )
        }
        "audio-headroom" {
            return @(
                "Audio peak max remains at or below -1 dBFS.",
                "No audio RMS movement regression.",
                "No monitor backlog or audio-path freshness regression.",
                "Weak recovery stays neutral or improves."
            )
        }
        "capture-readiness" {
            return @(
                "All live diagnostics endpoint samples succeed.",
                "Runtime evidence, RX meters, and final audio are fresh for every runtime sample.",
                "NR5 AGC diagnostics cover every NR5 sample.",
                "G2 hardware evidence remains connected and in-family."
            )
        }
        default {
            return @(
                "No live matrix regression against current-Zeus.",
                "No weak-signal loss, output pumping, clipping, or diagnostic freshness regression.",
                "Candidate remains opt-in until fixture and on-air evidence agree."
            )
        }
    }
}

function Add-LiveHistoryExpectedExperimentScenario {
    param(
        [System.Collections.Generic.List[object]]$Scenarios,
        $Action,
        [Parameter(Mandatory = $true)][string]$ScenarioId,
        [Parameter(Mandatory = $true)][string]$Purpose,
        [string]$OperatorSetup = "",
        [int]$SampleCount = 60,
        [int]$IntervalMs = 1000
    )

    foreach ($scenario in @($Scenarios.ToArray())) {
        if ([string]::Equals([string](Get-JsonValue $scenario "scenarioId"), $ScenarioId, [StringComparison]::OrdinalIgnoreCase)) {
            return
        }
    }

    $controlFamily = [string](Get-JsonValue $Action "controlFamily")
    $safetyClass = [string](Get-JsonValue $Action "safetyClass")
    if ([string]::IsNullOrWhiteSpace($controlFamily)) {
        $controlFamily = "dsp-review"
    }
    if ([string]::IsNullOrWhiteSpace($safetyClass)) {
        $safetyClass = "dsp-review"
    }

    $Scenarios.Add([ordered]@{
        priority = $Scenarios.Count + 1
        scenarioId = $ScenarioId
        purpose = $Purpose
        sourceActionId = [string](Get-JsonValue $Action "actionId")
        sourceActionPriority = [int](Get-NumericValueOrDefault (Get-JsonValue $Action "priority"))
        sourceTrend = [string](Get-JsonValue $Action "trendSource")
        controlFamily = $controlFamily
        safetyClass = $safetyClass
        signalName = [string](Get-JsonValue $Action "signalName")
        sampleCount = $SampleCount
        intervalMs = $IntervalMs
        requiredComparisons = @(Get-LiveHistoryExperimentRequiredComparisons)
        acceptanceGates = @(Get-LiveHistoryExperimentGatesForControlFamily -ControlFamily $controlFamily -SafetyClass $safetyClass)
        operatorSetup = $OperatorSetup
    }) | Out-Null
}

function Add-LiveHistoryExpectedExperimentScenariosForAction {
    param(
        [System.Collections.Generic.List[object]]$Scenarios,
        $Action
    )

    $controlFamily = [string](Get-JsonValue $Action "controlFamily")
    switch ($controlFamily) {
        "nr5-output-level-stability" {
            Add-LiveHistoryExpectedExperimentScenario -Scenarios $Scenarios -Action $Action -ScenarioId "ssb-like-speech-dynamic" -Purpose "Stress dynamic voice and AGC-driven NR5 output movement." -OperatorSetup "Use a live SSB voice segment with obvious level swings or QSB."
            Add-LiveHistoryExpectedExperimentScenario -Scenarios $Scenarios -Action $Action -ScenarioId "adjacent-strong-signal" -Purpose "Check strong-neighbor energy without rescue or makeup pumping." -OperatorSetup "Tune near a strong adjacent signal while preserving the wanted signal."
            Add-LiveHistoryExpectedExperimentScenario -Scenarios $Scenarios -Action $Action -ScenarioId "noise-only-gating" -Purpose "Verify level rescue and makeup stay closed without wanted signal." -OperatorSetup "Use a clear/noise-only slice with squelch open if needed for audio evidence."
        }
        "nr5-weak-signal-recovery" {
            Add-LiveHistoryExpectedExperimentScenario -Scenarios $Scenarios -Action $Action -ScenarioId "weak-cw-carrier" -Purpose "Measure faint carrier recovery without dropout." -OperatorSetup "Use the weakest stable carrier available on G2."
            Add-LiveHistoryExpectedExperimentScenario -Scenarios $Scenarios -Action $Action -ScenarioId "weak-ssb-speech" -Purpose "Measure faint speech preservation under NR5 rescue." -OperatorSetup "Use weak SSB speech at the edge of readability."
            Add-LiveHistoryExpectedExperimentScenario -Scenarios $Scenarios -Action $Action -ScenarioId "fading-weak-signal" -Purpose "Check recovery during QSB and near-threshold fades." -OperatorSetup "Prefer a weak signal with natural fading or flutter."
        }
        "nr5-recovery-makeup-bounds" {
            Add-LiveHistoryExpectedExperimentScenario -Scenarios $Scenarios -Action $Action -ScenarioId "ssb-like-speech-dynamic" -Purpose "Bound recovery and makeup motion during voice level swings." -OperatorSetup "Use a dynamic SSB voice segment."
            Add-LiveHistoryExpectedExperimentScenario -Scenarios $Scenarios -Action $Action -ScenarioId "weak-ssb-speech" -Purpose "Confirm makeup bounds do not bury weak speech." -OperatorSetup "Use weak speech where recovery matters."
            Add-LiveHistoryExpectedExperimentScenario -Scenarios $Scenarios -Action $Action -ScenarioId "noise-only-gating" -Purpose "Confirm makeup does not open on noise-only audio." -OperatorSetup "Use a no-signal slice after candidate tuning."
        }
        "nr5-mask-fill-texture" {
            Add-LiveHistoryExpectedExperimentScenario -Scenarios $Scenarios -Action $Action -ScenarioId "ssb-like-speech" -Purpose "Listen and measure speech texture while mask fill changes." -OperatorSetup "Use normal SSB speech with operator notes."
            Add-LiveHistoryExpectedExperimentScenario -Scenarios $Scenarios -Action $Action -ScenarioId "weak-ssb-speech" -Purpose "Check weak speech intelligibility with mask fill active." -OperatorSetup "Use weak speech that previously needed NR5 rescue."
            Add-LiveHistoryExpectedExperimentScenario -Scenarios $Scenarios -Action $Action -ScenarioId "noise-only-gating" -Purpose "Check mask fill does not create noise-only artifacts." -OperatorSetup "Use no wanted signal and capture operator notes."
        }
        "audio-headroom" {
            Add-LiveHistoryExpectedExperimentScenario -Scenarios $Scenarios -Action $Action -ScenarioId "strong-signal-headroom" -Purpose "Verify peak headroom on strong live audio." -OperatorSetup "Use the strongest clean SSB voice available without front-end overload."
            Add-LiveHistoryExpectedExperimentScenario -Scenarios $Scenarios -Action $Action -ScenarioId "ssb-like-speech-dynamic" -Purpose "Check RMS/peak motion on dynamic voice." -OperatorSetup "Use voice with level swings."
        }
        "capture-readiness" {
            Add-LiveHistoryExpectedExperimentScenario -Scenarios $Scenarios -Action $Action -ScenarioId "diagnostic-freshness-preflight" -Purpose "Re-establish complete live diagnostics before tuning." -OperatorSetup "Run against the active G2 backend before any candidate capture."
            Add-LiveHistoryExpectedExperimentScenario -Scenarios $Scenarios -Action $Action -ScenarioId "g2-hardware-preflight" -Purpose "Confirm G2 hardware and endpoint readiness." -OperatorSetup "Capture hardware diagnostics in the same session."
        }
        default {
            Add-LiveHistoryExpectedExperimentScenario -Scenarios $Scenarios -Action $Action -ScenarioId "weak-cw-carrier" -Purpose "Cover weak-signal preservation." -OperatorSetup "Use the weakest stable signal available."
            Add-LiveHistoryExpectedExperimentScenario -Scenarios $Scenarios -Action $Action -ScenarioId "ssb-like-speech" -Purpose "Cover speech quality and pumping risk." -OperatorSetup "Use live SSB speech with operator notes."
            Add-LiveHistoryExpectedExperimentScenario -Scenarios $Scenarios -Action $Action -ScenarioId "noise-only-gating" -Purpose "Cover noise-only false-open behavior." -OperatorSetup "Use no wanted signal."
        }
    }
}

function New-LiveHistoryExpectedExperimentPlan {
    param($ActionPlan)

    $scenarios = New-Object System.Collections.Generic.List[object]
    $actions = @(Get-JsonArray $ActionPlan "actions")
    foreach ($action in @($actions | Select-Object -First 3)) {
        Add-LiveHistoryExpectedExperimentScenariosForAction -Scenarios $scenarios -Action $action
    }

    if ($scenarios.Count -eq 0) {
        Add-LiveHistoryExpectedExperimentScenario `
            -Scenarios $scenarios `
            -Action ([ordered]@{ actionId = "capture-nr5-live-history"; priority = 1; controlFamily = "capture-readiness"; safetyClass = "tooling"; signalName = "no-nr5-history"; trendSource = "tooling" }) `
            -ScenarioId "diagnostic-freshness-preflight" `
            -Purpose "Capture enough G2 live diagnostics to build a tuning history." `
            -OperatorSetup "Run the watcher on the connected G2 backend before tuning."
    }

    $scenarioIds = @($scenarios.ToArray() | ForEach-Object { [string](Get-JsonValue $_ "scenarioId") })
    $scenarioArgument = if ($scenarioIds.Count -gt 0) { $scenarioIds -join "," } else { "<scenario-ids>" }
    $sampleCounts = @($scenarios.ToArray() | ForEach-Object { [int](Get-NumericValueOrDefault (Get-JsonValue $_ "sampleCount") 60) })
    $intervalValues = @($scenarios.ToArray() | ForEach-Object { [int](Get-NumericValueOrDefault (Get-JsonValue $_ "intervalMs") 1000) })
    $recommendedSampleCount = if ($sampleCounts.Count -gt 0) { [int](@($sampleCounts | Measure-Object -Maximum).Maximum) } else { 60 }
    $recommendedIntervalMs = if ($intervalValues.Count -gt 0) { [int](@($intervalValues | Measure-Object -Maximum).Maximum) } else { 1000 }
    $topAction = if ($actions.Count -gt 0) { $actions[0] } else { $null }

    return [ordered]@{
        planScope = "candidate-comparison-only"
        status = [string](Get-JsonValue $ActionPlan "status")
        sourceActionPlanStatus = [string](Get-JsonValue $ActionPlan "status")
        sourceDirectionStatus = [string](Get-JsonValue $ActionPlan "directionStatus")
        primaryControlFamily = [string](Get-JsonValue $ActionPlan "topControlFamily")
        primarySafetyClass = [string](Get-JsonValue $ActionPlan "primarySafetyClass")
        primarySignalName = [string](Get-JsonValue $ActionPlan "primarySignalName")
        referenceTraceId = [string](Get-JsonValue $ActionPlan "referenceTraceId")
        referenceTraceRole = [string](Get-JsonValue $ActionPlan "referenceTraceRole")
        topActionId = if ($null -eq $topAction) { "" } else { [string](Get-JsonValue $topAction "actionId") }
        recommendedComparisons = @(Get-LiveHistoryExperimentRequiredComparisons)
        recommendedSampleCount = $recommendedSampleCount
        recommendedIntervalMs = $recommendedIntervalMs
        minimumScenarioCount = [Math]::Min(3, $scenarios.Count)
        scenarioCount = $scenarios.Count
        scenarioIds = @($scenarioIds)
        scenarios = @($scenarios.ToArray())
        matrixCommandTemplates = [ordered]@{
            baseline = "powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-live-diagnostics-matrix.ps1 -BundleDir captures\dsp-modernization\<timestamp> -ScenarioIds $scenarioArgument -ComparisonId current-zeus -IndexPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-trace-index.baseline.json -ReportPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-matrix-report.baseline.json -Samples $recommendedSampleCount -IntervalMs $recommendedIntervalMs"
            candidate = "powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-live-diagnostics-matrix.ps1 -BundleDir captures\dsp-modernization\<timestamp> -ScenarioIds $scenarioArgument -ComparisonId nr5-spnr -IndexPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-trace-index.candidate.json -ReportPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-matrix-report.candidate.json -Samples $recommendedSampleCount -IntervalMs $recommendedIntervalMs"
            compare = "powershell -NoProfile -ExecutionPolicy Bypass -File tools\compare-dsp-live-diagnostics-matrix.ps1 -BundleDir captures\dsp-modernization\<timestamp> -BaselineIndexPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-trace-index.baseline.json -CandidateIndexPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-trace-index.candidate.json -ReportPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-trace-comparison.json -FailOnRegression"
        }
        defaultBehaviorChangeReady = $false
        requiredEvidence = @(Get-LiveHistoryExperimentRequiredEvidence)
    }
}

function Get-LiveHistoryTrendSummaryEntry {
    param(
        $Summary,
        [Parameter(Mandatory = $true)][string]$SafetyClass,
        [Parameter(Mandatory = $true)][string]$Unit
    )

    foreach ($item in @($Summary)) {
        if ([string]::Equals([string](Get-JsonValue $item "safetyClass"), $SafetyClass, [StringComparison]::OrdinalIgnoreCase) -and
            [string]::Equals([string](Get-JsonValue $item "readinessGapUnit"), $Unit, [StringComparison]::OrdinalIgnoreCase)) {
            return $item
        }
    }

    return $null
}

function Get-LiveHistoryTrendDetailByKey {
    param(
        $Details,
        [Parameter(Mandatory = $true)][string]$Key
    )

    foreach ($detail in @($Details)) {
        if ([string]::Equals([string](Get-JsonValue $detail "key"), $Key, [StringComparison]::OrdinalIgnoreCase)) {
            return $detail
        }
    }

    return $null
}

function Test-LiveHistoryReadinessTrendMatches {
    param(
        $Actual,
        $Expected
    )

    if ($null -eq $Actual -and $null -eq $Expected) {
        return $true
    }
    if ($null -eq $Actual -or $null -eq $Expected) {
        return $false
    }

    foreach ($field in @("status", "latestTraceId", "previousTraceId")) {
        if (-not [string]::Equals([string](Get-JsonValue $Actual $field), [string](Get-JsonValue $Expected $field), [StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
    }

    $actualScope = Get-JsonValue $Actual "comparisonScope"
    $expectedScope = [string](Get-JsonValue $Expected "comparisonScope")
    $contextRequired = ($null -ne $actualScope -or [string]::Equals($expectedScope, "latest-vs-reference", [StringComparison]::OrdinalIgnoreCase))
    if ($contextRequired) {
        foreach ($field in @("comparisonScope", "referenceTraceId", "referenceTraceRole")) {
            if (-not [string]::Equals([string](Get-JsonValue $Actual $field), [string](Get-JsonValue $Expected $field), [StringComparison]::OrdinalIgnoreCase)) {
                return $false
            }
        }

        foreach ($field in @("latestIsReference", "referenceIsRecommended")) {
            if ((Test-Truthy (Get-JsonValue $Actual $field)) -ne (Test-Truthy (Get-JsonValue $Expected $field))) {
                return $false
            }
        }
    }

    foreach ($field in @(
        "signalCount",
        "comparedSignalCount",
        "latestReadinessGapSignalCount",
        "previousReadinessGapSignalCount",
        "readinessGapSignalCountDelta",
        "improvedSignalCount",
        "regressedSignalCount",
        "narrowedSignalCount",
        "widenedSignalCount",
        "unchangedSignalCount",
        "clearedSignalCount",
        "resolvedSignalCount",
        "newSignalCount",
        "incomparableSignalCount",
        "persistentSignalCount",
        "detailCount")) {
        if ([int](Get-NumericValueOrDefault (Get-JsonValue $Actual $field) -999999) -ne [int](Get-NumericValueOrDefault (Get-JsonValue $Expected $field) -999999)) {
            return $false
        }
    }

    foreach ($field in @("latestReadinessGapMax", "previousReadinessGapMax", "readinessGapMaxDelta")) {
        if (-not (Test-NumericClose -Actual (Get-JsonValue $Actual $field) -Expected (Get-JsonValue $Expected $field))) {
            return $false
        }
    }

    $actualDetails = @(Get-JsonArray $Actual "readinessGapTrendDetails")
    if ($actualDetails.Count -eq 0) {
        $actualDetails = @(Get-JsonArray $Actual "details")
    }
    $expectedDetails = @(Get-JsonArray $Expected "details")
    if ($actualDetails.Count -ne $expectedDetails.Count) {
        return $false
    }
    foreach ($expectedDetail in @($expectedDetails)) {
        $key = [string](Get-JsonValue $expectedDetail "key")
        $actualDetail = Get-LiveHistoryTrendDetailByKey -Details $actualDetails -Key $key
        if ($null -eq $actualDetail) {
            return $false
        }
        foreach ($field in @("status", "trendStatus", "safetyClass", "name", "readinessGapUnit", "thresholdDirection")) {
            if (-not [string]::Equals([string](Get-JsonValue $actualDetail $field), [string](Get-JsonValue $expectedDetail $field), [StringComparison]::OrdinalIgnoreCase)) {
                return $false
            }
        }
        foreach ($field in @("previousReadinessGap", "latestReadinessGap", "readinessGapDelta")) {
            if (-not (Test-NumericClose -Actual (Get-JsonValue $actualDetail $field) -Expected (Get-JsonValue $expectedDetail $field))) {
                return $false
            }
        }
    }

    $actualSummary = @(Get-JsonArray $Actual "readinessGapTrendBuckets")
    if ($actualSummary.Count -eq 0) {
        $actualSummary = @(Get-JsonArray $Actual "safetyClassUnitTrend")
    }
    $expectedSummary = @(Get-JsonArray $Expected "safetyClassUnitTrend")
    if ($actualSummary.Count -ne $expectedSummary.Count) {
        return $false
    }
    foreach ($expectedItem in @($expectedSummary)) {
        $safetyClass = [string](Get-JsonValue $expectedItem "safetyClass")
        $unit = [string](Get-JsonValue $expectedItem "readinessGapUnit")
        $actualItem = Get-LiveHistoryTrendSummaryEntry -Summary $actualSummary -SafetyClass $safetyClass -Unit $unit
        if ($null -eq $actualItem) {
            return $false
        }
        foreach ($field in @("status")) {
            if (-not [string]::Equals([string](Get-JsonValue $actualItem $field), [string](Get-JsonValue $expectedItem $field), [StringComparison]::OrdinalIgnoreCase)) {
                return $false
            }
        }
        foreach ($field in @("signalCount", "improvedSignalCount", "regressedSignalCount", "narrowedSignalCount", "widenedSignalCount", "unchangedSignalCount", "clearedSignalCount", "resolvedSignalCount", "newSignalCount")) {
            if ([int](Get-NumericValueOrDefault (Get-JsonValue $actualItem $field) -999999) -ne [int](Get-NumericValueOrDefault (Get-JsonValue $expectedItem $field) -999999)) {
                return $false
            }
        }
        foreach ($field in @("previousReadinessGapTotal", "latestReadinessGapTotal", "readinessGapDeltaTotal", "readinessGapDeltaMin", "readinessGapDeltaMax")) {
            if (-not (Test-NumericClose -Actual (Get-JsonValue $actualItem $field) -Expected (Get-JsonValue $expectedItem $field))) {
                return $false
            }
        }
    }

    return $true
}

function Get-StringArray {
    param(
        $Object,
        [Parameter(Mandatory = $true)][string]$Name
    )

    return @(Get-JsonArray $Object $Name | ForEach-Object { [string]$_ })
}

function ConvertTo-HardwareId {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return ""
    }

    return ($Value.Trim().ToLowerInvariant() -replace "[^a-z0-9]+", "")
}

function Test-G2BenchmarkTarget {
    param([string]$Value)

    return (ConvertTo-HardwareId $Value) -eq "g2"
}

function Test-OrionMkIIBoard {
    param([string]$Value)

    return (ConvertTo-HardwareId $Value) -eq "orionmkii"
}

function Test-G2Variant {
    param([string]$Value)

    $id = ConvertTo-HardwareId $Value
    return ($id -eq "g2" -or $id -eq "g21k")
}

function Get-HardwareDiagnosticField {
    param(
        $Hardware,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $value = Get-JsonValue $Hardware $Name
    if ($null -ne $value) {
        return $value
    }

    $potential = Get-JsonValue $Hardware "hardwarePotential"
    if ($null -ne $potential) {
        return Get-JsonValue $potential $Name
    }

    return $null
}

function Get-BundlePath {
    param(
        [Parameter(Mandatory = $true)][string]$BundlePath,
        [Parameter(Mandatory = $true)][string]$Path
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return Join-Path $BundlePath $Path
}

function ConvertTo-NormalizedEvidencePathText {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return ""
    }

    return (($Path.Trim() -replace "\\", "/") -replace "/+", "/")
}

function ConvertTo-ComparableEvidencePath {
    param(
        [Parameter(Mandatory = $true)][string]$BundlePath,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $normalized = ConvertTo-NormalizedEvidencePathText $Path
    if ([string]::IsNullOrWhiteSpace($normalized)) {
        return ""
    }

    $artifactsIndex = $normalized.LastIndexOf("/artifacts/", [StringComparison]::OrdinalIgnoreCase)
    if ($artifactsIndex -ge 0) {
        return $normalized.Substring($artifactsIndex + 1).ToLowerInvariant()
    }
    if ($normalized.StartsWith("artifacts/", [StringComparison]::OrdinalIgnoreCase)) {
        return $normalized.ToLowerInvariant()
    }

    try {
        $bundleFull = [System.IO.Path]::GetFullPath($BundlePath)
        $candidateFull = if ([System.IO.Path]::IsPathRooted($Path)) {
            [System.IO.Path]::GetFullPath($Path)
        }
        else {
            [System.IO.Path]::GetFullPath((Join-Path $BundlePath $Path))
        }

        $relative = [System.IO.Path]::GetRelativePath($bundleFull, $candidateFull)
        if (-not $relative.StartsWith("..")) {
            return (ConvertTo-NormalizedEvidencePathText $relative).ToLowerInvariant()
        }
    }
    catch {
        # Fall through to text normalization for malformed or platform-specific paths.
    }

    return $normalized.ToLowerInvariant()
}

function Test-ComparableEvidencePathSame {
    param(
        [Parameter(Mandatory = $true)][string]$BundlePath,
        [Parameter(Mandatory = $true)][string]$ExpectedPath,
        [Parameter(Mandatory = $true)][string]$ActualPath
    )

    $expectedComparable = ConvertTo-ComparableEvidencePath -BundlePath $BundlePath -Path $ExpectedPath
    $actualComparable = ConvertTo-ComparableEvidencePath -BundlePath $BundlePath -Path $ActualPath
    if ([string]::IsNullOrWhiteSpace($expectedComparable) -or [string]::IsNullOrWhiteSpace($actualComparable)) {
        return $null
    }

    return [string]::Equals($expectedComparable, $actualComparable, [StringComparison]::OrdinalIgnoreCase)
}

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-ArtifactIndexFileSha256 {
    param($FileEntry)

    if ($FileEntry -is [string]) {
        return ""
    }

    foreach ($name in @("sha256", "jsonlSha256", "fileSha256")) {
        $value = [string](Get-JsonValue $FileEntry $name)
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            return $value.Trim().ToLowerInvariant()
        }
    }

    return ""
}

function Get-ArtifactIndexFileSummarySha256 {
    param($FileEntry)

    if ($FileEntry -is [string]) {
        return ""
    }

    foreach ($name in @("summarySha256", "summaryHash", "watchSummarySha256")) {
        $value = [string](Get-JsonValue $FileEntry $name)
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            return $value.Trim().ToLowerInvariant()
        }
    }

    return ""
}

function Test-JsonArtifact {
    param(
        [string]$Kind,
        [string]$Path
    )

    $extension = [System.IO.Path]::GetExtension($Path)
    if (Test-JsonlArtifact $Kind $Path) {
        return $false
    }

    return ($Kind -match "json" -or $extension -ieq ".json")
}

function Test-JsonlArtifact {
    param(
        [string]$Kind,
        [string]$Path
    )

    $extension = [System.IO.Path]::GetExtension($Path)
    return ($Kind -match "jsonl" -or $extension -ieq ".jsonl")
}

function Test-JsonlFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    $lineNumber = 0
    $recordCount = 0
    foreach ($line in (Get-Content -LiteralPath $Path)) {
        $lineNumber++
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        try {
            $null = $line | ConvertFrom-Json
            $recordCount++
        }
        catch {
            throw "Failed to parse JSONL file '$Path' at line ${lineNumber}: $($_.Exception.Message)"
        }
    }

    return $recordCount
}

function Test-ArtifactIndex {
    param(
        [string]$Kind,
        [string]$Path
    )

    $extension = [System.IO.Path]::GetExtension($Path)
    return (($Kind -match "audio|spectrum|trace") -and $extension -ieq ".json")
}

function Get-ArtifactIndexFilePath {
    param($FileEntry)

    if ($FileEntry -is [string]) {
        return [string]$FileEntry
    }

    return [string](Get-JsonValue $FileEntry "path")
}

function Get-ArtifactIndexFileSummaryPath {
    param($FileEntry)

    if ($FileEntry -is [string]) {
        return ""
    }

    foreach ($name in @("summaryPath", "reportPath", "watchSummaryPath", "diagnosticsSummaryPath")) {
        $summaryPath = [string](Get-JsonValue $FileEntry $name)
        if (-not [string]::IsNullOrWhiteSpace($summaryPath)) {
            return $summaryPath
        }
    }

    return ""
}

function Get-LiveTraceComparisonIndexRoot {
    param([Parameter(Mandatory = $true)][string]$ResolvedIndexPath)

    $indexDir = Split-Path -Parent $ResolvedIndexPath
    if ([string]::Equals((Split-Path -Leaf $indexDir), "artifacts", [StringComparison]::OrdinalIgnoreCase)) {
        return (Resolve-Path -LiteralPath (Join-Path $indexDir "..")).Path
    }

    return $indexDir
}

function Resolve-LiveTraceComparisonIndexEntryPath {
    param(
        [Parameter(Mandatory = $true)][string]$IndexRoot,
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return ""
    }
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return Join-Path $IndexRoot $Path
}

function Get-LiveTraceComparisonIndexEntryComparisonIds {
    param($FileEntry)

    if ($FileEntry -is [string]) {
        return @()
    }

    $comparisonIds = New-Object System.Collections.Generic.List[string]
    foreach ($name in @("comparisonId", "comparison", "candidate", "candidateId")) {
        $comparison = ConvertTo-ComparisonId ([string](Get-JsonValue $FileEntry $name))
        if (-not [string]::IsNullOrWhiteSpace($comparison)) {
            $comparisonIds.Add($comparison) | Out-Null
        }
    }

    foreach ($value in (Get-JsonArray $FileEntry "comparisonIds")) {
        $comparison = ConvertTo-ComparisonId ([string]$value)
        if (-not [string]::IsNullOrWhiteSpace($comparison)) {
            $comparisonIds.Add($comparison) | Out-Null
        }
    }

    $unique = @($comparisonIds.ToArray() | Select-Object -Unique)
    if ($unique.Count -eq 0) {
        return @("")
    }

    return $unique
}

function Get-WatcherSummaryTracePaths {
    param($Summary)

    $paths = New-Object System.Collections.Generic.List[string]
    foreach ($name in @("jsonlPath", "tracePath")) {
        $path = [string](Get-JsonValue $Summary $name)
        if (-not [string]::IsNullOrWhiteSpace($path)) {
            $paths.Add($path) | Out-Null
        }
    }

    return @($paths.ToArray() | Select-Object -Unique)
}

function Add-AbsolutePathIfPresent {
    param(
        [System.Collections.Generic.List[object]]$Target,
        [string]$Name,
        $Value
    )

    $path = [string]$Value
    if ([string]::IsNullOrWhiteSpace($path)) {
        return
    }

    if ([System.IO.Path]::IsPathRooted($path)) {
        $Target.Add([ordered]@{
            name = $Name
            path = $path
        }) | Out-Null
    }
}

function Get-LiveTraceComparisonAbsolutePaths {
    param($Report)

    $absolutePaths = New-Object System.Collections.Generic.List[object]
    foreach ($name in @("baselineIndexPath", "baselineRootPath", "candidateIndexPath", "candidateRootPath", "baselinePath", "candidatePath", "reportPath", "markdownPath", "outputDir")) {
        Add-AbsolutePathIfPresent $absolutePaths $name (Get-JsonValue $Report $name)
    }

    foreach ($collectionName in @("scenarioComparisons", "metricRegressionDetails", "hardConstraintRegressionDetails", "gateFailureDetails", "missingMetricDetails")) {
        foreach ($item in (Get-JsonArray $Report $collectionName)) {
            foreach ($name in @("baselineInputPath", "candidateInputPath", "reportPath")) {
                Add-AbsolutePathIfPresent $absolutePaths "$collectionName.$name" (Get-JsonValue $item $name)
            }
        }
    }

    return @($absolutePaths.ToArray())
}

function Get-LiveDiagnosticsHistoryAbsolutePaths {
    param($Report)

    $absolutePaths = New-Object System.Collections.Generic.List[object]
    foreach ($name in @("scanRoot", "reportPath", "markdownPath")) {
        Add-AbsolutePathIfPresent $absolutePaths $name (Get-JsonValue $Report $name)
    }

    foreach ($collectionName in @("traces")) {
        foreach ($item in (Get-JsonArray $Report $collectionName)) {
            foreach ($name in @("path", "jsonlPath")) {
                Add-AbsolutePathIfPresent $absolutePaths "$collectionName.$name" (Get-JsonValue $item $name)
            }
        }
    }

    foreach ($name in @("latestTrace", "previousNr5Trace", "bestBalancedTrace", "bestWeakSignalTrace", "lowestPumpingTrace")) {
        $trace = Get-JsonValue $Report $name
        Add-AbsolutePathIfPresent $absolutePaths "$name.path" (Get-JsonValue $trace "path")
    }

    return @($absolutePaths.ToArray())
}

function Get-PathExampleText {
    param($Items)

    $examples = @($Items | Select-Object -First 3 | ForEach-Object { "$($_.name)=$($_.path)" })
    if ($examples.Count -eq 0) {
        return ""
    }

    return $examples -join "; "
}

function Get-ArtifactIndexFileScenarioIds {
    param($FileEntry)

    if ($FileEntry -is [string]) {
        return @()
    }

    $scenarioIds = New-Object System.Collections.Generic.List[string]
    $scenarioId = [string](Get-JsonValue $FileEntry "scenarioId")
    if (-not [string]::IsNullOrWhiteSpace($scenarioId)) {
        $scenarioIds.Add($scenarioId) | Out-Null
    }

    foreach ($value in (Get-JsonArray $FileEntry "scenarioIds")) {
        $scenario = [string]$value
        if (-not [string]::IsNullOrWhiteSpace($scenario)) {
            $scenarioIds.Add($scenario) | Out-Null
        }
    }

    return @($scenarioIds.ToArray() | Select-Object -Unique)
}

function ConvertTo-ComparisonId {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return ""
    }

    $normalized = $Value.Trim().ToLowerInvariant() -replace "[^a-z0-9]+", "-"
    $normalized = $normalized.Trim("-")

    switch ($normalized) {
        "off" { return "off-baseline" }
        "baseline" { return "off-baseline" }
        "off-baseline" { return "off-baseline" }
        "thetis" { return "thetis-parity" }
        "thetis-parity" { return "thetis-parity" }
        "current" { return "current-zeus" }
        "current-zeus" { return "current-zeus" }
        "zeus-current" { return "current-zeus" }
        "zeus" { return "current-zeus" }
        "nr5" { return "nr5-spnr" }
        "spnr" { return "nr5-spnr" }
        "nr5-spnr" { return "nr5-spnr" }
        "candidate" { return "candidate-under-test" }
        "candidate-under-test" { return "candidate-under-test" }
        "external" { return "candidate-external-engine-opt-in" }
        "external-engine" { return "candidate-external-engine-opt-in" }
        "candidate-external-engine-opt-in" { return "candidate-external-engine-opt-in" }
        default { return $normalized }
    }
}

function Get-FixtureEvidenceEngine {
    param($ArtifactJson)

    $engine = [string](Get-JsonValue $ArtifactJson "evidenceEngine")
    if (-not [string]::IsNullOrWhiteSpace($engine)) {
        return (($engine.Trim().ToLowerInvariant() -replace "[^a-z0-9]+", "-").Trim("-"))
    }

    $tool = [string](Get-JsonValue $ArtifactJson "tool")
    switch ($tool) {
        "dsp-fixture-evidence" { return "wdsp" }
        "run-dsp-wdsp-fixture-evidence" { return "wdsp" }
        "run-dsp-offline-fixture-evidence" { return "deterministic-synthetic" }
        default { return "unknown" }
    }
}

function Get-ArtifactIndexFileCoverageComparisonIds {
    param($FileEntry)

    if ($FileEntry -is [string]) {
        return @()
    }

    $comparisonIds = New-Object System.Collections.Generic.List[string]
    foreach ($name in @("comparison", "comparisonId", "candidate", "candidateId", "mode", "backend")) {
        $comparison = ConvertTo-ComparisonId ([string](Get-JsonValue $FileEntry $name))
        if (-not [string]::IsNullOrWhiteSpace($comparison)) {
            $comparisonIds.Add($comparison) | Out-Null
        }
    }

    foreach ($name in @("comparisons", "comparisonIds", "candidates", "candidateIds")) {
        foreach ($value in (Get-JsonArray $FileEntry $name)) {
            $comparison = ConvertTo-ComparisonId ([string]$value)
            if (-not [string]::IsNullOrWhiteSpace($comparison)) {
                $comparisonIds.Add($comparison) | Out-Null
            }
        }
    }

    return @($comparisonIds.ToArray() | Select-Object -Unique)
}

function Get-ArtifactIndexFileExplicitComparisonIds {
    param($FileEntry)

    if ($FileEntry -is [string]) {
        return @()
    }

    $comparisonIds = New-Object System.Collections.Generic.List[string]
    foreach ($name in @("comparison", "comparisonId")) {
        $comparison = ConvertTo-ComparisonId ([string](Get-JsonValue $FileEntry $name))
        if (-not [string]::IsNullOrWhiteSpace($comparison)) {
            $comparisonIds.Add($comparison) | Out-Null
        }
    }

    foreach ($name in @("comparisons", "comparisonIds")) {
        foreach ($value in (Get-JsonArray $FileEntry $name)) {
            $comparison = ConvertTo-ComparisonId ([string]$value)
            if (-not [string]::IsNullOrWhiteSpace($comparison)) {
                $comparisonIds.Add($comparison) | Out-Null
            }
        }
    }

    return @($comparisonIds.ToArray() | Select-Object -Unique)
}

function Get-ArtifactIndexFileComparisonIds {
    param($FileEntry)

    $unique = @(Get-ArtifactIndexFileCoverageComparisonIds $FileEntry)
    $derived = New-Object System.Collections.Generic.List[string]
    foreach ($comparison in $unique) {
        $derived.Add($comparison) | Out-Null
        if ($comparison -ne "off-baseline" -and $comparison -ne "thetis-parity" -and $comparison -ne "current-zeus") {
            $derived.Add("candidate-under-test") | Out-Null
        }

        if ($comparison -eq "rnnoise" -or $comparison -eq "deepfilternet" -or $comparison -eq "speexdsp" -or $comparison -eq "webrtc-apm") {
            $derived.Add("candidate-external-engine-opt-in") | Out-Null
        }
    }

    return @($derived.ToArray() | Select-Object -Unique)
}

function Add-ReportComparisonIds {
    param(
        [System.Collections.Generic.List[string]]$Target,
        $Value,
        [int]$Depth = 0
    )

    if ($null -eq $Value -or $Value -is [string]) {
        return
    }

    foreach ($name in @("comparison", "comparisonId", "candidate", "candidateId", "mode", "backend", "baselineComparisonId", "candidateComparisonId")) {
        $comparison = ConvertTo-ComparisonId ([string](Get-JsonValue $Value $name))
        if (-not [string]::IsNullOrWhiteSpace($comparison)) {
            $Target.Add($comparison) | Out-Null
        }
    }

    foreach ($name in @("comparisons", "comparisonIds", "candidates", "candidateIds")) {
        foreach ($item in (Get-JsonArray $Value $name)) {
            $comparison = ConvertTo-ComparisonId ([string]$item)
            if (-not [string]::IsNullOrWhiteSpace($comparison)) {
                $Target.Add($comparison) | Out-Null
            }
        }
    }

    if ($Depth -ge 3) {
        return
    }

    foreach ($collectionName in @("comparisons", "scenarioComparisons", "metricComparisons", "metricRegressionDetails", "hardConstraintRegressionDetails", "gateFailureDetails", "missingMetricDetails", "missingScenarioPairs", "results", "entries", "candidates")) {
        foreach ($item in (Get-JsonArray $Value $collectionName)) {
            Add-ReportComparisonIds -Target $Target -Value $item -Depth ($Depth + 1)
        }
    }
}

function Get-ComparisonIdsFromReport {
    param($Report)

    $comparisonIds = New-Object System.Collections.Generic.List[string]
    Add-ReportComparisonIds -Target $comparisonIds -Value $Report
    $expanded = New-Object System.Collections.Generic.List[string]
    foreach ($comparison in @($comparisonIds.ToArray() | Select-Object -Unique)) {
        if ([string]::IsNullOrWhiteSpace($comparison)) {
            continue
        }

        $expanded.Add($comparison) | Out-Null
        if ($comparison -eq "rnnoise" -or $comparison -eq "deepfilternet" -or $comparison -eq "speexdsp" -or $comparison -eq "webrtc-apm") {
            $expanded.Add("candidate-external-engine-opt-in") | Out-Null
        }
    }

    return @($expanded.ToArray() | Select-Object -Unique)
}

function ConvertTo-MetricId {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return ""
    }

    return ($Value.Trim().ToLowerInvariant() -replace "[^a-z0-9]+", "")
}

function ConvertTo-CandidateId {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return ""
    }

    return ($Value.Trim().ToLowerInvariant() -replace "[^a-z0-9]+", "-").Trim("-")
}

function Test-TextHas {
    param(
        [string]$Text,
        [string[]]$Tokens
    )

    foreach ($token in @($Tokens)) {
        if ($Text -notlike "*$token*") {
            return $false
        }
    }

    return $true
}

function Get-ExternalCandidateRiskTier {
    param([string]$Text)

    $value = $Text.ToLowerInvariant()
    if ($value -like "*high*") { return "high" }
    if ($value -like "*medium*") { return "medium" }
    if ($value -like "*low*") { return "low" }
    if ([string]::IsNullOrWhiteSpace($Text)) { return "missing" }
    return "unknown"
}

function Get-ExternalCandidateRiskScore {
    param([string]$Tier)

    switch ($Tier) {
        "high" { return 3 }
        "medium" { return 2 }
        "low" { return 1 }
        default { return 2 }
    }
}

function Get-ExternalBakeoffSummaryCounts {
    param(
        $Report,
        [object[]]$CandidateSummaries,
        [string[]]$FallbackRequiredCandidateIds
    )

    $candidateIds = @{}
    foreach ($summary in @($CandidateSummaries)) {
        $id = ConvertTo-CandidateId ([string](Get-JsonValue $summary "id"))
        if (-not [string]::IsNullOrWhiteSpace($id)) {
            $candidateIds[$id] = $true
        }
    }

    $requiredIds = New-Object System.Collections.Generic.List[string]
    foreach ($value in (Get-JsonArray $Report "requiredCandidateIds")) {
        $id = ConvertTo-CandidateId ([string]$value)
        if (-not [string]::IsNullOrWhiteSpace($id)) {
            $requiredIds.Add($id) | Out-Null
        }
    }
    if ($requiredIds.Count -eq 0) {
        foreach ($id in @($FallbackRequiredCandidateIds)) {
            $candidateId = ConvertTo-CandidateId ([string]$id)
            if (-not [string]::IsNullOrWhiteSpace($candidateId)) {
                $requiredIds.Add($candidateId) | Out-Null
            }
        }
    }
    $requiredIds = @($requiredIds.ToArray() | Select-Object -Unique)

    $missingIds = New-Object System.Collections.Generic.List[string]
    foreach ($requiredId in @($requiredIds)) {
        if (-not $candidateIds.ContainsKey($requiredId)) {
            $missingIds.Add($requiredId) | Out-Null
        }
    }

    $safeCount = 0
    $blockedCount = 0
    $integrationReadyCount = 0
    $unsafeCount = 0
    $snapshotMismatchCount = 0
    foreach ($summary in @($CandidateSummaries)) {
        $safe = Test-Truthy (Get-JsonValue $summary "safeForBakeoff")
        $blocked = Test-Truthy (Get-JsonValue $summary "integrationBlocked")
        $inSnapshot = Test-Truthy (Get-JsonValue $summary "inSnapshot")

        if ($safe) {
            $safeCount++
            if (-not $blocked) {
                $integrationReadyCount++
            }
        }
        else {
            $unsafeCount++
        }

        if ($blocked) {
            $blockedCount++
        }
        if (-not $inSnapshot) {
            $snapshotMismatchCount++
        }
    }

    $readyForReview = ($CandidateSummaries.Count -gt 0 -and
        $missingIds.Count -eq 0 -and
        $unsafeCount -eq 0 -and
        $snapshotMismatchCount -eq 0)

    return [ordered]@{
        candidateCount = $CandidateSummaries.Count
        safeForBakeoffCount = $safeCount
        blockedCandidateCount = $blockedCount
        integrationReadyCandidateCount = $integrationReadyCount
        missingCandidateCount = $missingIds.Count
        missingCandidateIds = @($missingIds.ToArray() | Sort-Object)
        unsafeCandidateCount = $unsafeCount
        snapshotMismatchCount = $snapshotMismatchCount
        readyForReview = $readyForReview
    }
}

function Get-ExternalEngineCandidateEntries {
    param($Value)

    if ($null -eq $Value) {
        return @()
    }

    if ($Value -is [System.Array]) {
        return @($Value)
    }

    foreach ($name in @("externalEngineCandidates", "candidates", "items", "engines")) {
        $items = @(Get-JsonArray $Value $name)
        if ($items.Count -gt 0) {
            return @($items)
        }
    }

    $id = ConvertTo-CandidateId ([string](Get-JsonValue $Value "id"))
    if (-not [string]::IsNullOrWhiteSpace($id)) {
        return @($Value)
    }

    return @()
}

function Get-ExternalEngineCandidateIds {
    param($Value)

    $ids = New-Object System.Collections.Generic.List[string]
    foreach ($candidate in @(Get-ExternalEngineCandidateEntries $Value)) {
        $id = ConvertTo-CandidateId ([string](Get-JsonValue $candidate "id"))
        if (-not [string]::IsNullOrWhiteSpace($id)) {
            $ids.Add($id) | Out-Null
        }
    }

    return @($ids.ToArray() | Sort-Object -Unique)
}

function Get-ComparableStringArray {
    param($Values)

    if ($null -eq $Values) {
        return @()
    }

    $items = New-Object System.Collections.Generic.List[string]
    foreach ($value in @($Values)) {
        $text = [string]$value
        if (-not [string]::IsNullOrWhiteSpace($text)) {
            $items.Add($text.Trim()) | Out-Null
        }
    }

    return @($items.ToArray() | Sort-Object -Unique)
}

function Test-ComparableStringArraysEqual {
    param(
        $Actual,
        $Expected
    )

    $actualItems = @(Get-ComparableStringArray $Actual)
    $expectedItems = @(Get-ComparableStringArray $Expected)
    return (($actualItems -join "`n") -eq ($expectedItems -join "`n"))
}

function Test-OrderedStringArraysEqual {
    param(
        $Actual,
        $Expected
    )

    $actualItems = @($Actual | ForEach-Object { [string]$_ })
    $expectedItems = @($Expected | ForEach-Object { [string]$_ })
    if ($actualItems.Count -ne $expectedItems.Count) {
        return $false
    }

    for ($index = 0; $index -lt $actualItems.Count; $index++) {
        if (-not [string]::Equals($actualItems[$index], $expectedItems[$index], [StringComparison]::Ordinal)) {
            return $false
        }
    }

    return $true
}

function Get-ExternalBakeoffRequiredComparisons {
    return @("current-zeus", "nr5-spnr", "candidate-external-engine-opt-in")
}

function Get-ExternalBakeoffRequiredControls {
    return @("off-by-default", "post-demod-only", "clean-bypass-fallback", "operator-visible-opt-in")
}

function Get-ExternalBakeoffPackageAndRuntimeGates {
    return @("license-review-complete", "runtime-package-reviewed", "cpu-latency-measured-on-g2", "allocation-budget-measured")
}

function Get-ExternalBakeoffRadioSafetyGates {
    return @("no-raw-wdsp-iq-replacement", "no-default-behavior-change", "no-tx-or-puresignal-coupling", "cross-radio-validation-before-graduation")
}

function Get-ExternalBakeoffExpectedScenariosForCandidateId {
    param([string]$CandidateId)

    switch (ConvertTo-CandidateId $CandidateId) {
        "rnnoise" {
            return @(
                [ordered]@{
                    scenarioId = "ssb-like-speech-post-demod"
                    purpose = "Measure speech-denoise benefit after WDSP demodulation."
                    acceptanceGates = @("Improves speech artifact score without weak-signal loss.", "No added clipping or audio RMS pumping.", "Latency remains inside the post-demod budget.")
                },
                [ordered]@{
                    scenarioId = "weak-cw-carrier-bypass"
                    purpose = "Prove weak CW/carrier and non-speech HF content bypass safely."
                    acceptanceGates = @("Weak-carrier SNR/SINAD does not regress.", "Bypass/fallback is clean and measurable.", "No speech model artifacts are introduced on CW.")
                },
                [ordered]@{
                    scenarioId = "noise-only-gating"
                    purpose = "Verify the speech denoiser does not create false-open noise artifacts."
                    acceptanceGates = @("Noise-only output stays bounded.", "No VAD chatter or burbling.", "Fallback remains bit-clean when disabled.")
                }
            )
        }
        "deepfilternet" {
            return @(
                [ordered]@{
                    scenarioId = "ssb-like-speech-post-demod"
                    purpose = "Measure full-band neural enhancement on demodulated speech."
                    acceptanceGates = @("Improves speech artifact score beyond NR5/SPNR.", "No weak-signal intelligibility loss.", "No musical-noise or burbling artifacts.")
                },
                [ordered]@{
                    scenarioId = "weak-ssb-speech-latency"
                    purpose = "Bound CPU and latency on weak speech."
                    acceptanceGates = @("Latency and allocation budgets are measured.", "No realtime underrun or monitor backlog.", "Weak speech remains intelligible.")
                },
                [ordered]@{
                    scenarioId = "weak-cw-carrier-bypass"
                    purpose = "Prove the model is bypassed for non-speech HF content."
                    acceptanceGates = @("Carrier preservation is neutral or better.", "Bypass selection is deterministic.", "No raw IQ path replacement is used.")
                }
            )
        }
        "speexdsp" {
            return @(
                [ordered]@{
                    scenarioId = "ssb-like-speech-post-demod"
                    purpose = "Evaluate classic post-demod noise suppression as a lightweight baseline."
                    acceptanceGates = @("Denoise improves without pumping.", "WDSP AGC remains the only active AGC.", "No clipping or RMS movement regression.")
                },
                [ordered]@{
                    scenarioId = "agc-disabled-no-pumping"
                    purpose = "Prove Speex AGC/VAD behavior cannot fight WDSP AGC."
                    acceptanceGates = @("Speex AGC remains disabled.", "No VAD gate chatter.", "No NR5/SPNR level-stability regression.")
                },
                [ordered]@{
                    scenarioId = "noise-only-gating"
                    purpose = "Check denoise behavior on no-signal receive audio."
                    acceptanceGates = @("Noise floor does not false-open.", "No audible artifacts in operator notes.", "Fallback path is clean.")
                }
            )
        }
        "webrtc-apm" {
            return @(
                [ordered]@{
                    scenarioId = "ns-vad-only-post-demod"
                    purpose = "Evaluate WebRTC noise suppression/VAD with radio-unsafe modules disabled."
                    acceptanceGates = @("AEC remains disabled.", "AGC remains disabled.", "High-pass/default voice-chain behavior remains disabled unless separately approved.")
                },
                [ordered]@{
                    scenarioId = "ssb-like-speech-post-demod"
                    purpose = "Measure speech enhancement without corrupting radio gain staging."
                    acceptanceGates = @("No WDSP AGC interaction.", "No clipping or RMS movement regression.", "Speech artifact score improves.")
                },
                [ordered]@{
                    scenarioId = "weak-cw-carrier-bypass"
                    purpose = "Verify non-speech HF content bypasses WebRTC speech processing safely."
                    acceptanceGates = @("Weak carrier preservation is neutral or better.", "Bypass/fallback is deterministic.", "No raw IQ path replacement is used.")
                }
            )
        }
        default {
            return @(
                [ordered]@{
                    scenarioId = "ssb-like-speech-post-demod"
                    purpose = "Evaluate opt-in post-demod speech/audio behavior."
                    acceptanceGates = @("No weak-signal loss.", "No pumping or clipping regression.", "Fallback remains clean.")
                },
                [ordered]@{
                    scenarioId = "noise-only-gating"
                    purpose = "Check noise-only artifacts and fallback."
                    acceptanceGates = @("No false-open artifacts.", "No realtime budget regression.", "Candidate stays off by default.")
                }
            )
        }
    }
}

function New-ExternalBakeoffCandidateSummary {
    param(
        $Candidate,
        [string[]]$SnapshotCandidateIds = @()
    )

    $id = ConvertTo-CandidateId ([string](Get-JsonValue $Candidate "id"))
    $name = [string](Get-JsonValue $Candidate "name")
    $integrationPoint = [string](Get-JsonValue $Candidate "integrationPoint")
    $defaultState = [string](Get-JsonValue $Candidate "defaultState")
    $rolloutPolicy = [string](Get-JsonValue $Candidate "rolloutPolicy")
    $license = [string](Get-JsonValue $Candidate "license")
    $packagingStatus = [string](Get-JsonValue $Candidate "packagingStatus")
    $runtimeRisk = [string](Get-JsonValue $Candidate "runtimeRisk")
    $latencyRisk = [string](Get-JsonValue $Candidate "latencyRisk")
    $radioSafetyRisk = [string](Get-JsonValue $Candidate "radioSafetyRisk")
    $benchmarks = @(Get-JsonArray $Candidate "requiredBenchmarks")
    $evidence = @(Get-JsonArray $Candidate "requiredEvidence")
    $blockers = @(Get-JsonArray $Candidate "blockers")
    $references = @(Get-JsonArray $Candidate "referenceUrls")
    $evidenceText = "$($evidence -join ' ') $radioSafetyRisk $integrationPoint"

    $issues = New-Object System.Collections.Generic.List[string]
    if ([string]::IsNullOrWhiteSpace($id)) { $issues.Add("id-missing") | Out-Null }
    if ($defaultState -ine "off") { $issues.Add("default-not-off") | Out-Null }
    if ($rolloutPolicy -notlike "*opt-in*" -or $rolloutPolicy -notlike "*bakeoff*") { $issues.Add("rollout-not-opt-in-bakeoff") | Out-Null }
    if ($integrationPoint -notlike "*post-demod*") { $issues.Add("integration-not-post-demod") | Out-Null }
    if ([string]::IsNullOrWhiteSpace($license)) { $issues.Add("license-missing") | Out-Null }
    if ([string]::IsNullOrWhiteSpace($packagingStatus)) { $issues.Add("packaging-status-missing") | Out-Null }
    if ([string]::IsNullOrWhiteSpace($runtimeRisk)) { $issues.Add("runtime-risk-missing") | Out-Null }
    if ([string]::IsNullOrWhiteSpace($latencyRisk)) { $issues.Add("latency-risk-missing") | Out-Null }
    if ([string]::IsNullOrWhiteSpace($radioSafetyRisk)) { $issues.Add("radio-safety-risk-missing") | Out-Null }
    if ($benchmarks.Count -lt 3) { $issues.Add("benchmark-coverage-incomplete") | Out-Null }
    if ($evidence.Count -lt 3) { $issues.Add("evidence-coverage-incomplete") | Out-Null }
    if ($blockers.Count -le 0) { $issues.Add("blockers-missing") | Out-Null }
    if ($references.Count -le 0) { $issues.Add("references-missing") | Out-Null }

    $inSnapshot = ($SnapshotCandidateIds.Count -eq 0 -or $SnapshotCandidateIds -contains $id)
    if (-not $inSnapshot) { $issues.Add("snapshot-missing") | Out-Null }

    if ($id -eq "webrtc-apm" -and -not (Test-TextHas $evidenceText @("AEC", "AGC", "disabled"))) {
        $issues.Add("webrtc-aec-agc-disabled-gate-missing") | Out-Null
    }
    if ($id -eq "speexdsp" -and -not (Test-TextHas $evidenceText @("pumping"))) {
        $issues.Add("speex-no-pumping-gate-missing") | Out-Null
    }
    if (($id -eq "rnnoise" -or $id -eq "deepfilternet") -and
        -not ($evidenceText -like "*weak*" -and ($evidenceText -like "*CW*" -or $evidenceText -like "*carrier*" -or $evidenceText -like "*bypass*"))) {
        $issues.Add("neural-weak-signal-bypass-gate-missing") | Out-Null
    }

    $runtimeTier = Get-ExternalCandidateRiskTier $runtimeRisk
    $latencyTier = Get-ExternalCandidateRiskTier $latencyRisk
    $radioTier = Get-ExternalCandidateRiskTier $radioSafetyRisk
    $combinedRiskScore = (Get-ExternalCandidateRiskScore $runtimeTier) + (Get-ExternalCandidateRiskScore $latencyTier) + (Get-ExternalCandidateRiskScore $radioTier)
    $safeForBakeoff = ($issues.Count -eq 0)
    $integrationBlocked = ($blockers.Count -gt 0)

    return [ordered]@{
        id = $id
        name = $name
        family = [string](Get-JsonValue $Candidate "family")
        inSnapshot = $inSnapshot
        defaultState = $defaultState
        rolloutPolicy = $rolloutPolicy
        integrationPoint = $integrationPoint
        license = $license
        packagingStatus = $packagingStatus
        runtimeRisk = $runtimeRisk
        latencyRisk = $latencyRisk
        radioSafetyRisk = $radioSafetyRisk
        runtimeRiskTier = $runtimeTier
        latencyRiskTier = $latencyTier
        radioSafetyRiskTier = $radioTier
        combinedRiskScore = $combinedRiskScore
        requiredBenchmarkCount = $benchmarks.Count
        requiredEvidenceCount = $evidence.Count
        blockerCount = $blockers.Count
        referenceUrlCount = $references.Count
        safeForBakeoff = $safeForBakeoff
        integrationBlocked = $integrationBlocked
        integrationStatus = if ($safeForBakeoff -and $integrationBlocked) { "opt-in-bakeoff-only-blocked-for-integration" } elseif ($safeForBakeoff) { "needs-explicit-approval-before-integration" } else { "unsafe-catalog-entry" }
        issues = @($issues.ToArray())
        requiredBenchmarks = @($benchmarks)
        requiredEvidence = @($evidence)
        blockers = @($blockers)
        referenceUrls = @($references)
    }
}

function Get-MetricIdsFromValue {
    param($Value)

    $metricIds = New-Object System.Collections.Generic.List[string]
    if ($null -eq $Value) {
        return @()
    }

    if ($Value -is [System.Array]) {
        foreach ($item in @($Value)) {
            if ($item -is [string]) {
                $metric = ConvertTo-MetricId $item
                if (-not [string]::IsNullOrWhiteSpace($metric)) {
                    $metricIds.Add($metric) | Out-Null
                }
            }
            else {
                foreach ($name in @("id", "name", "metric", "metricId", "key")) {
                    $metric = ConvertTo-MetricId ([string](Get-JsonValue $item $name))
                    if (-not [string]::IsNullOrWhiteSpace($metric)) {
                        $metricIds.Add($metric) | Out-Null
                    }
                }
            }
        }
    }
    elseif ($Value -is [string]) {
        $metric = ConvertTo-MetricId $Value
        if (-not [string]::IsNullOrWhiteSpace($metric)) {
            $metricIds.Add($metric) | Out-Null
        }
    }
    else {
        foreach ($property in @($Value.PSObject.Properties)) {
            $metric = ConvertTo-MetricId $property.Name
            if (-not [string]::IsNullOrWhiteSpace($metric)) {
                $metricIds.Add($metric) | Out-Null
            }
        }
    }

    return @($metricIds.ToArray() | Select-Object -Unique)
}

function Get-MetricResultMetricIds {
    param($Entry)

    $metricIds = New-Object System.Collections.Generic.List[string]
    foreach ($name in @("metrics", "metricResults", "measurements", "values")) {
        foreach ($metric in (Get-MetricIdsFromValue (Get-JsonValue $Entry $name))) {
            $metricIds.Add($metric) | Out-Null
        }
    }

    return @($metricIds.ToArray() | Select-Object -Unique)
}

function Get-GateOutcomeSummary {
    param($Entry)

    $count = 0
    $failed = 0

    foreach ($name in @("gates", "gateResults", "acceptanceGates")) {
        foreach ($gate in (Get-JsonArray $Entry $name)) {
            if ($gate -is [string]) {
                continue
            }

            $passedValue = Get-JsonValue $gate "passed"
            if ($null -eq $passedValue) {
                $passedValue = Get-JsonValue $gate "pass"
            }
            if ($null -eq $passedValue) {
                $passedValue = Get-JsonValue $gate "ok"
            }

            $status = [string](Get-JsonValue $gate "status")
            if ($null -ne $passedValue) {
                $count++
                if (-not (Test-Truthy $passedValue)) {
                    $failed++
                }
            }
            elseif (-not [string]::IsNullOrWhiteSpace($status)) {
                $count++
                $normalizedStatus = $status.Trim().ToLowerInvariant()
                if ($normalizedStatus -ne "pass" -and $normalizedStatus -ne "passed" -and $normalizedStatus -ne "ok") {
                    $failed++
                }
            }
        }
    }

    return [ordered]@{
        count = $count
        failed = $failed
    }
}

function Add-MetricEvidenceEntry {
    param(
        [System.Collections.Generic.List[object]]$Target,
        $Entry,
        [string[]]$InheritedScenarioIds = @(),
        [string[]]$InheritedComparisonIds = @()
    )

    $metricIds = @(Get-MetricResultMetricIds $Entry)
    if ($metricIds.Count -eq 0) {
        return
    }

    $scenarioIds = @(Get-ArtifactIndexFileScenarioIds $Entry)
    if ($scenarioIds.Count -eq 0) {
        $scenarioIds = @($InheritedScenarioIds)
    }

    $comparisonIds = @(Get-ArtifactIndexFileComparisonIds $Entry)
    if ($comparisonIds.Count -eq 0) {
        $comparisonIds = @($InheritedComparisonIds)
    }

    $gateSummary = Get-GateOutcomeSummary $Entry
    $Target.Add([ordered]@{
        scenarioIds = @($scenarioIds)
        comparisonIds = @($comparisonIds)
        metricIds = @($metricIds)
        gateOutcomeCount = [int]$gateSummary.count
        failedGateCount = [int]$gateSummary.failed
    }) | Out-Null
}

function Get-MetricEvidenceEntries {
    param($MetricsJson)

    $entries = New-Object System.Collections.Generic.List[object]
    Add-MetricEvidenceEntry $entries $MetricsJson

    foreach ($name in @("results", "entries", "comparisons", "candidates")) {
        foreach ($entry in (Get-JsonArray $MetricsJson $name)) {
            Add-MetricEvidenceEntry $entries $entry
        }
    }

    foreach ($scenario in (Get-JsonArray $MetricsJson "scenarios")) {
        $scenarioIds = @(Get-ArtifactIndexFileScenarioIds $scenario)
        Add-MetricEvidenceEntry $entries $scenario -InheritedScenarioIds $scenarioIds

        foreach ($name in @("results", "entries", "comparisons", "candidates")) {
            foreach ($entry in (Get-JsonArray $scenario $name)) {
                Add-MetricEvidenceEntry $entries $entry -InheritedScenarioIds $scenarioIds
            }
        }
    }

    return @($entries.ToArray())
}

function Write-JsonFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Value
    )

    $json = $Value | ConvertTo-Json -Depth 32
    Set-Content -LiteralPath $Path -Value $json -Encoding UTF8
}

$bundlePath = (Resolve-Path -LiteralPath $BundleDir).Path
$indexPath = Join-Path $bundlePath "bundle-index.json"
if (-not (Test-Path -LiteralPath $indexPath -PathType Leaf)) {
    throw "DSP modernization bundle is missing bundle-index.json: $bundlePath"
}

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path $bundlePath "validation-report.json"
}

$index = Read-JsonFile $indexPath
$errors = New-Object System.Collections.Generic.List[object]
$warnings = New-Object System.Collections.Generic.List[object]
$artifactFiles = New-Object System.Collections.Generic.List[object]
$artifactReferencedFiles = New-Object System.Collections.Generic.List[object]
$artifactScenarioCoverage = New-Object System.Collections.Generic.List[object]
$artifactComparisonCoverage = New-Object System.Collections.Generic.List[object]
$artifactMetricCoverage = New-Object System.Collections.Generic.List[object]
$parsedFiles = @{}
$artifactManifestReportPath = $null
$artifactManifestProvided = $false
$requiredArtifactFileCount = 0
$requiredArtifactReferencedFileCount = 0
$artifactMissingScenarioCount = 0
$artifactMissingComparisonCount = 0
$artifactMissingMetricCount = 0
$artifactGateOutcomeCount = 0
$artifactFailedGateCount = 0
$captureAllArtifactIds = @{}
$captureRequiredPhysicalArtifactIds = @{}
$captureArtifactScenarioIds = @{}
$scenarioRequiredComparisons = @{}
$scenarioRequiredMetrics = @{}
$metricComparisonArtifactId = "fixture-metric-comparison-report"
$offlineFixtureMetricsEvidence = [ordered]@{
    present = $false
    artifactId = ""
    path = ""
    evidenceEngine = ""
    evidenceTool = ""
    wdspBackedEvidence = $false
    syntheticFallbackEvidence = $false
    scenarioCount = 0
    comparisonCount = 0
    sha256 = ""
    wdspRuntimeRid = ""
    wdspRuntimePath = ""
    wdspRuntimePathKind = ""
    wdspRuntimeFileName = ""
    wdspRuntimeLength = 0
    wdspRuntimeSha256 = ""
    wdspRuntimeStatus = ""
    runtimeArtifactHashStatus = "not-evaluated"
    status = "not-evaluated"
}
$metricComparisonEvidence = [ordered]@{
    present = $false
    readyForReview = $false
    reportReadyForReview = $false
    metricCoverageReadyForReview = $false
    evidenceEngine = ""
    evidenceTool = ""
    wdspBackedEvidence = $false
    syntheticFallbackEvidence = $false
    sourceMetricsPath = ""
    sourceMetricsSha256 = ""
    sourceMetricsHashStatus = "not-evaluated"
    wdspRuntimeRid = ""
    wdspRuntimePath = ""
    wdspRuntimePathKind = ""
    wdspRuntimeFileName = ""
    wdspRuntimeLength = 0
    wdspRuntimeSha256 = ""
    wdspRuntimeStatus = ""
    wdspRuntimeIdentityReadyForReview = $false
    runtimeHashStatus = "not-evaluated"
    fixtureScenarioScope = ""
    skippedNonFixtureScenarioCount = 0
    skippedNonFixtureScenarioIds = @()
    regressionCount = 0
    gateFailureCount = 0
    missingScenarioCount = 0
    missingScenarios = @()
    missingCurrentBaselineCount = 0
    missingThetisBaselineCount = 0
    missingCandidateCount = 0
    missingMetricValueCount = 0
    candidateComparisonCount = 0
    status = "not-evaluated"
}
$liveTraceComparisonArtifactId = "live-diagnostics-trace-comparison"
$liveTraceComparisonEvidence = [ordered]@{
    present = $false
    readyForReview = $false
    regressionCount = 0
    hardConstraintRegressionCount = 0
    gateFailureCount = 0
    missingMetricValueCount = 0
    metricRegressionDetailCount = 0
    hardConstraintRegressionDetailCount = 0
    gateFailureDetailCount = 0
    missingMetricDetailCount = 0
    metricRegressionSafetyClassCounts = @()
    metricMissingSafetyClassCounts = @()
    pumpingRegressionCount = 0
    weakSignalRegressionCount = 0
    clippingRegressionCount = 0
    readinessRegressionCount = 0
    hardGateRegressionCount = 0
    freshnessRegressionCount = 0
    frontEndRegressionCount = 0
    audioPathRegressionCount = 0
    toolingRegressionCount = 0
    bundleRelativePaths = $false
    absolutePathCount = 0
    nr5WeakSignalComparisonSummary = $null
    nr5WeakInputSampleDelta = 0
    nr5WeakRecoveredSampleDelta = 0
    nr5WeakDropoutSampleDelta = 0
    nr5HotMakeupSampleDelta = 0
    nr5WeakRecoveryPctDelta = 0.0
    nr5OutputMovementDbDelta = 0.0
    nr5MakeupMovementDbDelta = 0.0
    nr5MakeupMaxDbDelta = 0.0
    nr5RecoveryDriveMovementDelta = 0.0
    nr5TextureFillAverageDelta = 0.0
    nr5OutputMovementRegressionCount = 0
    nr5MakeupMovementRegressionCount = 0
    nr5MakeupMaxRegressionCount = 0
    nr5RecoveryDriveMovementRegressionCount = 0
    status = "not-evaluated"
}
$liveDiagnosticsHistoryArtifactId = "live-diagnostics-history"
$liveDiagnosticsHistoryEvidence = [ordered]@{
    present = $false
    readyForReview = $false
    traceCount = 0
    nr5TraceCount = 0
    readyTraceCount = 0
    readyNr5TraceCount = 0
    candidateReadyNr5TraceCount = 0
    latestTraceId = ""
    latestReviewStatus = ""
    latestSafetyRiskScore = 0
    latestTraceSequenceUtc = ""
    latestTraceSortKeySource = ""
    traceOrderingStatus = ""
    traceOrderingViolationCount = 0
    bestBalancedTraceId = ""
    bestWeakSignalTraceId = ""
    lowestPumpingTraceId = ""
    promotionStatus = ""
    candidatePromotionReady = $false
    promotionRecommendedTraceId = ""
    promotionRecommendedTraceRole = ""
    promotionReferenceTraceId = ""
    promotionBlockerCount = 0
    promotionBlockerClasses = @()
    promotionNextAction = ""
    aggregateSafetyClassCounts = @()
    aggregateReadinessGaps = @()
    readinessGapSignalCount = 0
    readinessGapNumericSignalCount = 0
    readinessGapMax = $null
    readinessTrendStatus = ""
    readinessTrendPreviousTraceId = ""
    readinessTrendSignalCount = 0
    readinessTrendImprovedSignalCount = 0
    readinessTrendRegressedSignalCount = 0
    readinessTrendResolvedSignalCount = 0
    readinessTrendNewSignalCount = 0
    readinessTrendGapMaxDelta = $null
    referenceTrendStatus = ""
    referenceTrendReferenceTraceId = ""
    referenceTrendReferenceTraceRole = ""
    referenceTrendSignalCount = 0
    referenceTrendImprovedSignalCount = 0
    referenceTrendRegressedSignalCount = 0
    referenceTrendResolvedSignalCount = 0
    referenceTrendNewSignalCount = 0
    referenceTrendGapMaxDelta = $null
    tuningActionPlanStatus = ""
    tuningActionPlanDirectionStatus = ""
    tuningActionPlanPrimarySafetyClass = ""
    tuningActionPlanPrimarySignalName = ""
    tuningActionPlanTopControlFamily = ""
    tuningActionPlanActionCount = 0
    liveExperimentPlanStatus = ""
    liveExperimentPlanDirectionStatus = ""
    liveExperimentPlanPrimaryControlFamily = ""
    liveExperimentPlanScenarioCount = 0
    liveExperimentPlanRecommendedSampleCount = 0
    liveExperimentPlanRecommendedIntervalMs = 0
    liveExperimentCoverageStatus = ""
    liveExperimentCoverageScenarioCount = 0
    liveExperimentCoverageCoveredScenarioCount = 0
    liveExperimentCoverageRequiredComparisonCount = 0
    liveExperimentCoverageCoveredComparisonCount = 0
    liveExperimentCoverageMissingComparisonCount = 0
    pumpingSignalCount = 0
    weakSignalCount = 0
    recommendationCount = 0
    traceSourceStatus = "not-evaluated"
    traceSourceCheckedCount = 0
    traceSourceMissingCount = 0
    traceSourceInvalidCount = 0
    traceSourceJsonlMissingCount = 0
    traceSourceSummaryHashPresentCount = 0
    traceSourceSummaryHashMissingCount = 0
    traceSourceSummaryHashMismatchCount = 0
    traceSourceJsonlHashPresentCount = 0
    traceSourceJsonlHashMissingCount = 0
    traceSourceJsonlHashMismatchCount = 0
    bundleRelativePaths = $false
    absolutePathCount = 0
    status = "not-evaluated"
}
$externalEngineBakeoffArtifactId = "external-engine-bakeoff-report"
$externalEngineBakeoffEvidence = [ordered]@{
    present = $false
    readyForReview = $false
    candidateCount = 0
    safeForBakeoffCount = 0
    blockedCandidateCount = 0
    integrationReadyCandidateCount = 0
    missingCandidateCount = 0
    unsafeCandidateCount = 0
    snapshotMismatchCount = 0
    candidateSha256 = ""
    snapshotSha256 = ""
    bakeoffPlanCandidateCount = 0
    bakeoffPlanScenarioCount = 0
    bakeoffPlanDefaultBehaviorChangeReady = $false
    bakeoffPlanRawWdspIqReplacementAllowed = $false
    status = "not-evaluated"
}
$nativeSymbolAuditArtifactId = "wdsp-native-symbol-audit"
$nativeSymbolAuditEvidence = [ordered]@{
    present = $false
    readyForReview = $false
    importedSymbolCount = 0
    sourceMissingRequiredCount = 0
    signatureMismatchCount = 0
    binaryExportsEvaluated = $false
    binaryExportCount = 0
    binaryMissingRequiredCount = 0
    binaryExportStatus = ""
    status = "not-evaluated"
}
$nativeRuntimeArtifactAuditId = "wdsp-runtime-artifact-audit"
$nativeRuntimeArtifactAuditEvidence = [ordered]@{
    present = $false
    readyForWinX64Package = $false
    pendingRidCount = 0
    pendingRids = @()
    artifactCount = 0
    winX64NativePath = ""
    winX64NativeLength = 0
    winX64NativeSha256 = ""
    status = "not-evaluated"
}
$metricCatalogEvidence = [ordered]@{
    present = $false
    metricCount = 0
    requiredMetricCount = 0
    missingRequiredMetricCount = 0
    invalidDirectionCount = 0
    status = "not-evaluated"
}
$requiredExternalCandidateIds = @("rnnoise", "deepfilternet", "speexdsp", "webrtc-apm")
$externalEngineOptInComparisonId = "candidate-external-engine-opt-in"
$externalEngineBakeoffRequiredByScope = $false
$externalEngineCandidateEvidence = [ordered]@{
    present = $false
    snapshotPresent = $false
    candidateCount = 0
    snapshotCandidateCount = 0
    candidateIds = @()
    missingCandidateIds = @()
    unsafeCandidateCount = 0
    snapshotMismatchCount = 0
    status = "not-evaluated"
}

foreach ($endpoint in (Get-JsonArray $index "endpoints")) {
    $id = [string]$endpoint.id
    $file = [string]$endpoint.file
    $required = Test-Truthy $endpoint.required
    $ok = Test-Truthy $endpoint.ok

    if ([string]::IsNullOrWhiteSpace($id)) {
        Add-ValidationIssue $errors "error" "endpoint-id-missing" "Bundle index contains an endpoint entry with no id."
        continue
    }

    if ($required -and -not $ok) {
        Add-ValidationIssue $errors "error" "required-endpoint-failed" "Required endpoint '$id' failed during capture."
    }
    elseif (-not $required -and -not $ok) {
        Add-ValidationIssue $warnings "warning" "optional-endpoint-failed" "Optional endpoint '$id' failed during capture."
    }

    if ([string]::IsNullOrWhiteSpace($file)) {
        if ($required) {
            Add-ValidationIssue $errors "error" "endpoint-file-missing" "Required endpoint '$id' does not declare an output file."
        }
        continue
    }

    $path = Join-Path $bundlePath $file
    if ($ok -and -not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Add-ValidationIssue $errors "error" "captured-file-missing" "Endpoint '$id' is marked ok but file '$file' is missing."
        continue
    }

    if (Test-Path -LiteralPath $path -PathType Leaf) {
        $length = (Get-Item -LiteralPath $path).Length
        if ($length -le 0) {
            Add-ValidationIssue $errors "error" "captured-file-empty" "Captured file '$file' is empty."
            continue
        }

        try {
            $parsedFiles[$id] = Read-JsonFile $path
        }
        catch {
            Add-ValidationIssue $errors "error" "captured-json-invalid" $_.Exception.Message
        }
    }
}

foreach ($failure in (Get-JsonArray $index "requiredFailures")) {
    if (-not [string]::IsNullOrWhiteSpace([string]$failure)) {
        Add-ValidationIssue $errors "error" "bundle-required-failure" "Bundle index reports required endpoint failure: $failure."
    }
}

$snapshot = $parsedFiles["modernization-snapshot"]
$live = $parsedFiles["live-diagnostics"]
$manifest = $parsedFiles["benchmark-capture-manifest"]
$plan = $parsedFiles["benchmark-plan"]
$metricCatalog = $parsedFiles["benchmark-metric-catalog"]
$hardware = $parsedFiles["hardware-diagnostics"]
$externalCandidates = $parsedFiles["external-engine-candidates"]

$hardwareEvidence = [ordered]@{
    planTarget = $null
    manifestTarget = $null
    diagnosticsPresent = $false
    connectionStatus = $null
    connectedBoard = $null
    effectiveBoard = $null
    orionMkIIVariant = $null
    g2Class = $false
    ok = $false
    status = "not-evaluated"
}
$hardwareTargetOk = $true
$hardwareDiagnosticsOk = $false

if ($null -ne $plan) {
    foreach ($scenario in (Get-JsonArray $plan "scenarios")) {
        $scenarioId = [string](Get-JsonValue $scenario "id")
        if (-not [string]::IsNullOrWhiteSpace($scenarioId)) {
            $comparisons = New-Object System.Collections.Generic.List[string]
            foreach ($value in (Get-JsonArray $scenario "requiredComparisons")) {
                $comparison = ConvertTo-ComparisonId ([string]$value)
                if (-not [string]::IsNullOrWhiteSpace($comparison)) {
                    $comparisons.Add($comparison) | Out-Null
                    if ($comparison -eq $externalEngineOptInComparisonId) {
                        $externalEngineBakeoffRequiredByScope = $true
                    }
                }
            }
            $scenarioRequiredComparisons[$scenarioId] = @($comparisons.ToArray() | Select-Object -Unique)

            $metrics = New-Object System.Collections.Generic.List[string]
            foreach ($value in (Get-JsonArray $scenario "requiredMetrics")) {
                $metric = ConvertTo-MetricId ([string]$value)
                if (-not [string]::IsNullOrWhiteSpace($metric)) {
                    $metrics.Add($metric) | Out-Null
                }
            }
            $scenarioRequiredMetrics[$scenarioId] = @($metrics.ToArray() | Select-Object -Unique)
        }
    }
}

if ($null -eq $metricCatalog) {
    $metricCatalogEvidence["status"] = "missing"
    Add-AcceptanceIssue $errors $warnings -AllowPreflight:$AllowPreflight "benchmark-metric-catalog-missing" "benchmark-metric-catalog.json is required so fixture comparison directions are captured with the bundle."
}
else {
    $metricCatalogEvidence["present"] = $true
    $catalogMetricIds = @{}
    $invalidDirectionCount = 0

    foreach ($metric in (Get-JsonArray $metricCatalog "metrics")) {
        $metricId = ConvertTo-MetricId ([string](Get-JsonValue $metric "id"))
        if ([string]::IsNullOrWhiteSpace($metricId)) {
            $metricId = ConvertTo-MetricId ([string](Get-JsonValue $metric "name"))
        }

        if ([string]::IsNullOrWhiteSpace($metricId)) {
            continue
        }

        $catalogMetricIds[$metricId] = $true
        $direction = [string](Get-JsonValue $metric "direction")
        if ($direction -ne "higher" -and $direction -ne "lower" -and $direction -ne "informational") {
            $invalidDirectionCount++
            Add-ValidationIssue $errors "error" "benchmark-metric-direction-invalid" "Benchmark metric catalog entry '$metricId' has invalid direction '$direction'."
        }
    }

    $requiredMetricIds = @{}
    foreach ($scenarioId in $scenarioRequiredMetrics.Keys) {
        foreach ($metricId in @($scenarioRequiredMetrics[$scenarioId])) {
            $metric = ConvertTo-MetricId ([string]$metricId)
            if (-not [string]::IsNullOrWhiteSpace($metric)) {
                $requiredMetricIds[$metric] = $true
            }
        }
    }

    $missingCatalogMetricIds = New-Object System.Collections.Generic.List[string]
    foreach ($metricId in ($requiredMetricIds.Keys | Sort-Object)) {
        if (-not $catalogMetricIds.ContainsKey($metricId)) {
            $missingCatalogMetricIds.Add($metricId) | Out-Null
        }
    }

    if ($missingCatalogMetricIds.Count -gt 0) {
        Add-AcceptanceIssue $errors $warnings -AllowPreflight:$AllowPreflight "benchmark-metric-catalog-incomplete" "Benchmark metric catalog is missing required metrics: $($missingCatalogMetricIds.ToArray() -join ', ')."
    }

    $metricCatalogEvidence["metricCount"] = $catalogMetricIds.Count
    $metricCatalogEvidence["requiredMetricCount"] = $requiredMetricIds.Count
    $metricCatalogEvidence["missingRequiredMetricCount"] = $missingCatalogMetricIds.Count
    $metricCatalogEvidence["invalidDirectionCount"] = $invalidDirectionCount
    $metricCatalogEvidence["status"] = if ($missingCatalogMetricIds.Count -eq 0 -and $invalidDirectionCount -eq 0) { "ready" } else { "not-ready" }
}

if ($null -eq $externalCandidates) {
    $externalEngineCandidateEvidence["status"] = "missing"
    Add-AcceptanceIssue $errors $warnings -AllowPreflight:$AllowPreflight "external-engine-candidates-missing" "external-engine-candidates.json is required so optional RNNoise/DeepFilterNet/SpeexDSP/WebRTC candidates remain opt-in and evidence-gated."
}
else {
    $externalEngineCandidateEvidence["present"] = $true
    $candidateEntries = @(Get-ExternalEngineCandidateEntries $externalCandidates)
    $candidateIds = New-Object System.Collections.Generic.List[string]
    $seenCandidateIds = @{}
    $unsafeCandidateCount = 0

    foreach ($candidate in $candidateEntries) {
        $candidateUnsafe = $false
        $id = ConvertTo-CandidateId ([string](Get-JsonValue $candidate "id"))
        if ([string]::IsNullOrWhiteSpace($id)) {
            Add-ValidationIssue $errors "error" "external-candidate-id-missing" "External DSP candidate entry is missing an id."
            $candidateUnsafe = $true
        }
        else {
            $candidateIds.Add($id) | Out-Null
            $seenCandidateIds[$id] = $true
        }

        $schemaVersion = [int](Get-JsonValue $candidate "schemaVersion")
        if ($schemaVersion -ne 1) {
            Add-ValidationIssue $errors "error" "external-candidate-schema-unsupported" "External DSP candidate '$id' must use schemaVersion=1."
            $candidateUnsafe = $true
        }

        $defaultState = [string](Get-JsonValue $candidate "defaultState")
        if ($defaultState -ine "off") {
            Add-ValidationIssue $errors "error" "external-candidate-default-not-off" "External DSP candidate '$id' must default to off; found '$defaultState'."
            $candidateUnsafe = $true
        }

        $rolloutPolicy = [string](Get-JsonValue $candidate "rolloutPolicy")
        if ($rolloutPolicy -notlike "*opt-in*" -or $rolloutPolicy -notlike "*bakeoff*") {
            Add-ValidationIssue $errors "error" "external-candidate-rollout-not-gated" "External DSP candidate '$id' must remain opt-in bakeoff only; rolloutPolicy='$rolloutPolicy'."
            $candidateUnsafe = $true
        }

        $integrationPoint = [string](Get-JsonValue $candidate "integrationPoint")
        if ($integrationPoint -notlike "*post-demod*") {
            Add-ValidationIssue $errors "error" "external-candidate-integration-not-post-demod" "External DSP candidate '$id' must stay post-demod until benchmark proof exists; integrationPoint='$integrationPoint'."
            $candidateUnsafe = $true
        }

        foreach ($field in @("license", "packagingStatus", "runtimeRisk", "latencyRisk", "radioSafetyRisk")) {
            $value = [string](Get-JsonValue $candidate $field)
            if ([string]::IsNullOrWhiteSpace($value)) {
                Add-ValidationIssue $errors "error" "external-candidate-required-field-missing" "External DSP candidate '$id' must declare '$field'."
                $candidateUnsafe = $true
            }
        }

        $requiredBenchmarks = @(Get-JsonArray $candidate "requiredBenchmarks")
        $requiredEvidence = @(Get-JsonArray $candidate "requiredEvidence")
        $blockers = @(Get-JsonArray $candidate "blockers")
        $referenceUrls = @(Get-JsonArray $candidate "referenceUrls")

        if ($requiredBenchmarks.Count -lt 3) {
            Add-ValidationIssue $errors "error" "external-candidate-benchmarks-incomplete" "External DSP candidate '$id' must list at least three required benchmark scenarios."
            $candidateUnsafe = $true
        }
        if ($requiredEvidence.Count -lt 3) {
            Add-ValidationIssue $errors "error" "external-candidate-evidence-incomplete" "External DSP candidate '$id' must list licensing/package/runtime/fallback evidence requirements before integration."
            $candidateUnsafe = $true
        }
        if ($blockers.Count -le 0) {
            Add-ValidationIssue $errors "error" "external-candidate-blockers-missing" "External DSP candidate '$id' must keep explicit blockers until evidence clears them."
            $candidateUnsafe = $true
        }
        if ($referenceUrls.Count -le 0) {
            Add-ValidationIssue $errors "error" "external-candidate-references-missing" "External DSP candidate '$id' must include reference URLs for review."
            $candidateUnsafe = $true
        }

        if ($id -eq "webrtc-apm") {
            $webrtcSafetyText = "$((Get-JsonArray $candidate "requiredEvidence") -join ' ') $((Get-JsonValue $candidate "radioSafetyRisk"))"
            if ($webrtcSafetyText -notlike "*AGC*" -or $webrtcSafetyText -notlike "*AEC*" -or $webrtcSafetyText -notlike "*disabled*") {
                Add-ValidationIssue $errors "error" "external-candidate-webrtc-safety-gate-missing" "WebRTC APM candidate must explicitly keep AEC/AGC disabled unless separately approved."
                $candidateUnsafe = $true
            }
        }

        if ($candidateUnsafe) {
            $unsafeCandidateCount++
        }
    }

    $missingCandidateIds = New-Object System.Collections.Generic.List[string]
    foreach ($requiredCandidateId in $requiredExternalCandidateIds) {
        if (-not $seenCandidateIds.ContainsKey($requiredCandidateId)) {
            $missingCandidateIds.Add($requiredCandidateId) | Out-Null
        }
    }
    if ($missingCandidateIds.Count -gt 0) {
        Add-AcceptanceIssue $errors $warnings -AllowPreflight:$AllowPreflight "external-engine-candidates-incomplete" "External DSP candidate catalog is missing required candidate(s): $($missingCandidateIds.ToArray() -join ', ')."
    }

    $externalEngineCandidateEvidence["candidateCount"] = $candidateEntries.Count
    $externalEngineCandidateEvidence["candidateIds"] = @($candidateIds.ToArray() | Select-Object -Unique)
    $externalEngineCandidateEvidence["missingCandidateIds"] = @($missingCandidateIds.ToArray())
    $externalEngineCandidateEvidence["unsafeCandidateCount"] = $unsafeCandidateCount

    if ($null -ne $snapshot) {
        $snapshotCandidateEntries = @(Get-ExternalEngineCandidateEntries (Get-JsonValue $snapshot "externalEngineCandidates"))
        $externalEngineCandidateEvidence["snapshotPresent"] = ($snapshotCandidateEntries.Count -gt 0)
        $externalEngineCandidateEvidence["snapshotCandidateCount"] = $snapshotCandidateEntries.Count

        $snapshotIds = @{}
        foreach ($candidate in $snapshotCandidateEntries) {
            $snapshotId = ConvertTo-CandidateId ([string](Get-JsonValue $candidate "id"))
            if (-not [string]::IsNullOrWhiteSpace($snapshotId)) {
                $snapshotIds[$snapshotId] = $true
            }
        }

        $snapshotMismatchCount = 0
        foreach ($id in @($seenCandidateIds.Keys)) {
            if (-not $snapshotIds.ContainsKey($id)) {
                $snapshotMismatchCount++
                Add-ValidationIssue $errors "error" "external-candidate-snapshot-mismatch" "Modernization snapshot is missing external DSP candidate '$id' captured by /api/dsp/external-engine-candidates."
            }
        }
        foreach ($id in @($snapshotIds.Keys)) {
            if (-not $seenCandidateIds.ContainsKey($id)) {
                $snapshotMismatchCount++
                Add-ValidationIssue $errors "error" "external-candidate-snapshot-mismatch" "Modernization snapshot includes external DSP candidate '$id' not present in /api/dsp/external-engine-candidates."
            }
        }
        $externalEngineCandidateEvidence["snapshotMismatchCount"] = $snapshotMismatchCount
    }

    if ($candidateEntries.Count -eq 0) {
        $externalEngineCandidateEvidence["status"] = "empty"
        Add-AcceptanceIssue $errors $warnings -AllowPreflight:$AllowPreflight "external-engine-candidates-empty" "External DSP candidate catalog is empty."
    }
    elseif ($missingCandidateIds.Count -eq 0 -and $unsafeCandidateCount -eq 0 -and [int]$externalEngineCandidateEvidence["snapshotMismatchCount"] -eq 0) {
        $externalEngineCandidateEvidence["status"] = "opt-in-gated"
    }
    else {
        $externalEngineCandidateEvidence["status"] = "not-ready"
    }
}

$planHardwareTarget = ""
if ($null -ne $plan) {
    $planHardwareTarget = [string](Get-JsonValue $plan "firstHardwareTarget")
    $hardwareEvidence["planTarget"] = $planHardwareTarget

    if ([string]::IsNullOrWhiteSpace($planHardwareTarget)) {
        $hardwareTargetOk = $false
        Add-ValidationIssue $errors "error" "benchmark-first-hardware-target-missing" "Benchmark plan must declare firstHardwareTarget for first-cycle DSP evidence."
    }
    elseif (-not (Test-G2BenchmarkTarget $planHardwareTarget)) {
        $hardwareTargetOk = $false
        Add-ValidationIssue $errors "error" "benchmark-first-hardware-target-not-g2" "Benchmark plan firstHardwareTarget must remain G2 for first-cycle DSP evidence; found '$planHardwareTarget'."
    }

    $requiredHardwareText = (Get-JsonArray $plan "requiredHardwareBeforeGraduation") -join " "
    if ($requiredHardwareText -notlike "*G2*" -or $requiredHardwareText -notlike "*non-G2*") {
        Add-ValidationIssue $errors "error" "benchmark-hardware-graduation-gates-incomplete" "Benchmark plan must keep both G2 first-pass validation and non-G2 cross-radio validation before graduation."
    }
}

if ($null -ne $manifest) {
    $manifestHardwareTarget = [string](Get-JsonValue $manifest "hardwareTarget")
    $hardwareEvidence["manifestTarget"] = $manifestHardwareTarget

    if ([string]::IsNullOrWhiteSpace($manifestHardwareTarget)) {
        $hardwareTargetOk = $false
        Add-ValidationIssue $errors "error" "capture-hardware-target-missing" "Capture manifest must declare hardwareTarget."
    }
    elseif (-not (Test-G2BenchmarkTarget $manifestHardwareTarget)) {
        $hardwareTargetOk = $false
        Add-AcceptanceIssue $errors $warnings -AllowPreflight:$AllowPreflight "capture-hardware-target-not-g2" "Capture manifest hardwareTarget must be G2 for first-cycle DSP evidence; found '$manifestHardwareTarget'."
    }

    $planTargetId = ConvertTo-HardwareId $planHardwareTarget
    $manifestTargetId = ConvertTo-HardwareId $manifestHardwareTarget
    if ((-not [string]::IsNullOrWhiteSpace($planTargetId)) -and
        (-not [string]::IsNullOrWhiteSpace($manifestTargetId)) -and
        ($planTargetId -ne $manifestTargetId)) {
        $hardwareTargetOk = $false
        Add-AcceptanceIssue $errors $warnings -AllowPreflight:$AllowPreflight "capture-hardware-target-mismatch" "Capture manifest hardwareTarget '$manifestHardwareTarget' does not match benchmark plan firstHardwareTarget '$planHardwareTarget'."
    }
}

if ($null -eq $hardware) {
    $hardwareEvidence["status"] = "diagnostics-missing"
    Add-ValidationIssue $errors "error" "hardware-diagnostics-missing" "hardware-diagnostics.json is required for G2 first-cycle DSP evidence."
}
else {
    $hardwareEvidence["diagnosticsPresent"] = $true

    $connectionStatus = [string](Get-JsonValue $hardware "connectionStatus")
    $connectedBoard = [string](Get-HardwareDiagnosticField $hardware "connectedBoard")
    $effectiveBoard = [string](Get-HardwareDiagnosticField $hardware "effectiveBoard")
    $variant = [string](Get-HardwareDiagnosticField $hardware "orionMkIIVariant")
    $g2Class = Test-Truthy (Get-HardwareDiagnosticField $hardware "g2Class")

    $hardwareEvidence["connectionStatus"] = $connectionStatus
    $hardwareEvidence["connectedBoard"] = $connectedBoard
    $hardwareEvidence["effectiveBoard"] = $effectiveBoard
    $hardwareEvidence["orionMkIIVariant"] = $variant
    $hardwareEvidence["g2Class"] = $g2Class

    $connectedOk = $connectionStatus -ieq "Connected"
    if (-not $connectedOk) {
        Add-AcceptanceIssue $errors $warnings -AllowPreflight:$AllowPreflight "hardware-diagnostics-not-connected" "Hardware diagnostics must be captured from a connected G2 radio; connectionStatus is '$connectionStatus'."
    }

    $orionEvidence = (Test-OrionMkIIBoard $connectedBoard) -or (Test-OrionMkIIBoard $effectiveBoard)
    $variantEvidence = Test-G2Variant $variant
    $g2Evidence = $g2Class -and $orionEvidence -and $variantEvidence

    if (-not $g2Evidence) {
        Add-AcceptanceIssue $errors $warnings -AllowPreflight:$AllowPreflight "hardware-diagnostics-not-g2" "Hardware diagnostics must show G2-class OrionMkII evidence; connectedBoard='$connectedBoard', effectiveBoard='$effectiveBoard', orionMkIIVariant='$variant', g2Class='$g2Class'."
    }

    $hardwareDiagnosticsOk = $connectedOk -and $g2Evidence
    if ($hardwareDiagnosticsOk) {
        $hardwareEvidence["status"] = "g2-hardware-evidence-ready"
    }
    elseif (-not $connectedOk) {
        $hardwareEvidence["status"] = "not-connected"
    }
    else {
        $hardwareEvidence["status"] = "not-g2"
    }
}

$hardwareEvidence["ok"] = ($hardwareTargetOk -and $hardwareDiagnosticsOk)

if ($null -eq $snapshot) {
    Add-ValidationIssue $errors "error" "snapshot-missing" "modernization-snapshot.json is required for acceptance review."
}
else {
    $score = Get-JsonValue $snapshot "evidenceCompletenessScore"
    $readyCapture = Test-Truthy (Get-JsonValue $snapshot "readyForCapture")
    $readyLive = Test-Truthy (Get-JsonValue $snapshot "readyForLiveBenchmark")
    $missingEvidence = Get-JsonArray $snapshot "missingEvidence"

    if ($null -eq $score -or [int]$score -lt 0 -or [int]$score -gt 100) {
        Add-ValidationIssue $errors "error" "snapshot-score-invalid" "Modernization snapshot evidenceCompletenessScore must be 0..100."
    }
    elseif ([int]$score -lt 90) {
        Add-AcceptanceIssue $errors $warnings -AllowPreflight:$AllowPreflight "snapshot-score-low" "Modernization snapshot evidenceCompletenessScore is $score; acceptance evidence requires at least 90."
    }

    if (-not $readyLive) {
        Add-AcceptanceIssue $errors $warnings -AllowPreflight:$AllowPreflight "live-benchmark-not-ready" "Snapshot is not ready for live benchmark capture."
    }

    if (-not $readyCapture) {
        Add-AcceptanceIssue $errors $warnings -AllowPreflight:$AllowPreflight "capture-not-ready" "Snapshot is not ready for capture acceptance."
    }

    if ($missingEvidence.Count -gt 0) {
        Add-AcceptanceIssue $errors $warnings -AllowPreflight:$AllowPreflight "snapshot-missing-evidence" "Snapshot reports missing evidence: $($missingEvidence -join ', ')."
    }
}

if ($null -eq $live) {
    Add-ValidationIssue $errors "error" "live-diagnostics-missing" "live-diagnostics.json is required."
}
else {
    $rolloutGate = [string](Get-JsonValue $live "rolloutGate")
    if ($rolloutGate -notlike "*opt-in*") {
        Add-ValidationIssue $errors "error" "rollout-gate-not-opt-in" "Live diagnostics rollout gate must remain opt-in until acceptance evidence passes."
    }
}

if ($null -eq $manifest) {
    Add-ValidationIssue $errors "error" "capture-manifest-missing" "benchmark-capture-manifest.json is required."
}
else {
    $requiredArtifacts = Get-JsonArray $manifest "requiredArtifacts"
    if ($requiredArtifacts.Count -eq 0) {
        Add-ValidationIssue $errors "error" "manifest-artifacts-missing" "Capture manifest does not list required artifacts."
    }

    $artifactIds = @{}
    foreach ($artifact in $requiredArtifacts) {
        $artifactId = [string](Get-JsonValue $artifact "id")
        if (-not [string]::IsNullOrWhiteSpace($artifactId)) {
            $artifactIds[$artifactId] = $true
            $captureAllArtifactIds[$artifactId] = $true

            $artifactKind = [string](Get-JsonValue $artifact "kind")
            $artifactRequired = Test-Truthy (Get-JsonValue $artifact "required")
            if ($artifactRequired -and $artifactKind -ine "endpoint-json") {
                $captureRequiredPhysicalArtifactIds[$artifactId] = $true
            }
            $captureArtifactScenarioIds[$artifactId] = @(Get-JsonArray $artifact "scenarioIds")
        }
    }

    $captureAllArtifactIds[$metricComparisonArtifactId] = $true
    $captureRequiredPhysicalArtifactIds[$metricComparisonArtifactId] = $true
    $captureArtifactScenarioIds[$metricComparisonArtifactId] = @(Get-JsonArray $manifest "scenarioIds")
    $captureAllArtifactIds[$liveDiagnosticsHistoryArtifactId] = $true
    $captureArtifactScenarioIds[$liveDiagnosticsHistoryArtifactId] = @(Get-JsonArray $manifest "scenarioIds")
    foreach ($matrixReportArtifactId in @("live-diagnostics-matrix-report-baseline", "live-diagnostics-matrix-report-candidate")) {
        $captureAllArtifactIds[$matrixReportArtifactId] = $true
        $captureArtifactScenarioIds[$matrixReportArtifactId] = @(Get-JsonArray $manifest "scenarioIds")
    }

    foreach ($requiredArtifact in @("live-diagnostics-json", "benchmark-plan-json", $nativeSymbolAuditArtifactId, $nativeRuntimeArtifactAuditId, "offline-fixture-metrics")) {
        if (-not $artifactIds.ContainsKey($requiredArtifact)) {
            Add-ValidationIssue $errors "error" "manifest-required-artifact-missing" "Capture manifest is missing required artifact '$requiredArtifact'."
        }
    }
}

$artifactManifestCandidate = ""
$artifactManifestSpecified = -not [string]::IsNullOrWhiteSpace($ArtifactManifestPath)
if ($artifactManifestSpecified) {
    $artifactManifestCandidate = Get-BundlePath $bundlePath $ArtifactManifestPath
}
else {
    $defaultArtifactManifestPath = Join-Path $bundlePath "artifact-manifest.json"
    if (Test-Path -LiteralPath $defaultArtifactManifestPath -PathType Leaf) {
        $artifactManifestCandidate = $defaultArtifactManifestPath
    }
}

if ([string]::IsNullOrWhiteSpace($artifactManifestCandidate)) {
    if ($RequireArtifactFiles) {
        Add-ValidationIssue $errors "error" "artifact-manifest-missing" "Artifact file validation was required, but no artifact-manifest.json was found in the bundle."
    }
    else {
        Add-ValidationIssue $warnings "warning" "artifact-manifest-not-provided" "No artifact-manifest.json was provided; endpoint JSON was checked, but offline metrics/audio/spectrum files were not validated."
    }
}
elseif (-not (Test-Path -LiteralPath $artifactManifestCandidate -PathType Leaf)) {
    Add-ValidationIssue $errors "error" "artifact-manifest-missing" "Artifact manifest file is missing: $artifactManifestCandidate"
}
else {
    $artifactManifestReportPath = (Resolve-Path -LiteralPath $artifactManifestCandidate).Path
    $artifactManifestProvided = $true
    $artifactManifest = $null

    try {
        $artifactManifest = Read-JsonFile $artifactManifestReportPath
    }
    catch {
        Add-ValidationIssue $errors "error" "artifact-manifest-invalid" $_.Exception.Message
    }

    if ($null -ne $artifactManifest) {
        $schemaVersion = Get-JsonValue $artifactManifest "schemaVersion"
        if ($null -ne $schemaVersion) {
            $parsedSchemaVersion = 0
            if (-not [int]::TryParse([string]$schemaVersion, [ref]$parsedSchemaVersion) -or $parsedSchemaVersion -ne 1) {
                Add-ValidationIssue $errors "error" "artifact-manifest-schema-unsupported" "Artifact manifest schemaVersion must be 1."
            }
        }

        $declaredArtifacts = Get-JsonArray $artifactManifest "artifacts"
        if ($declaredArtifacts.Count -eq 0) {
            Add-ValidationIssue $errors "error" "artifact-manifest-artifacts-missing" "Artifact manifest does not list any artifacts."
        }

        $declaredArtifactIds = @{}
        foreach ($artifact in $declaredArtifacts) {
            $artifactId = [string](Get-JsonValue $artifact "id")
            $artifactKind = [string](Get-JsonValue $artifact "kind")
            $artifactPath = [string](Get-JsonValue $artifact "path")
            $manifestRequired = Test-Truthy (Get-JsonValue $artifact "required")
            $effectiveRequired = $manifestRequired
            $expectedScenarioIds = @(Get-JsonArray $artifact "scenarioIds")
            $expectedComparisonIds = New-Object System.Collections.Generic.List[string]
            foreach ($comparisonId in (Get-JsonArray $artifact "comparisonIds")) {
                $comparison = ConvertTo-ComparisonId ([string]$comparisonId)
                if (-not [string]::IsNullOrWhiteSpace($comparison)) {
                    $expectedComparisonIds.Add($comparison) | Out-Null
                }
            }
            $expectedComparisonIds = @($expectedComparisonIds.ToArray() | Select-Object -Unique)
            if ($artifactId -ne $externalEngineBakeoffArtifactId -and $expectedComparisonIds -contains $externalEngineOptInComparisonId) {
                $externalEngineBakeoffRequiredByScope = $true
            }

            if (-not [string]::IsNullOrWhiteSpace($artifactId) -and $captureRequiredPhysicalArtifactIds.ContainsKey($artifactId)) {
                $effectiveRequired = $true
            }
            if ($expectedScenarioIds.Count -eq 0 -and -not [string]::IsNullOrWhiteSpace($artifactId) -and $captureArtifactScenarioIds.ContainsKey($artifactId)) {
                $expectedScenarioIds = @($captureArtifactScenarioIds[$artifactId])
            }

            if ($effectiveRequired) {
                $requiredArtifactFileCount++
            }

            $record = [ordered]@{
                id = $artifactId
                kind = $artifactKind
                path = $artifactPath
                required = $effectiveRequired
                scenarioIds = @($expectedScenarioIds)
                comparisonIds = @($expectedComparisonIds)
                ok = $false
                bytes = 0
                jsonlLineCount = 0
            }
            $artifactFiles.Add($record) | Out-Null

            if ([string]::IsNullOrWhiteSpace($artifactId)) {
                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "artifact-id-missing" "Artifact manifest contains an entry with no id."
                continue
            }

            $declaredArtifactIds[$artifactId] = $true

            if (-not $captureAllArtifactIds.ContainsKey($artifactId) -and $artifactKind -ine "notes") {
                Add-ValidationIssue $warnings "warning" "artifact-not-in-capture-manifest" "Artifact '$artifactId' is not referenced by the capture manifest requiredArtifacts list."
            }

            if ([string]::IsNullOrWhiteSpace($artifactPath)) {
                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "artifact-path-missing" "Artifact '$artifactId' does not declare a path."
                continue
            }

            if ([System.IO.Path]::IsPathRooted($artifactPath)) {
                Add-ValidationIssue $warnings "warning" "artifact-path-absolute" "Artifact '$artifactId' uses an absolute path; relative paths keep capture bundles portable."
            }

            $resolvedArtifactPath = Get-BundlePath $bundlePath $artifactPath
            if (-not (Test-Path -LiteralPath $resolvedArtifactPath -PathType Leaf)) {
                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "artifact-file-missing" "Artifact '$artifactId' file is missing: $artifactPath"
                continue
            }

            $item = Get-Item -LiteralPath $resolvedArtifactPath
            $record["bytes"] = $item.Length
            if ($item.Length -le 0) {
                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "artifact-file-empty" "Artifact '$artifactId' file is empty: $artifactPath"
                continue
            }

            $artifactJson = $null
            if (Test-JsonlArtifact $artifactKind $artifactPath) {
                try {
                    $record["jsonlLineCount"] = Test-JsonlFile $resolvedArtifactPath
                    if ([int]$record["jsonlLineCount"] -le 0) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "artifact-jsonl-empty" "Artifact '$artifactId' JSONL file has no records: $artifactPath"
                        continue
                    }
                }
                catch {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "artifact-jsonl-invalid" $_.Exception.Message
                    continue
                }
            }
            elseif (Test-JsonArtifact $artifactKind $artifactPath) {
                try {
                    $artifactJson = Read-JsonFile $resolvedArtifactPath
                }
                catch {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "artifact-json-invalid" $_.Exception.Message
                    continue
                }
            }

            $artifactValidationOk = $true
            if ($null -ne $artifactJson -and $artifactId -ne $externalEngineBakeoffArtifactId) {
                $artifactContentComparisonIds = @(Get-ComparisonIdsFromReport $artifactJson)
                if ($artifactContentComparisonIds -contains $externalEngineOptInComparisonId) {
                    $externalEngineBakeoffRequiredByScope = $true
                }
            }

            if ($artifactKind -ieq "metrics-json" -or $artifactId -eq "offline-fixture-metrics") {
                if ($artifactId -eq "offline-fixture-metrics") {
                    $sourceEvidenceEngine = Get-FixtureEvidenceEngine $artifactJson
                    $sourceEvidenceTool = [string](Get-JsonValue $artifactJson "tool")
                    $sourceSha256 = Get-FileSha256 $resolvedArtifactPath
                    $sourceWdspBacked = [string]::Equals($sourceEvidenceEngine, "wdsp", [StringComparison]::OrdinalIgnoreCase)
                    $sourceSyntheticFallback = [string]::Equals($sourceEvidenceEngine, "deterministic-synthetic", [StringComparison]::OrdinalIgnoreCase)
                    $sourceRuntimeRid = [string](Get-JsonValue $artifactJson "wdspRuntimeRid")
                    $sourceRuntimePath = [string](Get-JsonValue $artifactJson "wdspRuntimePath")
                    $sourceRuntimePathKind = [string](Get-JsonValue $artifactJson "wdspRuntimePathKind")
                    $sourceRuntimeFileName = [string](Get-JsonValue $artifactJson "wdspRuntimeFileName")
                    $sourceRuntimeLength = [int64](Get-NumericValueOrDefault (Get-JsonValue $artifactJson "wdspRuntimeLength"))
                    $sourceRuntimeSha256 = ([string](Get-JsonValue $artifactJson "wdspRuntimeSha256")).Trim().ToLowerInvariant()
                    $sourceRuntimeStatus = [string](Get-JsonValue $artifactJson "wdspRuntimeStatus")
                    $sourceScenarioCount = [int](Get-JsonValue $artifactJson "scenarioCount")
                    if ($sourceScenarioCount -eq 0) {
                        $sourceScenarioCount = @(Get-JsonArray $artifactJson "scenarios").Count
                    }
                    $sourceComparisonIds = New-Object System.Collections.Generic.HashSet[string]
                    foreach ($entry in @(Get-MetricEvidenceEntries $artifactJson)) {
                        foreach ($comparisonId in @($entry.comparisonIds)) {
                            $comparison = ConvertTo-ComparisonId ([string]$comparisonId)
                            if (-not [string]::IsNullOrWhiteSpace($comparison)) {
                                [void]$sourceComparisonIds.Add($comparison)
                            }
                        }
                    }

                    $offlineFixtureMetricsEvidence["present"] = $true
                    $offlineFixtureMetricsEvidence["artifactId"] = $artifactId
                    $offlineFixtureMetricsEvidence["path"] = $artifactPath
                    $offlineFixtureMetricsEvidence["evidenceEngine"] = $sourceEvidenceEngine
                    $offlineFixtureMetricsEvidence["evidenceTool"] = $sourceEvidenceTool
                    $offlineFixtureMetricsEvidence["wdspBackedEvidence"] = $sourceWdspBacked
                    $offlineFixtureMetricsEvidence["syntheticFallbackEvidence"] = $sourceSyntheticFallback
                    $offlineFixtureMetricsEvidence["scenarioCount"] = $sourceScenarioCount
                    $offlineFixtureMetricsEvidence["comparisonCount"] = $sourceComparisonIds.Count
                    $offlineFixtureMetricsEvidence["sha256"] = $sourceSha256
                    $offlineFixtureMetricsEvidence["wdspRuntimeRid"] = $sourceRuntimeRid
                    $offlineFixtureMetricsEvidence["wdspRuntimePath"] = $sourceRuntimePath
                    $offlineFixtureMetricsEvidence["wdspRuntimePathKind"] = $sourceRuntimePathKind
                    $offlineFixtureMetricsEvidence["wdspRuntimeFileName"] = $sourceRuntimeFileName
                    $offlineFixtureMetricsEvidence["wdspRuntimeLength"] = $sourceRuntimeLength
                    $offlineFixtureMetricsEvidence["wdspRuntimeSha256"] = $sourceRuntimeSha256
                    $offlineFixtureMetricsEvidence["wdspRuntimeStatus"] = $sourceRuntimeStatus
                    $offlineFixtureMetricsEvidence["status"] = if ($sourceWdspBacked) { "ready" } else { "not-ready" }

                    if (-not $sourceWdspBacked) {
                        $engineText = if ([string]::IsNullOrWhiteSpace($sourceEvidenceEngine)) { "missing" } else { $sourceEvidenceEngine }
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "offline-fixture-metrics-not-wdsp-backed" "Artifact '$artifactId' must be generated by run-dsp-wdsp-fixture-matrix.ps1 or run-dsp-wdsp-fixture-evidence.ps1; evidenceEngine='$engineText'."
                        $artifactValidationOk = $false
                    }
                    if ($sourceWdspBacked -and [string]::IsNullOrWhiteSpace($sourceRuntimeSha256)) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "offline-fixture-runtime-hash-missing" "Artifact '$artifactId' is WDSP-backed but does not declare wdspRuntimeSha256 for the native runtime used by the fixture run."
                        $artifactValidationOk = $false
                        $offlineFixtureMetricsEvidence["status"] = "not-ready"
                    }
                    if ($sourceWdspBacked -and
                        -not [string]::IsNullOrWhiteSpace($sourceRuntimeStatus) -and
                        -not [string]::Equals($sourceRuntimeStatus, "found", [StringComparison]::OrdinalIgnoreCase)) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "offline-fixture-runtime-not-found" "Artifact '$artifactId' is WDSP-backed but reports wdspRuntimeStatus='$sourceRuntimeStatus'."
                        $artifactValidationOk = $false
                        $offlineFixtureMetricsEvidence["status"] = "not-ready"
                    }
                }

                $metricEntries = @(Get-MetricEvidenceEntries $artifactJson)
                if ($metricEntries.Count -eq 0) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "artifact-metrics-missing" "Artifact '$artifactId' does not contain any metric result entries."
                    $artifactValidationOk = $false
                }

                $metricCoverageByScenarioComparison = @{}
                foreach ($metricEntry in $metricEntries) {
                    foreach ($scenarioId in @($metricEntry.scenarioIds)) {
                        $scenario = [string]$scenarioId
                        if ([string]::IsNullOrWhiteSpace($scenario)) {
                            continue
                        }

                        foreach ($comparisonId in @($metricEntry.comparisonIds)) {
                            $comparison = ConvertTo-ComparisonId ([string]$comparisonId)
                            if ([string]::IsNullOrWhiteSpace($comparison)) {
                                continue
                            }

                            $key = "$scenario||$comparison"
                            if (-not $metricCoverageByScenarioComparison.ContainsKey($key)) {
                                $metricCoverageByScenarioComparison[$key] = [ordered]@{
                                    metricIds = @{}
                                    gateOutcomeCount = 0
                                    failedGateCount = 0
                                }
                            }

                            foreach ($metricId in @($metricEntry.metricIds)) {
                                $metric = ConvertTo-MetricId ([string]$metricId)
                                if (-not [string]::IsNullOrWhiteSpace($metric)) {
                                    $metricCoverageByScenarioComparison[$key].metricIds[$metric] = $true
                                }
                            }

                            $metricCoverageByScenarioComparison[$key].gateOutcomeCount += [int]$metricEntry.gateOutcomeCount
                            $metricCoverageByScenarioComparison[$key].failedGateCount += [int]$metricEntry.failedGateCount
                        }
                    }
                }

                foreach ($expectedScenarioId in $expectedScenarioIds) {
                    $scenario = [string]$expectedScenarioId
                    if ([string]::IsNullOrWhiteSpace($scenario)) {
                        continue
                    }

                    $requiredComparisons = @($expectedComparisonIds)
                    if ($requiredComparisons.Count -eq 0 -and $scenarioRequiredComparisons.ContainsKey($scenario)) {
                        $requiredComparisons = @($scenarioRequiredComparisons[$scenario])
                    }

                    $requiredMetrics = @()
                    if ($scenarioRequiredMetrics.ContainsKey($scenario)) {
                        $requiredMetrics = @($scenarioRequiredMetrics[$scenario])
                    }

                    if ($requiredComparisons.Count -eq 0 -or $requiredMetrics.Count -eq 0) {
                        continue
                    }

                    foreach ($requiredComparison in $requiredComparisons) {
                        $comparison = ConvertTo-ComparisonId ([string]$requiredComparison)
                        if ([string]::IsNullOrWhiteSpace($comparison)) {
                            continue
                        }

                        $key = "$scenario||$comparison"
                        $coveredMetricIds = @{}
                        $gateOutcomeCount = 0
                        $failedGateCount = 0
                        if ($metricCoverageByScenarioComparison.ContainsKey($key)) {
                            $coveredMetricIds = $metricCoverageByScenarioComparison[$key].metricIds
                            $gateOutcomeCount = [int]$metricCoverageByScenarioComparison[$key].gateOutcomeCount
                            $failedGateCount = [int]$metricCoverageByScenarioComparison[$key].failedGateCount
                        }

                        $missingMetricIds = New-Object System.Collections.Generic.List[string]
                        foreach ($requiredMetric in $requiredMetrics) {
                            $metric = ConvertTo-MetricId ([string]$requiredMetric)
                            if (-not [string]::IsNullOrWhiteSpace($metric) -and -not $coveredMetricIds.ContainsKey($metric)) {
                                $missingMetricIds.Add($metric) | Out-Null
                            }
                        }

                        $artifactGateOutcomeCount += $gateOutcomeCount
                        $artifactFailedGateCount += $failedGateCount

                        $metricCoverageRecord = [ordered]@{
                            artifactId = $artifactId
                            scenarioId = $scenario
                            comparisonId = $comparison
                            required = $effectiveRequired
                            ok = ($missingMetricIds.Count -eq 0 -and $gateOutcomeCount -gt 0 -and $failedGateCount -eq 0)
                            requiredMetricIds = @($requiredMetrics)
                            coveredMetricIds = @($coveredMetricIds.Keys | Sort-Object)
                            missingMetricIds = @($missingMetricIds.ToArray())
                            gateOutcomeCount = $gateOutcomeCount
                            failedGateCount = $failedGateCount
                        }
                        $artifactMetricCoverage.Add($metricCoverageRecord) | Out-Null

                        if ($missingMetricIds.Count -gt 0) {
                            $artifactMissingMetricCount += $missingMetricIds.Count
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "artifact-metric-missing" "Artifact '$artifactId' metrics are missing required metrics for scenario '$scenario' comparison '$comparison': $($missingMetricIds.ToArray() -join ', ')."
                            $artifactValidationOk = $false
                        }

                        if ($gateOutcomeCount -le 0) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "artifact-gate-outcome-missing" "Artifact '$artifactId' metrics are missing gate outcomes for scenario '$scenario' comparison '$comparison'."
                            $artifactValidationOk = $false
                        }

                        if ($failedGateCount -gt 0) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "artifact-gate-failed" "Artifact '$artifactId' metrics report $failedGateCount failed gate outcome(s) for scenario '$scenario' comparison '$comparison'."
                            $artifactValidationOk = $false
                        }
                    }
                }
            }

            if ($artifactKind -ieq "comparison-json" -or $artifactId -eq $metricComparisonArtifactId) {
                $metricComparisonEvidence["present"] = $true

                $tool = [string](Get-JsonValue $artifactJson "tool")
                if ($tool -ne "compare-dsp-fixture-metrics") {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "metric-comparison-tool-invalid" "Artifact '$artifactId' must be generated by compare-dsp-fixture-metrics.ps1."
                    $artifactValidationOk = $false
                }

                $fixtureScenarioScope = [string](Get-JsonValue $artifactJson "fixtureScenarioScope")
                $skippedNonFixtureScenarioIds = @(Get-JsonArray $artifactJson "skippedNonFixtureScenarioIds")
                $skippedNonFixtureScenarioCount = [int](Get-JsonValue $artifactJson "skippedNonFixtureScenarioCount")
                if ($skippedNonFixtureScenarioCount -eq 0 -and $skippedNonFixtureScenarioIds.Count -gt 0) {
                    $skippedNonFixtureScenarioCount = $skippedNonFixtureScenarioIds.Count
                }
                $readyForReview = Test-Truthy (Get-JsonValue $artifactJson "readyForReview")
                $metricCoverageReadyForReview = Test-Truthy (Get-JsonValue $artifactJson "metricCoverageReadyForReview")
                $evidenceEngine = [string](Get-JsonValue $artifactJson "evidenceEngine")
                $evidenceTool = [string](Get-JsonValue $artifactJson "evidenceTool")
                $sourceMetricsPath = [string](Get-JsonValue $artifactJson "metricsPath")
                $sourceMetricsSha256 = ([string](Get-JsonValue $artifactJson "metricsSha256")).Trim().ToLowerInvariant()
                $runtimeRid = [string](Get-JsonValue $artifactJson "wdspRuntimeRid")
                $runtimePath = [string](Get-JsonValue $artifactJson "wdspRuntimePath")
                $runtimePathKind = [string](Get-JsonValue $artifactJson "wdspRuntimePathKind")
                $runtimeFileName = [string](Get-JsonValue $artifactJson "wdspRuntimeFileName")
                $runtimeLength = [int64](Get-NumericValueOrDefault (Get-JsonValue $artifactJson "wdspRuntimeLength"))
                $runtimeSha256 = ([string](Get-JsonValue $artifactJson "wdspRuntimeSha256")).Trim().ToLowerInvariant()
                $runtimeStatus = [string](Get-JsonValue $artifactJson "wdspRuntimeStatus")
                $runtimeIdentityReadyForReview = Test-Truthy (Get-JsonValue $artifactJson "wdspRuntimeIdentityReadyForReview")
                if ([string]::IsNullOrWhiteSpace($evidenceTool)) {
                    $evidenceTool = [string](Get-JsonValue $artifactJson "tool")
                }
                $wdspBackedEvidence = Test-Truthy (Get-JsonValue $artifactJson "wdspBackedEvidence")
                $syntheticFallbackEvidence = Test-Truthy (Get-JsonValue $artifactJson "syntheticFallbackEvidence")
                $regressions = [int](Get-JsonValue $artifactJson "regressionCount")
                $gateFailures = [int](Get-JsonValue $artifactJson "gateFailureCount")
                $missingScenarios = @(Get-JsonArray $artifactJson "missingScenarios")
                $missingScenarioCount = [int](Get-JsonValue $artifactJson "missingScenarioCount")
                if ($missingScenarioCount -eq 0 -and $missingScenarios.Count -gt 0) {
                    $missingScenarioCount = $missingScenarios.Count
                }
                $missingCurrent = [int](Get-JsonValue $artifactJson "missingCurrentBaselineCount")
                $missingThetis = [int](Get-JsonValue $artifactJson "missingThetisBaselineCount")
                $missingCandidate = [int](Get-JsonValue $artifactJson "missingCandidateCount")
                $missingMetrics = [int](Get-JsonValue $artifactJson "missingMetricValueCount")
                $candidateComparisons = [int](Get-JsonValue $artifactJson "candidateComparisonCount")

                $metricComparisonEvidence["reportReadyForReview"] = $readyForReview
                $metricComparisonEvidence["metricCoverageReadyForReview"] = $metricCoverageReadyForReview
                $metricComparisonEvidence["evidenceEngine"] = $evidenceEngine
                $metricComparisonEvidence["evidenceTool"] = $evidenceTool
                $metricComparisonEvidence["wdspBackedEvidence"] = $wdspBackedEvidence
                $metricComparisonEvidence["syntheticFallbackEvidence"] = $syntheticFallbackEvidence
                $metricComparisonEvidence["sourceMetricsPath"] = $sourceMetricsPath
                $metricComparisonEvidence["sourceMetricsSha256"] = $sourceMetricsSha256
                $metricComparisonEvidence["wdspRuntimeRid"] = $runtimeRid
                $metricComparisonEvidence["wdspRuntimePath"] = $runtimePath
                $metricComparisonEvidence["wdspRuntimePathKind"] = $runtimePathKind
                $metricComparisonEvidence["wdspRuntimeFileName"] = $runtimeFileName
                $metricComparisonEvidence["wdspRuntimeLength"] = $runtimeLength
                $metricComparisonEvidence["wdspRuntimeSha256"] = $runtimeSha256
                $metricComparisonEvidence["wdspRuntimeStatus"] = $runtimeStatus
                $metricComparisonEvidence["wdspRuntimeIdentityReadyForReview"] = $runtimeIdentityReadyForReview
                $metricComparisonEvidence["readyForReview"] = ($readyForReview -and $wdspBackedEvidence)
                $metricComparisonEvidence["fixtureScenarioScope"] = $fixtureScenarioScope
                $metricComparisonEvidence["skippedNonFixtureScenarioCount"] = $skippedNonFixtureScenarioCount
                $metricComparisonEvidence["skippedNonFixtureScenarioIds"] = @($skippedNonFixtureScenarioIds)
                $metricComparisonEvidence["regressionCount"] = $regressions
                $metricComparisonEvidence["gateFailureCount"] = $gateFailures
                $metricComparisonEvidence["missingScenarioCount"] = $missingScenarioCount
                $metricComparisonEvidence["missingScenarios"] = @($missingScenarios)
                $metricComparisonEvidence["missingCurrentBaselineCount"] = $missingCurrent
                $metricComparisonEvidence["missingThetisBaselineCount"] = $missingThetis
                $metricComparisonEvidence["missingCandidateCount"] = $missingCandidate
                $metricComparisonEvidence["missingMetricValueCount"] = $missingMetrics
                $metricComparisonEvidence["candidateComparisonCount"] = $candidateComparisons

                if (-not $readyForReview) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "metric-comparison-not-ready" "Artifact '$artifactId' reports readyForReview=false."
                    $artifactValidationOk = $false
                }
                if (-not $wdspBackedEvidence) {
                    $engineText = if ([string]::IsNullOrWhiteSpace($evidenceEngine)) { "missing" } else { $evidenceEngine }
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "metric-comparison-not-wdsp-backed" "Artifact '$artifactId' must compare WDSP-backed fixture evidence from run-dsp-wdsp-fixture-matrix.ps1 or run-dsp-wdsp-fixture-evidence.ps1; evidenceEngine='$engineText'."
                    $artifactValidationOk = $false
                }
                if ($wdspBackedEvidence -and [string]::IsNullOrWhiteSpace($runtimeSha256)) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "metric-comparison-runtime-hash-missing" "Artifact '$artifactId' must declare wdspRuntimeSha256 copied from the source offline-fixture-metrics artifact."
                    $artifactValidationOk = $false
                    $metricComparisonEvidence["readyForReview"] = $false
                }
                if ($wdspBackedEvidence -and -not $runtimeIdentityReadyForReview) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "metric-comparison-runtime-identity-not-ready" "Artifact '$artifactId' reports wdspRuntimeIdentityReadyForReview=false."
                    $artifactValidationOk = $false
                    $metricComparisonEvidence["readyForReview"] = $false
                }
                if ($candidateComparisons -le 0) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "metric-comparison-candidate-missing" "Artifact '$artifactId' does not compare any candidate metrics."
                    $artifactValidationOk = $false
                }
                if ($regressions -gt 0) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "metric-comparison-regression" "Artifact '$artifactId' reports $regressions metric regression(s)."
                    $artifactValidationOk = $false
                }
                if ($gateFailures -gt 0) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "metric-comparison-gate-failed" "Artifact '$artifactId' reports $gateFailures failed gate outcome(s)."
                    $artifactValidationOk = $false
                }
                if ($missingScenarioCount -gt 0) {
                    $scenarioText = if ($missingScenarios.Count -gt 0) { $missingScenarios -join ', ' } else { "$missingScenarioCount scenario(s)" }
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "metric-comparison-scenario-missing" "Artifact '$artifactId' is missing fixture metric coverage for benchmark scenario(s): $scenarioText."
                    $artifactValidationOk = $false
                }
                if ($missingCurrent -gt 0) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "metric-comparison-current-baseline-missing" "Artifact '$artifactId' is missing current-Zeus baseline coverage for $missingCurrent candidate scenario(s)."
                    $artifactValidationOk = $false
                }
                if ($missingThetis -gt 0) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "metric-comparison-thetis-baseline-missing" "Artifact '$artifactId' is missing Thetis-parity baseline coverage for $missingThetis candidate scenario(s)."
                    $artifactValidationOk = $false
                }
                if ($missingCandidate -gt 0) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "metric-comparison-candidate-coverage-missing" "Artifact '$artifactId' is missing candidate coverage for $missingCandidate benchmark scenario(s)."
                    $artifactValidationOk = $false
                }
                if ($missingMetrics -gt 0) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "metric-comparison-value-missing" "Artifact '$artifactId' is missing $missingMetrics baseline/candidate metric value(s)."
                    $artifactValidationOk = $false
                }

                if ($artifactValidationOk) {
                    $metricComparisonEvidence["status"] = "ready"
                }
                else {
                    $metricComparisonEvidence["status"] = "not-ready"
                }
            }

            $matrixRuns = @(Get-JsonArray $artifactJson "runs")
            $matrixTool = [string](Get-JsonValue $artifactJson "tool")
            if ($matrixTool -eq "run-dsp-live-diagnostics-matrix" -and $matrixRuns.Count -gt 0) {
                $matrixScenarioIds = New-Object System.Collections.Generic.List[string]
                $matrixFailedRunCount = 0
                $matrixNotReadyTraceCount = 0
                $matrixHardBlockerRunCount = 0
                $matrixWeakInputSampleCount = 0
                $matrixWeakRecoveredSampleCount = 0
                $matrixWeakDropoutSampleCount = 0
                $matrixHotMakeupSampleCount = 0
                $matrixExpectedSamples = [int](Get-NumericValueOrDefault (Get-JsonValue $artifactJson "samples"))

                foreach ($matrixRun in $matrixRuns) {
                    $runScenarioId = [string](Get-JsonValue $matrixRun "scenarioId")
                    if (-not [string]::IsNullOrWhiteSpace($runScenarioId)) {
                        $matrixScenarioIds.Add($runScenarioId) | Out-Null
                    }

                    if (-not (Test-Truthy (Get-JsonValue $matrixRun "ok"))) {
                        $matrixFailedRunCount++
                    }
                    if (-not (Test-Truthy (Get-JsonValue $matrixRun "readyForBenchmarkTrace"))) {
                        $matrixNotReadyTraceCount++
                    }
                    if ([int](Get-NumericValueOrDefault (Get-JsonValue $matrixRun "hardBlockerSampleCount")) -gt 0) {
                        $matrixHardBlockerRunCount++
                    }

                    $runOkSamples = [int](Get-NumericValueOrDefault (Get-JsonValue $matrixRun "okSampleCount"))
                    $runFailedSamples = [int](Get-NumericValueOrDefault (Get-JsonValue $matrixRun "failedSampleCount"))
                    if ($matrixExpectedSamples -gt 0 -and ($runOkSamples + $runFailedSamples) -ne $matrixExpectedSamples) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-matrix-report-run-sample-count-mismatch" "Artifact '$artifactId' run '$runScenarioId' reports okSampleCount+failedSampleCount=$($runOkSamples + $runFailedSamples), expected samples=$matrixExpectedSamples."
                        $artifactValidationOk = $false
                    }

                    $matrixWeakInputSampleCount += [int](Get-NumericValueOrDefault (Get-JsonValue $matrixRun "nr5WeakInputSampleCount"))
                    $matrixWeakRecoveredSampleCount += [int](Get-NumericValueOrDefault (Get-JsonValue $matrixRun "nr5WeakRecoveredSampleCount"))
                    $matrixWeakDropoutSampleCount += [int](Get-NumericValueOrDefault (Get-JsonValue $matrixRun "nr5WeakDropoutSampleCount"))
                    $matrixHotMakeupSampleCount += [int](Get-NumericValueOrDefault (Get-JsonValue $matrixRun "nr5HotMakeupSampleCount"))
                }

                $matrixScenarioCount = @($matrixScenarioIds.ToArray() | Select-Object -Unique).Count
                $reportedMatrixScenarioCount = [int](Get-NumericValueOrDefault (Get-JsonValue $artifactJson "scenarioCount"))
                $reportedMatrixFailedRunCount = [int](Get-NumericValueOrDefault (Get-JsonValue $artifactJson "failedRunCount"))
                $reportedMatrixNotReadyTraceCount = [int](Get-NumericValueOrDefault (Get-JsonValue $artifactJson "notReadyTraceCount"))
                $reportedMatrixHardBlockerRunCount = [int](Get-NumericValueOrDefault (Get-JsonValue $artifactJson "hardBlockerRunCount"))

                if ($reportedMatrixScenarioCount -ne $matrixScenarioCount) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-matrix-report-scenario-count-mismatch" "Artifact '$artifactId' reports scenarioCount=$reportedMatrixScenarioCount but runs cover $matrixScenarioCount scenario(s)."
                    $artifactValidationOk = $false
                }
                if ($reportedMatrixFailedRunCount -ne $matrixFailedRunCount) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-matrix-report-failed-count-mismatch" "Artifact '$artifactId' reports failedRunCount=$reportedMatrixFailedRunCount but runs contain $matrixFailedRunCount failed run(s)."
                    $artifactValidationOk = $false
                }
                if ($reportedMatrixNotReadyTraceCount -ne $matrixNotReadyTraceCount) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-matrix-report-not-ready-count-mismatch" "Artifact '$artifactId' reports notReadyTraceCount=$reportedMatrixNotReadyTraceCount but runs contain $matrixNotReadyTraceCount not-ready trace(s)."
                    $artifactValidationOk = $false
                }
                if ($reportedMatrixHardBlockerRunCount -ne $matrixHardBlockerRunCount) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-matrix-report-hard-blocker-count-mismatch" "Artifact '$artifactId' reports hardBlockerRunCount=$reportedMatrixHardBlockerRunCount but runs contain $matrixHardBlockerRunCount hard-blocker run(s)."
                    $artifactValidationOk = $false
                }

                $expectedMatrixCollectionReady = ($matrixFailedRunCount -eq 0)
                $expectedMatrixAcceptanceReady = ($matrixFailedRunCount -eq 0 -and $matrixNotReadyTraceCount -eq 0 -and $matrixHardBlockerRunCount -eq 0)
                if ((Test-Truthy (Get-JsonValue $artifactJson "collectionReady")) -ne $expectedMatrixCollectionReady) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-matrix-report-collection-ready-mismatch" "Artifact '$artifactId' collectionReady does not match failedRunCount."
                    $artifactValidationOk = $false
                }
                if ((Test-Truthy (Get-JsonValue $artifactJson "acceptanceReady")) -ne $expectedMatrixAcceptanceReady) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-matrix-report-acceptance-ready-mismatch" "Artifact '$artifactId' acceptanceReady does not match failed/not-ready/hard-blocker counts."
                    $artifactValidationOk = $false
                }

                foreach ($counterSpec in @(
                        @{ Name = "nr5WeakInputSampleCount"; Expected = $matrixWeakInputSampleCount },
                        @{ Name = "nr5WeakRecoveredSampleCount"; Expected = $matrixWeakRecoveredSampleCount },
                        @{ Name = "nr5WeakDropoutSampleCount"; Expected = $matrixWeakDropoutSampleCount },
                        @{ Name = "nr5HotMakeupSampleCount"; Expected = $matrixHotMakeupSampleCount }
                    )) {
                    $counterName = [string]$counterSpec["Name"]
                    $reportedCounter = [int](Get-NumericValueOrDefault (Get-JsonValue $artifactJson $counterName))
                    $expectedCounter = [int]$counterSpec["Expected"]
                    if ($reportedCounter -ne $expectedCounter) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-matrix-report-weak-counter-mismatch" "Artifact '$artifactId' reports $counterName=$reportedCounter but runs sum to $expectedCounter."
                        $artifactValidationOk = $false
                    }
                }

                $matrixIndexPath = [string](Get-JsonValue $artifactJson "indexPath")
                if ([string]::IsNullOrWhiteSpace($matrixIndexPath)) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-matrix-report-index-path-missing" "Artifact '$artifactId' does not declare indexPath."
                    $artifactValidationOk = $false
                }
                else {
                    if ([System.IO.Path]::IsPathRooted($matrixIndexPath)) {
                        Add-ValidationIssue $warnings "warning" "live-matrix-report-index-path-absolute" "Artifact '$artifactId' uses an absolute indexPath; relative paths keep capture bundles portable."
                    }

                    $resolvedMatrixIndexPath = Get-BundlePath $bundlePath $matrixIndexPath
                    if (-not (Test-Path -LiteralPath $resolvedMatrixIndexPath -PathType Leaf)) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-matrix-report-index-file-missing" "Artifact '$artifactId' references a missing trace index: $matrixIndexPath"
                        $artifactValidationOk = $false
                    }
                    else {
                        $matrixIndexSha256 = [string](Get-JsonValue $artifactJson "indexSha256")
                        if (-not [string]::IsNullOrWhiteSpace($matrixIndexSha256)) {
                            $actualMatrixIndexSha256 = Get-FileSha256 $resolvedMatrixIndexPath
                            if (-not [string]::Equals($matrixIndexSha256.Trim().ToLowerInvariant(), $actualMatrixIndexSha256, [StringComparison]::OrdinalIgnoreCase)) {
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-matrix-report-index-hash-mismatch" "Artifact '$artifactId' reports indexSha256='$matrixIndexSha256' but '$matrixIndexPath' hashes to '$actualMatrixIndexSha256'."
                                $artifactValidationOk = $false
                            }
                        }

                        $matrixIndexJson = $null
                        try {
                            $matrixIndexJson = Read-JsonFile $resolvedMatrixIndexPath
                        }
                        catch {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-matrix-report-index-json-invalid" $_.Exception.Message
                            $artifactValidationOk = $false
                        }

                        if ($null -ne $matrixIndexJson) {
                            $matrixIndexEntries = @(Get-JsonArray $matrixIndexJson "files")
                            foreach ($matrixRun in $matrixRuns) {
                                $runScenarioId = ConvertTo-LiveHistoryScenarioId ([string](Get-JsonValue $matrixRun "scenarioId"))
                                $runComparisonId = ConvertTo-ComparisonId ([string](Get-JsonValue $matrixRun "comparisonId"))
                                $runLabel = if (-not [string]::IsNullOrWhiteSpace($runScenarioId) -and -not [string]::IsNullOrWhiteSpace($runComparisonId)) { "$runScenarioId/$runComparisonId" } else { "unknown-run" }
                                $matchingIndexEntries = @($matrixIndexEntries | Where-Object {
                                        $entryScenarioIds = @(Get-ArtifactIndexFileScenarioIds $_ | ForEach-Object { ConvertTo-LiveHistoryScenarioId ([string]$_) })
                                        $entryComparisonIds = @(Get-ArtifactIndexFileExplicitComparisonIds $_)
                                        (Test-StringArrayContains $entryScenarioIds $runScenarioId) -and
                                        (Test-StringArrayContains $entryComparisonIds $runComparisonId)
                                    })

                                if ($matchingIndexEntries.Count -eq 0) {
                                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-matrix-report-run-index-entry-missing" "Artifact '$artifactId' run '$runLabel' has no matching trace-index entry in '$matrixIndexPath'."
                                    $artifactValidationOk = $false
                                    continue
                                }
                                if ($matchingIndexEntries.Count -gt 1) {
                                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-matrix-report-run-index-entry-ambiguous" "Artifact '$artifactId' run '$runLabel' matches $($matchingIndexEntries.Count) trace-index entries in '$matrixIndexPath'."
                                    $artifactValidationOk = $false
                                    continue
                                }

                                $matchingIndexEntry = $matchingIndexEntries[0]
                                $indexJsonlPath = Get-ArtifactIndexFilePath $matchingIndexEntry
                                $runJsonlPath = [string](Get-JsonValue $matrixRun "jsonlPath")
                                if ([string]::IsNullOrWhiteSpace($indexJsonlPath) -or [string]::IsNullOrWhiteSpace($runJsonlPath) -or -not (Test-ComparableEvidencePathSame -BundlePath $bundlePath -ExpectedPath $indexJsonlPath -ActualPath $runJsonlPath)) {
                                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-matrix-report-run-jsonl-path-mismatch" "Artifact '$artifactId' run '$runLabel' jsonlPath='$runJsonlPath' does not match trace-index path='$indexJsonlPath'."
                                    $artifactValidationOk = $false
                                }

                                $indexSummaryPath = Get-ArtifactIndexFileSummaryPath $matchingIndexEntry
                                $runReportPath = [string](Get-JsonValue $matrixRun "reportPath")
                                if ([string]::IsNullOrWhiteSpace($indexSummaryPath) -or [string]::IsNullOrWhiteSpace($runReportPath) -or -not (Test-ComparableEvidencePathSame -BundlePath $bundlePath -ExpectedPath $indexSummaryPath -ActualPath $runReportPath)) {
                                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-matrix-report-run-summary-path-mismatch" "Artifact '$artifactId' run '$runLabel' reportPath='$runReportPath' does not match trace-index summaryPath='$indexSummaryPath'."
                                    $artifactValidationOk = $false
                                }

                                $indexSampleCount = Get-NumericValue (Get-JsonValue $matchingIndexEntry "sampleCount")
                                $runOkSamples = [int](Get-NumericValueOrDefault (Get-JsonValue $matrixRun "okSampleCount"))
                                $runFailedSamples = [int](Get-NumericValueOrDefault (Get-JsonValue $matrixRun "failedSampleCount"))
                                if ($null -eq $indexSampleCount) {
                                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-matrix-report-run-index-sample-count-missing" "Artifact '$artifactId' run '$runLabel' matched a trace-index entry without sampleCount."
                                    $artifactValidationOk = $false
                                }
                                else {
                                    if (($runOkSamples + $runFailedSamples) -ne [int]$indexSampleCount) {
                                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-matrix-report-run-index-sample-count-mismatch" "Artifact '$artifactId' run '$runLabel' reports okSampleCount+failedSampleCount=$($runOkSamples + $runFailedSamples) but trace-index sampleCount=$([int]$indexSampleCount)."
                                        $artifactValidationOk = $false
                                    }
                                }

                                foreach ($counterSpec in @(
                                        @{ Name = "nr5WeakInputSampleCount" },
                                        @{ Name = "nr5WeakRecoveredSampleCount" },
                                        @{ Name = "nr5WeakDropoutSampleCount" },
                                        @{ Name = "nr5HotMakeupSampleCount" }
                                    )) {
                                    $counterName = [string]$counterSpec["Name"]
                                    $indexCounterValue = Get-NumericValue (Get-JsonValue $matchingIndexEntry $counterName)
                                    if ($null -eq $indexCounterValue) {
                                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-matrix-report-run-index-weak-counter-missing" "Artifact '$artifactId' run '$runLabel' matched a trace-index entry without $counterName."
                                        $artifactValidationOk = $false
                                        continue
                                    }

                                    $runCounterValue = [int](Get-NumericValueOrDefault (Get-JsonValue $matrixRun $counterName))
                                    if ($runCounterValue -ne [int]$indexCounterValue) {
                                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-matrix-report-run-index-weak-counter-mismatch" "Artifact '$artifactId' run '$runLabel' reports $counterName=$runCounterValue but trace-index entry reports $([int]$indexCounterValue)."
                                        $artifactValidationOk = $false
                                    }
                                }
                            }
                        }
                    }
                }
            }

            if ($artifactKind -ieq "diagnostics-comparison-json" -or $artifactId -eq $liveTraceComparisonArtifactId) {
                $liveTraceComparisonEvidence["present"] = $true

                $tool = [string](Get-JsonValue $artifactJson "tool")
                if ($tool -ne "compare-dsp-live-diagnostics-traces" -and $tool -ne "compare-dsp-live-diagnostics-matrix") {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-comparison-tool-invalid" "Artifact '$artifactId' must be generated by compare-dsp-live-diagnostics-traces.ps1 or compare-dsp-live-diagnostics-matrix.ps1."
                    $artifactValidationOk = $false
                }

                if ($tool -eq "compare-dsp-live-diagnostics-traces") {
                    foreach ($inputSpec in @(
                            @{ Role = "baseline"; PathName = "baselinePath"; HashName = "baselineInputSha256" },
                            @{ Role = "candidate"; PathName = "candidatePath"; HashName = "candidateInputSha256" }
                        )) {
                        $role = [string]$inputSpec["Role"]
                        $inputPathName = [string]$inputSpec["PathName"]
                        $inputHashName = [string]$inputSpec["HashName"]
                        $inputPathValue = [string](Get-JsonValue $artifactJson $inputPathName)
                        if ([string]::IsNullOrWhiteSpace($inputPathValue)) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-comparison-$role-input-path-missing" "Artifact '$artifactId' does not declare $inputPathName."
                            $artifactValidationOk = $false
                            continue
                        }

                        $resolvedInputPath = Get-BundlePath $bundlePath $inputPathValue
                        if (-not (Test-Path -LiteralPath $resolvedInputPath -PathType Leaf)) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-comparison-$role-input-file-missing" "Artifact '$artifactId' references a missing $role input: $inputPathValue"
                            $artifactValidationOk = $false
                            continue
                        }

                        $declaredInputSha256 = [string](Get-JsonValue $artifactJson $inputHashName)
                        if ([string]::IsNullOrWhiteSpace($declaredInputSha256)) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-comparison-$role-input-hash-missing" "Artifact '$artifactId' does not declare $inputHashName."
                            $artifactValidationOk = $false
                        }
                        else {
                            $actualInputSha256 = Get-FileSha256 $resolvedInputPath
                            if (-not [string]::Equals($declaredInputSha256.Trim().ToLowerInvariant(), $actualInputSha256, [StringComparison]::OrdinalIgnoreCase)) {
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-comparison-$role-input-hash-mismatch" "Artifact '$artifactId' reports $inputHashName='$declaredInputSha256' but '$inputPathValue' hashes to '$actualInputSha256'."
                                $artifactValidationOk = $false
                            }
                        }
                    }
                }

                $readyForReview = Test-Truthy (Get-JsonValue $artifactJson "readyForReview")
                $regressions = [int](Get-JsonValue $artifactJson "regressionCount")
                $hardConstraintRegressions = [int](Get-JsonValue $artifactJson "hardConstraintRegressionCount")
                $gateFailures = [int](Get-JsonValue $artifactJson "gateFailureCount")
                $missingMetrics = [int](Get-JsonValue $artifactJson "missingMetricValueCount")
                $candidateComparisonsValue = Get-JsonValue $artifactJson "candidateComparisonCount"
                $candidateComparisons = if ($null -eq $candidateComparisonsValue) { 1 } else { [int]$candidateComparisonsValue }
                $failedComparisons = [int](Get-JsonValue $artifactJson "failedComparisonCount")
                $missingBaselines = [int](Get-JsonValue $artifactJson "missingBaselineCount")
                $missingCandidates = [int](Get-JsonValue $artifactJson "missingCandidateCount")
                $metricRegressionDetails = @(Get-JsonArray $artifactJson "metricRegressionDetails")
                $hardConstraintRegressionDetails = @(Get-JsonArray $artifactJson "hardConstraintRegressionDetails")
                $gateFailureDetails = @(Get-JsonArray $artifactJson "gateFailureDetails")
                $missingMetricDetails = @(Get-JsonArray $artifactJson "missingMetricDetails")
                $metricRegressionSafetyClassCounts = @(Get-LiveTraceSafetyClassCounts `
                    -ArtifactJson $artifactJson `
                    -CountsName "metricRegressionSafetyClassCounts" `
                    -DetailsName "metricRegressionDetails" `
                    -MetricComparisonsVerdict "regression")
                $metricMissingSafetyClassCounts = @(Get-LiveTraceSafetyClassCounts `
                    -ArtifactJson $artifactJson `
                    -CountsName "metricMissingSafetyClassCounts" `
                    -DetailsName "missingMetricDetails" `
                    -MetricComparisonsVerdict "missing")
                $bundleRelativePaths = Test-Truthy (Get-JsonValue $artifactJson "bundleRelativePaths")
                $absolutePaths = if ($tool -eq "compare-dsp-live-diagnostics-traces" -or $tool -eq "compare-dsp-live-diagnostics-matrix") { @(Get-LiveTraceComparisonAbsolutePaths $artifactJson) } else { @() }
                $nr5WeakSignalSummary = Get-JsonValue $artifactJson "nr5WeakSignalComparisonSummary"
                if ($null -eq $nr5WeakSignalSummary) {
                    $nr5WeakSignalSummary = Get-JsonValue $artifactJson "nr5WeakSignalComparison"
                }

                if ($tool -eq "compare-dsp-live-diagnostics-matrix") {
                    $matrixScenarioComparisons = @(Get-JsonArray $artifactJson "scenarioComparisons")
                    $matrixMissingScenarioPairs = @(Get-JsonArray $artifactJson "missingScenarioPairs")
                    $expectedMatrixCandidateComparisons = $matrixScenarioComparisons.Count
                    $expectedMatrixFailedComparisons = 0
                    $expectedMatrixRegressions = 0
                    $expectedMatrixImprovements = 0
                    $expectedMatrixHardConstraintRegressions = 0
                    $expectedMatrixGateFailures = 0
                    $expectedMatrixMissingMetrics = 0
                    $expectedMatrixMissingBaselines = @($matrixMissingScenarioPairs | Where-Object { [string](Get-JsonValue $_ "missing") -eq "baseline" }).Count
                    $expectedMatrixMissingCandidates = @($matrixMissingScenarioPairs | Where-Object { [string](Get-JsonValue $_ "missing") -eq "candidate" }).Count

                    foreach ($scenarioComparison in $matrixScenarioComparisons) {
                        if (-not [string]::IsNullOrWhiteSpace([string](Get-JsonValue $scenarioComparison "error"))) {
                            $expectedMatrixFailedComparisons++
                        }
                        $expectedMatrixRegressions += [int](Get-NumericValueOrDefault (Get-JsonValue $scenarioComparison "regressionCount"))
                        $expectedMatrixImprovements += [int](Get-NumericValueOrDefault (Get-JsonValue $scenarioComparison "improvementCount"))
                        $expectedMatrixHardConstraintRegressions += [int](Get-NumericValueOrDefault (Get-JsonValue $scenarioComparison "hardConstraintRegressionCount"))
                        $expectedMatrixGateFailures += [int](Get-NumericValueOrDefault (Get-JsonValue $scenarioComparison "gateFailureCount"))
                        $expectedMatrixMissingMetrics += [int](Get-NumericValueOrDefault (Get-JsonValue $scenarioComparison "missingMetricValueCount"))
                    }

                    if ($matrixScenarioComparisons.Count -eq 0) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-comparison-scenario-comparisons-missing" "Artifact '$artifactId' matrix report does not include scenarioComparisons."
                        $artifactValidationOk = $false
                    }

                    foreach ($countSpec in @(
                            @{ Name = "candidateComparisonCount"; Reported = $candidateComparisons; Expected = $expectedMatrixCandidateComparisons; Code = "live-trace-comparison-matrix-candidate-count-mismatch" },
                            @{ Name = "failedComparisonCount"; Reported = $failedComparisons; Expected = $expectedMatrixFailedComparisons; Code = "live-trace-comparison-matrix-failed-count-mismatch" },
                            @{ Name = "missingBaselineCount"; Reported = $missingBaselines; Expected = $expectedMatrixMissingBaselines; Code = "live-trace-comparison-matrix-missing-baseline-count-mismatch" },
                            @{ Name = "missingCandidateCount"; Reported = $missingCandidates; Expected = $expectedMatrixMissingCandidates; Code = "live-trace-comparison-matrix-missing-candidate-count-mismatch" },
                            @{ Name = "regressionCount"; Reported = $regressions; Expected = $expectedMatrixRegressions; Code = "live-trace-comparison-matrix-regression-count-mismatch" },
                            @{ Name = "improvementCount"; Reported = [int](Get-NumericValueOrDefault (Get-JsonValue $artifactJson "improvementCount")); Expected = $expectedMatrixImprovements; Code = "live-trace-comparison-matrix-improvement-count-mismatch" },
                            @{ Name = "hardConstraintRegressionCount"; Reported = $hardConstraintRegressions; Expected = $expectedMatrixHardConstraintRegressions; Code = "live-trace-comparison-matrix-hard-constraint-count-mismatch" },
                            @{ Name = "gateFailureCount"; Reported = $gateFailures; Expected = $expectedMatrixGateFailures; Code = "live-trace-comparison-matrix-gate-count-mismatch" },
                            @{ Name = "missingMetricValueCount"; Reported = $missingMetrics; Expected = $expectedMatrixMissingMetrics; Code = "live-trace-comparison-matrix-missing-metric-count-mismatch" }
                        )) {
                        $reportedCount = [int]$countSpec["Reported"]
                        $expectedCount = [int]$countSpec["Expected"]
                        if ($reportedCount -ne $expectedCount) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired ([string]$countSpec["Code"]) "Artifact '$artifactId' reports $([string]$countSpec["Name"])=$reportedCount but scenario rows/missing pairs produce $expectedCount."
                            $artifactValidationOk = $false
                        }
                    }

                    $expectedMatrixReadyForReview = ($expectedMatrixCandidateComparisons -gt 0 -and
                        $expectedMatrixFailedComparisons -eq 0 -and
                        $expectedMatrixMissingBaselines -eq 0 -and
                        $expectedMatrixMissingCandidates -eq 0 -and
                        $expectedMatrixRegressions -eq 0 -and
                        $expectedMatrixHardConstraintRegressions -eq 0 -and
                        $expectedMatrixGateFailures -eq 0 -and
                        $expectedMatrixMissingMetrics -eq 0)
                    if ($readyForReview -ne $expectedMatrixReadyForReview) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-comparison-matrix-ready-mismatch" "Artifact '$artifactId' readyForReview=$readyForReview does not match scenario rows/missing pairs readiness $expectedMatrixReadyForReview."
                        $artifactValidationOk = $false
                    }

                    $comparisonIndexEvidenceByRole = @{}
                    foreach ($indexSpec in @(
                            @{ Role = "baseline"; PathName = "baselineIndexPath"; HashName = "baselineIndexSha256"; RootPathName = "baselineRootPath" },
                            @{ Role = "candidate"; PathName = "candidateIndexPath"; HashName = "candidateIndexSha256"; RootPathName = "candidateRootPath" }
                        )) {
                        $role = [string]$indexSpec["Role"]
                        $indexPathName = [string]$indexSpec["PathName"]
                        $indexHashName = [string]$indexSpec["HashName"]
                        $rootPathName = [string]$indexSpec["RootPathName"]
                        $indexPathValue = [string](Get-JsonValue $artifactJson $indexPathName)
                        if ([string]::IsNullOrWhiteSpace($indexPathValue)) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-comparison-$role-index-path-missing" "Artifact '$artifactId' does not declare $indexPathName."
                            $artifactValidationOk = $false
                            continue
                        }

                        $resolvedComparisonIndexPath = Get-BundlePath $bundlePath $indexPathValue
                        if (-not (Test-Path -LiteralPath $resolvedComparisonIndexPath -PathType Leaf)) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-comparison-$role-index-file-missing" "Artifact '$artifactId' references a missing $role trace index: $indexPathValue"
                            $artifactValidationOk = $false
                            continue
                        }

                        $declaredComparisonIndexSha256 = [string](Get-JsonValue $artifactJson $indexHashName)
                        if ([string]::IsNullOrWhiteSpace($declaredComparisonIndexSha256)) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-comparison-$role-index-hash-missing" "Artifact '$artifactId' does not declare $indexHashName."
                            $artifactValidationOk = $false
                        }
                        else {
                            $actualComparisonIndexSha256 = Get-FileSha256 $resolvedComparisonIndexPath
                            if (-not [string]::Equals($declaredComparisonIndexSha256.Trim().ToLowerInvariant(), $actualComparisonIndexSha256, [StringComparison]::OrdinalIgnoreCase)) {
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-comparison-$role-index-hash-mismatch" "Artifact '$artifactId' reports $indexHashName='$declaredComparisonIndexSha256' but '$indexPathValue' hashes to '$actualComparisonIndexSha256'."
                                $artifactValidationOk = $false
                            }
                        }

                        $comparisonIndexJson = $null
                        try {
                            $comparisonIndexJson = Read-JsonFile $resolvedComparisonIndexPath
                        }
                        catch {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-comparison-$role-index-json-invalid" $_.Exception.Message
                            $artifactValidationOk = $false
                        }

                        if ($null -ne $comparisonIndexJson) {
                            $rootPathValue = [string](Get-JsonValue $artifactJson $rootPathName)
                            $comparisonIndexRoot = if ([string]::IsNullOrWhiteSpace($rootPathValue)) {
                                Get-LiveTraceComparisonIndexRoot $resolvedComparisonIndexPath
                            }
                            else {
                                Get-BundlePath $bundlePath $rootPathValue
                            }
                            $comparisonIndexEvidenceByRole[$role] = [ordered]@{
                                path = $indexPathValue
                                resolvedPath = $resolvedComparisonIndexPath
                                root = $comparisonIndexRoot
                                entries = @(Get-JsonArray $comparisonIndexJson "files")
                            }
                        }
                    }

                    foreach ($scenarioComparison in $matrixScenarioComparisons) {
                        $scenarioId = [string](Get-JsonValue $scenarioComparison "scenarioId")
                        $baselineComparisonId = [string](Get-JsonValue $scenarioComparison "baselineComparisonId")
                        $candidateComparisonId = [string](Get-JsonValue $scenarioComparison "candidateComparisonId")
                        $scenarioLabel = if (-not [string]::IsNullOrWhiteSpace($scenarioId) -and -not [string]::IsNullOrWhiteSpace($candidateComparisonId)) { "$scenarioId/$candidateComparisonId" } else { "unknown-scenario" }
                        $normalizedScenarioId = ConvertTo-LiveHistoryScenarioId $scenarioId

                        foreach ($roleSpec in @(
                                @{ Role = "baseline"; ComparisonId = $baselineComparisonId; InputPathName = "baselineInputPath" },
                                @{ Role = "candidate"; ComparisonId = $candidateComparisonId; InputPathName = "candidateInputPath" }
                            )) {
                            $role = [string]$roleSpec["Role"]
                            if (-not $comparisonIndexEvidenceByRole.ContainsKey($role)) {
                                continue
                            }

                            $roleIndexEvidence = $comparisonIndexEvidenceByRole[$role]
                            $roleComparisonId = ConvertTo-ComparisonId ([string]$roleSpec["ComparisonId"])
                            $roleInputPathName = [string]$roleSpec["InputPathName"]
                            $recordInputPath = [string](Get-JsonValue $scenarioComparison $roleInputPathName)
                            $matchingIndexEntries = @($roleIndexEvidence["entries"] | Where-Object {
                                    $entryScenarioIds = @(Get-ArtifactIndexFileScenarioIds $_ | ForEach-Object { ConvertTo-LiveHistoryScenarioId ([string]$_) })
                                    $entryComparisonIds = @(Get-LiveTraceComparisonIndexEntryComparisonIds $_)
                                    $entryComparisonMatches = if ([string]::IsNullOrWhiteSpace($roleComparisonId)) {
                                        @($entryComparisonIds | Where-Object { [string]::IsNullOrWhiteSpace([string]$_) }).Count -gt 0
                                    }
                                    else {
                                        Test-StringArrayContains $entryComparisonIds $roleComparisonId
                                    }
                                    (Test-StringArrayContains $entryScenarioIds $normalizedScenarioId) -and
                                    $entryComparisonMatches
                                })

                            if ($matchingIndexEntries.Count -eq 0) {
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-comparison-scenario-$role-index-entry-missing" "Artifact '$artifactId' scenario '$scenarioLabel' has no matching $role trace-index entry for scenario '$scenarioId' comparison '$([string]$roleSpec["ComparisonId"])'."
                                $artifactValidationOk = $false
                                continue
                            }
                            if ($matchingIndexEntries.Count -gt 1) {
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-comparison-scenario-$role-index-entry-ambiguous" "Artifact '$artifactId' scenario '$scenarioLabel' matches $($matchingIndexEntries.Count) $role trace-index entries for scenario '$scenarioId' comparison '$([string]$roleSpec["ComparisonId"])'."
                                $artifactValidationOk = $false
                                continue
                            }

                            $matchingIndexEntry = $matchingIndexEntries[0]
                            $indexTracePath = Resolve-LiveTraceComparisonIndexEntryPath -IndexRoot ([string]$roleIndexEvidence["root"]) -Path (Get-ArtifactIndexFilePath $matchingIndexEntry)
                            $indexSummaryPath = Resolve-LiveTraceComparisonIndexEntryPath -IndexRoot ([string]$roleIndexEvidence["root"]) -Path (Get-ArtifactIndexFileSummaryPath $matchingIndexEntry)
                            $indexInputPath = if (-not [string]::IsNullOrWhiteSpace($indexSummaryPath) -and (Test-Path -LiteralPath $indexSummaryPath -PathType Leaf)) { $indexSummaryPath } else { $indexTracePath }
                            $indexInputSha256 = if (-not [string]::IsNullOrWhiteSpace($indexSummaryPath) -and (Test-Path -LiteralPath $indexSummaryPath -PathType Leaf)) { Get-ArtifactIndexFileSummarySha256 $matchingIndexEntry } else { Get-ArtifactIndexFileSha256 $matchingIndexEntry }

                            if ([string]::IsNullOrWhiteSpace($recordInputPath) -or [string]::IsNullOrWhiteSpace($indexInputPath) -or -not (Test-ComparableEvidencePathSame -BundlePath $bundlePath -ExpectedPath $indexInputPath -ActualPath $recordInputPath)) {
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-comparison-scenario-$role-index-input-path-mismatch" "Artifact '$artifactId' scenario '$scenarioLabel' $roleInputPathName='$recordInputPath' does not match selected $role trace-index input path '$indexInputPath'."
                                $artifactValidationOk = $false
                            }

                            if ([string]::IsNullOrWhiteSpace($indexInputSha256)) {
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-comparison-scenario-$role-index-input-hash-missing" "Artifact '$artifactId' scenario '$scenarioLabel' matched a $role trace-index entry without a hash for selected input '$indexInputPath'."
                                $artifactValidationOk = $false
                            }
                            elseif (-not [string]::IsNullOrWhiteSpace($indexInputPath) -and (Test-Path -LiteralPath $indexInputPath -PathType Leaf)) {
                                $actualIndexInputSha256 = Get-FileSha256 $indexInputPath
                                if (-not [string]::Equals($indexInputSha256.Trim().ToLowerInvariant(), $actualIndexInputSha256, [StringComparison]::OrdinalIgnoreCase)) {
                                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-comparison-scenario-$role-index-input-hash-mismatch" "Artifact '$artifactId' scenario '$scenarioLabel' $role trace-index entry declares selected input hash '$indexInputSha256' but '$indexInputPath' hashes to '$actualIndexInputSha256'."
                                    $artifactValidationOk = $false
                                }
                            }
                        }

                        $scenarioReportPath = [string](Get-JsonValue $scenarioComparison "reportPath")
                        if ([string]::IsNullOrWhiteSpace($scenarioReportPath)) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-comparison-scenario-report-path-missing" "Artifact '$artifactId' scenario '$scenarioLabel' does not declare reportPath."
                            $artifactValidationOk = $false
                            continue
                        }

                        $resolvedScenarioReportPath = Get-BundlePath $bundlePath $scenarioReportPath
                        if (-not (Test-Path -LiteralPath $resolvedScenarioReportPath -PathType Leaf)) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-comparison-scenario-report-file-missing" "Artifact '$artifactId' scenario '$scenarioLabel' references a missing scenario report: $scenarioReportPath"
                            $artifactValidationOk = $false
                            continue
                        }

                        $declaredScenarioReportSha256 = [string](Get-JsonValue $scenarioComparison "reportSha256")
                        if ([string]::IsNullOrWhiteSpace($declaredScenarioReportSha256)) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-comparison-scenario-report-hash-missing" "Artifact '$artifactId' scenario '$scenarioLabel' does not declare reportSha256."
                            $artifactValidationOk = $false
                        }
                        else {
                            $actualScenarioReportSha256 = Get-FileSha256 $resolvedScenarioReportPath
                            if (-not [string]::Equals($declaredScenarioReportSha256.Trim().ToLowerInvariant(), $actualScenarioReportSha256, [StringComparison]::OrdinalIgnoreCase)) {
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-comparison-scenario-report-hash-mismatch" "Artifact '$artifactId' scenario '$scenarioLabel' reports reportSha256='$declaredScenarioReportSha256' but '$scenarioReportPath' hashes to '$actualScenarioReportSha256'."
                                $artifactValidationOk = $false
                            }
                        }

                        $scenarioReportJson = $null
                        try {
                            $scenarioReportJson = Read-JsonFile $resolvedScenarioReportPath
                        }
                        catch {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-comparison-scenario-report-json-invalid" $_.Exception.Message
                            $artifactValidationOk = $false
                        }

                        if ($null -ne $scenarioReportJson) {
                            if ([string](Get-JsonValue $scenarioReportJson "tool") -ne "compare-dsp-live-diagnostics-traces") {
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-comparison-scenario-report-tool-invalid" "Artifact '$artifactId' scenario '$scenarioLabel' report '$scenarioReportPath' was not generated by compare-dsp-live-diagnostics-traces.ps1."
                                $artifactValidationOk = $false
                            }

                            foreach ($inputSpec in @(
                                    @{ Role = "baseline"; PathName = "baselinePath"; HashName = "baselineInputSha256" },
                                    @{ Role = "candidate"; PathName = "candidatePath"; HashName = "candidateInputSha256" }
                                )) {
                                $inputRole = [string]$inputSpec["Role"]
                                $inputPathName = [string]$inputSpec["PathName"]
                                $inputHashName = [string]$inputSpec["HashName"]
                                $scenarioInputPath = [string](Get-JsonValue $scenarioReportJson $inputPathName)
                                if ([string]::IsNullOrWhiteSpace($scenarioInputPath)) {
                                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-comparison-scenario-report-$inputRole-input-path-missing" "Artifact '$artifactId' scenario '$scenarioLabel' report '$scenarioReportPath' does not declare $inputPathName."
                                    $artifactValidationOk = $false
                                    continue
                                }

                                $resolvedScenarioInputPath = Get-BundlePath $bundlePath $scenarioInputPath
                                if (-not (Test-Path -LiteralPath $resolvedScenarioInputPath -PathType Leaf)) {
                                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-comparison-scenario-report-$inputRole-input-file-missing" "Artifact '$artifactId' scenario '$scenarioLabel' report '$scenarioReportPath' references a missing $inputRole input: $scenarioInputPath"
                                    $artifactValidationOk = $false
                                    continue
                                }

                                $declaredScenarioInputSha256 = [string](Get-JsonValue $scenarioReportJson $inputHashName)
                                if ([string]::IsNullOrWhiteSpace($declaredScenarioInputSha256)) {
                                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-comparison-scenario-report-$inputRole-input-hash-missing" "Artifact '$artifactId' scenario '$scenarioLabel' report '$scenarioReportPath' does not declare $inputHashName."
                                    $artifactValidationOk = $false
                                }
                                else {
                                    $actualScenarioInputSha256 = Get-FileSha256 $resolvedScenarioInputPath
                                    if (-not [string]::Equals($declaredScenarioInputSha256.Trim().ToLowerInvariant(), $actualScenarioInputSha256, [StringComparison]::OrdinalIgnoreCase)) {
                                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-comparison-scenario-report-$inputRole-input-hash-mismatch" "Artifact '$artifactId' scenario '$scenarioLabel' report '$scenarioReportPath' reports $inputHashName='$declaredScenarioInputSha256' but '$scenarioInputPath' hashes to '$actualScenarioInputSha256'."
                                        $artifactValidationOk = $false
                                    }
                                }
                            }

                            if ((Test-Truthy (Get-JsonValue $scenarioComparison "readyForReview")) -ne (Test-Truthy (Get-JsonValue $scenarioReportJson "readyForReview"))) {
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-comparison-scenario-report-ready-mismatch" "Artifact '$artifactId' scenario '$scenarioLabel' readyForReview does not match '$scenarioReportPath'."
                                $artifactValidationOk = $false
                            }

                            foreach ($countField in @("regressionCount", "improvementCount", "hardConstraintRegressionCount", "gateFailureCount", "missingMetricValueCount")) {
                                $recordCount = [int](Get-NumericValueOrDefault (Get-JsonValue $scenarioComparison $countField))
                                $reportCount = [int](Get-NumericValueOrDefault (Get-JsonValue $scenarioReportJson $countField))
                                if ($recordCount -ne $reportCount) {
                                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-comparison-scenario-report-count-mismatch" "Artifact '$artifactId' scenario '$scenarioLabel' reports $countField=$recordCount but '$scenarioReportPath' reports $reportCount."
                                    $artifactValidationOk = $false
                                }
                            }

                            $recordBaselinePath = [string](Get-JsonValue $scenarioComparison "baselineInputPath")
                            $reportBaselinePath = [string](Get-JsonValue $scenarioReportJson "baselinePath")
                            if ([string]::IsNullOrWhiteSpace($recordBaselinePath) -or [string]::IsNullOrWhiteSpace($reportBaselinePath) -or -not (Test-ComparableEvidencePathSame -BundlePath $bundlePath -ExpectedPath $recordBaselinePath -ActualPath $reportBaselinePath)) {
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-comparison-scenario-baseline-path-mismatch" "Artifact '$artifactId' scenario '$scenarioLabel' baselineInputPath='$recordBaselinePath' does not match scenario report baselinePath='$reportBaselinePath'."
                                $artifactValidationOk = $false
                            }

                            $recordCandidatePath = [string](Get-JsonValue $scenarioComparison "candidateInputPath")
                            $reportCandidatePath = [string](Get-JsonValue $scenarioReportJson "candidatePath")
                            if ([string]::IsNullOrWhiteSpace($recordCandidatePath) -or [string]::IsNullOrWhiteSpace($reportCandidatePath) -or -not (Test-ComparableEvidencePathSame -BundlePath $bundlePath -ExpectedPath $recordCandidatePath -ActualPath $reportCandidatePath)) {
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-comparison-scenario-candidate-path-mismatch" "Artifact '$artifactId' scenario '$scenarioLabel' candidateInputPath='$recordCandidatePath' does not match scenario report candidatePath='$reportCandidatePath'."
                                $artifactValidationOk = $false
                            }
                        }
                    }
                }

                $liveTraceComparisonEvidence["readyForReview"] = $readyForReview
                $liveTraceComparisonEvidence["candidateComparisonCount"] = $candidateComparisons
                $liveTraceComparisonEvidence["failedComparisonCount"] = $failedComparisons
                $liveTraceComparisonEvidence["missingBaselineCount"] = $missingBaselines
                $liveTraceComparisonEvidence["missingCandidateCount"] = $missingCandidates
                $liveTraceComparisonEvidence["regressionCount"] = $regressions
                $liveTraceComparisonEvidence["hardConstraintRegressionCount"] = $hardConstraintRegressions
                $liveTraceComparisonEvidence["gateFailureCount"] = $gateFailures
                $liveTraceComparisonEvidence["missingMetricValueCount"] = $missingMetrics
                $liveTraceComparisonEvidence["metricRegressionDetailCount"] = $metricRegressionDetails.Count
                $liveTraceComparisonEvidence["hardConstraintRegressionDetailCount"] = $hardConstraintRegressionDetails.Count
                $liveTraceComparisonEvidence["gateFailureDetailCount"] = $gateFailureDetails.Count
                $liveTraceComparisonEvidence["missingMetricDetailCount"] = $missingMetricDetails.Count
                $liveTraceComparisonEvidence["metricRegressionSafetyClassCounts"] = @($metricRegressionSafetyClassCounts)
                $liveTraceComparisonEvidence["metricMissingSafetyClassCounts"] = @($metricMissingSafetyClassCounts)
                $liveTraceComparisonEvidence["pumpingRegressionCount"] = Get-SafetyClassCount $metricRegressionSafetyClassCounts "pumping"
                $liveTraceComparisonEvidence["weakSignalRegressionCount"] = Get-SafetyClassCount $metricRegressionSafetyClassCounts "weak-signal"
                $liveTraceComparisonEvidence["clippingRegressionCount"] = Get-SafetyClassCount $metricRegressionSafetyClassCounts "clipping"
                $liveTraceComparisonEvidence["readinessRegressionCount"] = Get-SafetyClassCount $metricRegressionSafetyClassCounts "readiness"
                $liveTraceComparisonEvidence["hardGateRegressionCount"] = Get-SafetyClassCount $metricRegressionSafetyClassCounts "hard-gate"
                $liveTraceComparisonEvidence["freshnessRegressionCount"] = Get-SafetyClassCount $metricRegressionSafetyClassCounts "freshness"
                $liveTraceComparisonEvidence["frontEndRegressionCount"] = Get-SafetyClassCount $metricRegressionSafetyClassCounts "front-end"
                $liveTraceComparisonEvidence["audioPathRegressionCount"] = Get-SafetyClassCount $metricRegressionSafetyClassCounts "audio-path"
                $liveTraceComparisonEvidence["toolingRegressionCount"] = Get-SafetyClassCount $metricRegressionSafetyClassCounts "tooling"
                $liveTraceComparisonEvidence["bundleRelativePaths"] = $bundleRelativePaths
                $liveTraceComparisonEvidence["absolutePathCount"] = $absolutePaths.Count
                $liveTraceComparisonEvidence["nr5WeakSignalComparisonSummary"] = $nr5WeakSignalSummary
                if ($null -ne $nr5WeakSignalSummary) {
                    $liveTraceComparisonEvidence["nr5WeakInputSampleDelta"] = [int](Get-NumericValue (Get-JsonValue $nr5WeakSignalSummary "weakInputSampleDelta"))
                    $liveTraceComparisonEvidence["nr5WeakRecoveredSampleDelta"] = [int](Get-NumericValue (Get-JsonValue $nr5WeakSignalSummary "weakRecoveredSampleDelta"))
                    $liveTraceComparisonEvidence["nr5WeakDropoutSampleDelta"] = [int](Get-NumericValue (Get-JsonValue $nr5WeakSignalSummary "weakDropoutSampleDelta"))
                    $liveTraceComparisonEvidence["nr5HotMakeupSampleDelta"] = [int](Get-NumericValue (Get-JsonValue $nr5WeakSignalSummary "hotMakeupSampleDelta"))
                    $liveTraceComparisonEvidence["nr5WeakRecoveryPctDelta"] = [Math]::Round([double](Get-NumericValue (Get-JsonValue $nr5WeakSignalSummary "weakRecoveryPctDelta")), 3)
                    $liveTraceComparisonEvidence["nr5OutputMovementDbDelta"] = [Math]::Round([double](Get-NumericValueOrDefault (Get-JsonValue $nr5WeakSignalSummary "outputMovementDbDelta")), 3)
                    $liveTraceComparisonEvidence["nr5MakeupMovementDbDelta"] = [Math]::Round([double](Get-NumericValueOrDefault (Get-JsonValue $nr5WeakSignalSummary "makeupMovementDbDelta")), 3)
                    $liveTraceComparisonEvidence["nr5MakeupMaxDbDelta"] = [Math]::Round([double](Get-NumericValueOrDefault (Get-JsonValue $nr5WeakSignalSummary "makeupMaxDbDelta")), 3)
                    $liveTraceComparisonEvidence["nr5RecoveryDriveMovementDelta"] = [Math]::Round([double](Get-NumericValueOrDefault (Get-JsonValue $nr5WeakSignalSummary "recoveryDriveMovementDelta")), 3)
                    $liveTraceComparisonEvidence["nr5TextureFillAverageDelta"] = [Math]::Round([double](Get-NumericValueOrDefault (Get-JsonValue $nr5WeakSignalSummary "textureFillAverageDelta")), 3)
                    $liveTraceComparisonEvidence["nr5OutputMovementRegressionCount"] = Get-RegressionCountFromSummary $nr5WeakSignalSummary "outputMovementRegressionCount" "outputMovementRegression"
                    $liveTraceComparisonEvidence["nr5MakeupMovementRegressionCount"] = Get-RegressionCountFromSummary $nr5WeakSignalSummary "makeupMovementRegressionCount" "makeupMovementRegression"
                    $liveTraceComparisonEvidence["nr5MakeupMaxRegressionCount"] = Get-RegressionCountFromSummary $nr5WeakSignalSummary "makeupMaxRegressionCount" "makeupMaxRegression"
                    $liveTraceComparisonEvidence["nr5RecoveryDriveMovementRegressionCount"] = Get-RegressionCountFromSummary $nr5WeakSignalSummary "recoveryDriveMovementRegressionCount" "recoveryDriveMovementRegression"
                }

                if ($candidateComparisons -le 0) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-comparison-candidate-missing" "Artifact '$artifactId' does not compare any candidate live trace scenario."
                    $artifactValidationOk = $false
                }
                if (($tool -eq "compare-dsp-live-diagnostics-traces" -or $tool -eq "compare-dsp-live-diagnostics-matrix") -and -not $bundleRelativePaths) {
                    $comparisonScript = if ($tool -eq "compare-dsp-live-diagnostics-matrix") { "compare-dsp-live-diagnostics-matrix.ps1" } else { "compare-dsp-live-diagnostics-traces.ps1" }
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-comparison-paths-not-bundle-relative" "Artifact '$artifactId' report was not generated with bundleRelativePaths=true; rerun $comparisonScript with -BundleDir."
                    $artifactValidationOk = $false
                }
                if ($absolutePaths.Count -gt 0) {
                    $pathExamples = Get-PathExampleText $absolutePaths
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-comparison-absolute-paths" "Artifact '$artifactId' report contains $($absolutePaths.Count) absolute path(s); rerun with -BundleDir so capture bundles are portable. Examples: $pathExamples"
                    $artifactValidationOk = $false
                }
                if ($failedComparisons -gt 0) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-comparison-failed" "Artifact '$artifactId' reports $failedComparisons failed per-scenario comparison(s)."
                    $artifactValidationOk = $false
                }
                if ($missingBaselines -gt 0) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-comparison-baseline-missing" "Artifact '$artifactId' is missing $missingBaselines baseline scenario trace(s)."
                    $artifactValidationOk = $false
                }
                if ($missingCandidates -gt 0) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-comparison-candidate-scenario-missing" "Artifact '$artifactId' is missing $missingCandidates candidate scenario trace(s)."
                    $artifactValidationOk = $false
                }
                if (-not $readyForReview) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-comparison-not-ready" "Artifact '$artifactId' reports readyForReview=false."
                    $artifactValidationOk = $false
                }
                if ($regressions -gt 0) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-comparison-regression" "Artifact '$artifactId' reports $regressions live diagnostics metric regression(s)."
                    $artifactValidationOk = $false
                }
                if ($hardConstraintRegressions -gt 0) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-comparison-hard-constraint-regression" "Artifact '$artifactId' reports $hardConstraintRegressions hard live diagnostics constraint regression(s)."
                    $artifactValidationOk = $false
                }
                if ($gateFailures -gt 0) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-comparison-gate-failed" "Artifact '$artifactId' reports $gateFailures live diagnostics gate failure(s)."
                    $artifactValidationOk = $false
                }
                if ($missingMetrics -gt 0) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-comparison-value-missing" "Artifact '$artifactId' is missing $missingMetrics baseline/candidate metric value(s)."
                    $artifactValidationOk = $false
                }

                if ($artifactValidationOk) {
                    $liveTraceComparisonEvidence["status"] = "ready"
                }
                else {
                    $liveTraceComparisonEvidence["status"] = "not-ready"
                }
            }

            if ($artifactKind -ieq "diagnostics-history-json" -or $artifactId -eq $liveDiagnosticsHistoryArtifactId) {
                $liveDiagnosticsHistoryEvidence["present"] = $true

                $tool = [string](Get-JsonValue $artifactJson "tool")
                if ($tool -ne "summarize-dsp-live-diagnostics-history") {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-tool-invalid" "Artifact '$artifactId' must be generated by summarize-dsp-live-diagnostics-history.ps1."
                    $artifactValidationOk = $false
                }

                $schemaVersion = [int](Get-NumericValueOrDefault (Get-JsonValue $artifactJson "schemaVersion") 1)
                if ($tool -eq "summarize-dsp-live-diagnostics-history" -and $schemaVersion -ne 9) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-schema-version-mismatch" "Artifact '$artifactId' schemaVersion=$schemaVersion but summarize-dsp-live-diagnostics-history.ps1 currently emits schemaVersion=9."
                    $artifactValidationOk = $false
                }
                $traceCount = [int](Get-JsonValue $artifactJson "traceCount")
                $nr5TraceCount = [int](Get-JsonValue $artifactJson "nr5TraceCount")
                $readyTraceCount = [int](Get-JsonValue $artifactJson "readyTraceCount")
                $readyNr5TraceCount = [int](Get-NumericValueOrDefault (Get-JsonValue $artifactJson "readyNr5TraceCount"))
                $candidateReadyNr5TraceCount = [int](Get-NumericValueOrDefault (Get-JsonValue $artifactJson "candidateReadyNr5TraceCount"))
                $latestTrace = Get-JsonValue $artifactJson "latestTrace"
                $previousNr5Trace = Get-JsonValue $artifactJson "previousNr5Trace"
                $bestBalancedTrace = Get-JsonValue $artifactJson "bestBalancedTrace"
                $bestWeakSignalTrace = Get-JsonValue $artifactJson "bestWeakSignalTrace"
                $lowestPumpingTrace = Get-JsonValue $artifactJson "lowestPumpingTrace"
                $promotionDecision = Get-JsonValue $artifactJson "latestNr5Decision"
                $legacyPromotionDecision = Get-JsonValue $artifactJson "promotionDecision"
                if ($null -eq $promotionDecision) {
                    $promotionDecision = $legacyPromotionDecision
                }
                $readinessTrend = Get-JsonValue $artifactJson "latestVsPreviousNr5ReadinessGapTrend"
                $legacyReadinessTrend = Get-JsonValue $artifactJson "latestVsPreviousReadinessTrend"
                if ($null -eq $readinessTrend) {
                    $readinessTrend = $legacyReadinessTrend
                }
                $referenceReadinessTrend = Get-JsonValue $artifactJson "latestVsReferenceNr5ReadinessGapTrend"
                $tuningActionPlan = Get-JsonValue $artifactJson "latestTuningActionPlan"
                $liveExperimentPlan = Get-JsonValue $artifactJson "latestLiveExperimentPlan"
                $liveExperimentCoverage = Get-JsonValue $artifactJson "latestLiveExperimentCoverage"
                $latestVsPreviousNr5Delta = Get-JsonValue $artifactJson "latestVsPreviousNr5Delta"
                $thresholds = Get-JsonValue $artifactJson "thresholds"
                $reviewStatusCounts = @(Get-JsonArray $artifactJson "reviewStatusCounts")
                $traceRecords = @(Get-JsonArray $artifactJson "traces")
                $aggregateSafetyClassCounts = @(Get-JsonArray $artifactJson "aggregateSafetyClassCounts")
                $aggregateReadinessGaps = @(Get-JsonArray $artifactJson "aggregateReadinessGaps")
                if ($aggregateReadinessGaps.Count -eq 0) {
                    $aggregateReadinessGaps = @(Get-JsonArray $artifactJson "aggregateSafetyClassReadiness")
                }
                $bundleRelativePaths = Test-Truthy (Get-JsonValue $artifactJson "bundleRelativePaths")
                $absolutePaths = if ($tool -eq "summarize-dsp-live-diagnostics-history") { @(Get-LiveDiagnosticsHistoryAbsolutePaths $artifactJson) } else { @() }
                $recommendations = @(Get-JsonArray $artifactJson "recommendations")
                $candidatePromotionReady = Test-Truthy (Get-JsonValue $promotionDecision "candidatePromotionReady")
                $defaultBehaviorChangeReady = Test-Truthy (Get-JsonValue $promotionDecision "defaultBehaviorChangeReady")
                $promotionStatus = [string](Get-JsonValue $promotionDecision "status")
                $promotionBlockers = @(Get-JsonArray $promotionDecision "blockers")
                $promotionBlockerClasses = @(Get-JsonArray $promotionDecision "blockerClasses" | ForEach-Object { [string]$_ })
                $promotionBlockerClassCounts = @(Get-JsonArray $promotionDecision "blockerClassCounts")
                $promotionBlockerCountValue = Get-NumericValue (Get-JsonValue $promotionDecision "blockerCount")
                $promotionBlockerCount = if ($null -eq $promotionBlockerCountValue) { $promotionBlockers.Count } else { [int][Math]::Round($promotionBlockerCountValue) }
                $promotionRecommendedTraceId = [string](Get-JsonValue $promotionDecision "recommendedTraceId")
                $promotionRecommendedTraceRole = [string](Get-JsonValue $promotionDecision "recommendedTraceRole")
                $promotionReferenceTraceId = [string](Get-JsonValue $promotionDecision "referenceTraceId")
                $promotionReferenceTraceRole = [string](Get-JsonValue $promotionDecision "referenceTraceRole")
                $promotable = Test-Truthy (Get-JsonValue $promotionDecision "promotable")
                $validPromotionStatuses = @(
                    "ready-for-candidate-comparison",
                    "blocked-hard-gate",
                    "blocked-clipping",
                    "blocked-diagnostic-freshness",
                    "blocked-runtime-evidence",
                    "blocked-weak-and-pumping",
                    "blocked-pumping",
                    "blocked-weak-signal",
                    "blocked-safety-signals",
                    "no-nr5-history"
                )
                $latestPromotionBlockerClasses = @(Get-JsonArray $latestTrace "promotionBlockerClasses" | ForEach-Object { [string]$_ })
                $latestCandidateReady = Test-Truthy (Get-JsonValue $latestTrace "candidateComparisonReady")
                $actualTraceCount = $traceRecords.Count
                $actualNr5TraceCount = @($traceRecords | Where-Object { [int](Get-NumericValueOrDefault (Get-JsonValue $_ "nr5SampleCount")) -gt 0 }).Count
                $actualReadyTraceCount = @($traceRecords | Where-Object { Test-Truthy (Get-JsonValue $_ "readyForBenchmarkTrace") }).Count
                $actualReadyNr5TraceCount = @($traceRecords | Where-Object { [int](Get-NumericValueOrDefault (Get-JsonValue $_ "nr5SampleCount")) -gt 0 -and (Test-Truthy (Get-JsonValue $_ "readyForBenchmarkTrace")) }).Count
                $actualCandidateReadyNr5TraceCount = @($traceRecords | Where-Object { [int](Get-NumericValueOrDefault (Get-JsonValue $_ "nr5SampleCount")) -gt 0 -and (Test-Truthy (Get-JsonValue $_ "candidateComparisonReady")) }).Count
                $traceSourceCheckedCount = 0
                $traceSourceMissingCount = 0
                $traceSourceInvalidCount = 0
                $traceSourceJsonlMissingCount = 0
                $traceSourceSummaryHashPresentCount = 0
                $traceSourceSummaryHashMissingCount = 0
                $traceSourceSummaryHashMismatchCount = 0
                $traceSourceJsonlHashPresentCount = 0
                $traceSourceJsonlHashMissingCount = 0
                $traceSourceJsonlHashMismatchCount = 0
                $traceOrderingFieldsPresent = $false
                foreach ($trace in @($traceRecords)) {
                    if ($null -ne (Get-JsonValue $trace "traceSequenceUtc") -or $null -ne (Get-JsonValue $trace "sortKeySource")) {
                        $traceOrderingFieldsPresent = $true
                        break
                    }
                }
                if (-not $traceOrderingFieldsPresent) {
                    foreach ($trace in @($latestTrace, $previousNr5Trace, $bestBalancedTrace, $bestWeakSignalTrace, $lowestPumpingTrace)) {
                        if ($null -ne $trace -and ($null -ne (Get-JsonValue $trace "traceSequenceUtc") -or $null -ne (Get-JsonValue $trace "sortKeySource"))) {
                            $traceOrderingFieldsPresent = $true
                            break
                        }
                    }
                }
                $traceOrderingFieldsRequired = ($schemaVersion -ge 7 -or $traceOrderingFieldsPresent)
                $traceOrderingViolationCount = 0
                $validSortKeySources = @("trace-directory-timestamp", "completedUtc", "file-last-write-utc")
                $orderedTraceRecordsBySequence = @($traceRecords | Sort-Object `
                    @{ Expression = {
                        $sequence = Get-DateTimeOffsetValue (Get-JsonValue $_ "traceSequenceUtc")
                        if ($null -eq $sequence) { [Int64]::MinValue } else { $sequence.UtcDateTime.Ticks }
                    } }, `
                    @{ Expression = { [string](Get-JsonValue $_ "traceId") } })
                $orderedNr5TraceRecordsBySequence = @($orderedTraceRecordsBySequence | Where-Object { [int](Get-NumericValueOrDefault (Get-JsonValue $_ "nr5SampleCount")) -gt 0 })
                $expectedLatestTraceBySequence = $null
                if ($orderedNr5TraceRecordsBySequence.Count -gt 0) {
                    $expectedLatestTraceBySequence = $orderedNr5TraceRecordsBySequence[$orderedNr5TraceRecordsBySequence.Count - 1]
                }
                $expectedPreviousNr5TraceBySequence = $null
                if ($orderedNr5TraceRecordsBySequence.Count -gt 1) {
                    $expectedPreviousNr5TraceBySequence = $orderedNr5TraceRecordsBySequence[$orderedNr5TraceRecordsBySequence.Count - 2]
                }
                $allTraceSignals = New-Object System.Collections.Generic.List[object]
                $readinessFieldsPresent = ($aggregateReadinessGaps.Count -gt 0)
                foreach ($trace in @($traceRecords)) {
                    if (@(Get-JsonArray $trace "readinessGapSummary").Count -gt 0 -or @(Get-JsonArray $trace "safetyClassReadiness").Count -gt 0) {
                        $readinessFieldsPresent = $true
                    }
                    foreach ($signal in @(Get-JsonArray $trace "safetySignals")) {
                        $allTraceSignals.Add($signal) | Out-Null
                        foreach ($field in @("thresholdDirection", "unit", "readinessGap", "readinessMargin", "readinessGapUnit", "readinessGapScore")) {
                            if ($null -ne (Get-JsonValue $signal $field)) {
                                $readinessFieldsPresent = $true
                            }
                        }
                    }
                }
                $readinessFieldsRequired = ($schemaVersion -ge 2 -or $readinessFieldsPresent)
                $expectedAggregateSafetyClassCounts = @(New-LiveHistorySafetyClassCountsFromSignals -Signals @($allTraceSignals.ToArray()))
                $expectedAggregateReadinessGaps = @(New-LiveHistoryReadinessSummaryFromSignals -Signals @($allTraceSignals.ToArray()))
                $latestFullTrace = Get-LiveHistoryTraceRecordById -Records $traceRecords -TraceId ([string](Get-JsonValue $latestTrace "traceId"))
                $previousFullTrace = Get-LiveHistoryTraceRecordById -Records $traceRecords -TraceId ([string](Get-JsonValue $previousNr5Trace "traceId"))
                $expectedBestBalancedTrace = Select-LiveHistoryTraceForSummary -Records $traceRecords -Mode "best-balanced"
                $expectedBestWeakSignalTrace = Select-LiveHistoryTraceForSummary -Records $traceRecords -Mode "best-weak"
                $expectedLowestPumpingTrace = Select-LiveHistoryTraceForSummary -Records $traceRecords -Mode "lowest-pumping"
                $bestBalancedFullTrace = $expectedBestBalancedTrace
                $bestWeakSignalFullTrace = $expectedBestWeakSignalTrace
                $lowestPumpingFullTrace = $expectedLowestPumpingTrace
                $expectedPromotionDecision = New-LiveHistoryExpectedPromotionDecision -Latest $latestFullTrace -BestBalanced $bestBalancedFullTrace -BestWeak $bestWeakSignalFullTrace -LowestPumping $lowestPumpingFullTrace
                $expectedPromotionRecommendedTraceId = [string](Get-JsonValue $expectedPromotionDecision "recommendedTraceId")
                $expectedPromotionReferenceTraceId = [string](Get-JsonValue $expectedPromotionDecision "referenceTraceId")
                $expectedPromotionReferenceTraceRole = [string](Get-JsonValue $expectedPromotionDecision "referenceTraceRole")
                $expectedReadinessTrend = Add-LiveHistoryReadinessTrendContext `
                    -Trend (New-LiveHistoryReadinessTrend -Latest $latestFullTrace -Previous $previousFullTrace) `
                    -ComparisonScope "latest-vs-previous" `
                    -LatestTraceId ([string](Get-JsonValue $latestTrace "traceId")) `
                    -ReferenceTraceId ([string](Get-JsonValue $previousNr5Trace "traceId")) `
                    -ReferenceTraceRole "previous-nr5" `
                    -RecommendedTraceId $expectedPromotionRecommendedTraceId
                $referenceFullTrace = Get-LiveHistoryTraceRecordById -Records $traceRecords -TraceId $expectedPromotionReferenceTraceId
                $expectedReferenceReadinessTrend = Add-LiveHistoryReadinessTrendContext `
                    -Trend (New-LiveHistoryReadinessTrend -Latest $latestFullTrace -Previous $referenceFullTrace) `
                    -ComparisonScope "latest-vs-reference" `
                    -LatestTraceId ([string](Get-JsonValue $latestTrace "traceId")) `
                    -ReferenceTraceId $expectedPromotionReferenceTraceId `
                    -ReferenceTraceRole $expectedPromotionReferenceTraceRole `
                    -RecommendedTraceId $expectedPromotionRecommendedTraceId
                $readinessTrendFieldsPresent = ($null -ne $readinessTrend)
                $readinessTrendFieldsRequired = ($schemaVersion -ge 3 -or $readinessTrendFieldsPresent)
                $referenceTrendFieldsPresent = ($null -ne $referenceReadinessTrend)
                $referenceTrendFieldsRequired = ($schemaVersion -ge 4 -or $referenceTrendFieldsPresent)
                $tuningActionPlanFieldsPresent = ($null -ne $tuningActionPlan)
                $tuningActionPlanFieldsRequired = ($schemaVersion -ge 5 -or $tuningActionPlanFieldsPresent)
                $liveExperimentPlanFieldsPresent = ($null -ne $liveExperimentPlan)
                $liveExperimentPlanFieldsRequired = ($schemaVersion -ge 6 -or $liveExperimentPlanFieldsPresent)
                $liveExperimentCoverageFieldsPresent = ($null -ne $liveExperimentCoverage)
                $liveExperimentCoverageFieldsRequired = ($schemaVersion -ge 8 -or $liveExperimentCoverageFieldsPresent)
                $expectedTuningActionPlan = New-LiveHistoryExpectedTuningActionPlan -Latest $latestFullTrace -PromotionDecision $expectedPromotionDecision -LatestVsPreviousTrend $expectedReadinessTrend -LatestVsReferenceTrend $expectedReferenceReadinessTrend
                $expectedLiveExperimentPlan = New-LiveHistoryExpectedExperimentPlan -ActionPlan $expectedTuningActionPlan
                $expectedLiveExperimentCoverage = New-LiveHistoryExperimentCoverage -Plan $expectedLiveExperimentPlan -Records $traceRecords
                $expectedLatestVsPreviousNr5Delta = New-LiveHistoryExpectedLatestDelta -Latest $latestFullTrace -Previous $previousFullTrace
                $expectedThresholds = New-LiveHistoryExpectedThresholds
                $expectedReviewStatusCounts = @(New-LiveHistoryExpectedReviewStatusCounts -Records $traceRecords)
                $expectedRecommendations = @(New-LiveHistoryExpectedRecommendations -Latest $latestFullTrace -BestBalanced $bestBalancedFullTrace -BestWeak $bestWeakSignalFullTrace -LowestPumping $lowestPumpingFullTrace)

                $liveDiagnosticsHistoryEvidence["traceCount"] = $traceCount
                $liveDiagnosticsHistoryEvidence["nr5TraceCount"] = $nr5TraceCount
                $liveDiagnosticsHistoryEvidence["readyTraceCount"] = $readyTraceCount
                $liveDiagnosticsHistoryEvidence["readyNr5TraceCount"] = $readyNr5TraceCount
                $liveDiagnosticsHistoryEvidence["candidateReadyNr5TraceCount"] = $candidateReadyNr5TraceCount
                $liveDiagnosticsHistoryEvidence["latestTraceId"] = [string](Get-JsonValue $latestTrace "traceId")
                $liveDiagnosticsHistoryEvidence["latestReviewStatus"] = [string](Get-JsonValue $latestTrace "reviewStatus")
                $liveDiagnosticsHistoryEvidence["latestSafetyRiskScore"] = [int](Get-NumericValueOrDefault (Get-JsonValue $latestTrace "safetyRiskScore"))
                $liveDiagnosticsHistoryEvidence["latestTraceSequenceUtc"] = Format-DateTimeOffsetUtcString (Get-JsonValue $latestTrace "traceSequenceUtc")
                $liveDiagnosticsHistoryEvidence["latestTraceSortKeySource"] = [string](Get-JsonValue $latestTrace "sortKeySource")
                $liveDiagnosticsHistoryEvidence["bestBalancedTraceId"] = [string](Get-JsonValue $bestBalancedTrace "traceId")
                $liveDiagnosticsHistoryEvidence["bestWeakSignalTraceId"] = [string](Get-JsonValue $bestWeakSignalTrace "traceId")
                $liveDiagnosticsHistoryEvidence["lowestPumpingTraceId"] = [string](Get-JsonValue $lowestPumpingTrace "traceId")
                $liveDiagnosticsHistoryEvidence["promotionStatus"] = $promotionStatus
                $liveDiagnosticsHistoryEvidence["candidatePromotionReady"] = $candidatePromotionReady
                $liveDiagnosticsHistoryEvidence["promotionRecommendedTraceId"] = $promotionRecommendedTraceId
                $liveDiagnosticsHistoryEvidence["promotionRecommendedTraceRole"] = $promotionRecommendedTraceRole
                $liveDiagnosticsHistoryEvidence["promotionReferenceTraceId"] = $promotionReferenceTraceId
                $liveDiagnosticsHistoryEvidence["promotionBlockerCount"] = $promotionBlockerCount
                $liveDiagnosticsHistoryEvidence["promotionBlockerClasses"] = @($promotionBlockerClasses)
                $liveDiagnosticsHistoryEvidence["promotionNextAction"] = [string](Get-JsonValue $promotionDecision "nextAction")
                $liveDiagnosticsHistoryEvidence["aggregateSafetyClassCounts"] = @($aggregateSafetyClassCounts)
                $liveDiagnosticsHistoryEvidence["aggregateReadinessGaps"] = @($aggregateReadinessGaps)
                $liveDiagnosticsHistoryEvidence["readinessGapSignalCount"] = Get-LiveHistoryReadinessSummarySignalCount -Summary $aggregateReadinessGaps
                $liveDiagnosticsHistoryEvidence["readinessGapNumericSignalCount"] = Get-LiveHistoryReadinessSummaryNumericSignalCount -Summary $aggregateReadinessGaps
                $liveDiagnosticsHistoryEvidence["readinessGapMax"] = Get-LiveHistoryReadinessSummaryMax -Summary $aggregateReadinessGaps
                $liveDiagnosticsHistoryEvidence["readinessTrendStatus"] = [string](Get-JsonValue $readinessTrend "status")
                $liveDiagnosticsHistoryEvidence["readinessTrendPreviousTraceId"] = [string](Get-JsonValue $readinessTrend "previousTraceId")
                $liveDiagnosticsHistoryEvidence["readinessTrendSignalCount"] = [int](Get-NumericValueOrDefault (Get-JsonValue $readinessTrend "signalCount"))
                $liveDiagnosticsHistoryEvidence["readinessTrendImprovedSignalCount"] = [int](Get-NumericValueOrDefault (Get-JsonValue $readinessTrend "improvedSignalCount"))
                $liveDiagnosticsHistoryEvidence["readinessTrendRegressedSignalCount"] = [int](Get-NumericValueOrDefault (Get-JsonValue $readinessTrend "regressedSignalCount"))
                $liveDiagnosticsHistoryEvidence["readinessTrendResolvedSignalCount"] = [int](Get-NumericValueOrDefault (Get-JsonValue $readinessTrend "resolvedSignalCount"))
                $liveDiagnosticsHistoryEvidence["readinessTrendNewSignalCount"] = [int](Get-NumericValueOrDefault (Get-JsonValue $readinessTrend "newSignalCount"))
                $liveDiagnosticsHistoryEvidence["readinessTrendGapMaxDelta"] = Get-NumericValue (Get-JsonValue $readinessTrend "readinessGapMaxDelta")
                $liveDiagnosticsHistoryEvidence["referenceTrendStatus"] = [string](Get-JsonValue $referenceReadinessTrend "status")
                $liveDiagnosticsHistoryEvidence["referenceTrendReferenceTraceId"] = [string](Get-JsonValue $referenceReadinessTrend "referenceTraceId")
                $liveDiagnosticsHistoryEvidence["referenceTrendReferenceTraceRole"] = [string](Get-JsonValue $referenceReadinessTrend "referenceTraceRole")
                $liveDiagnosticsHistoryEvidence["referenceTrendSignalCount"] = [int](Get-NumericValueOrDefault (Get-JsonValue $referenceReadinessTrend "signalCount"))
                $liveDiagnosticsHistoryEvidence["referenceTrendImprovedSignalCount"] = [int](Get-NumericValueOrDefault (Get-JsonValue $referenceReadinessTrend "improvedSignalCount"))
                $liveDiagnosticsHistoryEvidence["referenceTrendRegressedSignalCount"] = [int](Get-NumericValueOrDefault (Get-JsonValue $referenceReadinessTrend "regressedSignalCount"))
                $liveDiagnosticsHistoryEvidence["referenceTrendResolvedSignalCount"] = [int](Get-NumericValueOrDefault (Get-JsonValue $referenceReadinessTrend "resolvedSignalCount"))
                $liveDiagnosticsHistoryEvidence["referenceTrendNewSignalCount"] = [int](Get-NumericValueOrDefault (Get-JsonValue $referenceReadinessTrend "newSignalCount"))
                $liveDiagnosticsHistoryEvidence["referenceTrendGapMaxDelta"] = Get-NumericValue (Get-JsonValue $referenceReadinessTrend "readinessGapMaxDelta")
                $tuningActions = @(Get-JsonArray $tuningActionPlan "actions")
                $liveDiagnosticsHistoryEvidence["tuningActionPlanStatus"] = [string](Get-JsonValue $tuningActionPlan "status")
                $liveDiagnosticsHistoryEvidence["tuningActionPlanDirectionStatus"] = [string](Get-JsonValue $tuningActionPlan "directionStatus")
                $liveDiagnosticsHistoryEvidence["tuningActionPlanPrimarySafetyClass"] = [string](Get-JsonValue $tuningActionPlan "primarySafetyClass")
                $liveDiagnosticsHistoryEvidence["tuningActionPlanPrimarySignalName"] = [string](Get-JsonValue $tuningActionPlan "primarySignalName")
                $liveDiagnosticsHistoryEvidence["tuningActionPlanTopControlFamily"] = [string](Get-JsonValue $tuningActionPlan "topControlFamily")
                $liveDiagnosticsHistoryEvidence["tuningActionPlanActionCount"] = [int](Get-NumericValueOrDefault (Get-JsonValue $tuningActionPlan "actionCount"))
                $liveExperimentScenarios = @(Get-JsonArray $liveExperimentPlan "scenarios")
                $liveDiagnosticsHistoryEvidence["liveExperimentPlanStatus"] = [string](Get-JsonValue $liveExperimentPlan "status")
                $liveDiagnosticsHistoryEvidence["liveExperimentPlanDirectionStatus"] = [string](Get-JsonValue $liveExperimentPlan "sourceDirectionStatus")
                $liveDiagnosticsHistoryEvidence["liveExperimentPlanPrimaryControlFamily"] = [string](Get-JsonValue $liveExperimentPlan "primaryControlFamily")
                $liveDiagnosticsHistoryEvidence["liveExperimentPlanScenarioCount"] = [int](Get-NumericValueOrDefault (Get-JsonValue $liveExperimentPlan "scenarioCount"))
                $liveDiagnosticsHistoryEvidence["liveExperimentPlanRecommendedSampleCount"] = [int](Get-NumericValueOrDefault (Get-JsonValue $liveExperimentPlan "recommendedSampleCount"))
                $liveDiagnosticsHistoryEvidence["liveExperimentPlanRecommendedIntervalMs"] = [int](Get-NumericValueOrDefault (Get-JsonValue $liveExperimentPlan "recommendedIntervalMs"))
                $liveDiagnosticsHistoryEvidence["liveExperimentCoverageStatus"] = [string](Get-JsonValue $liveExperimentCoverage "status")
                $liveDiagnosticsHistoryEvidence["liveExperimentCoverageScenarioCount"] = [int](Get-NumericValueOrDefault (Get-JsonValue $liveExperimentCoverage "scenarioCount"))
                $liveDiagnosticsHistoryEvidence["liveExperimentCoverageCoveredScenarioCount"] = [int](Get-NumericValueOrDefault (Get-JsonValue $liveExperimentCoverage "coveredScenarioCount"))
                $liveDiagnosticsHistoryEvidence["liveExperimentCoverageRequiredComparisonCount"] = [int](Get-NumericValueOrDefault (Get-JsonValue $liveExperimentCoverage "requiredComparisonCount"))
                $liveDiagnosticsHistoryEvidence["liveExperimentCoverageCoveredComparisonCount"] = [int](Get-NumericValueOrDefault (Get-JsonValue $liveExperimentCoverage "coveredComparisonCount"))
                $liveDiagnosticsHistoryEvidence["liveExperimentCoverageMissingComparisonCount"] = [int](Get-NumericValueOrDefault (Get-JsonValue $liveExperimentCoverage "missingComparisonCount"))
                $liveDiagnosticsHistoryEvidence["pumpingSignalCount"] = Get-SafetyClassCount $aggregateSafetyClassCounts "pumping"
                $liveDiagnosticsHistoryEvidence["weakSignalCount"] = Get-SafetyClassCount $aggregateSafetyClassCounts "weak-signal"
                $liveDiagnosticsHistoryEvidence["recommendationCount"] = $recommendations.Count
                $liveDiagnosticsHistoryEvidence["bundleRelativePaths"] = $bundleRelativePaths
                $liveDiagnosticsHistoryEvidence["absolutePathCount"] = $absolutePaths.Count

                if ($traceOrderingFieldsRequired) {
                    $previousSequenceTicks = $null
                    foreach ($trace in @($traceRecords)) {
                        $traceId = [string](Get-JsonValue $trace "traceId")
                        $sequenceText = [string](Get-JsonValue $trace "traceSequenceUtc")
                        $sortKeySource = [string](Get-JsonValue $trace "sortKeySource")
                        $sequence = Get-DateTimeOffsetValue $sequenceText
                        if ($null -eq $sequence) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-trace-sequence-missing" "Artifact '$artifactId' trace '$traceId' is missing a valid traceSequenceUtc."
                            $traceOrderingViolationCount++
                            $artifactValidationOk = $false
                            continue
                        }
                        if ([string]::IsNullOrWhiteSpace($sortKeySource) -or $validSortKeySources -notcontains $sortKeySource) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-trace-sort-key-source-invalid" "Artifact '$artifactId' trace '$traceId' has invalid sortKeySource='$sortKeySource'."
                            $traceOrderingViolationCount++
                            $artifactValidationOk = $false
                        }

                        $sequenceTicks = $sequence.UtcDateTime.Ticks
                        if ($null -ne $previousSequenceTicks -and $sequenceTicks -lt $previousSequenceTicks) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-trace-order-invalid" "Artifact '$artifactId' traces are not ordered by traceSequenceUtc; trace '$traceId' moved backwards."
                            $traceOrderingViolationCount++
                            $artifactValidationOk = $false
                        }
                        $previousSequenceTicks = $sequenceTicks
                    }

                    foreach ($entry in @(
                        [ordered]@{ name = "latestTrace"; trace = $latestTrace },
                        [ordered]@{ name = "previousNr5Trace"; trace = $previousNr5Trace },
                        [ordered]@{ name = "bestBalancedTrace"; trace = $bestBalancedTrace },
                        [ordered]@{ name = "bestWeakSignalTrace"; trace = $bestWeakSignalTrace },
                        [ordered]@{ name = "lowestPumpingTrace"; trace = $lowestPumpingTrace }
                    )) {
                        $compactName = [string]$entry["name"]
                        $compactTrace = $entry["trace"]
                        $compactTraceId = [string](Get-JsonValue $compactTrace "traceId")
                        if ([string]::IsNullOrWhiteSpace($compactTraceId)) {
                            continue
                        }

                        $fullTrace = Get-LiveHistoryTraceRecordById -Records $traceRecords -TraceId $compactTraceId
                        if ($null -eq $fullTrace) {
                            continue
                        }

                        $compactSequence = Get-DateTimeOffsetValue (Get-JsonValue $compactTrace "traceSequenceUtc")
                        $fullSequence = Get-DateTimeOffsetValue (Get-JsonValue $fullTrace "traceSequenceUtc")
                        if ($null -eq $compactSequence -or $null -eq $fullSequence) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-compact-trace-sequence-missing" "Artifact '$artifactId' $compactName for trace '$compactTraceId' is missing a valid traceSequenceUtc."
                            $traceOrderingViolationCount++
                            $artifactValidationOk = $false
                        }
                        elseif ($compactSequence.UtcDateTime.Ticks -ne $fullSequence.UtcDateTime.Ticks) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-compact-trace-sequence-mismatch" "Artifact '$artifactId' $compactName traceSequenceUtc does not match the full trace record for '$compactTraceId'."
                            $traceOrderingViolationCount++
                            $artifactValidationOk = $false
                        }

                        $compactSource = [string](Get-JsonValue $compactTrace "sortKeySource")
                        $fullSource = [string](Get-JsonValue $fullTrace "sortKeySource")
                        if ([string]::IsNullOrWhiteSpace($compactSource) -or -not [string]::Equals($compactSource, $fullSource, [StringComparison]::OrdinalIgnoreCase)) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-compact-trace-source-mismatch" "Artifact '$artifactId' $compactName sortKeySource does not match the full trace record for '$compactTraceId'."
                            $traceOrderingViolationCount++
                            $artifactValidationOk = $false
                        }

                        foreach ($fieldName in @("readyForBenchmarkTrace", "candidateComparisonReady", "promotable")) {
                            $actualCompactFlag = Test-Truthy (Get-JsonValue $compactTrace $fieldName)
                            $expectedFullFlag = Test-Truthy (Get-JsonValue $fullTrace $fieldName)
                            if ($actualCompactFlag -ne $expectedFullFlag) {
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-compact-trace-decision-field-mismatch" "Artifact '$artifactId' $compactName.$fieldName=$actualCompactFlag does not match the full trace record for '$compactTraceId'."
                                $artifactValidationOk = $false
                            }
                        }
                        foreach ($fieldName in @("weakDropoutSampleCount", "weakRecoveryPct", "nr5OutputMovementDb", "audioRmsMovementDb", "safetyRiskScore", "promotionBlockerCount")) {
                            if (-not (Test-LiveHistoryScalarEquivalent -Actual (Get-JsonValue $compactTrace $fieldName) -Expected (Get-JsonValue $fullTrace $fieldName))) {
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-compact-trace-decision-field-mismatch" "Artifact '$artifactId' $compactName.$fieldName does not match the full trace record for '$compactTraceId'."
                                $artifactValidationOk = $false
                            }
                        }
                        if (-not (Test-StringArraySame -Actual (Get-JsonArray $compactTrace "promotionBlockerClasses") -Expected (Get-JsonArray $fullTrace "promotionBlockerClasses"))) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-compact-trace-decision-rollup-mismatch" "Artifact '$artifactId' $compactName.promotionBlockerClasses does not match the full trace record for '$compactTraceId'."
                            $artifactValidationOk = $false
                        }
                        if (-not (Test-LiveHistorySafetyClassCountsMatch -Actual (Get-JsonArray $compactTrace "promotionBlockerClassCounts") -Expected (Get-JsonArray $fullTrace "promotionBlockerClassCounts"))) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-compact-trace-decision-rollup-mismatch" "Artifact '$artifactId' $compactName.promotionBlockerClassCounts does not match the full trace record for '$compactTraceId'."
                            $artifactValidationOk = $false
                        }
                        if (-not (Test-LiveHistorySafetyClassCountsMatch -Actual (Get-JsonArray $compactTrace "safetyClassCounts") -Expected (Get-JsonArray $fullTrace "safetyClassCounts"))) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-compact-trace-decision-rollup-mismatch" "Artifact '$artifactId' $compactName.safetyClassCounts does not match the full trace record for '$compactTraceId'."
                            $artifactValidationOk = $false
                        }
                    }

                    if ($null -ne $expectedLatestTraceBySequence) {
                        $expectedLatestTraceId = [string](Get-JsonValue $expectedLatestTraceBySequence "traceId")
                        $actualLatestTraceId = [string](Get-JsonValue $latestTrace "traceId")
                        if (-not [string]::Equals($actualLatestTraceId, $expectedLatestTraceId, [StringComparison]::OrdinalIgnoreCase)) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-latest-trace-sequence-mismatch" "Artifact '$artifactId' latestTrace.traceId='$actualLatestTraceId' does not match highest NR5 traceSequenceUtc trace '$expectedLatestTraceId'."
                            $traceOrderingViolationCount++
                            $artifactValidationOk = $false
                        }
                    }

                    if ($null -ne $expectedPreviousNr5TraceBySequence) {
                        $expectedPreviousNr5TraceId = [string](Get-JsonValue $expectedPreviousNr5TraceBySequence "traceId")
                        $actualPreviousNr5TraceId = [string](Get-JsonValue $previousNr5Trace "traceId")
                        if (-not [string]::Equals($actualPreviousNr5TraceId, $expectedPreviousNr5TraceId, [StringComparison]::OrdinalIgnoreCase)) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-previous-nr5-sequence-mismatch" "Artifact '$artifactId' previousNr5Trace.traceId='$actualPreviousNr5TraceId' does not match second-highest NR5 traceSequenceUtc trace '$expectedPreviousNr5TraceId'."
                            $traceOrderingViolationCount++
                            $artifactValidationOk = $false
                        }
                    }
                }

                $liveDiagnosticsHistoryEvidence["traceOrderingViolationCount"] = $traceOrderingViolationCount
                if (-not $traceOrderingFieldsRequired) {
                    $liveDiagnosticsHistoryEvidence["traceOrderingStatus"] = "legacy"
                }
                elseif ($traceOrderingViolationCount -eq 0) {
                    $liveDiagnosticsHistoryEvidence["traceOrderingStatus"] = "sequence-ready"
                }
                else {
                    $liveDiagnosticsHistoryEvidence["traceOrderingStatus"] = "sequence-invalid"
                }

                foreach ($entry in @(
                    [ordered]@{ name = "bestBalancedTrace"; actual = $bestBalancedTrace; expected = $expectedBestBalancedTrace; code = "live-history-best-balanced-selection-mismatch"; mode = "best-balanced" },
                    [ordered]@{ name = "bestWeakSignalTrace"; actual = $bestWeakSignalTrace; expected = $expectedBestWeakSignalTrace; code = "live-history-best-weak-selection-mismatch"; mode = "best-weak" },
                    [ordered]@{ name = "lowestPumpingTrace"; actual = $lowestPumpingTrace; expected = $expectedLowestPumpingTrace; code = "live-history-lowest-pumping-selection-mismatch"; mode = "lowest-pumping" }
                )) {
                    $expectedTrace = $entry["expected"]
                    if ($null -eq $expectedTrace) {
                        continue
                    }

                    $actualTraceId = [string](Get-JsonValue $entry["actual"] "traceId")
                    $expectedTraceId = [string](Get-JsonValue $expectedTrace "traceId")
                    if (-not [string]::Equals($actualTraceId, $expectedTraceId, [StringComparison]::OrdinalIgnoreCase)) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired ([string]$entry["code"]) "Artifact '$artifactId' $($entry["name"]).traceId='$actualTraceId' does not match the trace-derived $($entry["mode"]) selection '$expectedTraceId'."
                        $artifactValidationOk = $false
                    }
                }

                if ($traceCount -le 0) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-traces-missing" "Artifact '$artifactId' does not summarize any live diagnostics watch traces."
                    $artifactValidationOk = $false
                }
                if ($traceCount -ne $actualTraceCount) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-trace-count-mismatch" "Artifact '$artifactId' reports traceCount=$traceCount but contains $actualTraceCount trace record(s)."
                    $artifactValidationOk = $false
                }
                if ($nr5TraceCount -le 0) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-nr5-traces-missing" "Artifact '$artifactId' does not summarize any NR5 live diagnostics traces."
                    $artifactValidationOk = $false
                }
                if ($nr5TraceCount -ne $actualNr5TraceCount) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-nr5-count-mismatch" "Artifact '$artifactId' reports nr5TraceCount=$nr5TraceCount but contains $actualNr5TraceCount NR5 trace record(s)."
                    $artifactValidationOk = $false
                }
                if ($readyTraceCount -ne $actualReadyTraceCount) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-ready-count-mismatch" "Artifact '$artifactId' reports readyTraceCount=$readyTraceCount but contains $actualReadyTraceCount benchmark-ready trace record(s)."
                    $artifactValidationOk = $false
                }
                if ($readyNr5TraceCount -ne $actualReadyNr5TraceCount) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-ready-nr5-count-mismatch" "Artifact '$artifactId' reports readyNr5TraceCount=$readyNr5TraceCount but contains $actualReadyNr5TraceCount benchmark-ready NR5 trace record(s)."
                    $artifactValidationOk = $false
                }
                if ($candidateReadyNr5TraceCount -ne $actualCandidateReadyNr5TraceCount) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-candidate-ready-nr5-count-mismatch" "Artifact '$artifactId' reports candidateReadyNr5TraceCount=$candidateReadyNr5TraceCount but contains $actualCandidateReadyNr5TraceCount candidate-ready NR5 trace record(s)."
                    $artifactValidationOk = $false
                }
                if (-not (Test-LiveHistoryReviewStatusCountsMatch -Actual $reviewStatusCounts -Expected $expectedReviewStatusCounts)) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-review-status-count-mismatch" "Artifact '$artifactId' reviewStatusCounts does not match the trace reviewStatus rollup."
                    $artifactValidationOk = $false
                }
                foreach ($fieldName in @("weakRecoveryPctMinimum", "nr5OutputMovementDbMaximum", "audioRmsMovementDbMaximum", "nr5MakeupMovementDbMaximum", "nr5RecoveryDriveMovementMaximum", "nr5MakeupMaxDbMaximum", "audioPeakMaxDbfsMaximum", "adcHeadroomMinDbMinimum")) {
                    if (-not (Test-LiveHistoryScalarEquivalent -Actual (Get-JsonValue $thresholds $fieldName) -Expected (Get-JsonValue $expectedThresholds $fieldName))) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-thresholds-mismatch" "Artifact '$artifactId' thresholds.$fieldName does not match the summarizer tuning threshold."
                        $artifactValidationOk = $false
                    }
                }
                foreach ($fieldName in @("weakRecoveryPct", "weakDropoutSampleCount", "nr5OutputMovementDb", "nr5MakeupMovementDb", "nr5RecoveryDriveMovement", "safetyRiskScore")) {
                    if (-not (Test-LiveHistoryScalarEquivalent -Actual (Get-JsonValue $latestVsPreviousNr5Delta $fieldName) -Expected (Get-JsonValue $expectedLatestVsPreviousNr5Delta $fieldName))) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-latest-delta-mismatch" "Artifact '$artifactId' latestVsPreviousNr5Delta.$fieldName does not match latest and previous NR5 trace records."
                        $artifactValidationOk = $false
                    }
                }
                if (-not (Test-OrderedStringArraysEqual -Actual $recommendations -Expected $expectedRecommendations)) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-recommendations-mismatch" "Artifact '$artifactId' recommendations do not match the trace-derived live-history guidance."
                    $artifactValidationOk = $false
                }
                if (-not (Test-LiveHistorySafetyClassCountsMatch -Actual $aggregateSafetyClassCounts -Expected $expectedAggregateSafetyClassCounts)) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-aggregate-safety-count-mismatch" "Artifact '$artifactId' aggregateSafetyClassCounts does not match the trace safetySignals rollup."
                    $artifactValidationOk = $false
                }
                if ($readinessFieldsRequired -and -not (Test-LiveHistoryReadinessSummaryMatches -Actual $aggregateReadinessGaps -Expected $expectedAggregateReadinessGaps)) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-aggregate-readiness-gap-mismatch" "Artifact '$artifactId' aggregateReadinessGaps does not match the trace safetySignals rollup."
                    $artifactValidationOk = $false
                }
                if ($readinessTrendFieldsRequired -and -not (Test-LiveHistoryReadinessTrendMatches -Actual $readinessTrend -Expected $expectedReadinessTrend)) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-readiness-trend-mismatch" "Artifact '$artifactId' latestVsPreviousNr5ReadinessGapTrend does not match the latest and previous NR5 trace safetySignals."
                    $artifactValidationOk = $false
                }
                if ($null -ne $readinessTrend -and $null -ne $legacyReadinessTrend -and -not (Test-LiveHistoryReadinessTrendMatches -Actual $legacyReadinessTrend -Expected $readinessTrend)) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-readiness-trend-alias-mismatch" "Artifact '$artifactId' latestVsPreviousReadinessTrend does not match latestVsPreviousNr5ReadinessGapTrend."
                    $artifactValidationOk = $false
                }
                if ($referenceTrendFieldsRequired -and $null -eq $referenceReadinessTrend) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-reference-readiness-trend-missing" "Artifact '$artifactId' schemaVersion=$schemaVersion must include latestVsReferenceNr5ReadinessGapTrend."
                    $artifactValidationOk = $false
                }
                elseif ($referenceTrendFieldsRequired -and -not (Test-LiveHistoryReadinessTrendMatches -Actual $referenceReadinessTrend -Expected $expectedReferenceReadinessTrend)) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-reference-readiness-trend-mismatch" "Artifact '$artifactId' latestVsReferenceNr5ReadinessGapTrend does not match the latest trace and promotion reference trace safetySignals."
                    $artifactValidationOk = $false
                }
                if ($tuningActionPlanFieldsRequired -and $null -eq $tuningActionPlan) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-tuning-action-plan-missing" "Artifact '$artifactId' schemaVersion=$schemaVersion must include latestTuningActionPlan."
                    $artifactValidationOk = $false
                }
                elseif ($tuningActionPlanFieldsRequired) {
                    $planStatus = [string](Get-JsonValue $tuningActionPlan "status")
                    $planDirectionStatus = [string](Get-JsonValue $tuningActionPlan "directionStatus")
                    $planLatestTraceId = [string](Get-JsonValue $tuningActionPlan "latestTraceId")
                    $planReferenceTraceId = [string](Get-JsonValue $tuningActionPlan "referenceTraceId")
                    $planReferenceTraceRole = [string](Get-JsonValue $tuningActionPlan "referenceTraceRole")
                    $planPreviousStatus = [string](Get-JsonValue $tuningActionPlan "latestVsPreviousStatus")
                    $planReferenceStatus = [string](Get-JsonValue $tuningActionPlan "latestVsReferenceStatus")
                    $planActionCount = [int](Get-NumericValueOrDefault (Get-JsonValue $tuningActionPlan "actionCount"))
                    $validPlanDirections = @(
                        "candidate-ready",
                        "latest-regressed-vs-reference",
                        "latest-regressed-vs-previous",
                        "latest-improved-vs-reference",
                        "blocked"
                    )

                    if ($validPlanDirections -notcontains $planDirectionStatus) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-tuning-action-plan-direction-invalid" "Artifact '$artifactId' latestTuningActionPlan.directionStatus='$planDirectionStatus' is not supported."
                        $artifactValidationOk = $false
                    }
                    if ($planStatus -ne $promotionStatus) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-tuning-action-plan-status-mismatch" "Artifact '$artifactId' latestTuningActionPlan.status does not match promotionDecision.status."
                        $artifactValidationOk = $false
                    }
                    if ($planLatestTraceId -ne [string](Get-JsonValue $latestTrace "traceId")) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-tuning-action-plan-latest-mismatch" "Artifact '$artifactId' latestTuningActionPlan.latestTraceId does not match latestTrace.traceId."
                        $artifactValidationOk = $false
                    }
                    if ($planReferenceTraceId -ne $promotionReferenceTraceId -or $planReferenceTraceRole -ne $promotionReferenceTraceRole) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-tuning-action-plan-reference-mismatch" "Artifact '$artifactId' latestTuningActionPlan reference fields do not match promotionDecision reference fields."
                        $artifactValidationOk = $false
                    }
                    if ($planPreviousStatus -ne [string](Get-JsonValue $readinessTrend "status") -or $planReferenceStatus -ne [string](Get-JsonValue $referenceReadinessTrend "status")) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-tuning-action-plan-trend-status-mismatch" "Artifact '$artifactId' latestTuningActionPlan trend statuses do not match the readiness trend summaries."
                        $artifactValidationOk = $false
                    }
                    if ($planActionCount -ne $tuningActions.Count) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-tuning-action-plan-action-count-mismatch" "Artifact '$artifactId' latestTuningActionPlan.actionCount=$planActionCount but contains $($tuningActions.Count) action(s)."
                        $artifactValidationOk = $false
                    }
                    if ($planActionCount -ne [int](Get-NumericValueOrDefault (Get-JsonValue $expectedTuningActionPlan "actionCount") -1)) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-tuning-action-plan-expected-action-count-mismatch" "Artifact '$artifactId' latestTuningActionPlan.actionCount=$planActionCount but trace safety signals imply $([int](Get-NumericValueOrDefault (Get-JsonValue $expectedTuningActionPlan "actionCount") -1))."
                        $artifactValidationOk = $false
                    }
                    if (-not $candidatePromotionReady -and $tuningActions.Count -le 0) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-tuning-action-plan-actions-missing" "Artifact '$artifactId' latestTuningActionPlan has no ranked actions while promotion is blocked."
                        $artifactValidationOk = $false
                    }

                    foreach ($fieldName in @("planScope", "status", "directionStatus", "promotionStatus", "latestTraceId", "referenceTraceId", "referenceTraceRole", "latestVsPreviousStatus", "latestVsReferenceStatus", "primarySafetyClass", "primarySignalName", "topControlFamily")) {
                        $actualPlanValue = [string](Get-JsonValue $tuningActionPlan $fieldName)
                        $expectedPlanValue = [string](Get-JsonValue $expectedTuningActionPlan $fieldName)
                        if (-not [string]::Equals($actualPlanValue, $expectedPlanValue, [StringComparison]::Ordinal)) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-tuning-action-plan-field-mismatch" "Artifact '$artifactId' latestTuningActionPlan.$fieldName='$actualPlanValue' but trace safety signals imply '$expectedPlanValue'."
                            $artifactValidationOk = $false
                        }
                    }

                    $expectedPriority = 1
                    $expectedTuningActions = @(Get-JsonArray $expectedTuningActionPlan "actions")
                    foreach ($action in @($tuningActions)) {
                        $priority = [int](Get-NumericValueOrDefault (Get-JsonValue $action "priority") -1)
                        $actionId = [string](Get-JsonValue $action "actionId")
                        $controlFamily = [string](Get-JsonValue $action "controlFamily")
                        $safetyClass = [string](Get-JsonValue $action "safetyClass")
                        $expectedAction = $null
                        $expectedActionIndex = $expectedPriority - 1
                        if ($expectedActionIndex -ge 0 -and $expectedActionIndex -lt $expectedTuningActions.Count) {
                            $expectedAction = $expectedTuningActions[$expectedActionIndex]
                        }
                        if ([string]::IsNullOrWhiteSpace($actionId) -or [string]::IsNullOrWhiteSpace($controlFamily) -or [string]::IsNullOrWhiteSpace($safetyClass)) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-tuning-action-plan-action-invalid" "Artifact '$artifactId' latestTuningActionPlan action is missing actionId, controlFamily, or safetyClass."
                            $artifactValidationOk = $false
                        }
                        if ($priority -ne $expectedPriority) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-tuning-action-plan-priority-invalid" "Artifact '$artifactId' latestTuningActionPlan action priority=$priority but expected $expectedPriority."
                            $artifactValidationOk = $false
                        }
                        if ($null -ne $expectedAction) {
                            foreach ($fieldName in @("actionId", "safetyClass", "signalName", "controlFamily", "trendSource", "readinessGapUnit", "referenceTraceId", "referenceTraceRole", "rationale", "guardrail")) {
                                $actualActionValue = [string](Get-JsonValue $action $fieldName)
                                $expectedActionValue = [string](Get-JsonValue $expectedAction $fieldName)
                                if (-not [string]::Equals($actualActionValue, $expectedActionValue, [StringComparison]::Ordinal)) {
                                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-tuning-action-plan-action-field-mismatch" "Artifact '$artifactId' latestTuningActionPlan action priority=$expectedPriority field '$fieldName'='$actualActionValue' but trace safety signals imply '$expectedActionValue'."
                                    $artifactValidationOk = $false
                                }
                            }
                            foreach ($fieldName in @("latestReadinessGap", "latestVsPreviousGapDelta", "latestVsReferenceGapDelta")) {
                                if (-not (Test-NumericClose -Actual (Get-JsonValue $action $fieldName) -Expected (Get-JsonValue $expectedAction $fieldName))) {
                                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-tuning-action-plan-action-numeric-field-mismatch" "Artifact '$artifactId' latestTuningActionPlan action '$actionId' field '$fieldName' does not match the trace-derived tuning action."
                                    $artifactValidationOk = $false
                                }
                            }
                        }
                        $expectedPriority++
                    }

                    if ($tuningActions.Count -gt 0) {
                        $firstAction = $tuningActions[0]
                        if ([string](Get-JsonValue $tuningActionPlan "topControlFamily") -ne [string](Get-JsonValue $firstAction "controlFamily") -or
                            [string](Get-JsonValue $tuningActionPlan "primarySafetyClass") -ne [string](Get-JsonValue $firstAction "safetyClass") -or
                            [string](Get-JsonValue $tuningActionPlan "primarySignalName") -ne [string](Get-JsonValue $firstAction "signalName")) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-tuning-action-plan-primary-mismatch" "Artifact '$artifactId' latestTuningActionPlan primary fields do not match the first ranked action."
                            $artifactValidationOk = $false
                        }
                    }
                }
                if ($liveExperimentPlanFieldsRequired -and $null -eq $liveExperimentPlan) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-live-experiment-plan-missing" "Artifact '$artifactId' schemaVersion=$schemaVersion must include latestLiveExperimentPlan."
                    $artifactValidationOk = $false
                }
                elseif ($liveExperimentPlanFieldsRequired) {
                    $experimentStatus = [string](Get-JsonValue $liveExperimentPlan "status")
                    $experimentSourceStatus = [string](Get-JsonValue $liveExperimentPlan "sourceActionPlanStatus")
                    $experimentDirection = [string](Get-JsonValue $liveExperimentPlan "sourceDirectionStatus")
                    $experimentControlFamily = [string](Get-JsonValue $liveExperimentPlan "primaryControlFamily")
                    $experimentScenarioCount = [int](Get-NumericValueOrDefault (Get-JsonValue $liveExperimentPlan "scenarioCount"))
                    $experimentSampleCount = [int](Get-NumericValueOrDefault (Get-JsonValue $liveExperimentPlan "recommendedSampleCount"))
                    $experimentIntervalMs = [int](Get-NumericValueOrDefault (Get-JsonValue $liveExperimentPlan "recommendedIntervalMs"))
                    $experimentComparisons = @(Get-JsonArray $liveExperimentPlan "recommendedComparisons" | ForEach-Object { [string]$_ })
                    $experimentCommandTemplates = Get-JsonValue $liveExperimentPlan "matrixCommandTemplates"
                    $expectedExperimentScenarios = @(Get-JsonArray $expectedLiveExperimentPlan "scenarios")

                    if ($experimentStatus -ne $promotionStatus -or $experimentSourceStatus -ne [string](Get-JsonValue $tuningActionPlan "status")) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-live-experiment-plan-status-mismatch" "Artifact '$artifactId' latestLiveExperimentPlan status fields do not match promotionDecision/latestTuningActionPlan."
                        $artifactValidationOk = $false
                    }
                    if ($experimentDirection -ne [string](Get-JsonValue $tuningActionPlan "directionStatus")) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-live-experiment-plan-direction-mismatch" "Artifact '$artifactId' latestLiveExperimentPlan.sourceDirectionStatus does not match latestTuningActionPlan.directionStatus."
                        $artifactValidationOk = $false
                    }
                    if ($experimentControlFamily -ne [string](Get-JsonValue $tuningActionPlan "topControlFamily")) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-live-experiment-plan-control-family-mismatch" "Artifact '$artifactId' latestLiveExperimentPlan.primaryControlFamily does not match latestTuningActionPlan.topControlFamily."
                        $artifactValidationOk = $false
                    }
                    if ($null -ne $expectedLiveExperimentPlan) {
                        foreach ($fieldName in @("planScope", "status", "sourceActionPlanStatus", "sourceDirectionStatus", "primaryControlFamily", "primarySafetyClass", "primarySignalName", "referenceTraceId", "referenceTraceRole", "topActionId")) {
                            $actualPlanField = [string](Get-JsonValue $liveExperimentPlan $fieldName)
                            $expectedPlanField = [string](Get-JsonValue $expectedLiveExperimentPlan $fieldName)
                            if (-not [string]::Equals($actualPlanField, $expectedPlanField, [StringComparison]::Ordinal)) {
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-live-experiment-plan-field-mismatch" "Artifact '$artifactId' latestLiveExperimentPlan.$fieldName='$actualPlanField' but latestTuningActionPlan implies '$expectedPlanField'."
                                $artifactValidationOk = $false
                            }
                        }
                    }
                    if ($experimentScenarioCount -ne $liveExperimentScenarios.Count) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-live-experiment-plan-scenario-count-mismatch" "Artifact '$artifactId' latestLiveExperimentPlan.scenarioCount=$experimentScenarioCount but contains $($liveExperimentScenarios.Count) scenario(s)."
                        $artifactValidationOk = $false
                    }
                    if ($null -ne $expectedLiveExperimentPlan -and $experimentScenarioCount -ne $expectedExperimentScenarios.Count) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-live-experiment-plan-expected-scenario-count-mismatch" "Artifact '$artifactId' latestLiveExperimentPlan.scenarioCount=$experimentScenarioCount but latestTuningActionPlan implies $($expectedExperimentScenarios.Count)."
                        $artifactValidationOk = $false
                    }
                    if ($experimentScenarioCount -le 0) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-live-experiment-plan-scenarios-missing" "Artifact '$artifactId' latestLiveExperimentPlan has no live experiment scenarios."
                        $artifactValidationOk = $false
                    }
                    if ($experimentSampleCount -le 0 -or $experimentIntervalMs -le 0) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-live-experiment-plan-sampling-invalid" "Artifact '$artifactId' latestLiveExperimentPlan must include positive recommendedSampleCount and recommendedIntervalMs."
                        $artifactValidationOk = $false
                    }
                    if ($null -ne $expectedLiveExperimentPlan) {
                        foreach ($fieldName in @("recommendedSampleCount", "recommendedIntervalMs", "minimumScenarioCount")) {
                            $actualNumericValue = [int](Get-NumericValueOrDefault (Get-JsonValue $liveExperimentPlan $fieldName) -1)
                            $expectedNumericValue = [int](Get-NumericValueOrDefault (Get-JsonValue $expectedLiveExperimentPlan $fieldName) -1)
                            if ($actualNumericValue -ne $expectedNumericValue) {
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-live-experiment-plan-numeric-field-mismatch" "Artifact '$artifactId' latestLiveExperimentPlan.$fieldName=$actualNumericValue but latestTuningActionPlan implies $expectedNumericValue."
                                $artifactValidationOk = $false
                            }
                        }

                        if (-not (Test-OrderedStringArraysEqual -Actual (Get-JsonArray $liveExperimentPlan "scenarioIds") -Expected (Get-JsonArray $expectedLiveExperimentPlan "scenarioIds"))) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-live-experiment-plan-scenario-ids-mismatch" "Artifact '$artifactId' latestLiveExperimentPlan.scenarioIds does not match latestTuningActionPlan-derived scenarios."
                            $artifactValidationOk = $false
                        }
                        if (-not (Test-OrderedStringArraysEqual -Actual $experimentComparisons -Expected (Get-JsonArray $expectedLiveExperimentPlan "recommendedComparisons"))) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-live-experiment-plan-comparisons-mismatch" "Artifact '$artifactId' latestLiveExperimentPlan.recommendedComparisons does not match the generated live experiment plan."
                            $artifactValidationOk = $false
                        }
                        if (-not (Test-OrderedStringArraysEqual -Actual (Get-JsonArray $liveExperimentPlan "requiredEvidence") -Expected (Get-JsonArray $expectedLiveExperimentPlan "requiredEvidence"))) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-live-experiment-plan-required-evidence-mismatch" "Artifact '$artifactId' latestLiveExperimentPlan.requiredEvidence does not match the generated live experiment plan."
                            $artifactValidationOk = $false
                        }
                    }
                    foreach ($comparisonId in @(Get-LiveHistoryExperimentRequiredComparisons)) {
                        if ($experimentComparisons -notcontains $comparisonId) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-live-experiment-plan-comparison-missing" "Artifact '$artifactId' latestLiveExperimentPlan.recommendedComparisons is missing '$comparisonId'."
                            $artifactValidationOk = $false
                        }
                    }
                    if (Test-Truthy (Get-JsonValue $liveExperimentPlan "defaultBehaviorChangeReady")) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-live-experiment-plan-default-change-ready" "Artifact '$artifactId' latestLiveExperimentPlan must not authorize default DSP behavior changes."
                        $artifactValidationOk = $false
                    }
                    foreach ($templateName in @("baseline", "candidate", "compare")) {
                        $template = [string](Get-JsonValue $experimentCommandTemplates $templateName)
                        if ([string]::IsNullOrWhiteSpace($template)) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-live-experiment-plan-command-missing" "Artifact '$artifactId' latestLiveExperimentPlan.matrixCommandTemplates.$templateName is missing."
                            $artifactValidationOk = $false
                        }
                        elseif ($null -ne $expectedLiveExperimentPlan) {
                            $expectedTemplate = [string](Get-JsonValue (Get-JsonValue $expectedLiveExperimentPlan "matrixCommandTemplates") $templateName)
                            if (-not [string]::Equals($template, $expectedTemplate, [StringComparison]::Ordinal)) {
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-live-experiment-plan-command-mismatch" "Artifact '$artifactId' latestLiveExperimentPlan.matrixCommandTemplates.$templateName does not match the generated live experiment plan."
                                $artifactValidationOk = $false
                            }
                        }
                    }

                    $expectedScenarioPriority = 1
                    foreach ($scenario in @($liveExperimentScenarios)) {
                        $scenarioPriority = [int](Get-NumericValueOrDefault (Get-JsonValue $scenario "priority") -1)
                        $scenarioId = [string](Get-JsonValue $scenario "scenarioId")
                        $purpose = [string](Get-JsonValue $scenario "purpose")
                        $controlFamily = [string](Get-JsonValue $scenario "controlFamily")
                        $safetyClass = [string](Get-JsonValue $scenario "safetyClass")
                        $scenarioSampleCount = [int](Get-NumericValueOrDefault (Get-JsonValue $scenario "sampleCount"))
                        $scenarioIntervalMs = [int](Get-NumericValueOrDefault (Get-JsonValue $scenario "intervalMs"))
                        $scenarioComparisons = @(Get-JsonArray $scenario "requiredComparisons" | ForEach-Object { [string]$_ })
                        $scenarioGates = @(Get-JsonArray $scenario "acceptanceGates")
                        $expectedScenario = $null
                        $expectedScenarioIndex = $expectedScenarioPriority - 1
                        if ($expectedScenarioIndex -ge 0 -and $expectedScenarioIndex -lt $expectedExperimentScenarios.Count) {
                            $expectedScenario = $expectedExperimentScenarios[$expectedScenarioIndex]
                        }

                        if ($scenarioPriority -ne $expectedScenarioPriority) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-live-experiment-plan-priority-invalid" "Artifact '$artifactId' latestLiveExperimentPlan scenario priority=$scenarioPriority but expected $expectedScenarioPriority."
                            $artifactValidationOk = $false
                        }
                        if ([string]::IsNullOrWhiteSpace($scenarioId) -or [string]::IsNullOrWhiteSpace($purpose) -or [string]::IsNullOrWhiteSpace($controlFamily) -or [string]::IsNullOrWhiteSpace($safetyClass)) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-live-experiment-plan-scenario-invalid" "Artifact '$artifactId' latestLiveExperimentPlan scenario is missing scenarioId, purpose, controlFamily, or safetyClass."
                            $artifactValidationOk = $false
                        }
                        if ($scenarioSampleCount -le 0 -or $scenarioIntervalMs -le 0) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-live-experiment-plan-scenario-sampling-invalid" "Artifact '$artifactId' latestLiveExperimentPlan scenario '$scenarioId' must include positive sampleCount and intervalMs."
                            $artifactValidationOk = $false
                        }
                        foreach ($comparisonId in @(Get-LiveHistoryExperimentRequiredComparisons)) {
                            if ($scenarioComparisons -notcontains $comparisonId) {
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-live-experiment-plan-scenario-comparison-missing" "Artifact '$artifactId' latestLiveExperimentPlan scenario '$scenarioId' is missing required comparison '$comparisonId'."
                                $artifactValidationOk = $false
                            }
                        }
                        if ($null -ne $expectedScenario) {
                            foreach ($fieldName in @("scenarioId", "purpose", "sourceActionId", "sourceTrend", "controlFamily", "safetyClass", "signalName", "operatorSetup")) {
                                $actualScenarioField = [string](Get-JsonValue $scenario $fieldName)
                                $expectedScenarioField = [string](Get-JsonValue $expectedScenario $fieldName)
                                if (-not [string]::Equals($actualScenarioField, $expectedScenarioField, [StringComparison]::Ordinal)) {
                                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-live-experiment-plan-scenario-field-mismatch" "Artifact '$artifactId' latestLiveExperimentPlan scenario priority=$expectedScenarioPriority field '$fieldName'='$actualScenarioField' but the generated plan requires '$expectedScenarioField'."
                                    $artifactValidationOk = $false
                                }
                            }
                            foreach ($fieldName in @("sourceActionPriority", "sampleCount", "intervalMs")) {
                                $actualScenarioNumber = [int](Get-NumericValueOrDefault (Get-JsonValue $scenario $fieldName) -1)
                                $expectedScenarioNumber = [int](Get-NumericValueOrDefault (Get-JsonValue $expectedScenario $fieldName) -1)
                                if ($actualScenarioNumber -ne $expectedScenarioNumber) {
                                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-live-experiment-plan-scenario-numeric-field-mismatch" "Artifact '$artifactId' latestLiveExperimentPlan scenario '$scenarioId' field '$fieldName'=$actualScenarioNumber but the generated plan requires $expectedScenarioNumber."
                                    $artifactValidationOk = $false
                                }
                            }
                            if (-not (Test-OrderedStringArraysEqual -Actual $scenarioComparisons -Expected (Get-JsonArray $expectedScenario "requiredComparisons"))) {
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-live-experiment-plan-scenario-comparisons-mismatch" "Artifact '$artifactId' latestLiveExperimentPlan scenario '$scenarioId' requiredComparisons does not match the generated live experiment plan."
                                $artifactValidationOk = $false
                            }
                            if (-not (Test-OrderedStringArraysEqual -Actual $scenarioGates -Expected (Get-JsonArray $expectedScenario "acceptanceGates"))) {
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-live-experiment-plan-scenario-gates-mismatch" "Artifact '$artifactId' latestLiveExperimentPlan scenario '$scenarioId' acceptanceGates does not match the generated live experiment plan."
                                $artifactValidationOk = $false
                            }
                        }
                        if ($scenarioGates.Count -le 0) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-live-experiment-plan-gates-missing" "Artifact '$artifactId' latestLiveExperimentPlan scenario '$scenarioId' has no acceptance gates."
                            $artifactValidationOk = $false
                        }
                        $expectedScenarioPriority++
                    }
                }
                if ($liveExperimentCoverageFieldsRequired -and $null -eq $liveExperimentCoverage) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-live-experiment-coverage-missing" "Artifact '$artifactId' schemaVersion=$schemaVersion must include latestLiveExperimentCoverage."
                    $artifactValidationOk = $false
                }
                elseif ($liveExperimentCoverageFieldsRequired -and -not (Test-LiveHistoryExperimentCoverageMatches -Actual $liveExperimentCoverage -Expected $expectedLiveExperimentCoverage)) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-live-experiment-coverage-mismatch" "Artifact '$artifactId' latestLiveExperimentCoverage does not match the generated live experiment plan and trace scenario/comparison records."
                    $artifactValidationOk = $false
                }
                if ($readyTraceCount -le 0) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-ready-traces-missing" "Artifact '$artifactId' does not include any benchmark-ready live diagnostics traces."
                    $artifactValidationOk = $false
                }
                if ($nr5TraceCount -gt 0 -and $readyNr5TraceCount -le 0) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-ready-nr5-traces-missing" "Artifact '$artifactId' does not include any benchmark-ready NR5 live diagnostics traces."
                    $artifactValidationOk = $false
                }
                if ($null -eq $latestTrace) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-latest-trace-missing" "Artifact '$artifactId' does not identify a latest trace."
                    $artifactValidationOk = $false
                }
                if ($nr5TraceCount -gt 0 -and $null -eq $bestBalancedTrace) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-best-balanced-missing" "Artifact '$artifactId' does not identify a best balanced NR5 trace."
                    $artifactValidationOk = $false
                }
                if ($null -eq $promotionDecision) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-promotion-decision-missing" "Artifact '$artifactId' does not include promotionDecision; rerun summarize-dsp-live-diagnostics-history.ps1."
                    $artifactValidationOk = $false
                }
                else {
                    if ($null -ne $legacyPromotionDecision) {
                        foreach ($fieldName in @("promotionScope", "status", "promotable", "candidatePromotionReady", "candidateComparisonReady", "defaultBehaviorChangeReady", "latestTraceId", "recommendedTraceId", "recommendedTraceRole", "referenceTraceId", "referenceTraceRole", "latestIsBestBalanced", "latestSafetyRiskScore", "bestBalancedSafetyRiskScore", "riskScoreDeltaVsBestBalanced", "blockerCount", "nextAction")) {
                            $canonicalValue = Get-JsonValue $promotionDecision $fieldName
                            $legacyValue = Get-JsonValue $legacyPromotionDecision $fieldName
                            if (-not (Test-LiveHistoryScalarEquivalent -Actual $legacyValue -Expected $canonicalValue)) {
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-promotion-alias-mismatch" "Artifact '$artifactId' latestNr5Decision.$fieldName does not match promotionDecision.$fieldName."
                                $artifactValidationOk = $false
                            }
                        }
                        if (-not (Test-StringArraySame -Actual (Get-JsonArray $legacyPromotionDecision "blockerClasses") -Expected (Get-JsonArray $promotionDecision "blockerClasses"))) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-promotion-alias-mismatch" "Artifact '$artifactId' latestNr5Decision.blockerClasses does not match promotionDecision.blockerClasses."
                            $artifactValidationOk = $false
                        }
                        if (-not (Test-LiveHistorySafetyClassCountsMatch -Actual (Get-JsonArray $legacyPromotionDecision "blockerClassCounts") -Expected (Get-JsonArray $promotionDecision "blockerClassCounts"))) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-promotion-alias-mismatch" "Artifact '$artifactId' latestNr5Decision.blockerClassCounts does not match promotionDecision.blockerClassCounts."
                            $artifactValidationOk = $false
                        }
                        if (-not (Test-LiveHistoryPromotionBlockersMatch -Actual (Get-JsonArray $legacyPromotionDecision "blockers") -Expected (Get-JsonArray $promotionDecision "blockers"))) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-promotion-alias-mismatch" "Artifact '$artifactId' latestNr5Decision.blockers does not match promotionDecision.blockers."
                            $artifactValidationOk = $false
                        }
                    }
                    if ($validPromotionStatuses -notcontains $promotionStatus) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-promotion-status-invalid" "Artifact '$artifactId' reports unsupported promotionDecision.status='$promotionStatus'."
                        $artifactValidationOk = $false
                    }
                    if ($defaultBehaviorChangeReady) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-default-change-ready" "Artifact '$artifactId' reports defaultBehaviorChangeReady=true; live history can only authorize candidate comparison review."
                        $artifactValidationOk = $false
                    }
                    if ($promotable -ne $candidatePromotionReady) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-promotion-flag-mismatch" "Artifact '$artifactId' reports promotable=$promotable but candidatePromotionReady=$candidatePromotionReady."
                        $artifactValidationOk = $false
                    }
                    if ([string](Get-JsonValue $promotionDecision "latestTraceId") -ne [string](Get-JsonValue $latestTrace "traceId")) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-promotion-latest-mismatch" "Artifact '$artifactId' promotionDecision.latestTraceId does not match latestTrace.traceId."
                        $artifactValidationOk = $false
                    }
                    foreach ($fieldName in @("promotionScope", "status", "latestTraceId", "recommendedTraceId", "recommendedTraceRole", "referenceTraceId", "referenceTraceRole", "nextAction")) {
                        $actualPromotionValue = [string](Get-JsonValue $promotionDecision $fieldName)
                        $expectedPromotionValue = [string](Get-JsonValue $expectedPromotionDecision $fieldName)
                        if (-not [string]::Equals($actualPromotionValue, $expectedPromotionValue, [StringComparison]::Ordinal)) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-promotion-decision-field-mismatch" "Artifact '$artifactId' promotionDecision.$fieldName='$actualPromotionValue' but latest trace evidence implies '$expectedPromotionValue'."
                            $artifactValidationOk = $false
                        }
                    }
                    foreach ($fieldName in @("promotable", "candidatePromotionReady", "candidateComparisonReady", "defaultBehaviorChangeReady", "latestIsBestBalanced")) {
                        $actualPromotionFlag = Test-Truthy (Get-JsonValue $promotionDecision $fieldName)
                        $expectedPromotionFlag = Test-Truthy (Get-JsonValue $expectedPromotionDecision $fieldName)
                        if ($actualPromotionFlag -ne $expectedPromotionFlag) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-promotion-decision-flag-mismatch" "Artifact '$artifactId' promotionDecision.$fieldName=$actualPromotionFlag but latest trace evidence implies $expectedPromotionFlag."
                            $artifactValidationOk = $false
                        }
                    }
                    foreach ($fieldName in @("latestSafetyRiskScore", "bestBalancedSafetyRiskScore", "riskScoreDeltaVsBestBalanced", "blockerCount")) {
                        if (-not (Test-LiveHistoryScalarEquivalent -Actual (Get-JsonValue $promotionDecision $fieldName) -Expected (Get-JsonValue $expectedPromotionDecision $fieldName))) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-promotion-decision-numeric-field-mismatch" "Artifact '$artifactId' promotionDecision.$fieldName does not match the trace-derived promotion decision."
                            $artifactValidationOk = $false
                        }
                    }
                    if (-not (Test-StringArraySame -Actual (Get-JsonArray $promotionDecision "blockerClasses") -Expected (Get-JsonArray $expectedPromotionDecision "blockerClasses"))) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-promotion-decision-blockers-mismatch" "Artifact '$artifactId' promotionDecision.blockerClasses does not match the latest trace safety signals."
                        $artifactValidationOk = $false
                    }
                    if (-not (Test-LiveHistorySafetyClassCountsMatch -Actual (Get-JsonArray $promotionDecision "blockerClassCounts") -Expected (Get-JsonArray $expectedPromotionDecision "blockerClassCounts"))) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-promotion-decision-blockers-mismatch" "Artifact '$artifactId' promotionDecision.blockerClassCounts does not match the latest trace safety signals."
                        $artifactValidationOk = $false
                    }
                    if (-not (Test-LiveHistoryPromotionBlockersMatch -Actual (Get-JsonArray $promotionDecision "blockers") -Expected (Get-JsonArray $expectedPromotionDecision "blockers"))) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-promotion-decision-blockers-mismatch" "Artifact '$artifactId' promotionDecision.blockers is not the exact latest-trace safety signal projection."
                        $artifactValidationOk = $false
                    }
                    if ($readinessFieldsRequired -and -not (Test-LiveHistoryReadinessSummaryMatches -Actual (Get-JsonArray $promotionDecision "safetyClassReadiness") -Expected (Get-JsonArray $expectedPromotionDecision "safetyClassReadiness"))) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-promotion-decision-readiness-mismatch" "Artifact '$artifactId' promotionDecision.safetyClassReadiness does not match the trace-derived blocker readiness summary."
                        $artifactValidationOk = $false
                    }
                    if ($readinessFieldsRequired -and -not (Test-LiveHistoryReadinessSummaryMatches -Actual (Get-JsonArray $promotionDecision "blockerGapSummary") -Expected (Get-JsonArray $expectedPromotionDecision "blockerGapSummary"))) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-promotion-decision-readiness-mismatch" "Artifact '$artifactId' promotionDecision.blockerGapSummary does not match the trace-derived blocker readiness summary."
                        $artifactValidationOk = $false
                    }
                    $validPromotionTraceRoles = @("latest", "best-balanced", "best-weak-signal", "lowest-pumping")
                    if ($promotionStatus -ne "no-nr5-history") {
                        if ($validPromotionTraceRoles -notcontains $promotionRecommendedTraceRole) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-promotion-role-invalid" "Artifact '$artifactId' promotionDecision.recommendedTraceRole='$promotionRecommendedTraceRole' is not a concrete trace role."
                            $artifactValidationOk = $false
                        }
                        if ($validPromotionTraceRoles -notcontains $promotionReferenceTraceRole) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-promotion-role-invalid" "Artifact '$artifactId' promotionDecision.referenceTraceRole='$promotionReferenceTraceRole' is not a concrete trace role."
                            $artifactValidationOk = $false
                        }

                        $expectedRecommendedTraceId = Get-LiveHistoryTraceIdForRole -Role $promotionRecommendedTraceRole -LatestTrace $latestTrace -BestBalancedTrace $bestBalancedTrace -BestWeakSignalTrace $bestWeakSignalTrace -LowestPumpingTrace $lowestPumpingTrace
                        if (-not [string]::IsNullOrWhiteSpace($expectedRecommendedTraceId) -and -not [string]::Equals($promotionRecommendedTraceId, $expectedRecommendedTraceId, [StringComparison]::OrdinalIgnoreCase)) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-promotion-role-id-mismatch" "Artifact '$artifactId' promotionDecision.recommendedTraceRole='$promotionRecommendedTraceRole' does not match recommendedTraceId='$promotionRecommendedTraceId'."
                            $artifactValidationOk = $false
                        }

                        $expectedReferenceTraceId = Get-LiveHistoryTraceIdForRole -Role $promotionReferenceTraceRole -LatestTrace $latestTrace -BestBalancedTrace $bestBalancedTrace -BestWeakSignalTrace $bestWeakSignalTrace -LowestPumpingTrace $lowestPumpingTrace
                        if (-not [string]::IsNullOrWhiteSpace($expectedReferenceTraceId) -and -not [string]::Equals($promotionReferenceTraceId, $expectedReferenceTraceId, [StringComparison]::OrdinalIgnoreCase)) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-promotion-role-id-mismatch" "Artifact '$artifactId' promotionDecision.referenceTraceRole='$promotionReferenceTraceRole' does not match referenceTraceId='$promotionReferenceTraceId'."
                            $artifactValidationOk = $false
                        }
                        if ($null -eq (Get-LiveHistoryTraceRecordById -Records $traceRecords -TraceId $promotionRecommendedTraceId)) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-promotion-trace-missing" "Artifact '$artifactId' promotionDecision.recommendedTraceId='$promotionRecommendedTraceId' does not exist in traces."
                            $artifactValidationOk = $false
                        }
                        if ($null -eq (Get-LiveHistoryTraceRecordById -Records $traceRecords -TraceId $promotionReferenceTraceId)) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-promotion-trace-missing" "Artifact '$artifactId' promotionDecision.referenceTraceId='$promotionReferenceTraceId' does not exist in traces."
                            $artifactValidationOk = $false
                        }
                    }
                    if ((Get-TotalSafetyClassCount -Counts $promotionBlockerClassCounts) -ne $promotionBlockerCount) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-promotion-blocker-count-mismatch" "Artifact '$artifactId' promotionDecision blockerCount does not match blockerClassCounts."
                        $artifactValidationOk = $false
                    }
                    foreach ($blockerClass in @($promotionBlockerClasses)) {
                        if (-not [string]::IsNullOrWhiteSpace($blockerClass) -and (Get-SafetyClassCount -Counts $promotionBlockerClassCounts -SafetyClass $blockerClass) -le 0) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-promotion-blocker-class-count-missing" "Artifact '$artifactId' promotionDecision.blockerClasses includes '$blockerClass' without a matching blockerClassCounts entry."
                            $artifactValidationOk = $false
                        }
                    }
                    if ($promotionStatus -eq "ready-for-candidate-comparison" -and -not $candidatePromotionReady) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-promotion-status-mismatch" "Artifact '$artifactId' reports ready status but candidatePromotionReady=false."
                        $artifactValidationOk = $false
                    }
                    if ($promotionStatus -ne "ready-for-candidate-comparison" -and $candidatePromotionReady) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-promotion-status-mismatch" "Artifact '$artifactId' reports candidatePromotionReady=true but status='$promotionStatus'."
                        $artifactValidationOk = $false
                    }
                    if ($candidatePromotionReady -and -not $latestCandidateReady) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-latest-trace-not-ready" "Artifact '$artifactId' says the latest trace is promotable, but latestTrace.candidateComparisonReady is false."
                        $artifactValidationOk = $false
                    }
                    foreach ($latestClass in @($latestPromotionBlockerClasses)) {
                        if (-not [string]::IsNullOrWhiteSpace($latestClass) -and $promotionBlockerClasses -notcontains $latestClass) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-promotion-blocker-class-missing" "Artifact '$artifactId' latestTrace has blocker class '$latestClass' but promotionDecision.blockerClasses does not include it."
                            $artifactValidationOk = $false
                        }
                    }
                    $latestTraceIdForSignals = [string](Get-JsonValue $latestTrace "traceId")
                    $latestSignalSourceTrace = $latestTrace
                    foreach ($traceRecord in @($traceRecords)) {
                        if ([string]::Equals([string](Get-JsonValue $traceRecord "traceId"), $latestTraceIdForSignals, [StringComparison]::OrdinalIgnoreCase)) {
                            $latestSignalSourceTrace = $traceRecord
                            break
                        }
                    }
                    $latestSafetySignals = @(Get-JsonArray $latestSignalSourceTrace "safetySignals")
                    foreach ($blocker in @($promotionBlockers)) {
                        $blockerCode = [string](Get-JsonValue $blocker "code")
                        if ([string]::IsNullOrWhiteSpace($blockerCode)) {
                            continue
                        }

                        $matchingSignal = $null
                        foreach ($latestSignal in @($latestSafetySignals)) {
                            if ([string]::Equals([string](Get-JsonValue $latestSignal "name"), $blockerCode, [StringComparison]::OrdinalIgnoreCase)) {
                                $matchingSignal = $latestSignal
                                break
                            }
                        }
                        if ($null -eq $matchingSignal) {
                            continue
                        }

                        foreach ($field in @("thresholdDirection", "unit", "comparison", "readinessGapUnit")) {
                            $actualBlockerValue = Get-JsonValue $blocker $field
                            $expectedBlockerValue = Get-JsonValue $matchingSignal $field
                            if (($readinessFieldsRequired -or $null -ne $actualBlockerValue -or $null -ne $expectedBlockerValue) -and
                                -not [string]::Equals([string]$actualBlockerValue, [string]$expectedBlockerValue, [StringComparison]::OrdinalIgnoreCase)) {
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-promotion-blocker-readiness-mismatch" "Artifact '$artifactId' promotion blocker '$blockerCode' does not copy $field from the latest trace safety signal."
                                $artifactValidationOk = $false
                            }
                        }
                        foreach ($field in @("readinessGap", "readinessMargin", "readinessGapScore")) {
                            $actualBlockerValue = Get-JsonValue $blocker $field
                            $expectedBlockerValue = Get-JsonValue $matchingSignal $field
                            if (($readinessFieldsRequired -or $null -ne $actualBlockerValue -or $null -ne $expectedBlockerValue) -and
                                -not (Test-NumericClose -Actual $actualBlockerValue -Expected $expectedBlockerValue)) {
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-promotion-blocker-readiness-mismatch" "Artifact '$artifactId' promotion blocker '$blockerCode' does not copy $field from the latest trace safety signal."
                                $artifactValidationOk = $false
                            }
                        }
                    }
                    foreach ($trace in @($traceRecords)) {
                        $traceId = [string](Get-JsonValue $trace "traceId")
                        $traceCandidateReady = Test-Truthy (Get-JsonValue $trace "candidateComparisonReady")
                        $tracePromotable = Test-Truthy (Get-JsonValue $trace "promotable")
                        $traceNr5Samples = [int](Get-NumericValueOrDefault (Get-JsonValue $trace "nr5SampleCount"))
                        $traceReady = Test-Truthy (Get-JsonValue $trace "readyForBenchmarkTrace")
                        $traceBlockerCount = [int](Get-NumericValueOrDefault (Get-JsonValue $trace "promotionBlockerCount"))
                        $traceBlockerClassCounts = @(Get-JsonArray $trace "promotionBlockerClassCounts")
                        $traceBlockerClassCountTotal = Get-TotalSafetyClassCount -Counts $traceBlockerClassCounts
                        $traceBlockerClasses = Get-StringArray -Object $trace -Name "promotionBlockerClasses"
                        $traceSignals = @(Get-JsonArray $trace "safetySignals")
                        $traceSafetyClassCounts = @(Get-JsonArray $trace "safetyClassCounts")
                        $expectedTraceSafetyClassCounts = @(New-LiveHistorySafetyClassCountsFromSignals -Signals $traceSignals)
                        $expectedTraceReadinessSummary = @(New-LiveHistoryReadinessSummaryFromSignals -Signals $traceSignals)
                        $traceReadinessSummary = @(Get-JsonArray $trace "readinessGapSummary")
                        if ($traceReadinessSummary.Count -eq 0) {
                            $traceReadinessSummary = @(Get-JsonArray $trace "safetyClassReadiness")
                        }
                        $sourcePath = [string](Get-JsonValue $trace "path")
                        $sourceRecord = [ordered]@{
                            artifactId = $artifactId
                            artifactKind = $artifactKind
                            sourceType = "live-diagnostics-history-trace-source"
                            traceId = $traceId
                            scenarioId = [string](Get-JsonValue $trace "scenarioId")
                            comparisonId = [string](Get-JsonValue $trace "comparisonId")
                            label = [string](Get-JsonValue $trace "label")
                            path = $sourcePath
                            jsonlPath = [string](Get-JsonValue $trace "jsonlPath")
                            required = $effectiveRequired
                            ok = $false
                            sourceStatus = "unchecked"
                            summaryHashStatus = "not-evaluated"
                            jsonlHashStatus = "not-evaluated"
                            summarySha256 = [string](Get-JsonValue $trace "summarySha256")
                            actualSummarySha256 = ""
                            jsonlSha256 = [string](Get-JsonValue $trace "jsonlSha256")
                            actualJsonlSha256 = ""
                        }
                        $artifactReferencedFiles.Add($sourceRecord) | Out-Null
                        $sourceRecordOk = $true
                        $expectedSourceTrace = $null
                        if ([string]::IsNullOrWhiteSpace($sourcePath)) {
                            $sourceRecord["sourceStatus"] = "path-missing"
                            $sourceRecordOk = $false
                            $traceSourceMissingCount++
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-trace-source-path-missing" "Artifact '$artifactId' trace '$traceId' is missing path, so the trace cannot be bound back to a watcher summary."
                            $artifactValidationOk = $false
                        }
                        else {
                            $resolvedSourcePath = Get-BundlePath -BundlePath $bundlePath -Path $sourcePath
                            if (-not (Test-Path -LiteralPath $resolvedSourcePath -PathType Leaf)) {
                                $sourceRecord["sourceStatus"] = "file-missing"
                                $sourceRecordOk = $false
                                $traceSourceMissingCount++
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-trace-source-file-missing" "Artifact '$artifactId' trace '$traceId' references missing watcher summary '$sourcePath'."
                                $artifactValidationOk = $false
                            }
                            else {
                                $sourceSummary = $null
                                try {
                                    $sourceSummary = Read-JsonFile $resolvedSourcePath
                                }
                                catch {
                                    $sourceRecord["sourceStatus"] = "json-invalid"
                                    $sourceRecordOk = $false
                                    $traceSourceInvalidCount++
                                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-trace-source-json-invalid" "Artifact '$artifactId' trace '$traceId' references watcher summary '$sourcePath' that cannot be parsed: $($_.Exception.Message)"
                                    $artifactValidationOk = $false
                                }

                                if ($null -ne $sourceSummary) {
                                    $sourceTool = [string](Get-JsonValue $sourceSummary "tool")
                                    if ($sourceTool -ne "watch-dsp-live-diagnostics") {
                                        $sourceRecord["sourceStatus"] = "tool-invalid"
                                        $sourceRecordOk = $false
                                        $traceSourceInvalidCount++
                                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-trace-source-tool-invalid" "Artifact '$artifactId' trace '$traceId' source '$sourcePath' must be generated by watch-dsp-live-diagnostics.ps1; found tool '$sourceTool'."
                                        $artifactValidationOk = $false
                                    }
                                    else {
                                        $sourceJsonlPath = [string](Get-JsonValue $sourceSummary "jsonlPath")
                                        if (-not [string]::IsNullOrWhiteSpace($sourceJsonlPath)) {
                                            $sourceRecord["jsonlPath"] = $sourceJsonlPath
                                            $resolvedSourceJsonlPath = Resolve-LiveHistoryOptionalFilePath -Path $sourceJsonlPath -BaseDirectory (Split-Path -Parent $resolvedSourcePath)
                                            if (-not (Test-Path -LiteralPath $resolvedSourceJsonlPath -PathType Leaf)) {
                                                $sourceRecord["jsonlHashStatus"] = "file-missing"
                                                $sourceRecordOk = $false
                                                $traceSourceJsonlMissingCount++
                                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-trace-source-jsonl-file-missing" "Artifact '$artifactId' trace '$traceId' source '$sourcePath' references missing JSONL trace '$sourceJsonlPath'."
                                                $artifactValidationOk = $false
                                            }
                                        }

                                        try {
                                            $expectedSourceTrace = New-LiveHistoryExpectedTraceRecordFromSummary -Path $resolvedSourcePath -Report $sourceSummary -PortableRoot $bundlePath
                                            $sourceRecord["sourceStatus"] = "matched"
                                            $traceSourceCheckedCount++
                                        }
                                        catch {
                                            $sourceRecord["sourceStatus"] = "conversion-invalid"
                                            $sourceRecordOk = $false
                                            $traceSourceInvalidCount++
                                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-trace-source-json-invalid" "Artifact '$artifactId' trace '$traceId' source '$sourcePath' could not be converted into a live-history trace record: $($_.Exception.Message)"
                                            $artifactValidationOk = $false
                                        }
                                    }
                                }
                            }
                        }

                        if ($null -ne $expectedSourceTrace) {
                            $textMismatches = @(Get-LiveHistoryTraceSourceTextMismatches -Actual $trace -Expected $expectedSourceTrace -Fields @(
                                    "traceId",
                                    "sortKeySource",
                                    "scenarioId",
                                    "comparisonId",
                                    "label",
                                    "path",
                                    "jsonlPath",
                                    "endpoint",
                                    "sourceMode",
                                    "trendStatus",
                                    "nr5TuningTraceStatus",
                                    "reviewStatus"
                            ))
                            if ($textMismatches.Count -gt 0) {
                                $sourceRecord["sourceStatus"] = "content-mismatch"
                                $sourceRecordOk = $false
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-trace-source-field-mismatch" "Artifact '$artifactId' trace '$traceId' does not match watcher summary '$sourcePath' for field(s): $($textMismatches -join ', ')."
                                $artifactValidationOk = $false
                            }

                            $dateMismatches = @(Get-LiveHistoryTraceSourceDateMismatches -Actual $trace -Expected $expectedSourceTrace -Fields @(
                                    "traceSequenceUtc",
                                    "startedUtc",
                                    "completedUtc",
                                    "generatedUtc"
                            ))
                            if ($dateMismatches.Count -gt 0) {
                                $sourceRecord["sourceStatus"] = "content-mismatch"
                                $sourceRecordOk = $false
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-trace-source-field-mismatch" "Artifact '$artifactId' trace '$traceId' does not match watcher summary '$sourcePath' for date field(s): $($dateMismatches -join ', ')."
                                $artifactValidationOk = $false
                            }

                            $actualSummarySha256 = [string](Get-JsonValue $trace "summarySha256")
                            $expectedSummarySha256 = [string](Get-JsonValue $expectedSourceTrace "summarySha256")
                            $sourceRecord["actualSummarySha256"] = $expectedSummarySha256
                            if ([string]::IsNullOrWhiteSpace($actualSummarySha256)) {
                                $sourceRecord["summaryHashStatus"] = "missing"
                                $sourceRecordOk = $false
                                $traceSourceSummaryHashMissingCount++
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-trace-source-summary-hash-missing" "Artifact '$artifactId' trace '$traceId' is missing summarySha256 for watcher summary '$sourcePath'."
                                $artifactValidationOk = $false
                            }
                            elseif (-not [string]::Equals($actualSummarySha256.Trim(), $expectedSummarySha256, [StringComparison]::OrdinalIgnoreCase)) {
                                $sourceRecord["summaryHashStatus"] = "mismatch"
                                $sourceRecordOk = $false
                                $traceSourceSummaryHashPresentCount++
                                $traceSourceSummaryHashMismatchCount++
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-trace-source-summary-hash-mismatch" "Artifact '$artifactId' trace '$traceId' summarySha256 does not match watcher summary '$sourcePath'."
                                $artifactValidationOk = $false
                            }
                            else {
                                $sourceRecord["summaryHashStatus"] = "matched"
                                $traceSourceSummaryHashPresentCount++
                            }

                            $actualJsonlSha256 = [string](Get-JsonValue $trace "jsonlSha256")
                            $expectedJsonlSha256 = [string](Get-JsonValue $expectedSourceTrace "jsonlSha256")
                            $expectedJsonlPath = [string](Get-JsonValue $expectedSourceTrace "jsonlPath")
                            $sourceRecord["jsonlPath"] = $expectedJsonlPath
                            $sourceRecord["actualJsonlSha256"] = $expectedJsonlSha256
                            if (-not [string]::IsNullOrWhiteSpace($expectedJsonlPath) -and [string]::IsNullOrWhiteSpace($actualJsonlSha256)) {
                                if ($sourceRecord["jsonlHashStatus"] -ne "file-missing") {
                                    $sourceRecord["jsonlHashStatus"] = "missing"
                                }
                                $sourceRecordOk = $false
                                $traceSourceJsonlHashMissingCount++
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-trace-source-jsonl-hash-missing" "Artifact '$artifactId' trace '$traceId' is missing jsonlSha256 for JSONL trace '$expectedJsonlPath'."
                                $artifactValidationOk = $false
                            }
                            elseif (-not [string]::IsNullOrWhiteSpace($actualJsonlSha256) -and -not [string]::Equals($actualJsonlSha256.Trim(), $expectedJsonlSha256, [StringComparison]::OrdinalIgnoreCase)) {
                                if ($sourceRecord["jsonlHashStatus"] -ne "file-missing") {
                                    $sourceRecord["jsonlHashStatus"] = "mismatch"
                                }
                                $sourceRecordOk = $false
                                $traceSourceJsonlHashPresentCount++
                                $traceSourceJsonlHashMismatchCount++
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-trace-source-jsonl-hash-mismatch" "Artifact '$artifactId' trace '$traceId' jsonlSha256 does not match JSONL trace '$expectedJsonlPath'."
                                $artifactValidationOk = $false
                            }
                            elseif (-not [string]::IsNullOrWhiteSpace($actualJsonlSha256)) {
                                if ($sourceRecord["jsonlHashStatus"] -ne "file-missing") {
                                    $sourceRecord["jsonlHashStatus"] = "matched"
                                }
                                $traceSourceJsonlHashPresentCount++
                            }
                            elseif ([string]::IsNullOrWhiteSpace($expectedJsonlPath) -and $sourceRecord["jsonlHashStatus"] -ne "file-missing") {
                                $sourceRecord["jsonlHashStatus"] = "not-applicable"
                            }

                            $scalarMismatches = @(Get-LiveHistoryTraceSourceScalarMismatches -Actual $trace -Expected $expectedSourceTrace -Fields @(
                                    "sampleCount",
                                    "okSampleCount",
                                    "failedSampleCount",
                                    "hardBlockerSampleCount",
                                    "readyForBenchmarkTrace",
                                    "nr5TuningReadyTrace",
                                    "nr5SampleCount",
                                    "nr5ProbabilityDiagnosticSampleCount",
                                    "nr5AgcDiagnosticSampleCount",
                                    "weakInputSampleCount",
                                    "weakRecoveredSampleCount",
                                    "weakDropoutSampleCount",
                                    "hotMakeupSampleCount",
                                    "weakRecoveryPct",
                                    "weakStrongOutputGapDb",
                                    "nr5OutputMovementDb",
                                    "audioRmsMovementDb",
                                    "nr5MakeupMovementDb",
                                    "nr5MakeupMaxDb",
                                    "nr5RecoveryDriveMovement",
                                    "nr5TextureFillAverage",
                                    "nr5SignalProbabilityAverage",
                                    "audioPeakMaxDbfs",
                                    "adcHeadroomMinDb",
                                    "monitorBacklogMaxSamples",
                                    "latencyAverageMs",
                                    "audioFreshPct",
                                    "rxMetersFreshPct",
                                    "readinessGapSignalCount",
                                    "readinessGapNumericSignalCount",
                                    "readinessGapMax",
                                    "safetyRiskScore",
                                    "candidateComparisonReady",
                                    "promotable",
                                    "promotionBlockerCount"
                            ))
                            if ($scalarMismatches.Count -gt 0) {
                                $sourceRecord["sourceStatus"] = "content-mismatch"
                                $sourceRecordOk = $false
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-trace-source-numeric-field-mismatch" "Artifact '$artifactId' trace '$traceId' numeric/boolean fields do not match watcher summary '$sourcePath': $($scalarMismatches -join ', ')."
                                $artifactValidationOk = $false
                            }

                            if (-not (Test-LiveHistorySafetySignalsMatch -Actual $traceSignals -Expected (Get-JsonArray $expectedSourceTrace "safetySignals"))) {
                                $sourceRecord["sourceStatus"] = "content-mismatch"
                                $sourceRecordOk = $false
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-trace-source-safety-signals-mismatch" "Artifact '$artifactId' trace '$traceId' safetySignals do not match watcher summary '$sourcePath'."
                                $artifactValidationOk = $false
                            }
                            if (-not (Test-LiveHistorySafetyClassCountsMatch -Actual $traceSafetyClassCounts -Expected (Get-JsonArray $expectedSourceTrace "safetyClassCounts"))) {
                                $sourceRecord["sourceStatus"] = "content-mismatch"
                                $sourceRecordOk = $false
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-trace-source-safety-count-mismatch" "Artifact '$artifactId' trace '$traceId' safetyClassCounts do not match watcher summary '$sourcePath'."
                                $artifactValidationOk = $false
                            }
                            if ($readinessFieldsRequired -and -not (Test-LiveHistoryReadinessSummaryMatches -Actual $traceReadinessSummary -Expected (Get-JsonArray $expectedSourceTrace "readinessGapSummary"))) {
                                $sourceRecord["sourceStatus"] = "content-mismatch"
                                $sourceRecordOk = $false
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-trace-source-readiness-mismatch" "Artifact '$artifactId' trace '$traceId' readinessGapSummary does not match watcher summary '$sourcePath'."
                                $artifactValidationOk = $false
                            }
                            if (-not (Test-StringArraySame -Actual $traceBlockerClasses -Expected (Get-JsonArray $expectedSourceTrace "promotionBlockerClasses"))) {
                                $sourceRecord["sourceStatus"] = "content-mismatch"
                                $sourceRecordOk = $false
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-trace-source-promotion-mismatch" "Artifact '$artifactId' trace '$traceId' promotionBlockerClasses do not match watcher summary '$sourcePath'."
                                $artifactValidationOk = $false
                            }
                            if (-not (Test-LiveHistorySafetyClassCountsMatch -Actual $traceBlockerClassCounts -Expected (Get-JsonArray $expectedSourceTrace "promotionBlockerClassCounts"))) {
                                $sourceRecord["sourceStatus"] = "content-mismatch"
                                $sourceRecordOk = $false
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-trace-source-promotion-mismatch" "Artifact '$artifactId' trace '$traceId' promotionBlockerClassCounts do not match watcher summary '$sourcePath'."
                                $artifactValidationOk = $false
                            }
                            if (-not (Test-OrderedStringArraysEqual -Actual (Get-JsonArray $trace "recommendations") -Expected (Get-JsonArray $expectedSourceTrace "recommendations"))) {
                                $sourceRecord["sourceStatus"] = "content-mismatch"
                                $sourceRecordOk = $false
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-trace-source-field-mismatch" "Artifact '$artifactId' trace '$traceId' recommendations do not match watcher summary '$sourcePath'."
                                $artifactValidationOk = $false
                            }
                        }
                        $sourceRecord["ok"] = ($sourceRecordOk -and $null -ne $expectedSourceTrace)

                        if ($traceCandidateReady -ne $tracePromotable) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-trace-promotion-flag-mismatch" "Artifact '$artifactId' trace '$traceId' reports candidateComparisonReady=$traceCandidateReady but promotable=$tracePromotable."
                            $artifactValidationOk = $false
                        }
                        if ($traceBlockerCount -ne $traceBlockerClassCountTotal) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-trace-blocker-count-mismatch" "Artifact '$artifactId' trace '$traceId' promotionBlockerCount does not match promotionBlockerClassCounts."
                            $artifactValidationOk = $false
                        }
                        if ($traceCandidateReady -and ($traceNr5Samples -le 0 -or -not $traceReady -or $traceBlockerCount -gt 0 -or $traceBlockerClasses.Count -gt 0)) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-trace-readiness-inconsistent" "Artifact '$artifactId' trace '$traceId' is candidate-ready but has no NR5 samples, is not benchmark-ready, or still has blockers."
                            $artifactValidationOk = $false
                        }
                        if (-not (Test-LiveHistorySafetyClassCountsMatch -Actual $traceSafetyClassCounts -Expected $expectedTraceSafetyClassCounts)) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-trace-safety-count-mismatch" "Artifact '$artifactId' trace '$traceId' safetyClassCounts does not match safetySignals."
                            $artifactValidationOk = $false
                        }
                        if ($readinessFieldsRequired -and -not (Test-LiveHistoryReadinessSummaryMatches -Actual $traceReadinessSummary -Expected $expectedTraceReadinessSummary)) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-trace-readiness-gap-mismatch" "Artifact '$artifactId' trace '$traceId' readinessGapSummary does not match safetySignals."
                            $artifactValidationOk = $false
                        }
                        $traceReadinessSignalCount = Get-NumericValue (Get-JsonValue $trace "readinessGapSignalCount")
                        if (($readinessFieldsRequired -or $null -ne $traceReadinessSignalCount) -and ($null -eq $traceReadinessSignalCount -or [int][Math]::Round($traceReadinessSignalCount) -ne (Get-LiveHistoryReadinessSummarySignalCount -Summary $expectedTraceReadinessSummary))) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-trace-readiness-gap-count-mismatch" "Artifact '$artifactId' trace '$traceId' readinessGapSignalCount does not match safetySignals."
                            $artifactValidationOk = $false
                        }
                        $traceReadinessNumericSignalCount = Get-NumericValue (Get-JsonValue $trace "readinessGapNumericSignalCount")
                        if (($readinessFieldsRequired -or $null -ne $traceReadinessNumericSignalCount) -and ($null -eq $traceReadinessNumericSignalCount -or [int][Math]::Round($traceReadinessNumericSignalCount) -ne (Get-LiveHistoryReadinessSummaryNumericSignalCount -Summary $expectedTraceReadinessSummary))) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-trace-readiness-gap-numeric-count-mismatch" "Artifact '$artifactId' trace '$traceId' readinessGapNumericSignalCount does not match safetySignals."
                            $artifactValidationOk = $false
                        }
                        $traceReadinessMax = Get-JsonValue $trace "readinessGapMax"
                        if (($readinessFieldsRequired -or $null -ne $traceReadinessMax) -and -not (Test-NumericClose -Actual $traceReadinessMax -Expected (Get-LiveHistoryReadinessSummaryMax -Summary $expectedTraceReadinessSummary))) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-trace-readiness-gap-max-mismatch" "Artifact '$artifactId' trace '$traceId' readinessGapMax does not match safetySignals."
                            $artifactValidationOk = $false
                        }
                        foreach ($signal in @($traceSignals)) {
                            $signalName = [string](Get-JsonValue $signal "name")
                            $expectedReadiness = Get-LiveHistorySignalReadiness $signal
                            $expectedGap = Get-NumericValue (Get-JsonValue $expectedReadiness "readinessGap")
                            if ($null -ne $expectedGap -and $expectedGap -le 0.001) {
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-readiness-signal-not-failed" "Artifact '$artifactId' trace '$traceId' safety signal '$signalName' does not exceed its threshold."
                                $artifactValidationOk = $false
                            }

                            $signalReadinessFieldsPresent = $false
                            foreach ($field in @("thresholdDirection", "unit", "readinessGap", "readinessMargin", "readinessGapUnit", "readinessGapScore")) {
                                if ($null -ne (Get-JsonValue $signal $field)) {
                                    $signalReadinessFieldsPresent = $true
                                }
                            }
                            if ($readinessFieldsRequired -or $signalReadinessFieldsPresent) {
                                foreach ($field in @("thresholdDirection", "unit", "comparison")) {
                                    $actualFieldValue = [string](Get-JsonValue $signal $field)
                                    $expectedFieldValue = [string](Get-JsonValue $expectedReadiness $field)
                                    if ([string]::IsNullOrWhiteSpace($actualFieldValue) -or -not [string]::Equals($actualFieldValue, $expectedFieldValue, [StringComparison]::OrdinalIgnoreCase)) {
                                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-readiness-field-mismatch" "Artifact '$artifactId' trace '$traceId' safety signal '$signalName' has invalid $field."
                                        $artifactValidationOk = $false
                                    }
                                }
                                $actualGapUnit = [string](Get-JsonValue $signal "readinessGapUnit")
                                if ([string]::IsNullOrWhiteSpace($actualGapUnit) -or -not [string]::Equals($actualGapUnit, [string](Get-JsonValue $expectedReadiness "unit"), [StringComparison]::OrdinalIgnoreCase)) {
                                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-readiness-field-mismatch" "Artifact '$artifactId' trace '$traceId' safety signal '$signalName' has invalid readinessGapUnit."
                                    $artifactValidationOk = $false
                                }
                                foreach ($field in @("readinessGap", "readinessMargin", "readinessGapScore")) {
                                    if (-not (Test-NumericClose -Actual (Get-JsonValue $signal $field) -Expected (Get-JsonValue $expectedReadiness $field))) {
                                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-readiness-field-mismatch" "Artifact '$artifactId' trace '$traceId' safety signal '$signalName' has invalid $field."
                                        $artifactValidationOk = $false
                                    }
                                }
                            }
                        }
                    }
                    if (-not $candidatePromotionReady) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-promotion-not-ready" "Artifact '$artifactId' reports promotionDecision.status='$promotionStatus'; use the recommended trace or recapture before treating the latest trace as candidate evidence."
                        if ($effectiveRequired) {
                            $artifactValidationOk = $false
                        }
                    }
                }
                $traceSourceStatus = "not-evaluated"
                if ($traceRecords.Count -gt 0) {
                    if ($traceSourceCheckedCount -ne $traceRecords.Count -or
                        $traceSourceMissingCount -gt 0 -or
                        $traceSourceInvalidCount -gt 0 -or
                        $traceSourceJsonlMissingCount -gt 0) {
                        $traceSourceStatus = "source-incomplete"
                    }
                    elseif ($traceSourceSummaryHashMissingCount -gt 0 -or
                        $traceSourceSummaryHashMismatchCount -gt 0 -or
                        $traceSourceJsonlHashMissingCount -gt 0 -or
                        $traceSourceJsonlHashMismatchCount -gt 0) {
                        $traceSourceStatus = "hash-invalid"
                    }
                    elseif ($traceSourceSummaryHashPresentCount -eq $traceSourceCheckedCount -and
                        $traceSourceJsonlHashPresentCount -eq $traceSourceCheckedCount) {
                        $traceSourceStatus = "hash-ready"
                    }
                    else {
                        $traceSourceStatus = "hash-incomplete"
                    }
                }
                $liveDiagnosticsHistoryEvidence["traceSourceStatus"] = $traceSourceStatus
                $liveDiagnosticsHistoryEvidence["traceSourceCheckedCount"] = $traceSourceCheckedCount
                $liveDiagnosticsHistoryEvidence["traceSourceMissingCount"] = $traceSourceMissingCount
                $liveDiagnosticsHistoryEvidence["traceSourceInvalidCount"] = $traceSourceInvalidCount
                $liveDiagnosticsHistoryEvidence["traceSourceJsonlMissingCount"] = $traceSourceJsonlMissingCount
                $liveDiagnosticsHistoryEvidence["traceSourceSummaryHashPresentCount"] = $traceSourceSummaryHashPresentCount
                $liveDiagnosticsHistoryEvidence["traceSourceSummaryHashMissingCount"] = $traceSourceSummaryHashMissingCount
                $liveDiagnosticsHistoryEvidence["traceSourceSummaryHashMismatchCount"] = $traceSourceSummaryHashMismatchCount
                $liveDiagnosticsHistoryEvidence["traceSourceJsonlHashPresentCount"] = $traceSourceJsonlHashPresentCount
                $liveDiagnosticsHistoryEvidence["traceSourceJsonlHashMissingCount"] = $traceSourceJsonlHashMissingCount
                $liveDiagnosticsHistoryEvidence["traceSourceJsonlHashMismatchCount"] = $traceSourceJsonlHashMismatchCount
                if ($tool -eq "summarize-dsp-live-diagnostics-history" -and -not $bundleRelativePaths) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-paths-not-bundle-relative" "Artifact '$artifactId' report was not generated with bundleRelativePaths=true; rerun summarize-dsp-live-diagnostics-history.ps1 with -BundleDir."
                    $artifactValidationOk = $false
                }
                if ($absolutePaths.Count -gt 0) {
                    $pathExamples = Get-PathExampleText $absolutePaths
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-history-absolute-paths" "Artifact '$artifactId' report contains $($absolutePaths.Count) absolute path(s); rerun with -BundleDir so capture bundles are portable. Examples: $pathExamples"
                    $artifactValidationOk = $false
                }

                if ($artifactValidationOk) {
                    $liveDiagnosticsHistoryEvidence["readyForReview"] = $true
                    $liveDiagnosticsHistoryEvidence["status"] = "ready"
                }
                else {
                    $liveDiagnosticsHistoryEvidence["readyForReview"] = $false
                    $liveDiagnosticsHistoryEvidence["status"] = "not-ready"
                }
            }

            if ($artifactKind -ieq "external-candidate-report-json" -or $artifactId -eq $externalEngineBakeoffArtifactId) {
                $externalEngineBakeoffEvidence["present"] = $true

                $tool = [string](Get-JsonValue $artifactJson "tool")
                if ($tool -ne "summarize-dsp-external-engine-candidates") {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-tool-invalid" "Artifact '$artifactId' must be generated by summarize-dsp-external-engine-candidates.ps1."
                    $artifactValidationOk = $false
                }

                $schemaVersion = [int](Get-NumericValueOrDefault (Get-JsonValue $artifactJson "schemaVersion") 1)
                $candidatePath = [string](Get-JsonValue $artifactJson "candidatePath")
                $candidateSha256 = [string](Get-JsonValue $artifactJson "candidateSha256")
                $snapshotPath = [string](Get-JsonValue $artifactJson "snapshotPath")
                $snapshotSha256 = [string](Get-JsonValue $artifactJson "snapshotSha256")
                $readyForReview = Test-Truthy (Get-JsonValue $artifactJson "readyForReview")
                $candidateCount = [int](Get-JsonValue $artifactJson "candidateCount")
                $safeForBakeoffCount = [int](Get-JsonValue $artifactJson "safeForBakeoffCount")
                $blockedCandidateCount = [int](Get-JsonValue $artifactJson "blockedCandidateCount")
                $integrationReadyCandidateCount = [int](Get-JsonValue $artifactJson "integrationReadyCandidateCount")
                $missingCandidateCount = [int](Get-JsonValue $artifactJson "missingCandidateCount")
                $unsafeCandidateCount = [int](Get-JsonValue $artifactJson "unsafeCandidateCount")
                $snapshotMismatchCount = [int](Get-JsonValue $artifactJson "snapshotMismatchCount")
                $candidateSummaries = @(Get-JsonArray $artifactJson "candidateSummaries")
                $externalBakeoffPlan = Get-JsonValue $artifactJson "externalBakeoffPlan"
                $externalBakeoffPlanRequired = ($schemaVersion -ge 2 -or $null -ne $externalBakeoffPlan)

                $externalEngineBakeoffEvidence["readyForReview"] = $readyForReview
                $externalEngineBakeoffEvidence["candidateCount"] = $candidateCount
                $externalEngineBakeoffEvidence["safeForBakeoffCount"] = $safeForBakeoffCount
                $externalEngineBakeoffEvidence["blockedCandidateCount"] = $blockedCandidateCount
                $externalEngineBakeoffEvidence["integrationReadyCandidateCount"] = $integrationReadyCandidateCount
                $externalEngineBakeoffEvidence["missingCandidateCount"] = $missingCandidateCount
                $externalEngineBakeoffEvidence["unsafeCandidateCount"] = $unsafeCandidateCount
                $externalEngineBakeoffEvidence["snapshotMismatchCount"] = $snapshotMismatchCount
                $externalEngineBakeoffEvidence["candidateSha256"] = $candidateSha256
                $externalEngineBakeoffEvidence["snapshotSha256"] = $snapshotSha256
                $externalEngineBakeoffEvidence["bakeoffPlanCandidateCount"] = [int](Get-NumericValueOrDefault (Get-JsonValue $externalBakeoffPlan "candidatePlanCount"))
                $externalEngineBakeoffEvidence["bakeoffPlanScenarioCount"] = [int](Get-NumericValueOrDefault (Get-JsonValue $externalBakeoffPlan "scenarioCount"))
                $externalEngineBakeoffEvidence["bakeoffPlanDefaultBehaviorChangeReady"] = Test-Truthy (Get-JsonValue $externalBakeoffPlan "defaultBehaviorChangeReady")
                $externalEngineBakeoffEvidence["bakeoffPlanRawWdspIqReplacementAllowed"] = Test-Truthy (Get-JsonValue $externalBakeoffPlan "rawWdspIqReplacementAllowed")

                if ([string]::IsNullOrWhiteSpace($candidatePath)) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-candidate-path-missing" "Artifact '$artifactId' does not declare candidatePath."
                    $artifactValidationOk = $false
                }
                else {
                    $candidateEndpoint = Get-EndpointById $index "external-engine-candidates"
                    $candidateEndpointFile = [string](Get-JsonValue $candidateEndpoint "file")
                    if (-not [string]::IsNullOrWhiteSpace($candidateEndpointFile) -and
                        -not (Test-ComparableEvidencePathSame -BundlePath $bundlePath -ExpectedPath $candidateEndpointFile -ActualPath $candidatePath)) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-candidate-endpoint-path-mismatch" "Artifact '$artifactId' candidatePath='$candidatePath' does not match captured external-engine-candidates endpoint file '$candidateEndpointFile'."
                        $artifactValidationOk = $false
                    }

                    $resolvedCandidatePath = Get-BundlePath $bundlePath $candidatePath
                    if (-not (Test-Path -LiteralPath $resolvedCandidatePath -PathType Leaf)) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-candidate-file-missing" "Artifact '$artifactId' references a missing external-engine candidate catalog: $candidatePath"
                        $artifactValidationOk = $false
                    }
                    elseif ([string]::IsNullOrWhiteSpace($candidateSha256)) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-candidate-hash-missing" "Artifact '$artifactId' does not declare candidateSha256."
                        $artifactValidationOk = $false
                    }
                    else {
                        $actualCandidateSha256 = Get-FileSha256 $resolvedCandidatePath
                        if (-not [string]::Equals($candidateSha256.Trim().ToLowerInvariant(), $actualCandidateSha256, [StringComparison]::OrdinalIgnoreCase)) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-candidate-hash-mismatch" "Artifact '$artifactId' reports candidateSha256='$candidateSha256' but '$candidatePath' hashes to '$actualCandidateSha256'."
                            $artifactValidationOk = $false
                        }
                    }
                }

                $snapshotEndpoint = Get-EndpointById $index "modernization-snapshot"
                $snapshotEndpointFile = [string](Get-JsonValue $snapshotEndpoint "file")
                if ([string]::IsNullOrWhiteSpace($snapshotPath)) {
                    if (-not [string]::IsNullOrWhiteSpace($snapshotEndpointFile)) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-snapshot-path-missing" "Artifact '$artifactId' does not declare snapshotPath even though the bundle captured modernization-snapshot endpoint file '$snapshotEndpointFile'."
                        $artifactValidationOk = $false
                    }
                    if (-not [string]::IsNullOrWhiteSpace($snapshotSha256)) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-snapshot-hash-without-path" "Artifact '$artifactId' declares snapshotSha256 without snapshotPath."
                        $artifactValidationOk = $false
                    }
                }
                else {
                    if (-not [string]::IsNullOrWhiteSpace($snapshotEndpointFile) -and
                        -not (Test-ComparableEvidencePathSame -BundlePath $bundlePath -ExpectedPath $snapshotEndpointFile -ActualPath $snapshotPath)) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-snapshot-endpoint-path-mismatch" "Artifact '$artifactId' snapshotPath='$snapshotPath' does not match captured modernization-snapshot endpoint file '$snapshotEndpointFile'."
                        $artifactValidationOk = $false
                    }

                    $resolvedSnapshotPath = Get-BundlePath $bundlePath $snapshotPath
                    if (-not (Test-Path -LiteralPath $resolvedSnapshotPath -PathType Leaf)) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-snapshot-file-missing" "Artifact '$artifactId' references a missing modernization snapshot: $snapshotPath"
                        $artifactValidationOk = $false
                    }
                    elseif ([string]::IsNullOrWhiteSpace($snapshotSha256)) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-snapshot-hash-missing" "Artifact '$artifactId' does not declare snapshotSha256."
                        $artifactValidationOk = $false
                    }
                    else {
                        $actualSnapshotSha256 = Get-FileSha256 $resolvedSnapshotPath
                        if (-not [string]::Equals($snapshotSha256.Trim().ToLowerInvariant(), $actualSnapshotSha256, [StringComparison]::OrdinalIgnoreCase)) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-snapshot-hash-mismatch" "Artifact '$artifactId' reports snapshotSha256='$snapshotSha256' but '$snapshotPath' hashes to '$actualSnapshotSha256'."
                            $artifactValidationOk = $false
                        }
                    }
                }

                if (-not $readyForReview) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-not-ready" "Artifact '$artifactId' reports readyForReview=false."
                    $artifactValidationOk = $false
                }
                if ($candidateSummaries.Count -ne $candidateCount) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-candidate-count-mismatch" "Artifact '$artifactId' candidateCount=$candidateCount but contains $($candidateSummaries.Count) candidate summary record(s)."
                    $artifactValidationOk = $false
                }
                $catalogCandidateIds = if ($null -eq $externalCandidates) { @() } else { @(Get-ExternalEngineCandidateIds $externalCandidates) }
                $summaryCandidateIdsForCatalog = @(Get-ExternalEngineCandidateIds $candidateSummaries)
                if ($catalogCandidateIds.Count -gt 0) {
                    if ($candidateCount -ne $catalogCandidateIds.Count) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-catalog-candidate-count-mismatch" "Artifact '$artifactId' candidateCount=$candidateCount but captured external-engine catalog contains $($catalogCandidateIds.Count) candidate(s)."
                        $artifactValidationOk = $false
                    }
                    if (($summaryCandidateIdsForCatalog -join "|") -ne ($catalogCandidateIds -join "|")) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-catalog-candidate-set-mismatch" "Artifact '$artifactId' candidateSummaries ids '$($summaryCandidateIdsForCatalog -join ', ')' do not match captured external-engine catalog ids '$($catalogCandidateIds -join ', ')'."
                        $artifactValidationOk = $false
                    }

                    $snapshotCandidateIdsForBakeoff = if ($null -eq $snapshot) { @() } else { @(Get-ExternalEngineCandidateIds (Get-JsonValue $snapshot "externalEngineCandidates")) }
                    $expectedSummariesById = @{}
                    foreach ($catalogCandidate in @(Get-ExternalEngineCandidateEntries $externalCandidates)) {
                        $expectedSummary = New-ExternalBakeoffCandidateSummary -Candidate $catalogCandidate -SnapshotCandidateIds $snapshotCandidateIdsForBakeoff
                        $expectedSummaryId = [string](Get-JsonValue $expectedSummary "id")
                        if (-not [string]::IsNullOrWhiteSpace($expectedSummaryId)) {
                            $expectedSummariesById[$expectedSummaryId] = $expectedSummary
                        }
                    }

                    foreach ($summary in @($candidateSummaries)) {
                        $summaryId = ConvertTo-CandidateId ([string](Get-JsonValue $summary "id"))
                        if ([string]::IsNullOrWhiteSpace($summaryId) -or -not $expectedSummariesById.ContainsKey($summaryId)) {
                            continue
                        }

                        $expectedSummary = $expectedSummariesById[$summaryId]
                        foreach ($fieldName in @("id", "name", "family", "defaultState", "rolloutPolicy", "integrationPoint", "license", "packagingStatus", "runtimeRisk", "latencyRisk", "radioSafetyRisk", "runtimeRiskTier", "latencyRiskTier", "radioSafetyRiskTier", "integrationStatus")) {
                            $actualValue = [string](Get-JsonValue $summary $fieldName)
                            $expectedValue = [string](Get-JsonValue $expectedSummary $fieldName)
                            if (-not [string]::Equals($actualValue, $expectedValue, [StringComparison]::Ordinal)) {
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-summary-catalog-field-mismatch" "Artifact '$artifactId' candidate '$summaryId' summary field '$fieldName'='$actualValue' but captured catalog implies '$expectedValue'."
                                $artifactValidationOk = $false
                            }
                        }

                        foreach ($fieldName in @("inSnapshot", "safeForBakeoff", "integrationBlocked")) {
                            $actualValue = Test-Truthy (Get-JsonValue $summary $fieldName)
                            $expectedValue = Test-Truthy (Get-JsonValue $expectedSummary $fieldName)
                            if ($actualValue -ne $expectedValue) {
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-summary-catalog-field-mismatch" "Artifact '$artifactId' candidate '$summaryId' summary field '$fieldName'=$actualValue but captured catalog implies $expectedValue."
                                $artifactValidationOk = $false
                            }
                        }

                        foreach ($fieldName in @("combinedRiskScore", "requiredBenchmarkCount", "requiredEvidenceCount", "blockerCount", "referenceUrlCount")) {
                            $actualValue = [int](Get-NumericValueOrDefault (Get-JsonValue $summary $fieldName))
                            $expectedValue = [int](Get-NumericValueOrDefault (Get-JsonValue $expectedSummary $fieldName))
                            if ($actualValue -ne $expectedValue) {
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-summary-catalog-field-mismatch" "Artifact '$artifactId' candidate '$summaryId' summary field '$fieldName'=$actualValue but captured catalog implies $expectedValue."
                                $artifactValidationOk = $false
                            }
                        }

                        foreach ($fieldName in @("issues", "requiredBenchmarks", "requiredEvidence", "blockers", "referenceUrls")) {
                            if (-not (Test-ComparableStringArraysEqual -Actual (Get-JsonArray $summary $fieldName) -Expected (Get-JsonArray $expectedSummary $fieldName))) {
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-summary-catalog-field-mismatch" "Artifact '$artifactId' candidate '$summaryId' summary field '$fieldName' does not match the captured catalog-derived value."
                                $artifactValidationOk = $false
                            }
                        }
                    }
                }

                $reportedRequiredCandidateIds = @(Get-JsonArray $artifactJson "requiredCandidateIds" | ForEach-Object {
                        ConvertTo-CandidateId ([string]$_)
                    } | Where-Object {
                        -not [string]::IsNullOrWhiteSpace($_)
                    } | Sort-Object -Unique)
                $expectedRequiredCandidateIds = @($requiredExternalCandidateIds | ForEach-Object {
                        ConvertTo-CandidateId ([string]$_)
                    } | Where-Object {
                        -not [string]::IsNullOrWhiteSpace($_)
                    } | Sort-Object -Unique)
                if (($reportedRequiredCandidateIds -join "|") -ne ($expectedRequiredCandidateIds -join "|")) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-required-ids-mismatch" "Artifact '$artifactId' requiredCandidateIds='$($reportedRequiredCandidateIds -join ', ')' but validator requires '$($expectedRequiredCandidateIds -join ', ')'."
                    $artifactValidationOk = $false
                }

                $expectedBakeoffCounts = Get-ExternalBakeoffSummaryCounts -Report $artifactJson -CandidateSummaries $candidateSummaries -FallbackRequiredCandidateIds $requiredExternalCandidateIds
                foreach ($countSpec in @(
                        @{ Name = "safeForBakeoffCount"; Reported = $safeForBakeoffCount; Expected = [int]$expectedBakeoffCounts.safeForBakeoffCount; Code = "external-bakeoff-safe-count-mismatch" },
                        @{ Name = "blockedCandidateCount"; Reported = $blockedCandidateCount; Expected = [int]$expectedBakeoffCounts.blockedCandidateCount; Code = "external-bakeoff-blocked-count-mismatch" },
                        @{ Name = "integrationReadyCandidateCount"; Reported = $integrationReadyCandidateCount; Expected = [int]$expectedBakeoffCounts.integrationReadyCandidateCount; Code = "external-bakeoff-integration-ready-count-mismatch" },
                        @{ Name = "missingCandidateCount"; Reported = $missingCandidateCount; Expected = [int]$expectedBakeoffCounts.missingCandidateCount; Code = "external-bakeoff-missing-count-mismatch" },
                        @{ Name = "unsafeCandidateCount"; Reported = $unsafeCandidateCount; Expected = [int]$expectedBakeoffCounts.unsafeCandidateCount; Code = "external-bakeoff-unsafe-count-mismatch" },
                        @{ Name = "snapshotMismatchCount"; Reported = $snapshotMismatchCount; Expected = [int]$expectedBakeoffCounts.snapshotMismatchCount; Code = "external-bakeoff-snapshot-mismatch-count-mismatch" }
                    )) {
                    if ([int]$countSpec.Reported -ne [int]$countSpec.Expected) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired ([string]$countSpec.Code) "Artifact '$artifactId' $($countSpec.Name)=$($countSpec.Reported) but candidateSummaries imply $($countSpec.Expected)."
                        $artifactValidationOk = $false
                    }
                }

                $reportedMissingCandidateIds = @(Get-JsonArray $artifactJson "missingCandidateIds" | ForEach-Object {
                        ConvertTo-CandidateId ([string]$_)
                    } | Where-Object {
                        -not [string]::IsNullOrWhiteSpace($_)
                    } | Sort-Object -Unique)
                $expectedMissingCandidateIds = @($expectedBakeoffCounts.missingCandidateIds)
                if (($reportedMissingCandidateIds -join "|") -ne ($expectedMissingCandidateIds -join "|")) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-missing-ids-mismatch" "Artifact '$artifactId' missingCandidateIds='$($reportedMissingCandidateIds -join ', ')' but candidateSummaries imply '$($expectedMissingCandidateIds -join ', ')'."
                    $artifactValidationOk = $false
                }

                if ($readyForReview -ne (Test-Truthy $expectedBakeoffCounts.readyForReview)) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-ready-mismatch" "Artifact '$artifactId' readyForReview=$readyForReview but candidateSummaries imply $($expectedBakeoffCounts.readyForReview)."
                    $artifactValidationOk = $false
                }
                if ($candidateCount -le 0) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-candidates-missing" "Artifact '$artifactId' does not summarize any external-engine candidates."
                    $artifactValidationOk = $false
                }
                if ($missingCandidateCount -gt 0) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-required-candidate-missing" "Artifact '$artifactId' is missing $missingCandidateCount required external-engine candidate(s)."
                    $artifactValidationOk = $false
                }
                if ($unsafeCandidateCount -gt 0) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-unsafe-candidate" "Artifact '$artifactId' reports $unsafeCandidateCount unsafe external-engine candidate catalog entry/entries."
                    $artifactValidationOk = $false
                }
                if ($snapshotMismatchCount -gt 0) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-snapshot-mismatch" "Artifact '$artifactId' reports $snapshotMismatchCount modernization-snapshot mismatch(es)."
                    $artifactValidationOk = $false
                }
                if ($integrationReadyCandidateCount -gt 0) {
                    Add-ValidationIssue $warnings "warning" "external-bakeoff-integration-approval-required" "Artifact '$artifactId' reports $integrationReadyCandidateCount external candidate(s) without blockers; explicit approval is still required before integration or default behavior changes."
                }
                if ($externalBakeoffPlanRequired -and $null -eq $externalBakeoffPlan) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-plan-missing" "Artifact '$artifactId' schemaVersion=$schemaVersion must include externalBakeoffPlan."
                    $artifactValidationOk = $false
                }
                elseif ($externalBakeoffPlanRequired) {
                    $planScope = [string](Get-JsonValue $externalBakeoffPlan "planScope")
                    $candidatePlans = @(Get-JsonArray $externalBakeoffPlan "candidatePlans")
                    $planCandidateCount = [int](Get-NumericValueOrDefault (Get-JsonValue $externalBakeoffPlan "candidatePlanCount"))
                    $planScenarioCount = [int](Get-NumericValueOrDefault (Get-JsonValue $externalBakeoffPlan "scenarioCount"))
                    $planComparisons = @(Get-JsonArray $externalBakeoffPlan "requiredComparisons" | ForEach-Object { [string]$_ })
                    $expectedRequiredComparisons = @(Get-ExternalBakeoffRequiredComparisons)
                    $planScenarioIds = New-Object System.Collections.Generic.List[string]
                    $planCandidateIds = New-Object System.Collections.Generic.List[string]
                    $summaryCandidateIds = New-Object System.Collections.Generic.List[string]
                    $duplicatePlanCandidateIds = New-Object System.Collections.Generic.List[string]
                    $duplicateSummaryCandidateIds = New-Object System.Collections.Generic.List[string]
                    $seenPlanCandidateIds = @{}
                    $seenSummaryCandidateIds = @{}

                    foreach ($summary in @($candidateSummaries)) {
                        $summaryId = ConvertTo-CandidateId ([string](Get-JsonValue $summary "id"))
                        if ([string]::IsNullOrWhiteSpace($summaryId)) {
                            continue
                        }

                        $summaryCandidateIds.Add($summaryId) | Out-Null
                        if ($seenSummaryCandidateIds.ContainsKey($summaryId)) {
                            $duplicateSummaryCandidateIds.Add($summaryId) | Out-Null
                        }
                        else {
                            $seenSummaryCandidateIds[$summaryId] = $true
                        }
                    }

                    foreach ($candidatePlan in @($candidatePlans)) {
                        $planCandidateId = ConvertTo-CandidateId ([string](Get-JsonValue $candidatePlan "candidateId"))
                        if (-not [string]::IsNullOrWhiteSpace($planCandidateId)) {
                            $planCandidateIds.Add($planCandidateId) | Out-Null
                            if ($seenPlanCandidateIds.ContainsKey($planCandidateId)) {
                                $duplicatePlanCandidateIds.Add($planCandidateId) | Out-Null
                            }
                            else {
                                $seenPlanCandidateIds[$planCandidateId] = $true
                            }
                        }

                        foreach ($scenario in @(Get-JsonArray $candidatePlan "scenarios")) {
                            $scenarioId = [string](Get-JsonValue $scenario "scenarioId")
                            if (-not [string]::IsNullOrWhiteSpace($scenarioId)) {
                                $planScenarioIds.Add($scenarioId.Trim().ToLowerInvariant()) | Out-Null
                            }
                        }
                    }

                    $reportedPlanScenarioIds = @(Get-JsonArray $externalBakeoffPlan "scenarioIds" | ForEach-Object {
                            ([string]$_).Trim().ToLowerInvariant()
                        } | Where-Object {
                            -not [string]::IsNullOrWhiteSpace($_)
                        } | Sort-Object -Unique)
                    $uniquePlanCandidateIds = @($planCandidateIds.ToArray() | Sort-Object -Unique)
                    $uniqueSummaryCandidateIds = @($summaryCandidateIds.ToArray() | Sort-Object -Unique)
                    $expectedPlanScenarioIds = @($uniqueSummaryCandidateIds | ForEach-Object {
                            foreach ($expectedScenario in @(Get-ExternalBakeoffExpectedScenariosForCandidateId $_)) {
                                $expectedScenarioId = [string](Get-JsonValue $expectedScenario "scenarioId")
                                if (-not [string]::IsNullOrWhiteSpace($expectedScenarioId)) {
                                    $expectedScenarioId.Trim().ToLowerInvariant()
                                }
                            }
                        } | Sort-Object -Unique)

                    if ($planScope -ne "opt-in-post-demod-bakeoff-only") {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-plan-scope-invalid" "Artifact '$artifactId' externalBakeoffPlan.planScope='$planScope' is not supported."
                        $artifactValidationOk = $false
                    }
                    if (Test-Truthy (Get-JsonValue $externalBakeoffPlan "defaultBehaviorChangeReady")) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-plan-default-change-ready" "Artifact '$artifactId' externalBakeoffPlan must not authorize default behavior changes."
                        $artifactValidationOk = $false
                    }
                    if (Test-Truthy (Get-JsonValue $externalBakeoffPlan "rawWdspIqReplacementAllowed")) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-plan-raw-iq-replacement" "Artifact '$artifactId' externalBakeoffPlan must not allow raw WDSP IQ replacement."
                        $artifactValidationOk = $false
                    }
                    if (Test-Truthy (Get-JsonValue $externalBakeoffPlan "txPathAllowed")) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-plan-tx-path-allowed" "Artifact '$artifactId' externalBakeoffPlan must not allow TX/PureSignal coupling."
                        $artifactValidationOk = $false
                    }
                    foreach ($comparisonId in @($expectedRequiredComparisons)) {
                        if ($planComparisons -notcontains $comparisonId) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-plan-comparison-missing" "Artifact '$artifactId' externalBakeoffPlan.requiredComparisons is missing '$comparisonId'."
                            $artifactValidationOk = $false
                        }
                    }
                    if (-not (Test-OrderedStringArraysEqual -Actual $planComparisons -Expected $expectedRequiredComparisons)) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-plan-comparisons-mismatch" "Artifact '$artifactId' externalBakeoffPlan.requiredComparisons does not match the generated bakeoff plan."
                        $artifactValidationOk = $false
                    }
                    if ($planCandidateCount -ne $candidatePlans.Count -or $planCandidateCount -ne $candidateCount) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-plan-candidate-count-mismatch" "Artifact '$artifactId' externalBakeoffPlan candidate count does not match candidate summaries."
                        $artifactValidationOk = $false
                    }
                    if (($duplicateSummaryCandidateIds.ToArray() | Select-Object -Unique).Count -gt 0) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-summary-candidate-duplicate" "Artifact '$artifactId' candidateSummaries contains duplicate candidate id(s): $((@($duplicateSummaryCandidateIds.ToArray() | Select-Object -Unique | Sort-Object)) -join ', ')."
                        $artifactValidationOk = $false
                    }
                    if (($duplicatePlanCandidateIds.ToArray() | Select-Object -Unique).Count -gt 0) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-plan-candidate-duplicate" "Artifact '$artifactId' externalBakeoffPlan contains duplicate candidate id(s): $((@($duplicatePlanCandidateIds.ToArray() | Select-Object -Unique | Sort-Object)) -join ', ')."
                        $artifactValidationOk = $false
                    }
                    if (($uniquePlanCandidateIds -join "|") -ne ($uniqueSummaryCandidateIds -join "|")) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-plan-candidate-set-mismatch" "Artifact '$artifactId' externalBakeoffPlan candidate ids '$($uniquePlanCandidateIds -join ', ')' do not match candidateSummaries '$($uniqueSummaryCandidateIds -join ', ')'."
                        $artifactValidationOk = $false
                    }
                    if ($planScenarioCount -le 0) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-plan-scenarios-missing" "Artifact '$artifactId' externalBakeoffPlan has no scenario coverage."
                        $artifactValidationOk = $false
                    }
                    elseif ($planScenarioCount -ne $expectedPlanScenarioIds.Count) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-plan-scenario-count-mismatch" "Artifact '$artifactId' externalBakeoffPlan scenarioCount=$planScenarioCount but candidateSummaries imply $($expectedPlanScenarioIds.Count)."
                        $artifactValidationOk = $false
                    }
                    if (($reportedPlanScenarioIds -join "|") -ne ($expectedPlanScenarioIds -join "|")) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-plan-scenario-ids-mismatch" "Artifact '$artifactId' externalBakeoffPlan scenarioIds='$($reportedPlanScenarioIds -join ', ')' but candidateSummaries imply '$($expectedPlanScenarioIds -join ', ')'."
                        $artifactValidationOk = $false
                    }
                    foreach ($templateName in @("fixtureComparisonCommandTemplate", "liveMatrixCommandTemplate")) {
                        if ([string]::IsNullOrWhiteSpace([string](Get-JsonValue $externalBakeoffPlan $templateName))) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-plan-command-missing" "Artifact '$artifactId' externalBakeoffPlan.$templateName is missing."
                            $artifactValidationOk = $false
                        }
                    }

                    $summaryById = @{}
                    foreach ($summary in @($candidateSummaries)) {
                        $summaryId = [string](Get-JsonValue $summary "id")
                        if (-not [string]::IsNullOrWhiteSpace($summaryId)) {
                            $summaryById[$summaryId] = $summary
                        }
                    }
                    foreach ($candidatePlan in @($candidatePlans)) {
                        $candidateId = [string](Get-JsonValue $candidatePlan "candidateId")
                        if ([string]::IsNullOrWhiteSpace($candidateId) -or -not $summaryById.ContainsKey($candidateId)) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-plan-candidate-missing" "Artifact '$artifactId' externalBakeoffPlan references candidate '$candidateId' without a matching summary."
                            $artifactValidationOk = $false
                            continue
                        }
                        $summary = $summaryById[$candidateId]
                        if ((Test-Truthy (Get-JsonValue $candidatePlan "readyForBakeoff")) -ne (Test-Truthy (Get-JsonValue $summary "safeForBakeoff"))) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-plan-ready-mismatch" "Artifact '$artifactId' externalBakeoffPlan candidate '$candidateId' readyForBakeoff does not match summary.safeForBakeoff."
                            $artifactValidationOk = $false
                        }
                        if ((Test-Truthy (Get-JsonValue $candidatePlan "blockedForIntegration")) -ne (Test-Truthy (Get-JsonValue $summary "integrationBlocked"))) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-plan-blocker-mismatch" "Artifact '$artifactId' externalBakeoffPlan candidate '$candidateId' blockedForIntegration does not match summary.integrationBlocked."
                            $artifactValidationOk = $false
                        }
                        foreach ($fieldName in @("integrationPoint", "defaultState")) {
                            $actualPlanValue = [string](Get-JsonValue $candidatePlan $fieldName)
                            $expectedSummaryValue = [string](Get-JsonValue $summary $fieldName)
                            if (-not [string]::Equals($actualPlanValue, $expectedSummaryValue, [StringComparison]::Ordinal)) {
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-plan-candidate-field-mismatch" "Artifact '$artifactId' externalBakeoffPlan candidate '$candidateId' field '$fieldName'='$actualPlanValue' but summary implies '$expectedSummaryValue'."
                                $artifactValidationOk = $false
                            }
                        }
                        $scenarios = @(Get-JsonArray $candidatePlan "scenarios")
                        $scenarioCount = [int](Get-NumericValueOrDefault (Get-JsonValue $candidatePlan "scenarioCount"))
                        $expectedScenarios = @(Get-ExternalBakeoffExpectedScenariosForCandidateId $candidateId)
                        if ($scenarioCount -ne $scenarios.Count -or $scenarioCount -le 0) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-plan-candidate-scenario-count-mismatch" "Artifact '$artifactId' externalBakeoffPlan candidate '$candidateId' scenario count is invalid."
                            $artifactValidationOk = $false
                        }
                        if ($scenarioCount -ne $expectedScenarios.Count) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-plan-candidate-expected-scenario-count-mismatch" "Artifact '$artifactId' externalBakeoffPlan candidate '$candidateId' scenarioCount=$scenarioCount but the generated bakeoff plan requires $($expectedScenarios.Count)."
                            $artifactValidationOk = $false
                        }
                        $expectedRequiredControls = @(Get-ExternalBakeoffRequiredControls)
                        $requiredControls = @(Get-JsonArray $candidatePlan "requiredControls" | ForEach-Object { [string]$_ })
                        foreach ($control in @($expectedRequiredControls)) {
                            if ($requiredControls -notcontains $control) {
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-plan-control-missing" "Artifact '$artifactId' externalBakeoffPlan candidate '$candidateId' is missing required control '$control'."
                                $artifactValidationOk = $false
                            }
                        }
                        if (-not (Test-OrderedStringArraysEqual -Actual $requiredControls -Expected $expectedRequiredControls)) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-plan-controls-mismatch" "Artifact '$artifactId' externalBakeoffPlan candidate '$candidateId' requiredControls does not match the generated bakeoff plan."
                            $artifactValidationOk = $false
                        }
                        $packageAndRuntimeGates = @(Get-JsonArray $candidatePlan "packageAndRuntimeGates" | ForEach-Object { [string]$_ })
                        $expectedPackageAndRuntimeGates = @(Get-ExternalBakeoffPackageAndRuntimeGates)
                        if (-not (Test-OrderedStringArraysEqual -Actual $packageAndRuntimeGates -Expected $expectedPackageAndRuntimeGates)) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-plan-package-gates-mismatch" "Artifact '$artifactId' externalBakeoffPlan candidate '$candidateId' packageAndRuntimeGates does not match the generated bakeoff plan."
                            $artifactValidationOk = $false
                        }
                        $radioSafetyGates = @(Get-JsonArray $candidatePlan "radioSafetyGates" | ForEach-Object { [string]$_ })
                        $expectedRadioSafetyGates = @(Get-ExternalBakeoffRadioSafetyGates)
                        if (-not (Test-OrderedStringArraysEqual -Actual $radioSafetyGates -Expected $expectedRadioSafetyGates)) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-plan-radio-gates-mismatch" "Artifact '$artifactId' externalBakeoffPlan candidate '$candidateId' radioSafetyGates does not match the generated bakeoff plan."
                            $artifactValidationOk = $false
                        }
                        $expectedPriority = 1
                        foreach ($scenario in @($scenarios)) {
                            $priority = [int](Get-NumericValueOrDefault (Get-JsonValue $scenario "priority") -1)
                            $scenarioId = [string](Get-JsonValue $scenario "scenarioId")
                            $purpose = [string](Get-JsonValue $scenario "purpose")
                            $scenarioComparisons = @(Get-JsonArray $scenario "requiredComparisons" | ForEach-Object { [string]$_ })
                            $gates = @(Get-JsonArray $scenario "acceptanceGates")
                            $expectedScenario = $null
                            $expectedScenarioIndex = $expectedPriority - 1
                            if ($expectedScenarioIndex -ge 0 -and $expectedScenarioIndex -lt $expectedScenarios.Count) {
                                $expectedScenario = $expectedScenarios[$expectedScenarioIndex]
                            }
                            if ($priority -ne $expectedPriority) {
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-plan-priority-invalid" "Artifact '$artifactId' externalBakeoffPlan candidate '$candidateId' scenario priority=$priority but expected $expectedPriority."
                                $artifactValidationOk = $false
                            }
                            if ([string]::IsNullOrWhiteSpace($scenarioId) -or [string]::IsNullOrWhiteSpace($purpose)) {
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-plan-scenario-invalid" "Artifact '$artifactId' externalBakeoffPlan candidate '$candidateId' scenario is missing scenarioId or purpose."
                                $artifactValidationOk = $false
                            }
                            foreach ($comparisonId in @("current-zeus", "nr5-spnr", "candidate-external-engine-opt-in")) {
                                if ($scenarioComparisons -notcontains $comparisonId) {
                                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-plan-scenario-comparison-missing" "Artifact '$artifactId' externalBakeoffPlan candidate '$candidateId' scenario '$scenarioId' is missing comparison '$comparisonId'."
                                    $artifactValidationOk = $false
                                }
                            }
                            if (-not (Test-OrderedStringArraysEqual -Actual $scenarioComparisons -Expected $expectedRequiredComparisons)) {
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-plan-scenario-comparisons-mismatch" "Artifact '$artifactId' externalBakeoffPlan candidate '$candidateId' scenario '$scenarioId' requiredComparisons does not match the generated bakeoff plan."
                                $artifactValidationOk = $false
                            }
                            if ($gates.Count -le 0) {
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-plan-gates-missing" "Artifact '$artifactId' externalBakeoffPlan candidate '$candidateId' scenario '$scenarioId' has no acceptance gates."
                                $artifactValidationOk = $false
                            }
                            if ($null -ne $expectedScenario) {
                                $expectedScenarioId = [string](Get-JsonValue $expectedScenario "scenarioId")
                                $expectedPurpose = [string](Get-JsonValue $expectedScenario "purpose")
                                $expectedGates = @(Get-JsonArray $expectedScenario "acceptanceGates")
                                if (-not [string]::Equals($scenarioId, $expectedScenarioId, [StringComparison]::Ordinal)) {
                                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-plan-scenario-id-mismatch" "Artifact '$artifactId' externalBakeoffPlan candidate '$candidateId' scenario priority=$expectedPriority has scenarioId='$scenarioId' but the generated bakeoff plan requires '$expectedScenarioId'."
                                    $artifactValidationOk = $false
                                }
                                if (-not [string]::Equals($purpose, $expectedPurpose, [StringComparison]::Ordinal)) {
                                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-plan-scenario-purpose-mismatch" "Artifact '$artifactId' externalBakeoffPlan candidate '$candidateId' scenario '$scenarioId' purpose does not match the generated bakeoff plan."
                                    $artifactValidationOk = $false
                                }
                                if (-not (Test-OrderedStringArraysEqual -Actual $gates -Expected $expectedGates)) {
                                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "external-bakeoff-plan-scenario-gates-mismatch" "Artifact '$artifactId' externalBakeoffPlan candidate '$candidateId' scenario '$scenarioId' acceptanceGates does not match the generated bakeoff plan."
                                    $artifactValidationOk = $false
                                }
                            }
                            $expectedPriority++
                        }
                    }
                }

                if ($artifactValidationOk) {
                    $externalEngineBakeoffEvidence["status"] = "ready"
                }
                else {
                    $externalEngineBakeoffEvidence["status"] = "not-ready"
                }
            }

            if ($artifactKind -ieq "native-audit-json" -or $artifactId -eq $nativeSymbolAuditArtifactId) {
                $nativeSymbolAuditEvidence["present"] = $true

                $tool = [string](Get-JsonValue $artifactJson "tool")
                if ($tool -ne "audit-wdsp-native-symbols") {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "native-symbol-audit-tool-invalid" "Artifact '$artifactId' must be generated by audit-wdsp-native-symbols.ps1."
                    $artifactValidationOk = $false
                }

                $readyForReview = Test-Truthy (Get-JsonValue $artifactJson "readyForReview")
                $importedSymbols = [int](Get-JsonValue $artifactJson "importedSymbolCount")
                $sourceMissingRequired = [int](Get-JsonValue $artifactJson "sourceMissingRequiredCount")
                $signatureMismatches = [int](Get-JsonValue $artifactJson "signatureMismatchCount")
                $binaryExportsEvaluated = Test-Truthy (Get-JsonValue $artifactJson "binaryExportsEvaluated")
                $binaryExportCount = [int](Get-JsonValue $artifactJson "binaryExportCount")
                $binaryMissingRequired = [int](Get-JsonValue $artifactJson "binaryMissingRequiredCount")
                $binaryExportStatus = [string](Get-JsonValue $artifactJson "binaryExportStatus")

                $nativeSymbolAuditEvidence["readyForReview"] = $readyForReview
                $nativeSymbolAuditEvidence["importedSymbolCount"] = $importedSymbols
                $nativeSymbolAuditEvidence["sourceMissingRequiredCount"] = $sourceMissingRequired
                $nativeSymbolAuditEvidence["signatureMismatchCount"] = $signatureMismatches
                $nativeSymbolAuditEvidence["binaryExportsEvaluated"] = $binaryExportsEvaluated
                $nativeSymbolAuditEvidence["binaryExportCount"] = $binaryExportCount
                $nativeSymbolAuditEvidence["binaryMissingRequiredCount"] = $binaryMissingRequired
                $nativeSymbolAuditEvidence["binaryExportStatus"] = $binaryExportStatus

                if (-not $readyForReview) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "native-symbol-audit-not-ready" "Artifact '$artifactId' reports readyForReview=false."
                    $artifactValidationOk = $false
                }
                if ($importedSymbols -le 0) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "native-symbol-audit-empty" "Artifact '$artifactId' did not audit any NativeMethods symbols."
                    $artifactValidationOk = $false
                }
                if ($sourceMissingRequired -gt 0) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "native-symbol-source-missing" "Artifact '$artifactId' reports $sourceMissingRequired required NativeMethods symbol(s) missing from vendored WDSP source."
                    $artifactValidationOk = $false
                }
                if ($signatureMismatches -gt 0) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "native-symbol-signature-mismatch" "Artifact '$artifactId' reports $signatureMismatches managed/native signature mismatch(es)."
                    $artifactValidationOk = $false
                }
                if (-not $binaryExportsEvaluated) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "native-symbol-binary-not-evaluated" "Artifact '$artifactId' did not evaluate native binary exports; rerun audit-wdsp-native-symbols.ps1 with -RequireBinaryExports."
                    $artifactValidationOk = $false
                }
                if ($binaryExportsEvaluated -and $binaryExportCount -le 0) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "native-symbol-binary-export-empty" "Artifact '$artifactId' evaluated binary exports but found no exported symbols."
                    $artifactValidationOk = $false
                }
                if ($binaryMissingRequired -gt 0) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "native-symbol-binary-missing" "Artifact '$artifactId' reports $binaryMissingRequired required NativeMethods symbol(s) missing from the native binary export table."
                    $artifactValidationOk = $false
                }

                if ($artifactValidationOk) {
                    $nativeSymbolAuditEvidence["status"] = "ready"
                }
                else {
                    $nativeSymbolAuditEvidence["status"] = "not-ready"
                }
            }

            if ($artifactKind -ieq "runtime-audit-json" -or $artifactId -eq $nativeRuntimeArtifactAuditId) {
                $nativeRuntimeArtifactAuditEvidence["present"] = $true

                $tool = [string](Get-JsonValue $artifactJson "tool")
                if ($tool -ne "audit-wdsp-runtime-artifacts") {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "native-runtime-audit-tool-invalid" "Artifact '$artifactId' must be generated by audit-wdsp-runtime-artifacts.ps1."
                    $artifactValidationOk = $false
                }

                $readyForWinX64Package = Test-Truthy (Get-JsonValue $artifactJson "readyForWinX64Package")
                $pendingRids = @(Get-JsonArray $artifactJson "pendingRids" | ForEach-Object { [string]$_ })
                $pendingRidCount = [int](Get-JsonValue $artifactJson "pendingRidCount")
                $runtimeArtifacts = @(Get-JsonArray $artifactJson "artifacts")
                $winX64NativePath = [string](Get-JsonValue $artifactJson "winX64NativePath")
                $winX64NativeLength = [int64](Get-NumericValueOrDefault (Get-JsonValue $artifactJson "winX64NativeLength"))
                $winX64NativeSha256 = ([string](Get-JsonValue $artifactJson "winX64NativeSha256")).Trim().ToLowerInvariant()
                foreach ($runtimeArtifact in $runtimeArtifacts) {
                    if (-not [string]::Equals([string](Get-JsonValue $runtimeArtifact "rid"), "win-x64", [StringComparison]::OrdinalIgnoreCase)) {
                        continue
                    }
                    if ([string]::IsNullOrWhiteSpace($winX64NativePath)) {
                        $winX64NativePath = [string](Get-JsonValue $runtimeArtifact "nativePath")
                    }
                    if ($winX64NativeLength -eq 0) {
                        $winX64NativeLength = [int64](Get-NumericValueOrDefault (Get-JsonValue $runtimeArtifact "nativeLength"))
                    }
                    if ([string]::IsNullOrWhiteSpace($winX64NativeSha256)) {
                        $winX64NativeSha256 = ([string](Get-JsonValue $runtimeArtifact "nativeSha256")).Trim().ToLowerInvariant()
                    }
                    break
                }

                $nativeRuntimeArtifactAuditEvidence["readyForWinX64Package"] = $readyForWinX64Package
                $nativeRuntimeArtifactAuditEvidence["pendingRidCount"] = $pendingRidCount
                $nativeRuntimeArtifactAuditEvidence["pendingRids"] = @($pendingRids)
                $nativeRuntimeArtifactAuditEvidence["artifactCount"] = $runtimeArtifacts.Count
                $nativeRuntimeArtifactAuditEvidence["winX64NativePath"] = $winX64NativePath
                $nativeRuntimeArtifactAuditEvidence["winX64NativeLength"] = $winX64NativeLength
                $nativeRuntimeArtifactAuditEvidence["winX64NativeSha256"] = $winX64NativeSha256

                if (-not $readyForWinX64Package) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "native-runtime-win-x64-not-ready" "Artifact '$artifactId' reports readyForWinX64Package=false."
                    $artifactValidationOk = $false
                }
                if ($readyForWinX64Package -and [string]::IsNullOrWhiteSpace($winX64NativeSha256)) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "native-runtime-win-x64-hash-missing" "Artifact '$artifactId' reports Win x64 runtime readiness but does not declare winX64NativeSha256."
                    $artifactValidationOk = $false
                }
                if ($runtimeArtifacts.Count -le 0) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "native-runtime-audit-empty" "Artifact '$artifactId' did not audit any packaged runtime artifacts."
                    $artifactValidationOk = $false
                }
                if ($pendingRidCount -ne $pendingRids.Count) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "native-runtime-pending-rid-count-mismatch" "Artifact '$artifactId' reports pendingRidCount=$pendingRidCount but lists $($pendingRids.Count) pending RID(s)."
                    $artifactValidationOk = $false
                }

                if ($artifactValidationOk) {
                    if ($pendingRidCount -gt 0) {
                        $nativeRuntimeArtifactAuditEvidence["status"] = "win-x64-ready-pending-rids"
                    }
                    else {
                        $nativeRuntimeArtifactAuditEvidence["status"] = "ready"
                    }
                }
                else {
                    $nativeRuntimeArtifactAuditEvidence["status"] = "not-ready"
                }
            }

            if (Test-ArtifactIndex $artifactKind $artifactPath) {
                $indexedFiles = Get-JsonArray $artifactJson "files"
                if ($indexedFiles.Count -eq 0) {
                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "artifact-index-files-missing" "Artifact '$artifactId' index does not list any files."
                    $artifactValidationOk = $false
                    continue
                }

                $coveredScenarioIds = @{}
                $coveredComparisonsByScenario = @{}
                foreach ($indexedFile in $indexedFiles) {
                    $indexedPath = Get-ArtifactIndexFilePath $indexedFile
                    $indexedSummaryPath = Get-ArtifactIndexFileSummaryPath $indexedFile
                    $indexedSha256 = Get-ArtifactIndexFileSha256 $indexedFile
                    $indexedSummarySha256 = Get-ArtifactIndexFileSummarySha256 $indexedFile
                    $indexedScenarioIds = @(Get-ArtifactIndexFileScenarioIds $indexedFile)
                    $indexedScenarioIdsNormalized = @($indexedScenarioIds | ForEach-Object { ConvertTo-LiveHistoryScenarioId ([string]$_) } | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | Select-Object -Unique)
                    $indexedExplicitComparisonIds = @(Get-ArtifactIndexFileExplicitComparisonIds $indexedFile)
                    $indexedComparisonIds = @(Get-ArtifactIndexFileComparisonIds $indexedFile)
                    $indexedExplicitComparisonIdsNormalized = @($indexedExplicitComparisonIds | ForEach-Object { ConvertTo-ComparisonId ([string]$_) } | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | Select-Object -Unique)
                    $indexedRecord = [ordered]@{
                        artifactId = $artifactId
                        artifactKind = $artifactKind
                        path = $indexedPath
                        summaryPath = $indexedSummaryPath
                        scenarioIds = @($indexedScenarioIds)
                        comparisonIds = @($indexedComparisonIds)
                        scenarioIdsNormalized = @($indexedScenarioIdsNormalized)
                        explicitComparisonIds = @($indexedExplicitComparisonIdsNormalized)
                        required = $effectiveRequired
                        ok = $false
                        bytes = 0
                        sha256 = $indexedSha256
                        actualSha256 = $null
                        hashStatus = $null
                        summarySha256 = $indexedSummarySha256
                        actualSummarySha256 = $null
                        summaryHashStatus = $null
                        jsonlLineCount = $null
                        indexSampleCount = $null
                        summarySampleCount = $null
                        summaryOk = $null
                        summaryMetadataStatus = $null
                        summaryScenarioId = $null
                        summaryComparisonId = $null
                        summaryScenarioMatch = $null
                        summaryComparisonMatch = $null
                        summaryJsonlPath = $null
                        summaryTracePaths = @()
                        summaryTracePathMatch = $null
                        summaryTracePathStatus = $null
                        summaryTrendStatus = $null
                        readyForBenchmarkTrace = $null
                        nr5SampleCount = $null
                        nr5AgcDiagnosticSampleCount = $null
                        indexNr5WeakInputSampleCount = $null
                        indexNr5WeakRecoveredSampleCount = $null
                        indexNr5WeakDropoutSampleCount = $null
                        indexNr5HotMakeupSampleCount = $null
                        nr5WeakInputSampleCount = $null
                        nr5WeakRecoveredSampleCount = $null
                        nr5WeakDropoutSampleCount = $null
                        nr5HotMakeupSampleCount = $null
                        nr5WeakCounterStatus = $null
                        nr5WeakCounterMismatchCount = 0
                    }
                    $artifactReferencedFiles.Add($indexedRecord) | Out-Null

                    if ($effectiveRequired) {
                        $requiredArtifactReferencedFileCount++
                    }

                    foreach ($indexedScenarioId in $indexedScenarioIds) {
                        $coveredScenarioIds[$indexedScenarioId] = $true
                        if (-not $coveredComparisonsByScenario.ContainsKey($indexedScenarioId)) {
                            $coveredComparisonsByScenario[$indexedScenarioId] = @{}
                        }

                        foreach ($indexedComparisonId in $indexedComparisonIds) {
                            $coveredComparisonsByScenario[$indexedScenarioId][$indexedComparisonId] = $true
                        }
                    }

                    if ([string]::IsNullOrWhiteSpace($indexedPath)) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "artifact-index-file-path-missing" "Artifact '$artifactId' index contains a file entry with no path."
                        $artifactValidationOk = $false
                        continue
                    }

                    if ([System.IO.Path]::IsPathRooted($indexedPath)) {
                        Add-ValidationIssue $warnings "warning" "artifact-index-file-path-absolute" "Artifact '$artifactId' index uses an absolute file path; relative paths keep capture bundles portable."
                    }

                    $resolvedIndexedPath = Get-BundlePath $bundlePath $indexedPath
                    if (-not (Test-Path -LiteralPath $resolvedIndexedPath -PathType Leaf)) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "artifact-index-file-missing" "Artifact '$artifactId' index references a missing file: $indexedPath"
                        $artifactValidationOk = $false
                        continue
                    }

                    $indexedItem = Get-Item -LiteralPath $resolvedIndexedPath
                    $indexedRecord["bytes"] = $indexedItem.Length
                    if ($indexedItem.Length -le 0) {
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "artifact-index-file-empty" "Artifact '$artifactId' index references an empty file: $indexedPath"
                        $artifactValidationOk = $false
                        continue
                    }

                    $indexedRecord["ok"] = $true
                    if ($artifactId -eq "live-diagnostics-trace-index") {
                        $actualIndexedSha256 = Get-FileSha256 $resolvedIndexedPath
                        $indexedRecord["actualSha256"] = $actualIndexedSha256
                        if ([string]::IsNullOrWhiteSpace($indexedSha256)) {
                            $indexedRecord["hashStatus"] = "legacy-missing-index"
                        }
                        elseif ([string]::Equals($indexedSha256, $actualIndexedSha256, [StringComparison]::OrdinalIgnoreCase)) {
                            $indexedRecord["hashStatus"] = "matched"
                        }
                        else {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-index-hash-mismatch" "Artifact '$artifactId' index entry '$indexedPath' declares sha256='$indexedSha256' but the file hash is '$actualIndexedSha256'."
                            $artifactValidationOk = $false
                            $indexedRecord["ok"] = $false
                            $indexedRecord["hashStatus"] = "mismatch"
                        }

                        $indexedJsonlLineCount = $null
                        try {
                            $indexedJsonlLineCount = Test-JsonlFile $resolvedIndexedPath
                            $indexedRecord["jsonlLineCount"] = $indexedJsonlLineCount
                            if ($indexedJsonlLineCount -le 0) {
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-index-jsonl-empty" "Artifact '$artifactId' index entry '$indexedPath' did not contain any JSONL records."
                                $artifactValidationOk = $false
                                $indexedRecord["ok"] = $false
                            }
                        }
                        catch {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-index-jsonl-invalid" $_.Exception.Message
                            $artifactValidationOk = $false
                            $indexedRecord["ok"] = $false
                        }

                        $indexedSampleCount = Get-NumericValue (Get-JsonValue $indexedFile "sampleCount")
                        if ($null -ne $indexedSampleCount) {
                            $indexedRecord["indexSampleCount"] = [int]$indexedSampleCount
                            if ($null -ne $indexedJsonlLineCount -and [int]$indexedSampleCount -ne $indexedJsonlLineCount) {
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-index-file-sample-count-mismatch" "Artifact '$artifactId' index entry '$indexedPath' declares sampleCount=$([int]$indexedSampleCount) but the JSONL file contains $indexedJsonlLineCount record(s)."
                                $artifactValidationOk = $false
                                $indexedRecord["ok"] = $false
                            }
                        }

                        if ([string]::IsNullOrWhiteSpace($indexedSummaryPath)) {
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-index-summary-missing" "Artifact '$artifactId' index entry '$indexedPath' must include summaryPath for watch-dsp-live-diagnostics evidence validation."
                            $artifactValidationOk = $false
                            $indexedRecord["ok"] = $false
                        }
                        else {
                            if ([System.IO.Path]::IsPathRooted($indexedSummaryPath)) {
                                Add-ValidationIssue $warnings "warning" "live-trace-index-summary-path-absolute" "Artifact '$artifactId' index uses an absolute summary path; relative paths keep capture bundles portable."
                            }

                            $resolvedSummaryPath = Get-BundlePath $bundlePath $indexedSummaryPath
                            if (-not (Test-Path -LiteralPath $resolvedSummaryPath -PathType Leaf)) {
                                Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-index-summary-file-missing" "Artifact '$artifactId' index references a missing watch summary: $indexedSummaryPath"
                                $artifactValidationOk = $false
                                $indexedRecord["ok"] = $false
                            }
                            else {
                                $actualSummarySha256 = Get-FileSha256 $resolvedSummaryPath
                                $indexedRecord["actualSummarySha256"] = $actualSummarySha256
                                if ([string]::IsNullOrWhiteSpace($indexedSummarySha256)) {
                                    $indexedRecord["summaryHashStatus"] = "legacy-missing-index"
                                }
                                elseif ([string]::Equals($indexedSummarySha256, $actualSummarySha256, [StringComparison]::OrdinalIgnoreCase)) {
                                    $indexedRecord["summaryHashStatus"] = "matched"
                                }
                                else {
                                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-index-summary-hash-mismatch" "Artifact '$artifactId' index entry '$indexedPath' declares summarySha256='$indexedSummarySha256' for '$indexedSummaryPath' but the summary file hash is '$actualSummarySha256'."
                                    $artifactValidationOk = $false
                                    $indexedRecord["ok"] = $false
                                    $indexedRecord["summaryHashStatus"] = "mismatch"
                                }

                                $summaryJson = $null
                                try {
                                    $summaryJson = Read-JsonFile $resolvedSummaryPath
                                }
                                catch {
                                    Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-index-summary-json-invalid" $_.Exception.Message
                                    $artifactValidationOk = $false
                                    $indexedRecord["ok"] = $false
                                }

                                if ($null -ne $summaryJson) {
                                    $summaryTool = [string](Get-JsonValue $summaryJson "tool")
                                    $summaryScenarioId = ConvertTo-LiveHistoryScenarioId ([string](Get-JsonValue $summaryJson "scenarioId"))
                                    $summaryComparisonId = ConvertTo-ComparisonId ([string](Get-JsonValue $summaryJson "comparisonId"))
                                    $summaryTracePaths = @(Get-WatcherSummaryTracePaths $summaryJson)
                                    $summaryJsonlPath = if ($summaryTracePaths.Count -gt 0) { [string]$summaryTracePaths[0] } else { "" }
                                    $summarySampleCount = Get-NumericValue (Get-JsonValue $summaryJson "sampleCount")
                                    $summaryMetadataFieldCount = 0
                                    $summaryMetadataCheckCount = 0
                                    $summaryMetadataMismatch = $false
                                    if (-not [string]::IsNullOrWhiteSpace($summaryScenarioId)) {
                                        $summaryMetadataFieldCount++
                                    }
                                    if (-not [string]::IsNullOrWhiteSpace($summaryComparisonId)) {
                                        $summaryMetadataFieldCount++
                                    }
                                    $readyTrace = Test-Truthy (Get-JsonValue $summaryJson "readyForBenchmarkTrace")
                                    $trendStatus = [string](Get-JsonValue $summaryJson "trendStatus")
                                    $nr5SampleCount = [int](Get-JsonValue $summaryJson "nr5SampleCount")
                                    $nr5AgcDiagnosticSampleCount = [int](Get-JsonValue $summaryJson "nr5AgcDiagnosticSampleCount")
                                    $nr5Weak = Get-JsonValue $summaryJson "nr5WeakSignalWatch"
                                    $nr5WeakInputSampleValue = Get-NumericValue (Get-JsonValue $nr5Weak "weakInputSampleCount")
                                    $nr5WeakRecoveredSampleValue = Get-NumericValue (Get-JsonValue $nr5Weak "weakRecoveredSampleCount")
                                    $nr5WeakDropoutSampleValue = Get-NumericValue (Get-JsonValue $nr5Weak "weakDropoutSampleCount")
                                    $nr5HotMakeupSampleValue = Get-NumericValue (Get-JsonValue $nr5Weak "hotMakeupSampleCount")
                                    $nr5WeakInputSampleCount = if ($null -ne $nr5WeakInputSampleValue) { [int]$nr5WeakInputSampleValue } else { 0 }
                                    $nr5WeakRecoveredSampleCount = if ($null -ne $nr5WeakRecoveredSampleValue) { [int]$nr5WeakRecoveredSampleValue } else { 0 }
                                    $nr5WeakDropoutSampleCount = if ($null -ne $nr5WeakDropoutSampleValue) { [int]$nr5WeakDropoutSampleValue } else { 0 }
                                    $nr5HotMakeupSampleCount = if ($null -ne $nr5HotMakeupSampleValue) { [int]$nr5HotMakeupSampleValue } else { 0 }

                                    $indexedRecord["summaryOk"] = ($summaryTool -eq "watch-dsp-live-diagnostics")
                                    $indexedRecord["summaryScenarioId"] = $summaryScenarioId
                                    $indexedRecord["summaryComparisonId"] = $summaryComparisonId
                                    $indexedRecord["summaryJsonlPath"] = $summaryJsonlPath
                                    $indexedRecord["summaryTracePaths"] = @($summaryTracePaths)
                                    if ($null -ne $summarySampleCount) {
                                        $indexedRecord["summarySampleCount"] = [int]$summarySampleCount
                                    }
                                    $indexedRecord["summaryTrendStatus"] = $trendStatus
                                    $indexedRecord["readyForBenchmarkTrace"] = $readyTrace
                                    $indexedRecord["nr5SampleCount"] = $nr5SampleCount
                                    $indexedRecord["nr5AgcDiagnosticSampleCount"] = $nr5AgcDiagnosticSampleCount
                                    $indexedRecord["nr5WeakInputSampleCount"] = $nr5WeakInputSampleCount
                                    $indexedRecord["nr5WeakRecoveredSampleCount"] = $nr5WeakRecoveredSampleCount
                                    $indexedRecord["nr5WeakDropoutSampleCount"] = $nr5WeakDropoutSampleCount
                                    $indexedRecord["nr5HotMakeupSampleCount"] = $nr5HotMakeupSampleCount

                                    if ($indexedScenarioIdsNormalized.Count -gt 0 -and -not [string]::IsNullOrWhiteSpace($summaryScenarioId)) {
                                        $summaryMetadataCheckCount++
                                        $scenarioMatches = Test-StringArrayContains $indexedScenarioIdsNormalized $summaryScenarioId
                                        $indexedRecord["summaryScenarioMatch"] = $scenarioMatches
                                        if (-not $scenarioMatches) {
                                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-index-summary-scenario-mismatch" "Artifact '$artifactId' summary '$indexedSummaryPath' reports scenarioId='$summaryScenarioId' but index entry '$indexedPath' declares scenarioIds='$($indexedScenarioIdsNormalized -join ', ')'."
                                            $artifactValidationOk = $false
                                            $indexedRecord["ok"] = $false
                                            $summaryMetadataMismatch = $true
                                        }
                                    }

                                    if ($indexedExplicitComparisonIdsNormalized.Count -gt 0 -and -not [string]::IsNullOrWhiteSpace($summaryComparisonId)) {
                                        $summaryMetadataCheckCount++
                                        $comparisonMatches = Test-StringArrayContains $indexedExplicitComparisonIdsNormalized $summaryComparisonId
                                        $indexedRecord["summaryComparisonMatch"] = $comparisonMatches
                                        if (-not $comparisonMatches) {
                                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-index-summary-comparison-mismatch" "Artifact '$artifactId' summary '$indexedSummaryPath' reports comparisonId='$summaryComparisonId' but index entry '$indexedPath' declares comparisonIds='$($indexedExplicitComparisonIdsNormalized -join ', ')'."
                                            $artifactValidationOk = $false
                                            $indexedRecord["ok"] = $false
                                            $summaryMetadataMismatch = $true
                                        }
                                    }

                                    if ($summaryMetadataMismatch) {
                                        $indexedRecord["summaryMetadataStatus"] = "mismatch"
                                    }
                                    elseif ($summaryMetadataFieldCount -eq 0) {
                                        $indexedRecord["summaryMetadataStatus"] = "legacy-missing"
                                    }
                                    elseif ($summaryMetadataCheckCount -eq 0) {
                                        $indexedRecord["summaryMetadataStatus"] = "unchecked-no-index-metadata"
                                    }
                                    elseif ($summaryMetadataCheckCount -lt $summaryMetadataFieldCount) {
                                        $indexedRecord["summaryMetadataStatus"] = "partially-matched-unchecked"
                                    }
                                    else {
                                        $indexedRecord["summaryMetadataStatus"] = "matched"
                                    }

                                    if ($null -ne $indexedJsonlLineCount -and $null -ne $summarySampleCount -and $indexedJsonlLineCount -ne [int]$summarySampleCount) {
                                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-index-summary-sample-count-mismatch" "Artifact '$artifactId' summary '$indexedSummaryPath' reports sampleCount=$([int]$summarySampleCount) but index entry '$indexedPath' contains $indexedJsonlLineCount JSONL record(s)."
                                        $artifactValidationOk = $false
                                        $indexedRecord["ok"] = $false
                                    }

                                    $weakCounterSpecs = @(
                                        @{ IndexName = "nr5WeakInputSampleCount"; RecordName = "indexNr5WeakInputSampleCount"; SummaryName = "weakInputSampleCount"; SummaryValue = $nr5WeakInputSampleValue; SummaryCount = $nr5WeakInputSampleCount },
                                        @{ IndexName = "nr5WeakRecoveredSampleCount"; RecordName = "indexNr5WeakRecoveredSampleCount"; SummaryName = "weakRecoveredSampleCount"; SummaryValue = $nr5WeakRecoveredSampleValue; SummaryCount = $nr5WeakRecoveredSampleCount },
                                        @{ IndexName = "nr5WeakDropoutSampleCount"; RecordName = "indexNr5WeakDropoutSampleCount"; SummaryName = "weakDropoutSampleCount"; SummaryValue = $nr5WeakDropoutSampleValue; SummaryCount = $nr5WeakDropoutSampleCount },
                                        @{ IndexName = "nr5HotMakeupSampleCount"; RecordName = "indexNr5HotMakeupSampleCount"; SummaryName = "hotMakeupSampleCount"; SummaryValue = $nr5HotMakeupSampleValue; SummaryCount = $nr5HotMakeupSampleCount }
                                    )
                                    $weakCounterIndexPresentCount = 0
                                    $weakCounterSummaryPresentCount = 0
                                    $weakCounterCheckedCount = 0
                                    $weakCounterMismatchCount = 0
                                    foreach ($weakCounterSpec in $weakCounterSpecs) {
                                        $indexCounterName = [string]$weakCounterSpec["IndexName"]
                                        $indexCounterRecordName = [string]$weakCounterSpec["RecordName"]
                                        $summaryCounterName = [string]$weakCounterSpec["SummaryName"]
                                        $summaryCounterValue = $weakCounterSpec["SummaryValue"]
                                        $summaryCounterCount = [int]$weakCounterSpec["SummaryCount"]
                                        $indexCounterValue = Get-NumericValue (Get-JsonValue $indexedFile $indexCounterName)
                                        if ($null -ne $indexCounterValue) {
                                            $weakCounterIndexPresentCount++
                                            $indexedRecord[$indexCounterRecordName] = [int]$indexCounterValue
                                        }
                                        if ($null -ne $summaryCounterValue) {
                                            $weakCounterSummaryPresentCount++
                                        }
                                        if ($null -eq $indexCounterValue -or $null -eq $summaryCounterValue) {
                                            continue
                                        }

                                        $weakCounterCheckedCount++
                                        if ([int]$indexCounterValue -ne $summaryCounterCount) {
                                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-index-nr5-weak-counter-mismatch" "Artifact '$artifactId' index entry '$indexedPath' declares $indexCounterName=$([int]$indexCounterValue) but summary '$indexedSummaryPath' reports $summaryCounterName=$summaryCounterCount."
                                            $artifactValidationOk = $false
                                            $indexedRecord["ok"] = $false
                                            $weakCounterMismatchCount++
                                        }
                                    }
                                    $indexedRecord["nr5WeakCounterMismatchCount"] = $weakCounterMismatchCount
                                    if ($weakCounterMismatchCount -gt 0) {
                                        $indexedRecord["nr5WeakCounterStatus"] = "mismatch"
                                    }
                                    elseif ($weakCounterCheckedCount -gt 0) {
                                        $indexedRecord["nr5WeakCounterStatus"] = "matched"
                                    }
                                    elseif ($weakCounterIndexPresentCount -gt 0 -and $weakCounterSummaryPresentCount -eq 0) {
                                        $indexedRecord["nr5WeakCounterStatus"] = "unchecked-summary-missing"
                                    }
                                    else {
                                        $indexedRecord["nr5WeakCounterStatus"] = "legacy-missing-index"
                                    }

                                    if ($summaryTracePaths.Count -eq 0) {
                                        $indexedRecord["summaryTracePathStatus"] = "legacy-missing"
                                    }
                                    else {
                                        $tracePathMatchedCount = 0
                                        $tracePathUnchecked = $false
                                        $tracePathMismatch = $false
                                        foreach ($summaryTracePath in $summaryTracePaths) {
                                            $tracePathMatches = Test-ComparableEvidencePathSame -BundlePath $bundlePath -ExpectedPath $indexedPath -ActualPath ([string]$summaryTracePath)
                                            if ($null -eq $tracePathMatches) {
                                                $tracePathUnchecked = $true
                                                continue
                                            }

                                            if ($tracePathMatches) {
                                                $tracePathMatchedCount++
                                                continue
                                            }

                                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-index-summary-trace-path-mismatch" "Artifact '$artifactId' summary '$indexedSummaryPath' reports trace path '$summaryTracePath' but index entry points to '$indexedPath'."
                                            $artifactValidationOk = $false
                                            $indexedRecord["ok"] = $false
                                            $tracePathMismatch = $true
                                        }

                                        if ($tracePathMismatch) {
                                            $indexedRecord["summaryTracePathMatch"] = $false
                                            $indexedRecord["summaryTracePathStatus"] = "mismatch"
                                        }
                                        elseif ($tracePathUnchecked) {
                                            $indexedRecord["summaryTracePathStatus"] = "unchecked"
                                        }
                                        else {
                                            $indexedRecord["summaryTracePathMatch"] = ($tracePathMatchedCount -eq $summaryTracePaths.Count)
                                            $indexedRecord["summaryTracePathStatus"] = "matched"
                                        }
                                    }

                                    if ($summaryTool -ne "watch-dsp-live-diagnostics") {
                                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-index-summary-tool-invalid" "Artifact '$artifactId' summary '$indexedSummaryPath' must be generated by watch-dsp-live-diagnostics.ps1."
                                        $artifactValidationOk = $false
                                        $indexedRecord["ok"] = $false
                                    }

                                    if (-not $readyTrace) {
                                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-index-not-ready" "Artifact '$artifactId' summary '$indexedSummaryPath' reports readyForBenchmarkTrace=false with trendStatus='$trendStatus'."
                                        $artifactValidationOk = $false
                                        $indexedRecord["ok"] = $false
                                    }

                                    if ($trendStatus -eq "nr5-agc-diagnostics-missing") {
                                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-index-nr5-agc-diagnostics-missing" "Artifact '$artifactId' summary '$indexedSummaryPath' lacks NR5 AGC diagnostics; recapture after the backend exports GetRXASPNRAgcDiagnostics."
                                        $artifactValidationOk = $false
                                        $indexedRecord["ok"] = $false
                                    }

                                    if ($nr5SampleCount -gt 0 -and $nr5AgcDiagnosticSampleCount -lt $nr5SampleCount) {
                                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "live-trace-index-nr5-agc-coverage-incomplete" "Artifact '$artifactId' summary '$indexedSummaryPath' has NR5 AGC diagnostics for $nr5AgcDiagnosticSampleCount of $nr5SampleCount NR5 sample(s)."
                                        $artifactValidationOk = $false
                                        $indexedRecord["ok"] = $false
                                    }
                                }
                            }
                        }
                    }
                }

                if ($expectedScenarioIds.Count -gt 0) {
                    $missingScenarioIds = New-Object System.Collections.Generic.List[string]
                    foreach ($expectedScenarioId in $expectedScenarioIds) {
                        $scenario = [string]$expectedScenarioId
                        if (-not [string]::IsNullOrWhiteSpace($scenario) -and -not $coveredScenarioIds.ContainsKey($scenario)) {
                            $missingScenarioIds.Add($scenario) | Out-Null
                        }
                    }

                    $coverageRecord = [ordered]@{
                        artifactId = $artifactId
                        artifactKind = $artifactKind
                        required = $effectiveRequired
                        ok = ($missingScenarioIds.Count -eq 0)
                        requiredScenarioIds = @($expectedScenarioIds)
                        coveredScenarioIds = @($coveredScenarioIds.Keys | Sort-Object)
                        missingScenarioIds = @($missingScenarioIds.ToArray())
                    }
                    $artifactScenarioCoverage.Add($coverageRecord) | Out-Null

                    if ($missingScenarioIds.Count -gt 0) {
                        $artifactMissingScenarioCount += $missingScenarioIds.Count
                        Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "artifact-index-scenario-missing" "Artifact '$artifactId' index is missing scenario coverage: $($missingScenarioIds.ToArray() -join ', ')."
                        $artifactValidationOk = $false
                    }

                    foreach ($expectedScenarioId in $expectedScenarioIds) {
                        $scenario = [string]$expectedScenarioId
                        if ([string]::IsNullOrWhiteSpace($scenario)) {
                            continue
                        }

                        $requiredComparisons = @($expectedComparisonIds)
                        if ($requiredComparisons.Count -eq 0 -and $scenarioRequiredComparisons.ContainsKey($scenario)) {
                            $requiredComparisons = @($scenarioRequiredComparisons[$scenario])
                        }

                        if ($requiredComparisons.Count -eq 0) {
                            continue
                        }

                        $coveredComparisons = @{}
                        if ($coveredComparisonsByScenario.ContainsKey($scenario)) {
                            $coveredComparisons = $coveredComparisonsByScenario[$scenario]
                        }

                        $missingComparisons = New-Object System.Collections.Generic.List[string]
                        foreach ($requiredComparison in $requiredComparisons) {
                            $comparison = ConvertTo-ComparisonId ([string]$requiredComparison)
                            if (-not [string]::IsNullOrWhiteSpace($comparison) -and -not $coveredComparisons.ContainsKey($comparison)) {
                                $missingComparisons.Add($comparison) | Out-Null
                            }
                        }

                        $comparisonCoverageRecord = [ordered]@{
                            artifactId = $artifactId
                            artifactKind = $artifactKind
                            scenarioId = $scenario
                            required = $effectiveRequired
                            ok = ($missingComparisons.Count -eq 0)
                            requiredComparisonIds = @($requiredComparisons)
                            coveredComparisonIds = @($coveredComparisons.Keys | Sort-Object)
                            missingComparisonIds = @($missingComparisons.ToArray())
                        }
                        $artifactComparisonCoverage.Add($comparisonCoverageRecord) | Out-Null

                        if ($missingComparisons.Count -gt 0) {
                            $artifactMissingComparisonCount += $missingComparisons.Count
                            Add-ArtifactIssue $errors $warnings -Required:$effectiveRequired "artifact-index-comparison-missing" "Artifact '$artifactId' index is missing comparison coverage for scenario '$scenario': $($missingComparisons.ToArray() -join ', ')."
                            $artifactValidationOk = $false
                        }
                    }
                }
            }

            $record["ok"] = $artifactValidationOk
        }

        foreach ($requiredPhysicalArtifact in ($captureRequiredPhysicalArtifactIds.Keys | Sort-Object)) {
            if (-not $declaredArtifactIds.ContainsKey($requiredPhysicalArtifact)) {
                $message = "Artifact manifest is missing required physical artifact '$requiredPhysicalArtifact' from the capture manifest."
                Add-ArtifactIssue $errors $warnings -Required:$RequireArtifactFiles "artifact-file-entry-missing" $message
            }
        }
    }
}

if ($externalEngineBakeoffRequiredByScope -and -not (Test-Truthy $externalEngineBakeoffEvidence.present)) {
    Add-AcceptanceIssue $errors $warnings -AllowPreflight:$AllowPreflight "external-bakeoff-required-for-opt-in-comparison" "candidate-external-engine-opt-in comparison evidence is in scope, so artifact '$externalEngineBakeoffArtifactId' must be included and validated before any external DSP/ML bakeoff review."
}

if ((Test-Truthy $offlineFixtureMetricsEvidence.present) -and (Test-Truthy $metricComparisonEvidence.present)) {
    $sourceMetricsPath = [string]$metricComparisonEvidence.sourceMetricsPath
    if ([string]::IsNullOrWhiteSpace($sourceMetricsPath)) {
        Add-AcceptanceIssue $errors $warnings -AllowPreflight:$AllowPreflight "metric-comparison-source-metrics-path-missing" "Fixture metric comparison report must declare the metricsPath used to generate it."
        $metricComparisonEvidence["readyForReview"] = $false
        $metricComparisonEvidence["status"] = "not-ready"
    }
    else {
        $sameMetricsPath = Test-ComparableEvidencePathSame -BundlePath $bundlePath -ExpectedPath ([string]$offlineFixtureMetricsEvidence.path) -ActualPath $sourceMetricsPath
        if ($null -eq $sameMetricsPath -or -not $sameMetricsPath) {
            Add-AcceptanceIssue $errors $warnings -AllowPreflight:$AllowPreflight "metric-comparison-source-metrics-path-mismatch" "Fixture metric comparison metricsPath '$sourceMetricsPath' does not match artifact-manifest offline-fixture-metrics path '$($offlineFixtureMetricsEvidence.path)'."
            $metricComparisonEvidence["readyForReview"] = $false
            $metricComparisonEvidence["status"] = "not-ready"
        }
    }

    $sourceEngine = [string]$offlineFixtureMetricsEvidence.evidenceEngine
    $comparisonEngine = [string]$metricComparisonEvidence.evidenceEngine
    if (-not [string]::IsNullOrWhiteSpace($sourceEngine) -and
        -not [string]::IsNullOrWhiteSpace($comparisonEngine) -and
        -not [string]::Equals($sourceEngine, $comparisonEngine, [StringComparison]::OrdinalIgnoreCase)) {
        Add-AcceptanceIssue $errors $warnings -AllowPreflight:$AllowPreflight "metric-comparison-source-engine-mismatch" "Fixture metric comparison evidenceEngine '$comparisonEngine' does not match offline-fixture-metrics evidenceEngine '$sourceEngine'."
        $metricComparisonEvidence["readyForReview"] = $false
        $metricComparisonEvidence["status"] = "not-ready"
    }

    if ((Test-Truthy $metricComparisonEvidence.wdspBackedEvidence) -and -not (Test-Truthy $offlineFixtureMetricsEvidence.wdspBackedEvidence)) {
        Add-AcceptanceIssue $errors $warnings -AllowPreflight:$AllowPreflight "metric-comparison-source-not-wdsp-backed" "Fixture metric comparison claims WDSP-backed evidence, but offline-fixture-metrics is not WDSP-backed."
        $metricComparisonEvidence["readyForReview"] = $false
        $metricComparisonEvidence["status"] = "not-ready"
    }

    $expectedMetricsSha256 = ([string]$offlineFixtureMetricsEvidence.sha256).Trim().ToLowerInvariant()
    $actualMetricsSha256 = ([string]$metricComparisonEvidence.sourceMetricsSha256).Trim().ToLowerInvariant()
    if ([string]::IsNullOrWhiteSpace($actualMetricsSha256)) {
        $metricComparisonEvidence["sourceMetricsHashStatus"] = "missing"
        Add-AcceptanceIssue $errors $warnings -AllowPreflight:$AllowPreflight "metric-comparison-source-metrics-hash-missing" "Fixture metric comparison report must declare metricsSha256 for the source offline-fixture-metrics artifact."
        $metricComparisonEvidence["readyForReview"] = $false
        $metricComparisonEvidence["status"] = "not-ready"
    }
    elseif ([string]::IsNullOrWhiteSpace($expectedMetricsSha256)) {
        $metricComparisonEvidence["sourceMetricsHashStatus"] = "source-missing"
        Add-AcceptanceIssue $errors $warnings -AllowPreflight:$AllowPreflight "metric-comparison-source-metrics-hash-source-missing" "Strict validation could not compute SHA-256 for the offline-fixture-metrics artifact."
        $metricComparisonEvidence["readyForReview"] = $false
        $metricComparisonEvidence["status"] = "not-ready"
    }
    elseif (-not [string]::Equals($expectedMetricsSha256, $actualMetricsSha256, [StringComparison]::OrdinalIgnoreCase)) {
        $metricComparisonEvidence["sourceMetricsHashStatus"] = "mismatch"
        Add-AcceptanceIssue $errors $warnings -AllowPreflight:$AllowPreflight "metric-comparison-source-metrics-hash-mismatch" "Fixture metric comparison metricsSha256 '$actualMetricsSha256' does not match current offline-fixture-metrics SHA-256 '$expectedMetricsSha256'. Rerun run-dsp-wdsp-fixture-matrix.ps1 after regenerating fixture evidence."
        $metricComparisonEvidence["readyForReview"] = $false
        $metricComparisonEvidence["status"] = "not-ready"
    }
    else {
        $metricComparisonEvidence["sourceMetricsHashStatus"] = "match"
    }

    $expectedRuntimeSha256 = ([string]$offlineFixtureMetricsEvidence.wdspRuntimeSha256).Trim().ToLowerInvariant()
    $actualRuntimeSha256 = ([string]$metricComparisonEvidence.wdspRuntimeSha256).Trim().ToLowerInvariant()
    if ((Test-Truthy $offlineFixtureMetricsEvidence.wdspBackedEvidence) -or (Test-Truthy $metricComparisonEvidence.wdspBackedEvidence)) {
        if ([string]::IsNullOrWhiteSpace($actualRuntimeSha256)) {
            $metricComparisonEvidence["runtimeHashStatus"] = "missing"
            Add-AcceptanceIssue $errors $warnings -AllowPreflight:$AllowPreflight "metric-comparison-runtime-hash-missing" "Fixture metric comparison report must declare wdspRuntimeSha256 for the native WDSP runtime used by the source fixture run."
            $metricComparisonEvidence["readyForReview"] = $false
            $metricComparisonEvidence["status"] = "not-ready"
        }
        elseif ([string]::IsNullOrWhiteSpace($expectedRuntimeSha256)) {
            $metricComparisonEvidence["runtimeHashStatus"] = "source-missing"
            Add-AcceptanceIssue $errors $warnings -AllowPreflight:$AllowPreflight "metric-comparison-source-runtime-hash-missing" "offline-fixture-metrics must declare wdspRuntimeSha256 before a fixture comparison can be accepted."
            $metricComparisonEvidence["readyForReview"] = $false
            $metricComparisonEvidence["status"] = "not-ready"
        }
        elseif (-not [string]::Equals($expectedRuntimeSha256, $actualRuntimeSha256, [StringComparison]::OrdinalIgnoreCase)) {
            $metricComparisonEvidence["runtimeHashStatus"] = "mismatch"
            Add-AcceptanceIssue $errors $warnings -AllowPreflight:$AllowPreflight "metric-comparison-runtime-hash-mismatch" "Fixture metric comparison wdspRuntimeSha256 '$actualRuntimeSha256' does not match offline-fixture-metrics wdspRuntimeSha256 '$expectedRuntimeSha256'. Rerun run-dsp-wdsp-fixture-matrix.ps1 after regenerating fixture evidence."
            $metricComparisonEvidence["readyForReview"] = $false
            $metricComparisonEvidence["status"] = "not-ready"
        }
        else {
            $metricComparisonEvidence["runtimeHashStatus"] = "match"
        }
    }
    else {
        $metricComparisonEvidence["runtimeHashStatus"] = "not-wdsp-backed"
    }
}

if ((Test-Truthy $offlineFixtureMetricsEvidence.present) -and (Test-Truthy $nativeRuntimeArtifactAuditEvidence.present)) {
    $sourceRuntimeRid = [string]$offlineFixtureMetricsEvidence.wdspRuntimeRid
    $sourceRuntimeSha256 = ([string]$offlineFixtureMetricsEvidence.wdspRuntimeSha256).Trim().ToLowerInvariant()
    $auditWinX64Sha256 = ([string]$nativeRuntimeArtifactAuditEvidence.winX64NativeSha256).Trim().ToLowerInvariant()
    if (Test-Truthy $offlineFixtureMetricsEvidence.wdspBackedEvidence) {
        if ([string]::IsNullOrWhiteSpace($sourceRuntimeRid)) {
            $offlineFixtureMetricsEvidence["runtimeArtifactHashStatus"] = "rid-missing"
            Add-AcceptanceIssue $errors $warnings -AllowPreflight:$AllowPreflight "offline-fixture-runtime-rid-missing" "offline-fixture-metrics must declare wdspRuntimeRid so fixture evidence can be matched to the packaged runtime audit."
            $metricComparisonEvidence["readyForReview"] = $false
            $metricComparisonEvidence["status"] = "not-ready"
        }
        elseif ([string]::Equals($sourceRuntimeRid, "win-x64", [StringComparison]::OrdinalIgnoreCase)) {
            if ([string]::IsNullOrWhiteSpace($auditWinX64Sha256)) {
                $offlineFixtureMetricsEvidence["runtimeArtifactHashStatus"] = "audit-missing"
                Add-AcceptanceIssue $errors $warnings -AllowPreflight:$AllowPreflight "offline-fixture-runtime-audit-hash-missing" "wdsp-runtime-artifact-audit must declare winX64NativeSha256 before Win x64 WDSP-backed fixture evidence can be accepted."
                $metricComparisonEvidence["readyForReview"] = $false
                $metricComparisonEvidence["status"] = "not-ready"
            }
            elseif ([string]::IsNullOrWhiteSpace($sourceRuntimeSha256)) {
                $offlineFixtureMetricsEvidence["runtimeArtifactHashStatus"] = "source-missing"
                Add-AcceptanceIssue $errors $warnings -AllowPreflight:$AllowPreflight "offline-fixture-runtime-hash-missing" "offline-fixture-metrics must declare wdspRuntimeSha256 before it can be matched to the packaged Win x64 runtime audit."
                $metricComparisonEvidence["readyForReview"] = $false
                $metricComparisonEvidence["status"] = "not-ready"
            }
            elseif (-not [string]::Equals($sourceRuntimeSha256, $auditWinX64Sha256, [StringComparison]::OrdinalIgnoreCase)) {
                $offlineFixtureMetricsEvidence["runtimeArtifactHashStatus"] = "mismatch"
                Add-AcceptanceIssue $errors $warnings -AllowPreflight:$AllowPreflight "offline-fixture-runtime-audit-hash-mismatch" "offline-fixture-metrics wdspRuntimeSha256 '$sourceRuntimeSha256' does not match wdsp-runtime-artifact-audit winX64NativeSha256 '$auditWinX64Sha256'. Regenerate fixture evidence from the packaged runtime under review."
                $metricComparisonEvidence["readyForReview"] = $false
                $metricComparisonEvidence["status"] = "not-ready"
            }
            else {
                $offlineFixtureMetricsEvidence["runtimeArtifactHashStatus"] = "match"
            }
        }
        else {
            $offlineFixtureMetricsEvidence["runtimeArtifactHashStatus"] = "not-win-x64"
        }
    }
    else {
        $offlineFixtureMetricsEvidence["runtimeArtifactHashStatus"] = "not-wdsp-backed"
    }
}

if ($null -eq $plan) {
    Add-ValidationIssue $errors "error" "benchmark-plan-missing" "benchmark-plan.json is required."
}
else {
    $globalGates = Get-JsonArray $plan "globalAcceptanceGates"
    $gateText = $globalGates -join " "
    if ($gateText -notlike "*No weak-signal loss*" -or $gateText -notlike "*No TX clipping*") {
        Add-ValidationIssue $errors "error" "benchmark-gates-incomplete" "Benchmark plan must include weak-signal and TX clipping acceptance gates."
    }
}

$ok = ($errors.Count -eq 0)
$report = [ordered]@{
    schemaVersion = 1
    tool = "validate-dsp-modernization-bundle"
    generatedUtc = [DateTimeOffset]::UtcNow
    bundleDir = $bundlePath
    ok = $ok
    allowPreflight = [bool]$AllowPreflight
    requireArtifactFiles = [bool]$RequireArtifactFiles
    artifactManifestProvided = $artifactManifestProvided
    artifactManifestPath = $artifactManifestReportPath
    artifactFileCount = $artifactFiles.Count
    requiredArtifactFileCount = $requiredArtifactFileCount
    artifactReferencedFileCount = $artifactReferencedFiles.Count
    requiredArtifactReferencedFileCount = $requiredArtifactReferencedFileCount
    artifactScenarioCoverageCount = $artifactScenarioCoverage.Count
    artifactMissingScenarioCount = $artifactMissingScenarioCount
    artifactComparisonCoverageCount = $artifactComparisonCoverage.Count
    artifactMissingComparisonCount = $artifactMissingComparisonCount
    artifactMetricCoverageCount = $artifactMetricCoverage.Count
    artifactMissingMetricCount = $artifactMissingMetricCount
    artifactGateOutcomeCount = $artifactGateOutcomeCount
    artifactFailedGateCount = $artifactFailedGateCount
    hardwareTarget = $hardwareEvidence.planTarget
    captureHardwareTarget = $hardwareEvidence.manifestTarget
    hardwareDiagnosticsPresent = $hardwareEvidence.diagnosticsPresent
    hardwareEvidenceStatus = $hardwareEvidence.status
    offlineFixtureMetricsPresent = $offlineFixtureMetricsEvidence.present
    offlineFixtureMetricsEvidenceEngine = $offlineFixtureMetricsEvidence.evidenceEngine
    offlineFixtureMetricsEvidenceTool = $offlineFixtureMetricsEvidence.evidenceTool
    offlineFixtureMetricsWdspBackedEvidence = $offlineFixtureMetricsEvidence.wdspBackedEvidence
    offlineFixtureMetricsSyntheticFallbackEvidence = $offlineFixtureMetricsEvidence.syntheticFallbackEvidence
    offlineFixtureMetricsScenarioCount = $offlineFixtureMetricsEvidence.scenarioCount
    offlineFixtureMetricsComparisonCount = $offlineFixtureMetricsEvidence.comparisonCount
    offlineFixtureMetricsPath = $offlineFixtureMetricsEvidence.path
    offlineFixtureMetricsSha256 = $offlineFixtureMetricsEvidence.sha256
    offlineFixtureMetricsWdspRuntimeRid = $offlineFixtureMetricsEvidence.wdspRuntimeRid
    offlineFixtureMetricsWdspRuntimePath = $offlineFixtureMetricsEvidence.wdspRuntimePath
    offlineFixtureMetricsWdspRuntimePathKind = $offlineFixtureMetricsEvidence.wdspRuntimePathKind
    offlineFixtureMetricsWdspRuntimeFileName = $offlineFixtureMetricsEvidence.wdspRuntimeFileName
    offlineFixtureMetricsWdspRuntimeLength = $offlineFixtureMetricsEvidence.wdspRuntimeLength
    offlineFixtureMetricsWdspRuntimeSha256 = $offlineFixtureMetricsEvidence.wdspRuntimeSha256
    offlineFixtureMetricsWdspRuntimeStatus = $offlineFixtureMetricsEvidence.wdspRuntimeStatus
    offlineFixtureMetricsRuntimeArtifactHashStatus = $offlineFixtureMetricsEvidence.runtimeArtifactHashStatus
    metricComparisonPresent = $metricComparisonEvidence.present
    metricComparisonReady = $metricComparisonEvidence.readyForReview
    metricComparisonReportReadyForReview = $metricComparisonEvidence.reportReadyForReview
    metricComparisonMetricCoverageReadyForReview = $metricComparisonEvidence.metricCoverageReadyForReview
    metricComparisonEvidenceEngine = $metricComparisonEvidence.evidenceEngine
    metricComparisonEvidenceTool = $metricComparisonEvidence.evidenceTool
    metricComparisonWdspBackedEvidence = $metricComparisonEvidence.wdspBackedEvidence
    metricComparisonSyntheticFallbackEvidence = $metricComparisonEvidence.syntheticFallbackEvidence
    metricComparisonSourceMetricsPath = $metricComparisonEvidence.sourceMetricsPath
    metricComparisonSourceMetricsSha256 = $metricComparisonEvidence.sourceMetricsSha256
    metricComparisonSourceMetricsHashStatus = $metricComparisonEvidence.sourceMetricsHashStatus
    metricComparisonWdspRuntimeRid = $metricComparisonEvidence.wdspRuntimeRid
    metricComparisonWdspRuntimePath = $metricComparisonEvidence.wdspRuntimePath
    metricComparisonWdspRuntimePathKind = $metricComparisonEvidence.wdspRuntimePathKind
    metricComparisonWdspRuntimeFileName = $metricComparisonEvidence.wdspRuntimeFileName
    metricComparisonWdspRuntimeLength = $metricComparisonEvidence.wdspRuntimeLength
    metricComparisonWdspRuntimeSha256 = $metricComparisonEvidence.wdspRuntimeSha256
    metricComparisonWdspRuntimeStatus = $metricComparisonEvidence.wdspRuntimeStatus
    metricComparisonWdspRuntimeIdentityReadyForReview = $metricComparisonEvidence.wdspRuntimeIdentityReadyForReview
    metricComparisonWdspRuntimeHashStatus = $metricComparisonEvidence.runtimeHashStatus
    metricComparisonFixtureScenarioScope = $metricComparisonEvidence.fixtureScenarioScope
    metricComparisonSkippedNonFixtureScenarioCount = $metricComparisonEvidence.skippedNonFixtureScenarioCount
    metricComparisonSkippedNonFixtureScenarioIds = @($metricComparisonEvidence.skippedNonFixtureScenarioIds)
    metricComparisonRegressionCount = $metricComparisonEvidence.regressionCount
    metricComparisonGateFailureCount = $metricComparisonEvidence.gateFailureCount
    metricComparisonMissingScenarioCount = $metricComparisonEvidence.missingScenarioCount
    metricComparisonMissingScenarios = @($metricComparisonEvidence.missingScenarios)
    metricComparisonMissingCurrentBaselineCount = $metricComparisonEvidence.missingCurrentBaselineCount
    metricComparisonMissingThetisBaselineCount = $metricComparisonEvidence.missingThetisBaselineCount
    metricComparisonMissingCandidateCount = $metricComparisonEvidence.missingCandidateCount
    metricComparisonMissingMetricValueCount = $metricComparisonEvidence.missingMetricValueCount
    metricComparisonCandidateComparisonCount = $metricComparisonEvidence.candidateComparisonCount
    liveTraceComparisonPresent = $liveTraceComparisonEvidence.present
    liveTraceComparisonReady = $liveTraceComparisonEvidence.readyForReview
    liveTraceComparisonCandidateComparisonCount = $liveTraceComparisonEvidence.candidateComparisonCount
    liveTraceComparisonFailedComparisonCount = $liveTraceComparisonEvidence.failedComparisonCount
    liveTraceComparisonMissingBaselineCount = $liveTraceComparisonEvidence.missingBaselineCount
    liveTraceComparisonMissingCandidateCount = $liveTraceComparisonEvidence.missingCandidateCount
    liveTraceComparisonRegressionCount = $liveTraceComparisonEvidence.regressionCount
    liveTraceComparisonHardConstraintRegressionCount = $liveTraceComparisonEvidence.hardConstraintRegressionCount
    liveTraceComparisonGateFailureCount = $liveTraceComparisonEvidence.gateFailureCount
    liveTraceComparisonMetricRegressionDetailCount = $liveTraceComparisonEvidence.metricRegressionDetailCount
    liveTraceComparisonHardConstraintRegressionDetailCount = $liveTraceComparisonEvidence.hardConstraintRegressionDetailCount
    liveTraceComparisonGateFailureDetailCount = $liveTraceComparisonEvidence.gateFailureDetailCount
    liveTraceComparisonMissingMetricDetailCount = $liveTraceComparisonEvidence.missingMetricDetailCount
    liveTraceComparisonMetricRegressionSafetyClassCounts = @($liveTraceComparisonEvidence.metricRegressionSafetyClassCounts)
    liveTraceComparisonMetricMissingSafetyClassCounts = @($liveTraceComparisonEvidence.metricMissingSafetyClassCounts)
    liveTraceComparisonPumpingRegressionCount = $liveTraceComparisonEvidence.pumpingRegressionCount
    liveTraceComparisonWeakSignalRegressionCount = $liveTraceComparisonEvidence.weakSignalRegressionCount
    liveTraceComparisonClippingRegressionCount = $liveTraceComparisonEvidence.clippingRegressionCount
    liveTraceComparisonReadinessRegressionCount = $liveTraceComparisonEvidence.readinessRegressionCount
    liveTraceComparisonHardGateRegressionCount = $liveTraceComparisonEvidence.hardGateRegressionCount
    liveTraceComparisonFreshnessRegressionCount = $liveTraceComparisonEvidence.freshnessRegressionCount
    liveTraceComparisonFrontEndRegressionCount = $liveTraceComparisonEvidence.frontEndRegressionCount
    liveTraceComparisonAudioPathRegressionCount = $liveTraceComparisonEvidence.audioPathRegressionCount
    liveTraceComparisonToolingRegressionCount = $liveTraceComparisonEvidence.toolingRegressionCount
    liveTraceComparisonBundleRelativePaths = $liveTraceComparisonEvidence.bundleRelativePaths
    liveTraceComparisonAbsolutePathCount = $liveTraceComparisonEvidence.absolutePathCount
    liveTraceComparisonNr5WeakInputSampleDelta = $liveTraceComparisonEvidence.nr5WeakInputSampleDelta
    liveTraceComparisonNr5WeakRecoveredSampleDelta = $liveTraceComparisonEvidence.nr5WeakRecoveredSampleDelta
    liveTraceComparisonNr5WeakDropoutSampleDelta = $liveTraceComparisonEvidence.nr5WeakDropoutSampleDelta
    liveTraceComparisonNr5HotMakeupSampleDelta = $liveTraceComparisonEvidence.nr5HotMakeupSampleDelta
    liveTraceComparisonNr5WeakRecoveryPctDelta = $liveTraceComparisonEvidence.nr5WeakRecoveryPctDelta
    liveTraceComparisonNr5OutputMovementDbDelta = $liveTraceComparisonEvidence.nr5OutputMovementDbDelta
    liveTraceComparisonNr5MakeupMovementDbDelta = $liveTraceComparisonEvidence.nr5MakeupMovementDbDelta
    liveTraceComparisonNr5MakeupMaxDbDelta = $liveTraceComparisonEvidence.nr5MakeupMaxDbDelta
    liveTraceComparisonNr5RecoveryDriveMovementDelta = $liveTraceComparisonEvidence.nr5RecoveryDriveMovementDelta
    liveTraceComparisonNr5TextureFillAverageDelta = $liveTraceComparisonEvidence.nr5TextureFillAverageDelta
    liveTraceComparisonNr5OutputMovementRegressionCount = $liveTraceComparisonEvidence.nr5OutputMovementRegressionCount
    liveTraceComparisonNr5MakeupMovementRegressionCount = $liveTraceComparisonEvidence.nr5MakeupMovementRegressionCount
    liveTraceComparisonNr5MakeupMaxRegressionCount = $liveTraceComparisonEvidence.nr5MakeupMaxRegressionCount
    liveTraceComparisonNr5RecoveryDriveMovementRegressionCount = $liveTraceComparisonEvidence.nr5RecoveryDriveMovementRegressionCount
    liveDiagnosticsHistoryPresent = $liveDiagnosticsHistoryEvidence.present
    liveDiagnosticsHistoryReady = $liveDiagnosticsHistoryEvidence.readyForReview
    liveDiagnosticsHistoryTraceCount = $liveDiagnosticsHistoryEvidence.traceCount
    liveDiagnosticsHistoryNr5TraceCount = $liveDiagnosticsHistoryEvidence.nr5TraceCount
    liveDiagnosticsHistoryReadyTraceCount = $liveDiagnosticsHistoryEvidence.readyTraceCount
    liveDiagnosticsHistoryReadyNr5TraceCount = $liveDiagnosticsHistoryEvidence.readyNr5TraceCount
    liveDiagnosticsHistoryCandidateReadyNr5TraceCount = $liveDiagnosticsHistoryEvidence.candidateReadyNr5TraceCount
    liveDiagnosticsHistoryLatestTraceId = $liveDiagnosticsHistoryEvidence.latestTraceId
    liveDiagnosticsHistoryLatestReviewStatus = $liveDiagnosticsHistoryEvidence.latestReviewStatus
    liveDiagnosticsHistoryLatestSafetyRiskScore = $liveDiagnosticsHistoryEvidence.latestSafetyRiskScore
    liveDiagnosticsHistoryLatestTraceSequenceUtc = $liveDiagnosticsHistoryEvidence.latestTraceSequenceUtc
    liveDiagnosticsHistoryLatestTraceSortKeySource = $liveDiagnosticsHistoryEvidence.latestTraceSortKeySource
    liveDiagnosticsHistoryTraceOrderingStatus = $liveDiagnosticsHistoryEvidence.traceOrderingStatus
    liveDiagnosticsHistoryTraceOrderingViolationCount = $liveDiagnosticsHistoryEvidence.traceOrderingViolationCount
    liveDiagnosticsHistoryBestBalancedTraceId = $liveDiagnosticsHistoryEvidence.bestBalancedTraceId
    liveDiagnosticsHistoryBestWeakSignalTraceId = $liveDiagnosticsHistoryEvidence.bestWeakSignalTraceId
    liveDiagnosticsHistoryLowestPumpingTraceId = $liveDiagnosticsHistoryEvidence.lowestPumpingTraceId
    liveDiagnosticsHistoryPromotionStatus = $liveDiagnosticsHistoryEvidence.promotionStatus
    liveDiagnosticsHistoryCandidatePromotionReady = $liveDiagnosticsHistoryEvidence.candidatePromotionReady
    liveDiagnosticsHistoryPromotionRecommendedTraceId = $liveDiagnosticsHistoryEvidence.promotionRecommendedTraceId
    liveDiagnosticsHistoryPromotionRecommendedTraceRole = $liveDiagnosticsHistoryEvidence.promotionRecommendedTraceRole
    liveDiagnosticsHistoryPromotionReferenceTraceId = $liveDiagnosticsHistoryEvidence.promotionReferenceTraceId
    liveDiagnosticsHistoryPromotionBlockerCount = $liveDiagnosticsHistoryEvidence.promotionBlockerCount
    liveDiagnosticsHistoryPromotionBlockerClasses = @($liveDiagnosticsHistoryEvidence.promotionBlockerClasses)
    liveDiagnosticsHistoryPromotionNextAction = $liveDiagnosticsHistoryEvidence.promotionNextAction
    liveDiagnosticsHistoryAggregateSafetyClassCounts = @($liveDiagnosticsHistoryEvidence.aggregateSafetyClassCounts)
    liveDiagnosticsHistoryAggregateReadinessGaps = @($liveDiagnosticsHistoryEvidence.aggregateReadinessGaps)
    liveDiagnosticsHistoryReadinessGapSignalCount = $liveDiagnosticsHistoryEvidence.readinessGapSignalCount
    liveDiagnosticsHistoryReadinessGapNumericSignalCount = $liveDiagnosticsHistoryEvidence.readinessGapNumericSignalCount
    liveDiagnosticsHistoryReadinessGapMax = $liveDiagnosticsHistoryEvidence.readinessGapMax
    liveDiagnosticsHistoryReadinessTrendStatus = $liveDiagnosticsHistoryEvidence.readinessTrendStatus
    liveDiagnosticsHistoryReadinessTrendPreviousTraceId = $liveDiagnosticsHistoryEvidence.readinessTrendPreviousTraceId
    liveDiagnosticsHistoryReadinessTrendSignalCount = $liveDiagnosticsHistoryEvidence.readinessTrendSignalCount
    liveDiagnosticsHistoryReadinessTrendImprovedSignalCount = $liveDiagnosticsHistoryEvidence.readinessTrendImprovedSignalCount
    liveDiagnosticsHistoryReadinessTrendRegressedSignalCount = $liveDiagnosticsHistoryEvidence.readinessTrendRegressedSignalCount
    liveDiagnosticsHistoryReadinessTrendResolvedSignalCount = $liveDiagnosticsHistoryEvidence.readinessTrendResolvedSignalCount
    liveDiagnosticsHistoryReadinessTrendNewSignalCount = $liveDiagnosticsHistoryEvidence.readinessTrendNewSignalCount
    liveDiagnosticsHistoryReadinessTrendGapMaxDelta = $liveDiagnosticsHistoryEvidence.readinessTrendGapMaxDelta
    liveDiagnosticsHistoryReferenceTrendStatus = $liveDiagnosticsHistoryEvidence.referenceTrendStatus
    liveDiagnosticsHistoryReferenceTrendReferenceTraceId = $liveDiagnosticsHistoryEvidence.referenceTrendReferenceTraceId
    liveDiagnosticsHistoryReferenceTrendReferenceTraceRole = $liveDiagnosticsHistoryEvidence.referenceTrendReferenceTraceRole
    liveDiagnosticsHistoryReferenceTrendSignalCount = $liveDiagnosticsHistoryEvidence.referenceTrendSignalCount
    liveDiagnosticsHistoryReferenceTrendImprovedSignalCount = $liveDiagnosticsHistoryEvidence.referenceTrendImprovedSignalCount
    liveDiagnosticsHistoryReferenceTrendRegressedSignalCount = $liveDiagnosticsHistoryEvidence.referenceTrendRegressedSignalCount
    liveDiagnosticsHistoryReferenceTrendResolvedSignalCount = $liveDiagnosticsHistoryEvidence.referenceTrendResolvedSignalCount
    liveDiagnosticsHistoryReferenceTrendNewSignalCount = $liveDiagnosticsHistoryEvidence.referenceTrendNewSignalCount
    liveDiagnosticsHistoryReferenceTrendGapMaxDelta = $liveDiagnosticsHistoryEvidence.referenceTrendGapMaxDelta
    liveDiagnosticsHistoryTuningActionPlanStatus = $liveDiagnosticsHistoryEvidence.tuningActionPlanStatus
    liveDiagnosticsHistoryTuningActionPlanDirectionStatus = $liveDiagnosticsHistoryEvidence.tuningActionPlanDirectionStatus
    liveDiagnosticsHistoryTuningActionPlanPrimarySafetyClass = $liveDiagnosticsHistoryEvidence.tuningActionPlanPrimarySafetyClass
    liveDiagnosticsHistoryTuningActionPlanPrimarySignalName = $liveDiagnosticsHistoryEvidence.tuningActionPlanPrimarySignalName
    liveDiagnosticsHistoryTuningActionPlanTopControlFamily = $liveDiagnosticsHistoryEvidence.tuningActionPlanTopControlFamily
    liveDiagnosticsHistoryTuningActionPlanActionCount = $liveDiagnosticsHistoryEvidence.tuningActionPlanActionCount
    liveDiagnosticsHistoryLiveExperimentPlanStatus = $liveDiagnosticsHistoryEvidence.liveExperimentPlanStatus
    liveDiagnosticsHistoryLiveExperimentPlanDirectionStatus = $liveDiagnosticsHistoryEvidence.liveExperimentPlanDirectionStatus
    liveDiagnosticsHistoryLiveExperimentPlanPrimaryControlFamily = $liveDiagnosticsHistoryEvidence.liveExperimentPlanPrimaryControlFamily
    liveDiagnosticsHistoryLiveExperimentPlanScenarioCount = $liveDiagnosticsHistoryEvidence.liveExperimentPlanScenarioCount
    liveDiagnosticsHistoryLiveExperimentPlanRecommendedSampleCount = $liveDiagnosticsHistoryEvidence.liveExperimentPlanRecommendedSampleCount
    liveDiagnosticsHistoryLiveExperimentPlanRecommendedIntervalMs = $liveDiagnosticsHistoryEvidence.liveExperimentPlanRecommendedIntervalMs
    liveDiagnosticsHistoryLiveExperimentCoverageStatus = $liveDiagnosticsHistoryEvidence.liveExperimentCoverageStatus
    liveDiagnosticsHistoryLiveExperimentCoverageScenarioCount = $liveDiagnosticsHistoryEvidence.liveExperimentCoverageScenarioCount
    liveDiagnosticsHistoryLiveExperimentCoverageCoveredScenarioCount = $liveDiagnosticsHistoryEvidence.liveExperimentCoverageCoveredScenarioCount
    liveDiagnosticsHistoryLiveExperimentCoverageRequiredComparisonCount = $liveDiagnosticsHistoryEvidence.liveExperimentCoverageRequiredComparisonCount
    liveDiagnosticsHistoryLiveExperimentCoverageCoveredComparisonCount = $liveDiagnosticsHistoryEvidence.liveExperimentCoverageCoveredComparisonCount
    liveDiagnosticsHistoryLiveExperimentCoverageMissingComparisonCount = $liveDiagnosticsHistoryEvidence.liveExperimentCoverageMissingComparisonCount
    liveDiagnosticsHistoryPumpingSignalCount = $liveDiagnosticsHistoryEvidence.pumpingSignalCount
    liveDiagnosticsHistoryWeakSignalCount = $liveDiagnosticsHistoryEvidence.weakSignalCount
    liveDiagnosticsHistoryRecommendationCount = $liveDiagnosticsHistoryEvidence.recommendationCount
    liveDiagnosticsHistoryTraceSourceStatus = $liveDiagnosticsHistoryEvidence.traceSourceStatus
    liveDiagnosticsHistoryTraceSourceCheckedCount = $liveDiagnosticsHistoryEvidence.traceSourceCheckedCount
    liveDiagnosticsHistoryTraceSourceMissingCount = $liveDiagnosticsHistoryEvidence.traceSourceMissingCount
    liveDiagnosticsHistoryTraceSourceInvalidCount = $liveDiagnosticsHistoryEvidence.traceSourceInvalidCount
    liveDiagnosticsHistoryTraceSourceJsonlMissingCount = $liveDiagnosticsHistoryEvidence.traceSourceJsonlMissingCount
    liveDiagnosticsHistoryTraceSourceSummaryHashPresentCount = $liveDiagnosticsHistoryEvidence.traceSourceSummaryHashPresentCount
    liveDiagnosticsHistoryTraceSourceSummaryHashMissingCount = $liveDiagnosticsHistoryEvidence.traceSourceSummaryHashMissingCount
    liveDiagnosticsHistoryTraceSourceSummaryHashMismatchCount = $liveDiagnosticsHistoryEvidence.traceSourceSummaryHashMismatchCount
    liveDiagnosticsHistoryTraceSourceJsonlHashPresentCount = $liveDiagnosticsHistoryEvidence.traceSourceJsonlHashPresentCount
    liveDiagnosticsHistoryTraceSourceJsonlHashMissingCount = $liveDiagnosticsHistoryEvidence.traceSourceJsonlHashMissingCount
    liveDiagnosticsHistoryTraceSourceJsonlHashMismatchCount = $liveDiagnosticsHistoryEvidence.traceSourceJsonlHashMismatchCount
    liveDiagnosticsHistoryBundleRelativePaths = $liveDiagnosticsHistoryEvidence.bundleRelativePaths
    liveDiagnosticsHistoryAbsolutePathCount = $liveDiagnosticsHistoryEvidence.absolutePathCount
    nativeSymbolAuditPresent = $nativeSymbolAuditEvidence.present
    nativeSymbolAuditReady = $nativeSymbolAuditEvidence.readyForReview
    nativeSymbolAuditImportedSymbolCount = $nativeSymbolAuditEvidence.importedSymbolCount
    nativeSymbolAuditSourceMissingRequiredCount = $nativeSymbolAuditEvidence.sourceMissingRequiredCount
    nativeSymbolAuditSignatureMismatchCount = $nativeSymbolAuditEvidence.signatureMismatchCount
    nativeSymbolAuditBinaryExportsEvaluated = $nativeSymbolAuditEvidence.binaryExportsEvaluated
    nativeSymbolAuditBinaryExportCount = $nativeSymbolAuditEvidence.binaryExportCount
    nativeSymbolAuditBinaryMissingRequiredCount = $nativeSymbolAuditEvidence.binaryMissingRequiredCount
    nativeRuntimeArtifactAuditPresent = $nativeRuntimeArtifactAuditEvidence.present
    nativeRuntimeArtifactAuditReadyForWinX64Package = $nativeRuntimeArtifactAuditEvidence.readyForWinX64Package
    nativeRuntimeArtifactAuditPendingRidCount = $nativeRuntimeArtifactAuditEvidence.pendingRidCount
    nativeRuntimeArtifactAuditArtifactCount = $nativeRuntimeArtifactAuditEvidence.artifactCount
    nativeRuntimeArtifactAuditWinX64NativePath = $nativeRuntimeArtifactAuditEvidence.winX64NativePath
    nativeRuntimeArtifactAuditWinX64NativeLength = $nativeRuntimeArtifactAuditEvidence.winX64NativeLength
    nativeRuntimeArtifactAuditWinX64NativeSha256 = $nativeRuntimeArtifactAuditEvidence.winX64NativeSha256
    metricCatalogPresent = $metricCatalogEvidence.present
    metricCatalogStatus = $metricCatalogEvidence.status
    metricCatalogMetricCount = $metricCatalogEvidence.metricCount
    metricCatalogMissingRequiredMetricCount = $metricCatalogEvidence.missingRequiredMetricCount
    externalEngineCandidatesPresent = $externalEngineCandidateEvidence.present
    externalEngineCandidateStatus = $externalEngineCandidateEvidence.status
    externalEngineCandidateCount = $externalEngineCandidateEvidence.candidateCount
    externalEngineCandidateMissingCount = $externalEngineCandidateEvidence.missingCandidateIds.Count
    externalEngineCandidateUnsafeCount = $externalEngineCandidateEvidence.unsafeCandidateCount
    externalEngineCandidateSnapshotMismatchCount = $externalEngineCandidateEvidence.snapshotMismatchCount
    externalEngineBakeoffReportPresent = $externalEngineBakeoffEvidence.present
    externalEngineBakeoffReady = $externalEngineBakeoffEvidence.readyForReview
    externalEngineBakeoffRequiredByScope = $externalEngineBakeoffRequiredByScope
    externalEngineBakeoffCandidateCount = $externalEngineBakeoffEvidence.candidateCount
    externalEngineBakeoffSafeForBakeoffCount = $externalEngineBakeoffEvidence.safeForBakeoffCount
    externalEngineBakeoffBlockedCandidateCount = $externalEngineBakeoffEvidence.blockedCandidateCount
    externalEngineBakeoffIntegrationReadyCandidateCount = $externalEngineBakeoffEvidence.integrationReadyCandidateCount
    externalEngineBakeoffMissingCandidateCount = $externalEngineBakeoffEvidence.missingCandidateCount
    externalEngineBakeoffUnsafeCandidateCount = $externalEngineBakeoffEvidence.unsafeCandidateCount
    externalEngineBakeoffSnapshotMismatchCount = $externalEngineBakeoffEvidence.snapshotMismatchCount
    externalEngineBakeoffCandidateSha256 = $externalEngineBakeoffEvidence.candidateSha256
    externalEngineBakeoffSnapshotSha256 = $externalEngineBakeoffEvidence.snapshotSha256
    externalEngineBakeoffPlanCandidateCount = $externalEngineBakeoffEvidence.bakeoffPlanCandidateCount
    externalEngineBakeoffPlanScenarioCount = $externalEngineBakeoffEvidence.bakeoffPlanScenarioCount
    externalEngineBakeoffPlanDefaultBehaviorChangeReady = $externalEngineBakeoffEvidence.bakeoffPlanDefaultBehaviorChangeReady
    externalEngineBakeoffPlanRawWdspIqReplacementAllowed = $externalEngineBakeoffEvidence.bakeoffPlanRawWdspIqReplacementAllowed
    errorCount = $errors.Count
    warningCount = $warnings.Count
    hardwareEvidence = $hardwareEvidence
    metricCatalogEvidence = $metricCatalogEvidence
    offlineFixtureMetricsEvidence = $offlineFixtureMetricsEvidence
    externalEngineCandidateEvidence = $externalEngineCandidateEvidence
    externalEngineBakeoffEvidence = $externalEngineBakeoffEvidence
    metricComparisonEvidence = $metricComparisonEvidence
    liveTraceComparisonEvidence = $liveTraceComparisonEvidence
    liveDiagnosticsHistoryEvidence = $liveDiagnosticsHistoryEvidence
    nativeSymbolAuditEvidence = $nativeSymbolAuditEvidence
    nativeRuntimeArtifactAuditEvidence = $nativeRuntimeArtifactAuditEvidence
    artifactFiles = @($artifactFiles.ToArray())
    artifactReferencedFiles = @($artifactReferencedFiles.ToArray())
    artifactScenarioCoverage = @($artifactScenarioCoverage.ToArray())
    artifactComparisonCoverage = @($artifactComparisonCoverage.ToArray())
    artifactMetricCoverage = @($artifactMetricCoverage.ToArray())
    errors = @($errors.ToArray())
    warnings = @($warnings.ToArray())
}

Write-JsonFile -Path $ReportPath -Value $report

if ($JsonOnly) {
    $report | ConvertTo-Json -Depth 32
}
else {
    if ($ok) {
        Write-Host "DSP modernization bundle validation passed: $bundlePath"
    }
    else {
        Write-Host "DSP modernization bundle validation failed: $bundlePath"
    }
    Write-Host "Report: $ReportPath"
    Write-Host "Errors: $($errors.Count), Warnings: $($warnings.Count)"
}

if (-not $ok) {
    exit 1
}
