param(
    [string]$BaseUrl = "http://localhost:6060",

    [string]$OutputRoot = "",

    [string]$Label = "",

    [int]$TimeoutSec = 10,

    [switch]$PlanOnly,

    [switch]$SkipCertificateCheck,

    [switch]$ContinueOnError
)

$ErrorActionPreference = "Stop"

function Get-RepoRoot {
    return (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
}

function Normalize-BaseUrl {
    param([Parameter(Mandatory = $true)][string]$Url)
    return $Url.TrimEnd("/")
}

function Enable-ModernTls {
    try {
        [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12
    }
    catch {
        # PowerShell 7+ uses platform defaults; older Windows PowerShell needs TLS 1.2 explicitly.
    }
}

function Enable-CertificateBypass {
    Enable-ModernTls

    if ((Get-Command Invoke-WebRequest).Parameters.ContainsKey("SkipCertificateCheck")) {
        return
    }

    [System.Net.ServicePointManager]::ServerCertificateValidationCallback = [System.Net.Security.RemoteCertificateValidationCallback]{
        param($sender, $certificate, $chain, $sslPolicyErrors)
        return $true
    }
}

function ConvertTo-SafeName {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return ""
    }

    $safe = $Value.Trim().ToLowerInvariant() -replace "[^a-z0-9._-]+", "-"
    $safe = $safe.Trim("-")
    if ($safe.Length -gt 64) {
        $safe = $safe.Substring(0, 64).Trim("-")
    }
    return $safe
}

function Get-CaptureEndpoints {
    return @(
        [ordered]@{
            id = "modernization-snapshot"
            path = "/api/dsp/modernization-snapshot"
            file = "modernization-snapshot.json"
            required = $true
            purpose = "One-call DSP modernization evidence bundle."
        },
        [ordered]@{
            id = "live-diagnostics"
            path = "/api/dsp/live-diagnostics"
            file = "live-diagnostics.json"
            required = $true
            purpose = "Current DSP readiness, constraints, next scenarios, and candidate gates."
        },
        [ordered]@{
            id = "benchmark-capture-manifest"
            path = "/api/dsp/benchmark-capture-manifest"
            file = "benchmark-capture-manifest.json"
            required = $true
            purpose = "Concrete G2 evidence checklist for the current DSP state."
        },
        [ordered]@{
            id = "benchmark-plan"
            path = "/api/dsp/benchmark-plan"
            file = "benchmark-plan.json"
            required = $true
            purpose = "Scenario catalog, required metrics, comparisons, and acceptance gates."
        },
        [ordered]@{
            id = "benchmark-metric-catalog"
            path = "/api/dsp/benchmark-metric-catalog"
            file = "benchmark-metric-catalog.json"
            required = $true
            purpose = "Metric direction, unit, safety class, and rationale catalog for fixture comparisons."
        },
        [ordered]@{
            id = "nr-condition"
            path = "/api/dsp/nr-condition"
            file = "nr-condition.json"
            required = $true
            purpose = "Smart NR scene condition and backend RX-chain runtime."
        },
        [ordered]@{
            id = "frontend-dsp-scene"
            path = "/api/radio/diagnostics/dsp-scene"
            file = "frontend-dsp-scene.json"
            required = $true
            purpose = "Frontend scene freshness, profile, and coherent signal evidence."
        },
        [ordered]@{
            id = "hardware-diagnostics"
            path = "/api/radio/diagnostics"
            file = "hardware-diagnostics.json"
            required = $true
            purpose = "Radio capabilities, feature surfaces, and diagnostics discoverability."
        },
        [ordered]@{
            id = "external-engine-candidates"
            path = "/api/dsp/external-engine-candidates"
            file = "external-engine-candidates.json"
            required = $true
            purpose = "Opt-in external DSP candidate blockers, risks, and benchmark gates."
        },
        [ordered]@{
            id = "state"
            path = "/api/state"
            file = "state.json"
            required = $false
            purpose = "General radio state snapshot for operator context."
        }
    )
}

function Write-JsonFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Value
    )

    $json = $Value | ConvertTo-Json -Depth 32
    Set-Content -LiteralPath $Path -Value $json -Encoding UTF8
}

