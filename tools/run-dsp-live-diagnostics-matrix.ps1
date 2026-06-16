param(
    [string]$BaseUrl = "http://localhost:6060",

    [string[]]$ScenarioIds = @(),

    [string]$ComparisonId = "current-zeus",

    [string]$Label = "",

    [int]$Samples = 15,

    [int]$IntervalMs = 1000,

    [int]$TimeoutSec = 5,

    [string]$BundleDir = "",

    [string]$OutputRoot = "",

    [string]$ReportPath = "",

    [string]$IndexPath = "",

    [string]$WatchScriptPath = "",

    [switch]$PlanOnly,

    [switch]$JsonOnly,

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

function ConvertTo-ComparisonId {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return "candidate-under-test"
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

function Read-JsonText {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Source
    )

    try {
        return $Text | ConvertFrom-Json
    }
    catch {
        throw "Failed to parse JSON from '$Source': $($_.Exception.Message)"
    }
}

function Read-JsonFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    return Read-JsonText -Text (Get-Content -LiteralPath $Path -Raw) -Source $Path
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

function Get-IntValue {
    param($Value)

    if ($null -eq $Value) {
        return 0
    }

    if ($Value -is [byte] -or $Value -is [int] -or $Value -is [long]) {
        return [int]$Value
    }

    $parsed = 0
    if ([int]::TryParse([string]$Value, [ref]$parsed)) {
        return $parsed
    }

    return 0
}

function Resolve-RelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $rootPath = (Resolve-Path -LiteralPath $Root).Path.TrimEnd("\", "/")
    $fullPath = (Resolve-Path -LiteralPath $Path).Path
    if ($fullPath.StartsWith($rootPath, [StringComparison]::OrdinalIgnoreCase)) {
        return $fullPath.Substring($rootPath.Length).TrimStart("\", "/") -replace "\\", "/"
    }

    return $fullPath -replace "\\", "/"
}

function ConvertTo-PortablePath {
    param(
        [string]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )

    if (-not [string]::IsNullOrWhiteSpace($Root) -and (Test-Path -LiteralPath $Path)) {
        return Resolve-RelativePath -Root $Root -Path $Path
    }

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path -replace "\\", "/"
    }

    return $Path -replace "\\", "/"
}

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $null
    }

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Invoke-JsonGet {
    param(
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][int]$RequestTimeoutSec,
        [switch]$SkipCertificateCheck
    )

    try {
        $webRequestCommand = Get-Command Invoke-WebRequest
        $requestArgs = @{
            Uri = $Uri
            TimeoutSec = $RequestTimeoutSec
        }
        if ($webRequestCommand.Parameters.ContainsKey("UseBasicParsing")) {
            $requestArgs["UseBasicParsing"] = $true
        }
        if ($SkipCertificateCheck -and $webRequestCommand.Parameters.ContainsKey("SkipCertificateCheck")) {
            $requestArgs["SkipCertificateCheck"] = $true
        }

        $response = Invoke-WebRequest @requestArgs
        if ($response.StatusCode -lt 200 -or $response.StatusCode -gt 299) {
            throw "HTTP $($response.StatusCode)"
        }

        return Read-JsonText -Text ([string]$response.Content) -Source $Uri
    }
    catch {
        throw "GET $Uri failed: $($_.Exception.Message)"
    }
}

function Get-ManifestScenarioIds {
    param(
        [Parameter(Mandatory = $true)][string]$Base,
        [Parameter(Mandatory = $true)][int]$RequestTimeoutSec,
        [switch]$SkipCertificateCheck
    )

    $manifest = Invoke-JsonGet -Uri "$Base/api/dsp/benchmark-capture-manifest" -RequestTimeoutSec $RequestTimeoutSec -SkipCertificateCheck:$SkipCertificateCheck
    $ids = New-Object System.Collections.Generic.List[string]
    foreach ($scenarioId in (Get-JsonArray $manifest "scenarioIds")) {
        $value = [string]$scenarioId
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            $ids.Add($value) | Out-Null
        }
    }

    return @($ids.ToArray() | Select-Object -Unique)
}

function Expand-ScenarioIds {
    param([string[]]$Values)

    $ids = New-Object System.Collections.Generic.List[string]
    foreach ($item in @($Values)) {
        foreach ($part in ([string]$item -split "[,;]")) {
            $value = $part.Trim()
            if (-not [string]::IsNullOrWhiteSpace($value)) {
                $ids.Add($value) | Out-Null
            }
        }
    }

    return @($ids.ToArray())
}

