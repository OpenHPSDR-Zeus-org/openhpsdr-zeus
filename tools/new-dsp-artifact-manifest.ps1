param(
    [Parameter(Mandatory = $true)]
    [string]$BundleDir,

    [string]$OutputPath = "",

    [switch]$AcceptanceManifest,

    [switch]$IncludeEndpointJson,

    [switch]$IncludeOptionalArtifacts,

    [switch]$RequireLiveAcceptanceArtifacts,

    [switch]$Force
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

function ConvertTo-ArtifactFileName {
    param([Parameter(Mandatory = $true)][string]$Value)

    $safe = $Value.Trim().ToLowerInvariant() -replace "[^a-z0-9._-]+", "-"
    $safe = $safe.Trim("-")
    if ([string]::IsNullOrWhiteSpace($safe)) {
        return "artifact"
    }

    return $safe
}

function ConvertTo-ComparisonId {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return ""
    }

    $normalized = $Value.Trim().ToLowerInvariant() -replace "[^a-z0-9]+", "-"
    $normalized = $normalized.Trim("-")

    switch ($normalized) {
        "external" { return "candidate-external-engine-opt-in" }
        "external-engine" { return "candidate-external-engine-opt-in" }
        "candidate-external-engine-opt-in" { return "candidate-external-engine-opt-in" }
        default { return $normalized }
    }
}

function Add-ExternalEngineBakeoffScopeTrigger {
    param(
        [System.Collections.Generic.List[string]]$Triggers,
        [Parameter(Mandatory = $true)][string]$SourceName,
        $Object
    )

    foreach ($value in (Get-JsonArray $Object "requiredComparisons")) {
        $comparison = ConvertTo-ComparisonId ([string]$value)
        if ($comparison -eq "candidate-external-engine-opt-in") {
            if (-not $Triggers.Contains($SourceName)) {
                $Triggers.Add($SourceName) | Out-Null
            }
        }
    }
}

function Get-DefaultArtifactPath {
    param(
        [Parameter(Mandatory = $true)][string]$Id,
        [Parameter(Mandatory = $true)][string]$Kind
    )

    $fileName = ConvertTo-ArtifactFileName $Id
    switch -Regex ($Kind) {
        "jsonl" { return "artifacts/$fileName.jsonl" }
        "json" { return "artifacts/$fileName.json" }
        "audio" { return "artifacts/$fileName.json" }
        "spectrum" { return "artifacts/$fileName.json" }
        "trace" { return "artifacts/$fileName.json" }
        "notes" { return "artifacts/$fileName.md" }
        default { return "artifacts/$fileName.dat" }
    }
}

function Get-EndpointFilesByPath {
    param($Index)

    $result = @{}
    foreach ($endpoint in (Get-JsonArray $Index "endpoints")) {
        $path = [string](Get-JsonValue $endpoint "path")
        $file = [string](Get-JsonValue $endpoint "file")
        if (-not [string]::IsNullOrWhiteSpace($path) -and -not [string]::IsNullOrWhiteSpace($file)) {
            $result[$path] = $file
        }
    }
    return $result
}

function Add-ArtifactRecord {
    param(
        [System.Collections.Generic.List[object]]$Artifacts,
        [hashtable]$SeenArtifactIds,
        [Parameter(Mandatory = $true)][string]$Id,
        [Parameter(Mandatory = $true)][string]$Kind,
        [string]$Source = "",
        [string]$Purpose = "",
        [string]$Cadence = "",
        [Parameter(Mandatory = $true)][string]$Path,
        [bool]$Required = $false,
        [object[]]$ScenarioIds = @(),
        [object[]]$ComparisonIds = @()
    )

    $SeenArtifactIds[$Id] = $true
    $record = [ordered]@{
        id = $Id
        kind = $Kind
        source = $Source
        purpose = $Purpose
        cadence = $Cadence
        path = $Path
        required = $Required
        scenarioIds = @($ScenarioIds)
    }

    if ($ComparisonIds.Count -gt 0) {
        $record["comparisonIds"] = @($ComparisonIds)
    }

    $Artifacts.Add($record) | Out-Null
}

$bundlePath = (Resolve-Path -LiteralPath $BundleDir).Path
$captureManifestPath = Join-Path $bundlePath "benchmark-capture-manifest.json"
if (-not (Test-Path -LiteralPath $captureManifestPath -PathType Leaf)) {
    throw "DSP modernization bundle is missing benchmark-capture-manifest.json: $bundlePath"
}

$bundleIndexPath = Join-Path $bundlePath "bundle-index.json"
$bundleIndex = $null
if (Test-Path -LiteralPath $bundleIndexPath -PathType Leaf) {
    $bundleIndex = Read-JsonFile $bundleIndexPath
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    if ($AcceptanceManifest) {
        $OutputPath = "artifact-manifest.json"
    }
    else {
        $OutputPath = "artifact-manifest.template.json"
    }
}

$resolvedOutputPath = Get-BundlePath $bundlePath $OutputPath
if ((Test-Path -LiteralPath $resolvedOutputPath -PathType Leaf) -and -not $Force) {
    throw "Output file already exists. Use -Force to overwrite: $resolvedOutputPath"
}

