param(
    [string[]]$ValidationReportPath = @(),

    [string[]]$HardwareTarget = @(),

    [string[]]$ScenarioId = @(),

    [string[]]$ComparisonId = @(),

    [string]$BundleDir = "",

    [string]$ReportPath = "",

    [string]$MarkdownPath = "",

    [switch]$FailOnNotReady,

    [switch]$NoMarkdown,

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
        return [bool]$Value
    }

    $text = ([string]$Value).Trim()
    if ([string]::IsNullOrWhiteSpace($text)) {
        return $false
    }

    return @("1", "true", "yes", "y", "ready", "ok") -contains $text.ToLowerInvariant()
}

function Get-NumericValueOrDefault {
    param(
        $Value,
        [int]$Default = 0
    )

    if ($null -eq $Value) {
        return $Default
    }

    $parsed = 0
    if ([int]::TryParse([string]$Value, [ref]$parsed)) {
        return $parsed
    }

    return $Default
}

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

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

function Get-BundlePath {
    param(
        [string]$BundlePath,
        [Parameter(Mandatory = $true)][string]$Path
    )

    if ([System.IO.Path]::IsPathRooted($Path) -or [string]::IsNullOrWhiteSpace($BundlePath)) {
        return $Path
    }

    return Join-Path $BundlePath $Path
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
        if (-not $rootFull.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
            $rootFull = $rootFull + [System.IO.Path]::DirectorySeparatorChar
        }

        try {
            $relative = [System.IO.Path]::GetRelativePath($rootFull, $pathFull)
            if (-not $relative.StartsWith("..")) {
                return ($relative -replace "\\", "/")
            }
        }
        catch {
            $uriRoot = [Uri]$rootFull
            $uriPath = [Uri]$pathFull
            $relativeUri = $uriRoot.MakeRelativeUri($uriPath)
            $relative = [Uri]::UnescapeDataString($relativeUri.ToString())
            if (-not $relative.StartsWith("..")) {
                return ($relative -replace "\\", "/")
            }
        }
    }
    catch {
        return $Path
    }

    return $Path
}

function ConvertTo-HardwareId {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return ""
    }

    return ($Value.Trim().ToLowerInvariant() -replace "[^a-z0-9]+", "")
}

function Test-G2HardwareTarget {
    param([string]$Value)

    return (ConvertTo-HardwareId $Value) -eq "g2"
}

function ConvertTo-ComparisonId {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return ""
    }

    return ($Value.Trim().ToLowerInvariant() -replace "[^a-z0-9]+", "-").Trim("-")
}

function Add-UniqueString {
    param(
        [System.Collections.Generic.List[string]]$List,
        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return
    }

    $trimmed = $Value.Trim()
    foreach ($item in @($List.ToArray())) {
        if ([string]::Equals($item, $trimmed, [StringComparison]::OrdinalIgnoreCase)) {
            return
        }
    }

    $List.Add($trimmed) | Out-Null
}

function Expand-DelimitedArgumentValues {
    param([string[]]$Values)

    $items = New-Object System.Collections.Generic.List[string]
    foreach ($value in @($Values)) {
        foreach ($part in (([string]$value) -split "[,;]")) {
            if (-not [string]::IsNullOrWhiteSpace($part)) {
                $items.Add($part.Trim()) | Out-Null
            }
        }
    }

    return @($items.ToArray())
}

function Add-ScenarioIdsFromReport {
    param(
        $Report,
        [System.Collections.Generic.List[string]]$ScenarioIds
    )

    foreach ($value in @(Get-JsonArray $Report "crossRadioValidationScenarioIds")) {
        Add-UniqueString $ScenarioIds ([string]$value)
    }
    foreach ($value in @(Get-JsonArray $Report "benchmarkPlanScenarioIds")) {
        Add-UniqueString $ScenarioIds ([string]$value)
    }
    foreach ($value in @(Get-JsonArray $Report "offlineFixtureMetricsScenarioIds")) {
        Add-UniqueString $ScenarioIds ([string]$value)
    }
    foreach ($value in @(Get-JsonArray $Report "txFixtureSafetyScenarioIds")) {
        Add-UniqueString $ScenarioIds ([string]$value)
    }
    foreach ($value in @(Get-JsonArray $Report "scenarioIds")) {
        Add-UniqueString $ScenarioIds ([string]$value)
    }
    foreach ($record in @(Get-JsonArray $Report "artifactScenarioCoverage")) {
        Add-UniqueString $ScenarioIds ([string](Get-JsonValue $record "scenarioId"))
    }
}

