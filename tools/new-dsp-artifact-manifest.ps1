param(
    [Parameter(Mandatory = $true)]
    [string]$BundleDir,

    [string]$OutputPath = "",

    [switch]$AcceptanceManifest,

    [switch]$IncludeEndpointJson,

    [switch]$IncludeOptionalArtifacts,

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
    if (-not $required -and -not $IncludeOptionalArtifacts) {
        continue
    }

    $path = Get-DefaultArtifactPath $id $kind
    if ($isEndpoint -and $endpointFilesByPath.ContainsKey($source)) {
        $path = [string]$endpointFilesByPath[$source]
    }

    $scenarioIds = @(Get-JsonArray $artifact "scenarioIds")
    $purpose = [string](Get-JsonValue $artifact "purpose")
    $cadence = [string](Get-JsonValue $artifact "cadence")

    if ($id -eq "live-diagnostics-trace-index") {
        Add-ArtifactRecord `
            -Artifacts $artifacts `
            -SeenArtifactIds $seenArtifactIds `
            -Id $id `
            -Kind $kind `
            -Source $source `
            -Purpose "$purpose Baseline matrix index." `
            -Cadence $cadence `
            -Path "artifacts/live-diagnostics-trace-index.baseline.json" `
            -Required $required `
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
            -Required $required `
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
            -Required $required `
            -ScenarioIds $scenarioIds `
            -ComparisonIds @("nr5-spnr")

        Add-ArtifactRecord `
            -Artifacts $artifacts `
            -SeenArtifactIds $seenArtifactIds `
            -Id "live-diagnostics-matrix-report-candidate" `
            -Kind "diagnostics-matrix-json" `
            -Source "tools/run-dsp-live-diagnostics-matrix.ps1" `
            -Purpose "$purpose Candidate matrix report with self-consistency and trace-index hash evidence." `
            -Cadence $cadence `
            -Path "artifacts/live-diagnostics-matrix-report.candidate.json" `
            -Required $required `
            -ScenarioIds $scenarioIds `
            -ComparisonIds @("nr5-spnr")
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
        -Required $required `
        -ScenarioIds $scenarioIds
}

if (-not $seenArtifactIds.ContainsKey("fixture-metric-comparison-report")) {
    Add-ArtifactRecord `
        -Artifacts $artifacts `
        -SeenArtifactIds $seenArtifactIds `
        -Id "fixture-metric-comparison-report" `
        -Kind "comparison-json" `
        -Source "tools/compare-dsp-fixture-metrics.ps1" `
        -Purpose "Summarize candidate-vs-current-Zeus and candidate-vs-Thetis metric deltas, regressions, missing baselines, and gate failures before strict bundle acceptance." `
        -Cadence "once-after-offline-fixture-metrics" `
        -Path "dsp-fixture-metric-comparison.json" `
        -Required $true `
        -ScenarioIds @(Get-JsonArray $captureManifest "scenarioIds")
}

if ($IncludeOptionalArtifacts -and -not $seenArtifactIds.ContainsKey("external-engine-bakeoff-report")) {
    Add-ArtifactRecord `
        -Artifacts $artifacts `
        -SeenArtifactIds $seenArtifactIds `
        -Id "external-engine-bakeoff-report" `
        -Kind "external-candidate-report-json" `
        -Source "tools/summarize-dsp-external-engine-candidates.ps1" `
        -Purpose "Summarize opt-in external DSP/ML candidate readiness, blockers, risk tiers, required benchmark coverage, and snapshot sync before any post-demod bakeoff." `
        -Cadence "once-per-capture-bundle-when-external-engines-are-reviewed" `
        -Path "artifacts/external-engine-bakeoff-report.json" `
        -Required $false `
        -ScenarioIds @(Get-JsonArray $captureManifest "scenarioIds")
}

if ($IncludeOptionalArtifacts -and -not $seenArtifactIds.ContainsKey("live-diagnostics-history")) {
    Add-ArtifactRecord `
        -Artifacts $artifacts `
        -SeenArtifactIds $seenArtifactIds `
        -Id "live-diagnostics-history" `
        -Kind "diagnostics-history-json" `
        -Source "tools/summarize-dsp-live-diagnostics-history.ps1" `
        -Purpose "Summarize captured NR5/NR2 live diagnostics attempts, rank best weak-signal and lowest-pumping traces, preserve safety-class rollups, and bind each history trace to watcher-summary/JSONL SHA-256 provenance before choosing the next candidate comparison." `
        -Cadence "once-after-several-live-diagnostics-attempts" `
        -Path "artifacts/live-diagnostics-history.json" `
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
    notes = @(
        "This scaffold is derived from benchmark-capture-manifest.json.",
        "Endpoint JSON is validated through bundle-index.json unless -IncludeEndpointJson is used.",
        "Use watch-dsp-live-diagnostics.ps1 for optional diagnostics-jsonl traces across live scenario windows.",
        "Use run-dsp-live-diagnostics-matrix.ps1 for optional multi-scenario trace indexes; pass separate -IndexPath and -ReportPath values for baseline and candidate runs. With -IncludeOptionalArtifacts, this scaffold emits separate baseline/candidate live-diagnostics-trace-index and live-diagnostics-matrix-report entries.",
        "Use compare-dsp-live-diagnostics-traces.ps1 with -BundleDir to compare baseline and candidate live traces before accepting a candidate window while keeping report paths portable.",
        "Use compare-dsp-live-diagnostics-matrix.ps1 with -BundleDir to compare baseline and candidate trace indexes across all captured live scenarios while keeping report paths portable.",
        "Use summarize-dsp-live-diagnostics-history.ps1 with -BundleDir after several NR5/NR2 live attempts so best weak-signal, lowest-pumping, latest tuning directions, and per-trace watcher-summary/JSONL hashes are preserved as portable review evidence.",
        "Use summarize-dsp-external-engine-candidates.ps1 with -BundleDir before any external DSP/ML bakeoff so RNNoise/DeepFilterNet/SpeexDSP/WebRTC blockers, risk, and required evidence stay explicit.",
        "For acceptance review, set the live-diagnostics-trace-comparison artifact required=true after the comparison report is captured so regressions fail strict validation.",
        "For single-comparison artifact indexes, add comparisonIds to the artifact entry so validation checks only the captured comparison scope.",
        "Run audit-wdsp-native-symbols.ps1 with -RequireBinaryExports for the required wdsp-native-symbol-audit.json before accepting native or P/Invoke changes.",
        "Run audit-wdsp-runtime-artifacts.ps1 for the required wdsp-runtime-artifact-audit.json before claiming packaged NR4/NR5 support for any RID.",
        "For plural audio, spectrum, and trace evidence, store an index JSON at the generated path with a files array of bundle-relative evidence file paths plus scenario/candidate metadata.",
        "Use run-dsp-wdsp-fixture-evidence.ps1 with -BundleDir to generate WDSP-backed offline fixture metrics plus portable audio/spectrum evidence indexes before running fixture comparison; run-dsp-offline-fixture-evidence.ps1 is only a deterministic schema fallback.",
        "Run compare-dsp-fixture-metrics.ps1 after offline-fixture-metrics.json is filled; strict validation requires dsp-fixture-metric-comparison.json.",
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
} | ConvertTo-Json -Depth 8
