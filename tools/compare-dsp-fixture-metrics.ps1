param(
    [string]$BundleDir = "",

    [string]$MetricsPath = "",

    [string]$BenchmarkPlanPath = "",

    [string]$MetricCatalogPath = "",

    [string]$ReportPath = "",

    [string]$MarkdownPath = "",

    [double]$Tolerance = 0.000001,

    [switch]$FailOnRegression,

    [switch]$IncludeNonFixtureScenarios,

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

    $json = $Value | ConvertTo-Json -Depth 32
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

function ConvertTo-MetricId {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return ""
    }

    return ($Value.Trim().ToLowerInvariant() -replace "[^a-z0-9]+", "")
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

function Get-NumericValue {
    param($Value)

    if ($null -eq $Value) {
        return $null
    }

    if ($Value -is [byte] -or $Value -is [int] -or $Value -is [long] -or $Value -is [float] -or $Value -is [double] -or $Value -is [decimal]) {
        return [double]$Value
    }

    $parsed = 0.0
    $style = [System.Globalization.NumberStyles]::Float -bor [System.Globalization.NumberStyles]::AllowThousands
    if ([double]::TryParse([string]$Value, $style, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$parsed)) {
        return $parsed
    }

    return $null
}

function Add-MetricValue {
    param(
        [hashtable]$Map,
        [string]$Name,
        $Value
    )

    $metricId = ConvertTo-MetricId $Name
    if ([string]::IsNullOrWhiteSpace($metricId)) {
        return
    }

    $numeric = Get-NumericValue $Value
    if ($null -ne $numeric) {
        $Map[$metricId] = $numeric
    }
}

function Add-MetricValuesFromValue {
    param(
        [hashtable]$Map,
        $Value
    )

    if ($null -eq $Value) {
        return
    }

    if ($Value -is [System.Array]) {
        foreach ($item in @($Value)) {
            Add-MetricValuesFromValue $Map $item
        }
        return
    }

    if ($Value -is [string]) {
        return
    }

    $explicitMetricId = ""
    foreach ($name in @("id", "name", "metric", "metricId", "key")) {
        $candidate = ConvertTo-MetricId ([string](Get-JsonValue $Value $name))
        if (-not [string]::IsNullOrWhiteSpace($candidate)) {
            $explicitMetricId = $candidate
            break
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($explicitMetricId)) {
        foreach ($name in @("value", "valueDb", "valueDbfs", "valueMs", "score", "amount", "count", "rate")) {
            $numeric = Get-NumericValue (Get-JsonValue $Value $name)
            if ($null -ne $numeric) {
                $Map[$explicitMetricId] = $numeric
                return
            }
        }
    }

    foreach ($property in @($Value.PSObject.Properties)) {
        if ($property.Name -in @("id", "name", "metric", "metricId", "key", "unit", "units", "status")) {
            continue
        }

        Add-MetricValue $Map $property.Name $property.Value
    }
}

function Get-MetricMapFromEntry {
    param($Entry)

    $map = @{}
    foreach ($name in @("metrics", "metricResults", "measurements", "values")) {
        Add-MetricValuesFromValue $map (Get-JsonValue $Entry $name)
    }

    return $map
}

function Get-ScenarioIdsFromEntry {
    param($Entry)

    $scenarioIds = New-Object System.Collections.Generic.List[string]
    foreach ($name in @("scenarioId", "scenario", "id")) {
        $scenario = [string](Get-JsonValue $Entry $name)
        if (-not [string]::IsNullOrWhiteSpace($scenario)) {
            $scenarioIds.Add($scenario) | Out-Null
            break
        }
    }

    foreach ($value in (Get-JsonArray $Entry "scenarioIds")) {
        $scenario = [string]$value
        if (-not [string]::IsNullOrWhiteSpace($scenario)) {
            $scenarioIds.Add($scenario) | Out-Null
        }
    }

    return @($scenarioIds.ToArray() | Select-Object -Unique)
}

function Get-ComparisonIdsFromEntry {
    param($Entry)

    $comparisonIds = New-Object System.Collections.Generic.List[string]
    foreach ($name in @("comparison", "comparisonId", "candidate", "candidateId", "mode", "backend")) {
        $comparison = ConvertTo-ComparisonId ([string](Get-JsonValue $Entry $name))
        if (-not [string]::IsNullOrWhiteSpace($comparison)) {
            $comparisonIds.Add($comparison) | Out-Null
        }
    }

    foreach ($name in @("comparisons", "comparisonIds", "candidates", "candidateIds")) {
        foreach ($value in (Get-JsonArray $Entry $name)) {
            $comparison = ConvertTo-ComparisonId ([string]$value)
            if (-not [string]::IsNullOrWhiteSpace($comparison)) {
                $comparisonIds.Add($comparison) | Out-Null
            }
        }
    }

    return @($comparisonIds.ToArray() | Select-Object -Unique)
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

    $metricMap = Get-MetricMapFromEntry $Entry
    if ($metricMap.Count -eq 0) {
        return
    }

    $scenarioIds = @(Get-ScenarioIdsFromEntry $Entry)
    if ($scenarioIds.Count -eq 0) {
        $scenarioIds = @($InheritedScenarioIds)
    }

    $comparisonIds = @(Get-ComparisonIdsFromEntry $Entry)
    if ($comparisonIds.Count -eq 0) {
        $comparisonIds = @($InheritedComparisonIds)
    }

    if ($scenarioIds.Count -eq 0 -or $comparisonIds.Count -eq 0) {
        return
    }

    $gateSummary = Get-GateOutcomeSummary $Entry
    $Target.Add([ordered]@{
        scenarioIds = @($scenarioIds)
        comparisonIds = @($comparisonIds)
        metrics = $metricMap
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
        $scenarioIds = @(Get-ScenarioIdsFromEntry $scenario)
        Add-MetricEvidenceEntry $entries $scenario -InheritedScenarioIds $scenarioIds

        foreach ($name in @("results", "entries", "comparisons", "candidates")) {
            foreach ($entry in (Get-JsonArray $scenario $name)) {
                Add-MetricEvidenceEntry $entries $entry -InheritedScenarioIds $scenarioIds
            }
        }
    }

    return @($entries.ToArray())
}

function Get-FixtureEvidenceEngine {
    param($MetricsJson)

    $engine = [string](Get-JsonValue $MetricsJson "evidenceEngine")
    if (-not [string]::IsNullOrWhiteSpace($engine)) {
        return ($engine.Trim().ToLowerInvariant() -replace "[^a-z0-9]+", "-").Trim("-")
    }

    $tool = [string](Get-JsonValue $MetricsJson "tool")
    switch ($tool) {
        "dsp-fixture-evidence" { return "wdsp" }
        "run-dsp-wdsp-fixture-evidence" { return "wdsp" }
        "run-dsp-offline-fixture-evidence" { return "deterministic-synthetic" }
        default { return "unknown" }
    }
}

function Get-MetricDirection {
    param(
        [string]$MetricId,
        [hashtable]$Catalog
    )

    if ($null -ne $Catalog -and $Catalog.ContainsKey($MetricId)) {
        return [string]$Catalog[$MetricId]
    }

    switch ($MetricId) {
        "coherenttonepower" { return "higher" }
        "wantedsnr" { return "higher" }
        "spectralpreservation" { return "higher" }
        "speechbandpreservation" { return "higher" }
        "noisereduction" { return "higher" }
        "coherenttonecontinuity" { return "higher" }
        "impulsesuppression" { return "higher" }
        "wantedadjacentratio" { return "higher" }
        "statetransitionsuccess" { return "higher" }
        "feedbackstability" { return "higher" }
        "scenefreshness" { return "higher" }
        "runtimealignment" { return "higher" }

        "latency" { return "lower" }
        "cpu" { return "lower" }
        "artifactscore" { return "lower" }
        "rmsmovement" { return "lower" }
        "windowedrmsmovement" { return "lower" }
        "agcgainmovement" { return "lower" }
        "postblankerringing" { return "lower" }
        "filterleakage" { return "lower" }
        "falseopenrate" { return "lower" }
        "noisefloormovement" { return "lower" }
        "settlingtime" { return "lower" }
        "overshoot" { return "lower" }
        "clippingcount" { return "lower" }
        "intermodulationproxy" { return "lower" }
        "openlatency" { return "lower" }
        "closelatency" { return "lower" }
        "audiodiscontinuity" { return "lower" }
        "nativeexceptioncount" { return "lower" }
        "meterescape" { return "lower" }
        "txmonitorcoupling" { return "lower" }
        default { return "informational" }
    }
}

function Test-BaselineComparison {
    param([string]$ComparisonId)

    return $ComparisonId -eq "off-baseline" -or $ComparisonId -eq "thetis-parity" -or $ComparisonId -eq "current-zeus"
}

function Get-ComparisonVerdict {
    param(
        [string]$Direction,
        [double]$BaselineValue,
        [double]$CandidateValue,
        [double]$Tolerance
    )

    if ($Direction -eq "informational") {
        return "informational"
    }

    $improvement = $CandidateValue - $BaselineValue
    if ($Direction -eq "lower") {
        $improvement = $BaselineValue - $CandidateValue
    }

    if ($improvement -gt $Tolerance) {
        return "improvement"
    }

    if ($improvement -lt (-1.0 * $Tolerance)) {
        return "regression"
    }

    return "tie"
}

function Resolve-ArtifactMetricsPath {
    param([Parameter(Mandatory = $true)][string]$BundlePath)

    $artifactManifestPath = Join-Path $BundlePath "artifact-manifest.json"
    if (Test-Path -LiteralPath $artifactManifestPath -PathType Leaf) {
        $artifactManifest = Read-JsonFile $artifactManifestPath
        foreach ($artifact in (Get-JsonArray $artifactManifest "artifacts")) {
            $id = [string](Get-JsonValue $artifact "id")
            if ($id -eq "offline-fixture-metrics") {
                $path = [string](Get-JsonValue $artifact "path")
                if (-not [string]::IsNullOrWhiteSpace($path)) {
                    return Get-BundlePath $BundlePath $path
                }
            }
        }
    }

    return Join-Path $BundlePath "artifacts\offline-fixture-metrics.json"
}

function Build-MarkdownReport {
    param($Report)

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# DSP Fixture Metric Comparison") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add("- Ready for review: $($Report.readyForReview)") | Out-Null
    $lines.Add("- Evidence engine: $($Report.evidenceEngine)") | Out-Null
    $lines.Add("- WDSP-backed evidence: $($Report.wdspBackedEvidence)") | Out-Null
    $lines.Add("- WDSP runtime RID: $($Report.wdspRuntimeRid)") | Out-Null
    $lines.Add("- WDSP runtime SHA-256: $($Report.wdspRuntimeSha256)") | Out-Null
    $lines.Add("- WDSP runtime status: $($Report.wdspRuntimeStatus)") | Out-Null
    $lines.Add("- Scenario scope: $($Report.fixtureScenarioScope)") | Out-Null
    $lines.Add("- Skipped non-fixture scenarios: $($Report.skippedNonFixtureScenarioCount)") | Out-Null
    $lines.Add("- Candidate comparisons: $($Report.candidateComparisonCount)") | Out-Null
    $lines.Add("- Improvements: $($Report.improvementCount)") | Out-Null
    $lines.Add("- Regressions: $($Report.regressionCount)") | Out-Null
    $lines.Add("- Gate failures: $($Report.gateFailureCount)") | Out-Null
    $lines.Add("- Missing scenarios: $($Report.missingScenarioCount)") | Out-Null
    $lines.Add("- Missing current-Zeus baselines: $($Report.missingCurrentBaselineCount)") | Out-Null
    $lines.Add("- Missing Thetis-parity baselines: $($Report.missingThetisBaselineCount)") | Out-Null
    $lines.Add("- Missing candidate coverage: $($Report.missingCandidateCount)") | Out-Null
    $lines.Add("- Missing values: $($Report.missingMetricValueCount)") | Out-Null
    $lines.Add("- Informational metrics: $($Report.informationalMetricCount)") | Out-Null
    $lines.Add("") | Out-Null

    if ($Report.missingScenarios.Count -gt 0) {
        $lines.Add("## Missing Scenarios") | Out-Null
        $lines.Add("") | Out-Null
        foreach ($scenario in @($Report.missingScenarios)) {
            $lines.Add("- $scenario") | Out-Null
        }
        $lines.Add("") | Out-Null
    }

    $regressions = @($Report.comparisons | ForEach-Object {
        $comparison = $_
        @($comparison.metricComparisons | Where-Object { $_.verdict -eq "regression" } | ForEach-Object {
            [ordered]@{
                scenarioId = $comparison.scenarioId
                candidate = $comparison.candidateComparisonId
                baseline = $comparison.baselineComparisonId
                metric = $_.metricId
                baselineValue = $_.baselineValue
                candidateValue = $_.candidateValue
                improvementValue = $_.improvementValue
            }
        })
    })

    if ($regressions.Count -gt 0) {
        $lines.Add("## Regressions") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("| Scenario | Candidate | Baseline | Metric | Baseline | Candidate | Improvement |") | Out-Null
        $lines.Add("|---|---|---|---|---:|---:|---:|") | Out-Null
        foreach ($regression in $regressions) {
            $lines.Add("| $($regression.scenarioId) | $($regression.candidate) | $($regression.baseline) | $($regression.metric) | $($regression.baselineValue) | $($regression.candidateValue) | $($regression.improvementValue) |") | Out-Null
        }
        $lines.Add("") | Out-Null
    }

    $lines.Add("## Scenario Summary") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add("| Scenario | Candidate | Baseline | Improvements | Regressions | Ties | Informational | Missing | Gate failures |") | Out-Null
    $lines.Add("|---|---|---|---:|---:|---:|---:|---:|---:|") | Out-Null
    foreach ($comparison in @($Report.comparisons)) {
        $lines.Add("| $($comparison.scenarioId) | $($comparison.candidateComparisonId) | $($comparison.baselineComparisonId) | $($comparison.improvementCount) | $($comparison.regressionCount) | $($comparison.tieCount) | $($comparison.informationalMetricCount) | $($comparison.missingMetricValueCount) | $($comparison.gateFailureCount) |") | Out-Null
    }

    return @($lines.ToArray())
}

$bundlePath = ""
if (-not [string]::IsNullOrWhiteSpace($BundleDir)) {
    $bundlePath = (Resolve-Path -LiteralPath $BundleDir).Path

    if ([string]::IsNullOrWhiteSpace($BenchmarkPlanPath)) {
        $BenchmarkPlanPath = Join-Path $bundlePath "benchmark-plan.json"
    }

    if ([string]::IsNullOrWhiteSpace($MetricCatalogPath)) {
        $candidateMetricCatalogPath = Join-Path $bundlePath "benchmark-metric-catalog.json"
        if (Test-Path -LiteralPath $candidateMetricCatalogPath -PathType Leaf) {
            $MetricCatalogPath = $candidateMetricCatalogPath
        }
    }

    if ([string]::IsNullOrWhiteSpace($MetricsPath)) {
        $MetricsPath = Resolve-ArtifactMetricsPath $bundlePath
    }

    if ([string]::IsNullOrWhiteSpace($ReportPath)) {
        $ReportPath = Join-Path $bundlePath "dsp-fixture-metric-comparison.json"
    }
}

if ([string]::IsNullOrWhiteSpace($MetricsPath)) {
    throw "MetricsPath is required unless BundleDir contains artifact-manifest.json or artifacts/offline-fixture-metrics.json."
}

if ([string]::IsNullOrWhiteSpace($BenchmarkPlanPath)) {
    throw "BenchmarkPlanPath is required unless BundleDir is provided."
}

$metricsResolvedPath = (Resolve-Path -LiteralPath $MetricsPath).Path
$planResolvedPath = (Resolve-Path -LiteralPath $BenchmarkPlanPath).Path
$metricCatalogResolvedPath = $null

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $metricsDir = Split-Path -Parent $metricsResolvedPath
    $metricsName = [System.IO.Path]::GetFileNameWithoutExtension($metricsResolvedPath)
    $ReportPath = Join-Path $metricsDir "$metricsName.comparison.json"
}

if (-not $NoMarkdown -and [string]::IsNullOrWhiteSpace($MarkdownPath)) {
    $reportDir = Split-Path -Parent $ReportPath
    $reportName = [System.IO.Path]::GetFileNameWithoutExtension($ReportPath)
    $MarkdownPath = Join-Path $reportDir "$reportName.md"
}

$metricsJson = Read-JsonFile $metricsResolvedPath
$benchmarkPlan = Read-JsonFile $planResolvedPath
$metricsSha256 = Get-FileSha256 $metricsResolvedPath
$evidenceEngine = Get-FixtureEvidenceEngine $metricsJson
$evidenceTool = [string](Get-JsonValue $metricsJson "tool")
$wdspBackedEvidence = [string]::Equals($evidenceEngine, "wdsp", [StringComparison]::OrdinalIgnoreCase)
$syntheticFallbackEvidence = [string]::Equals($evidenceEngine, "deterministic-synthetic", [StringComparison]::OrdinalIgnoreCase)
$wdspRuntimeRid = [string](Get-JsonValue $metricsJson "wdspRuntimeRid")
$wdspRuntimePath = [string](Get-JsonValue $metricsJson "wdspRuntimePath")
$wdspRuntimePathKind = [string](Get-JsonValue $metricsJson "wdspRuntimePathKind")
$wdspRuntimeFileName = [string](Get-JsonValue $metricsJson "wdspRuntimeFileName")
$wdspRuntimeLength = [long](Get-NumericValue (Get-JsonValue $metricsJson "wdspRuntimeLength"))
$wdspRuntimeSha256 = ([string](Get-JsonValue $metricsJson "wdspRuntimeSha256")).Trim().ToLowerInvariant()
$wdspRuntimeStatus = [string](Get-JsonValue $metricsJson "wdspRuntimeStatus")
$metricDirectionCatalog = @{}

if (-not [string]::IsNullOrWhiteSpace($MetricCatalogPath)) {
    $metricCatalogResolvedPath = (Resolve-Path -LiteralPath $MetricCatalogPath).Path
    $metricCatalog = Read-JsonFile $metricCatalogResolvedPath

    foreach ($metric in (Get-JsonArray $metricCatalog "metrics")) {
        $metricId = ConvertTo-MetricId ([string](Get-JsonValue $metric "id"))
        if ([string]::IsNullOrWhiteSpace($metricId)) {
            $metricId = ConvertTo-MetricId ([string](Get-JsonValue $metric "name"))
        }

        $direction = [string](Get-JsonValue $metric "direction")
        if ((-not [string]::IsNullOrWhiteSpace($metricId)) -and
            ($direction -eq "higher" -or $direction -eq "lower" -or $direction -eq "informational")) {
            $metricDirectionCatalog[$metricId] = $direction
        }
    }
}

$scenarioPlan = @{}
$skippedScenarioIds = New-Object System.Collections.Generic.List[string]
foreach ($scenario in (Get-JsonArray $benchmarkPlan "scenarios")) {
    $scenarioId = [string](Get-JsonValue $scenario "id")
    if ([string]::IsNullOrWhiteSpace($scenarioId)) {
        continue
    }

    $fixtureStatus = [string](Get-JsonValue $scenario "fixtureStatus")
    if (-not $IncludeNonFixtureScenarios -and
        -not [string]::Equals($fixtureStatus, "offline-fixture-ready", [StringComparison]::OrdinalIgnoreCase)) {
        $skippedScenarioIds.Add($scenarioId) | Out-Null
        continue
    }

    $requiredMetricIds = New-Object System.Collections.Generic.List[string]
    foreach ($metric in (Get-JsonArray $scenario "requiredMetrics")) {
        $metricId = ConvertTo-MetricId ([string]$metric)
        if (-not [string]::IsNullOrWhiteSpace($metricId)) {
            $requiredMetricIds.Add($metricId) | Out-Null
        }
    }

    $scenarioPlan[$scenarioId] = [ordered]@{
        id = $scenarioId
        name = [string](Get-JsonValue $scenario "name")
        fixtureStatus = $fixtureStatus
        requiredMetricIds = @($requiredMetricIds.ToArray() | Select-Object -Unique)
    }
}

$metricEntries = @(Get-MetricEvidenceEntries $metricsJson)
$metricIndex = @{}
foreach ($entry in $metricEntries) {
    foreach ($scenarioId in @($entry.scenarioIds)) {
        $scenario = [string]$scenarioId
        if ([string]::IsNullOrWhiteSpace($scenario)) {
            continue
        }

        if (-not $metricIndex.ContainsKey($scenario)) {
            $metricIndex[$scenario] = @{}
        }

        foreach ($comparisonId in @($entry.comparisonIds)) {
            $comparison = ConvertTo-ComparisonId ([string]$comparisonId)
            if ([string]::IsNullOrWhiteSpace($comparison)) {
                continue
            }

            if (-not $metricIndex[$scenario].ContainsKey($comparison)) {
                $metricIndex[$scenario][$comparison] = [ordered]@{
                    metricValues = @{}
                    gateOutcomeCount = 0
                    failedGateCount = 0
                }
            }

            foreach ($metricId in @($entry.metrics.Keys)) {
                $metric = ConvertTo-MetricId ([string]$metricId)
                if (-not [string]::IsNullOrWhiteSpace($metric)) {
                    $metricIndex[$scenario][$comparison].metricValues[$metric] = [double]$entry.metrics[$metricId]
                }
            }

            $metricIndex[$scenario][$comparison].gateOutcomeCount += [int]$entry.gateOutcomeCount
            $metricIndex[$scenario][$comparison].failedGateCount += [int]$entry.failedGateCount
        }
    }
}

$comparisons = New-Object System.Collections.Generic.List[object]
$missingScenarios = New-Object System.Collections.Generic.List[string]
$regressionCount = 0
$improvementCount = 0
$tieCount = 0
$informationalMetricCount = 0
$missingMetricValueCount = 0
$candidateComparisonCount = 0
$gateFailureCount = 0
$missingCurrentBaselineCount = 0
$missingThetisBaselineCount = 0
$missingCandidateCount = 0

foreach ($scenarioId in ($scenarioPlan.Keys | Sort-Object)) {
    $plan = $scenarioPlan[$scenarioId]
    if (-not $metricIndex.ContainsKey($scenarioId)) {
        $missingScenarios.Add($scenarioId) | Out-Null
        continue
    }

    $scenarioMetrics = $metricIndex[$scenarioId]
    $candidateIds = @($scenarioMetrics.Keys | Where-Object { -not (Test-BaselineComparison $_) } | Sort-Object)
    if ($candidateIds.Count -eq 0) {
        $missingCandidateCount++
        continue
    }

    foreach ($candidateId in $candidateIds) {
        $candidateComparisonCount++
        $candidate = $scenarioMetrics[$candidateId]
        if ([int]$candidate.failedGateCount -gt 0) {
            $gateFailureCount += [int]$candidate.failedGateCount
        }

        foreach ($baselineId in @("current-zeus", "thetis-parity")) {
            if (-not $scenarioMetrics.ContainsKey($baselineId)) {
                if ($baselineId -eq "current-zeus") {
                    $missingCurrentBaselineCount++
                }
                else {
                    $missingThetisBaselineCount++
                }
                continue
            }

            $baseline = $scenarioMetrics[$baselineId]
            $metricComparisons = New-Object System.Collections.Generic.List[object]
            $comparisonImprovementCount = 0
            $comparisonRegressionCount = 0
            $comparisonTieCount = 0
            $comparisonInformationalCount = 0
            $comparisonMissingCount = 0

            foreach ($metricId in @($plan.requiredMetricIds)) {
                $direction = Get-MetricDirection $metricId $metricDirectionCatalog
                $baselineHasMetric = $baseline.metricValues.ContainsKey($metricId)
                $candidateHasMetric = $candidate.metricValues.ContainsKey($metricId)
                $baselineValue = $null
                $candidateValue = $null
                $improvementValue = $null
                $verdict = "missing"

                if ($baselineHasMetric) {
                    $baselineValue = [double]$baseline.metricValues[$metricId]
                }
                if ($candidateHasMetric) {
                    $candidateValue = [double]$candidate.metricValues[$metricId]
                }

                if (-not $baselineHasMetric -or -not $candidateHasMetric) {
                    $missingMetricValueCount++
                    $comparisonMissingCount++
                }
                elseif ($direction -eq "informational") {
                    $informationalMetricCount++
                    $comparisonInformationalCount++
                    $verdict = "informational"
                    $improvementValue = $candidateValue - $baselineValue
                }
                else {
                    $verdict = Get-ComparisonVerdict $direction $baselineValue $candidateValue $Tolerance
                    $improvementValue = $candidateValue - $baselineValue
                    if ($direction -eq "lower") {
                        $improvementValue = $baselineValue - $candidateValue
                    }

                    if ($verdict -eq "improvement") {
                        $improvementCount++
                        $comparisonImprovementCount++
                    }
                    elseif ($verdict -eq "regression") {
                        $regressionCount++
                        $comparisonRegressionCount++
                    }
                    elseif ($verdict -eq "tie") {
                        $tieCount++
                        $comparisonTieCount++
                    }
                }

                $metricComparisons.Add([ordered]@{
                    metricId = $metricId
                    direction = $direction
                    baselineValue = $baselineValue
                    candidateValue = $candidateValue
                    improvementValue = $improvementValue
                    verdict = $verdict
                }) | Out-Null
            }

            $comparisons.Add([ordered]@{
                scenarioId = $scenarioId
                scenarioName = $plan.name
                candidateComparisonId = $candidateId
                baselineComparisonId = $baselineId
                improvementCount = $comparisonImprovementCount
                regressionCount = $comparisonRegressionCount
                tieCount = $comparisonTieCount
                informationalMetricCount = $comparisonInformationalCount
                missingMetricValueCount = $comparisonMissingCount
                gateOutcomeCount = [int]$candidate.gateOutcomeCount
                gateFailureCount = [int]$candidate.failedGateCount
                metricComparisons = @($metricComparisons.ToArray())
            }) | Out-Null
        }
    }
}

$metricCoverageReadyForReview = ($regressionCount -eq 0 -and
    $gateFailureCount -eq 0 -and
    $missingScenarios.Count -eq 0 -and
    $missingCurrentBaselineCount -eq 0 -and
    $missingThetisBaselineCount -eq 0 -and
    $missingCandidateCount -eq 0 -and
    $missingMetricValueCount -eq 0)
$runtimeIdentityReadyForReview = ((-not $wdspBackedEvidence) -or
    (-not [string]::IsNullOrWhiteSpace($wdspRuntimeSha256) -and
        [string]::Equals($wdspRuntimeStatus, "found", [StringComparison]::OrdinalIgnoreCase)))
$readyForReview = ($metricCoverageReadyForReview -and $wdspBackedEvidence -and $runtimeIdentityReadyForReview)

$report = [ordered]@{
    schemaVersion = 1
    tool = "compare-dsp-fixture-metrics"
    generatedUtc = [DateTimeOffset]::UtcNow
    bundleDir = $bundlePath
    benchmarkPlanPath = $planResolvedPath
    metricCatalogPath = $metricCatalogResolvedPath
    metricCatalogMetricCount = $metricDirectionCatalog.Count
    metricsPath = $metricsResolvedPath
    metricsSha256 = $metricsSha256
    reportPath = $ReportPath
    markdownPath = if ($NoMarkdown) { $null } else { $MarkdownPath }
    tolerance = $Tolerance
    readyForReview = $readyForReview
    metricCoverageReadyForReview = $metricCoverageReadyForReview
    evidenceEngine = $evidenceEngine
    evidenceTool = $evidenceTool
    wdspBackedEvidence = $wdspBackedEvidence
    syntheticFallbackEvidence = $syntheticFallbackEvidence
    wdspRuntimeRid = $wdspRuntimeRid
    wdspRuntimePath = $wdspRuntimePath
    wdspRuntimePathKind = $wdspRuntimePathKind
    wdspRuntimeFileName = $wdspRuntimeFileName
    wdspRuntimeLength = $wdspRuntimeLength
    wdspRuntimeSha256 = $wdspRuntimeSha256
    wdspRuntimeStatus = $wdspRuntimeStatus
    wdspRuntimeIdentityReadyForReview = $runtimeIdentityReadyForReview
    fixtureScenarioScope = if ($IncludeNonFixtureScenarios) { "all-plan-scenarios" } else { "offline-fixture-ready" }
    scenarioPlanCount = $scenarioPlan.Count
    skippedNonFixtureScenarioCount = $skippedScenarioIds.Count
    skippedNonFixtureScenarioIds = @($skippedScenarioIds.ToArray())
    metricEntryCount = $metricEntries.Count
    candidateComparisonCount = $candidateComparisonCount
    improvementCount = $improvementCount
    regressionCount = $regressionCount
    tieCount = $tieCount
    informationalMetricCount = $informationalMetricCount
    missingMetricValueCount = $missingMetricValueCount
    gateFailureCount = $gateFailureCount
    missingScenarioCount = $missingScenarios.Count
    missingCurrentBaselineCount = $missingCurrentBaselineCount
    missingThetisBaselineCount = $missingThetisBaselineCount
    missingCandidateCount = $missingCandidateCount
    missingScenarios = @($missingScenarios.ToArray())
    comparisons = @($comparisons.ToArray())
}

Write-JsonFile -Path $ReportPath -Value $report

if (-not $NoMarkdown) {
    $markdown = Build-MarkdownReport $report
    Set-Content -LiteralPath $MarkdownPath -Value $markdown -Encoding UTF8
}

if ($JsonOnly) {
    $report | ConvertTo-Json -Depth 32
}
else {
    if ($readyForReview) {
        Write-Host "DSP fixture metric comparison passed: $metricsResolvedPath"
    }
    else {
        Write-Host "DSP fixture metric comparison found issues: $metricsResolvedPath"
    }
    Write-Host "Report: $ReportPath"
    if (-not $NoMarkdown) {
        Write-Host "Markdown: $MarkdownPath"
    }
    Write-Host "Regressions: $regressionCount, Gate failures: $gateFailureCount, Missing scenarios: $($missingScenarios.Count), Missing values: $missingMetricValueCount"
}

if ($FailOnRegression -and -not $readyForReview) {
    exit 1
}