function Add-ComparisonIdsFromReport {
    param(
        $Report,
        [System.Collections.Generic.List[string]]$ComparisonIds
    )

    foreach ($value in @(Get-JsonArray $Report "crossRadioValidationComparisonIds")) {
        Add-UniqueString $ComparisonIds (ConvertTo-ComparisonId ([string]$value))
    }
    foreach ($value in @(Get-JsonArray $Report "offlineFixtureMetricsComparisonIds")) {
        Add-UniqueString $ComparisonIds (ConvertTo-ComparisonId ([string]$value))
    }
    foreach ($value in @(Get-JsonArray $Report "txFixtureSafetyComparisonIds")) {
        Add-UniqueString $ComparisonIds (ConvertTo-ComparisonId ([string]$value))
    }
    foreach ($value in @(Get-JsonArray $Report "comparisonIds")) {
        Add-UniqueString $ComparisonIds (ConvertTo-ComparisonId ([string]$value))
    }
    foreach ($record in @(Get-JsonArray $Report "artifactComparisonCoverage")) {
        Add-UniqueString $ComparisonIds (ConvertTo-ComparisonId ([string](Get-JsonValue $record "comparisonId")))
    }
}

function Get-MissingStrings {
    param(
        [string[]]$Required,
        [string[]]$Actual
    )

    $missing = New-Object System.Collections.Generic.List[string]
    foreach ($requiredValue in @($Required)) {
        $required = [string]$requiredValue
        if ([string]::IsNullOrWhiteSpace($required)) {
            continue
        }

        $found = $false
        foreach ($actualValue in @($Actual)) {
            if ([string]::Equals([string]$actualValue, $required, [StringComparison]::OrdinalIgnoreCase)) {
                $found = $true
                break
            }
        }

        if (-not $found) {
            $missing.Add($required) | Out-Null
        }
    }

    return @($missing.ToArray())
}

function Get-ReportHardwareTarget {
    param($Report)

    foreach ($field in @("crossRadioValidationNonG2TargetIds", "crossRadioValidationTargetIds")) {
        foreach ($value in @(Get-JsonArray $Report $field)) {
            if (-not [string]::IsNullOrWhiteSpace([string]$value)) {
                return [string]$value
            }
        }
    }

    foreach ($field in @("captureHardwareTarget", "hardwareTarget", "liveAcceptanceCycleCaptureHardwareTarget", "liveAcceptanceCycleHardwareTarget")) {
        $value = [string](Get-JsonValue $Report $field)
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            return $value
        }
    }

    return ""
}

function New-Blocker {
    param(
        [Parameter(Mandatory = $true)][string]$Code,
        [Parameter(Mandatory = $true)][string]$Message
    )

    return [ordered]@{
        code = $Code
        message = $Message
    }
}

function Format-MarkdownCell {
    param($Value)

    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) {
        return ""
    }

    return (($text -replace "\r?\n", " ") -replace "\|", "\\|")
}