$outputParent = Split-Path -Parent $resolvedOutputPath
if (-not [string]::IsNullOrWhiteSpace($outputParent)) {
    New-Item -ItemType Directory -Force -Path $outputParent | Out-Null
}

$captureManifest = Read-JsonFile $captureManifestPath
$benchmarkPlanPath = Join-Path $bundlePath "benchmark-plan.json"
$benchmarkPlan = if (Test-Path -LiteralPath $benchmarkPlanPath -PathType Leaf) {
    Read-JsonFile $benchmarkPlanPath
}
else {
    $null
}

$externalEngineBakeoffScopeTriggers = New-Object System.Collections.Generic.List[string]
if ($null -ne $benchmarkPlan) {
    Add-ExternalEngineBakeoffScopeTrigger `
        -Triggers $externalEngineBakeoffScopeTriggers `
        -SourceName "benchmark-plan.requiredComparisons" `
        -Object $benchmarkPlan
}
Add-ExternalEngineBakeoffScopeTrigger `
    -Triggers $externalEngineBakeoffScopeTriggers `
    -SourceName "benchmark-capture-manifest.requiredComparisons" `
    -Object $captureManifest
$externalEngineBakeoffInScope = $externalEngineBakeoffScopeTriggers.Count -gt 0
$endpointFilesByPath = @{}
if ($null -ne $bundleIndex) {
    $endpointFilesByPath = Get-EndpointFilesByPath $bundleIndex
}

$artifacts = New-Object System.Collections.Generic.List[object]
$seenArtifactIds = @{}
foreach ($artifact in (Get-JsonArray $captureManifest "requiredArtifacts")) {
    $id = [string](Get-JsonValue $artifact "id")
    $kind = [string](Get-JsonValue $artifact "kind")
    if ([string]::IsNullOrWhiteSpace($id)) {
        continue
    }

    $source = [string](Get-JsonValue $artifact "source")
    $isEndpoint = ($kind -ieq "endpoint-json")
    if ($isEndpoint -and -not $IncludeEndpointJson) {
        continue
    }

    $required = Test-Truthy (Get-JsonValue $artifact "required")
    $forceLiveAcceptanceArtifact = ($RequireLiveAcceptanceArtifacts -and @(
            "live-diagnostics-trace-comparison",
            "live-diagnostics-trace-index",
            "live-diagnostics-history"
        ) -contains $id)
    $effectiveRequired = ($required -or $forceLiveAcceptanceArtifact)
    if (-not $required -and -not $IncludeOptionalArtifacts -and -not $forceLiveAcceptanceArtifact) {
        continue
    }

    $path = Get-DefaultArtifactPath $id $kind
    if ($isEndpoint -and $endpointFilesByPath.ContainsKey($source)) {
        $path = [string]$endpointFilesByPath[$source]
    }

    $scenarioIds = @(Get-JsonArray $artifact "scenarioIds")
    $comparisonIds = @(Get-JsonArray $artifact "comparisonIds")
    if ($id -eq "external-engine-bakeoff-report" -and $comparisonIds.Count -eq 0 -and $externalEngineBakeoffInScope) {
        $comparisonIds = @("candidate-external-engine-opt-in")
    }
    $purpose = [string](Get-JsonValue $artifact "purpose")
    $cadence = [string](Get-JsonValue $artifact "cadence")

    if ($id -eq "live-diagnostics-trace-index") {
        Add-ArtifactRecord `
            -Artifacts $artifacts `
            -SeenArtifactIds $seenArtifactIds `
            -Id $id `
            -Kind $kind `
            -Source $source `
            -Purpose "$purpose Off-baseline matrix index." `
            -Cadence $cadence `
            -Path "artifacts/live-diagnostics-trace-index.off-baseline.json" `
            -Required $effectiveRequired `
            -ScenarioIds $scenarioIds `
            -ComparisonIds @("off-baseline")

        Add-ArtifactRecord `
            -Artifacts $artifacts `
            -SeenArtifactIds $seenArtifactIds `
            -Id "live-diagnostics-matrix-report-off-baseline" `
            -Kind "diagnostics-matrix-json" `
            -Source "tools/run-dsp-live-diagnostics-matrix.ps1" `
            -Purpose "$purpose Off-baseline matrix report with self-consistency and trace-index hash evidence." `
            -Cadence $cadence `
            -Path "artifacts/live-diagnostics-matrix-report.off-baseline.json" `
            -Required $effectiveRequired `
            -ScenarioIds $scenarioIds `
            -ComparisonIds @("off-baseline")

        Add-ArtifactRecord `
            -Artifacts $artifacts `
            -SeenArtifactIds $seenArtifactIds `
            -Id $id `
            -Kind $kind `
            -Source $source `
            -Purpose "$purpose Thetis-parity matrix index." `
            -Cadence $cadence `
            -Path "artifacts/live-diagnostics-trace-index.thetis-parity.json" `
            -Required $effectiveRequired `
            -ScenarioIds $scenarioIds `
            -ComparisonIds @("thetis-parity")

        Add-ArtifactRecord `
            -Artifacts $artifacts `
            -SeenArtifactIds $seenArtifactIds `
            -Id "live-diagnostics-matrix-report-thetis-parity" `
            -Kind "diagnostics-matrix-json" `
            -Source "tools/run-dsp-live-diagnostics-matrix.ps1" `
            -Purpose "$purpose Thetis-parity matrix report with self-consistency and trace-index hash evidence." `
            -Cadence $cadence `
            -Path "artifacts/live-diagnostics-matrix-report.thetis-parity.json" `
            -Required $effectiveRequired `
            -ScenarioIds $scenarioIds `
            -ComparisonIds @("thetis-parity")

        Add-ArtifactRecord `
            -Artifacts $artifacts `
            -SeenArtifactIds $seenArtifactIds `
            -Id $id `
            -Kind $kind `
            -Source $source `
            -Purpose "$purpose Baseline matrix index." `
            -Cadence $cadence `
            -Path "artifacts/live-diagnostics-trace-index.baseline.json" `
            -Required $effectiveRequired `
            -ScenarioIds $scenarioIds `
            -ComparisonIds @("current-zeus")

        Add-ArtifactRecord `
            -Artifacts $artifacts `
            -SeenArtifactIds $seenArtifactIds `
            -Id "live-diagnostics-matrix-report-baseline" `
            -Kind "diagnostics-matrix-json" `
            -Source "tools/run-dsp-live-diagnostics-matrix.ps1" `
            -Purpose "$purpose Baseline matrix report with self-consistency and trace-index hash evidence." `
            -Cadence $cadence `
            -Path "artifacts/live-diagnostics-matrix-report.baseline.json" `
            -Required $effectiveRequired `
            -ScenarioIds $scenarioIds `
            -ComparisonIds @("current-zeus")

        Add-ArtifactRecord `
            -Artifacts $artifacts `
            -SeenArtifactIds $seenArtifactIds `
            -Id $id `
            -Kind $kind `
            -Source $source `
            -Purpose "$purpose Candidate matrix index." `
            -Cadence $cadence `
            -Path "artifacts/live-diagnostics-trace-index.candidate.json" `
            -Required $effectiveRequired `
            -ScenarioIds $scenarioIds `
            -ComparisonIds @("candidate-under-test")

        Add-ArtifactRecord `
            -Artifacts $artifacts `
            -SeenArtifactIds $seenArtifactIds `
            -Id "live-diagnostics-matrix-report-candidate" `
            -Kind "diagnostics-matrix-json" `
            -Source "tools/run-dsp-live-diagnostics-matrix.ps1" `
            -Purpose "$purpose Candidate matrix report with self-consistency and trace-index hash evidence." `
            -Cadence $cadence `
            -Path "artifacts/live-diagnostics-matrix-report.candidate.json" `
            -Required $effectiveRequired `
            -ScenarioIds $scenarioIds `
            -ComparisonIds @("candidate-under-test")
        continue
    }

    Add-ArtifactRecord `
        -Artifacts $artifacts `
        -SeenArtifactIds $seenArtifactIds `
        -Id $id `
        -Kind $kind `
        -Source $source `
        -Purpose $purpose `
        -Cadence $cadence `
        -Path $path `
        -Required $effectiveRequired `
        -ScenarioIds $scenarioIds `
        -ComparisonIds $comparisonIds
}

if ($RequireLiveAcceptanceArtifacts -and -not $seenArtifactIds.ContainsKey("live-diagnostics-trace-comparison")) {
    Add-ArtifactRecord `
        -Artifacts $artifacts `
        -SeenArtifactIds $seenArtifactIds `
        -Id "live-diagnostics-trace-comparison" `
        -Kind "diagnostics-comparison-json" `
        -Source "tools/compare-dsp-live-diagnostics-matrix.ps1" `
        -Purpose "Compare current-Zeus baseline and candidate DSP candidate live matrix windows across the G2 scenario set before accepting live DSP evidence." `
        -Cadence "once-per-candidate-live-matrix" `
        -Path "artifacts/live-diagnostics-trace-comparison.json" `
        -Required $true `
        -ScenarioIds @(Get-JsonArray $captureManifest "scenarioIds") `
        -ComparisonIds @("current-zeus", "candidate-under-test")
}

if ($RequireLiveAcceptanceArtifacts -and -not $seenArtifactIds.ContainsKey("live-diagnostics-trace-comparison-thetis-parity")) {
    Add-ArtifactRecord `
        -Artifacts $artifacts `
        -SeenArtifactIds $seenArtifactIds `
        -Id "live-diagnostics-trace-comparison-thetis-parity" `
        -Kind "diagnostics-comparison-json" `
        -Source "tools/compare-dsp-live-diagnostics-matrix.ps1" `
        -Purpose "Compare Thetis-parity baseline and candidate DSP candidate live matrix windows across the G2 scenario set so candidate evidence cannot drift from the WDSP behavior authority." `
        -Cadence "once-per-candidate-live-matrix" `
        -Path "artifacts/live-diagnostics-trace-comparison.thetis-parity.json" `
        -Required $true `
        -ScenarioIds @(Get-JsonArray $captureManifest "scenarioIds") `
        -ComparisonIds @("thetis-parity", "candidate-under-test")
}

if ($RequireLiveAcceptanceArtifacts -and -not $seenArtifactIds.ContainsKey("live-diagnostics-trace-index")) {
    $scenarioIds = @(Get-JsonArray $captureManifest "scenarioIds")

    Add-ArtifactRecord `
        -Artifacts $artifacts `
        -SeenArtifactIds $seenArtifactIds `
        -Id "live-diagnostics-trace-index" `
        -Kind "trace" `
        -Source "tools/run-dsp-live-diagnostics-matrix.ps1" `
        -Purpose "Required off-baseline live diagnostics matrix index for G2 acceptance coverage." `
        -Cadence "once-per-g2-live-acceptance-matrix" `
        -Path "artifacts/live-diagnostics-trace-index.off-baseline.json" `
        -Required $true `
        -ScenarioIds $scenarioIds `
        -ComparisonIds @("off-baseline")

    Add-ArtifactRecord `
        -Artifacts $artifacts `
        -SeenArtifactIds $seenArtifactIds `
        -Id "live-diagnostics-matrix-report-off-baseline" `
        -Kind "diagnostics-matrix-json" `
        -Source "tools/run-dsp-live-diagnostics-matrix.ps1" `
        -Purpose "Required off-baseline matrix report with trace-index hash evidence for G2 acceptance coverage." `
        -Cadence "once-per-g2-live-acceptance-matrix" `
        -Path "artifacts/live-diagnostics-matrix-report.off-baseline.json" `
        -Required $true `
        -ScenarioIds $scenarioIds `
        -ComparisonIds @("off-baseline")

    Add-ArtifactRecord `
        -Artifacts $artifacts `
        -SeenArtifactIds $seenArtifactIds `
        -Id "live-diagnostics-trace-index" `
        -Kind "trace" `
        -Source "tools/run-dsp-live-diagnostics-matrix.ps1" `
        -Purpose "Required Thetis-parity live diagnostics matrix index for G2 acceptance coverage." `
        -Cadence "once-per-g2-live-acceptance-matrix" `
        -Path "artifacts/live-diagnostics-trace-index.thetis-parity.json" `
        -Required $true `
        -ScenarioIds $scenarioIds `
        -ComparisonIds @("thetis-parity")

    Add-ArtifactRecord `
        -Artifacts $artifacts `
        -SeenArtifactIds $seenArtifactIds `
        -Id "live-diagnostics-matrix-report-thetis-parity" `
        -Kind "diagnostics-matrix-json" `
        -Source "tools/run-dsp-live-diagnostics-matrix.ps1" `
        -Purpose "Required Thetis-parity matrix report with trace-index hash evidence for G2 acceptance coverage." `
        -Cadence "once-per-g2-live-acceptance-matrix" `
        -Path "artifacts/live-diagnostics-matrix-report.thetis-parity.json" `
        -Required $true `
        -ScenarioIds $scenarioIds `
        -ComparisonIds @("thetis-parity")

    Add-ArtifactRecord `
        -Artifacts $artifacts `
        -SeenArtifactIds $seenArtifactIds `
        -Id "live-diagnostics-trace-index" `
        -Kind "trace" `
        -Source "tools/run-dsp-live-diagnostics-matrix.ps1" `
        -Purpose "Required current-Zeus live diagnostics matrix index for G2 acceptance coverage." `
        -Cadence "once-per-g2-live-acceptance-matrix" `
        -Path "artifacts/live-diagnostics-trace-index.baseline.json" `
        -Required $true `
        -ScenarioIds $scenarioIds `
        -ComparisonIds @("current-zeus")

    Add-ArtifactRecord `
        -Artifacts $artifacts `
        -SeenArtifactIds $seenArtifactIds `
        -Id "live-diagnostics-matrix-report-baseline" `
        -Kind "diagnostics-matrix-json" `
        -Source "tools/run-dsp-live-diagnostics-matrix.ps1" `
        -Purpose "Required current-Zeus matrix report with trace-index hash evidence for G2 acceptance coverage." `
        -Cadence "once-per-g2-live-acceptance-matrix" `
        -Path "artifacts/live-diagnostics-matrix-report.baseline.json" `
        -Required $true `
        -ScenarioIds $scenarioIds `
        -ComparisonIds @("current-zeus")

    Add-ArtifactRecord `
        -Artifacts $artifacts `
        -SeenArtifactIds $seenArtifactIds `
        -Id "live-diagnostics-trace-index" `
        -Kind "trace" `
        -Source "tools/run-dsp-live-diagnostics-matrix.ps1" `
        -Purpose "Required candidate DSP live diagnostics matrix index for G2 acceptance coverage." `
        -Cadence "once-per-g2-live-acceptance-matrix" `
        -Path "artifacts/live-diagnostics-trace-index.candidate.json" `
        -Required $true `
        -ScenarioIds $scenarioIds `
        -ComparisonIds @("candidate-under-test")

    Add-ArtifactRecord `
        -Artifacts $artifacts `
        -SeenArtifactIds $seenArtifactIds `
        -Id "live-diagnostics-matrix-report-candidate" `
        -Kind "diagnostics-matrix-json" `
        -Source "tools/run-dsp-live-diagnostics-matrix.ps1" `
        -Purpose "Required candidate DSP matrix report with trace-index hash evidence for G2 acceptance coverage." `
        -Cadence "once-per-g2-live-acceptance-matrix" `
        -Path "artifacts/live-diagnostics-matrix-report.candidate.json" `
        -Required $true `
        -ScenarioIds $scenarioIds `
        -ComparisonIds @("candidate-under-test")
}

if (-not $seenArtifactIds.ContainsKey("fixture-metric-comparison-report")) {
    Add-ArtifactRecord `
        -Artifacts $artifacts `
        -SeenArtifactIds $seenArtifactIds `
        -Id "fixture-metric-comparison-report" `
        -Kind "comparison-json" `
        -Source "tools/run-dsp-wdsp-fixture-matrix.ps1" `
        -Purpose "Summarize candidate-vs-current-Zeus and candidate-vs-Thetis metric deltas, regressions, missing baselines, gate failures, and WDSP runtime hash alignment before strict bundle acceptance." `
        -Cadence "once-after-wdsp-offline-fixture-matrix" `
        -Path "artifacts/dsp-fixture-metric-comparison.json" `
        -Required $true `
        -ScenarioIds @(Get-JsonArray $captureManifest "scenarioIds")
}

if (-not $seenArtifactIds.ContainsKey("native-stage-timing-report")) {
    Add-ArtifactRecord `
        -Artifacts $artifacts `
        -SeenArtifactIds $seenArtifactIds `
        -Id "native-stage-timing-report" `
        -Kind "native-stage-timing-report-json" `
        -Source "tools/summarize-dsp-native-stage-timing.ps1" `
        -Purpose "Summarize WDSP fixture wrapper stage timing, managed allocation deltas, runtime hash provenance, and the remaining native C instrumentation gap before DSP build-out evidence is accepted." `
        -Cadence "once-after-wdsp-offline-fixture-matrix" `
        -Path "artifacts/native-stage-timing-report.json" `
        -Required $true `
        -ScenarioIds @(Get-JsonArray $captureManifest "scenarioIds")
}

if (($IncludeOptionalArtifacts -or $externalEngineBakeoffInScope) -and -not $seenArtifactIds.ContainsKey("external-engine-bakeoff-report")) {
    $externalBakeoffCadence = if ($externalEngineBakeoffInScope) {
        "once-per-capture-bundle-when-external-opt-in-comparison-is-in-scope"
    }
    else {
        "once-per-capture-bundle-when-external-engines-are-reviewed"
    }

    Add-ArtifactRecord `
        -Artifacts $artifacts `
        -SeenArtifactIds $seenArtifactIds `
        -Id "external-engine-bakeoff-report" `
        -Kind "external-candidate-report-json" `
        -Source "tools/summarize-dsp-external-engine-candidates.ps1" `
        -Purpose "Summarize opt-in external DSP/ML candidate readiness, blockers, risk tiers, required benchmark coverage, and snapshot sync before any post-demod bakeoff; required when candidate-external-engine-opt-in is in capture scope." `
        -Cadence $externalBakeoffCadence `
        -Path "artifacts/external-engine-bakeoff-report.json" `
        -Required $externalEngineBakeoffInScope `
        -ScenarioIds @(Get-JsonArray $captureManifest "scenarioIds") `
        -ComparisonIds @("candidate-external-engine-opt-in")
}

if ($externalEngineBakeoffInScope -and -not $seenArtifactIds.ContainsKey("external-engine-bakeoff-cycle-summary")) {
    Add-ArtifactRecord `
        -Artifacts $artifacts `
        -SeenArtifactIds $seenArtifactIds `
        -Id "external-engine-bakeoff-cycle-summary" `
        -Kind "external-engine-bakeoff-cycle-summary-json" `
        -Source "tools/run-dsp-external-engine-bakeoff.ps1" `
        -Purpose "Plan or summarize the operator-triggered first-safe external DSP/ML bakeoff cycle without approving raw WDSP IQ replacement, TX/PureSignal routing, or default behavior changes." `
        -Cadence "optional-after-external-engine-bakeoff-report-is-ready" `
        -Path "artifacts/external-engine-bakeoff-cycle-summary.json" `
        -Required $false `
        -ScenarioIds @(Get-JsonArray $captureManifest "scenarioIds") `
        -ComparisonIds @("candidate-external-engine-opt-in")
}

if (($IncludeOptionalArtifacts -or $RequireLiveAcceptanceArtifacts) -and -not $seenArtifactIds.ContainsKey("manual-tune-observer-report")) {
    Add-ArtifactRecord `
        -Artifacts $artifacts `
        -SeenArtifactIds $seenArtifactIds `
        -Id "manual-tune-observer-report" `
        -Kind "manual-tune-observer-report-json" `
        -Source "tools/watch-dsp-manual-tune-observer.ps1" `
        -Purpose "Summarize read-only operator/manual tuning captures, no-retune/no-write safety, stable active VFO evidence, weak/strong Candidate sample coverage, and AGC pumping risk before selecting a live-history recapture window." `
        -Cadence "optional-while-operator-manually-tunes-g2-active-frequencies" `
        -Path "artifacts/manual-tune-observer-report.json" `
        -Required $false `
        -ScenarioIds @(Get-JsonArray $captureManifest "scenarioIds") `
        -ComparisonIds @("candidate-under-test")
}

if (($IncludeOptionalArtifacts -or $RequireLiveAcceptanceArtifacts) -and -not $seenArtifactIds.ContainsKey("g2-rx-peak-hunt-report")) {
    Add-ArtifactRecord `
        -Artifacts $artifacts `
        -SeenArtifactIds $seenArtifactIds `
        -Id "g2-rx-peak-hunt-report" `
        -Kind "g2-rx-peak-hunt-report-json" `
        -Source "tools/run-dsp-g2-rx-peak-hunt.ps1" `
        -Purpose "Summarize RX-only G2 frontend peak-hunt windows, VFO restore safety, weak/strong Candidate sample coverage, AGC pumping risk, and candidate mixed weak+strong promotion evidence before live-history recapture." `
        -Cadence "optional-before-g2-live-history-mixed-weak-strong-recapture" `
        -Path "artifacts/g2-rx-peak-hunt-report.json" `
        -Required $false `
        -ScenarioIds @(Get-JsonArray $captureManifest "scenarioIds") `
        -ComparisonIds @("candidate-under-test")
}

if (($IncludeOptionalArtifacts -or $RequireLiveAcceptanceArtifacts) -and -not $seenArtifactIds.ContainsKey("live-diagnostics-history")) {
    Add-ArtifactRecord `
        -Artifacts $artifacts `
        -SeenArtifactIds $seenArtifactIds `
        -Id "live-diagnostics-history" `
        -Kind "diagnostics-history-json" `
        -Source "tools/summarize-dsp-live-diagnostics-history.ps1" `
        -Purpose "Summarize captured Candidate/NR2 live diagnostics attempts, rank best weak-signal and lowest-pumping traces, preserve safety-class rollups, and bind each history trace to watcher-summary/JSONL SHA-256 provenance before choosing the next candidate comparison." `
        -Cadence "once-after-several-live-diagnostics-attempts" `
        -Path "artifacts/live-diagnostics-history.json" `
        -Required ([bool]$RequireLiveAcceptanceArtifacts) `
        -ScenarioIds @(Get-JsonArray $captureManifest "scenarioIds")
}

if (($IncludeOptionalArtifacts -or $RequireLiveAcceptanceArtifacts) -and -not $seenArtifactIds.ContainsKey("live-acceptance-cycle-summary")) {
    Add-ArtifactRecord `
        -Artifacts $artifacts `
        -SeenArtifactIds $seenArtifactIds `
        -Id "live-acceptance-cycle-summary" `
        -Kind "live-acceptance-cycle-summary-json" `
        -Source "tools/run-dsp-live-acceptance-cycle.ps1" `
        -Purpose "Summarize the live acceptance wrapper outcome, child tool exit codes, strict validation result, triage result, blockers, and remaining limitations after a G2 live acceptance cycle." `
        -Cadence "once-after-g2-live-acceptance-cycle-wrapper" `
        -Path "artifacts/live-acceptance-cycle-summary.json" `
        -Required $false `
        -ScenarioIds @(Get-JsonArray $captureManifest "scenarioIds") `
        -ComparisonIds @("off-baseline", "thetis-parity", "current-zeus", "candidate-under-test")
}

if (($IncludeOptionalArtifacts -or $RequireLiveAcceptanceArtifacts) -and -not $seenArtifactIds.ContainsKey("rx-leveler-ab-live-comparison")) {
    Add-ArtifactRecord `
        -Artifacts $artifacts `
        -SeenArtifactIds $seenArtifactIds `
        -Id "rx-leveler-ab-live-comparison" `
        -Kind "rx-leveler-ab-comparison-json" `
        -Source "tools/summarize-dsp-rx-leveler-ab.ps1" `
        -Purpose "Score guarded frontend-backed G2 RX leveler A/B evidence and require active audio, tuned passband proof, candidate control-memory safety, and material opt-in improvement before treating the stable-speech candidate as promotable evidence." `
        -Cadence "once-after-g2-rx-leveler-active-audio-passband-ab" `
        -Path "artifacts/rx-leveler-ab-live-comparison.json" `
        -Required $false `
        -ScenarioIds @("rx-audio-leveler-passband") `
        -ComparisonIds @("current-zeus", "candidate-under-test")
}

if ($IncludeOptionalArtifacts -and -not $seenArtifactIds.ContainsKey("cross-radio-validation-report")) {
    Add-ArtifactRecord `
        -Artifacts $artifacts `
        -SeenArtifactIds $seenArtifactIds `
        -Id "cross-radio-validation-report" `
        -Kind "cross-radio-validation-report-json" `
        -Source "tools/summarize-dsp-cross-radio-validation.ps1" `
        -Purpose "Summarize non-G2 cross-radio scenario/comparison evidence before any default DSP graduation review; this artifact never approves default behavior by itself." `
        -Cadence "once-after-non-g2-cross-radio-validation-pass" `
        -Path "artifacts/cross-radio-validation-report.json" `
        -Required $false `
        -ScenarioIds @(Get-JsonArray $captureManifest "scenarioIds") `
        -ComparisonIds @("current-zeus", "thetis-parity", "candidate-under-test")
}

if ($IncludeOptionalArtifacts -and -not $seenArtifactIds.ContainsKey("wdsp-source-drift-report")) {
    Add-ArtifactRecord `
        -Artifacts $artifacts `
        -SeenArtifactIds $seenArtifactIds `
        -Id "wdsp-source-drift-report" `
        -Kind "wdsp-source-drift-report-json" `
        -Source "tools/compare-wdsp-source-drift.ps1" `
        -Purpose "Compare Zeus native/wdsp against a local Thetis or other reference WDSP source tree with line-ending-insensitive hashes and classify every source delta before native code changes are accepted." `
        -Cadence "once-before-native-wdsp-import-delete-or-refactor" `
        -Path "artifacts/wdsp-source-drift-report.json" `
        -Required $false `
        -ScenarioIds @(Get-JsonArray $captureManifest "scenarioIds")
}

if (-not $seenArtifactIds.ContainsKey("operator-notes")) {
    Add-ArtifactRecord `
        -Artifacts $artifacts `
        -SeenArtifactIds $seenArtifactIds `
        -Id "operator-notes" `
        -Kind "notes" `
        -Source "operator-session-notes" `
        -Purpose "Record mode, band, filter width, sample rate, AGC mode/top, attenuator state, squelch state, NR mode, listening impressions, and on-air observations." `
        -Cadence "once-per-capture-bundle" `
        -Path "artifacts/operator-notes.md" `
        -Required $true `
        -ScenarioIds @(Get-JsonArray $captureManifest "scenarioIds")
}

$output = [ordered]@{
    schemaVersion = 1
    tool = "new-dsp-artifact-manifest"
    generatedUtc = [DateTimeOffset]::UtcNow
    bundleDir = $bundlePath
    sourceCaptureManifest = "benchmark-capture-manifest.json"
    acceptanceManifest = [bool]$AcceptanceManifest
    requireLiveAcceptanceArtifacts = [bool]$RequireLiveAcceptanceArtifacts
    externalEngineBakeoffInScope = [bool]$externalEngineBakeoffInScope
    externalEngineBakeoffScopeTriggers = @($externalEngineBakeoffScopeTriggers.ToArray())
    notes = @(
        "This scaffold is derived from benchmark-capture-manifest.json.",
        "Endpoint JSON is validated through bundle-index.json unless -IncludeEndpointJson is used.",
        "Use watch-dsp-live-diagnostics.ps1 for optional diagnostics-jsonl traces across live scenario windows.",
        "Use run-dsp-live-diagnostics-matrix.ps1 for optional multi-scenario trace indexes; pass separate -IndexPath and -ReportPath values for off-baseline, Thetis-parity, current-Zeus baseline, and candidate DSP candidate runs. With -IncludeOptionalArtifacts, this scaffold emits separate off-baseline, Thetis-parity, baseline, and candidate live-diagnostics-trace-index and live-diagnostics-matrix-report entries.",
        "Use -RequireLiveAcceptanceArtifacts for G2 live acceptance review manifests after capture; it marks current-Zeus and Thetis-parity live-diagnostics-trace-comparison reports, the four live matrix trace-index/report pairs, and live-diagnostics-history required without pulling external-engine bakeoff into scope.",
        "The live-acceptance-cycle-summary artifact is optional because run-dsp-live-acceptance-cycle.ps1 writes it after strict validation; when present, strict validation verifies its wrapper identity and child report references.",
        "Use summarize-dsp-cross-radio-validation.ps1 with -BundleDir after a non-G2 validation pass to generate the optional cross-radio-validation-report artifact; it is evidence only and cannot approve default DSP behavior changes.",
        "Use summarize-dsp-puresignal-bench.ps1 after G2 TX bench captures to turn disabled and enabled PureSignal feedback traces into the required puresignal-safe-bypass-report artifact before TX profile graduation.",
        "Use capture-tx-output-headroom-ab.ps1 with -PlanOnly first, then -AllowTransmit only under manual operator TX control, to collect optional current-vs-headroom-trim-candidate live diagnostics; the script never keys MOX, TUN, or two-tone and resets the TX profile to current.",
        "Use summarize-dsp-rx-leveler-ab.ps1 with -BundleDir after guarded G2 RX leveler A/B capture to generate rx-leveler-ab-live-comparison.json; strict validation requires active audio, passband proof, candidate control-memory safety, and material improvement before this opt-in candidate can advance.",
        "Use compare-dsp-live-diagnostics-traces.ps1 with -BundleDir to compare baseline and candidate live traces before accepting a candidate window while keeping report paths portable.",
        "Use compare-dsp-live-diagnostics-matrix.ps1 with -BundleDir to compare baseline and candidate trace indexes across all captured live scenarios while keeping report paths portable; pass -BaselineComparisonId current-zeus and -CandidateComparisonId candidate-under-test for the required Zeus live acceptance comparison, then repeat with -BaselineComparisonId thetis-parity for the required WDSP authority comparison.",
        "live-diagnostics-trace-comparison reports carry capture-readiness comparison evidence into strict validation and validation triage, including hard-gate pass/fail, strict-preflight pass/fail, top soft constraints, and top hard gates.",
        "Use watch-dsp-manual-tune-observer.ps1 with -BundleDir, -MaxCapturesPerVfo 2, -RequireFrontendNearPassband, and -AllowStaleSceneCapture while the operator is manually tuning active G2 frequencies; the optional report is read-only/no-retune evidence that can retry an intermittent same-VFO window until mixed weak+strong evidence appears without spending acceptance captures on off-passband frontend peaks.",
        "Use run-dsp-g2-rx-peak-hunt.ps1 before mixed weak+strong live-history recapture when G2 windows are weak-only or off-signal; the optional report keeps RX-only safety, VFO restore, weak/strong sample coverage, and best-window recommendation evidence portable.",
        "Use summarize-dsp-live-diagnostics-history.ps1 with -BundleDir after several Candidate/NR2 live attempts so best weak-signal, lowest-pumping, latest tuning directions, and per-trace watcher-summary/JSONL hashes are preserved as portable review evidence.",
        "Use summarize-dsp-external-engine-candidates.ps1 with -BundleDir before any external DSP/ML bakeoff so RNNoise/DeepFilterNet/SpeexDSP/WebRTC blockers, risk, and required evidence stay explicit.",
        "Use compare-wdsp-source-drift.ps1 with -ReferenceDir pointing at the local Thetis WDSP source before native WDSP imports, deletions, or refactors; line-ending-only changes are ignored and likely-defect source drift must be explicitly classified.",
        "Use summarize-dsp-native-stage-timing.ps1 after WDSP-backed offline fixture evidence so stage timing, managed allocation deltas, runtime hash provenance, and the native C instrumentation gap are explicit in strict validation.",
        "If benchmark-plan.requiredComparisons or benchmark-capture-manifest.requiredComparisons includes candidate-external-engine-opt-in, this scaffold emits external-engine-bakeoff-report as required even without -IncludeOptionalArtifacts because strict validation requires that report by scope.",
        "For acceptance review, regenerate with -AcceptanceManifest -RequireLiveAcceptanceArtifacts after the comparison report is captured so live trace regressions and incomplete history coverage fail strict validation.",
        "For single-comparison artifact indexes, add comparisonIds to the artifact entry so validation checks only the captured comparison scope.",
        "Run audit-wdsp-native-symbols.ps1 with -RequireBinaryExports for the required wdsp-native-symbol-audit.json before accepting native or P/Invoke changes.",
        "Run audit-wdsp-runtime-artifacts.ps1 for the required wdsp-runtime-artifact-audit.json before claiming packaged NR4/Candidate support for any RID.",
        "For plural audio, spectrum, and trace evidence, store an index JSON at the generated path with a files array of bundle-relative evidence file paths plus scenario/candidate metadata.",
        "Use run-dsp-wdsp-fixture-matrix.ps1 with -BundleDir to generate WDSP-backed offline fixture metrics, portable audio/spectrum evidence indexes, runtime audit, and dsp-fixture-metric-comparison.json in one repeatable step; add -ValidateBundle -RequireArtifactFiles before treating acceptanceEvidenceReady as possible.",
        "run-dsp-offline-fixture-evidence.ps1 is only a deterministic schema fallback; strict validation requires WDSP-backed fixture comparison evidence.",
        "Run validate-dsp-modernization-bundle.ps1 with -RequireArtifactFiles only after every required path exists and is non-empty."
    )
    artifacts = @($artifacts.ToArray())
}

Write-JsonFile -Path $resolvedOutputPath -Value $output

[ordered]@{
    outputPath = $resolvedOutputPath
    artifactCount = $artifacts.Count
    acceptanceManifest = [bool]$AcceptanceManifest
    includeEndpointJson = [bool]$IncludeEndpointJson
    includeOptionalArtifacts = [bool]$IncludeOptionalArtifacts
    requireLiveAcceptanceArtifacts = [bool]$RequireLiveAcceptanceArtifacts
    externalEngineBakeoffInScope = [bool]$externalEngineBakeoffInScope
    externalEngineBakeoffScopeTriggers = @($externalEngineBakeoffScopeTriggers.ToArray())
} | ConvertTo-Json -Depth 8