function Invoke-Watch {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [Parameter(Mandatory = $true)][string]$Base,
        [Parameter(Mandatory = $true)][string]$ScenarioId,
        [Parameter(Mandatory = $true)][string]$Comparison,
        [string]$RunLabel,
        [Parameter(Mandatory = $true)][int]$SampleCount,
        [Parameter(Mandatory = $true)][int]$DelayMs,
        [Parameter(Mandatory = $true)][int]$RequestTimeoutSec,
        [Parameter(Mandatory = $true)][string]$JsonlPath,
        [Parameter(Mandatory = $true)][string]$SummaryPath,
        [switch]$SkipCertificateCheck
    )

    $labelParts = @($ScenarioId, $Comparison, $RunLabel) |
        Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }
    $watchLabel = ($labelParts -join "-")

    $watchArgs = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", $ScriptPath,
        "-BaseUrl", $Base,
        "-Samples", ([string]$SampleCount),
        "-IntervalMs", ([string]$DelayMs),
        "-TimeoutSec", ([string]$RequestTimeoutSec),
        "-Label", $watchLabel,
        "-ScenarioId", $ScenarioId,
        "-ComparisonId", $Comparison,
        "-ReportPath", $SummaryPath,
        "-JsonlPath", $JsonlPath,
        "-JsonOnly"
    )
    if ($SkipCertificateCheck) {
        $watchArgs += "-SkipCertificateCheck"
    }

    $output = & powershell @watchArgs 2>&1
    $exitCode = $LASTEXITCODE

    if ($exitCode -ne 0) {
        return [ordered]@{
            ok = $false
            exitCode = $exitCode
            error = ($output | Out-String).Trim()
            report = $null
        }
    }

    $text = ($output | Out-String).Trim()
    $report = if ([string]::IsNullOrWhiteSpace($text)) { Read-JsonFile $SummaryPath } else { Read-JsonText -Text $text -Source $SummaryPath }

    return [ordered]@{
        ok = $true
        exitCode = 0
        error = $null
        report = $report
    }
}

if ($Samples -lt 1) {
    throw "Samples must be at least 1."
}
if ($IntervalMs -lt 0) {
    throw "IntervalMs must be greater than or equal to 0."
}
if ($TimeoutSec -lt 1) {
    throw "TimeoutSec must be at least 1."
}

$repoRoot = Get-RepoRoot
$base = Normalize-BaseUrl $BaseUrl
if ($SkipCertificateCheck) {
    Enable-CertificateBypass
}
$comparison = ConvertTo-ComparisonId $ComparisonId
$comparisons = @($comparison)
$safeComparison = ConvertTo-SafeName $comparison
$safeLabel = ConvertTo-SafeName $Label

if ([string]::IsNullOrWhiteSpace($WatchScriptPath)) {
    $WatchScriptPath = Join-Path $repoRoot "tools\watch-dsp-live-diagnostics.ps1"
}
$resolvedWatchScript = (Resolve-Path -LiteralPath $WatchScriptPath).Path

if ($PlanOnly) {
    [ordered]@{
        schemaVersion = 1
        tool = "run-dsp-live-diagnostics-matrix"
        mode = "plan-only"
        baseUrl = $base
        endpoint = "$base/api/dsp/live-diagnostics"
        captureManifestEndpoint = "$base/api/dsp/benchmark-capture-manifest"
        scenarios = if ($ScenarioIds.Count -gt 0) { @($ScenarioIds) } else { @("from /api/dsp/benchmark-capture-manifest") }
        comparisonId = $comparison
        comparisonIds = @($comparisons)
        samples = $Samples
        intervalMs = $IntervalMs
        skipCertificateCheck = [bool]$SkipCertificateCheck
        outputs = @(
            "JSONL trace per scenario",
            "watch-dsp-live-diagnostics summary per scenario",
            "bundle-compatible trace index JSON",
            "matrix summary JSON"
        )
        example = "powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-live-diagnostics-matrix.ps1 -BundleDir captures\dsp-modernization\<timestamp> -ComparisonId current-zeus -IndexPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-trace-index.baseline.json -ReportPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-matrix-report.baseline.json -Samples 60 -IntervalMs 1000"
        candidateExample = "powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-live-diagnostics-matrix.ps1 -BundleDir captures\dsp-modernization\<timestamp> -ComparisonId nr5-spnr -IndexPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-trace-index.candidate.json -ReportPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-matrix-report.candidate.json -Samples 60 -IntervalMs 1000"
        desktopExample = "powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-live-diagnostics-matrix.ps1 -BaseUrl https://localhost:6443 -SkipCertificateCheck -BundleDir captures\dsp-modernization\<timestamp> -ComparisonId current-zeus -IndexPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-trace-index.baseline.json -ReportPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-matrix-report.baseline.json -Samples 60 -IntervalMs 500"
        notes = @(
            "Read-only: delegates to watch-dsp-live-diagnostics.ps1, which only calls GET /api/dsp/live-diagnostics.",
            "Use one ComparisonId per invocation; switch DSP state between baseline and candidate windows and run the tool again.",
            "When BundleDir is reused, pass separate IndexPath and ReportPath values for baseline and candidate runs to avoid overwriting evidence.",
            "The trace index is suitable for artifact-manifest entries with kind=trace."
        )
    } | ConvertTo-Json -Depth 16
    exit 0
}