function Build-MarkdownReport {
    param($Report)

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# DSP Cross-Radio Validation Report") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add("- Ready for review: $(Get-JsonValue $Report "readyForReview")") | Out-Null
    $lines.Add("- Evidence status: $(Get-JsonValue $Report "evidenceStatus")") | Out-Null
    $lines.Add("- Non-G2 targets: $((@(Get-JsonArray $Report "nonG2HardwareTargets") | ForEach-Object { [string]$_ }) -join ', ')") | Out-Null
    $lines.Add("- Scenarios: $((@(Get-JsonArray $Report "scenarioIds") | ForEach-Object { [string]$_ }) -join ', ')") | Out-Null
    $lines.Add("- Comparisons: $((@(Get-JsonArray $Report "comparisonIds") | ForEach-Object { [string]$_ }) -join ', ')") | Out-Null
    $lines.Add("- Source reports: $(Get-JsonValue $Report "sourceReportCount") total / $(Get-JsonValue $Report "nonG2SourceReportCount") non-G2 / $(Get-JsonValue $Report "readyNonG2SourceReportCount") ready") | Out-Null
    $lines.Add("- Source readiness: metric $(Get-JsonValue $Report "sourceMetricComparisonReadyCount") / live $(Get-JsonValue $Report "sourceLiveTraceComparisonReadyCount") / Thetis live $(Get-JsonValue $Report "sourceThetisLiveTraceComparisonReadyCount")") | Out-Null
    $lines.Add("- Default behavior change approved: $(Get-JsonValue $Report "defaultBehaviorChangeApproved")") | Out-Null
    $lines.Add("- Default behavior change ready: $(Get-JsonValue $Report "defaultBehaviorChangeReady")") | Out-Null
    $lines.Add("") | Out-Null

    $sourceReports = @(Get-JsonArray $Report "sourceReports")
    if ($sourceReports.Count -gt 0) {
        $lines.Add("## Source Reports") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("| Path | OK | Errors | Warnings | Hardware | Non-G2 | Metric | Live | Thetis Live | Ready | Status |") | Out-Null
        $lines.Add("|---|---:|---:|---:|---|---:|---:|---:|---:|---:|---|") | Out-Null
        foreach ($source in $sourceReports) {
            $lines.Add("| $(Format-MarkdownCell (Get-JsonValue $source "path")) | $(Get-JsonValue $source "validationOk") | $(Get-JsonValue $source "errorCount") | $(Get-JsonValue $source "warningCount") | $(Format-MarkdownCell (Get-JsonValue $source "hardwareTarget")) | $(Get-JsonValue $source "targetIsNonG2") | $(Get-JsonValue $source "metricComparisonReady") | $(Get-JsonValue $source "liveTraceComparisonReady") | $(Get-JsonValue $source "liveTraceThetisComparisonReady") | $(Get-JsonValue $source "readyForCrossRadio") | $(Format-MarkdownCell (Get-JsonValue $source "hardwareEvidenceStatus")) |") | Out-Null
        }
        $lines.Add("") | Out-Null
    }

    $blockers = @(Get-JsonArray $Report "blockers")
    if ($blockers.Count -gt 0) {
        $lines.Add("## Blockers") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("| Code | Message |") | Out-Null
        $lines.Add("|---|---|") | Out-Null
        foreach ($blocker in $blockers) {
            $lines.Add("| $(Format-MarkdownCell (Get-JsonValue $blocker "code")) | $(Format-MarkdownCell (Get-JsonValue $blocker "message")) |") | Out-Null
        }
        $lines.Add("") | Out-Null
    }

    $recommendations = @(Get-JsonArray $Report "recommendations")
    if ($recommendations.Count -gt 0) {
        $lines.Add("## Recommendations") | Out-Null
        $lines.Add("") | Out-Null
        foreach ($recommendation in $recommendations) {
            $lines.Add("- $recommendation") | Out-Null
        }
        $lines.Add("") | Out-Null
    }

    return $lines -join [Environment]::NewLine
}

$bundlePath = ""
if (-not [string]::IsNullOrWhiteSpace($BundleDir)) {
    $bundlePath = (Resolve-Path -LiteralPath $BundleDir).Path
}

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = if (-not [string]::IsNullOrWhiteSpace($bundlePath)) {
        "artifacts/cross-radio-validation-report.json"
    }
    else {
        "cross-radio-validation-report.json"
    }
}

$resolvedReportPath = Get-BundlePath -BundlePath $bundlePath -Path $ReportPath
if ([string]::IsNullOrWhiteSpace($MarkdownPath) -and -not $NoMarkdown) {
    $MarkdownPath = [System.IO.Path]::ChangeExtension($ReportPath, ".md")
}
$resolvedMarkdownPath = if ($NoMarkdown) { "" } else { Get-BundlePath -BundlePath $bundlePath -Path $MarkdownPath }

$hardwareTargets = New-Object System.Collections.Generic.List[string]
$scenarioIds = New-Object System.Collections.Generic.List[string]
$comparisonIds = New-Object System.Collections.Generic.List[string]
$sourceBackedScenarioIds = New-Object System.Collections.Generic.List[string]
$sourceBackedComparisonIds = New-Object System.Collections.Generic.List[string]
$sourceReports = New-Object System.Collections.Generic.List[object]
$blockers = New-Object System.Collections.Generic.List[object]

