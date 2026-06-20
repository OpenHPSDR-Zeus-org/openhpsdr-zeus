param(
    [string]$BaseUrl = "https://127.0.0.1:6443",

    [ValidateSet("disabled", "enabled")]
    [string]$Mode = "disabled",

    [int]$Samples = 12,

    [int]$IntervalMs = 500,

    [int]$TimeoutSec = 5,

    [string]$OutputPath = "",

    [switch]$SkipCertificateCheck,

    [switch]$RequireLiveReady,

    [switch]$PlanOnly,

    [switch]$JsonOnly
)

$ErrorActionPreference = "Stop"

function Normalize-BaseUrl {
    param([Parameter(Mandatory = $true)][string]$Url)
    return $Url.TrimEnd("/")
}

function Get-Timestamp {
    return (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmssZ")
}

function Enable-ModernTls {
    try {
        [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12
    }
    catch {
    }
}

function Enable-CertificateBypass {
    Enable-ModernTls

    if ((Get-Command Invoke-RestMethod).Parameters.ContainsKey("SkipCertificateCheck")) {
        return
    }

    [System.Net.ServicePointManager]::ServerCertificateValidationCallback = [System.Net.Security.RemoteCertificateValidationCallback]{
        param($sender, $certificate, $chain, $sslPolicyErrors)
        return $true
    }
}

function New-RestArgs {
    param([Parameter(Mandatory = $true)][string]$Url)

    $args = @{
        Uri = $Url
        Method = "GET"
        TimeoutSec = $TimeoutSec
    }

    if ($SkipCertificateCheck -and (Get-Command Invoke-RestMethod).Parameters.ContainsKey("SkipCertificateCheck")) {
        $args["SkipCertificateCheck"] = $true
    }

    return $args
}

function Invoke-JsonGet {
    param([Parameter(Mandatory = $true)][string]$Url)

    $args = New-RestArgs -Url $Url
    return Invoke-RestMethod @args
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

    return $null
}

function Get-NestedValue {
    param(
        $Object,
        [Parameter(Mandatory = $true)][string[]]$Path
    )

    $current = $Object
    foreach ($part in $Path) {
        $current = Get-JsonValue $current $part
        if ($null -eq $current) {
            return $null
        }
    }

    return $current
}

function Test-Truthy {
    param($Value)

    if ($null -eq $Value) {
        return $false
    }

    if ($Value -is [bool]) {
        return [bool]$Value
    }

    $text = ([string]$Value).Trim().ToLowerInvariant()
    return $text -in @("true", "1", "yes", "on", "enabled", "active")
}

function Invoke-LiveDiagnosticsApi {
    param([Parameter(Mandatory = $true)][string]$Base)

    try {
        return Invoke-JsonGet -Url "$Base/api/dsp/live-diagnostics"
    }
    catch {
        throw "Live diagnostics endpoint is not reachable at $Base/api/dsp/live-diagnostics. Open the updated frontend scene and connect the G2 before PureSignal bench capture. $($_.Exception.Message)"
    }
}

function Get-LiveReadiness {
    param($Diagnostics)

    $runtime = Get-JsonValue $Diagnostics "runtimeEvidence"
    $status = [string](Get-JsonValue $Diagnostics "status")
    $runtimeStatus = [string](Get-JsonValue $runtime "status")
    $readyForLiveBenchmark = Test-Truthy (Get-JsonValue $Diagnostics "readyForLiveBenchmark")
    $wdspActive = Test-Truthy (Get-JsonValue $Diagnostics "wdspActive")
    $frontendSceneFresh = Test-Truthy (Get-JsonValue $Diagnostics "frontendSceneFresh")
    $runtimeFresh = [string]::Equals($runtimeStatus, "fresh", [StringComparison]::OrdinalIgnoreCase)

    $failureReasons = New-Object System.Collections.Generic.List[string]
    if (-not $readyForLiveBenchmark) { $failureReasons.Add("readyForLiveBenchmark=false") | Out-Null }
    if (-not $wdspActive) { $failureReasons.Add("wdspActive=false") | Out-Null }
    if (-not $frontendSceneFresh) { $failureReasons.Add("frontendSceneFresh=false") | Out-Null }
    if (-not $runtimeFresh) { $failureReasons.Add("runtimeEvidence.status=$runtimeStatus") | Out-Null }

    return [ordered]@{
        ready = ($readyForLiveBenchmark -and $wdspActive -and $frontendSceneFresh -and $runtimeFresh)
        status = $status
        readyForLiveBenchmark = $readyForLiveBenchmark
        wdspActive = $wdspActive
        frontendSceneFresh = $frontendSceneFresh
        runtimeStatus = $runtimeStatus
        radioVfoHz = Get-JsonValue $Diagnostics "radioVfoHz"
        radioMode = [string](Get-JsonValue $Diagnostics "radioMode")
        generatedUtc = [string](Get-JsonValue $Diagnostics "generatedUtc")
        failureReasons = @($failureReasons.ToArray())
    }
}

function Get-NumberOrNull {
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

function Get-HealthStable {
    param(
        [string]$ModeName,
        [string]$Health,
        [bool]$Enabled
    )

    $normalized = $Health.Trim().ToLowerInvariant()
    if ([string]::Equals($ModeName, "disabled", [StringComparison]::OrdinalIgnoreCase)) {
        return (-not $Enabled) -and ($normalized -eq "off" -or [string]::IsNullOrWhiteSpace($normalized))
    }

    if (-not $Enabled) {
        return $false
    }

    return $normalized -in @(
        "centered-correcting",
        "correcting-usable",
        "collecting-usable-feedback"
    )
}

function Test-ActiveTxStatus {
    param([string]$Status)

    if ([string]::IsNullOrWhiteSpace($Status)) {
        return $false
    }

    $normalized = $Status.Trim().ToLowerInvariant()
    return $normalized -notin @("idle", "inactive", "off", "disabled", "none")
}

function Get-SampleSummary {
    param(
        [Parameter(Mandatory = $true)]$HardwareDiagnostics,
        [Parameter(Mandatory = $true)]$TxDiagnostics
    )

    $pureSignal = Get-JsonValue $HardwareDiagnostics "pureSignal"
    $stage = Get-JsonValue $TxDiagnostics "stage"
    $density = Get-JsonValue $stage "density"
    $txPlugins = Get-JsonValue $TxDiagnostics "txPlugins"
    $vstEngine = Get-JsonValue $TxDiagnostics "vstEngine"
    $rxVstEngine = Get-JsonValue $TxDiagnostics "rxVstEngine"

    $enabled = Test-Truthy (Get-JsonValue $pureSignal "enabled")
    $health = [string](Get-JsonValue $pureSignal "healthStatus")
    $outPkDbfs = Get-NumberOrNull (Get-JsonValue $stage "outPkDbfs")
    $densityStatus = [string](Get-JsonValue $density "status")
    $txMonitorEnabled = Test-Truthy (Get-JsonValue $pureSignal "monitorEnabled")
    $txPluginActive = $false
    if ($null -ne $txPlugins) {
        $masterBypassed = Get-JsonValue $txPlugins "masterBypassed"
        if ($null -ne $masterBypassed) {
            $txPluginActive = -not (Test-Truthy $masterBypassed)
        }
    }
    $vstActive = Test-Truthy (Get-JsonValue $vstEngine "active")
    $rxVstActive = Test-Truthy (Get-JsonValue $rxVstEngine "active")
    $stageStatus = [string](Get-JsonValue $stage "status")
    $txAudioPathStatus = [string](Get-NestedValue $TxDiagnostics @("audioPath", "status"))
    $txEgressStatus = [string](Get-NestedValue $TxDiagnostics @("egress", "status"))
    $txPathActive = (Test-ActiveTxStatus $stageStatus) -or
        (Test-ActiveTxStatus $txAudioPathStatus) -or
        (Test-ActiveTxStatus $txEgressStatus) -or
        ($null -ne $outPkDbfs)
    $clipRisk = [string]::Equals($densityStatus, "clip-risk", [StringComparison]::OrdinalIgnoreCase) -or
        ($null -ne $outPkDbfs -and [double]$outPkDbfs -gt -0.5)

    return [ordered]@{
        sampledUtc = (Get-Date).ToUniversalTime().ToString("o")
        pureSignalEnabled = $enabled
        pureSignalHealthStatus = $health
        feedbackLevelRaw = Get-NumberOrNull (Get-JsonValue $pureSignal "feedbackLevelRaw")
        feedbackSource = [string](Get-JsonValue $pureSignal "feedbackSource")
        correcting = Test-Truthy (Get-JsonValue $pureSignal "correcting")
        calibrationStalled = Test-Truthy (Get-JsonValue $pureSignal "calibrationStalled")
        txMonitorEnabled = $txMonitorEnabled
        txPluginActive = $txPluginActive
        vstEngineActive = $vstActive
        rxVstEngineActive = $rxVstActive
        txPathActive = $txPathActive
        txMonitorCouplingFlag = ($txMonitorEnabled -or ($txPathActive -and ($txPluginActive -or $vstActive)))
        stageStatus = $stageStatus
        densityStatus = $densityStatus
        outPkDbfs = $outPkDbfs
        clipRisk = $clipRisk
        txAudioPathStatus = $txAudioPathStatus
        txEgressStatus = $txEgressStatus
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

if ($SkipCertificateCheck) {
    Enable-CertificateBypass
}

$base = Normalize-BaseUrl $BaseUrl
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
    $OutputPath = Join-Path (Join-Path $repoRoot "tmp\wdsp-v2-live") ("puresignal-$Mode-trace-" + (Get-Timestamp) + ".json")
}

$plan = [ordered]@{
    schemaVersion = 1
    tool = "capture-dsp-puresignal-bench-trace"
    mode = "plan-only"
    baseUrl = $base
    pureSignalMode = $Mode
    samples = $Samples
    intervalMs = $IntervalMs
    outputPath = $OutputPath
    endpoints = @(
        "/api/dsp/live-diagnostics",
        "/api/radio/diagnostics",
        "/api/tx/diag"
    )
    requiresLiveReady = [bool]$RequireLiveReady
    safety = @(
        "Read-only helper: performs GET requests only.",
        "Does not key MOX/TUN, does not start two-tone, and does not toggle PureSignal.",
        "When -RequireLiveReady is used, refuses capture unless /api/dsp/live-diagnostics is benchmark-ready.",
        "Use one disabled trace and one enabled trace with summarize-dsp-puresignal-bench.ps1 before TX/PureSignal graduation.",
        "This trace does not approve default DSP behavior changes."
    )
}

if ($PlanOnly) {
    $plan | ConvertTo-Json -Depth 8
    exit 0
}

$liveReadinessBefore = $null
if ($RequireLiveReady) {
    $liveReadinessBefore = Get-LiveReadiness (Invoke-LiveDiagnosticsApi -Base $base)
    if (-not [bool]$liveReadinessBefore.ready) {
        $reason = if ($liveReadinessBefore.failureReasons.Count -gt 0) {
            [string]::Join(", ", @($liveReadinessBefore.failureReasons))
        }
        else {
            "unknown"
        }

        throw "Refusing PureSignal bench capture because live diagnostics are not benchmark-ready ($reason). Open the frontend scene, connect the G2, and wait for WDSP/runtime evidence before capturing disabled/enabled PureSignal traces."
    }
}

$sampleRecords = New-Object System.Collections.Generic.List[object]
for ($i = 0; $i -lt $Samples; $i++) {
    $hardware = Invoke-JsonGet -Url "$base/api/radio/diagnostics"
    $tx = Invoke-JsonGet -Url "$base/api/tx/diag"
    $sampleRecords.Add([pscustomobject](Get-SampleSummary -HardwareDiagnostics $hardware -TxDiagnostics $tx)) | Out-Null
    if ($i -lt ($Samples - 1) -and $IntervalMs -gt 0) {
        Start-Sleep -Milliseconds $IntervalMs
    }
}

$sampleArray = @($sampleRecords.ToArray())
$enabledValues = @($sampleArray | ForEach-Object { [bool]$_.pureSignalEnabled })
$expectedEnabled = [string]::Equals($Mode, "enabled", [StringComparison]::OrdinalIgnoreCase)
$modeConsistentSampleCount = @($sampleArray | Where-Object { ([bool]$_.pureSignalEnabled) -eq $expectedEnabled }).Count
$modeMismatchSampleCount = $sampleArray.Count - $modeConsistentSampleCount
$modeConsistent = ($sampleArray.Count -gt 0 -and $modeMismatchSampleCount -eq 0)
$healthStableCount = @($sampleArray | Where-Object { Get-HealthStable -ModeName $Mode -Health ([string]$_.pureSignalHealthStatus) -Enabled ([bool]$_.pureSignalEnabled) }).Count
$feedbackStability = if ($sampleArray.Count -gt 0) {
    [Math]::Round($healthStableCount / [double]$sampleArray.Count, 6)
}
else {
    0.0
}
$couplingCount = @($sampleArray | Where-Object { [bool]$_.txMonitorCouplingFlag }).Count
$txMonitorCoupling = if ($sampleArray.Count -gt 0) {
    [Math]::Round($couplingCount / [double]$sampleArray.Count, 6)
}
else {
    0.0
}
$clippingCount = @($sampleArray | Where-Object { [bool]$_.clipRisk }).Count
$outPeakValues = @($sampleArray | ForEach-Object { $_.outPkDbfs } | Where-Object { $null -ne $_ })
$outPeak = if ($outPeakValues.Count -gt 0) {
    [Math]::Round([double]($outPeakValues | Measure-Object -Maximum).Maximum, 6)
}
else {
    $null
}
$actualEnabled = if ($enabledValues.Count -gt 0) {
    @($enabledValues | Where-Object { $_ }).Count -ge [Math]::Ceiling($enabledValues.Count / 2.0)
}
else {
    [string]::Equals($Mode, "enabled", [StringComparison]::OrdinalIgnoreCase)
}
$healthCounts = @(
    $sampleArray |
        Group-Object -Property pureSignalHealthStatus |
        ForEach-Object {
            [ordered]@{
                name = if ([string]::IsNullOrWhiteSpace([string]$_.Name)) { "unknown" } else { [string]$_.Name }
                count = $_.Count
            }
        }
)

$trace = [ordered]@{
    schemaVersion = 1
    tool = "capture-dsp-puresignal-bench-trace"
    generatedUtc = (Get-Date).ToUniversalTime().ToString("o")
    mode = $Mode
    baseUrl = $base
    sampleCount = $sampleArray.Count
    requiresLiveReady = [bool]$RequireLiveReady
    liveReadinessBefore = $liveReadinessBefore
    operatorMutation = $false
    defaultBehaviorChanged = $false
    pureSignal = [ordered]@{
        pureSignalEnabled = $actualEnabled
        expectedEnabled = $expectedEnabled
        modeConsistent = $modeConsistent
        modeConsistentSampleCount = $modeConsistentSampleCount
        modeMismatchSampleCount = $modeMismatchSampleCount
        bypassState = if ($actualEnabled) { "enabled-feedback-correction" } else { "disabled-bypass" }
        feedbackStability = $feedbackStability
        txMonitorCoupling = $txMonitorCoupling
        clippingCount = $clippingCount
        txOutputPeakDbfs = $outPeak
        healthStatusCounts = @($healthCounts)
        expectedMode = $Mode
    }
    samples = $sampleArray
    notes = @(
        "Read-only trace for summarize-dsp-puresignal-bench.ps1.",
        "This tool did not key the radio, toggle PureSignal, change TX monitor, or approve defaults.",
        "Capture disabled and enabled paths under controlled G2 TX bench conditions before TX/PureSignal graduation."
    )
}

$parent = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($parent)) {
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
}
$trace | ConvertTo-Json -Depth 24 | Set-Content -LiteralPath $OutputPath -Encoding UTF8

$summary = [ordered]@{
    outputPath = $OutputPath
    mode = $Mode
    sampleCount = $sampleArray.Count
    pureSignalEnabled = $actualEnabled
    expectedEnabled = $expectedEnabled
    modeConsistent = $modeConsistent
    modeConsistentSampleCount = $modeConsistentSampleCount
    modeMismatchSampleCount = $modeMismatchSampleCount
    feedbackStability = $feedbackStability
    txMonitorCoupling = $txMonitorCoupling
    clippingCount = $clippingCount
    txOutputPeakDbfs = $outPeak
    liveReadinessReady = if ($null -eq $liveReadinessBefore) { $null } else { [bool]$liveReadinessBefore.ready }
    defaultBehaviorChanged = $false
}

if ($JsonOnly) {
    $summary | ConvertTo-Json -Depth 8
}
else {
    Write-Host "PureSignal bench trace written."
    Write-Host "Trace: $OutputPath"
    Write-Host "Mode: $Mode"
    Write-Host "Samples: $($sampleArray.Count)"
    Write-Host "Feedback stability: $feedbackStability"
    Write-Host "TX monitor coupling: $txMonitorCoupling"
    Write-Host "Clipping count: $clippingCount"
}