$scenarioList = New-Object System.Collections.Generic.List[string]
foreach ($scenarioId in (Expand-ScenarioIds $ScenarioIds)) {
    $value = [string]$scenarioId
    if (-not [string]::IsNullOrWhiteSpace($value)) {
        $scenarioList.Add($value) | Out-Null
    }
}

if ($scenarioList.Count -eq 0) {
    foreach ($scenarioId in (Get-ManifestScenarioIds -Base $base -RequestTimeoutSec $TimeoutSec -SkipCertificateCheck:$SkipCertificateCheck)) {
        $scenarioList.Add([string]$scenarioId) | Out-Null
    }
}

$scenarios = @($scenarioList.ToArray() | Select-Object -Unique)
if ($scenarios.Count -eq 0) {
    throw "No scenario IDs were supplied and none could be read from /api/dsp/benchmark-capture-manifest."
}

$startedUtc = [DateTimeOffset]::UtcNow
$stamp = $startedUtc.ToString("yyyyMMddTHHmmssfffZ")

$bundlePath = ""
if (-not [string]::IsNullOrWhiteSpace($BundleDir)) {
    $bundlePath = (Resolve-Path -LiteralPath $BundleDir).Path
    $outputBase = Join-Path $bundlePath "artifacts\live-diagnostics-traces"
    if ([string]::IsNullOrWhiteSpace($IndexPath)) {
        $IndexPath = Join-Path $bundlePath "artifacts\live-diagnostics-trace-index.json"
    }
    if ([string]::IsNullOrWhiteSpace($ReportPath)) {
        $ReportPath = Join-Path $bundlePath "artifacts\live-diagnostics-matrix-report.json"
    }
}
else {
    if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
        $OutputRoot = Join-Path $repoRoot "captures\dsp-live-diagnostics-matrix"
    }

    $captureName = if ([string]::IsNullOrWhiteSpace($safeLabel)) { "$stamp-$safeComparison" } else { "$stamp-$safeComparison-$safeLabel" }
    $outputBase = Join-Path $OutputRoot $captureName
    if ([string]::IsNullOrWhiteSpace($IndexPath)) {
        $IndexPath = Join-Path $outputBase "live-diagnostics-trace-index.json"
    }
    if ([string]::IsNullOrWhiteSpace($ReportPath)) {
        $ReportPath = Join-Path $outputBase "live-diagnostics-matrix-report.json"
    }
}

New-Item -ItemType Directory -Force -Path $outputBase | Out-Null

$runs = New-Object System.Collections.Generic.List[object]
$files = New-Object System.Collections.Generic.List[object]

