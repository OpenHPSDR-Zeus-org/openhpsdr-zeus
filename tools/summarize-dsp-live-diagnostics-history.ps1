param(
    [string[]]$InputPath = @(),

    [string]$RootPath = "",

    [string]$BundleDir = "",

    [string]$ReportPath = "",

    [string]$MarkdownPath = "",

    [int]$Recent = 0,

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

function Get-NumericOrNull {
    param(
        $Object,
        [Parameter(Mandatory = $true)][string]$Name
    )

    return Get-NumericValue (Get-JsonValue $Object $Name)
}

function Get-StatValue {
    param(
        $Report,
        [Parameter(Mandatory = $true)][string]$Group,
        [Parameter(Mandatory = $true)][string]$Name
    )

    return Get-NumericOrNull (Get-JsonValue $Report $Group) $Name
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

function Resolve-OptionalFilePath {
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

function Get-FileSha256IfPresent {
    param(
        [string]$Path,
        [string]$BaseDirectory = ""
    )

    $resolvedPath = Resolve-OptionalFilePath -Path $Path -BaseDirectory $BaseDirectory
    if ([string]::IsNullOrWhiteSpace($resolvedPath) -or -not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
        return ""
    }

    return (Get-FileHash -LiteralPath $resolvedPath -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-DateSortKey {
    param($Value)

    if ($Value -is [DateTimeOffset]) {
        return $Value
    }

    if ($Value -is [DateTime]) {
        return [DateTimeOffset]$Value
    }

    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) {
        return [DateTimeOffset]::MinValue
    }

    $parsed = [DateTimeOffset]::MinValue
    if ([DateTimeOffset]::TryParse($text, [ref]$parsed)) {
        return $parsed
    }

    return [DateTimeOffset]::MinValue
}

function Get-TraceIdSortKey {
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

function ConvertTo-PortablePath {
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

function Get-RelativePathSegments {
    param(
        [string]$Root,
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Root) -or [string]::IsNullOrWhiteSpace($Path)) {
        return @()
    }

    $relative = ConvertTo-PortablePath -Root $Root -Path $Path
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

function ConvertTo-TraceIdSegment {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return ""
    }

    $segment = $Value.Trim().ToLowerInvariant() -replace "[^a-z0-9]+", "-"
    return $segment.Trim("-")
}

function Get-TraceIdentity {
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
        $candidateSortKey = Get-TraceIdSortKey $leaf
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
        $relativeSegments = @(Get-RelativePathSegments -Root $timestampPath -Path $parentPath)
        for ($i = 0; $i -lt $relativeSegments.Count; $i++) {
            $safeSegment = ConvertTo-TraceIdSegment ([string]$relativeSegments[$i])
            if ([string]::Equals($safeSegment, "live-diagnostics-traces", [StringComparison]::OrdinalIgnoreCase)) {
                if ($i + 1 -lt $relativeSegments.Count) {
                    $scenarioId = ConvertTo-TraceIdSegment ([string]$relativeSegments[$i + 1])
                }
                if ($i + 2 -lt $relativeSegments.Count) {
                    $comparisonId = ConvertTo-TraceIdSegment ([string]$relativeSegments[$i + 2])
                }
            }
        }

        foreach ($segment in $relativeSegments) {
            $safeSegment = ConvertTo-TraceIdSegment $segment
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
    $fallbackSegments = @(Get-RelativePathSegments -Root $fallbackRoot -Path $parentPath)
    for ($i = 0; $i -lt $fallbackSegments.Count; $i++) {
        $safeSegment = ConvertTo-TraceIdSegment ([string]$fallbackSegments[$i])
        if ([string]::Equals($safeSegment, "live-diagnostics-traces", [StringComparison]::OrdinalIgnoreCase)) {
            if ($i + 1 -lt $fallbackSegments.Count) {
                $scenarioId = ConvertTo-TraceIdSegment ([string]$fallbackSegments[$i + 1])
            }
            if ($i + 2 -lt $fallbackSegments.Count) {
                $comparisonId = ConvertTo-TraceIdSegment ([string]$fallbackSegments[$i + 2])
            }
        }
    }

    foreach ($segment in $fallbackSegments) {
        $safeSegment = ConvertTo-TraceIdSegment $segment
        if ([string]::IsNullOrWhiteSpace($safeSegment) -or $genericSegments -contains $safeSegment) {
            continue
        }

        $traceSegments.Add($safeSegment) | Out-Null
    }

    if ($traceSegments.Count -eq 0) {
        $fileSegment = ConvertTo-TraceIdSegment ([System.IO.Path]::GetFileNameWithoutExtension($resolvedPath))
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

function Add-WatchPath {
    param(
        [System.Collections.Generic.List[string]]$Target,
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return
    }

    $resolved = (Resolve-Path -LiteralPath $Path).Path
    if (Test-Path -LiteralPath $resolved -PathType Leaf) {
        $Target.Add($resolved) | Out-Null
        return
    }

    foreach ($file in @(Get-ChildItem -LiteralPath $resolved -Recurse -File -Filter "live-diagnostics-watch.json")) {
        $Target.Add($file.FullName) | Out-Null
    }
}

function Get-WatchReportPaths {
    param(
        [string[]]$Paths,
        [string]$Root
    )

    $items = New-Object System.Collections.Generic.List[string]
    if ($Paths.Count -gt 0) {
        foreach ($path in $Paths) {
            foreach ($item in @(([string]$path).Split(",", [System.StringSplitOptions]::RemoveEmptyEntries))) {
                Add-WatchPath -Target $items -Path $item.Trim()
            }
        }
    }
    else {
        $scanRoot = $Root
        if ([string]::IsNullOrWhiteSpace($scanRoot)) {
            $scanRoot = Join-Path (Get-RepoRoot) "tmp"
        }

        if (Test-Path -LiteralPath $scanRoot -PathType Container) {
            Add-WatchPath -Target $items -Path $scanRoot
        }
    }

    return @($items.ToArray() | Select-Object -Unique | Sort-Object)
}

function New-SafetySignal {
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

    $unit = Get-SafetySignalUnit -Name $Name -Value $Value -Threshold $Threshold
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

function Add-SignalIf {
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
        $Signals.Add((New-SafetySignal -SafetyClass $SafetyClass -Name $Name -Value $Value -Threshold $Threshold -Message $Message)) | Out-Null
    }
}

function Get-SafetyClassCounts {
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

function Get-SafetySignalUnit {
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
    if ($Name -match "(^|-)count($|-)|(^|-)blockers($|-)") {
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

function Get-SafetyRiskScore {
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

function Get-CandidateBlockingSafetyClassCounts {
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

function Add-ReadinessGapBucket {
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

function Get-ReadinessGapSummary {
    param($Signals)

    $buckets = @{}
    foreach ($signal in @($Signals)) {
        $safetyClass = [string](Get-JsonValue $signal "safetyClass")
        if ([string]::IsNullOrWhiteSpace($safetyClass)) {
            $safetyClass = "unknown"
        }

        $unit = [string](Get-JsonValue $signal "readinessGapUnit")
        if ([string]::IsNullOrWhiteSpace($unit)) {
            $unit = "value"
        }

        Add-ReadinessGapBucket -Buckets $buckets -SafetyClass $safetyClass -Unit $unit -Gap (Get-JsonValue $signal "readinessGap")
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

function Get-ReadinessGapSummarySignalCount {
    param($Summary)

    $total = 0
    foreach ($item in @($Summary)) {
        $count = Get-NumericValue (Get-JsonValue $item "signalCount")
        if ($null -ne $count) {
            $total += [int][Math]::Round($count)
        }
    }

    return $total
}

function Get-ReadinessGapSummaryNumericSignalCount {
    param($Summary)

    $total = 0
    foreach ($item in @($Summary)) {
        $count = Get-NumericValue (Get-JsonValue $item "numericSignalCount")
        if ($null -ne $count) {
            $total += [int][Math]::Round($count)
        }
    }

    return $total
}

function Get-ReadinessGapSummaryMax {
    param($Summary)

    $max = $null
    foreach ($item in @($Summary)) {
        $value = Get-NumericValue (Get-JsonValue $item "readinessGapMax")
        if ($null -ne $value -and ($null -eq $max -or $value -gt $max)) {
            $max = $value
        }
    }

    if ($null -eq $max) {
        return $null
    }

    return [Math]::Round($max, 3)
}

function Get-ReadinessTrendSignalKey {
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

function New-ReadinessTrendSignalMap {
    param($Signals)

    $map = @{}
    foreach ($signal in @($Signals)) {
        $key = Get-ReadinessTrendSignalKey $signal
        $map[$key] = $signal
    }

    return ,$map
}

function Add-ReadinessTrendClassBucket {
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

function Get-ReadinessTrendOverallStatus {
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

function New-ReadinessTrendClassSummary {
    param($Details)

    $buckets = @{}
    foreach ($detail in @($Details)) {
        Add-ReadinessTrendClassBucket -Buckets $buckets -Detail $detail
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
        $bucket["status"] = Get-ReadinessTrendOverallStatus `
            -ImprovedCount ([int]$bucket.improvedSignalCount) `
            -RegressedCount ([int]$bucket.regressedSignalCount) `
            -ClearedCount ([int]$bucket.resolvedSignalCount) `
            -NewCount ([int]$bucket.newSignalCount) `
            -UnchangedCount ([int]$bucket.unchangedSignalCount)
        $result.Add($bucket) | Out-Null
    }

    return @($result.ToArray())
}

function New-ReadinessTrend {
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
    $latestMap = New-ReadinessTrendSignalMap -Signals $latestSignals
    $previousMap = New-ReadinessTrendSignalMap -Signals $previousSignals
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
    $latestMax = Get-ReadinessGapSummaryMax -Summary $latestSummary
    $previousMax = if ($null -eq $Previous) { $null } else { Get-ReadinessGapSummaryMax -Summary $previousSummary }
    $maxDelta = if ($null -eq $latestMax -or $null -eq $previousMax) { $null } else { [Math]::Round($latestMax - $previousMax, 3) }
    $improvedCount = $narrowedCount + $resolvedCount
    $regressedCount = $widenedCount + $newCount
    $statusValue = if ($null -eq $Previous) {
        "no-previous-nr5-trace"
    }
    else {
        Get-ReadinessTrendOverallStatus -ImprovedCount $improvedCount -RegressedCount $regressedCount -ClearedCount $resolvedCount -NewCount $newCount -UnchangedCount $unchangedCount
    }
    $buckets = @(New-ReadinessTrendClassSummary -Details @($details.ToArray()))

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

function Get-TraceRecordById {
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

function Add-ReadinessTrendContext {
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

function Get-TraceStatus {
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

function New-TraceRecord {
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
    $weakInput = Get-NumericOrNull $weak "weakInputSampleCount"
    $weakRecovered = Get-NumericOrNull $weak "weakRecoveredSampleCount"
    $weakDropout = Get-NumericOrNull $weak "weakDropoutSampleCount"
    $hotMakeup = Get-NumericOrNull $weak "hotMakeupSampleCount"
    $weakRecoveryPct = Get-NumericOrNull $weak "weakRecoveryPct"
    if ($null -eq $weakRecoveryPct) {
        $weakRecoveryPct = Get-Percent $weakRecovered $weakInput
    }

    $okSampleCount = Get-NumericOrNull $Report "okSampleCount"
    $sampleCount = Get-NumericOrNull $Report "sampleCount"
    $failedSampleCount = Get-NumericOrNull $Report "failedSampleCount"
    $hardBlockerSampleCount = Get-NumericOrNull $Report "hardBlockerSampleCount"
    $runtimeEvidenceSampleCount = Get-NumericOrNull $Report "runtimeEvidenceSampleCount"
    $audioFreshPct = Get-Percent (Get-JsonValue $Report "audioFreshSampleCount") $runtimeEvidenceSampleCount
    $rxMetersFreshPct = Get-Percent (Get-JsonValue $Report "rxMetersFreshSampleCount") $runtimeEvidenceSampleCount

    $nr5SampleCount = Get-NumericOrNull $Report "nr5SampleCount"
    $readyForBenchmark = [bool](Get-JsonValue $Report "readyForBenchmarkTrace")
    $signals = New-Object System.Collections.Generic.List[object]

    Add-SignalIf -Signals $signals -Condition ($null -ne $failedSampleCount -and $failedSampleCount -gt 0) -SafetyClass "hard-gate" -Name "failed-samples" -Value $failedSampleCount -Threshold 0 -Message "Endpoint failures make the trace incomplete."
    Add-SignalIf -Signals $signals -Condition ($null -ne $hardBlockerSampleCount -and $hardBlockerSampleCount -gt 0) -SafetyClass "hard-gate" -Name "hard-blockers" -Value $hardBlockerSampleCount -Threshold 0 -Message "Hard blockers must be cleared before using this trace as evidence."
    Add-SignalIf -Signals $signals -Condition (-not $readyForBenchmark) -SafetyClass "hard-gate" -Name "not-ready" -Value $readyForBenchmark -Threshold $true -Message "Trace is not benchmark-ready."

    if ($null -ne $weakInput -and $weakInput -gt 0) {
        Add-SignalIf -Signals $signals -Condition ($null -ne $weakDropout -and $weakDropout -gt 0) -SafetyClass "weak-signal" -Name "weak-dropouts" -Value $weakDropout -Threshold 0 -Message "Weak-input samples dropped below input level."
        Add-SignalIf -Signals $signals -Condition ($null -ne $weakRecoveryPct -and $weakRecoveryPct -lt 95.0) -SafetyClass "weak-signal" -Name "weak-recovery-pct" -Value $weakRecoveryPct -Threshold 95.0 -Message "Weak-input recovery is below the live tuning target."
    }

    $nr5OutputMovement = Get-StatValue $Report "nr5OutputDbfs" "movement"
    $audioRmsMovement = Get-StatValue $Report "audioRmsDbfs" "movement"
    $makeupMovement = Get-StatValue $Report "nr5MakeupGainDb" "movement"
    $makeupMax = Get-StatValue $Report "nr5MakeupGainDb" "max"
    $recoveryMovement = Get-StatValue $Report "nr5RecoveryDrive" "movement"
    $audioPeakMax = Get-StatValue $Report "audioPeakDbfs" "max"
    $adcHeadroomMin = Get-StatValue $Report "adcHeadroomDb" "min"
    $monitorBacklogMax = Get-StatValue $Report "monitorBacklogSamples" "max"
    $latencyAverage = Get-StatValue $Report "latencyMs" "average"

    Add-SignalIf -Signals $signals -Condition ($null -ne $nr5OutputMovement -and $nr5OutputMovement -gt 6.0) -SafetyClass "pumping" -Name "nr5-output-movement-db" -Value $nr5OutputMovement -Threshold 6.0 -Message "NR5 output movement is high enough to need pumping review."
    Add-SignalIf -Signals $signals -Condition ($null -ne $audioRmsMovement -and $audioRmsMovement -gt 6.0) -SafetyClass "pumping" -Name "audio-rms-movement-db" -Value $audioRmsMovement -Threshold 6.0 -Message "Final-audio RMS movement is high enough to need listening review."
    Add-SignalIf -Signals $signals -Condition ($null -ne $makeupMovement -and $makeupMovement -gt 6.0) -SafetyClass "pumping" -Name "nr5-makeup-movement-db" -Value $makeupMovement -Threshold 6.0 -Message "NR5 makeup gain movement is high."
    Add-SignalIf -Signals $signals -Condition ($null -ne $recoveryMovement -and $recoveryMovement -gt 0.5) -SafetyClass "pumping" -Name "nr5-recovery-drive-movement" -Value $recoveryMovement -Threshold 0.5 -Message "Recovery-drive movement is high."
    Add-SignalIf -Signals $signals -Condition ($null -ne $makeupMax -and $makeupMax -gt 12.0) -SafetyClass "pumping" -Name "nr5-makeup-max-db" -Value $makeupMax -Threshold 12.0 -Message "NR5 makeup peak exceeded the hot-makeup threshold."
    Add-SignalIf -Signals $signals -Condition ($null -ne $hotMakeup -and $hotMakeup -gt 0) -SafetyClass "pumping" -Name "hot-makeup-samples" -Value $hotMakeup -Threshold 0 -Message "Hot-makeup samples appeared in the trace."
    Add-SignalIf -Signals $signals -Condition ($null -ne $audioPeakMax -and $audioPeakMax -gt -1.0) -SafetyClass "clipping" -Name "audio-peak-max-dbfs" -Value $audioPeakMax -Threshold -1.0 -Message "Audio peak is close to clipping."
    Add-SignalIf -Signals $signals -Condition ($null -ne $adcHeadroomMin -and $adcHeadroomMin -lt 6.0) -SafetyClass "front-end" -Name "adc-headroom-min-db" -Value $adcHeadroomMin -Threshold 6.0 -Message "ADC headroom is low."
    Add-SignalIf -Signals $signals -Condition ($null -ne $monitorBacklogMax -and $monitorBacklogMax -gt 0) -SafetyClass "audio-path" -Name "monitor-backlog-max-samples" -Value $monitorBacklogMax -Threshold 0 -Message "Monitor backlog invalidates live audio evidence."
    Add-SignalIf -Signals $signals -Condition ($null -ne $audioFreshPct -and $audioFreshPct -lt 100.0) -SafetyClass "freshness" -Name "audio-fresh-pct" -Value $audioFreshPct -Threshold 100.0 -Message "Final-audio diagnostics were not fresh for every runtime sample."
    Add-SignalIf -Signals $signals -Condition ($null -ne $rxMetersFreshPct -and $rxMetersFreshPct -lt 100.0) -SafetyClass "freshness" -Name "rx-meters-fresh-pct" -Value $rxMetersFreshPct -Threshold 100.0 -Message "RX meter diagnostics were not fresh for every runtime sample."

    $counts = @(Get-SafetyClassCounts -Signals @($signals.ToArray()))
    $readinessGapSummary = @(Get-ReadinessGapSummary -Signals @($signals.ToArray()))
    $hardGateCount = Get-SafetyClassCount -Counts $counts -SafetyClass "hard-gate"
    $weakSignalCount = Get-SafetyClassCount -Counts $counts -SafetyClass "weak-signal"
    $pumpingCount = Get-SafetyClassCount -Counts $counts -SafetyClass "pumping"
    $candidateBlockingCounts = @(Get-CandidateBlockingSafetyClassCounts -Counts $counts)
    $candidateBlockerCount = Get-TotalSafetyClassCount -Counts $candidateBlockingCounts
    $candidateComparisonReady = ($readyForBenchmark -and
        $candidateBlockerCount -eq 0 -and
        $null -ne $nr5SampleCount -and
        $nr5SampleCount -gt 0)
    $reviewStatus = Get-TraceStatus `
        -ReadyForBenchmark $readyForBenchmark `
        -HardGateCount $hardGateCount `
        -WeakSignalCount $weakSignalCount `
        -PumpingCount $pumpingCount

    $traceIdentity = Get-TraceIdentity -Path $Path -PortableRoot $PortableRoot
    $traceScenarioId = ConvertTo-TraceIdSegment ([string](Get-JsonValue $Report "scenarioId"))
    $traceComparisonId = ConvertTo-TraceIdSegment ([string](Get-JsonValue $Report "comparisonId"))
    if ([string]::IsNullOrWhiteSpace($traceScenarioId)) {
        $traceScenarioId = [string](Get-JsonValue $traceIdentity "scenarioId")
    }
    if ([string]::IsNullOrWhiteSpace($traceComparisonId)) {
        $traceComparisonId = [string](Get-JsonValue $traceIdentity "comparisonId")
    }
    $traceId = [string](Get-JsonValue $traceIdentity "traceId")
    $completedSortKey = Get-DateSortKey (Get-JsonValue $Report "completedUtc")
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
        path = ConvertTo-PortablePath -Root $PortableRoot -Path $Path
        summarySha256 = Get-FileSha256IfPresent -Path $Path
        jsonlPath = ConvertTo-PortablePath -Root $PortableRoot -Path $jsonlSourcePath
        jsonlSha256 = Get-FileSha256IfPresent -Path $jsonlSourcePath -BaseDirectory $sourceBaseDirectory
        endpoint = [string](Get-JsonValue $Report "endpoint")
        sourceMode = [string](Get-JsonValue $Report "sourceMode")
        startedUtc = Get-JsonValue $Report "startedUtc"
        completedUtc = Get-JsonValue $Report "completedUtc"
        generatedUtc = Get-JsonValue $Report "generatedUtc"
        sortKey = $sortKey
        sampleCount = if ($null -eq $sampleCount) { 0 } else { [int][Math]::Round($sampleCount) }
        okSampleCount = if ($null -eq $okSampleCount) { 0 } else { [int][Math]::Round($okSampleCount) }
        failedSampleCount = if ($null -eq $failedSampleCount) { 0 } else { [int][Math]::Round($failedSampleCount) }
        hardBlockerSampleCount = if ($null -eq $hardBlockerSampleCount) { 0 } else { [int][Math]::Round($hardBlockerSampleCount) }
        readyForBenchmarkTrace = $readyForBenchmark
        trendStatus = [string](Get-JsonValue $Report "trendStatus")
        nr5TuningReadyTrace = [bool](Get-JsonValue $Report "nr5TuningReadyTrace")
        nr5TuningTraceStatus = [string](Get-JsonValue $Report "nr5TuningTraceStatus")
        nr5SampleCount = if ($null -eq $nr5SampleCount) { 0 } else { [int][Math]::Round($nr5SampleCount) }
        nr5ProbabilityDiagnosticSampleCount = [int](Get-NumericValue (Get-JsonValue $Report "nr5ProbabilityDiagnosticSampleCount"))
        nr5AgcDiagnosticSampleCount = [int](Get-NumericValue (Get-JsonValue $Report "nr5AgcDiagnosticSampleCount"))
        weakInputSampleCount = if ($null -eq $weakInput) { 0 } else { [int][Math]::Round($weakInput) }
        weakRecoveredSampleCount = if ($null -eq $weakRecovered) { 0 } else { [int][Math]::Round($weakRecovered) }
        weakDropoutSampleCount = if ($null -eq $weakDropout) { 0 } else { [int][Math]::Round($weakDropout) }
        hotMakeupSampleCount = if ($null -eq $hotMakeup) { 0 } else { [int][Math]::Round($hotMakeup) }
        weakRecoveryPct = if ($null -eq $weakRecoveryPct) { $null } else { [Math]::Round([double]$weakRecoveryPct, 3) }
        weakStrongOutputGapDb = Get-NumericOrNull $weak "weakStrongOutputGapDb"
        nr5OutputMovementDb = $nr5OutputMovement
        audioRmsMovementDb = $audioRmsMovement
        nr5MakeupMovementDb = $makeupMovement
        nr5MakeupMaxDb = $makeupMax
        nr5RecoveryDriveMovement = $recoveryMovement
        nr5TextureFillAverage = Get-StatValue $Report "nr5TextureFill" "average"
        nr5SignalProbabilityAverage = Get-StatValue $Report "nr5SignalProbability" "average"
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
        readinessGapSignalCount = Get-ReadinessGapSummarySignalCount -Summary $readinessGapSummary
        readinessGapNumericSignalCount = Get-ReadinessGapSummaryNumericSignalCount -Summary $readinessGapSummary
        readinessGapMax = Get-ReadinessGapSummaryMax -Summary $readinessGapSummary
        safetyRiskScore = Get-SafetyRiskScore -Counts $counts
        candidateComparisonReady = $candidateComparisonReady
        promotable = $candidateComparisonReady
        promotionBlockerCount = $candidateBlockerCount
        promotionBlockerClasses = @($candidateBlockingCounts | ForEach-Object { [string](Get-JsonValue $_ "safetyClass") })
        promotionBlockerClassCounts = @($candidateBlockingCounts)
        reviewStatus = $reviewStatus
        recommendations = @(Get-JsonArray $Report "recommendations")
    }
}

function New-AggregateSafetyClassCounts {
    param($Records)

    $counts = @{}
    foreach ($record in @($Records)) {
        foreach ($item in @($record.safetyClassCounts)) {
            $safetyClass = [string](Get-JsonValue $item "safetyClass")
            $count = Get-NumericValue (Get-JsonValue $item "count")
            if ([string]::IsNullOrWhiteSpace($safetyClass) -or $null -eq $count) {
                continue
            }

            if (-not $counts.ContainsKey($safetyClass)) {
                $counts[$safetyClass] = 0
            }
            $counts[$safetyClass] = [int]$counts[$safetyClass] + [int][Math]::Round($count)
        }
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

function New-AggregateReadinessGaps {
    param($Records)

    $signals = New-Object System.Collections.Generic.List[object]
    foreach ($record in @($Records)) {
        foreach ($signal in @(Get-JsonArray $record "safetySignals")) {
            $signals.Add($signal) | Out-Null
        }
    }

    return @(Get-ReadinessGapSummary -Signals @($signals.ToArray()))
}

function Select-TraceForSummary {
    param(
        $Records,
        [Parameter(Mandatory = $true)][string]$Mode
    )

    $items = @($Records | Where-Object { [int](Get-NumericValue (Get-JsonValue $_ "nr5SampleCount")) -gt 0 })
    if ($items.Count -eq 0) {
        return $null
    }

    switch ($Mode) {
        "latest" {
            return @($items | Sort-Object `
                @{ Expression = { Get-JsonValue $_ "sortKey" }; Descending = $true },
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
                @{ Expression = { Get-SafetyClassCount -Counts (Get-JsonValue $_ "safetyClassCounts") -SafetyClass "pumping" }; Ascending = $true },
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

function Get-CompactTrace {
    param($Trace)

    if ($null -eq $Trace) {
        return $null
    }

    return [ordered]@{
        traceId = $Trace.traceId
        traceSequenceUtc = $Trace.traceSequenceUtc
        sortKeySource = $Trace.sortKeySource
        scenarioId = $Trace.scenarioId
        comparisonId = $Trace.comparisonId
        reviewStatus = $Trace.reviewStatus
        safetyRiskScore = $Trace.safetyRiskScore
        readyForBenchmarkTrace = $Trace.readyForBenchmarkTrace
        trendStatus = $Trace.trendStatus
        weakInputSampleCount = $Trace.weakInputSampleCount
        weakRecoveredSampleCount = $Trace.weakRecoveredSampleCount
        weakDropoutSampleCount = $Trace.weakDropoutSampleCount
        weakRecoveryPct = $Trace.weakRecoveryPct
        nr5OutputMovementDb = $Trace.nr5OutputMovementDb
        audioRmsMovementDb = $Trace.audioRmsMovementDb
        nr5MakeupMovementDb = $Trace.nr5MakeupMovementDb
        nr5MakeupMaxDb = $Trace.nr5MakeupMaxDb
        nr5RecoveryDriveMovement = $Trace.nr5RecoveryDriveMovement
        audioPeakMaxDbfs = $Trace.audioPeakMaxDbfs
        audioFreshPct = $Trace.audioFreshPct
        rxMetersFreshPct = $Trace.rxMetersFreshPct
        hardGateSignalCount = Get-SafetyClassCount -Counts $Trace.safetyClassCounts -SafetyClass "hard-gate"
        weakSignalSignalCount = Get-SafetyClassCount -Counts $Trace.safetyClassCounts -SafetyClass "weak-signal"
        pumpingSignalCount = Get-SafetyClassCount -Counts $Trace.safetyClassCounts -SafetyClass "pumping"
        clippingSignalCount = Get-SafetyClassCount -Counts $Trace.safetyClassCounts -SafetyClass "clipping"
        freshnessSignalCount = Get-SafetyClassCount -Counts $Trace.safetyClassCounts -SafetyClass "freshness"
        frontEndSignalCount = Get-SafetyClassCount -Counts $Trace.safetyClassCounts -SafetyClass "front-end"
        audioPathSignalCount = Get-SafetyClassCount -Counts $Trace.safetyClassCounts -SafetyClass "audio-path"
        candidateComparisonReady = $Trace.candidateComparisonReady
        promotable = $Trace.promotable
        promotionBlockerCount = $Trace.promotionBlockerCount
        promotionBlockerClasses = @($Trace.promotionBlockerClasses)
        promotionBlockerClassCounts = @($Trace.promotionBlockerClassCounts)
        safetyClassReadiness = @($Trace.readinessGapSummary)
        readinessGapSummary = @($Trace.readinessGapSummary)
        readinessGapSignalCount = $Trace.readinessGapSignalCount
        readinessGapNumericSignalCount = $Trace.readinessGapNumericSignalCount
        readinessGapMax = $Trace.readinessGapMax
        safetyClassCounts = @($Trace.safetyClassCounts)
        path = $Trace.path
    }
}

function Get-Delta {
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

function New-Recommendations {
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

    $latestPumping = Get-SafetyClassCount -Counts $Latest.safetyClassCounts -SafetyClass "pumping"
    $latestWeak = Get-SafetyClassCount -Counts $Latest.safetyClassCounts -SafetyClass "weak-signal"
    if ($latestPumping -gt 0 -and $latestWeak -gt 0) {
        $items.Add("Latest NR5 trace has both pumping and weak-signal safety signals; tune recovery/makeup coupling before raising global makeup or mask fill.") | Out-Null
    }
    elseif ($latestPumping -gt 0) {
        $items.Add("Latest NR5 trace is primarily pumping-limited; inspect output movement, makeup movement, and recovery-drive movement before changing weak-fill thresholds.") | Out-Null
    }
    elseif ($latestWeak -gt 0) {
        $items.Add("Latest NR5 trace is primarily weak-signal-limited; inspect top weak dropouts before increasing broad makeup.") | Out-Null
    }

    if ($null -ne $BestBalanced -and $BestBalanced.traceId -ne $Latest.traceId) {
        $items.Add("Best balanced trace is '$($BestBalanced.traceId)'; compare it against the latest trace before accepting the newest tuning direction.") | Out-Null
    }
    if ($null -ne $BestWeak -and $BestWeak.traceId -ne $Latest.traceId) {
        $items.Add("Best weak-signal trace is '$($BestWeak.traceId)'; preserve its recovery behavior when reducing pumping.") | Out-Null
    }
    if ($null -ne $LowestPumping -and $LowestPumping.traceId -ne $Latest.traceId) {
        $items.Add("Lowest-pumping trace is '$($LowestPumping.traceId)'; use it as a level-stability reference.") | Out-Null
    }

    if ($items.Count -eq 0) {
        $items.Add("No high-priority live-history safety signals were found; pair this history with fixture metrics before considering any behavior change.") | Out-Null
    }

    return @($items.ToArray())
}

function Get-ControlFamilyForSignal {
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

function Get-GuardrailForSafetyClass {
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

function Get-TuningRationaleForSafetyClass {
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

function Select-WorstSafetySignal {
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

function Select-WorstTrendDetail {
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

function Add-TuningAction {
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
        $Rationale = Get-TuningRationaleForSafetyClass -SafetyClass $SafetyClass -SignalName $SignalName
    }

    $Actions.Add([ordered]@{
        priority = $Actions.Count + 1
        actionId = $ActionId
        safetyClass = $SafetyClass
        signalName = $SignalName
        controlFamily = Get-ControlFamilyForSignal -SafetyClass $SafetyClass -SignalName $SignalName
        trendSource = $TrendSource
        latestReadinessGap = $LatestReadinessGap
        readinessGapUnit = $ReadinessGapUnit
        latestVsPreviousGapDelta = $PreviousDelta
        latestVsReferenceGapDelta = $ReferenceDelta
        referenceTraceId = $ReferenceTraceId
        referenceTraceRole = $ReferenceTraceRole
        rationale = $Rationale
        guardrail = Get-GuardrailForSafetyClass -SafetyClass $SafetyClass
    }) | Out-Null
}

function New-TuningActionPlan {
    param(
        $Latest,
        $PromotionDecision,
        $LatestVsPreviousTrend,
        $LatestVsReferenceTrend
    )

    $actions = New-Object System.Collections.Generic.List[object]
    if ($null -eq $Latest) {
        Add-TuningAction -Actions $actions -ActionId "capture-nr5-live-history" -SafetyClass "tooling" -SignalName "no-nr5-history" -Rationale "No NR5 live history exists; capture G2 NR5 and reference windows before tuning."
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
    $candidateReady = [bool](Get-JsonValue $PromotionDecision "candidatePromotionReady")
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
            $primaryClass = [string](Get-JsonValue (Select-WorstSafetySignal -Signals $latestSignals) "safetyClass")
            if ([string]::IsNullOrWhiteSpace($primaryClass)) {
                $primaryClass = "dsp-review"
            }
        }
    }

    if ($candidateReady) {
        Add-TuningAction `
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
        $signal = Select-WorstSafetySignal -Signals $latestSignals -SafetyClass $primaryClass
        if ($null -eq $signal) {
            $signal = Select-WorstSafetySignal -Signals $latestSignals
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
        Add-TuningAction `
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

    $referenceDetail = Select-WorstTrendDetail -Trend $LatestVsReferenceTrend
    if ($null -ne $referenceDetail) {
        Add-TuningAction `
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

    $previousDetail = Select-WorstTrendDetail -Trend $LatestVsPreviousTrend
    if ($null -ne $previousDetail) {
        Add-TuningAction `
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

function Get-ExperimentGatesForControlFamily {
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

function Add-LiveExperimentScenario {
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
        requiredComparisons = @("current-zeus", "nr5-spnr")
        acceptanceGates = @(Get-ExperimentGatesForControlFamily -ControlFamily $controlFamily -SafetyClass $safetyClass)
        operatorSetup = $OperatorSetup
    }) | Out-Null
}

function Add-LiveExperimentScenariosForAction {
    param(
        [System.Collections.Generic.List[object]]$Scenarios,
        $Action
    )

    $controlFamily = [string](Get-JsonValue $Action "controlFamily")
    switch ($controlFamily) {
        "nr5-output-level-stability" {
            Add-LiveExperimentScenario -Scenarios $Scenarios -Action $Action -ScenarioId "ssb-like-speech-dynamic" -Purpose "Stress dynamic voice and AGC-driven NR5 output movement." -OperatorSetup "Use a live SSB voice segment with obvious level swings or QSB."
            Add-LiveExperimentScenario -Scenarios $Scenarios -Action $Action -ScenarioId "adjacent-strong-signal" -Purpose "Check strong-neighbor energy without rescue or makeup pumping." -OperatorSetup "Tune near a strong adjacent signal while preserving the wanted signal."
            Add-LiveExperimentScenario -Scenarios $Scenarios -Action $Action -ScenarioId "noise-only-gating" -Purpose "Verify level rescue and makeup stay closed without wanted signal." -OperatorSetup "Use a clear/noise-only slice with squelch open if needed for audio evidence."
        }
        "nr5-weak-signal-recovery" {
            Add-LiveExperimentScenario -Scenarios $Scenarios -Action $Action -ScenarioId "weak-cw-carrier" -Purpose "Measure faint carrier recovery without dropout." -OperatorSetup "Use the weakest stable carrier available on G2."
            Add-LiveExperimentScenario -Scenarios $Scenarios -Action $Action -ScenarioId "weak-ssb-speech" -Purpose "Measure faint speech preservation under NR5 rescue." -OperatorSetup "Use weak SSB speech at the edge of readability."
            Add-LiveExperimentScenario -Scenarios $Scenarios -Action $Action -ScenarioId "fading-weak-signal" -Purpose "Check recovery during QSB and near-threshold fades." -OperatorSetup "Prefer a weak signal with natural fading or flutter."
        }
        "nr5-recovery-makeup-bounds" {
            Add-LiveExperimentScenario -Scenarios $Scenarios -Action $Action -ScenarioId "ssb-like-speech-dynamic" -Purpose "Bound recovery and makeup motion during voice level swings." -OperatorSetup "Use a dynamic SSB voice segment."
            Add-LiveExperimentScenario -Scenarios $Scenarios -Action $Action -ScenarioId "weak-ssb-speech" -Purpose "Confirm makeup bounds do not bury weak speech." -OperatorSetup "Use weak speech where recovery matters."
            Add-LiveExperimentScenario -Scenarios $Scenarios -Action $Action -ScenarioId "noise-only-gating" -Purpose "Confirm makeup does not open on noise-only audio." -OperatorSetup "Use a no-signal slice after candidate tuning."
        }
        "nr5-mask-fill-texture" {
            Add-LiveExperimentScenario -Scenarios $Scenarios -Action $Action -ScenarioId "ssb-like-speech" -Purpose "Listen and measure speech texture while mask fill changes." -OperatorSetup "Use normal SSB speech with operator notes."
            Add-LiveExperimentScenario -Scenarios $Scenarios -Action $Action -ScenarioId "weak-ssb-speech" -Purpose "Check weak speech intelligibility with mask fill active." -OperatorSetup "Use weak speech that previously needed NR5 rescue."
            Add-LiveExperimentScenario -Scenarios $Scenarios -Action $Action -ScenarioId "noise-only-gating" -Purpose "Check mask fill does not create noise-only artifacts." -OperatorSetup "Use no wanted signal and capture operator notes."
        }
        "audio-headroom" {
            Add-LiveExperimentScenario -Scenarios $Scenarios -Action $Action -ScenarioId "strong-signal-headroom" -Purpose "Verify peak headroom on strong live audio." -OperatorSetup "Use the strongest clean SSB voice available without front-end overload."
            Add-LiveExperimentScenario -Scenarios $Scenarios -Action $Action -ScenarioId "ssb-like-speech-dynamic" -Purpose "Check RMS/peak motion on dynamic voice." -OperatorSetup "Use voice with level swings."
        }
        "capture-readiness" {
            Add-LiveExperimentScenario -Scenarios $Scenarios -Action $Action -ScenarioId "diagnostic-freshness-preflight" -Purpose "Re-establish complete live diagnostics before tuning." -OperatorSetup "Run against the active G2 backend before any candidate capture."
            Add-LiveExperimentScenario -Scenarios $Scenarios -Action $Action -ScenarioId "g2-hardware-preflight" -Purpose "Confirm G2 hardware and endpoint readiness." -OperatorSetup "Capture hardware diagnostics in the same session."
        }
        default {
            Add-LiveExperimentScenario -Scenarios $Scenarios -Action $Action -ScenarioId "weak-cw-carrier" -Purpose "Cover weak-signal preservation." -OperatorSetup "Use the weakest stable signal available."
            Add-LiveExperimentScenario -Scenarios $Scenarios -Action $Action -ScenarioId "ssb-like-speech" -Purpose "Cover speech quality and pumping risk." -OperatorSetup "Use live SSB speech with operator notes."
            Add-LiveExperimentScenario -Scenarios $Scenarios -Action $Action -ScenarioId "noise-only-gating" -Purpose "Cover noise-only false-open behavior." -OperatorSetup "Use no wanted signal."
        }
    }
}

function New-LiveExperimentPlan {
    param($ActionPlan)

    $scenarios = New-Object System.Collections.Generic.List[object]
    $actions = @(Get-JsonArray $ActionPlan "actions")
    foreach ($action in @($actions | Select-Object -First 3)) {
        Add-LiveExperimentScenariosForAction -Scenarios $scenarios -Action $action
    }

    if ($scenarios.Count -eq 0) {
        Add-LiveExperimentScenario `
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
        recommendedComparisons = @("current-zeus", "nr5-spnr")
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
        requiredEvidence = @(
            "current-Zeus baseline live diagnostics trace index",
            "NR5/SPNR opt-in candidate live diagnostics trace index",
            "matrix comparison report with no regressions",
            "operator notes for speech texture and pumping"
        )
    }
}

function New-LiveExperimentCoverage {
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
        $scenarioId = ConvertTo-TraceIdSegment ([string](Get-JsonValue $scenario "scenarioId"))
        if ([string]::IsNullOrWhiteSpace($scenarioId)) {
            continue
        }

        $requiredComparisons = @(Get-JsonArray $scenario "requiredComparisons" | ForEach-Object { ConvertTo-TraceIdSegment ([string]$_) } | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | Select-Object -Unique)
        if ($requiredComparisons.Count -eq 0) {
            $requiredComparisons = @(Get-JsonArray $Plan "recommendedComparisons" | ForEach-Object { ConvertTo-TraceIdSegment ([string]$_) } | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | Select-Object -Unique)
        }

        $coveredComparisons = New-Object System.Collections.Generic.List[string]
        $scenarioTraceIds = New-Object System.Collections.Generic.List[string]
        $readyTraceCount = 0
        $nr5TraceCount = 0

        foreach ($record in @($Records)) {
            $recordScenarioId = ConvertTo-TraceIdSegment ([string](Get-JsonValue $record "scenarioId"))
            if (-not [string]::Equals($recordScenarioId, $scenarioId, [StringComparison]::OrdinalIgnoreCase)) {
                continue
            }

            $comparisonId = ConvertTo-TraceIdSegment ([string](Get-JsonValue $record "comparisonId"))
            if ([string]::IsNullOrWhiteSpace($comparisonId)) {
                continue
            }

            $scenarioTraceIds.Add([string](Get-JsonValue $record "traceId")) | Out-Null
            if ([int](Get-NumericValueOrDefault (Get-JsonValue $record "nr5SampleCount")) -gt 0) {
                $nr5TraceCount++
            }
            if ([bool](Get-JsonValue $record "readyForBenchmarkTrace")) {
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

function New-PromotionDecision {
    param(
        $Latest,
        $BestBalanced,
        $BestWeak,
        $LowestPumping
    )

    $blockers = New-Object System.Collections.Generic.List[object]
    if ($null -eq $Latest) {
        $signal = New-SafetySignal -SafetyClass "tooling" -Name "no-nr5-history" -Value 0 -Threshold 1 -Message "No NR5 live diagnostics summaries were found."
        $blockers.Add([ordered]@{
            code = "no-nr5-history"
            safetyClass = "tooling"
            traceId = ""
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

        $blockerGapSummary = @(Get-ReadinessGapSummary -Signals @($blockers.ToArray()))
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
            blockerClassCounts = @(Get-SafetyClassCounts -Signals @($blockers.ToArray()))
            safetyClassReadiness = @($blockerGapSummary)
            blockerGapSummary = @($blockerGapSummary)
            blockers = @($blockers.ToArray())
            nextAction = "Capture G2 NR5 live diagnostics before selecting a candidate comparison window."
        }
    }

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
            traceId = $Latest.traceId
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

    $hardGateCount = Get-SafetyClassCount -Counts $Latest.safetyClassCounts -SafetyClass "hard-gate"
    $weakSignalCount = Get-SafetyClassCount -Counts $Latest.safetyClassCounts -SafetyClass "weak-signal"
    $pumpingCount = Get-SafetyClassCount -Counts $Latest.safetyClassCounts -SafetyClass "pumping"
    $clippingCount = Get-SafetyClassCount -Counts $Latest.safetyClassCounts -SafetyClass "clipping"
    $freshnessCount = Get-SafetyClassCount -Counts $Latest.safetyClassCounts -SafetyClass "freshness"
    $frontEndCount = Get-SafetyClassCount -Counts $Latest.safetyClassCounts -SafetyClass "front-end"
    $audioPathCount = Get-SafetyClassCount -Counts $Latest.safetyClassCounts -SafetyClass "audio-path"

    $status = "ready-for-candidate-comparison"
    $nextAction = "Use the latest trace as the next candidate comparison window, then compare against current-Zeus and Thetis-parity evidence before any behavior change."
    if (-not [bool](Get-JsonValue $Latest "readyForBenchmarkTrace") -or $hardGateCount -gt 0) {
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
    $recommendedRole = if ($null -eq $recommendedTrace) {
        ""
    }
    elseif ($recommendedTrace.traceId -eq $Latest.traceId) {
        "latest"
    }
    elseif ($null -ne $BestBalanced -and $recommendedTrace.traceId -eq $BestBalanced.traceId) {
        "best-balanced"
    }
    elseif ($null -ne $BestWeak -and $recommendedTrace.traceId -eq $BestWeak.traceId) {
        "best-weak-signal"
    }
    elseif ($null -ne $LowestPumping -and $recommendedTrace.traceId -eq $LowestPumping.traceId) {
        "lowest-pumping"
    }
    else {
        "reference"
    }

    $referenceTrace = if ($null -ne $BestBalanced) { $BestBalanced } elseif ($null -ne $BestWeak) { $BestWeak } elseif ($null -ne $LowestPumping) { $LowestPumping } else { $Latest }
    $latestIsBestBalanced = ($null -ne $BestBalanced -and $BestBalanced.traceId -eq $Latest.traceId)
    $blockerClassCounts = @(Get-SafetyClassCounts -Signals @($blockers.ToArray()))
    $blockerClasses = @($blockerClassCounts | ForEach-Object { [string](Get-JsonValue $_ "safetyClass") })
    $blockerGapSummary = @(Get-ReadinessGapSummary -Signals @($blockers.ToArray()))

    return [ordered]@{
        promotionScope = "candidate-comparison-only"
        status = $status
        promotable = $candidatePromotionReady
        candidatePromotionReady = $candidatePromotionReady
        candidateComparisonReady = $candidatePromotionReady
        defaultBehaviorChangeReady = $false
        latestTraceId = $Latest.traceId
        recommendedTraceId = if ($null -eq $recommendedTrace) { "" } else { $recommendedTrace.traceId }
        recommendedTraceRole = $recommendedRole
        referenceTraceId = if ($null -eq $referenceTrace) { "" } else { $referenceTrace.traceId }
        referenceTraceRole = if ($null -ne $BestBalanced) { "best-balanced" } elseif ($null -ne $BestWeak) { "best-weak-signal" } elseif ($null -ne $LowestPumping) { "lowest-pumping" } else { "latest" }
        latestIsBestBalanced = $latestIsBestBalanced
        latestSafetyRiskScore = [int](Get-NumericValue (Get-JsonValue $Latest "safetyRiskScore"))
        bestBalancedSafetyRiskScore = if ($null -eq $BestBalanced) { $null } else { [int](Get-NumericValue (Get-JsonValue $BestBalanced "safetyRiskScore")) }
        riskScoreDeltaVsBestBalanced = Get-Delta $Latest $BestBalanced "safetyRiskScore"
        blockerCount = $blockers.Count
        blockerClasses = @($blockerClasses)
        blockerClassCounts = @($blockerClassCounts)
        safetyClassReadiness = @($blockerGapSummary)
        blockerGapSummary = @($blockerGapSummary)
        blockers = @($blockers.ToArray())
        nextAction = $nextAction
    }
}

function Build-MarkdownReport {
    param($Report)

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# DSP Live Diagnostics History") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add("- Trace count: $($Report.traceCount)") | Out-Null
    $lines.Add("- NR5 trace count: $($Report.nr5TraceCount)") | Out-Null
    $lines.Add("- Ready trace count: $($Report.readyTraceCount)") | Out-Null
    $lines.Add("- Ready NR5 trace count: $($Report.readyNr5TraceCount)") | Out-Null
    $lines.Add("- Candidate-ready NR5 trace count: $($Report.candidateReadyNr5TraceCount)") | Out-Null
    $lines.Add("- Latest trace: $($Report.latestTrace.traceId)") | Out-Null
    $lines.Add("- Best balanced trace: $($Report.bestBalancedTrace.traceId)") | Out-Null
    $lines.Add("- Best weak-signal trace: $($Report.bestWeakSignalTrace.traceId)") | Out-Null
    $lines.Add("- Lowest-pumping trace: $($Report.lowestPumpingTrace.traceId)") | Out-Null
    $lines.Add("") | Out-Null

    $lines.Add("## Promotion Decision") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add("- Scope: $($Report.promotionDecision.promotionScope)") | Out-Null
    $lines.Add("- Status: $($Report.promotionDecision.status)") | Out-Null
    $lines.Add("- Candidate comparison ready: $($Report.promotionDecision.candidatePromotionReady)") | Out-Null
    $lines.Add("- Default behavior change ready: $($Report.promotionDecision.defaultBehaviorChangeReady)") | Out-Null
    $lines.Add("- Recommended trace: $($Report.promotionDecision.recommendedTraceId) ($($Report.promotionDecision.recommendedTraceRole))") | Out-Null
    $lines.Add("- Blocker count: $($Report.promotionDecision.blockerCount)") | Out-Null
    $lines.Add("- Next action: $($Report.promotionDecision.nextAction)") | Out-Null
    $lines.Add("") | Out-Null

    if (@($Report.aggregateSafetyClassCounts).Count -gt 0) {
        $lines.Add("## Safety Rollup") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("| Safety class | Signals |") | Out-Null
        $lines.Add("|---|---:|") | Out-Null
        foreach ($item in @($Report.aggregateSafetyClassCounts)) {
            $lines.Add("| $($item.safetyClass) | $($item.count) |") | Out-Null
        }
        $lines.Add("") | Out-Null
    }

    if (@($Report.aggregateReadinessGaps).Count -gt 0) {
        $lines.Add("## Readiness Gap Rollup") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("| Safety class | Unit | Signals | Numeric signals | Non-numeric signals | Gap total | Gap max |") | Out-Null
        $lines.Add("|---|---|---:|---:|---:|---:|---:|") | Out-Null
        foreach ($item in @($Report.aggregateReadinessGaps)) {
            $lines.Add("| $($item.safetyClass) | $($item.readinessGapUnit) | $($item.signalCount) | $($item.numericSignalCount) | $($item.nonNumericSignalCount) | $($item.readinessGapTotal) | $($item.readinessGapMax) |") | Out-Null
        }
        $lines.Add("") | Out-Null
    }

    $trend = $Report.latestVsPreviousNr5ReadinessGapTrend
    if ($null -eq $trend) {
        $trend = $Report.latestVsPreviousReadinessTrend
    }
    if ($null -ne $trend) {
        $lines.Add("## Latest Readiness Trend") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("- Status: $($trend.status)") | Out-Null
        $lines.Add("- Previous trace: $($trend.previousTraceId)") | Out-Null
        $lines.Add("- Latest trace: $($trend.latestTraceId)") | Out-Null
        $lines.Add("- Signal count delta: $($trend.readinessGapSignalCountDelta)") | Out-Null
        $lines.Add("- Gap max delta: $($trend.readinessGapMaxDelta)") | Out-Null
        $lines.Add("- Improved: $($trend.improvedSignalCount), regressed: $($trend.regressedSignalCount), cleared: $($trend.clearedSignalCount), new: $($trend.newSignalCount), unchanged: $($trend.unchangedSignalCount)") | Out-Null
        $lines.Add("") | Out-Null

        if (@($trend.safetyClassUnitTrend).Count -gt 0) {
            $lines.Add("| Safety class | Unit | Status | Signals | Improved | Regressed | Cleared | New | Delta total |") | Out-Null
            $lines.Add("|---|---|---|---:|---:|---:|---:|---:|---:|") | Out-Null
            foreach ($item in @($trend.safetyClassUnitTrend)) {
                $lines.Add("| $($item.safetyClass) | $($item.readinessGapUnit) | $($item.status) | $($item.signalCount) | $($item.improvedSignalCount) | $($item.regressedSignalCount) | $($item.clearedSignalCount) | $($item.newSignalCount) | $($item.readinessGapDeltaTotal) |") | Out-Null
            }
            $lines.Add("") | Out-Null
        }

        if (@($trend.details).Count -gt 0) {
            $lines.Add("| Signal | Class | Unit | Status | Previous gap | Latest gap | Delta |") | Out-Null
            $lines.Add("|---|---|---|---|---:|---:|---:|") | Out-Null
            foreach ($item in @($trend.details | Sort-Object `
                @{ Expression = { if ($_.trendStatus -eq "new-gap" -or $_.trendStatus -eq "gap-widened") { 0 } elseif ($_.trendStatus -eq "resolved-gap" -or $_.trendStatus -eq "gap-narrowed") { 1 } else { 2 } }; Ascending = $true },
                @{ Expression = { [Math]::Abs([double]$_.readinessGapDelta) }; Ascending = $false })) {
                $lines.Add("| $($item.name) | $($item.safetyClass) | $($item.readinessGapUnit) | $($item.trendStatus) | $($item.previousReadinessGap) | $($item.latestReadinessGap) | $($item.readinessGapDelta) |") | Out-Null
            }
            $lines.Add("") | Out-Null
        }
    }

    $referenceTrend = $Report.latestVsReferenceNr5ReadinessGapTrend
    if ($null -ne $referenceTrend) {
        $lines.Add("## Reference Readiness Trend") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("- Status: $($referenceTrend.status)") | Out-Null
        $lines.Add("- Reference trace: $($referenceTrend.referenceTraceId) ($($referenceTrend.referenceTraceRole))") | Out-Null
        $lines.Add("- Latest trace: $($referenceTrend.latestTraceId)") | Out-Null
        $lines.Add("- Latest is reference: $($referenceTrend.latestIsReference)") | Out-Null
        $lines.Add("- Reference is recommended: $($referenceTrend.referenceIsRecommended)") | Out-Null
        $lines.Add("- Signal count delta: $($referenceTrend.readinessGapSignalCountDelta)") | Out-Null
        $lines.Add("- Gap max delta: $($referenceTrend.readinessGapMaxDelta)") | Out-Null
        $lines.Add("- Improved: $($referenceTrend.improvedSignalCount), regressed: $($referenceTrend.regressedSignalCount), cleared: $($referenceTrend.clearedSignalCount), new: $($referenceTrend.newSignalCount), unchanged: $($referenceTrend.unchangedSignalCount)") | Out-Null
        $lines.Add("") | Out-Null

        if (@($referenceTrend.readinessGapTrendBuckets).Count -gt 0) {
            $lines.Add("| Safety class | Unit | Status | Signals | Improved | Regressed | Cleared | New | Delta total |") | Out-Null
            $lines.Add("|---|---|---|---:|---:|---:|---:|---:|---:|") | Out-Null
            foreach ($item in @($referenceTrend.readinessGapTrendBuckets)) {
                $lines.Add("| $($item.safetyClass) | $($item.readinessGapUnit) | $($item.status) | $($item.signalCount) | $($item.improvedSignalCount) | $($item.regressedSignalCount) | $($item.clearedSignalCount) | $($item.newSignalCount) | $($item.readinessGapDeltaTotal) |") | Out-Null
            }
            $lines.Add("") | Out-Null
        }

        if (@($referenceTrend.readinessGapTrendDetails).Count -gt 0) {
            $lines.Add("| Signal | Class | Unit | Status | Reference gap | Latest gap | Delta |") | Out-Null
            $lines.Add("|---|---|---|---|---:|---:|---:|") | Out-Null
            foreach ($item in @($referenceTrend.readinessGapTrendDetails | Sort-Object `
                @{ Expression = { if ($_.trendStatus -eq "new-gap" -or $_.trendStatus -eq "gap-widened") { 0 } elseif ($_.trendStatus -eq "resolved-gap" -or $_.trendStatus -eq "gap-narrowed") { 1 } else { 2 } }; Ascending = $true },
                @{ Expression = { [Math]::Abs([double]$_.readinessGapDelta) }; Ascending = $false })) {
                $lines.Add("| $($item.name) | $($item.safetyClass) | $($item.readinessGapUnit) | $($item.trendStatus) | $($item.previousReadinessGap) | $($item.latestReadinessGap) | $($item.readinessGapDelta) |") | Out-Null
            }
            $lines.Add("") | Out-Null
        }
    }

    $actionPlan = $Report.latestTuningActionPlan
    if ($null -ne $actionPlan) {
        $lines.Add("## Tuning Action Plan") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("- Status: $($actionPlan.status)") | Out-Null
        $lines.Add("- Direction: $($actionPlan.directionStatus)") | Out-Null
        $lines.Add("- Primary safety class: $($actionPlan.primarySafetyClass)") | Out-Null
        $lines.Add("- Primary signal: $($actionPlan.primarySignalName)") | Out-Null
        $lines.Add("- Top control family: $($actionPlan.topControlFamily)") | Out-Null
        $lines.Add("") | Out-Null

        if (@($actionPlan.actions).Count -gt 0) {
            $lines.Add("| Priority | Action | Class | Signal | Control family | Trend source | Latest gap | Unit |") | Out-Null
            $lines.Add("|---:|---|---|---|---|---|---:|---|") | Out-Null
            foreach ($item in @($actionPlan.actions)) {
                $lines.Add("| $($item.priority) | $($item.actionId) | $($item.safetyClass) | $($item.signalName) | $($item.controlFamily) | $($item.trendSource) | $($item.latestReadinessGap) | $($item.readinessGapUnit) |") | Out-Null
            }
            $lines.Add("") | Out-Null
        }
    }

    $experimentPlan = $Report.latestLiveExperimentPlan
    if ($null -ne $experimentPlan) {
        $lines.Add("## Live Experiment Plan") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("- Status: $($experimentPlan.status)") | Out-Null
        $lines.Add("- Direction: $($experimentPlan.sourceDirectionStatus)") | Out-Null
        $lines.Add("- Primary control family: $($experimentPlan.primaryControlFamily)") | Out-Null
        $lines.Add("- Recommended comparisons: $((@($experimentPlan.recommendedComparisons) -join ', '))") | Out-Null
        $lines.Add("- Recommended samples: $($experimentPlan.recommendedSampleCount) at $($experimentPlan.recommendedIntervalMs) ms") | Out-Null
        $lines.Add("") | Out-Null

        if (@($experimentPlan.scenarios).Count -gt 0) {
            $lines.Add("| Priority | Scenario | Purpose | Control family | Class | Samples | Interval ms |") | Out-Null
            $lines.Add("|---:|---|---|---|---|---:|---:|") | Out-Null
            foreach ($item in @($experimentPlan.scenarios)) {
                $lines.Add("| $($item.priority) | $($item.scenarioId) | $($item.purpose) | $($item.controlFamily) | $($item.safetyClass) | $($item.sampleCount) | $($item.intervalMs) |") | Out-Null
            }
            $lines.Add("") | Out-Null
        }

        if ($null -ne $experimentPlan.matrixCommandTemplates) {
            $lines.Add("- Baseline matrix: ``$($experimentPlan.matrixCommandTemplates.baseline)``") | Out-Null
            $lines.Add("- Candidate matrix: ``$($experimentPlan.matrixCommandTemplates.candidate)``") | Out-Null
            $lines.Add("- Compare matrix: ``$($experimentPlan.matrixCommandTemplates.compare)``") | Out-Null
            $lines.Add("") | Out-Null
        }
    }

    $experimentCoverage = $Report.latestLiveExperimentCoverage
    if ($null -ne $experimentCoverage) {
        $lines.Add("## Live Experiment Coverage") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("- Status: $($experimentCoverage.status)") | Out-Null
        $lines.Add("- Covered scenarios: $($experimentCoverage.coveredScenarioCount) / $($experimentCoverage.scenarioCount)") | Out-Null
        $lines.Add("- Covered comparisons: $($experimentCoverage.coveredComparisonCount) / $($experimentCoverage.requiredComparisonCount)") | Out-Null
        $lines.Add("") | Out-Null

        if (@($experimentCoverage.scenarioCoverage).Count -gt 0) {
            $lines.Add("| Scenario | Status | Required | Covered | Missing | Ready traces |") | Out-Null
            $lines.Add("|---|---|---|---|---|---:|") | Out-Null
            foreach ($item in @($experimentCoverage.scenarioCoverage)) {
                $lines.Add("| $($item.scenarioId) | $($item.status) | $((@($item.requiredComparisons) -join ', ')) | $((@($item.coveredComparisons) -join ', ')) | $((@($item.missingComparisons) -join ', ')) | $($item.readyTraceCount) |") | Out-Null
            }
            $lines.Add("") | Out-Null
        }
    }

    $lines.Add("## Recommendations") | Out-Null
    $lines.Add("") | Out-Null
    foreach ($item in @($Report.recommendations)) {
        $lines.Add("- $item") | Out-Null
    }
    $lines.Add("") | Out-Null

    $lines.Add("## Trace Summary") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add("| Trace | Status | Risk | Ready | Weak recovered | Dropouts | Output move dB | Makeup move dB | Recovery move |") | Out-Null
    $lines.Add("|---|---|---:|---:|---:|---:|---:|---:|---:|") | Out-Null
    foreach ($trace in @($Report.traces)) {
        $lines.Add("| $($trace.traceId) | $($trace.reviewStatus) | $($trace.safetyRiskScore) | $($trace.readyForBenchmarkTrace) | $($trace.weakRecoveryPct) | $($trace.weakDropoutSampleCount) | $($trace.nr5OutputMovementDb) | $($trace.nr5MakeupMovementDb) | $($trace.nr5RecoveryDriveMovement) |") | Out-Null
    }

    return @($lines.ToArray())
}

$repoRoot = Get-RepoRoot
$bundlePath = ""
if (-not [string]::IsNullOrWhiteSpace($BundleDir)) {
    $bundlePath = (Resolve-Path -LiteralPath $BundleDir).Path
}

$scanRoot = $RootPath
if ([string]::IsNullOrWhiteSpace($scanRoot)) {
    if (-not [string]::IsNullOrWhiteSpace($bundlePath)) {
        $scanRoot = $bundlePath
    }
    else {
        $scanRoot = Join-Path $repoRoot "tmp"
    }
}

$watchPaths = @(Get-WatchReportPaths -Paths $InputPath -Root $scanRoot)
$portableRoot = if (-not [string]::IsNullOrWhiteSpace($bundlePath)) { $bundlePath } else { $repoRoot }
$records = New-Object System.Collections.Generic.List[object]
foreach ($path in $watchPaths) {
    $report = Read-JsonFile $path
    $records.Add((New-TraceRecord -Path $path -Report $report -PortableRoot $portableRoot)) | Out-Null
}

$orderedRecords = @($records.ToArray() | Sort-Object `
    @{ Expression = { Get-JsonValue $_ "sortKey" } },
    @{ Expression = { [string](Get-JsonValue $_ "traceId") } },
    @{ Expression = { [string](Get-JsonValue $_ "path") } })
if ($Recent -gt 0 -and $orderedRecords.Count -gt $Recent) {
    $orderedRecords = @($orderedRecords | Select-Object -Last $Recent)
}

$nr5Records = @($orderedRecords | Where-Object { [int](Get-NumericValue (Get-JsonValue $_ "nr5SampleCount")) -gt 0 })
$readyRecords = @($orderedRecords | Where-Object { [bool](Get-JsonValue $_ "readyForBenchmarkTrace") })
$readyNr5Records = @($nr5Records | Where-Object { [bool](Get-JsonValue $_ "readyForBenchmarkTrace") })
$candidateReadyNr5Records = @($nr5Records | Where-Object { [bool](Get-JsonValue $_ "candidateComparisonReady") })
$latestTrace = Select-TraceForSummary -Records $orderedRecords -Mode "latest"
$bestBalancedTrace = Select-TraceForSummary -Records $orderedRecords -Mode "balanced"
$bestWeakSignalTrace = Select-TraceForSummary -Records $orderedRecords -Mode "best-weak"
$lowestPumpingTrace = Select-TraceForSummary -Records $orderedRecords -Mode "lowest-pumping"
$previousTrace = $null
if ($nr5Records.Count -gt 1) {
    $previousTrace = @($nr5Records | Sort-Object `
        @{ Expression = { Get-JsonValue $_ "sortKey" } },
        @{ Expression = { [string](Get-JsonValue $_ "traceId") } },
        @{ Expression = { [string](Get-JsonValue $_ "path") } } | Select-Object -Last 2)[0]
}

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    if (-not [string]::IsNullOrWhiteSpace($bundlePath)) {
        $ReportPath = Join-Path $bundlePath "artifacts\live-diagnostics-history.json"
    }
    else {
        $ReportPath = Join-Path $repoRoot "tmp\dsp-live-diagnostics-history.json"
    }
}

if (-not $NoMarkdown -and [string]::IsNullOrWhiteSpace($MarkdownPath)) {
    $reportDir = Split-Path -Parent $ReportPath
    $reportName = [System.IO.Path]::GetFileNameWithoutExtension($ReportPath)
    $MarkdownPath = Join-Path $reportDir "$reportName.md"
}

$aggregateSafetyClassCounts = New-AggregateSafetyClassCounts -Records $orderedRecords
$aggregateReadinessGaps = New-AggregateReadinessGaps -Records $orderedRecords
$latestVsPreviousReadinessTrend = New-ReadinessTrend -Latest $latestTrace -Previous $previousTrace
$statusCountMap = @{}
foreach ($record in @($orderedRecords)) {
    $status = [string](Get-JsonValue $record "reviewStatus")
    if ([string]::IsNullOrWhiteSpace($status)) {
        $status = "unknown"
    }

    if (-not $statusCountMap.ContainsKey($status)) {
        $statusCountMap[$status] = 0
    }
    $statusCountMap[$status] = [int]$statusCountMap[$status] + 1
}

$statusCounts = New-Object System.Collections.Generic.List[object]
foreach ($status in @($statusCountMap.Keys | Sort-Object)) {
    $statusCounts.Add([ordered]@{
        status = [string]$status
        count = [int]$statusCountMap[$status]
    }) | Out-Null
}

$promotionDecision = New-PromotionDecision -Latest $latestTrace -BestBalanced $bestBalancedTrace -BestWeak $bestWeakSignalTrace -LowestPumping $lowestPumpingTrace
$promotionReferenceTraceId = [string](Get-JsonValue $promotionDecision "referenceTraceId")
$promotionReferenceTraceRole = [string](Get-JsonValue $promotionDecision "referenceTraceRole")
$promotionRecommendedTraceId = [string](Get-JsonValue $promotionDecision "recommendedTraceId")
$latestTraceId = [string](Get-JsonValue $latestTrace "traceId")
$referenceTrace = Get-TraceRecordById -Records $orderedRecords -TraceId $promotionReferenceTraceId
$latestVsReferenceReadinessTrend = Add-ReadinessTrendContext `
    -Trend (New-ReadinessTrend -Latest $latestTrace -Previous $referenceTrace) `
    -ComparisonScope "latest-vs-reference" `
    -LatestTraceId $latestTraceId `
    -ReferenceTraceId $promotionReferenceTraceId `
    -ReferenceTraceRole $promotionReferenceTraceRole `
    -RecommendedTraceId $promotionRecommendedTraceId
$latestVsPreviousReadinessTrend = Add-ReadinessTrendContext `
    -Trend $latestVsPreviousReadinessTrend `
    -ComparisonScope "latest-vs-previous" `
    -LatestTraceId $latestTraceId `
    -ReferenceTraceId ([string](Get-JsonValue $previousTrace "traceId")) `
    -ReferenceTraceRole "previous-nr5" `
    -RecommendedTraceId $promotionRecommendedTraceId
$latestTuningActionPlan = New-TuningActionPlan `
    -Latest $latestTrace `
    -PromotionDecision $promotionDecision `
    -LatestVsPreviousTrend $latestVsPreviousReadinessTrend `
    -LatestVsReferenceTrend $latestVsReferenceReadinessTrend
$latestLiveExperimentPlan = New-LiveExperimentPlan -ActionPlan $latestTuningActionPlan
$latestLiveExperimentCoverage = New-LiveExperimentCoverage -Plan $latestLiveExperimentPlan -Records $orderedRecords

$report = [ordered]@{
    schemaVersion = 9
    tool = "summarize-dsp-live-diagnostics-history"
    generatedUtc = [DateTimeOffset]::UtcNow.ToString("o")
    scanRoot = ConvertTo-PortablePath -Root $portableRoot -Path $scanRoot
    bundleRelativePaths = (-not [string]::IsNullOrWhiteSpace($bundlePath))
    reportPath = ConvertTo-PortablePath -Root $portableRoot -Path $ReportPath
    markdownPath = if ($NoMarkdown) { $null } else { ConvertTo-PortablePath -Root $portableRoot -Path $MarkdownPath }
    inputPathCount = $InputPath.Count
    traceCount = $orderedRecords.Count
    nr5TraceCount = $nr5Records.Count
    readyTraceCount = $readyRecords.Count
    readyNr5TraceCount = $readyNr5Records.Count
    candidateReadyNr5TraceCount = $candidateReadyNr5Records.Count
    aggregateSafetyClassCounts = @($aggregateSafetyClassCounts)
    aggregateSafetyClassReadiness = @($aggregateReadinessGaps)
    aggregateReadinessGaps = @($aggregateReadinessGaps)
    reviewStatusCounts = @($statusCounts.ToArray())
    thresholds = [ordered]@{
        weakRecoveryPctMinimum = 95.0
        nr5OutputMovementDbMaximum = 6.0
        audioRmsMovementDbMaximum = 6.0
        nr5MakeupMovementDbMaximum = 6.0
        nr5RecoveryDriveMovementMaximum = 0.5
        nr5MakeupMaxDbMaximum = 12.0
        audioPeakMaxDbfsMaximum = -1.0
        adcHeadroomMinDbMinimum = 6.0
    }
    latestTrace = Get-CompactTrace $latestTrace
    previousNr5Trace = Get-CompactTrace $previousTrace
    latestVsPreviousNr5ReadinessGapTrend = $latestVsPreviousReadinessTrend
    latestVsPreviousReadinessTrend = $latestVsPreviousReadinessTrend
    latestVsReferenceNr5ReadinessGapTrend = $latestVsReferenceReadinessTrend
    latestTuningActionPlan = $latestTuningActionPlan
    latestLiveExperimentPlan = $latestLiveExperimentPlan
    latestLiveExperimentCoverage = $latestLiveExperimentCoverage
    latestVsPreviousNr5Delta = [ordered]@{
        weakRecoveryPct = Get-Delta $latestTrace $previousTrace "weakRecoveryPct"
        weakDropoutSampleCount = Get-Delta $latestTrace $previousTrace "weakDropoutSampleCount"
        nr5OutputMovementDb = Get-Delta $latestTrace $previousTrace "nr5OutputMovementDb"
        nr5MakeupMovementDb = Get-Delta $latestTrace $previousTrace "nr5MakeupMovementDb"
        nr5RecoveryDriveMovement = Get-Delta $latestTrace $previousTrace "nr5RecoveryDriveMovement"
        safetyRiskScore = Get-Delta $latestTrace $previousTrace "safetyRiskScore"
    }
    bestBalancedTrace = Get-CompactTrace $bestBalancedTrace
    bestWeakSignalTrace = Get-CompactTrace $bestWeakSignalTrace
    lowestPumpingTrace = Get-CompactTrace $lowestPumpingTrace
    latestNr5Decision = $promotionDecision
    promotionDecision = $promotionDecision
    recommendations = @(New-Recommendations -Latest $latestTrace -BestBalanced $bestBalancedTrace -BestWeak $bestWeakSignalTrace -LowestPumping $lowestPumpingTrace)
    traces = @($orderedRecords | ForEach-Object {
        $copy = [ordered]@{}
        foreach ($property in @($_.Keys)) {
            if ($property -ne "sortKey") {
                $copy[$property] = $_[$property]
            }
        }
        $copy
    })
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
    Write-Host "DSP live diagnostics history written: $ReportPath"
    if (-not $NoMarkdown) {
        Write-Host "Markdown: $MarkdownPath"
    }
    Write-Host "Traces: $($report.traceCount), NR5 traces: $($report.nr5TraceCount), Ready traces: $($report.readyTraceCount)"
}