$repoRoot = Get-RepoRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "captures\dsp-modernization"
}

$base = Normalize-BaseUrl $BaseUrl
if ($SkipCertificateCheck) {
    Enable-CertificateBypass
}
$endpoints = Get-CaptureEndpoints

if ($PlanOnly) {
    [ordered]@{
        tool = "capture-dsp-modernization-bundle"
        mode = "plan-only"
        baseUrl = $base
        outputRoot = $OutputRoot
        skipCertificateCheck = [bool]$SkipCertificateCheck
        endpoints = $endpoints
        example = "powershell -NoProfile -ExecutionPolicy Bypass -File tools\capture-dsp-modernization-bundle.ps1 -BaseUrl $base -Label g2-nr5-before"
        desktopExample = "powershell -NoProfile -ExecutionPolicy Bypass -File tools\capture-dsp-modernization-bundle.ps1 -BaseUrl https://localhost:6443 -SkipCertificateCheck -Label g2-nr5-before"
    } | ConvertTo-Json -Depth 16
    exit 0
}

$startedUtc = [DateTimeOffset]::UtcNow
$stamp = $startedUtc.ToString("yyyyMMddTHHmmssfffZ")
$safeLabel = ConvertTo-SafeName $Label
if ([string]::IsNullOrWhiteSpace($safeLabel)) {
    $bundleName = $stamp
}
else {
    $bundleName = "$stamp-$safeLabel"
}

$bundleDir = Join-Path $OutputRoot $bundleName
New-Item -ItemType Directory -Force -Path $bundleDir | Out-Null

$results = New-Object System.Collections.Generic.List[object]
$requiredFailures = New-Object System.Collections.Generic.List[string]

foreach ($endpoint in $endpoints) {
    $uri = "$base$($endpoint.path)"
    $target = Join-Path $bundleDir $endpoint.file
    $capturedUtc = [DateTimeOffset]::UtcNow

    try {
        $webRequestCommand = Get-Command Invoke-WebRequest
        $requestArgs = @{
            Uri = $uri
            Method = "Get"
            Headers = @{ Accept = "application/json" }
            TimeoutSec = $TimeoutSec
        }
        if ($SkipCertificateCheck -and $webRequestCommand.Parameters.ContainsKey("SkipCertificateCheck")) {
            $requestArgs["SkipCertificateCheck"] = $true
        }
        if ($webRequestCommand.Parameters.ContainsKey("UseBasicParsing")) {
            $requestArgs["UseBasicParsing"] = $true
        }

        $response = Invoke-WebRequest @requestArgs
        Set-Content -LiteralPath $target -Value $response.Content -Encoding UTF8

        $results.Add([ordered]@{
            id = $endpoint.id
            path = $endpoint.path
            uri = $uri
            file = $endpoint.file
            required = [bool]$endpoint.required
            ok = $true
            statusCode = [int]$response.StatusCode
            bytes = (Get-Item -LiteralPath $target).Length
            capturedUtc = $capturedUtc
            purpose = $endpoint.purpose
        })
    }
    catch {
        $message = $_.Exception.Message
        if ($endpoint.required) {
            $requiredFailures.Add($endpoint.id) | Out-Null
        }

        $results.Add([ordered]@{
            id = $endpoint.id
            path = $endpoint.path
            uri = $uri
            file = $endpoint.file
            required = [bool]$endpoint.required
            ok = $false
            statusCode = $null
            bytes = 0
            capturedUtc = $capturedUtc
            purpose = $endpoint.purpose
            error = $message
        })
    }
}

$completedUtc = [DateTimeOffset]::UtcNow
$index = [ordered]@{
    schemaVersion = 1
    tool = "capture-dsp-modernization-bundle"
    generatedUtc = $completedUtc
    startedUtc = $startedUtc
    completedUtc = $completedUtc
    baseUrl = $base
    skipCertificateCheck = [bool]$SkipCertificateCheck
    bundleName = $bundleName
    bundleDir = $bundleDir
    label = $Label
    ok = ($requiredFailures.Count -eq 0)
    requiredFailures = @($requiredFailures.ToArray())
    endpoints = @($results.ToArray())
    notes = @(
        "Read-only capture. No DSP or radio settings are modified.",
        "Save this folder with G2/on-air notes, audio renders, spectrum captures, and offline fixture metrics.",
        "No DSP behavior graduates until benchmark, G2, on-air, and cross-radio evidence gates pass."
    )
}

