param(
    [string]$BaseUrl = "http://localhost:6060",

    [string]$OutputRoot = "",

    [string]$Label = "",

    [int]$TimeoutSec = 10,

    [switch]$PlanOnly,

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
$endpoints = Get-CaptureEndpoints

if ($PlanOnly) {
    [ordered]@{
        tool = "capture-dsp-modernization-bundle"
        mode = "plan-only"
        baseUrl = $base
        outputRoot = $OutputRoot
        endpoints = $endpoints
        example = "powershell -NoProfile -ExecutionPolicy Bypass -File tools\capture-dsp-modernization-bundle.ps1 -BaseUrl $base -Label g2-nr5-before"
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
        $response = Invoke-WebRequest -Uri $uri -Method Get -Headers @{ Accept = "application/json" } -TimeoutSec $TimeoutSec
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

$readme = @"
# DSP Modernization Capture Bundle

- Generated UTC: $completedUtc
- Base URL: $base
- Label: $Label
- Required endpoint failures: $($requiredFailures.Count)

This bundle is read-only evidence for WDSP modernization review. It does not
approve changing DSP defaults. Keep it with matching audio renders, spectrum
captures, offline fixture metrics, and operator notes.
"@
Set-Content -LiteralPath (Join-Path $bundleDir "README.md") -Value $readme -Encoding UTF8

if ($requiredFailures.Count -gt 0 -and -not $ContinueOnError) {
    Write-Error "Required DSP modernization capture endpoints failed: $($requiredFailures -join ', '). Bundle index was written to $bundleDir"
}

Write-Host "DSP modernization capture bundle written: $bundleDir"