foreach ($scenarioId in $scenarios) {
    $safeScenario = ConvertTo-SafeName $scenarioId
    if ([string]::IsNullOrWhiteSpace($safeScenario)) {
        $safeScenario = "scenario"
    }

    foreach ($comparison in $comparisons) {
        $safeCurrentComparison = ConvertTo-SafeName $comparison
        if ([string]::IsNullOrWhiteSpace($safeCurrentComparison)) {
            $safeCurrentComparison = "comparison"
        }

        $scenarioDir = Join-Path (Join-Path $outputBase $safeScenario) $safeCurrentComparison
        New-Item -ItemType Directory -Force -Path $scenarioDir | Out-Null

        $jsonlPath = Join-Path $scenarioDir "live-diagnostics-trace.jsonl"
        $summaryPath = Join-Path $scenarioDir "live-diagnostics-watch.json"
        $watch = Invoke-Watch `
            -ScriptPath $resolvedWatchScript `
            -Base $base `
            -ScenarioId $scenarioId `
            -Comparison $comparison `
            -RunLabel $Label `
            -SampleCount $Samples `
            -DelayMs $IntervalMs `
            -RequestTimeoutSec $TimeoutSec `
            -JsonlPath $jsonlPath `
            -SummaryPath $summaryPath `
            -SkipCertificateCheck:$SkipCertificateCheck

        $watchOk = Test-Truthy $watch.ok
        if ((-not $watchOk) -and -not $ContinueOnError) {
            throw "Live diagnostics watch failed for scenario '$scenarioId' comparison '$comparison': $($watch.error)"
        }

        $report = $watch.report
        $readyTrace = if ($null -eq $report) { $false } else { Test-Truthy (Get-JsonValue $report "readyForBenchmarkTrace") }
        $trendStatus = if ($null -eq $report) { "watch-failed" } else { [string](Get-JsonValue $report "trendStatus") }
        $okSamples = if ($null -eq $report) { 0 } else { [int](Get-JsonValue $report "okSampleCount") }
        $failedSamples = if ($null -eq $report) { $Samples } else { [int](Get-JsonValue $report "failedSampleCount") }
        $hardBlockers = if ($null -eq $report) { 0 } else { [int](Get-JsonValue $report "hardBlockerSampleCount") }
        $nr5Weak = if ($null -eq $report) { $null } else { Get-JsonValue $report "nr5WeakSignalWatch" }
        $nr5WeakInputs = Get-IntValue (Get-JsonValue $nr5Weak "weakInputSampleCount")
        $nr5WeakRecovered = Get-IntValue (Get-JsonValue $nr5Weak "weakRecoveredSampleCount")
        $nr5WeakDropouts = Get-IntValue (Get-JsonValue $nr5Weak "weakDropoutSampleCount")
        $nr5HotMakeup = Get-IntValue (Get-JsonValue $nr5Weak "hotMakeupSampleCount")

        $relativeRoot = if (-not [string]::IsNullOrWhiteSpace($bundlePath)) { $bundlePath } else { Split-Path -Parent $IndexPath }
        $jsonlRelative = ConvertTo-PortablePath -Root $relativeRoot -Path $jsonlPath
        $summaryRelative = ConvertTo-PortablePath -Root $relativeRoot -Path $summaryPath

        $files.Add([ordered]@{
            path = $jsonlRelative
            kind = "diagnostics-jsonl"
            scenarioId = $scenarioId
            comparisonId = $comparison
            label = $Label
            sampleCount = $Samples
            intervalMs = $IntervalMs
            summaryPath = $summaryRelative
            sha256 = Get-FileSha256 $jsonlPath
            summarySha256 = Get-FileSha256 $summaryPath
            nr5WeakInputSampleCount = $nr5WeakInputs
            nr5WeakRecoveredSampleCount = $nr5WeakRecovered
            nr5WeakDropoutSampleCount = $nr5WeakDropouts
            nr5HotMakeupSampleCount = $nr5HotMakeup
        }) | Out-Null

        $runs.Add([ordered]@{
            scenarioId = $scenarioId
            comparisonId = $comparison
            label = $Label
            ok = $watchOk
            exitCode = [int]$watch.exitCode
            error = $watch.error
            jsonlPath = $jsonlRelative
            reportPath = $summaryRelative
            trendStatus = $trendStatus
            readyForBenchmarkTrace = $readyTrace
            okSampleCount = $okSamples
            failedSampleCount = $failedSamples
            hardBlockerSampleCount = $hardBlockers
            nr5WeakInputSampleCount = $nr5WeakInputs
            nr5WeakRecoveredSampleCount = $nr5WeakRecovered
            nr5WeakDropoutSampleCount = $nr5WeakDropouts
            nr5HotMakeupSampleCount = $nr5HotMakeup
        }) | Out-Null
    }
}

$completedUtc = [DateTimeOffset]::UtcNow
$runArray = @($runs.ToArray())
$failedRunCount = @($runArray | Where-Object { -not (Test-Truthy $_.ok) }).Count
$notReadyTraceCount = @($runArray | Where-Object { -not (Test-Truthy $_.readyForBenchmarkTrace) }).Count
$hardBlockerRunCount = @($runArray | Where-Object { [int]$_.hardBlockerSampleCount -gt 0 }).Count
$nr5WeakInputSampleCount = 0
$nr5WeakRecoveredSampleCount = 0
$nr5WeakDropoutSampleCount = 0
$nr5HotMakeupSampleCount = 0
foreach ($run in $runArray) {
    $nr5WeakInputSampleCount += Get-IntValue $run.nr5WeakInputSampleCount
    $nr5WeakRecoveredSampleCount += Get-IntValue $run.nr5WeakRecoveredSampleCount
    $nr5WeakDropoutSampleCount += Get-IntValue $run.nr5WeakDropoutSampleCount
    $nr5HotMakeupSampleCount += Get-IntValue $run.nr5HotMakeupSampleCount
}

$recommendations = New-Object System.Collections.Generic.List[string]
if ($failedRunCount -gt 0) {
    $recommendations.Add("Repeat failed scenario windows before using this matrix as live DSP evidence.") | Out-Null
}
if ($hardBlockerRunCount -gt 0) {
    $recommendations.Add("Resolve hard live-diagnostics blockers before candidate acceptance review.") | Out-Null
}
if ($notReadyTraceCount -gt 0) {
    $recommendations.Add("Pair not-ready traces with operator notes; they are useful preflight evidence but not acceptance evidence.") | Out-Null
}
if ($nr5WeakDropoutSampleCount -gt 0) {
    $recommendations.Add("NR5 weak-input dropouts appeared in the matrix; compare these windows against baseline traces before tuning recovery/makeup further.") | Out-Null
}
if ($nr5HotMakeupSampleCount -gt 0) {
    $recommendations.Add("NR5 hot-makeup samples appeared in the matrix; inspect the watch summaries before changing recovery attack/release.") | Out-Null
}
if ($recommendations.Count -eq 0) {
    $recommendations.Add("Store this trace index with the modernization bundle and compare candidate windows against baseline traces before changing DSP defaults.") | Out-Null
}

$index = [ordered]@{
    schemaVersion = 1
    tool = "run-dsp-live-diagnostics-matrix"
    artifactId = "live-diagnostics-trace-index"
    generatedUtc = $completedUtc
    baseUrl = $base
    comparisonId = $comparison
    comparisonIds = @($comparisons)
    label = $Label
    scenarioIds = @($scenarios)
    files = @($files.ToArray())
}

Write-JsonFile -Path $IndexPath -Value $index

$reportObject = [ordered]@{
    schemaVersion = 1
    tool = "run-dsp-live-diagnostics-matrix"
    generatedUtc = $completedUtc
    startedUtc = $startedUtc
    completedUtc = $completedUtc
    durationMs = [int]($completedUtc - $startedUtc).TotalMilliseconds
    baseUrl = $base
    comparisonId = $comparison
    comparisonIds = @($comparisons)
    label = $Label
    samples = $Samples
    intervalMs = $IntervalMs
    skipCertificateCheck = [bool]$SkipCertificateCheck
    scenarioCount = $scenarios.Count
    failedRunCount = $failedRunCount
    notReadyTraceCount = $notReadyTraceCount
    hardBlockerRunCount = $hardBlockerRunCount
    nr5WeakInputSampleCount = $nr5WeakInputSampleCount
    nr5WeakRecoveredSampleCount = $nr5WeakRecoveredSampleCount
    nr5WeakDropoutSampleCount = $nr5WeakDropoutSampleCount
    nr5HotMakeupSampleCount = $nr5HotMakeupSampleCount
    collectionReady = ($failedRunCount -eq 0)
    acceptanceReady = ($failedRunCount -eq 0 -and $notReadyTraceCount -eq 0 -and $hardBlockerRunCount -eq 0)
    indexPath = if (-not [string]::IsNullOrWhiteSpace($bundlePath)) { Resolve-RelativePath -Root $bundlePath -Path $IndexPath } else { $IndexPath }
    indexSha256 = Get-FileSha256 $IndexPath
    runs = @($runArray)
    recommendations = @($recommendations.ToArray())
}

Write-JsonFile -Path $ReportPath -Value $reportObject

if ($JsonOnly) {
    $reportObject | ConvertTo-Json -Depth 64
}
else {
    Write-Host "DSP live diagnostics matrix written: $ReportPath"
    Write-Host "Trace index: $IndexPath"
    Write-Host "Scenarios: $($scenarios.Count), Failed: $failedRunCount, Not ready: $notReadyTraceCount"
}

if ($failedRunCount -gt 0 -and -not $ContinueOnError) {
    exit 1
}