Write-JsonFile -Path (Join-Path $bundleDir "bundle-index.json") -Value $index

$certArg = if ($SkipCertificateCheck) { " -SkipCertificateCheck" } else { "" }

$readme = @"
# DSP Modernization Capture Bundle

- Generated UTC: $completedUtc
- Base URL: $base
- Label: $Label
- Required endpoint failures: $($requiredFailures.Count)

This bundle is read-only evidence for WDSP modernization review. It does not
approve changing DSP defaults. Keep it with matching audio renders, spectrum
captures, offline fixture metrics, and operator notes.

Next steps:
1. Generate an artifact scaffold:
   powershell -NoProfile -ExecutionPolicy Bypass -File tools\new-dsp-artifact-manifest.ps1 -BundleDir "$bundleDir"
   For G2 live acceptance review after the four matrix captures are present, regenerate with:
   powershell -NoProfile -ExecutionPolicy Bypass -File tools\new-dsp-artifact-manifest.ps1 -BundleDir "$bundleDir" -AcceptanceManifest -RequireLiveAcceptanceArtifacts -Force
   If benchmark-plan.requiredComparisons or benchmark-capture-manifest.requiredComparisons includes candidate-external-engine-opt-in, the scaffold marks artifacts/external-engine-bakeoff-report.json required by scope.
2. Audit Zeus NativeMethods against vendored WDSP source and the native binary export table:
   powershell -NoProfile -ExecutionPolicy Bypass -File tools\audit-wdsp-native-symbols.ps1 -ReportPath "$bundleDir\artifacts\wdsp-native-symbol-audit.json" -RequireBinaryExports
3. Audit packaged WDSP runtime artifacts and side-by-side native dependencies:
   powershell -NoProfile -ExecutionPolicy Bypass -File tools\audit-wdsp-runtime-artifacts.ps1 -ReportPath "$bundleDir\artifacts\wdsp-runtime-artifact-audit.json" -FailOnMissingWinX64Nr5
4. Summarize opt-in external DSP/ML candidates before any external-engine bakeoff:
   powershell -NoProfile -ExecutionPolicy Bypass -File tools\summarize-dsp-external-engine-candidates.ps1 -BundleDir "$bundleDir" -CandidatePath "$bundleDir\external-engine-candidates.json" -SnapshotPath "$bundleDir\modernization-snapshot.json" -ReportPath "$bundleDir\artifacts\external-engine-bakeoff-report.json" -FailOnUnsafe
5. Generate WDSP-backed offline fixture evidence, runtime audit, and fixture comparison for the offline-ready scenario matrix:
   powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-wdsp-fixture-matrix.ps1 -BundleDir "$bundleDir" -Force
   After artifact-manifest.json is finalized, add -ValidateBundle -RequireArtifactFiles to set acceptanceEvidenceReady from strict validation and artifact provenance.
6. Capture optional live runtime trends during scenario windows:
   powershell -NoProfile -ExecutionPolicy Bypass -File tools\watch-dsp-live-diagnostics.ps1 -BaseUrl $base$certArg -Samples 60 -IntervalMs 1000 -JsonlPath "$bundleDir\artifacts\live-diagnostics-trace.jsonl" -ReportPath "$bundleDir\artifacts\live-diagnostics-watch.json"