$requiredSourceScenarioIds = @(
    "weak-cw-carrier",
    "ssb-like-speech",
    "fading-carrier",
    "impulse-noise",
    "strong-adjacent",
    "noise-only-gating",
    "agc-level-step",
    "rx-audio-leveler-passband",
    "squelch-transition",
    "tx-two-tone",
    "tx-voice-like",
    "tx-puresignal-safe-bypass"
)
$requiredSourceComparisonIds = @(
    "off-baseline",
    "thetis-parity",
    "current-zeus",
    "candidate-under-test"
)

foreach ($target in @(Expand-DelimitedArgumentValues $HardwareTarget)) {
    Add-UniqueString $hardwareTargets ([string]$target)
}
foreach ($scenario in @(Expand-DelimitedArgumentValues $ScenarioId)) {
    Add-UniqueString $scenarioIds ([string]$scenario)
}
foreach ($comparison in @(Expand-DelimitedArgumentValues $ComparisonId)) {
    Add-UniqueString $comparisonIds (ConvertTo-ComparisonId ([string]$comparison))
}

foreach ($path in @($ValidationReportPath)) {
    if ([string]::IsNullOrWhiteSpace($path)) {
        continue
    }

    $resolvedPath = Get-BundlePath -BundlePath $bundlePath -Path $path
    if (-not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
        throw "Validation report file is missing: $path"
    }

    $validation = Read-JsonFile $resolvedPath
    $validationOk = Test-Truthy (Get-JsonValue $validation "ok")
    $errorCount = Get-NumericValueOrDefault (Get-JsonValue $validation "errorCount")
    $warningCount = Get-NumericValueOrDefault (Get-JsonValue $validation "warningCount")
    $target = Get-ReportHardwareTarget $validation
    $targetIsNonG2 = (-not [string]::IsNullOrWhiteSpace($target)) -and -not (Test-G2HardwareTarget $target)
    $hardwareStatus = [string](Get-JsonValue $validation "hardwareEvidenceStatus")
    if ([string]::IsNullOrWhiteSpace($hardwareStatus)) {
        $hardwareStatus = [string](Get-JsonValue $validation "liveAcceptanceCycleHardwareEvidenceStatus")
    }
    $hardwareEvidenceReady = [string]::Equals($hardwareStatus, "cross-radio-hardware-evidence-ready", [StringComparison]::OrdinalIgnoreCase)
    $metricComparisonReady = Test-Truthy (Get-JsonValue $validation "metricComparisonReady")
    $liveTraceComparisonReady = Test-Truthy (Get-JsonValue $validation "liveTraceComparisonReady")
    $liveTraceThetisComparisonReady = Test-Truthy (Get-JsonValue $validation "liveTraceThetisComparisonReady")
    $sourceScenarioIds = New-Object System.Collections.Generic.List[string]
    $sourceComparisonIds = New-Object System.Collections.Generic.List[string]
    Add-ScenarioIdsFromReport -Report $validation -ScenarioIds $sourceScenarioIds
    Add-ComparisonIdsFromReport -Report $validation -ComparisonIds $sourceComparisonIds
    $missingSourceScenarioIds = @(Get-MissingStrings -Required $requiredSourceScenarioIds -Actual @($sourceScenarioIds.ToArray()))
    $missingSourceComparisonIds = @(Get-MissingStrings -Required $requiredSourceComparisonIds -Actual @($sourceComparisonIds.ToArray()))
    $sourceReadyForCrossRadio = $validationOk -and
    $errorCount -eq 0 -and
    $targetIsNonG2 -and
    $hardwareEvidenceReady -and
    $metricComparisonReady -and
    $liveTraceComparisonReady -and
    $liveTraceThetisComparisonReady -and
    $missingSourceScenarioIds.Count -eq 0 -and
    $missingSourceComparisonIds.Count -eq 0

    Add-UniqueString $hardwareTargets $target
    foreach ($sourceScenarioId in @($sourceScenarioIds.ToArray())) {
        Add-UniqueString $scenarioIds ([string]$sourceScenarioId)
    }
    foreach ($sourceComparisonId in @($sourceComparisonIds.ToArray())) {
        Add-UniqueString $comparisonIds (ConvertTo-ComparisonId ([string]$sourceComparisonId))
    }
    if ($validationOk -and $errorCount -eq 0 -and $targetIsNonG2 -and $hardwareEvidenceReady -and $metricComparisonReady -and $liveTraceComparisonReady -and $liveTraceThetisComparisonReady) {
        foreach ($sourceScenarioId in @($sourceScenarioIds.ToArray())) {
            Add-UniqueString $sourceBackedScenarioIds ([string]$sourceScenarioId)
        }
        foreach ($sourceComparisonId in @($sourceComparisonIds.ToArray())) {
            Add-UniqueString $sourceBackedComparisonIds (ConvertTo-ComparisonId ([string]$sourceComparisonId))
        }
    }

    if (-not $validationOk -or $errorCount -gt 0) {
        $blockers.Add((New-Blocker `
                    -Code "source-validation-not-ok" `
                    -Message "Source validation report '$path' is not clean: ok=$validationOk, errors=$errorCount.")) | Out-Null
    }

    $sourceReports.Add([ordered]@{
            path = ConvertTo-PortablePath -Root $bundlePath -Path (Resolve-Path -LiteralPath $resolvedPath).Path
            sha256 = Get-FileSha256 $resolvedPath
            validationOk = $validationOk
            errorCount = $errorCount
            warningCount = $warningCount
            hardwareTarget = $target
            targetIsNonG2 = $targetIsNonG2
            hardwareEvidenceStatus = $hardwareStatus
            hardwareEvidenceReady = $hardwareEvidenceReady
            metricComparisonReady = $metricComparisonReady
            liveTraceComparisonReady = $liveTraceComparisonReady
            liveTraceThetisComparisonReady = $liveTraceThetisComparisonReady
            scenarioIds = @($sourceScenarioIds.ToArray())
            comparisonIds = @($sourceComparisonIds.ToArray())
            missingRequiredScenarioIds = @($missingSourceScenarioIds)
            missingRequiredComparisonIds = @($missingSourceComparisonIds)
            readyForCrossRadio = $sourceReadyForCrossRadio
        }) | Out-Null
}

