param(
    [string]$BundleDir = "",

    [string[]]$DisabledTracePath = @(),

    [string[]]$EnabledTracePath = @(),

    [string]$ReportPath = "",

    [string]$MarkdownPath = "",

    [string]$HardwareTarget = "G2",

    [double]$FeedbackStabilityThreshold = 0.95,

    [double]$TxMonitorCouplingThreshold = 0.05,

    [int]$MaxClippingCount = 0,

    [bool]$PureSignalDefaultStatePreserved = $true,

    [bool]$DefaultBehaviorChangeApproved = $false,

    [switch]$RequireLiveReadinessEvidence,

    [switch]$Force,

    [switch]$NoMarkdown,

    [switch]$JsonOnly
)

$ErrorActionPreference = "Stop"

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

function ConvertTo-PortablePath {
    param(
        [string]$Root,
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path) -or [string]::IsNullOrWhiteSpace($Root)) {
        return $Path
    }

    try {
        $rootFull = [System.IO.Path]::GetFullPath($Root)
        $pathFull = [System.IO.Path]::GetFullPath($Path)
        if (-not $rootFull.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
            $rootFull = $rootFull + [System.IO.Path]::DirectorySeparatorChar
        }

        $rootUri = [System.Uri]::new($rootFull)
        $pathUri = [System.Uri]::new($pathFull)
        $relative = [System.Uri]::UnescapeDataString($rootUri.MakeRelativeUri($pathUri).ToString())
        if (-not $relative.StartsWith("..")) {
            return ($relative -replace "\\", "/")
        }
    }
    catch {
    }

    return $Path
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

    $Value | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $Path -Encoding UTF8
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

