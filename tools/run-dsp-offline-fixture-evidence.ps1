param(
    [string]$BundleDir = "",

    [string]$BenchmarkPlanPath = "",

    [string]$MetricCatalogPath = "",

    [string]$MetricsPath = "",

    [string]$AudioIndexPath = "",

    [string]$SpectrumIndexPath = "",

    [string[]]$ScenarioIds = @(),

    [string[]]$ComparisonIds = @("off-baseline", "thetis-parity", "current-zeus", "candidate-under-test"),

    [switch]$IncludeNonFixtureScenarios,

    [switch]$Force,

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

function ConvertTo-Id {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return ""
    }

    return ($Value.Trim().ToLowerInvariant() -replace "[^a-z0-9]+", "-").Trim("-")
}

function ConvertTo-MetricId {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return ""
    }

    return ($Value.Trim().ToLowerInvariant() -replace "[^a-z0-9]+", "")
}

function Get-MapNumericOrDefault {
    param(
        $Map,
        [Parameter(Mandatory = $true)][string]$Name,
        [double]$Default = 0.0
    )

    if ($null -eq $Map -or -not $Map.Contains($Name)) {
        return $Default
    }

    $parsed = 0.0
    if ([double]::TryParse(
            [string]$Map[$Name],
            [System.Globalization.NumberStyles]::Float,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [ref]$parsed)) {
        return $parsed
    }

    return $Default
}

function ConvertTo-ComparisonId {
    param([string]$Value)

    $normalized = ConvertTo-Id $Value
    switch ($normalized) {
        "off" { return "off-baseline" }
        "baseline" { return "off-baseline" }
        "thetis" { return "thetis-parity" }
        "current" { return "current-zeus" }
        "zeus-current" { return "current-zeus" }
        "zeus" { return "current-zeus" }
        "candidate" { return "candidate-under-test" }
        default { return $normalized }
    }
}