$nonG2HardwareTargets = @($hardwareTargets.ToArray() | Where-Object {
        -not [string]::IsNullOrWhiteSpace([string]$_) -and
        -not (Test-G2HardwareTarget ([string]$_))
    })
$sourceReportRecords = @($sourceReports.ToArray())
$nonG2SourceReports = @($sourceReportRecords | Where-Object { Test-Truthy (Get-JsonValue $_ "targetIsNonG2") })
$readyNonG2SourceReports = @($nonG2SourceReports | Where-Object { Test-Truthy (Get-JsonValue $_ "readyForCrossRadio") })
$sourceProblemReports = @($sourceReportRecords | Where-Object {
        -not (Test-Truthy (Get-JsonValue $_ "validationOk")) -or
        (Get-NumericValueOrDefault (Get-JsonValue $_ "errorCount")) -gt 0
    })
$sourceWarningReports = @($sourceReportRecords | Where-Object {
        (Get-NumericValueOrDefault (Get-JsonValue $_ "warningCount")) -gt 0
    })
$sourceMetricReadyReports = @($sourceReportRecords | Where-Object { Test-Truthy (Get-JsonValue $_ "metricComparisonReady") })
$sourceLiveTraceReadyReports = @($sourceReportRecords | Where-Object { Test-Truthy (Get-JsonValue $_ "liveTraceComparisonReady") })
$sourceThetisLiveTraceReadyReports = @($sourceReportRecords | Where-Object { Test-Truthy (Get-JsonValue $_ "liveTraceThetisComparisonReady") })
$missingRequiredSourceScenarioIds = @(Get-MissingStrings -Required $requiredSourceScenarioIds -Actual @($sourceBackedScenarioIds.ToArray()))
$missingRequiredSourceComparisonIds = @(Get-MissingStrings -Required $requiredSourceComparisonIds -Actual @($sourceBackedComparisonIds.ToArray()))

if ($hardwareTargets.Count -eq 0) {
    $blockers.Add((New-Blocker -Code "hardware-targets-missing" -Message "Provide at least one hardware target or source validation report.")) | Out-Null
}
elseif ($nonG2HardwareTargets.Count -eq 0) {
    $blockers.Add((New-Blocker -Code "non-g2-target-missing" -Message "Cross-radio validation requires at least one non-G2 hardware target.")) | Out-Null
}

if ($scenarioIds.Count -eq 0) {
    $blockers.Add((New-Blocker -Code "scenario-coverage-missing" -Message "Provide scenarioIds covered by the non-G2 validation pass.")) | Out-Null
}