7. Or capture repeatable off, Thetis-parity, current-Zeus, and NR5/SPNR multi-scenario live diagnostics matrices:
   powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-live-diagnostics-matrix.ps1 -BaseUrl $base$certArg -BundleDir "$bundleDir" -ComparisonId off-baseline -IndexPath "$bundleDir\artifacts\live-diagnostics-trace-index.off-baseline.json" -ReportPath "$bundleDir\artifacts\live-diagnostics-matrix-report.off-baseline.json" -Samples 60 -IntervalMs 1000
   powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-live-diagnostics-matrix.ps1 -BaseUrl $base$certArg -BundleDir "$bundleDir" -ComparisonId thetis-parity -IndexPath "$bundleDir\artifacts\live-diagnostics-trace-index.thetis-parity.json" -ReportPath "$bundleDir\artifacts\live-diagnostics-matrix-report.thetis-parity.json" -Samples 60 -IntervalMs 1000
   powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-live-diagnostics-matrix.ps1 -BaseUrl $base$certArg -BundleDir "$bundleDir" -ComparisonId current-zeus -IndexPath "$bundleDir\artifacts\live-diagnostics-trace-index.baseline.json" -ReportPath "$bundleDir\artifacts\live-diagnostics-matrix-report.baseline.json" -Samples 60 -IntervalMs 1000
   powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-live-diagnostics-matrix.ps1 -BaseUrl $base$certArg -BundleDir "$bundleDir" -ComparisonId nr5-spnr -IndexPath "$bundleDir\artifacts\live-diagnostics-trace-index.candidate.json" -ReportPath "$bundleDir\artifacts\live-diagnostics-matrix-report.candidate.json" -Samples 60 -IntervalMs 1000
8. Summarize live tuning history after several NR5/NR2 attempts; rerun this after the watcher summaries and JSONL traces are finalized because schema v9 records per-trace SHA-256 provenance:
   powershell -NoProfile -ExecutionPolicy Bypass -File tools\summarize-dsp-live-diagnostics-history.ps1 -BundleDir "$bundleDir" -ReportPath "$bundleDir\artifacts\live-diagnostics-history.json"
9. Compare a candidate live trace against a baseline trace before accepting the window:
   powershell -NoProfile -ExecutionPolicy Bypass -File tools\compare-dsp-live-diagnostics-traces.ps1 -BundleDir "$bundleDir" -BaselinePath "$bundleDir\artifacts\live-diagnostics-baseline.jsonl" -CandidatePath "$bundleDir\artifacts\live-diagnostics-trace.jsonl" -ReportPath "$bundleDir\artifacts\live-diagnostics-trace-comparison.json" -FailOnRegression
10. Compare candidate matrix windows against baseline matrix windows before accepting the scenario set:
   powershell -NoProfile -ExecutionPolicy Bypass -File tools\compare-dsp-live-diagnostics-matrix.ps1 -BundleDir "$bundleDir" -BaselineIndexPath "$bundleDir\artifacts\live-diagnostics-trace-index.baseline.json" -CandidateIndexPath "$bundleDir\artifacts\live-diagnostics-trace-index.candidate.json" -BaselineComparisonId current-zeus -CandidateComparisonId nr5-spnr -ReportPath "$bundleDir\artifacts\live-diagnostics-trace-comparison.json" -FailOnRegression
   Trace comparison reports carry capture-readiness hard-gate and strict-preflight evidence through strict validation and validation triage, so stale frontend scene or Smart NR publishing failures stay visible as setup blockers instead of being confused with DSP regressions.
11. For acceptance review, regenerate artifact-manifest.json with the required live evidence set after the comparison report is captured:
   powershell -NoProfile -ExecutionPolicy Bypass -File tools\new-dsp-artifact-manifest.ps1 -BundleDir "$bundleDir" -AcceptanceManifest -RequireLiveAcceptanceArtifacts -Force
12. Fill the required files listed in artifact-manifest.template.json.
13. Copy or regenerate the scaffold as artifact-manifest.json, then validate with:
   powershell -NoProfile -ExecutionPolicy Bypass -File tools\validate-dsp-modernization-bundle.ps1 -BundleDir "$bundleDir" -RequireArtifactFiles
14. Summarize validation/provenance failures, evidence-gate readiness, acceptance stage readiness, and the next evidence actions for review or CI triage:
   powershell -NoProfile -ExecutionPolicy Bypass -File tools\summarize-dsp-modernization-validation-report.ps1 -BundleDir "$bundleDir" -FailOnIssues
"@
Set-Content -LiteralPath (Join-Path $bundleDir "README.md") -Value $readme -Encoding UTF8

if ($requiredFailures.Count -gt 0 -and -not $ContinueOnError) {
    Write-Error "Required DSP modernization capture endpoints failed: $($requiredFailures -join ', '). Bundle index was written to $bundleDir"
}

Write-Host "DSP modernization capture bundle written: $bundleDir"