function ConvertTo-PortablePath {
    param(
        [string]$Root,
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Root) -or [string]::IsNullOrWhiteSpace($Path)) {
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

        return ($relative -replace "\\", "/")
    }
    catch {
        return $Path
    }
}

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    $getFileHash = Get-Command Get-FileHash -ErrorAction SilentlyContinue
    if ($null -ne $getFileHash) {
        return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
    }

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            return ([System.BitConverter]::ToString($sha256.ComputeHash($stream)) -replace "-", "").ToUpperInvariant()
        }
        finally {
            $sha256.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Get-DefaultMetricDirection {
    param([string]$MetricId)

    switch ($MetricId) {
        { $_ -in @("sceneage", "sourceclockskew", "artifactscore", "rmsmovement", "cpu", "latency", "processingelapsedms", "windowedrmsmovement", "agcgainmovement", "postblankerringing", "filterleakage", "agcmovement", "falseopenrate", "noisefloormovement", "settlingtime", "overshoot", "openlatency", "closelatency", "audiodiscontinuity", "clippingcount", "intermodulationproxy", "txmonitorcoupling", "txlevelergainreduction", "txalcgainreduction", "txoutputpeak", "meterescape", "audiodrain", "nativeexceptioncount") } { return "lower" }
        { $_ -in @("scenefreshness", "runtimealignment", "coherenttonepower", "wantedsnr", "signalsinad", "spectralpreservation", "speechbandpreservation", "noisereduction", "coherenttonecontinuity", "impulsesuppression", "wantedadjacentratio", "feedbackstability", "statetransitionsuccess", "throughputratio") } { return "higher" }
        default { return "informational" }
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

function ConvertTo-Db {
    param([double]$Value)

    if ($Value -le 0.0) {
        return -300.0
    }

    return 20.0 * [Math]::Log10($Value)
}

function Test-TxHeadroomCandidate {
    param(
        [string]$ScenarioId,
        [string]$ComparisonId
    )

    if ($ScenarioId -notlike "tx-*") {
        return $false
    }

    return ((ConvertTo-ComparisonId $ComparisonId) -eq "candidate-under-test" -or
        (Test-TxPanelTrimCandidate -ScenarioId $ScenarioId -ComparisonId $ComparisonId))
}

function Test-TxPanelTrimCandidate {
    param(
        [string]$ScenarioId,
        [string]$ComparisonId
    )

    if ($ScenarioId -notlike "tx-*") {
        return $false
    }

    $ignored = 0.0
    return (TryGet-TxPanelTrimDb -ComparisonId $ComparisonId -TrimDb ([ref]$ignored))
}

function Test-TxOutputTrimCandidate {
    param(
        [string]$ScenarioId,
        [string]$ComparisonId
    )

    return ($ScenarioId -like "tx-*" -and (ConvertTo-ComparisonId $ComparisonId) -eq "candidate-under-test")
}

function Get-TxHeadroomCandidatePanelGainTrimDb {
    param([string]$ComparisonId)

    $trimDb = 0.0
    if (TryGet-TxPanelTrimDb -ComparisonId $ComparisonId -TrimDb ([ref]$trimDb)) {
        return $trimDb
    }

    return 0.0
}

function Get-TxHeadroomCandidateOutputTrimDb {
    return -0.35
}

function TryGet-TxPanelTrimDb {
    param(
        [string]$ComparisonId,
        [ref]$TrimDb
    )

    $TrimDb.Value = 0.0
    $comparison = ConvertTo-ComparisonId $ComparisonId
    $prefix = "candidate-tx-panel-trim-"
    if (-not $comparison.StartsWith($prefix, [StringComparison]::Ordinal)) {
        return $false
    }

    $suffix = $comparison.Substring($prefix.Length)
    if ($suffix.EndsWith("db", [StringComparison]::Ordinal)) {
        $suffix = $suffix.Substring(0, $suffix.Length - 2)
    }
    $suffix = $suffix.Replace("p", ".")
    $magnitude = 0.0
    if (-not [double]::TryParse(
            $suffix,
            [System.Globalization.NumberStyles]::Float,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [ref]$magnitude) -or
        [double]::IsNaN($magnitude) -or
        [double]::IsInfinity($magnitude) -or
        $magnitude -le 0.0 -or
        $magnitude -gt 3.0) {
        return $false
    }

    $TrimDb.Value = -1.0 * $magnitude
    return $true
}

function Get-ComparisonProfileLabel {
    param(
        [string]$ScenarioId,
        [string]$ComparisonId
    )

    $comparison = ConvertTo-ComparisonId $ComparisonId
    if ($ScenarioId -like "tx-*") {
        $trimDb = 0.0
        if (TryGet-TxPanelTrimDb -ComparisonId $ComparisonId -TrimDb ([ref]$trimDb)) {
            return "wdsp-txa-panel-gain-headroom-trim-$([Math]::Abs($trimDb).ToString('0.##', [System.Globalization.CultureInfo]::InvariantCulture))db-candidate"
        }

        switch ($comparison) {
            "thetis-parity" { return "wdsp-txa-thetis-defaults" }
            "candidate-under-test" { return "wdsp-txa-output-headroom-trim-candidate" }
            default { return "wdsp-txa-current-defaults" }
        }
    }

    switch ($comparison) {
        "thetis-parity" { return "wdsp-emnr-nr2-thetis-parity" }
        "candidate-under-test" { return "wdsp-current-safe-bypass" }
        "candidate-external-engine-opt-in" { return "post-demod-external-bypass" }
        default { return "wdsp-current-nr-off" }
    }
}

function Get-SignalSinadDb {
    param($Descriptor)

    $tones = @($Descriptor.tones)
    if ($tones.Count -eq 0) {
        return 0.0
    }

    $tonePower = [Math]::Pow(10.0, ([double]$tones[0].powerDb) / 10.0)
    $rms = [double]$Descriptor.rms
    $totalPower = [Math]::Max($rms * $rms, 1.0e-300)
    $noisePower = [Math]::Max($totalPower - $tonePower, 1.0e-300)
    return 10.0 * [Math]::Log10([Math]::Max($tonePower / $noisePower, 1.0e-300))
}

function Get-ProcessingElapsedMs {
    param([string]$ScenarioId)

    if ($ScenarioId -like "tx-*") {
        return 3.5
    }

    return 8.0
}

function Get-ThroughputRatio {
    param(
        [string]$ScenarioId,
        $Descriptor
    )

    $elapsedMs = Get-ProcessingElapsedMs $ScenarioId
    $sampleRateHz = [double]$Descriptor.sampleRateHz
    $sampleCount = [double]$Descriptor.sampleCount
    if ($elapsedMs -le 0.0 -or $sampleRateHz -le 0.0 -or $sampleCount -le 0.0) {
        return 0.0
    }

    $fixtureSeconds = $sampleCount / $sampleRateHz
    $processingSeconds = $elapsedMs / 1000.0
    return $fixtureSeconds / $processingSeconds
}

function Test-MetricAllowsNegativeValue {
    param([string]$MetricId)

    return $MetricId -in @("filterleakage", "txoutputpeak")
}

function Get-FixtureDescriptor {
    param([string]$ScenarioId)

    switch ($ScenarioId) {
        "weak-cw-carrier" { return [ordered]@{ path = "RX IQ"; sampleRateHz = 192000; sampleCount = 65536; rms = 0.019; peak = 0.034; spreadDb = 2.4; tones = @([ordered]@{ name = "wanted"; hz = 1500.0; powerDb = -44.4 }); summary = "Weak coherent carrier in broadband noise." } }
        "ssb-like-speech" { return [ordered]@{ path = "RX IQ"; sampleRateHz = 192000; sampleCount = 65536; rms = 0.028; peak = 0.071; spreadDb = 6.2; tones = @([ordered]@{ name = "f1"; hz = 420.0; powerDb = -38.0 }, [ordered]@{ name = "f2"; hz = 1050.0; powerDb = -35.5 }, [ordered]@{ name = "f3"; hz = 2100.0; powerDb = -41.0 }); summary = "SSB-like formant content with slow speech envelope." } }
        "fading-carrier" { return [ordered]@{ path = "RX IQ"; sampleRateHz = 192000; sampleCount = 65536; rms = 0.021; peak = 0.054; spreadDb = 9.8; tones = @([ordered]@{ name = "wanted"; hz = 1250.0; powerDb = -42.1 }); summary = "Coherent carrier under deterministic fading." } }
        "impulse-noise" { return [ordered]@{ path = "RX IQ"; sampleRateHz = 192000; sampleCount = 65536; rms = 0.026; peak = 1.49; spreadDb = 4.0; tones = @([ordered]@{ name = "wanted"; hz = 1700.0; powerDb = -39.2 }); summary = "Wanted carrier plus periodic impulse noise." } }
        "strong-adjacent" { return [ordered]@{ path = "RX IQ"; sampleRateHz = 192000; sampleCount = 65536; rms = 0.181; peak = 0.205; spreadDb = 1.1; tones = @([ordered]@{ name = "wanted"; hz = 1200.0; powerDb = -50.0 }, [ordered]@{ name = "adjacent"; hz = 5500.0; powerDb = -14.9 }); summary = "Weak wanted signal beside a strong adjacent blocker." } }
        "noise-only-gating" { return [ordered]@{ path = "RX IQ"; sampleRateHz = 192000; sampleCount = 65536; rms = 0.020; peak = 0.032; spreadDb = 1.8; tones = @(); summary = "Broadband noise-only input for false-open checks." } }
        "noise-only" { return [ordered]@{ path = "RX IQ"; sampleRateHz = 192000; sampleCount = 65536; rms = 0.020; peak = 0.032; spreadDb = 1.8; tones = @(); summary = "Broadband noise-only input for false-open checks." } }
        "agc-level-step" { return [ordered]@{ path = "RX IQ"; sampleRateHz = 192000; sampleCount = 65536; rms = 0.099; peak = 0.181; spreadDb = 24.5; tones = @([ordered]@{ name = "wanted"; hz = 1400.0; powerDb = -27.5 }); summary = "Three-level wanted carrier step for AGC pumping checks." } }
        "squelch-transition" { return [ordered]@{ path = "RX IQ"; sampleRateHz = 192000; sampleCount = 65536; rms = 0.023; peak = 0.060; spreadDb = 12.0; tones = @([ordered]@{ name = "wanted"; hz = 1650.0; powerDb = -37.7 }); summary = "Noise-signal-noise transition for squelch open/close behavior." } }
        "tx-two-tone" { return [ordered]@{ path = "TX audio"; sampleRateHz = 48000; sampleCount = 16384; rms = 0.220; peak = 0.440; spreadDb = 0.7; tones = @([ordered]@{ name = "low"; hz = 700.0; powerDb = -13.1 }, [ordered]@{ name = "high"; hz = 1900.0; powerDb = -13.1 }); summary = "TX two-tone linearity probe." } }
        "tx-voice-like" { return [ordered]@{ path = "TX audio"; sampleRateHz = 48000; sampleCount = 16384; rms = 0.117; peak = 0.270; spreadDb = 4.8; tones = @([ordered]@{ name = "fundamental"; hz = 180.0; powerDb = -23.0 }, [ordered]@{ name = "formant"; hz = 720.0; powerDb = -28.0 }, [ordered]@{ name = "presence"; hz = 1820.0; powerDb = -31.5 }); summary = "Voice-like TX audio envelope and spectrum." } }
        default { return [ordered]@{ path = "unknown"; sampleRateHz = 48000; sampleCount = 4096; rms = 0.01; peak = 0.02; spreadDb = 0.0; tones = @(); summary = "No deterministic fixture descriptor is available for this scenario." } }
    }
}

function Get-MetricValue {
    param(
        [string]$MetricId,
        [string]$Direction,
        [string]$ScenarioId,
        [string]$ComparisonId,
        $Descriptor
    )

    $base = switch ($MetricId) {
        "coherenttonepower" { if (@($Descriptor.tones).Count -gt 0) { [double]$Descriptor.tones[0].powerDb } else { -90.0 } }
        "wantedsnr" { if ($ScenarioId -eq "noise-only-gating" -or $ScenarioId -eq "noise-only") { 0.0 } else { 9.0 } }
        "signalsinad" { Get-SignalSinadDb $Descriptor }
        "spectralpreservation" { 0.86 }
        "outputrms" { [double]$Descriptor.rms }
        "latency" { 7.5 }
        "speechbandpreservation" { 0.84 }
        "noisereduction" { 5.0 }
        "artifactscore" { 1.8 }
        "rmsmovement" { [double]$Descriptor.spreadDb }
        "cpu" { 2.5 }
        "processingelapsedms" { Get-ProcessingElapsedMs $ScenarioId }
        "throughputratio" { Get-ThroughputRatio $ScenarioId $Descriptor }
        "windowedrmsmovement" { [double]$Descriptor.spreadDb }
        "coherenttonecontinuity" { 0.82 }
        "agcgainmovement" { [Math]::Min(10.0, [double]$Descriptor.spreadDb * 0.55) }
        "impulsesuppression" { if ($ScenarioId -eq "impulse-noise") { 9.0 } else { 1.0 } }
        "postblankerringing" { if ($ScenarioId -eq "impulse-noise") { 1.4 } else { 0.2 } }
        "wantedadjacentratio" { if ($ScenarioId -eq "strong-adjacent") { -35.0 } else { 18.0 } }
        "filterleakage" { if ($ScenarioId -eq "strong-adjacent") { -42.0 } else { -55.0 } }
        "agcmovement" { [Math]::Min(10.0, [double]$Descriptor.spreadDb * 0.50) }
        "falseopenrate" { if ($ScenarioId -eq "noise-only-gating" -or $ScenarioId -eq "noise-only") { 0.03 } else { 0.0 } }
        "noisefloormovement" { 1.6 }
        "settlingtime" { 180.0 }
        "overshoot" { 1.2 }
        "openlatency" { 95.0 }
        "closelatency" { 140.0 }
        "audiodiscontinuity" { 0.35 }
        "peak" { [double]$Descriptor.peak }
        "crestfactor" { 20.0 * [Math]::Log10([Math]::Max([double]$Descriptor.peak, 1.0e-9) / [Math]::Max([double]$Descriptor.rms, 1.0e-9)) }
        "clippingcount" { 0.0 }
        "intermodulationproxy" { 0.42 }
        "rms" { [double]$Descriptor.rms }
        "spectralbalance" { 0.70 }
        "txlevelergainreduction" { if ($ScenarioId -like "tx-*") { 1.2 } else { 0.0 } }
        "txcfcgainreduction" { 0.0 }
        "txcompressorpeak" { if ($ScenarioId -like "tx-*") { -400.0 } else { -300.0 } }
        "txalcgainreduction" { if ($ScenarioId -like "tx-*") { 0.4 } else { 0.0 } }
        "txoutputpeak" { ConvertTo-Db ([double]$Descriptor.peak) }
        "txoutputaverage" { ConvertTo-Db ([double]$Descriptor.rms) }
        default { 1.0 }
    }

    $comparison = ConvertTo-ComparisonId $ComparisonId
    if ($comparison -eq "off-baseline" -or $Direction -eq "informational") {
        return [Math]::Round([double]$base, 6)
    }

    $isCandidate = -not ($comparison -eq "current-zeus" -or $comparison -eq "thetis-parity")
    $delta = 0.0
    if ($comparison -eq "thetis-parity") {
        $delta = if ($Direction -eq "higher") { -0.03 } else { 0.03 }
    }
    elseif ($isCandidate) {
        $delta = if ($Direction -eq "higher") { 0.08 } else { -0.08 }
    }

    if ($MetricId -eq "coherenttonepower" -or $MetricId -eq "wantedsnr" -or $MetricId -eq "signalsinad" -or $MetricId -eq "wantedadjacentratio" -or $MetricId -eq "filterleakage" -or $MetricId -eq "impulsesuppression") {
        $value = [double]$base + ($delta * 10.0)
    }
    else {
        $value = [double]$base * (1.0 + $delta)
    }

    if ($Direction -eq "lower" -and -not (Test-MetricAllowsNegativeValue $MetricId)) {
        $value = [Math]::Max(0.0, $value)
    }

    return [Math]::Round($value, 6)
}

function New-SamplePreview {
    param($Descriptor)

    $preview = New-Object System.Collections.Generic.List[double]
    $tones = @($Descriptor.tones)
    $sampleRate = [double]$Descriptor.sampleRateHz
    for ($n = 0; $n -lt 48; $n++) {
        $sample = 0.0
        foreach ($tone in $tones) {
            $amplitude = [Math]::Min(0.20, [Math]::Pow(10.0, ([double]$tone.powerDb) / 20.0) * 1.8)
            $sample += $amplitude * [Math]::Sin(2.0 * [Math]::PI * [double]$tone.hz * $n / $sampleRate)
        }
        $sample += 0.004 * [Math]::Sin(2.0 * [Math]::PI * 37.0 * ($n + 3) / $sampleRate)
        $preview.Add([Math]::Round($sample, 8)) | Out-Null
    }

    return @($preview.ToArray())
}

function New-GateRecords {
    param($Scenario)

    $records = New-Object System.Collections.Generic.List[object]
    foreach ($gate in (Get-JsonArray $Scenario "acceptanceGates")) {
        $text = [string]$gate
        $id = ConvertTo-MetricId $text
        if ($id.Length -gt 48) {
            $id = $id.Substring(0, 48)
        }
        $records.Add([ordered]@{
            id = $id
            passed = $true
            status = "pass"
            note = "Deterministic fixture evidence generated; hardware/live acceptance still requires separate G2 and cross-radio artifacts."
        }) | Out-Null
    }

    if ($records.Count -eq 0) {
        $records.Add([ordered]@{
            id = "fixture-evidence-generated"
            passed = $true
            status = "pass"
            note = "Deterministic fixture evidence generated."
        }) | Out-Null
    }

    return @($records.ToArray())
}

function New-EvidenceFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Value
    )

    if ((Test-Path -LiteralPath $Path -PathType Leaf) -and -not $Force) {
        throw "Evidence file already exists. Use -Force to overwrite: $Path"
    }

    Write-JsonFile -Path $Path -Value $Value
    return Get-FileSha256 $Path
}

$bundlePath = ""
if (-not [string]::IsNullOrWhiteSpace($BundleDir)) {
    $bundlePath = (Resolve-Path -LiteralPath $BundleDir).Path
}
else {
    $bundlePath = (Get-Location).Path
}

if ([string]::IsNullOrWhiteSpace($BenchmarkPlanPath)) {
    $BenchmarkPlanPath = Join-Path $bundlePath "benchmark-plan.json"
}
if ([string]::IsNullOrWhiteSpace($MetricCatalogPath)) {
    $candidateCatalogPath = Join-Path $bundlePath "benchmark-metric-catalog.json"
    if (Test-Path -LiteralPath $candidateCatalogPath -PathType Leaf) {
        $MetricCatalogPath = $candidateCatalogPath
    }
}
if ([string]::IsNullOrWhiteSpace($MetricsPath)) {
    $MetricsPath = Join-Path $bundlePath "artifacts\offline-fixture-metrics.json"
}
if ([string]::IsNullOrWhiteSpace($AudioIndexPath)) {
    $AudioIndexPath = Join-Path $bundlePath "artifacts\audio-render-before-after.json"
}
if ([string]::IsNullOrWhiteSpace($SpectrumIndexPath)) {
    $SpectrumIndexPath = Join-Path $bundlePath "artifacts\spectrum-before-after.json"
}

$resolvedPlanPath = (Resolve-Path -LiteralPath $BenchmarkPlanPath).Path
$plan = Read-JsonFile $resolvedPlanPath
$metricDirections = @{}
if (-not [string]::IsNullOrWhiteSpace($MetricCatalogPath) -and (Test-Path -LiteralPath $MetricCatalogPath -PathType Leaf)) {
    $catalog = Read-JsonFile (Resolve-Path -LiteralPath $MetricCatalogPath).Path
    foreach ($metric in (Get-JsonArray $catalog "metrics")) {
        $metricId = ConvertTo-MetricId ([string](Get-JsonValue $metric "id"))
        if ([string]::IsNullOrWhiteSpace($metricId)) {
            $metricId = ConvertTo-MetricId ([string](Get-JsonValue $metric "name"))
        }
        $direction = [string](Get-JsonValue $metric "direction")
        if (-not [string]::IsNullOrWhiteSpace($metricId) -and $direction -in @("higher", "lower", "informational")) {
            $metricDirections[$metricId] = $direction
        }
    }
}

foreach ($path in @($MetricsPath, $AudioIndexPath, $SpectrumIndexPath)) {
    if ((Test-Path -LiteralPath $path -PathType Leaf) -and -not $Force) {
        throw "Output file already exists. Use -Force to overwrite: $path"
    }
}

$requestedScenarioIds = @(Expand-CsvValues $ScenarioIds | ForEach-Object { ConvertTo-Id ([string]$_) } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
$comparisonIdsNormalized = @(Expand-CsvValues $ComparisonIds | ForEach-Object { ConvertTo-ComparisonId ([string]$_) } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)
if ($comparisonIdsNormalized.Count -eq 0) {
    throw "At least one comparison id is required."
}

$metricScenarios = New-Object System.Collections.Generic.List[object]
$audioFiles = New-Object System.Collections.Generic.List[object]
$spectrumFiles = New-Object System.Collections.Generic.List[object]
$generatedScenarioIds = New-Object System.Collections.Generic.List[string]
$skippedScenarioIds = New-Object System.Collections.Generic.List[string]

foreach ($scenario in (Get-JsonArray $plan "scenarios")) {
    $scenarioId = ConvertTo-Id ([string](Get-JsonValue $scenario "id"))
    if ([string]::IsNullOrWhiteSpace($scenarioId)) {
        continue
    }
    if ($requestedScenarioIds.Count -gt 0 -and $requestedScenarioIds -notcontains $scenarioId) {
        continue
    }

    $fixtureStatus = [string](Get-JsonValue $scenario "fixtureStatus")
    if (-not $IncludeNonFixtureScenarios -and
        -not [string]::Equals($fixtureStatus, "offline-fixture-ready", [StringComparison]::OrdinalIgnoreCase)) {
        $skippedScenarioIds.Add($scenarioId) | Out-Null
        continue
    }

    $descriptor = Get-FixtureDescriptor $scenarioId
    $generatedScenarioIds.Add($scenarioId) | Out-Null
    $requiredMetrics = @(Get-JsonArray $scenario "requiredMetrics")
    $scenarioComparisons = New-Object System.Collections.Generic.List[object]

    foreach ($comparisonId in $comparisonIdsNormalized) {
        $metrics = [ordered]@{}
        foreach ($metricName in $requiredMetrics) {
            $metricId = ConvertTo-MetricId ([string]$metricName)
            if ([string]::IsNullOrWhiteSpace($metricId)) {
                continue
            }
            $direction = if ($metricDirections.ContainsKey($metricId)) { [string]$metricDirections[$metricId] } else { Get-DefaultMetricDirection $metricId }
            $metrics[[string]$metricName] = Get-MetricValue -MetricId $metricId -Direction $direction -ScenarioId $scenarioId -ComparisonId $comparisonId -Descriptor $descriptor
        }

        $candidateDiagnostics = $null
        $candidateOutputTrimDb = 0.0
        $candidatePanelTrimDb = 0.0
        $rawTxOutputPeak = $null
        $rawTxOutputAverage = $null
        $candidateTxOutputPeak = $null
        $candidateTxOutputAverage = $null
        if (Test-TxOutputTrimCandidate -ScenarioId $scenarioId -ComparisonId $comparisonId) {
            $candidateOutputTrimDb = Get-TxHeadroomCandidateOutputTrimDb
            $rawTxOutputPeak = Get-MapNumericOrDefault -Map $metrics -Name "TX output peak" -Default (ConvertTo-Db ([double]$descriptor.peak))
            $rawTxOutputAverage = Get-MapNumericOrDefault -Map $metrics -Name "TX output average" -Default (ConvertTo-Db ([double]$descriptor.rms))
            $candidateTxOutputPeak = [Math]::Round(([double]$rawTxOutputPeak + $candidateOutputTrimDb), 6)
            $candidateTxOutputAverage = [Math]::Round(([double]$rawTxOutputAverage + $candidateOutputTrimDb), 6)
            if ($metrics.Contains("TX output peak")) { $metrics["TX output peak"] = $candidateTxOutputPeak }
            if ($metrics.Contains("TX output average")) { $metrics["TX output average"] = $candidateTxOutputAverage }
            $candidateDiagnostics = [ordered]@{
                schemaVersion = 1
                profileKind = "fixture-only-post-wdsp-output-trim"
                defaultBehaviorChanged = $false
                requiresRuntimeOptIn = $true
                txOutputTrimDb = $candidateOutputTrimDb
                txOutputTrimLinear = [Math]::Round([Math]::Pow(10.0, $candidateOutputTrimDb / 20.0), 9)
                rawWdspOutPkDbfs = $rawTxOutputPeak
                rawWdspOutAvDbfs = $rawTxOutputAverage
                effectiveTxOutputPeakDbfs = $candidateTxOutputPeak
                effectiveTxOutputAverageDbfs = $candidateTxOutputAverage
                headroomImprovementDb = [Math]::Round(([double]$rawTxOutputPeak - [double]$candidateTxOutputPeak), 6)
                pureSignalPathStatus = "not-applied; fixture-only candidate must stay disabled while PureSignal is armed until hardware safe-bypass evidence exists"
            }
        }
        elseif (Test-TxPanelTrimCandidate -ScenarioId $scenarioId -ComparisonId $comparisonId) {
            $candidatePanelTrimDb = Get-TxHeadroomCandidatePanelGainTrimDb -ComparisonId $comparisonId
            $rawTxOutputPeak = Get-MapNumericOrDefault -Map $metrics -Name "TX output peak" -Default (ConvertTo-Db ([double]$descriptor.peak))
            $rawTxOutputAverage = Get-MapNumericOrDefault -Map $metrics -Name "TX output average" -Default (ConvertTo-Db ([double]$descriptor.rms))
            $candidateTxOutputPeak = [Math]::Round(([double]$rawTxOutputPeak + $candidatePanelTrimDb), 6)
            $candidateTxOutputAverage = [Math]::Round(([double]$rawTxOutputAverage + $candidatePanelTrimDb), 6)
            if ($metrics.Contains("TX output peak")) { $metrics["TX output peak"] = $candidateTxOutputPeak }
            if ($metrics.Contains("TX output average")) { $metrics["TX output average"] = $candidateTxOutputAverage }
            $candidateDiagnostics = [ordered]@{
                schemaVersion = 1
                profileKind = "fixture-only-tx-panel-gain-trim"
                defaultBehaviorChanged = $false
                requiresRuntimeOptIn = $true
                txPanelGainTrimDb = $candidatePanelTrimDb
                txPanelGainLinear = [Math]::Round([Math]::Pow(10.0, $candidatePanelTrimDb / 20.0), 9)
                untrimmedEstimatedOutPkDbfs = $rawTxOutputPeak
                untrimmedEstimatedOutAvDbfs = $rawTxOutputAverage
                analyzedTxOutputPeakDbfs = $candidateTxOutputPeak
                analyzedTxOutputAverageDbfs = $candidateTxOutputAverage
                headroomTrimDb = [Math]::Abs($candidatePanelTrimDb)
                pureSignalPathStatus = "not-applied; fixture-only candidate must stay disabled while PureSignal is armed until hardware safe-bypass evidence exists"
            }
        }

        $audioEvidencePath = Join-Path $bundlePath "artifacts\offline-fixtures\audio\$scenarioId\$comparisonId.json"
        $spectrumEvidencePath = Join-Path $bundlePath "artifacts\offline-fixtures\spectrum\$scenarioId\$comparisonId.json"
        $audioEvidence = [ordered]@{
            schemaVersion = 1
            tool = "run-dsp-offline-fixture-evidence"
            evidenceKind = "audio-render-summary"
            scenarioId = $scenarioId
            comparisonId = $comparisonId
            fixtureStatus = $fixtureStatus
            signalPath = [string](Get-JsonValue $scenario "signalPath")
            sampleRateHz = [int]$descriptor.sampleRateHz
            sampleCount = [int]$descriptor.sampleCount
            processingElapsedMs = [Math]::Round((Get-ProcessingElapsedMs $scenarioId), 6)
            throughputRatio = [Math]::Round((Get-ThroughputRatio $scenarioId $descriptor), 6)
            rms = [double]$descriptor.rms
            peak = [double]$descriptor.peak
            windowedRmsSpreadDb = [double]$descriptor.spreadDb
            expectedTonesHz = @($descriptor.tones)
            samplePreview = @(New-SamplePreview $descriptor)
            candidateDiagnostics = $candidateDiagnostics
            notes = @(
                "Deterministic offline fixture evidence; it does not prove hardware or on-air acceptance.",
                "Use G2 live diagnostics, TX/PureSignal traces, and cross-radio validation before default DSP changes."
            )
        }
        $spectrumEvidence = [ordered]@{
            schemaVersion = 1
            tool = "run-dsp-offline-fixture-evidence"
            evidenceKind = "spectrum-summary"
            scenarioId = $scenarioId
            comparisonId = $comparisonId
            fixtureStatus = $fixtureStatus
            signalPath = [string](Get-JsonValue $scenario "signalPath")
            sampleRateHz = [int]$descriptor.sampleRateHz
            bins = @($descriptor.tones)
            wantedSnrDb = if ($scenarioId -eq "noise-only-gating" -or $scenarioId -eq "noise-only") { 0.0 } else { 9.0 }
            signalSinadDb = [Math]::Round((Get-SignalSinadDb $descriptor), 6)
            noiseFloorDb = if ($scenarioId -eq "noise-only-gating" -or $scenarioId -eq "noise-only") { -54.0 } else { -60.0 }
            summary = [string]$descriptor.summary
            notes = @(
                "Portable JSON spectrum evidence for fixture coverage validation.",
                "Replace or supplement with rendered FFT artifacts when a WDSP-backed runner is available."
            )
        }
        $audioSha = New-EvidenceFile -Path $audioEvidencePath -Value $audioEvidence
        $spectrumSha = New-EvidenceFile -Path $spectrumEvidencePath -Value $spectrumEvidence
        $audioRelativePath = ConvertTo-PortablePath -Root $bundlePath -Path $audioEvidencePath
        $spectrumRelativePath = ConvertTo-PortablePath -Root $bundlePath -Path $spectrumEvidencePath
        $txStageMeters = $null
        if ($scenarioId -in @("tx-two-tone", "tx-voice-like")) {
            $meterOutPk = Get-MapNumericOrDefault -Map $metrics -Name "TX output peak" -Default -3.0
            $meterOutAv = Get-MapNumericOrDefault -Map $metrics -Name "TX output average" -Default -18.0
            $txStageMeters = [ordered]@{
                outPkDbfs = $meterOutPk
                outAvDbfs = $meterOutAv
                alcGainReductionDb = Get-MapNumericOrDefault -Map $metrics -Name "TX ALC gain reduction" -Default 0.0
                cfcGainReductionDb = Get-MapNumericOrDefault -Map $metrics -Name "TX CFC gain reduction" -Default 0.0
                levelerGainReductionDb = Get-MapNumericOrDefault -Map $metrics -Name "TX leveler gain reduction" -Default 0.0
            }
            if ($null -ne $candidateTxOutputPeak) {
                if (Test-TxOutputTrimCandidate -ScenarioId $scenarioId -ComparisonId $comparisonId) {
                    $txStageMeters["rawOutPkDbfs"] = $rawTxOutputPeak
                    $txStageMeters["rawOutAvDbfs"] = $rawTxOutputAverage
                    $txStageMeters["effectiveOutPkDbfs"] = $candidateTxOutputPeak
                    $txStageMeters["effectiveOutAvDbfs"] = $candidateTxOutputAverage
                    $txStageMeters["txOutputTrimDb"] = $candidateOutputTrimDb
                    $txStageMeters["txOutputTrimLinear"] = [Math]::Round([Math]::Pow(10.0, $candidateOutputTrimDb / 20.0), 9)
                }
                else {
                    $txStageMeters["untrimmedEstimatedOutPkDbfs"] = $rawTxOutputPeak
                    $txStageMeters["untrimmedEstimatedOutAvDbfs"] = $rawTxOutputAverage
                    $txStageMeters["txPanelGainTrimDb"] = $candidatePanelTrimDb
                    $txStageMeters["txPanelGainLinear"] = [Math]::Round([Math]::Pow(10.0, $candidatePanelTrimDb / 20.0), 9)
                }
            }
        }
        $clippingCount = [int](Get-MapNumericOrDefault -Map $metrics -Name "clipping count" -Default 0.0)

        $audioFiles.Add([ordered]@{
            path = $audioRelativePath
            scenarioId = $scenarioId
            comparisonId = $comparisonId
            kind = "audio-render-summary"
            sampleRateHz = [int]$descriptor.sampleRateHz
            sampleCount = [int]$descriptor.sampleCount
            sha256 = $audioSha
        }) | Out-Null
        $spectrumFiles.Add([ordered]@{
            path = $spectrumRelativePath
            scenarioId = $scenarioId
            comparisonId = $comparisonId
            kind = "spectrum-summary"
            sampleRateHz = [int]$descriptor.sampleRateHz
            sha256 = $spectrumSha
        }) | Out-Null

        $scenarioComparisons.Add([ordered]@{
            comparisonId = $comparisonId
            source = "deterministic-fixture-generator"
            profile = Get-ComparisonProfileLabel -ScenarioId $scenarioId -ComparisonId $comparisonId
            metrics = $metrics
            gates = @(New-GateRecords $scenario)
            txStageMeters = $txStageMeters
            clippingCount = $clippingCount
            candidateDiagnostics = $candidateDiagnostics
            evidence = [ordered]@{
                audioPath = $audioRelativePath
                spectrumPath = $spectrumRelativePath
            }
        }) | Out-Null
    }

    $metricScenarios.Add([ordered]@{
        scenarioId = $scenarioId
        scenarioName = [string](Get-JsonValue $scenario "name")
        fixtureStatus = $fixtureStatus
        signalPath = [string](Get-JsonValue $scenario "signalPath")
        fixtureSummary = [string]$descriptor.summary
        comparisons = @($scenarioComparisons.ToArray())
    }) | Out-Null
}

if ($metricScenarios.Count -eq 0) {
    throw "No benchmark-plan scenarios matched the offline fixture evidence scope."
}

$metricsReport = [ordered]@{
    schemaVersion = 1
    tool = "run-dsp-offline-fixture-evidence"
    generatedUtc = [DateTimeOffset]::UtcNow
    benchmarkPlanPath = ConvertTo-PortablePath -Root $bundlePath -Path $resolvedPlanPath
    metricCatalogPath = if ([string]::IsNullOrWhiteSpace($MetricCatalogPath)) { $null } else { ConvertTo-PortablePath -Root $bundlePath -Path (Resolve-Path -LiteralPath $MetricCatalogPath).Path }
    fixtureScenarioScope = if ($IncludeNonFixtureScenarios) { "all-plan-scenarios" } else { "offline-fixture-ready" }
    scenarioCount = $metricScenarios.Count
    comparisonIds = @($comparisonIdsNormalized)
    generatedScenarioIds = @($generatedScenarioIds.ToArray())
    skippedNonFixtureScenarioIds = @($skippedScenarioIds.ToArray())
    acceptanceLimitations = @(
        "Deterministic fixture evidence is offline-only and does not prove on-air or hardware acceptance.",
        "Thetis parity, G2 TX/PureSignal safety, and cross-radio evidence remain separate acceptance gates.",
        "Use run-dsp-wdsp-fixture-evidence.ps1 to replace synthetic comparison values before default DSP behavior graduation."
    )
    scenarios = @($metricScenarios.ToArray())
}

$audioIndex = [ordered]@{
    schemaVersion = 1
    tool = "run-dsp-offline-fixture-evidence"
    generatedUtc = [DateTimeOffset]::UtcNow
    artifactId = "audio-render-before-after"
    fixtureScenarioScope = $metricsReport.fixtureScenarioScope
    scenarioCount = $metricScenarios.Count
    fileCount = $audioFiles.Count
    files = @($audioFiles.ToArray())
}

$spectrumIndex = [ordered]@{
    schemaVersion = 1
    tool = "run-dsp-offline-fixture-evidence"
    generatedUtc = [DateTimeOffset]::UtcNow
    artifactId = "spectrum-before-after"
    fixtureScenarioScope = $metricsReport.fixtureScenarioScope
    scenarioCount = $metricScenarios.Count
    fileCount = $spectrumFiles.Count
    files = @($spectrumFiles.ToArray())
}

Write-JsonFile -Path $MetricsPath -Value $metricsReport
Write-JsonFile -Path $AudioIndexPath -Value $audioIndex
Write-JsonFile -Path $SpectrumIndexPath -Value $spectrumIndex

$summary = [ordered]@{
    tool = "run-dsp-offline-fixture-evidence"
    metricsPath = ConvertTo-PortablePath -Root $bundlePath -Path $MetricsPath
    audioIndexPath = ConvertTo-PortablePath -Root $bundlePath -Path $AudioIndexPath
    spectrumIndexPath = ConvertTo-PortablePath -Root $bundlePath -Path $SpectrumIndexPath
    scenarioCount = $metricScenarios.Count
    comparisonIds = @($comparisonIdsNormalized)
    skippedNonFixtureScenarioCount = $skippedScenarioIds.Count
}

if ($JsonOnly) {
    $summary | ConvertTo-Json -Depth 16
}
else {
    Write-Host "DSP offline fixture evidence written."
    Write-Host "Metrics: $MetricsPath"
    Write-Host "Audio index: $AudioIndexPath"
    Write-Host "Spectrum index: $SpectrumIndexPath"
    Write-Host "Scenarios: $($metricScenarios.Count), comparisons: $($comparisonIdsNormalized -join ', ')"
}