function Get-NestedJsonValue {
    param(
        $Object,
        [string[]]$Names
    )

    foreach ($name in @($Names)) {
        $value = Get-JsonValue $Object $name
        if ($null -ne $value) {
            return $value
        }
    }

    foreach ($containerName in @("pureSignal", "puresignal", "tx", "diagnostics", "metrics")) {
        $container = Get-JsonValue $Object $containerName
        if ($null -eq $container) {
            continue
        }

        foreach ($name in @($Names)) {
            $value = Get-JsonValue $container $name
            if ($null -ne $value) {
                return $value
            }
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

    if ($Value -is [bool]) {
        return if ([bool]$Value) { 1.0 } else { 0.0 }
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

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    $stream = [System.IO.File]::OpenRead((Resolve-Path -LiteralPath $Path).Path)
    try {
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try {
            return ([System.BitConverter]::ToString($sha.ComputeHash($stream)) -replace "-", "").ToLowerInvariant()
        }
        finally {
            $sha.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Expand-CsvValues {
    param([string[]]$Values)

    $expanded = New-Object System.Collections.Generic.List[string]
    foreach ($value in @($Values)) {
        foreach ($item in ([string]$value).Split(',', [System.StringSplitOptions]::RemoveEmptyEntries)) {
            $trimmed = $item.Trim()
            if (-not [string]::IsNullOrWhiteSpace($trimmed)) {
                $expanded.Add($trimmed) | Out-Null
            }
        }
    }

    return @($expanded.ToArray())
}

function New-CaptureRecord {
    param(
        [Parameter(Mandatory = $true)][string]$Mode,
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$BundlePath
    )

    $resolved = Get-BundlePath $BundlePath $Path
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "PureSignal $Mode bench trace not found: $Path"
    }

    $json = Read-JsonFile $resolved
    $modeValue = ([string](Get-NestedJsonValue $json @("mode", "pureSignalMode", "psMode"))).Trim().ToLowerInvariant()
    if ([string]::IsNullOrWhiteSpace($modeValue)) {
        $modeValue = $Mode
    }

    $enabledValue = Get-NestedJsonValue $json @("pureSignalEnabled", "psEnabled", "puresignalEnabled", "enabled")
    $psEnabled = if ($null -ne $enabledValue) { Test-Truthy $enabledValue } else { [string]::Equals($Mode, "enabled", [StringComparison]::OrdinalIgnoreCase) }
    $expectedEnabled = [string]::Equals($Mode, "enabled", [StringComparison]::OrdinalIgnoreCase)
    $sampleEnabledValues = New-Object System.Collections.Generic.List[bool]
    foreach ($sample in (Get-JsonArray $json "samples")) {
        $sampleEnabledValue = Get-JsonValue $sample "pureSignalEnabled"
        if ($null -ne $sampleEnabledValue) {
            $sampleEnabledValues.Add((Test-Truthy $sampleEnabledValue)) | Out-Null
        }
    }
    if ($sampleEnabledValues.Count -eq 0) {
        $sampleEnabledValues.Add($psEnabled) | Out-Null
    }
    $modeConsistentSampleCount = @($sampleEnabledValues.ToArray() | Where-Object { $_ -eq $expectedEnabled }).Count
    $modeMismatchSampleCount = $sampleEnabledValues.Count - $modeConsistentSampleCount
    $modeConsistent = ($sampleEnabledValues.Count -gt 0 -and $modeMismatchSampleCount -eq 0)
    $bypassState = ([string](Get-NestedJsonValue $json @("bypassState", "pureSignalBypassState", "puresignalBypassState", "psBypassState"))).Trim()
    $feedbackStability = [double](Get-NumericValueOrDefault (Get-NestedJsonValue $json @("feedbackStability", "feedbackStabilityScore", "pureSignalFeedbackStability", "feedbackStabilityMetric")) -Default 1.0)
    $txMonitorCoupling = [double](Get-NumericValueOrDefault (Get-NestedJsonValue $json @("txMonitorCoupling", "monitorCoupling", "txMonitorCouplingScore", "pureSignalTxMonitorCoupling")) -Default 0.0)
    $clippingCount = [int](Get-NumericValueOrDefault (Get-NestedJsonValue $json @("clippingCount", "txClippingCount", "clipCount")) -Default 0.0)
    $txOutputPeakDbfs = Get-NumericValue (Get-NestedJsonValue $json @("txOutputPeakDbfs", "outPkDbfs", "outputPeakDbfs"))
    $requiresLiveReady = Test-Truthy (Get-JsonValue $json "requiresLiveReady")
    $liveReadinessBefore = Get-JsonValue $json "liveReadinessBefore"
    $liveReadinessReadyValue = Get-JsonValue $json "liveReadinessReady"
    if ($null -eq $liveReadinessReadyValue -and $null -ne $liveReadinessBefore) {
        $liveReadinessReadyValue = Get-JsonValue $liveReadinessBefore "ready"
    }
    $liveReadinessReady = if ($null -eq $liveReadinessReadyValue) { $null } else { Test-Truthy $liveReadinessReadyValue }
    $liveReadinessFailureReasons = if ($null -ne $liveReadinessBefore) {
        @(Get-JsonArray $liveReadinessBefore "failureReasons" | ForEach-Object { [string]$_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    }
    else {
        @()
    }

    $issues = New-Object System.Collections.Generic.List[string]
    if ([string]::Equals($Mode, "disabled", [StringComparison]::OrdinalIgnoreCase) -and $psEnabled) {
        $issues.Add("disabled-capture-has-puresignal-enabled") | Out-Null
    }
    if ([string]::Equals($Mode, "enabled", [StringComparison]::OrdinalIgnoreCase) -and -not $psEnabled) {
        $issues.Add("enabled-capture-does-not-have-puresignal-enabled") | Out-Null
    }
    if (-not $modeConsistent) {
        $issues.Add("puresignal-mode-sample-mismatch") | Out-Null
    }
    if ($feedbackStability -lt $FeedbackStabilityThreshold) {
        $issues.Add("feedback-stability-below-threshold") | Out-Null
    }
    if ($txMonitorCoupling -gt $TxMonitorCouplingThreshold) {
        $issues.Add("tx-monitor-coupling-above-threshold") | Out-Null
    }
    if ($clippingCount -gt $MaxClippingCount) {
        $issues.Add("clipping-count-above-threshold") | Out-Null
    }
    if ($RequireLiveReadinessEvidence) {
        if (-not $requiresLiveReady) {
            $issues.Add("live-readiness-evidence-not-required-by-capture") | Out-Null
        }
        if ($null -eq $liveReadinessBefore) {
            $issues.Add("live-readiness-snapshot-missing") | Out-Null
        }
        if ($true -ne $liveReadinessReady) {
            $issues.Add("live-readiness-not-ready") | Out-Null
        }
    }

    $ready = $issues.Count -eq 0
    return [ordered]@{
        mode = $Mode
        declaredMode = $modeValue
        path = ConvertTo-PortablePath -Root $BundlePath -Path $resolved
        sha256 = Get-FileSha256 $resolved
        pureSignalEnabled = $psEnabled
        bypassState = $bypassState
        feedbackStability = [Math]::Round($feedbackStability, 6)
        txMonitorCoupling = [Math]::Round($txMonitorCoupling, 6)
        clippingCount = $clippingCount
        txOutputPeakDbfs = if ($null -ne $txOutputPeakDbfs) { [Math]::Round([double]$txOutputPeakDbfs, 6) } else { $null }
        requiresLiveReady = $requiresLiveReady
        liveReadinessReady = $liveReadinessReady
        liveReadinessStatus = if ($null -ne $liveReadinessBefore) { [string](Get-JsonValue $liveReadinessBefore "status") } else { "" }
        liveReadinessWdspActive = if ($null -ne $liveReadinessBefore) { Test-Truthy (Get-JsonValue $liveReadinessBefore "wdspActive") } else { $null }
        liveReadinessFrontendSceneFresh = if ($null -ne $liveReadinessBefore) { Test-Truthy (Get-JsonValue $liveReadinessBefore "frontendSceneFresh") } else { $null }
        liveReadinessRuntimeStatus = if ($null -ne $liveReadinessBefore) { [string](Get-JsonValue $liveReadinessBefore "runtimeStatus") } else { "" }
        liveReadinessRadioVfoHz = if ($null -ne $liveReadinessBefore) { Get-JsonValue $liveReadinessBefore "radioVfoHz" } else { $null }
        liveReadinessRadioMode = if ($null -ne $liveReadinessBefore) { [string](Get-JsonValue $liveReadinessBefore "radioMode") } else { "" }
        liveReadinessFailureReasons = @($liveReadinessFailureReasons)
        ready = $ready
        expectedPureSignalEnabled = $expectedEnabled
        modeConsistent = $modeConsistent
        modeConsistentSampleCount = $modeConsistentSampleCount
        modeMismatchSampleCount = $modeMismatchSampleCount
        modeSampleCount = $sampleEnabledValues.Count
        issues = @($issues.ToArray())
    }
}

function New-Gate {
    param(
        [string]$Id,
        [bool]$Passed,
        [string]$Note
    )

    return [ordered]@{
        id = $Id
        passed = $Passed
        status = if ($Passed) { "pass" } else { "fail" }
        note = $Note
    }
}

$bundlePath = if ([string]::IsNullOrWhiteSpace($BundleDir)) {
    (Get-Location).Path
}
else {
    (Resolve-Path -LiteralPath $BundleDir).Path
}

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = "artifacts/puresignal-safe-bypass-report.json"
}
if ([string]::IsNullOrWhiteSpace($MarkdownPath)) {
    $MarkdownPath = "artifacts/puresignal-safe-bypass-report.md"
}

$resolvedReportPath = Get-BundlePath $bundlePath $ReportPath
$resolvedMarkdownPath = Get-BundlePath $bundlePath $MarkdownPath
foreach ($path in @($resolvedReportPath, $resolvedMarkdownPath)) {
    if (-not $NoMarkdown -or $path -eq $resolvedReportPath) {
        if ((Test-Path -LiteralPath $path -PathType Leaf) -and -not $Force) {
            throw "Output file already exists. Use -Force to overwrite: $path"
        }
    }
}

$disabledPaths = @(Expand-CsvValues $DisabledTracePath)
$enabledPaths = @(Expand-CsvValues $EnabledTracePath)
$captures = New-Object System.Collections.Generic.List[object]
foreach ($path in $disabledPaths) {
    $captures.Add((New-CaptureRecord -Mode "disabled" -Path $path -BundlePath $bundlePath)) | Out-Null
}
foreach ($path in $enabledPaths) {
    $captures.Add((New-CaptureRecord -Mode "enabled" -Path $path -BundlePath $bundlePath)) | Out-Null
}

$captureRecords = @($captures.ToArray())
$disabledCaptures = @($captureRecords | Where-Object { [string](Get-JsonValue $_ "mode") -eq "disabled" })
$enabledCaptures = @($captureRecords | Where-Object { [string](Get-JsonValue $_ "mode") -eq "enabled" })
$disabledPathReady = ($disabledCaptures.Count -gt 0 -and @($disabledCaptures | Where-Object { -not (Test-Truthy (Get-JsonValue $_ "ready")) }).Count -eq 0)
$enabledPathReady = ($enabledCaptures.Count -gt 0 -and @($enabledCaptures | Where-Object { -not (Test-Truthy (Get-JsonValue $_ "ready")) }).Count -eq 0)
$capturedModeCount = 0
if ($disabledCaptures.Count -gt 0) {
    $capturedModeCount++
}
if ($enabledCaptures.Count -gt 0) {
    $capturedModeCount++
}

$missingModes = New-Object System.Collections.Generic.List[string]
if ($disabledCaptures.Count -eq 0) {
    $missingModes.Add("disabled") | Out-Null
}
if ($enabledCaptures.Count -eq 0) {
    $missingModes.Add("enabled") | Out-Null
}

$feedbackValues = @($captureRecords | ForEach-Object { [double](Get-NumericValueOrDefault (Get-JsonValue $_ "feedbackStability")) })
$couplingValues = @($captureRecords | ForEach-Object { [double](Get-NumericValueOrDefault (Get-JsonValue $_ "txMonitorCoupling")) })
$clippingCountTotal = [int](@($captureRecords | ForEach-Object { [int](Get-NumericValueOrDefault (Get-JsonValue $_ "clippingCount")) } | Measure-Object -Sum).Sum)
$feedbackStabilityMin = if ($feedbackValues.Count -gt 0) { [double]($feedbackValues | Measure-Object -Minimum).Minimum } else { 0.0 }
$txMonitorCouplingMax = if ($couplingValues.Count -gt 0) { [double]($couplingValues | Measure-Object -Maximum).Maximum } else { 0.0 }
$liveReadinessReadyCount = @($captureRecords | Where-Object { Test-Truthy (Get-JsonValue $_ "liveReadinessReady") }).Count
$liveReadinessMissingCount = @($captureRecords | Where-Object {
        (-not (Test-Truthy (Get-JsonValue $_ "requiresLiveReady"))) -or
        ($null -eq (Get-JsonValue $_ "liveReadinessReady"))
    }).Count
$liveReadinessFailureCount = @($captureRecords | Where-Object {
        (Test-Truthy (Get-JsonValue $_ "requiresLiveReady")) -and
        ($null -ne (Get-JsonValue $_ "liveReadinessReady")) -and
        (-not (Test-Truthy (Get-JsonValue $_ "liveReadinessReady")))
    }).Count
$liveReadinessEvidenceReady = (-not [bool]$RequireLiveReadinessEvidence) -or (
    $captureRecords.Count -gt 0 -and
    $liveReadinessMissingCount -eq 0 -and
    $liveReadinessFailureCount -eq 0
)
$modeMismatchCaptureCount = @($captureRecords | Where-Object { -not (Test-Truthy (Get-JsonValue $_ "modeConsistent")) }).Count
$modeMismatchSampleCountTotal = [int](@($captureRecords | ForEach-Object { [int](Get-NumericValueOrDefault (Get-JsonValue $_ "modeMismatchSampleCount")) } | Measure-Object -Sum).Sum)
$modeConsistencyReady = ($captureRecords.Count -gt 0 -and $modeMismatchCaptureCount -eq 0 -and $modeMismatchSampleCountTotal -eq 0)

$gates = @(
    (New-Gate "disabled-path-captured" ($disabledCaptures.Count -gt 0) "PureSignal disabled/bypass TX bench capture must be present.")
    (New-Gate "enabled-path-captured" ($enabledCaptures.Count -gt 0) "PureSignal enabled TX bench capture must be present.")
    (New-Gate "disabled-path-ready" $disabledPathReady "Disabled/bypass capture must not have PureSignal active, clipping, coupling, or instability.")
    (New-Gate "enabled-path-ready" $enabledPathReady "Enabled capture must show PureSignal active with stable feedback and no coupling/clipping.")
    (New-Gate "default-state-preserved" ([bool]$PureSignalDefaultStatePreserved) "PureSignal default/bypass state must not change during modernization.")
    (New-Gate "default-behavior-not-approved" (-not [bool]$DefaultBehaviorChangeApproved) "This report cannot approve default DSP behavior changes.")
    (New-Gate "feedback-stability-bounded" ($feedbackStabilityMin -ge $FeedbackStabilityThreshold) "Minimum feedback stability must meet threshold.")
    (New-Gate "tx-monitor-coupling-bounded" ($txMonitorCouplingMax -le $TxMonitorCouplingThreshold) "TX monitor or external DSP audio must not couple into feedback.")
    (New-Gate "no-clipping" ($clippingCountTotal -le $MaxClippingCount) "TX/PureSignal bench captures must not clip.")
    (New-Gate "live-readiness-evidence-ready" $liveReadinessEvidenceReady "When required, every PureSignal bench trace must include ready G2/frontend/WDSP live diagnostics evidence.")
    (New-Gate "mode-sample-consistency" $modeConsistencyReady "Every PureSignal bench sample must match its requested disabled/enabled mode.")
)

$gateFailureCount = @($gates | Where-Object { -not (Test-Truthy (Get-JsonValue $_ "passed")) }).Count
$readyForReview = ($gateFailureCount -eq 0)

$report = [ordered]@{
    schemaVersion = 1
    tool = "summarize-dsp-puresignal-bench"
    generatedUtc = [DateTimeOffset]::UtcNow
    scenarioId = "tx-puresignal-safe-bypass"
    hardwareTarget = $HardwareTarget
    readyForReview = $readyForReview
    status = if ($readyForReview) { "ready" } else { "not-ready" }
    disabledPathReady = $disabledPathReady
    enabledPathReady = $enabledPathReady
    capturedModeCount = $capturedModeCount
    missingModeCount = $missingModes.Count
    missingModes = @($missingModes.ToArray())
    pureSignalDefaultStatePreserved = [bool]$PureSignalDefaultStatePreserved
    defaultBehaviorChangeApproved = [bool]$DefaultBehaviorChangeApproved
    liveReadinessEvidenceRequired = [bool]$RequireLiveReadinessEvidence
    liveReadinessReady = $liveReadinessEvidenceReady
    liveReadinessReadyCount = $liveReadinessReadyCount
    liveReadinessMissingCount = $liveReadinessMissingCount
    liveReadinessFailureCount = $liveReadinessFailureCount
    modeConsistencyReady = $modeConsistencyReady
    modeMismatchCaptureCount = $modeMismatchCaptureCount
    modeMismatchSampleCount = $modeMismatchSampleCountTotal
    feedbackStabilityMin = [Math]::Round($feedbackStabilityMin, 6)
    feedbackStabilityThreshold = [Math]::Round($FeedbackStabilityThreshold, 6)
    txMonitorCouplingMax = [Math]::Round($txMonitorCouplingMax, 6)
    txMonitorCouplingThreshold = [Math]::Round($TxMonitorCouplingThreshold, 6)
    clippingCountTotal = $clippingCountTotal
    maxClippingCount = $MaxClippingCount
    gateFailureCount = $gateFailureCount
    metrics = [ordered]@{
        "bypass state" = if ($disabledPathReady -and $enabledPathReady) { "disabled-and-enabled-captured" } else { "incomplete" }
        "feedback stability" = [Math]::Round($feedbackStabilityMin, 6)
        "TX monitor coupling" = [Math]::Round($txMonitorCouplingMax, 6)
        "clipping count" = $clippingCountTotal
        "live readiness evidence" = if ($liveReadinessEvidenceReady) { "ready" } else { "not-ready" }
        "mode sample consistency" = if ($modeConsistencyReady) { "ready" } else { "not-ready" }
    }
    gates = @($gates)
    captures = @($captureRecords)
    notes = @(
        "This report summarizes G2 TX/PureSignal bench evidence only; it does not key the radio or approve default behavior changes.",
        "PureSignal disabled and enabled paths must both be captured before TX profile graduation.",
        "External DSP/ML candidates must remain off the TX monitor and PureSignal feedback paths."
    )
}

Write-JsonFile -Path $resolvedReportPath -Value $report

if (-not $NoMarkdown) {
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# PureSignal Safe Bypass Bench Report") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add("- Status: $($report.status)") | Out-Null
    $lines.Add("- Disabled path ready: $($report.disabledPathReady)") | Out-Null
    $lines.Add("- Enabled path ready: $($report.enabledPathReady)") | Out-Null
    $lines.Add("- Feedback stability min: $($report.feedbackStabilityMin)") | Out-Null
    $lines.Add("- TX monitor coupling max: $($report.txMonitorCouplingMax)") | Out-Null
    $lines.Add("- Clipping count total: $($report.clippingCountTotal)") | Out-Null
    $lines.Add("- Live readiness evidence required: $($report.liveReadinessEvidenceRequired)") | Out-Null
    $lines.Add("- Live readiness evidence ready: $($report.liveReadinessReady)") | Out-Null
    $lines.Add("- Mode consistency ready: $($report.modeConsistencyReady)") | Out-Null
    $lines.Add("- Mode mismatch samples: $($report.modeMismatchSampleCount)") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add("## Gates") | Out-Null
    foreach ($gate in $gates) {
        $lines.Add("- $($gate.status): $($gate.id) - $($gate.note)") | Out-Null
    }
    $parent = Split-Path -Parent $resolvedMarkdownPath
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }
    Set-Content -LiteralPath $resolvedMarkdownPath -Value $lines -Encoding UTF8
}

$summary = [ordered]@{
    reportPath = ConvertTo-PortablePath -Root $bundlePath -Path $resolvedReportPath
    markdownPath = if ($NoMarkdown) { $null } else { ConvertTo-PortablePath -Root $bundlePath -Path $resolvedMarkdownPath }
    readyForReview = $readyForReview
    gateFailureCount = $gateFailureCount
    disabledPathReady = $disabledPathReady
    enabledPathReady = $enabledPathReady
    liveReadinessEvidenceRequired = [bool]$RequireLiveReadinessEvidence
    liveReadinessReady = $liveReadinessEvidenceReady
    modeConsistencyReady = $modeConsistencyReady
    modeMismatchCaptureCount = $modeMismatchCaptureCount
    modeMismatchSampleCount = $modeMismatchSampleCountTotal
}

if ($JsonOnly) {
    $summary | ConvertTo-Json -Depth 8
}
else {
    Write-Host "PureSignal bench report written."
    Write-Host "Report: $resolvedReportPath"
    if (-not $NoMarkdown) {
        Write-Host "Markdown: $resolvedMarkdownPath"
    }
    Write-Host "Ready for review: $readyForReview"
}

if (-not $readyForReview) {
    exit 1
}