if ($comparisonIds.Count -eq 0) {
    $blockers.Add((New-Blocker -Code "comparison-coverage-missing" -Message "Provide comparisonIds covered by the non-G2 validation pass.")) | Out-Null
}

if ($sourceReportRecords.Count -eq 0) {
    $blockers.Add((New-Blocker -Code "source-validation-report-missing" -Message "Cross-radio validation must be backed by at least one strict validation report from a non-G2 radio; manual target/scenario/comparison declarations are preflight only.")) | Out-Null
}
elseif ($nonG2SourceReports.Count -eq 0) {
    $blockers.Add((New-Blocker -Code "non-g2-source-validation-report-missing" -Message "At least one source validation report must come from a non-G2 hardware target.")) | Out-Null
}

foreach ($source in $nonG2SourceReports) {
    $sourcePath = [string](Get-JsonValue $source "path")
    if (-not (Test-Truthy (Get-JsonValue $source "validationOk")) -or
        (Get-NumericValueOrDefault (Get-JsonValue $source "errorCount")) -gt 0) {
        $blockers.Add((New-Blocker -Code "source-validation-not-clean" -Message "Non-G2 source validation report '$sourcePath' must be clean before cross-radio review.")) | Out-Null
    }
    if (-not (Test-Truthy (Get-JsonValue $source "metricComparisonReady"))) {
        $blockers.Add((New-Blocker -Code "source-metric-comparison-not-ready" -Message "Non-G2 source validation report '$sourcePath' must have metricComparisonReady=true.")) | Out-Null
    }
    if (-not (Test-Truthy (Get-JsonValue $source "hardwareEvidenceReady"))) {
        $sourceHardwareStatus = [string](Get-JsonValue $source "hardwareEvidenceStatus")
        $blockers.Add((New-Blocker -Code "source-hardware-evidence-not-ready" -Message "Non-G2 source validation report '$sourcePath' must have hardwareEvidenceStatus='cross-radio-hardware-evidence-ready' before cross-radio review; actual='$sourceHardwareStatus'.")) | Out-Null
    }
    if (-not (Test-Truthy (Get-JsonValue $source "liveTraceComparisonReady"))) {
        $blockers.Add((New-Blocker -Code "source-live-trace-comparison-not-ready" -Message "Non-G2 source validation report '$sourcePath' must have liveTraceComparisonReady=true.")) | Out-Null
    }
    if (-not (Test-Truthy (Get-JsonValue $source "liveTraceThetisComparisonReady"))) {
        $blockers.Add((New-Blocker -Code "source-thetis-live-trace-comparison-not-ready" -Message "Non-G2 source validation report '$sourcePath' must have liveTraceThetisComparisonReady=true.")) | Out-Null
    }
    $sourceMissingScenarios = @(Get-JsonArray $source "missingRequiredScenarioIds" | ForEach-Object { [string]$_ })
    if ($sourceMissingScenarios.Count -gt 0) {
        $blockers.Add((New-Blocker -Code "source-scenario-coverage-incomplete" -Message "Non-G2 source validation report '$sourcePath' is missing required scenario coverage: $($sourceMissingScenarios -join ', ').")) | Out-Null
    }
    $sourceMissingComparisons = @(Get-JsonArray $source "missingRequiredComparisonIds" | ForEach-Object { [string]$_ })
    if ($sourceMissingComparisons.Count -gt 0) {
        $blockers.Add((New-Blocker -Code "source-comparison-coverage-incomplete" -Message "Non-G2 source validation report '$sourcePath' is missing required comparison coverage: $($sourceMissingComparisons -join ', ').")) | Out-Null
    }
}

$sourceBackedEvidenceReady = ($readyNonG2SourceReports.Count -gt 0 -and $missingRequiredSourceScenarioIds.Count -eq 0 -and $missingRequiredSourceComparisonIds.Count -eq 0)

$readyForReview = ($blockers.Count -eq 0)
$evidenceStatus = if ($readyForReview) { "cross-radio-evidence-ready" } else { "not-ready" }
$recommendations = if ($readyForReview) {
    @(
        "Attach this report as artifact id cross-radio-validation-report with kind cross-radio-validation-report-json.",
        "Treat this as evidence for default-graduation review only; it does not approve default DSP behavior changes."
    )
}
else {
    @(
        "Capture at least one source-backed non-G2 validation report with clean metric, Zeus live-trace, and Thetis live-trace comparisons before default DSP graduation review.",
        "Keep the DSP profile opt-in until G2, Thetis/current-Zeus comparisons, on-air review, and cross-radio evidence are all reviewed."
    )
}

$report = [ordered]@{
    schemaVersion = 2
    tool = "summarize-dsp-cross-radio-validation"
    generatedUtc = [DateTimeOffset]::UtcNow
    bundleRelativePaths = (-not [string]::IsNullOrWhiteSpace($bundlePath))
    reportPath = ConvertTo-PortablePath -Root $bundlePath -Path $resolvedReportPath
    markdownPath = if ($NoMarkdown) { $null } else { ConvertTo-PortablePath -Root $bundlePath -Path $resolvedMarkdownPath }
    readyForReview = $readyForReview
    evidenceStatus = $evidenceStatus
    defaultBehaviorChangeApproved = $false
    hardwareTargetCount = $hardwareTargets.Count
    hardwareTargets = @($hardwareTargets.ToArray())
    nonG2HardwareTargetCount = $nonG2HardwareTargets.Count
    nonG2HardwareTargets = @($nonG2HardwareTargets)
    scenarioCount = $scenarioIds.Count
    scenarioIds = @($scenarioIds.ToArray())
    comparisonCount = $comparisonIds.Count
    comparisonIds = @($comparisonIds.ToArray())
    validationReportCount = $sourceReportRecords.Count
    sourceReportCount = $sourceReportRecords.Count
    sourceProblemReportCount = $sourceProblemReports.Count
    sourceWarningReportCount = $sourceWarningReports.Count
    nonG2SourceReportCount = $nonG2SourceReports.Count
    readyNonG2SourceReportCount = $readyNonG2SourceReports.Count
    sourceMetricComparisonReadyCount = $sourceMetricReadyReports.Count
    sourceLiveTraceComparisonReadyCount = $sourceLiveTraceReadyReports.Count
    sourceThetisLiveTraceComparisonReadyCount = $sourceThetisLiveTraceReadyReports.Count
    requiredSourceScenarioCount = $requiredSourceScenarioIds.Count
    requiredSourceScenarioIds = @($requiredSourceScenarioIds)
    sourceBackedScenarioCount = $sourceBackedScenarioIds.Count
    sourceBackedScenarioIds = @($sourceBackedScenarioIds.ToArray())
    missingRequiredSourceScenarioCount = $missingRequiredSourceScenarioIds.Count
    missingRequiredSourceScenarioIds = @($missingRequiredSourceScenarioIds)
    requiredSourceComparisonCount = $requiredSourceComparisonIds.Count
    requiredSourceComparisonIds = @($requiredSourceComparisonIds)
    sourceBackedComparisonCount = $sourceBackedComparisonIds.Count
    sourceBackedComparisonIds = @($sourceBackedComparisonIds.ToArray())
    missingRequiredSourceComparisonCount = $missingRequiredSourceComparisonIds.Count
    missingRequiredSourceComparisonIds = @($missingRequiredSourceComparisonIds)
    sourceBackedEvidenceReady = $sourceBackedEvidenceReady
    sourceReports = @($sourceReportRecords)
    blockerCount = $blockers.Count
    blockers = @($blockers.ToArray())
    recommendations = $recommendations
    defaultBehaviorChangeReady = $false
    defaultBehaviorChangeBlockerIds = @("explicit-default-approval-required", "cross-radio-review-required")
}

Write-JsonFile -Path $resolvedReportPath -Value $report

if (-not $NoMarkdown) {
    Set-Content -LiteralPath $resolvedMarkdownPath -Value (Build-MarkdownReport $report) -Encoding UTF8
}

if ($JsonOnly) {
    $report | ConvertTo-Json -Depth 48
}
else {
    if ($readyForReview) {
        Write-Host "DSP cross-radio validation report passed."
    }
    else {
        Write-Host "DSP cross-radio validation report is not ready."
    }
    Write-Host "Report: $resolvedReportPath"
    if (-not $NoMarkdown) {
        Write-Host "Markdown: $resolvedMarkdownPath"
    }
    Write-Host "Non-G2 targets: $($nonG2HardwareTargets.Count), Scenarios: $($scenarioIds.Count), Comparisons: $($comparisonIds.Count), Blockers: $($blockers.Count)"
}

if ($FailOnNotReady -and -not $readyForReview) {
    exit 1
}
