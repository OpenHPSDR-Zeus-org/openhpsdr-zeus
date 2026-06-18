param(
    [string]$BaseUrl = "http://localhost:6060",

    [int]$Samples = 15,

    [int]$IntervalMs = 1000,

    [int]$TimeoutSec = 5,

    [string]$InputPath = "",

    [string]$OutputRoot = "",

    [string]$Label = "",

    [string]$ScenarioId = "",

    [string]$ComparisonId = "",

    [string]$ReportPath = "",

    [string]$JsonlPath = "",

    [switch]$PlanOnly,

    [switch]$JsonOnly,

    [switch]$SkipCertificateCheck,

    [switch]$Realtime,

    [int]$RealtimeEvery = 1,

    [switch]$Preflight,

    [switch]$FailOnHardGate,

    [switch]$ContinueOnError,

    [int]$TuneStepHz = 1000
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

function Invoke-PwshRelaunchIfNeeded {
    param([Parameter(Mandatory = $true)][string]$ScriptPath)

    if ($env:ZEUS_DSP_WATCH_PWSH_RELAUNCHED -eq "1") {
        return
    }

    if ($PSVersionTable.PSVersion.Major -ge 6) {
        return
    }

    if (-not $SkipCertificateCheck -or -not $BaseUrl.StartsWith("https://", [StringComparison]::OrdinalIgnoreCase)) {
        return
    }

    $pwsh = Get-Command pwsh -ErrorAction SilentlyContinue
    if ($null -eq $pwsh) {
        return
    }

    $args = New-Object System.Collections.Generic.List[string]
    foreach ($item in @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", $ScriptPath,
        "-BaseUrl", $BaseUrl,
        "-Samples", ([string]$Samples),
        "-IntervalMs", ([string]$IntervalMs),
        "-TimeoutSec", ([string]$TimeoutSec),
        "-TuneStepHz", ([string]$TuneStepHz)
    )) {
        $args.Add($item) | Out-Null
    }

    foreach ($pair in @(
        @{ Name = "-InputPath"; Value = $InputPath },
        @{ Name = "-OutputRoot"; Value = $OutputRoot },
        @{ Name = "-Label"; Value = $Label },
        @{ Name = "-ScenarioId"; Value = $ScenarioId },
        @{ Name = "-ComparisonId"; Value = $ComparisonId },
        @{ Name = "-ReportPath"; Value = $ReportPath },
        @{ Name = "-JsonlPath"; Value = $JsonlPath }
    )) {
        if (-not [string]::IsNullOrWhiteSpace($pair.Value)) {
            $args.Add($pair.Name) | Out-Null
            $args.Add($pair.Value) | Out-Null
        }
    }

    $args.Add("-RealtimeEvery") | Out-Null
    $args.Add([string]$RealtimeEvery) | Out-Null

    foreach ($switchName in @("-PlanOnly", "-JsonOnly", "-SkipCertificateCheck", "-Realtime", "-Preflight", "-FailOnHardGate", "-ContinueOnError")) {
        switch ($switchName) {
            "-PlanOnly" { if ($PlanOnly) { $args.Add($switchName) | Out-Null } }
            "-JsonOnly" { if ($JsonOnly) { $args.Add($switchName) | Out-Null } }
            "-SkipCertificateCheck" { if ($SkipCertificateCheck) { $args.Add($switchName) | Out-Null } }
            "-Realtime" { if ($Realtime) { $args.Add($switchName) | Out-Null } }
            "-Preflight" { if ($Preflight) { $args.Add($switchName) | Out-Null } }
            "-FailOnHardGate" { if ($FailOnHardGate) { $args.Add($switchName) | Out-Null } }
            "-ContinueOnError" { if ($ContinueOnError) { $args.Add($switchName) | Out-Null } }
        }
    }

    $env:ZEUS_DSP_WATCH_PWSH_RELAUNCHED = "1"
    & $pwsh.Source @args
    $exitCode = $LASTEXITCODE
    Remove-Item Env:\ZEUS_DSP_WATCH_PWSH_RELAUNCHED -ErrorAction SilentlyContinue
    exit $exitCode
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

    $json = $Value | ConvertTo-Json -Depth 48
    Set-Content -LiteralPath $Path -Value $json -Encoding UTF8
}

function Add-JsonLine {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Value
    )

    $parent = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }

    $json = ($Value | ConvertTo-Json -Depth 48 -Compress) + [Environment]::NewLine
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    $lastError = $null
    for ($attempt = 0; $attempt -lt 8; $attempt++) {
        try {
            $stream = [System.IO.File]::Open(
                $Path,
                [System.IO.FileMode]::Append,
                [System.IO.FileAccess]::Write,
                [System.IO.FileShare]::ReadWrite)
            try {
                $stream.Write($bytes, 0, $bytes.Length)
            }
            finally {
                $stream.Dispose()
            }
            return
        }
        catch [System.IO.IOException] {
            $lastError = $_
            Start-Sleep -Milliseconds (25 * ($attempt + 1))
        }
    }

    if ($null -ne $lastError) {
        throw $lastError
    }
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

function Convert-FrontendTopPeak {
    param($Peak)

    if ($null -eq $Peak) {
        return $null
    }

    return [ordered]@{
        frequencyHz = Get-JsonValue $Peak "frequencyHz"
        offsetHz = Get-JsonValue $Peak "offsetHz"
        snrDb = Get-JsonValue $Peak "snrDb"
        dbfs = Get-JsonValue $Peak "dbfs"
        confidence = Get-JsonValue $Peak "confidence"
        coherent = Test-Truthy (Get-JsonValue $Peak "coherent")
    }
}

function Get-NearestFrontendTopPeak {
    param([object[]]$Peaks)

    $nearest = $null
    $nearestAbsOffset = [double]::PositiveInfinity
    foreach ($peak in @($Peaks)) {
        $offset = Get-NumericValue (Get-JsonValue $peak "offsetHz")
        if ($null -eq $offset) {
            continue
        }

        $absOffset = [Math]::Abs([double]$offset)
        if ($absOffset -lt $nearestAbsOffset) {
            $nearest = $peak
            $nearestAbsOffset = $absOffset
        }
    }

    return $nearest
}

function Get-StrongestFrontendTopPeak {
    param([object[]]$Peaks)

    $strongest = $null
    $strongestSnr = [double]::NegativeInfinity
    foreach ($peak in @($Peaks)) {
        $snr = Get-NumericValue (Get-JsonValue $peak "snrDb")
        if ($null -eq $snr) {
            continue
        }

        if ([double]$snr -gt $strongestSnr) {
            $strongest = $peak
            $strongestSnr = [double]$snr
        }
    }

    return $strongest
}

function Get-FrontendPeakFilterDistanceHz {
    param(
        $Peak,
        $FilterLowHz,
        $FilterHighHz
    )

    $offset = Get-NumericValue (Get-JsonValue $Peak "offsetHz")
    $low = Get-NumericValue $FilterLowHz
    $high = Get-NumericValue $FilterHighHz
    if ($null -eq $offset -or $null -eq $low -or $null -eq $high) {
        return $null
    }

    $passLow = [Math]::Min([double]$low, [double]$high)
    $passHigh = [Math]::Max([double]$low, [double]$high)
    if ($passHigh -le $passLow) {
        return $null
    }

    if ([double]$offset -lt $passLow) {
        return [Math]::Round($passLow - [double]$offset, 3)
    }

    if ([double]$offset -gt $passHigh) {
        return [Math]::Round([double]$offset - $passHigh, 3)
    }

    return 0.0
}

function Get-FrontendTuneCandidates {
    param(
        [object[]]$Samples,
        [int]$MaxCount = 6,
        [int]$TuneStepHz = 1000
    )

    $effectiveTuneStepHz = Normalize-TuneStepHz $TuneStepHz
    $byBucket = @{}
    foreach ($sample in @($Samples)) {
        $low = Get-NumericValue (Get-JsonValue $sample "filterLowHz")
        $high = Get-NumericValue (Get-JsonValue $sample "filterHighHz")
        if ($null -eq $low -or $null -eq $high) {
            continue
        }

        $passLow = [Math]::Min([double]$low, [double]$high)
        $passHigh = [Math]::Max([double]$low, [double]$high)
        if ($passHigh -le $passLow) {
            continue
        }

        $passbandCenterHz = ($passLow + $passHigh) / 2.0
        foreach ($sourceName in @("strongest", "nearestFilterPassbandPeak", "nearest")) {
            $peak = Get-JsonValue $sample $sourceName
            if ($null -eq $peak) {
                continue
            }

            $frequency = Get-NumericValue (Get-JsonValue $peak "frequencyHz")
            if ($null -eq $frequency) {
                continue
            }

            $offset = Get-NumericValue (Get-JsonValue $peak "offsetHz")
            $snr = Get-NumericValue (Get-JsonValue $peak "snrDb")
            $dbfs = Get-NumericValue (Get-JsonValue $peak "dbfs")
            $confidence = Get-NumericValue (Get-JsonValue $peak "confidence")
            $distance = Get-FrontendPeakFilterDistanceHz -Peak $peak -FilterLowHz $low -FilterHighHz $high
            if ($null -eq $distance) {
                continue
            }

            $distanceForScore = [double]$distance
            $rawSuggestedVfoHz = [long][Math]::Round([double]$frequency - $passbandCenterHz)
            if ($rawSuggestedVfoHz -le 0) {
                continue
            }

            $suggestedVfoHz = Quantize-HzToStep -Hz ([double]$rawSuggestedVfoHz) -StepHz $effectiveTuneStepHz
            if ($suggestedVfoHz -le 0) {
                continue
            }

            $bucketHz = $suggestedVfoHz
            $snrForScore = if ($null -eq $snr) { 0.0 } else { [double]$snr }
            $sourceBonus = if ([string]::Equals($sourceName, "strongest", [StringComparison]::OrdinalIgnoreCase)) { 1.0 } else { 0.0 }
            $retuneBonus = [Math]::Min(8.0, $distanceForScore / 1000.0) * 0.75
            $score = [Math]::Round($snrForScore + $sourceBonus + $retuneBonus, 3)
            if ($byBucket.ContainsKey($bucketHz) -and [double](Get-JsonValue $byBucket[$bucketHz] "score") -ge $score) {
                continue
            }

            $byBucket[$bucketHz] = [ordered]@{
                rank = 0
                suggestedVfoHz = $suggestedVfoHz
                suggestedVfoMhz = [Math]::Round($suggestedVfoHz / 1000000.0, 6)
                suggestedVfoStepHz = $effectiveTuneStepHz
                exactSuggestedVfoHz = $rawSuggestedVfoHz
                exactSuggestedVfoMhz = [Math]::Round($rawSuggestedVfoHz / 1000000.0, 6)
                rawSuggestedVfoHz = $rawSuggestedVfoHz
                rawSuggestedVfoMhz = [Math]::Round($rawSuggestedVfoHz / 1000000.0, 6)
                tuningStepHz = $effectiveTuneStepHz
                tuneSnapDeltaHz = [long]($suggestedVfoHz - $rawSuggestedVfoHz)
                peakFrequencyHz = [long][Math]::Round([double]$frequency)
                peakFrequencyMhz = [Math]::Round([double]$frequency / 1000000.0, 6)
                peakOffsetHz = if ($null -eq $offset) { $null } else { [long][Math]::Round([double]$offset) }
                filterDistanceHz = if ($null -eq $distance) { $null } else { [long][Math]::Round([double]$distance) }
                snrDb = if ($null -eq $snr) { $null } else { [Math]::Round([double]$snr, 1) }
                dbfs = if ($null -eq $dbfs) { $null } else { [Math]::Round([double]$dbfs, 1) }
                confidence = if ($null -eq $confidence) { $null } else { [Math]::Round([double]$confidence, 3) }
                source = $sourceName
                sampleIndex = Get-JsonValue $sample "sampleIndex"
                passbandCenterHz = [Math]::Round($passbandCenterHz, 1)
                reason = if ($distanceForScore -le 0.0) { "peak-already-in-passband" } else { "retune-to-center-frontend-peak" }
                score = $score
            }
        }
    }

    $rank = 0
    return @($byBucket.Values |
        Sort-Object @{Expression = { [double](Get-JsonValue $_ "score") }; Descending = $true },
            @{Expression = { [double](Get-JsonValue $_ "snrDb") }; Descending = $true } |
        Select-Object -First $MaxCount |
        ForEach-Object {
            $rank++
            $_["rank"] = $rank
            $_
        })
}

function Get-GapDirection {
    param(
        $GapDb,
        [double]$ThresholdDb
    )

    $gap = Get-NumericValue $GapDb
    if ($null -eq $gap) {
        return "unknown"
    }

    if ([double]$gap -gt $ThresholdDb) {
        return "weak-too-low"
    }

    if ([double]$gap -lt (-1.0 * $ThresholdDb)) {
        return "weak-too-hot"
    }

    return "within-parity"
}

function Get-GapExcessDb {
    param(
        $GapDb,
        [double]$ThresholdDb
    )

    $gap = Get-NumericValue $GapDb
    if ($null -eq $gap) {
        return $null
    }

    return [Math]::Round([Math]::Max(0.0, [Math]::Abs([double]$gap) - $ThresholdDb), 3)
}

function New-Nr5ClassifiedInputSample {
    param(
        [Parameter(Mandatory = $true)][string]$Class,
        $Sample,
        $InputDbfs,
        $OutputDbfs,
        $FinalAudioRmsDbfs,
        $LevelerInputRmsDbfs,
        $LevelerOutputRmsDbfs,
        $LevelerAppliedGainDb,
        $SignalConfidence,
        $AgcGate,
        $SignalProbability,
        $PeakEvidence,
        $MakeupGainDb,
        $RecoveryDrive,
        $WeakSignalMemory,
        [bool]$NearPassbandPeak,
        [bool]$FilterPassbandPeak,
        [bool]$PassbandEvidencePeak,
        $NearestFilterPassbandDistanceHz,
        $NearestPeak,
        $StrongestPeak,
        [bool]$SpeechQualified,
        [bool]$PassbandQualified,
        [bool]$LowEvidence,
        [bool]$AudioAlignmentMismatch,
        $AudioInputDeltaDb
    )

    $input = Get-NumericValue $InputDbfs
    $output = Get-NumericValue $OutputDbfs
    $finalAudio = Get-NumericValue $FinalAudioRmsDbfs
    $levelerInput = Get-NumericValue $LevelerInputRmsDbfs
    $levelerOutput = Get-NumericValue $LevelerOutputRmsDbfs
    $observedLevelerGainDb = $null
    if ($null -ne $levelerInput -and $null -ne $levelerOutput) {
        $observedLevelerGainDb = [Math]::Round([double]$levelerOutput - [double]$levelerInput, 3)
    }

    $outputMinusInputDb = $null
    if ($null -ne $input -and $null -ne $output) {
        $outputMinusInputDb = [Math]::Round([double]$output - [double]$input, 3)
    }

    $finalMinusOutputDb = $null
    if ($null -ne $output -and $null -ne $finalAudio) {
        $finalMinusOutputDb = [Math]::Round([double]$finalAudio - [double]$output, 3)
    }

    return [ordered]@{
        sampleIndex = [int](Get-JsonValue $Sample "sampleIndex")
        class = $Class
        inputDbfs = $input
        outputDbfs = $output
        outputMinusInputDb = $outputMinusInputDb
        finalAudioRmsDbfs = $finalAudio
        finalAudioMinusOutputDb = $finalMinusOutputDb
        rxAudioLevelerInputRmsDbfs = $levelerInput
        rxAudioLevelerOutputRmsDbfs = $levelerOutput
        rxAudioLevelerAppliedGainDb = Get-NumericValue $LevelerAppliedGainDb
        rxAudioLevelerObservedGainDb = $observedLevelerGainDb
        signalConfidence = Get-NumericValue $SignalConfidence
        agcGate = Get-NumericValue $AgcGate
        signalProbability = Get-NumericValue $SignalProbability
        peakEvidence = Get-NumericValue $PeakEvidence
        makeupGainDb = Get-NumericValue $MakeupGainDb
        recoveryDrive = Get-NumericValue $RecoveryDrive
        weakSignalMemory = Get-NumericValue $WeakSignalMemory
        nearPassbandPeak = $NearPassbandPeak
        filterPassbandPeak = $FilterPassbandPeak
        passbandEvidencePeak = $PassbandEvidencePeak
        nearestFilterPassbandDistanceHz = Get-NumericValue $NearestFilterPassbandDistanceHz
        nearest = Convert-FrontendTopPeak $NearestPeak
        strongest = Convert-FrontendTopPeak $StrongestPeak
        speechQualified = $SpeechQualified
        passbandQualified = $PassbandQualified
        lowEvidence = $LowEvidence
        nr5AudioAlignmentMismatch = $AudioAlignmentMismatch
        nr5AudioInputDeltaDb = Get-NumericValue $AudioInputDeltaDb
    }
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

function Get-NumericValue {
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

function Normalize-TuneStepHz {
    param($Value)

    $numeric = Get-NumericValue $Value
    if ($null -eq $numeric -or [double]::IsNaN([double]$numeric) -or [double]::IsInfinity([double]$numeric)) {
        return 1000
    }

    $step = [int][Math]::Round([double]$numeric)
    if ($step -le 0) {
        return 1000
    }

    return $step
}

function Resolve-TuneStepHz {
    param(
        [string]$BaseUrl,
        [int]$RequestedTuneStepHz,
        [bool]$AllowServerLookup
    )

    if ($RequestedTuneStepHz -gt 0) {
        return Normalize-TuneStepHz $RequestedTuneStepHz
    }

    if ($AllowServerLookup) {
        try {
            $settingsEndpoint = "$(Normalize-BaseUrl $BaseUrl)/api/toolbar-settings"
            $settings = Invoke-RestMethod -Uri $settingsEndpoint -TimeoutSec 2
            return Normalize-TuneStepHz (Get-JsonValue $settings "stepHz")
        }
        catch {
            return 1000
        }
    }

    return 1000
}

function Quantize-HzToStep {
    param(
        [double]$Hz,
        [int]$StepHz
    )

    $step = Normalize-TuneStepHz $StepHz
    return [long]([Math]::Floor(($Hz / [double]$step) + 0.5) * [double]$step)
}

function Add-Count {
    param(
        [hashtable]$Map,
        [string]$Name
    )

    if ([string]::IsNullOrWhiteSpace($Name)) {
        return
    }

    if (-not $Map.ContainsKey($Name)) {
        $Map[$Name] = 0
    }

    $Map[$Name] = [int]$Map[$Name] + 1
}

function ConvertTo-CountArray {
    param([hashtable]$Map)

    $items = New-Object System.Collections.Generic.List[object]
    foreach ($key in @($Map.Keys | Sort-Object)) {
        $items.Add([ordered]@{
            name = [string]$key
            count = [int]$Map[$key]
        }) | Out-Null
    }

    return @($items.ToArray())
}

function ConvertTo-NrModeName {
    param([string]$Mode)

    $text = ([string]$Mode).Trim()
    if ([string]::IsNullOrWhiteSpace($text)) {
        return "unknown"
    }

    switch ($text.ToLowerInvariant()) {
        "off" { return "Off" }
        "nr5" { return "Nr5" }
        default { return $text }
    }
}

function Test-NrModeName {
    param(
        [string]$Mode,
        [Parameter(Mandatory = $true)][string]$Expected
    )

    return [string]::Equals((ConvertTo-NrModeName $Mode), $Expected, [StringComparison]::OrdinalIgnoreCase)
}

function Get-CountPercent {
    param(
        [int]$Count,
        [int]$SampleCount
    )

    if ($SampleCount -le 0) {
        return $null
    }

    return [Math]::Round(100.0 * [double]$Count / [double]$SampleCount, 1)
}

function Test-Nr5LevelerNormalizedSpeechAlignment {
    param(
        $Nr5OutputDbfs,
        $LevelerInputRmsDbfs,
        $LevelerOutputRmsDbfs,
        $LevelerAppliedGainDb,
        [bool]$LevelerBoostSlewLimited,
        [bool]$LevelerPeakLimited,
        [bool]$LevelerOutputLimited
    )

    $nr5Output = Get-NumericValue $Nr5OutputDbfs
    $levelerInput = Get-NumericValue $LevelerInputRmsDbfs
    $levelerOutput = Get-NumericValue $LevelerOutputRmsDbfs
    if ($null -eq $nr5Output -or $null -eq $levelerInput -or $null -eq $levelerOutput) {
        return $false
    }

    if ($LevelerBoostSlewLimited -or $LevelerPeakLimited -or $LevelerOutputLimited) {
        return $false
    }

    $observedGainDb = $levelerOutput - $levelerInput
    $appliedGain = Get-NumericValue $LevelerAppliedGainDb
    if ($null -eq $appliedGain) {
        $appliedGain = $observedGainDb
    }

    return (
        $nr5Output -ge -43.0 -and
        $nr5Output -le -30.0 -and
        $levelerInput -le -42.0 -and
        $levelerOutput -ge -23.5 -and
        $levelerOutput -le -15.0 -and
        ($levelerOutput - $nr5Output) -ge 12.0 -and
        ($observedGainDb -ge 18.0 -or $appliedGain -ge 18.0))
}

function Get-CaptureReadinessAction {
    param([string]$Name)

    switch ($Name) {
        "frontend-dsp-scene-missing" { return "Open a Zeus frontend with Signal Intelligence and Smart NR enabled, then wait for a published frontend DSP scene before capture." }
        "frontend-dsp-scene-stale" { return "Refresh or reconnect the frontend scene publisher, then wait until frontendSceneFresh=true before starting the benchmark window." }
        "frontend-dsp-scene-aging" { return "Keep the frontend tab active and wait for a fresh spectrum scene sample before tuning NR/AGC from this trace." }
        "frontend-clock-skew" { return "Fix client/host clock skew before trusting scene age, freshness, or Smart NR recommendations." }
        "smart-nr-profile-unmapped" { return "Publish Smart NR profile evidence from the frontend or map the active requested/effective NR mode before using the trace as Smart NR acceptance evidence." }
        "smart-nr-apply-pending" { return "Wait for the DSP apply latch to settle before judging active NR mode or weak-signal behavior." }
        "smart-nr-runtime-misaligned" { return "Reapply Smart NR and verify requested/effective NR mode alignment before tuning weak-signal NR." }
        "smart-nr-held-by-rx-chain" { return "Resolve RX-chain health before increasing NR aggressiveness or treating the profile recommendation as final." }
        "rx-chain-protect" { return "Fix ADC/AGC/attenuator protect state before collecting DSP acceptance evidence." }
        "rx-chain-health-poor" { return "Stabilize RX-chain health before comparing NR or AGC quality." }
        "rx-chain-health-needs-attention" { return "Review RX-chain score and front-end settings before treating the trace as clean acceptance evidence." }
        "rx-chain-optimize" { return "Optimize RX-chain posture before treating the trace as final tuning evidence." }
        "final-audio-not-fresh" { return "Restore fresh final RX audio before judging NR, AGC, or external speech engines." }
        "final-audio-clipping-risk" { return "Reduce RX leveler boost, front-end gain, or plugin output before collecting DSP acceptance audio." }
        "final-audio-muted-by-squelch" { return "Open, lower, or disable squelch before using silence as weak-signal preservation evidence." }
        "rx-meters-not-fresh" { return "Wait for fresh RXA meter evidence before using AGC gain or ADC headroom as benchmark context." }
        "adc-headroom-low" { return "Add attenuation or reduce preamp/front-end gain before evaluating NR/AGC improvements." }
        "monitor-audio-backlog" { return "Drain or stop local playback monitor injection before judging live audio fidelity." }
        "wdsp-native-unloadable" { return "Fix native WDSP packaging/loading before judging DSP quality." }
        "wdsp-inactive" { return "Connect the radio or restart the DSP engine so live WDSP telemetry is available." }
        "nr4-sbnr-exports-missing" { return "Rebuild or install WDSP with NR4/SBNR exports before evaluating NR4." }
        "nr5-spnr-exports-missing" { return "Rebuild or install WDSP with NR5/SPNR exports before evaluating NR5 weak-signal behavior." }
        "rx-state-drift" { return "Keep VFO, LO, mode, filter, CTUN, and sample rate fixed for the capture window; restart the trace after tuning settles." }
        default { return "Inspect liveRecommendedActions and sampleSummaries for affected samples before using this trace as acceptance evidence." }
    }
}

function ConvertTo-ReadinessCountArray {
    param(
        [hashtable]$Map,
        [int]$SampleCount
    )

    $items = New-Object System.Collections.Generic.List[object]
    foreach ($key in @($Map.Keys | Sort-Object)) {
        $name = [string]$key
        $count = [int]$Map[$key]
        $items.Add([ordered]@{
            name = $name
            count = $count
            pct = Get-CountPercent -Count $count -SampleCount $SampleCount
            hard = Test-HardConstraint $name
            action = Get-CaptureReadinessAction $name
        }) | Out-Null
    }

    return @($items.ToArray() | Sort-Object `
        @{ Expression = { [int](Get-JsonValue $_ "count") }; Descending = $true },
        @{ Expression = { [string](Get-JsonValue $_ "name") }; Ascending = $true })
}

function Get-TopReadinessCount {
    param([object[]]$Items)

    $valid = @($Items | Where-Object {
        -not [string]::IsNullOrWhiteSpace([string](Get-JsonValue $_ "name")) -and
        [int](Get-JsonValue $_ "count") -gt 0
    })
    if ($valid.Count -eq 0) {
        return $null
    }

    return $valid[0]
}

function Add-Number {
    param(
        [System.Collections.Generic.List[double]]$Target,
        $Value
    )

    $number = Get-NumericValue $Value
    if ($null -ne $number) {
        $Target.Add([double]$number) | Out-Null
    }
}

function Get-NumberStats {
    param([System.Collections.Generic.List[double]]$Values)

    if ($Values.Count -eq 0) {
        return [ordered]@{
            count = 0
            min = $null
            max = $null
            average = $null
            movement = $null
        }
    }

    $min = [double]::PositiveInfinity
    $max = [double]::NegativeInfinity
    $sum = 0.0
    foreach ($value in @($Values.ToArray())) {
        if ($value -lt $min) { $min = $value }
        if ($value -gt $max) { $max = $value }
        $sum += $value
    }

    return [ordered]@{
        count = $Values.Count
        min = [Math]::Round($min, 3)
        max = [Math]::Round($max, 3)
        average = [Math]::Round($sum / $Values.Count, 3)
        movement = [Math]::Round($max - $min, 3)
    }
}

function Get-PairStats {
    param(
        [System.Collections.Generic.List[double]]$X,
        [System.Collections.Generic.List[double]]$Y
    )

    $count = [Math]::Min($X.Count, $Y.Count)
    if ($count -lt 2) {
        return [ordered]@{
            count = $count
            slope = $null
            intercept = $null
            correlation = $null
        }
    }

    $sumX = 0.0
    $sumY = 0.0
    for ($i = 0; $i -lt $count; $i++) {
        $sumX += [double]$X[$i]
        $sumY += [double]$Y[$i]
    }

    $meanX = $sumX / $count
    $meanY = $sumY / $count
    $ssX = 0.0
    $ssY = 0.0
    $ssXY = 0.0
    for ($i = 0; $i -lt $count; $i++) {
        $dx = [double]$X[$i] - $meanX
        $dy = [double]$Y[$i] - $meanY
        $ssX += $dx * $dx
        $ssY += $dy * $dy
        $ssXY += $dx * $dy
    }

    $slope = $null
    $intercept = $null
    $correlation = $null
    if ($ssX -gt 1.0e-12) {
        $slope = $ssXY / $ssX
        $intercept = $meanY - $slope * $meanX
    }
    if ($ssX -gt 1.0e-12 -and $ssY -gt 1.0e-12) {
        $correlation = $ssXY / [Math]::Sqrt($ssX * $ssY)
    }

    return [ordered]@{
        count = $count
        slope = if ($null -eq $slope) { $null } else { [Math]::Round($slope, 3) }
        intercept = if ($null -eq $intercept) { $null } else { [Math]::Round($intercept, 3) }
        correlation = if ($null -eq $correlation) { $null } else { [Math]::Round($correlation, 3) }
    }
}

function Test-HardConstraint {
    param([string]$Constraint)

    return $Constraint -in @(
        "wdsp-native-unloadable",
        "wdsp-inactive",
        "frontend-dsp-scene-missing",
        "frontend-dsp-scene-stale",
        "frontend-clock-skew",
        "nr4-sbnr-exports-missing",
        "nr5-spnr-exports-missing",
        "smart-nr-runtime-misaligned",
        "rx-chain-protect",
        "final-audio-not-fresh",
        "final-audio-clipping-risk",
        "adc-headroom-low"
    )
}

function Get-DiagnosticsFromSample {
    param($Sample)

    $diagnostics = Get-JsonValue $Sample "diagnostics"
    if ($null -ne $diagnostics) {
        return $diagnostics
    }

    $live = Get-JsonValue $Sample "liveDiagnostics"
    if ($null -ne $live) {
        return $live
    }

    if ($null -ne (Get-JsonValue $Sample "status") -and $null -ne (Get-JsonValue $Sample "schemaVersion")) {
        return $Sample
    }

    return $null
}

function New-SampleRecord {
    param(
        [int]$Index,
        [DateTimeOffset]$SampledUtc,
        [bool]$Ok,
        [int]$LatencyMs,
        $Diagnostics,
        [int]$StatusCode = 0,
        [string]$ErrorMessage = ""
    )

    return [ordered]@{
        schemaVersion = 1
        sampleIndex = $Index
        sampledUtc = $SampledUtc
        ok = $Ok
        statusCode = if ($StatusCode -gt 0) { $StatusCode } else { $null }
        latencyMs = if ($LatencyMs -ge 0) { $LatencyMs } else { $null }
        error = if ([string]::IsNullOrWhiteSpace($ErrorMessage)) { $null } else { $ErrorMessage }
        diagnostics = $Diagnostics
    }
}

function Read-InputSamples {
    param([Parameter(Mandatory = $true)][string]$Path)

    $resolved = (Resolve-Path -LiteralPath $Path).Path
    $records = New-Object System.Collections.Generic.List[object]
    $extension = [System.IO.Path]::GetExtension($resolved)

    if ($extension -ieq ".jsonl") {
        $index = 0
        foreach ($line in (Get-Content -LiteralPath $resolved)) {
            if ([string]::IsNullOrWhiteSpace($line)) {
                continue
            }

            $index++
            $sample = Read-JsonText -Text $line -Source "$resolved line $index"
            $diagnostics = Get-DiagnosticsFromSample $sample
            if ($null -eq $diagnostics) {
                throw "Input JSONL line $index does not contain a live diagnostics object."
            }

            $sampleIndex = Get-NumericValue (Get-JsonValue $sample "sampleIndex")
            if ($null -eq $sampleIndex) {
                $sampleIndex = $index
            }

            $sampledUtc = Get-JsonValue $sample "sampledUtc"
            if ($null -eq $sampledUtc) {
                $sampledUtc = [DateTimeOffset]::UtcNow
            }
            $okValue = Get-JsonValue $sample "ok"
            if ($null -eq $okValue) {
                $ok = $true
            }
            else {
                $ok = Test-Truthy $okValue
            }
            $latencyValue = Get-NumericValue (Get-JsonValue $sample "latencyMs")
            if ($null -eq $latencyValue) {
                $latencyMs = -1
            }
            else {
                $latencyMs = [int]$latencyValue
            }

            $records.Add((New-SampleRecord `
                -Index ([int]$sampleIndex) `
                -SampledUtc ([DateTimeOffset]$sampledUtc) `
                -Ok $ok `
                -LatencyMs $latencyMs `
                -Diagnostics $diagnostics)) | Out-Null
        }

        return @($records.ToArray())
    }

    $json = Read-JsonFile $resolved
    $items = @()
    $samples = Get-JsonArray $json "samples"
    if ($samples.Count -gt 0) {
        $items = @($samples)
    }
    elseif ($json -is [System.Array]) {
        $items = @($json)
    }
    else {
        $items = @($json)
    }

    $index = 0
    foreach ($item in @($items)) {
        $index++
        $diagnostics = Get-DiagnosticsFromSample $item
        if ($null -eq $diagnostics) {
            throw "Input JSON item $index does not contain a live diagnostics object."
        }

        $records.Add((New-SampleRecord `
            -Index $index `
            -SampledUtc ([DateTimeOffset]::UtcNow) `
            -Ok $true `
            -LatencyMs -1 `
            -Diagnostics $diagnostics)) | Out-Null
    }

    return @($records.ToArray())
}

function Invoke-LiveSamples {
    param(
        [Parameter(Mandatory = $true)][string]$Endpoint,
        [int]$Count,
        [int]$DelayMs,
        [int]$RequestTimeoutSec,
        [string]$LinePath
    )

    $webRequestCommand = Get-Command Invoke-WebRequest
    $webRequestSupportsCertificateSkip = $webRequestCommand.Parameters.ContainsKey("SkipCertificateCheck")
    $webRequestSupportsBasicParsing = $webRequestCommand.Parameters.ContainsKey("UseBasicParsing")
    $records = New-Object System.Collections.Generic.List[object]
    for ($i = 1; $i -le $Count; $i++) {
        $sampledUtc = [DateTimeOffset]::UtcNow
        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        $record = $null

        try {
            $requestArgs = @{
                Uri = $Endpoint
                Method = "Get"
                Headers = @{ Accept = "application/json" }
                TimeoutSec = $RequestTimeoutSec
            }
            if ($SkipCertificateCheck -and $webRequestSupportsCertificateSkip) {
                $requestArgs["SkipCertificateCheck"] = $true
            }
            if ($webRequestSupportsBasicParsing) {
                $requestArgs["UseBasicParsing"] = $true
            }
            $response = Invoke-WebRequest @requestArgs
            $watch.Stop()
            $diagnostics = Read-JsonText -Text $response.Content -Source $Endpoint
            $record = New-SampleRecord `
                -Index $i `
                -SampledUtc $sampledUtc `
                -Ok $true `
                -LatencyMs ([int]$watch.ElapsedMilliseconds) `
                -StatusCode ([int]$response.StatusCode) `
                -Diagnostics $diagnostics
        }
        catch {
            $watch.Stop()
            $statusCode = 0
            if ($null -ne $_.Exception.Response) {
                try {
                    $statusCode = [int]$_.Exception.Response.StatusCode
                }
                catch {
                    $statusCode = 0
                }
            }

            $record = New-SampleRecord `
                -Index $i `
                -SampledUtc $sampledUtc `
                -Ok $false `
                -LatencyMs ([int]$watch.ElapsedMilliseconds) `
                -StatusCode $statusCode `
                -Diagnostics $null `
                -ErrorMessage $_.Exception.Message
        }

        $records.Add($record) | Out-Null
        if (-not [string]::IsNullOrWhiteSpace($LinePath)) {
            Add-JsonLine -Path $LinePath -Value $record
        }
        if ($Realtime -and $RealtimeEvery -gt 0 -and (($i % $RealtimeEvery) -eq 0 -or $i -eq $Count)) {
            Write-RealtimeSample $record
        }

        if ($i -lt $Count -and $DelayMs -gt 0) {
            Start-Sleep -Milliseconds $DelayMs
        }
    }

    return @($records.ToArray())
}

function New-SampleSummary {
    param($Sample)

    $diagnostics = Get-JsonValue $Sample "diagnostics"
    $runtime = Get-JsonValue $diagnostics "runtimeEvidence"
    $nr5 = Get-JsonValue $diagnostics "nr5SpnrDiagnostics"
    $nr5InputDbfs = Get-NumericValue (Get-JsonValue $nr5 "inputDbfs")
    $nr5OutputDbfs = Get-NumericValue (Get-JsonValue $nr5 "outputDbfs")
    $nr5OutputMinusInputDb = $null
    if ($null -ne $nr5InputDbfs -and $null -ne $nr5OutputDbfs) {
        $nr5OutputMinusInputDb = [Math]::Round($nr5OutputDbfs - $nr5InputDbfs, 1)
    }
    $runtimeLevelerInputRmsDbfs = Get-NumericValue (Get-JsonValue $runtime "rxAudioLevelerInputRmsDbfs")
    $runtimeLevelerOutputRmsDbfs = Get-NumericValue (Get-JsonValue $runtime "rxAudioLevelerOutputRmsDbfs")
    $runtimeLevelerAppliedGainDb = Get-NumericValue (Get-JsonValue $runtime "rxAudioLevelerAppliedGainDb")
    $rxAudioLevelerBoostSlewLimited = Test-Truthy (Get-JsonValue $runtime "rxAudioLevelerBoostSlewLimited")
    $rxAudioLevelerPeakLimited = Test-Truthy (Get-JsonValue $runtime "rxAudioLevelerPeakLimited")
    $rxAudioLevelerOutputLimited = Test-Truthy (Get-JsonValue $runtime "rxAudioLevelerOutputLimited")
    $nr5AudioInputDeltaDb = $null
    if ($null -ne $nr5OutputDbfs -and $null -ne $runtimeLevelerInputRmsDbfs) {
        $nr5AudioInputDeltaDb = [Math]::Round($runtimeLevelerInputRmsDbfs - $nr5OutputDbfs, 1)
    }
    $nr5AudioOutputDeltaDb = $null
    if ($null -ne $nr5OutputDbfs -and $null -ne $runtimeLevelerOutputRmsDbfs) {
        $nr5AudioOutputDeltaDb = [Math]::Round($runtimeLevelerOutputRmsDbfs - $nr5OutputDbfs, 1)
    }
    $nr5AudioAlignedAfterLeveler = ($null -ne $nr5AudioOutputDeltaDb -and [Math]::Abs($nr5AudioOutputDeltaDb) -le 12.0)
    $nr5AudioLevelerNormalized = Test-Nr5LevelerNormalizedSpeechAlignment `
        -Nr5OutputDbfs $nr5OutputDbfs `
        -LevelerInputRmsDbfs $runtimeLevelerInputRmsDbfs `
        -LevelerOutputRmsDbfs $runtimeLevelerOutputRmsDbfs `
        -LevelerAppliedGainDb $runtimeLevelerAppliedGainDb `
        -LevelerBoostSlewLimited $rxAudioLevelerBoostSlewLimited `
        -LevelerPeakLimited $rxAudioLevelerPeakLimited `
        -LevelerOutputLimited $rxAudioLevelerOutputLimited
    $nr5AudioAlignmentMismatch = $false
    if ($null -ne $nr5AudioInputDeltaDb -and [Math]::Abs($nr5AudioInputDeltaDb) -gt 12.0 -and
        -not $nr5AudioAlignedAfterLeveler -and
        -not $nr5AudioLevelerNormalized) {
        $nr5AudioAlignmentMismatch = $true
    }
    $nr5Tuning = Get-Nr5TuningReadiness $diagnostics
    $frontendTopPeaks = @(Get-JsonArray $diagnostics "frontendTopPeaks")
    $frontendNearestTopPeak = Get-NearestFrontendTopPeak $frontendTopPeaks
    $frontendStrongestTopPeak = Get-StrongestFrontendTopPeak $frontendTopPeaks

    return [ordered]@{
        sampleIndex = Get-JsonValue $Sample "sampleIndex"
        sampledUtc = Get-JsonValue $Sample "sampledUtc"
        ok = Test-Truthy (Get-JsonValue $Sample "ok")
        latencyMs = Get-JsonValue $Sample "latencyMs"
        status = [string](Get-JsonValue $diagnostics "status")
        qualityTone = [string](Get-JsonValue $diagnostics "qualityTone")
        readinessScore = Get-JsonValue $diagnostics "readinessScore"
        readyForLiveBenchmark = Test-Truthy (Get-JsonValue $diagnostics "readyForLiveBenchmark")
        readyForNr5Tuning = Test-Truthy $nr5Tuning["ready"]
        nr5TuningStatus = [string]$nr5Tuning["status"]
        nr5TuningReadinessSource = [string]$nr5Tuning["source"]
        nr5TuningConstraints = @($nr5Tuning["constraints"])
        frontendSceneStatus = [string](Get-JsonValue $diagnostics "frontendSceneStatus")
        frontendSceneFresh = Test-Truthy (Get-JsonValue $diagnostics "frontendSceneFresh")
        frontendSceneAgeMs = Get-JsonValue $diagnostics "frontendSceneAgeMs"
        frontendTopPeakCount = $frontendTopPeaks.Count
        frontendTopPeaks = @($frontendTopPeaks | ForEach-Object { Convert-FrontendTopPeak $_ })
        frontendNearestTopPeak = Convert-FrontendTopPeak $frontendNearestTopPeak
        frontendStrongestTopPeak = Convert-FrontendTopPeak $frontendStrongestTopPeak
        frontendAdjacentNoiseUsable = Get-JsonValue $diagnostics "frontendAdjacentNoiseUsable"
        frontendAdjacentNoiseBins = Get-JsonValue $diagnostics "frontendAdjacentNoiseBins"
        frontendAdjacentNoiseFloorDb = Get-JsonValue $diagnostics "frontendAdjacentNoiseFloorDb"
        frontendAdjacentNoiseP50Db = Get-JsonValue $diagnostics "frontendAdjacentNoiseP50Db"
        frontendAdjacentNoiseSlopeDbPerKhz = Get-JsonValue $diagnostics "frontendAdjacentNoiseSlopeDbPerKhz"
        frontendAdjacentNoiseRejectedPct = Get-JsonValue $diagnostics "frontendAdjacentNoiseRejectedPct"
        runtimeStatus = [string](Get-JsonValue $runtime "status")
        audioStatus = [string](Get-JsonValue $runtime "audioStatus")
        rxMetersFresh = Test-Truthy (Get-JsonValue $runtime "rxMetersFresh")
        audioFresh = Test-Truthy (Get-JsonValue $runtime "audioFresh")
        rxMetersAgeMs = Get-JsonValue $runtime "rxMetersAgeMs"
        audioAgeMs = Get-JsonValue $runtime "audioAgeMs"
        audioFramesBroadcast = Get-JsonValue $runtime "audioFramesBroadcast"
        audioLastSeq = Get-JsonValue $runtime "audioLastSeq"
        audioSampleRateHz = Get-JsonValue $runtime "audioSampleRateHz"
        audioSampleCount = Get-JsonValue $runtime "audioSampleCount"
        agcGainDb = Get-JsonValue $runtime "agcGainDb"
        adcHeadroomDb = Get-JsonValue $runtime "adcHeadroomDb"
        audioRmsDbfs = Get-JsonValue $runtime "audioRmsDbfs"
        audioPeakDbfs = Get-JsonValue $runtime "audioPeakDbfs"
        rxAudioLevelerInputRmsDbfs = Get-JsonValue $runtime "rxAudioLevelerInputRmsDbfs"
        rxAudioLevelerOutputRmsDbfs = Get-JsonValue $runtime "rxAudioLevelerOutputRmsDbfs"
        rxAudioLevelerInputPeakDbfs = Get-JsonValue $runtime "rxAudioLevelerInputPeakDbfs"
        rxAudioLevelerOutputPeakDbfs = Get-JsonValue $runtime "rxAudioLevelerOutputPeakDbfs"
        rxAudioLevelerDesiredGainDb = Get-JsonValue $runtime "rxAudioLevelerDesiredGainDb"
        rxAudioLevelerAppliedGainDb = Get-JsonValue $runtime "rxAudioLevelerAppliedGainDb"
        rxAudioLevelerGainDeltaDb = Get-JsonValue $runtime "rxAudioLevelerGainDeltaDb"
        rxAudioLevelerPeakHeadroomDb = Get-JsonValue $runtime "rxAudioLevelerPeakHeadroomDb"
        rxAudioLevelerPreLimitPeakDbfs = Get-JsonValue $runtime "rxAudioLevelerPreLimitPeakDbfs"
        rxAudioLevelerOutputLimitReductionDb = Get-JsonValue $runtime "rxAudioLevelerOutputLimitReductionDb"
        rxAudioLevelerOutputLimitSampleCount = Get-JsonValue $runtime "rxAudioLevelerOutputLimitSampleCount"
        rxAudioLevelerPauseHoldBlocks = Get-JsonValue $runtime "rxAudioLevelerPauseHoldBlocks"
        rxAudioLevelerNr5SpeechHoldBlocks = Get-JsonValue $runtime "rxAudioLevelerNr5SpeechHoldBlocks"
        rxAudioLevelerBoostSlewLimited = Get-JsonValue $runtime "rxAudioLevelerBoostSlewLimited"
        rxAudioLevelerPeakLimited = Get-JsonValue $runtime "rxAudioLevelerPeakLimited"
        rxAudioLevelerOutputLimited = Get-JsonValue $runtime "rxAudioLevelerOutputLimited"
        squelchOpen = Test-Truthy (Get-JsonValue $runtime "squelchOpen")
        monitorBacklogSamples = Get-JsonValue $runtime "monitorBacklogSamples"
        requestedNrMode = [string](Get-JsonValue $diagnostics "requestedNrMode")
        effectiveNrMode = [string](Get-JsonValue $diagnostics "effectiveNrMode")
        radioVfoHz = Get-JsonValue $diagnostics "radioVfoHz"
        radioLoHz = Get-JsonValue $diagnostics "radioLoHz"
        radioMode = Get-JsonValue $diagnostics "radioMode"
        radioCtunEnabled = Get-JsonValue $diagnostics "radioCtunEnabled"
        radioSampleRate = Get-JsonValue $diagnostics "radioSampleRate"
        rxChainFilterLowHz = Get-JsonValue $diagnostics "rxChainFilterLowHz"
        rxChainFilterHighHz = Get-JsonValue $diagnostics "rxChainFilterHighHz"
        rxChainFilterWidthHz = Get-JsonValue $diagnostics "rxChainFilterWidthHz"
        rxChainFilterPresetName = Get-JsonValue $diagnostics "rxChainFilterPresetName"
        nr5LearnedFrames = Get-JsonValue $nr5 "learnedFrames"
        nr5InputDbfs = $nr5InputDbfs
        nr5OutputDbfs = $nr5OutputDbfs
        nr5OutputMinusInputDb = $nr5OutputMinusInputDb
        nr5AudioInputDeltaDb = $nr5AudioInputDeltaDb
        nr5AudioOutputDeltaDb = $nr5AudioOutputDeltaDb
        nr5AudioAlignedAfterLeveler = $nr5AudioAlignedAfterLeveler
        nr5AudioLevelerNormalized = $nr5AudioLevelerNormalized
        nr5AudioAlignmentMismatch = $nr5AudioAlignmentMismatch
        nr5MeanGain = Get-JsonValue $nr5 "meanGain"
        nr5SignalConfidence = Get-JsonValue $nr5 "signalConfidence"
        nr5AgcGate = Get-JsonValue $nr5 "agcGate"
        nr5SignalProbability = Get-JsonValue $nr5 "signalProbability"
        nr5TextureFill = Get-JsonValue $nr5 "textureFill"
        nr5MaskSmoothing = Get-JsonValue $nr5 "maskSmoothing"
        nr5LevelDrive = Get-JsonValue $nr5 "levelDrive"
        nr5RecoveryDrive = Get-JsonValue $nr5 "recoveryDrive"
        nr5WeakSignalMemory = Get-JsonValue $nr5 "weakSignalMemory"
        nr5MakeupGainDb = Get-JsonValue $nr5 "makeupGainDb"
        nr5OutputPeakDbfs = Get-JsonValue $nr5 "outputPeakDbfs"
        nr5PeakEvidence = Get-JsonValue $nr5 "peakEvidence"
        nr5PeakLimitDbfs = Get-JsonValue $nr5 "peakLimitDbfs"
        nr5PeakReductionDb = Get-JsonValue $nr5 "peakReductionDb"
        nr5ManagedChannelGeneration = Get-JsonValue $nr5 "managedChannelGeneration"
        nr5ManagedNr5ApplyCount = Get-JsonValue $nr5 "managedNr5ApplyCount"
        nr5ManagedNr5PositionApplyCount = Get-JsonValue $nr5 "managedNr5PositionApplyCount"
        nr5ManagedNr5PolicyApplyCount = Get-JsonValue $nr5 "managedNr5PolicyApplyCount"
        nr5ManagedNr5NoopApplyCount = Get-JsonValue $nr5 "managedNr5NoopApplyCount"
        nr5ManagedNr5RunApplyCount = Get-JsonValue $nr5 "managedNr5RunApplyCount"
        nr5ManagedNr5LastApplyReason = [string](Get-JsonValue $nr5 "managedNr5LastApplyReason")
        nr5AdjacentNoiseUsable = Get-JsonValue $nr5 "adjacentNoiseUsable"
        nr5AdjacentNoiseBins = Get-JsonValue $nr5 "adjacentNoiseBins"
        nr5AdjacentNoiseFloorDb = Get-JsonValue $nr5 "adjacentNoiseFloorDb"
        nr5AdjacentNoiseTrust = Get-JsonValue $nr5 "adjacentNoiseTrust"
        nr5AdjacentNoiseDrive = Get-JsonValue $nr5 "adjacentNoiseDrive"
        nr5AdjacentNoiseRejectedPct = Get-JsonValue $nr5 "adjacentNoiseRejectedPct"
        nr5AdjacentNoiseLeftBins = Get-JsonValue $nr5 "adjacentNoiseLeftBins"
        nr5AdjacentNoiseRightBins = Get-JsonValue $nr5 "adjacentNoiseRightBins"
        nr5AdjacentNoiseLeftFloorDb = Get-JsonValue $nr5 "adjacentNoiseLeftFloorDb"
        nr5AdjacentNoiseRightFloorDb = Get-JsonValue $nr5 "adjacentNoiseRightFloorDb"
        nr5AdjacentNoiseSideBalance = Get-JsonValue $nr5 "adjacentNoiseSideBalance"
        nr5AdjacentNoiseAsymmetryDb = Get-JsonValue $nr5 "adjacentNoiseAsymmetryDb"
        constraints = @(Get-JsonArray $diagnostics "constraints")
    }
}

function Write-RealtimeSample {
    param($Sample)

    if ($JsonOnly) {
        return
    }

    $summary = New-SampleSummary $Sample
    $okText = if (Test-Truthy (Get-JsonValue $Sample "ok")) { "ok" } else { "fail" }
    $modeText = "$($summary["requestedNrMode"])->$($summary["effectiveNrMode"])"
    $nr5ReadyText = if (Test-Truthy $summary["readyForNr5Tuning"]) { "nr5-tune=yes" } else { "nr5-tune=no" }
    $benchmarkText = if (Test-Truthy $summary["readyForLiveBenchmark"]) { "bench=yes" } else { "bench=no" }
    $weakText = ""
    $inputDb = Get-NumericValue $summary["nr5InputDbfs"]
    $outputDb = Get-NumericValue $summary["nr5OutputDbfs"]
    if ($null -ne $inputDb -and $null -ne $outputDb) {
        $delta = Get-NumericValue $summary["nr5OutputMinusInputDb"]
        $weakText = " in=$([Math]::Round($inputDb, 1)) out=$([Math]::Round($outputDb, 1)) delta=$delta"
    }
    $makeup = Get-NumericValue $summary["nr5MakeupGainDb"]
    $makeupText = if ($null -eq $makeup) { "" } else { " makeup=$([Math]::Round($makeup, 1))dB" }
    $probability = Get-NumericValue $summary["nr5SignalProbability"]
    $probabilityText = if ($null -eq $probability) { "" } else { " prob=$([Math]::Round($probability, 2))" }
    $memory = Get-NumericValue $summary["nr5WeakSignalMemory"]
    $memoryText = if ($null -eq $memory) { "" } else { " mem=$([Math]::Round($memory, 2))" }
    $peakReduction = Get-NumericValue $summary["nr5PeakReductionDb"]
    $peakText = if ($null -eq $peakReduction) { "" } else { " peakRed=$([Math]::Round($peakReduction, 1))dB" }
    $adjacentTrust = Get-NumericValue $summary["nr5AdjacentNoiseTrust"]
    $adjacentDrive = Get-NumericValue $summary["nr5AdjacentNoiseDrive"]
    $adjacentText = ""
    if ($null -ne $adjacentTrust) {
        if ($null -eq $adjacentDrive) { $adjacentDrive = 0.0 }
        $adjacentText = " adj=$([Math]::Round($adjacentTrust, 2))/$([Math]::Round($adjacentDrive, 2))"
    }
    $levelerGain = Get-NumericValue $summary["rxAudioLevelerAppliedGainDb"]
    $levelerText = if ($null -eq $levelerGain) { "" } else { " lvl=$([Math]::Round($levelerGain, 1))dB" }
    if (Test-Truthy $summary["rxAudioLevelerBoostSlewLimited"]) { $levelerText += "/slew" }
    if (Test-Truthy $summary["rxAudioLevelerPeakLimited"]) { $levelerText += "/peak" }
    if (Test-Truthy $summary["rxAudioLevelerOutputLimited"]) { $levelerText += "/cap" }
    Write-Host ("[{0}] {1} {2} {3} {4}{5}{6}{7}{8}{9}{10}{11}" -f
        $summary["sampleIndex"],
        $okText,
        $modeText,
        $nr5ReadyText,
        $benchmarkText,
        $weakText,
        $makeupText,
        $probabilityText,
        $memoryText,
        $peakText,
        $adjacentText,
        $levelerText)
}

function Get-Nr5TuningReadiness {
    param($Diagnostics)

    $endpointReady = Get-JsonValue $Diagnostics "readyForNr5Tuning"
    $endpointStatus = [string](Get-JsonValue $Diagnostics "nr5TuningStatus")
    $endpointConstraints = @(Get-JsonArray $Diagnostics "nr5TuningConstraints")
    if ($null -ne $endpointReady -or -not [string]::IsNullOrWhiteSpace($endpointStatus) -or $endpointConstraints.Count -gt 0) {
        return [ordered]@{
            ready = Test-Truthy $endpointReady
            status = if ([string]::IsNullOrWhiteSpace($endpointStatus)) { "nr5-tuning-watch" } else { $endpointStatus }
            constraints = $endpointConstraints
            source = "endpoint"
        }
    }

    $constraints = New-Object System.Collections.Generic.List[string]
    $requested = [string](Get-JsonValue $Diagnostics "requestedNrMode")
    $effective = [string](Get-JsonValue $Diagnostics "effectiveNrMode")
    $nr5 = Get-JsonValue $Diagnostics "nr5SpnrDiagnostics"
    $runtime = Get-JsonValue $Diagnostics "runtimeEvidence"

    if ($requested -ne "Nr5") {
        $constraints.Add("nr5-not-requested") | Out-Null
    }
    if ($effective -ne "Nr5") {
        $constraints.Add("nr5-not-effective") | Out-Null
    }
    if ($null -eq $nr5) {
        $constraints.Add("nr5-diagnostics-missing") | Out-Null
    }
    else {
        if (-not (Test-Truthy (Get-JsonValue $nr5 "run"))) {
            $constraints.Add("nr5-not-running") | Out-Null
        }
        $learned = Get-NumericValue (Get-JsonValue $nr5 "learnedFrames")
        if ($null -eq $learned -or $learned -lt 20) {
            $constraints.Add("nr5-learning") | Out-Null
        }
        $agcRunValue = Get-JsonValue $nr5 "agcRun"
        if ($null -ne $agcRunValue -and -not (Test-Truthy $agcRunValue)) {
            $constraints.Add("nr5-agc-disabled") | Out-Null
        }
    }

    if ($null -eq $runtime) {
        $constraints.Add("runtime-evidence-missing") | Out-Null
    }
    else {
        if (-not (Test-Truthy (Get-JsonValue $runtime "rxMetersFresh"))) {
            $constraints.Add("rx-meters-not-fresh") | Out-Null
        }
        if (-not (Test-Truthy (Get-JsonValue $runtime "audioFresh"))) {
            $constraints.Add("final-audio-not-fresh") | Out-Null
        }
        switch ([string](Get-JsonValue $runtime "status")) {
            "audio-clipping-risk" { $constraints.Add("final-audio-clipping-risk") | Out-Null }
            "audio-muted-by-squelch" { $constraints.Add("final-audio-muted-by-squelch") | Out-Null }
            "audio-monitor-backlog" { $constraints.Add("monitor-audio-backlog") | Out-Null }
            "audio-tx-monitor" { $constraints.Add("tx-monitor-audio-active") | Out-Null }
            "adc-headroom-low" { $constraints.Add("adc-headroom-low") | Out-Null }
        }
    }

    $uniqueConstraints = @($constraints.ToArray() | Select-Object -Unique)
    return [ordered]@{
        ready = ($uniqueConstraints.Count -eq 0)
        status = if ($uniqueConstraints.Count -eq 0) { "ready-for-nr5-live-tuning" } else { "nr5-tuning-preflight-required" }
        constraints = $uniqueConstraints
        source = "watcher-fallback"
    }
}

function Test-RuntimeRxAudio {
    param($Runtime)

    if ($null -eq $Runtime) {
        return $false
    }

    $runtimeStatus = [string](Get-JsonValue $Runtime "status")
    $audioStatus = [string](Get-JsonValue $Runtime "audioStatus")
    $audioSource = ([string](Get-JsonValue $Runtime "audioSource")).Trim()

    if (Test-Truthy (Get-JsonValue $Runtime "txMonitorRequested")) {
        return $false
    }
    if ([string]::Equals($runtimeStatus, "audio-tx-monitor", [StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($audioStatus, "tx-monitor", [StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }
    if ([string]::IsNullOrWhiteSpace($audioSource)) {
        return $true
    }

    return [string]::Equals($audioSource, "rx", [StringComparison]::OrdinalIgnoreCase)
}

function Build-Report {
    param(
        [object[]]$SampleRecords,
        [string]$SourceMode,
        [string]$Endpoint,
        [string]$SourcePath,
        [string]$LinePath,
        [string]$Label = "",
        [string]$ScenarioId = "",
        [string]$ComparisonId = "",
        [DateTimeOffset]$StartedUtc,
        [DateTimeOffset]$CompletedUtc,
        [int]$TuneStepHz = 1000
    )

    $statusCounts = @{}
    $toneCounts = @{}
    $runtimeStatusCounts = @{}
    $audioStatusCounts = @{}
    $requestedNrModeCounts = @{}
    $effectiveNrModeCounts = @{}
    $nr5TuningStatusCounts = @{}
    $nr5TuningConstraintCounts = @{}
    $constraintCounts = @{}
    $hardConstraintCounts = @{}
    $latencies = New-Object System.Collections.Generic.List[double]
    $readinessScores = New-Object System.Collections.Generic.List[double]
    $agcValues = New-Object System.Collections.Generic.List[double]
    $activeAgcValues = New-Object System.Collections.Generic.List[double]
    $voiceLikeAgcValues = New-Object System.Collections.Generic.List[double]
    $quietNoEvidenceAgcValues = New-Object System.Collections.Generic.List[double]
    $levelerConstrainedAgcValues = New-Object System.Collections.Generic.List[double]
    $headroomValues = New-Object System.Collections.Generic.List[double]
    $rmsValues = New-Object System.Collections.Generic.List[double]
    $peakValues = New-Object System.Collections.Generic.List[double]
    $floorAudioRmsValues = New-Object System.Collections.Generic.List[double]
    $activeAudioRmsValues = New-Object System.Collections.Generic.List[double]
    $voiceLikeAudioRmsValues = New-Object System.Collections.Generic.List[double]
    $quietNoEvidenceAudioRmsValues = New-Object System.Collections.Generic.List[double]
    $passbandAudioRmsValues = New-Object System.Collections.Generic.List[double]
    $passbandActiveAudioRmsValues = New-Object System.Collections.Generic.List[double]
    $passbandFloorAudioRmsValues = New-Object System.Collections.Generic.List[double]
    $offPassbandAudioRmsValues = New-Object System.Collections.Generic.List[double]
    $rxAudioLevelerInputRmsValues = New-Object System.Collections.Generic.List[double]
    $rxAudioLevelerOutputRmsValues = New-Object System.Collections.Generic.List[double]
    $rxAudioLevelerInputPeakValues = New-Object System.Collections.Generic.List[double]
    $rxAudioLevelerOutputPeakValues = New-Object System.Collections.Generic.List[double]
    $rxAudioLevelerDesiredGainValues = New-Object System.Collections.Generic.List[double]
    $rxAudioLevelerAppliedGainValues = New-Object System.Collections.Generic.List[double]
    $rxAudioLevelerGainDeltaValues = New-Object System.Collections.Generic.List[double]
    $rxAudioLevelerPeakHeadroomValues = New-Object System.Collections.Generic.List[double]
    $rxAudioLevelerPreLimitPeakValues = New-Object System.Collections.Generic.List[double]
    $rxAudioLevelerOutputLimitReductionValues = New-Object System.Collections.Generic.List[double]
    $rxAudioLevelerOutputLimitSampleCountValues = New-Object System.Collections.Generic.List[double]
    $rxAudioLevelerPauseHoldBlockValues = New-Object System.Collections.Generic.List[double]
    $rxAudioLevelerNr5SpeechHoldBlockValues = New-Object System.Collections.Generic.List[double]
    $backlogValues = New-Object System.Collections.Generic.List[double]
    $frontendSceneAgeValues = New-Object System.Collections.Generic.List[double]
    $frontendTopPeakCountValues = New-Object System.Collections.Generic.List[double]
    $frontendNearestTopPeakOffsetValues = New-Object System.Collections.Generic.List[double]
    $frontendNearestTopPeakAbsOffsetValues = New-Object System.Collections.Generic.List[double]
    $frontendStrongestTopPeakSnrValues = New-Object System.Collections.Generic.List[double]
    $frontendNearestFilterPassbandDistanceValues = New-Object System.Collections.Generic.List[double]
    $frontendAdjacentNoiseBinValues = New-Object System.Collections.Generic.List[double]
    $frontendAdjacentNoiseFloorValues = New-Object System.Collections.Generic.List[double]
    $frontendAdjacentNoiseP50Values = New-Object System.Collections.Generic.List[double]
    $frontendAdjacentNoiseSlopeValues = New-Object System.Collections.Generic.List[double]
    $frontendAdjacentNoiseRejectedValues = New-Object System.Collections.Generic.List[double]
    $rxMetersAgeValues = New-Object System.Collections.Generic.List[double]
    $audioAgeValues = New-Object System.Collections.Generic.List[double]
    $nr5InputValues = New-Object System.Collections.Generic.List[double]
    $nr5OutputValues = New-Object System.Collections.Generic.List[double]
    $nr5InputOutputXValues = New-Object System.Collections.Generic.List[double]
    $nr5InputOutputYValues = New-Object System.Collections.Generic.List[double]
    $nr5OutputMinusInputValues = New-Object System.Collections.Generic.List[double]
    $nr5WeakOutputValues = New-Object System.Collections.Generic.List[double]
    $nr5StrongOutputValues = New-Object System.Collections.Generic.List[double]
    $nr5WeakFinalAudioValues = New-Object System.Collections.Generic.List[double]
    $nr5StrongFinalAudioValues = New-Object System.Collections.Generic.List[double]
    $nr5NearStrongOutputValues = New-Object System.Collections.Generic.List[double]
    $nr5NearStrongFinalAudioValues = New-Object System.Collections.Generic.List[double]
    $nr5SpeechQualifiedWeakOutputValues = New-Object System.Collections.Generic.List[double]
    $nr5SpeechQualifiedStrongOutputValues = New-Object System.Collections.Generic.List[double]
    $nr5SpeechQualifiedNearStrongOutputValues = New-Object System.Collections.Generic.List[double]
    $nr5SpeechQualifiedNearStrongFinalAudioValues = New-Object System.Collections.Generic.List[double]
    $nr5SpeechQualifiedWeakFinalAudioValues = New-Object System.Collections.Generic.List[double]
    $nr5SpeechQualifiedStrongFinalAudioValues = New-Object System.Collections.Generic.List[double]
    $nr5PassbandQualifiedWeakOutputValues = New-Object System.Collections.Generic.List[double]
    $nr5PassbandQualifiedStrongOutputValues = New-Object System.Collections.Generic.List[double]
    $nr5PassbandQualifiedNearStrongOutputValues = New-Object System.Collections.Generic.List[double]
    $nr5PassbandQualifiedNearStrongFinalAudioValues = New-Object System.Collections.Generic.List[double]
    $nr5PassbandQualifiedWeakFinalAudioValues = New-Object System.Collections.Generic.List[double]
    $nr5PassbandQualifiedStrongFinalAudioValues = New-Object System.Collections.Generic.List[double]
    $nr5MeanGainValues = New-Object System.Collections.Generic.List[double]
    $nr5FloorReductionValues = New-Object System.Collections.Generic.List[double]
    $nr5DynamicRangeValues = New-Object System.Collections.Generic.List[double]
    $nr5SignalConfidenceValues = New-Object System.Collections.Generic.List[double]
    $nr5AgcGateValues = New-Object System.Collections.Generic.List[double]
    $nr5SignalProbabilityValues = New-Object System.Collections.Generic.List[double]
    $nr5TextureFillValues = New-Object System.Collections.Generic.List[double]
    $nr5MaskSmoothingValues = New-Object System.Collections.Generic.List[double]
    $nr5LevelDriveValues = New-Object System.Collections.Generic.List[double]
    $nr5RecoveryDriveValues = New-Object System.Collections.Generic.List[double]
    $nr5WeakSignalMemoryValues = New-Object System.Collections.Generic.List[double]
    $nr5MakeupGainDbValues = New-Object System.Collections.Generic.List[double]
    $nr5OutputPeakDbfsValues = New-Object System.Collections.Generic.List[double]
    $nr5PeakEvidenceValues = New-Object System.Collections.Generic.List[double]
    $nr5PeakLimitDbfsValues = New-Object System.Collections.Generic.List[double]
    $nr5PeakReductionDbValues = New-Object System.Collections.Generic.List[double]
    $nr5AdjacentNoiseTrustValues = New-Object System.Collections.Generic.List[double]
    $nr5AdjacentNoiseDriveValues = New-Object System.Collections.Generic.List[double]
    $nr5AdjacentNoiseFloorValues = New-Object System.Collections.Generic.List[double]
    $nr5AdjacentNoiseRejectedValues = New-Object System.Collections.Generic.List[double]
    $nr5AdjacentNoiseSideBalanceValues = New-Object System.Collections.Generic.List[double]
    $nr5AdjacentNoiseAsymmetryValues = New-Object System.Collections.Generic.List[double]
    $nr5AudioInputDeltaValues = New-Object System.Collections.Generic.List[double]
    $nr5AudioOutputDeltaValues = New-Object System.Collections.Generic.List[double]
    $nr5LearnedFrameValues = New-Object System.Collections.Generic.List[double]
    $nr5ManagedChannelGenerationValues = New-Object System.Collections.Generic.List[double]
    $nr5ManagedApplyCountValues = New-Object System.Collections.Generic.List[double]
    $nr5ManagedPositionApplyCountValues = New-Object System.Collections.Generic.List[double]
    $nr5ManagedPolicyApplyCountValues = New-Object System.Collections.Generic.List[double]
    $nr5ManagedNoopApplyCountValues = New-Object System.Collections.Generic.List[double]
    $nr5ManagedRunApplyCountValues = New-Object System.Collections.Generic.List[double]
    $sampleSummaries = New-Object System.Collections.Generic.List[object]
    $recommendations = New-Object System.Collections.Generic.List[string]
    $nr5WeakInputSamples = New-Object System.Collections.Generic.List[object]
    $nr5StrongInputSamples = New-Object System.Collections.Generic.List[object]
    $nr5SpeechQualifiedWeakInputSamples = New-Object System.Collections.Generic.List[object]
    $nr5SpeechQualifiedStrongInputSamples = New-Object System.Collections.Generic.List[object]
    $nr5PassbandQualifiedWeakInputSamples = New-Object System.Collections.Generic.List[object]
    $nr5PassbandQualifiedStrongInputSamples = New-Object System.Collections.Generic.List[object]
    $nr5WeakDropoutSamples = New-Object System.Collections.Generic.List[object]
    $nr5WeakDropoutFinalAudibleSamples = New-Object System.Collections.Generic.List[object]
    $nr5WeakDropoutCandidateLossSamples = New-Object System.Collections.Generic.List[object]
    $nr5HotMakeupSamples = New-Object System.Collections.Generic.List[object]
    $nr5NearStrongInputSamples = New-Object System.Collections.Generic.List[object]
    $nr5LowEvidenceLiftSamples = New-Object System.Collections.Generic.List[object]
    $nr5LowEvidenceSuppressedSamples = New-Object System.Collections.Generic.List[object]
    $nr5AudioAlignmentMismatchSamples = New-Object System.Collections.Generic.List[object]
    $nr5AudioLevelerNormalizedSamples = New-Object System.Collections.Generic.List[object]
    $nr5LearnerResetSamples = New-Object System.Collections.Generic.List[object]
    $nr5ManagedReapplySamples = New-Object System.Collections.Generic.List[object]
    $nr5ManagedGenerationChangeSamples = New-Object System.Collections.Generic.List[object]
    $rxAudioLevelerBoostSlewLimitedSamples = New-Object System.Collections.Generic.List[object]
    $rxAudioLevelerPeakLimitedSamples = New-Object System.Collections.Generic.List[object]
    $rxAudioLevelerOutputLimitedSamples = New-Object System.Collections.Generic.List[object]
    $rxAudioLevelerConstrainedSamples = New-Object System.Collections.Generic.List[object]
    $frontendTopPeakSamples = New-Object System.Collections.Generic.List[object]
    $frontendNearPassbandPeakSamples = New-Object System.Collections.Generic.List[object]
    $frontendFilterPassbandPeakSamples = New-Object System.Collections.Generic.List[object]
    $passbandAudioSamples = New-Object System.Collections.Generic.List[object]
    $offPassbandAudioSamples = New-Object System.Collections.Generic.List[object]
    $frontendTopPeakSampleCount = 0
    $frontendNearPassbandPeakSampleCount = 0
    $frontendFilterPassbandPeakSampleCount = 0
    $frontendAdjacentNoiseUsableCount = 0
    $nr5AdjacentNoiseUsableCount = 0
    $nr5AdjacentNoiseDriveCount = 0

    $nr5LowEvidenceInputThresholdDbfs = -30.0
    $nr5LowEvidenceConfidenceThreshold = 0.32
    $nr5LowEvidenceProbabilityThreshold = 0.18
    $nr5LowEvidenceAgcGateThreshold = 0.50
    $nr5LowEvidenceOutputThresholdDbfs = -28.0
    $nr5LowEvidenceAudioThresholdDbfs = -20.0
    $signalFloorAudioThresholdDbfs = -70.0
    $signalActiveAudioThresholdDbfs = -45.0
    $signalEvidenceConfidenceThreshold = 0.30
    $signalEvidenceProbabilityThreshold = 0.16
    $signalEvidenceAgcGateThreshold = 0.30
    $frontendNearPassbandThresholdHz = 3000.0
    $frontendFilterPassbandEdgeToleranceHz = 0.0
    $nr5StrongInputThresholdDbfs = -22.0
    $nr5NearStrongInputThresholdDbfs = -26.0
    $nr5WeakDropoutFinalAudibleThresholdDbfs = $signalActiveAudioThresholdDbfs
    $nr5WeakDropoutNativeLiftThresholdDb = 6.0
    $nr5WeakDropoutBelowInputThresholdDb = 1.0

    $okCount = 0
    $failedCount = 0
    $readyCount = 0
    $runtimeCount = 0
    $rxMetersFreshCount = 0
    $audioFreshCount = 0
    $txMonitorCount = 0
    $squelchEnabledCount = 0
    $squelchClosedCount = 0
    $squelchTailCount = 0
    $rxAudioLevelerDiagnosticCount = 0
    $rxAudioLevelerBoostSlewLimitedCount = 0
    $rxAudioLevelerPeakLimitedCount = 0
    $rxAudioLevelerOutputLimitedCount = 0
    $hardBlockerSampleCount = 0
    $nr5SampleCount = 0
    $nr5AlignedCount = 0
    $nr5TuningReadyCount = 0
    $nr5AgcDiagnosticCount = 0
    $nr5ProbabilityDiagnosticCount = 0
    $nr5PeakDiagnosticCount = 0
    $nr5LearnedFrameSampleCount = 0
    $nr5ManagedCounterSampleCount = 0
    $nr5ManagedPositionReapplyCount = 0
    $nr5ManagedPolicyReapplyCount = 0
    $nr5ManagedGenerationChangeCount = 0
    $nr5AudioAlignmentSampleCount = 0
    $nr5AudioAlignedAfterLevelerCount = 0
    $nr5AudioLevelerNormalizedCount = 0
    $nr5AudioAlignmentMismatchCount = 0
    $nr5WeakInputCount = 0
    $nr5WeakRecoveredCount = 0
    $nr5WeakDropoutCount = 0
    $nr5WeakDropoutFinalAudibleCount = 0
    $nr5WeakDropoutNativeLiftedCount = 0
    $nr5WeakDropoutCandidateLossCount = 0
    $nr5WeakBelowInputCount = 0
    $nr5WeakNearTargetCount = 0
    $nr5StrongInputCount = 0
    $nr5NearStrongInputCount = 0
    $nr5HotMakeupCount = 0
    $nr5RequestedSampleCount = 0
    $nr5EffectiveSampleCount = 0
    $nrOffRequestedSampleCount = 0
    $nrOffEffectiveSampleCount = 0
    $nrModeMismatchSampleCount = 0
    $nr5LowEvidenceSampleCount = 0
    $nr5LowEvidenceLiftCount = 0
    $nr5LowEvidenceAlignmentMismatchCount = 0
    $nr5LowEvidenceSuppressedCount = 0
    $signalFloorAudioCount = 0
    $signalActiveAudioCount = 0
    $signalVoiceLikeEvidenceCount = 0
    $signalQuietNoEvidenceCount = 0
    $signalIntermittentBurstCount = 0
    $previousAudioWasFloor = $false
    $rxChainFilterLowHz = $null
    $rxChainFilterHighHz = $null
    $rxChainFilterWidthHz = $null
    $rxChainFilterPresetName = $null
    $radioVfoHz = $null
    $radioLoHz = $null
    $radioMode = $null
    $radioCtunEnabled = $null
    $radioSampleRate = $null
    $rxStateVfoValues = New-Object System.Collections.Generic.List[double]
    $rxStateLoValues = New-Object System.Collections.Generic.List[double]
    $rxStateSampleRateValues = New-Object System.Collections.Generic.List[double]
    $rxStateModeCounts = @{}
    $rxStateFilterCounts = @{}
    $rxStateCtunCounts = @{}
    $rxStateEvidenceSampleCount = 0
    $nr5PreviousLearnedFrames = $null
    $nr5PreviousManagedChannelGeneration = $null
    $nr5PreviousManagedApplyCount = $null
    $nr5PreviousManagedPositionApplyCount = $null
    $nr5PreviousManagedPolicyApplyCount = $null

    foreach ($sample in @($SampleRecords)) {
        if (-not (Test-Truthy (Get-JsonValue $sample "ok"))) {
            $failedCount++
            $sampleSummaries.Add((New-SampleSummary $sample)) | Out-Null
            continue
        }

        $okCount++
        Add-Number $latencies (Get-JsonValue $sample "latencyMs")
        $diagnostics = Get-JsonValue $sample "diagnostics"
        $runtime = Get-JsonValue $diagnostics "runtimeEvidence"
        $nr5 = Get-JsonValue $diagnostics "nr5SpnrDiagnostics"
        $status = [string](Get-JsonValue $diagnostics "status")
        $tone = [string](Get-JsonValue $diagnostics "qualityTone")
        $requestedNrMode = ConvertTo-NrModeName ([string](Get-JsonValue $diagnostics "requestedNrMode"))
        $effectiveNrMode = ConvertTo-NrModeName ([string](Get-JsonValue $diagnostics "effectiveNrMode"))
        if ($null -eq $rxChainFilterLowHz) {
            $rxChainFilterLowHz = Get-JsonValue $diagnostics "rxChainFilterLowHz"
            $rxChainFilterHighHz = Get-JsonValue $diagnostics "rxChainFilterHighHz"
            $rxChainFilterWidthHz = Get-JsonValue $diagnostics "rxChainFilterWidthHz"
            $rxChainFilterPresetName = Get-JsonValue $diagnostics "rxChainFilterPresetName"
        }
        Add-Count $statusCounts $status
        Add-Count $toneCounts $tone
        Add-Count $requestedNrModeCounts $requestedNrMode
        Add-Count $effectiveNrModeCounts $effectiveNrMode
        if (Test-NrModeName $requestedNrMode "Nr5") {
            $nr5RequestedSampleCount++
        }
        if (Test-NrModeName $effectiveNrMode "Nr5") {
            $nr5EffectiveSampleCount++
        }
        if (Test-NrModeName $requestedNrMode "Off") {
            $nrOffRequestedSampleCount++
        }
        if (Test-NrModeName $effectiveNrMode "Off") {
            $nrOffEffectiveSampleCount++
        }
        if (-not [string]::Equals($requestedNrMode, "unknown", [StringComparison]::OrdinalIgnoreCase) -and
            -not [string]::Equals($effectiveNrMode, "unknown", [StringComparison]::OrdinalIgnoreCase) -and
            -not [string]::Equals($requestedNrMode, $effectiveNrMode, [StringComparison]::OrdinalIgnoreCase)) {
            $nrModeMismatchSampleCount++
        }
        Add-Number $readinessScores (Get-JsonValue $diagnostics "readinessScore")

        if (Test-Truthy (Get-JsonValue $diagnostics "readyForLiveBenchmark")) {
            $readyCount++
        }
        $nr5Tuning = Get-Nr5TuningReadiness $diagnostics
        if (Test-Truthy $nr5Tuning["ready"]) {
            $nr5TuningReadyCount++
        }
        Add-Count $nr5TuningStatusCounts ([string]$nr5Tuning["status"])
        Add-Number $frontendSceneAgeValues (Get-JsonValue $diagnostics "frontendSceneAgeMs")
        $frontendTopPeaks = @(Get-JsonArray $diagnostics "frontendTopPeaks")
        $nearestOffset = $null
        $nearestFrontendPeak = $null
        $strongestFrontendPeak = $null
        $sampleHasNearPassbandPeak = $false
        $sampleHasFilterPassbandPeak = $false
        $sampleHasPassbandEvidencePeak = $false
        $sampleFilterLowHz = Get-NumericValue (Get-JsonValue $diagnostics "rxChainFilterLowHz")
        $sampleFilterHighHz = Get-NumericValue (Get-JsonValue $diagnostics "rxChainFilterHighHz")
        $sampleFilterPassbandKnown = ($null -ne $sampleFilterLowHz -and $null -ne $sampleFilterHighHz)
        $sampleRadioVfoHz = Get-NumericValue (Get-JsonValue $diagnostics "radioVfoHz")
        $sampleRadioLoHz = Get-NumericValue (Get-JsonValue $diagnostics "radioLoHz")
        $sampleRadioSampleRate = Get-NumericValue (Get-JsonValue $diagnostics "radioSampleRate")
        $sampleRadioMode = ([string](Get-JsonValue $diagnostics "radioMode")).Trim()
        $sampleRadioCtunValue = Get-JsonValue $diagnostics "radioCtunEnabled"
        $sampleRadioCtunKnown = ($null -ne $sampleRadioCtunValue)
        $sampleRadioCtunText = ""
        if ($sampleRadioCtunKnown) {
            $sampleRadioCtunText = if (Test-Truthy $sampleRadioCtunValue) { "true" } else { "false" }
        }
        $sampleHasRxStateEvidence = $false
        if ($null -ne $sampleRadioVfoHz) {
            if ($null -eq $radioVfoHz) { $radioVfoHz = [long][Math]::Round([double]$sampleRadioVfoHz) }
            Add-Number $rxStateVfoValues $sampleRadioVfoHz
            $sampleHasRxStateEvidence = $true
        }
        if ($null -ne $sampleRadioLoHz) {
            if ($null -eq $radioLoHz) { $radioLoHz = [long][Math]::Round([double]$sampleRadioLoHz) }
            Add-Number $rxStateLoValues $sampleRadioLoHz
            $sampleHasRxStateEvidence = $true
        }
        if ($null -ne $sampleRadioSampleRate) {
            if ($null -eq $radioSampleRate) { $radioSampleRate = [int][Math]::Round([double]$sampleRadioSampleRate) }
            Add-Number $rxStateSampleRateValues $sampleRadioSampleRate
            $sampleHasRxStateEvidence = $true
        }
        if (-not [string]::IsNullOrWhiteSpace($sampleRadioMode)) {
            if ($null -eq $radioMode) { $radioMode = $sampleRadioMode }
            Add-Count $rxStateModeCounts $sampleRadioMode
            $sampleHasRxStateEvidence = $true
        }
        if ($sampleRadioCtunKnown) {
            if ($null -eq $radioCtunEnabled) { $radioCtunEnabled = Test-Truthy $sampleRadioCtunValue }
            Add-Count $rxStateCtunCounts $sampleRadioCtunText
            $sampleHasRxStateEvidence = $true
        }
        if ($sampleFilterPassbandKnown) {
            $sampleFilterKey = "{0}..{1}|{2}" -f `
                [int][Math]::Round([double]$sampleFilterLowHz), `
                [int][Math]::Round([double]$sampleFilterHighHz), `
                ([string](Get-JsonValue $diagnostics "rxChainFilterPresetName")).Trim()
            Add-Count $rxStateFilterCounts $sampleFilterKey
            $sampleHasRxStateEvidence = $true
        }
        if ($sampleHasRxStateEvidence) {
            $rxStateEvidenceSampleCount++
        }
        $frontendFilterPassbandTopPeakCount = 0
        $frontendNearestFilterPassbandDistanceHz = $null
        $frontendNearestFilterPassbandPeak = $null
        Add-Number $frontendTopPeakCountValues $frontendTopPeaks.Count
        if ($frontendTopPeaks.Count -gt 0) {
            $frontendTopPeakSampleCount++
            $nearestFrontendPeak = Get-NearestFrontendTopPeak $frontendTopPeaks
            $strongestFrontendPeak = Get-StrongestFrontendTopPeak $frontendTopPeaks
            $nearestOffset = Get-NumericValue (Get-JsonValue $nearestFrontendPeak "offsetHz")
            if ($null -ne $nearestOffset) {
                Add-Number $frontendNearestTopPeakOffsetValues $nearestOffset
                Add-Number $frontendNearestTopPeakAbsOffsetValues ([Math]::Abs([double]$nearestOffset))
            }
            Add-Number $frontendStrongestTopPeakSnrValues (Get-JsonValue $strongestFrontendPeak "snrDb")

            foreach ($peak in $frontendTopPeaks) {
                $offset = Get-NumericValue (Get-JsonValue $peak "offsetHz")
                if ($null -ne $offset -and [Math]::Abs([double]$offset) -le $frontendNearPassbandThresholdHz) {
                    $sampleHasNearPassbandPeak = $true
                }

                $filterDistance = Get-FrontendPeakFilterDistanceHz `
                    -Peak $peak `
                    -FilterLowHz $sampleFilterLowHz `
                    -FilterHighHz $sampleFilterHighHz
                if ($null -eq $filterDistance) {
                    continue
                }

                if ($null -eq $frontendNearestFilterPassbandDistanceHz -or
                    [double]$filterDistance -lt [double]$frontendNearestFilterPassbandDistanceHz) {
                    $frontendNearestFilterPassbandDistanceHz = $filterDistance
                    $frontendNearestFilterPassbandPeak = $peak
                }

                if ([double]$filterDistance -le $frontendFilterPassbandEdgeToleranceHz) {
                    $frontendFilterPassbandTopPeakCount++
                    $sampleHasFilterPassbandPeak = $true
                }
            }
            Add-Number $frontendNearestFilterPassbandDistanceValues $frontendNearestFilterPassbandDistanceHz

            $sampleHasPassbandEvidencePeak = if ($sampleFilterPassbandKnown) {
                $sampleHasFilterPassbandPeak
            }
            else {
                $sampleHasNearPassbandPeak
            }

            $peakRecord = [ordered]@{
                sampleIndex = Get-JsonValue $sample "sampleIndex"
                sampledUtc = Get-JsonValue $sample "sampledUtc"
                peakCount = $frontendTopPeaks.Count
                nearest = Convert-FrontendTopPeak $nearestFrontendPeak
                strongest = Convert-FrontendTopPeak $strongestFrontendPeak
                nearPassbandPeak = $sampleHasNearPassbandPeak
                filterPassbandKnown = $sampleFilterPassbandKnown
                filterLowHz = $sampleFilterLowHz
                filterHighHz = $sampleFilterHighHz
                filterPassbandEdgeToleranceHz = $frontendFilterPassbandEdgeToleranceHz
                filterPassbandPeakCount = $frontendFilterPassbandTopPeakCount
                nearestFilterPassbandDistanceHz = $frontendNearestFilterPassbandDistanceHz
                nearestFilterPassbandPeak = Convert-FrontendTopPeak $frontendNearestFilterPassbandPeak
                passbandEvidencePeak = $sampleHasPassbandEvidencePeak
            }
            $frontendTopPeakSamples.Add($peakRecord) | Out-Null
            if ($sampleHasNearPassbandPeak) {
                $frontendNearPassbandPeakSampleCount++
                $frontendNearPassbandPeakSamples.Add($peakRecord) | Out-Null
            }
            if ($sampleHasFilterPassbandPeak) {
                $frontendFilterPassbandPeakSampleCount++
                $frontendFilterPassbandPeakSamples.Add($peakRecord) | Out-Null
            }
        }
        if (Test-Truthy (Get-JsonValue $diagnostics "frontendAdjacentNoiseUsable")) {
            $frontendAdjacentNoiseUsableCount++
        }
        Add-Number $frontendAdjacentNoiseBinValues (Get-JsonValue $diagnostics "frontendAdjacentNoiseBins")
        Add-Number $frontendAdjacentNoiseFloorValues (Get-JsonValue $diagnostics "frontendAdjacentNoiseFloorDb")
        Add-Number $frontendAdjacentNoiseP50Values (Get-JsonValue $diagnostics "frontendAdjacentNoiseP50Db")
        Add-Number $frontendAdjacentNoiseSlopeValues (Get-JsonValue $diagnostics "frontendAdjacentNoiseSlopeDbPerKhz")
        Add-Number $frontendAdjacentNoiseRejectedValues (Get-JsonValue $diagnostics "frontendAdjacentNoiseRejectedPct")
        foreach ($constraint in @($nr5Tuning["constraints"])) {
            Add-Count $nr5TuningConstraintCounts ([string]$constraint)
        }

        $sampleHasHardBlocker = $false
        foreach ($constraint in (Get-JsonArray $diagnostics "constraints")) {
            $constraintText = [string]$constraint
            Add-Count $constraintCounts $constraintText
            if (Test-HardConstraint $constraintText) {
                Add-Count $hardConstraintCounts $constraintText
                $sampleHasHardBlocker = $true
            }
        }

        if ($sampleHasHardBlocker) {
            $hardBlockerSampleCount++
        }

        foreach ($action in (Get-JsonArray $diagnostics "recommendedActions")) {
            $actionText = [string]$action
            if (-not [string]::IsNullOrWhiteSpace($actionText) -and -not $recommendations.Contains($actionText)) {
                $recommendations.Add($actionText) | Out-Null
            }
        }

        $nr5ConfidenceNumber = $null
        $nr5AgcGateNumber = $null
        $nr5SignalProbabilityNumber = $null
        $nr5TextureFillNumber = $null
        $nr5MaskSmoothingNumber = $null
        $nr5RecoveryDriveNumber = $null
        $nr5WeakSignalMemoryNumber = $null
        $nr5MakeupGainDbNumber = $null
        $nr5PeakEvidenceNumber = $null

        if ($null -ne $nr5) {
            $nr5SampleCount++
            $nr5LearnedFramesNumber = Get-NumericValue (Get-JsonValue $nr5 "learnedFrames")
            $nr5ManagedChannelGenerationNumber = Get-NumericValue (Get-JsonValue $nr5 "managedChannelGeneration")
            $nr5ManagedApplyCountNumber = Get-NumericValue (Get-JsonValue $nr5 "managedNr5ApplyCount")
            $nr5ManagedPositionApplyCountNumber = Get-NumericValue (Get-JsonValue $nr5 "managedNr5PositionApplyCount")
            $nr5ManagedPolicyApplyCountNumber = Get-NumericValue (Get-JsonValue $nr5 "managedNr5PolicyApplyCount")
            $nr5ManagedNoopApplyCountNumber = Get-NumericValue (Get-JsonValue $nr5 "managedNr5NoopApplyCount")
            $nr5ManagedRunApplyCountNumber = Get-NumericValue (Get-JsonValue $nr5 "managedNr5RunApplyCount")
            $nr5ManagedLastApplyReason = [string](Get-JsonValue $nr5 "managedNr5LastApplyReason")
            Add-Number $nr5LearnedFrameValues $nr5LearnedFramesNumber
            Add-Number $nr5ManagedChannelGenerationValues $nr5ManagedChannelGenerationNumber
            Add-Number $nr5ManagedApplyCountValues $nr5ManagedApplyCountNumber
            Add-Number $nr5ManagedPositionApplyCountValues $nr5ManagedPositionApplyCountNumber
            Add-Number $nr5ManagedPolicyApplyCountValues $nr5ManagedPolicyApplyCountNumber
            Add-Number $nr5ManagedNoopApplyCountValues $nr5ManagedNoopApplyCountNumber
            Add-Number $nr5ManagedRunApplyCountValues $nr5ManagedRunApplyCountNumber
            if ($null -ne $nr5LearnedFramesNumber) {
                $nr5LearnedFrameSampleCount++
            }
            $sampleHasManagedCounters = (
                $null -ne $nr5ManagedPositionApplyCountNumber -and
                $null -ne $nr5ManagedPolicyApplyCountNumber)
            if ($sampleHasManagedCounters) {
                $nr5ManagedCounterSampleCount++
            }
            $nr5SameManagedGeneration = $true
            if ($null -ne $nr5PreviousManagedChannelGeneration -and $null -ne $nr5ManagedChannelGenerationNumber -and
                [double]$nr5ManagedChannelGenerationNumber -ne [double]$nr5PreviousManagedChannelGeneration) {
                $nr5SameManagedGeneration = $false
                $nr5ManagedGenerationChangeCount++
                $nr5ManagedGenerationChangeSamples.Add([ordered]@{
                    sampleIndex = [int](Get-JsonValue $sample "sampleIndex")
                    sampledUtc = Get-JsonValue $sample "sampledUtc"
                    previousGeneration = [int][Math]::Round([double]$nr5PreviousManagedChannelGeneration)
                    generation = [int][Math]::Round([double]$nr5ManagedChannelGenerationNumber)
                    previousLearnedFrames = if ($null -ne $nr5PreviousLearnedFrames) { [int][Math]::Round([double]$nr5PreviousLearnedFrames) } else { $null }
                    learnedFrames = if ($null -ne $nr5LearnedFramesNumber) { [int][Math]::Round([double]$nr5LearnedFramesNumber) } else { $null }
                    lastApplyReason = $nr5ManagedLastApplyReason
                }) | Out-Null
            }
            if ($null -ne $nr5PreviousLearnedFrames -and $null -ne $nr5LearnedFramesNumber -and
                [double]$nr5LearnedFramesNumber -lt [double]$nr5PreviousLearnedFrames -and
                $nr5SameManagedGeneration) {
                $nr5LearnerResetSamples.Add([ordered]@{
                    sampleIndex = [int](Get-JsonValue $sample "sampleIndex")
                    sampledUtc = Get-JsonValue $sample "sampledUtc"
                    previousLearnedFrames = [int][Math]::Round([double]$nr5PreviousLearnedFrames)
                    learnedFrames = [int][Math]::Round([double]$nr5LearnedFramesNumber)
                    channelGeneration = if ($null -ne $nr5ManagedChannelGenerationNumber) { [int][Math]::Round([double]$nr5ManagedChannelGenerationNumber) } else { $null }
                    managedNr5ApplyCount = if ($null -ne $nr5ManagedApplyCountNumber) { [int][Math]::Round([double]$nr5ManagedApplyCountNumber) } else { $null }
                    managedNr5PositionApplyCount = if ($null -ne $nr5ManagedPositionApplyCountNumber) { [int][Math]::Round([double]$nr5ManagedPositionApplyCountNumber) } else { $null }
                    managedNr5PolicyApplyCount = if ($null -ne $nr5ManagedPolicyApplyCountNumber) { [int][Math]::Round([double]$nr5ManagedPolicyApplyCountNumber) } else { $null }
                    lastApplyReason = $nr5ManagedLastApplyReason
                }) | Out-Null
            }
            $nr5ManagedApplyDelta = $null
            $nr5ManagedPositionApplyDelta = $null
            $nr5ManagedPolicyApplyDelta = $null
            if ($nr5SameManagedGeneration) {
                if ($null -ne $nr5PreviousManagedApplyCount -and $null -ne $nr5ManagedApplyCountNumber) {
                    $nr5ManagedApplyDelta = [double]$nr5ManagedApplyCountNumber - [double]$nr5PreviousManagedApplyCount
                }
                if ($null -ne $nr5PreviousManagedPositionApplyCount -and $null -ne $nr5ManagedPositionApplyCountNumber) {
                    $nr5ManagedPositionApplyDelta = [double]$nr5ManagedPositionApplyCountNumber - [double]$nr5PreviousManagedPositionApplyCount
                }
                if ($null -ne $nr5PreviousManagedPolicyApplyCount -and $null -ne $nr5ManagedPolicyApplyCountNumber) {
                    $nr5ManagedPolicyApplyDelta = [double]$nr5ManagedPolicyApplyCountNumber - [double]$nr5PreviousManagedPolicyApplyCount
                }
                if (($null -ne $nr5ManagedPositionApplyDelta -and $nr5ManagedPositionApplyDelta -gt 0.0) -or
                    ($null -ne $nr5ManagedPolicyApplyDelta -and $nr5ManagedPolicyApplyDelta -gt 0.0)) {
                    if ($null -ne $nr5ManagedPositionApplyDelta -and $nr5ManagedPositionApplyDelta -gt 0.0) {
                        $nr5ManagedPositionReapplyCount++
                    }
                    if ($null -ne $nr5ManagedPolicyApplyDelta -and $nr5ManagedPolicyApplyDelta -gt 0.0) {
                        $nr5ManagedPolicyReapplyCount++
                    }
                    $nr5ManagedReapplySamples.Add([ordered]@{
                        sampleIndex = [int](Get-JsonValue $sample "sampleIndex")
                        sampledUtc = Get-JsonValue $sample "sampledUtc"
                        channelGeneration = if ($null -ne $nr5ManagedChannelGenerationNumber) { [int][Math]::Round([double]$nr5ManagedChannelGenerationNumber) } else { $null }
                        learnedFrames = if ($null -ne $nr5LearnedFramesNumber) { [int][Math]::Round([double]$nr5LearnedFramesNumber) } else { $null }
                        managedNr5ApplyDelta = if ($null -ne $nr5ManagedApplyDelta) { [int][Math]::Round([double]$nr5ManagedApplyDelta) } else { $null }
                        managedNr5PositionApplyDelta = if ($null -ne $nr5ManagedPositionApplyDelta) { [int][Math]::Round([double]$nr5ManagedPositionApplyDelta) } else { $null }
                        managedNr5PolicyApplyDelta = if ($null -ne $nr5ManagedPolicyApplyDelta) { [int][Math]::Round([double]$nr5ManagedPolicyApplyDelta) } else { $null }
                        managedNr5ApplyCount = if ($null -ne $nr5ManagedApplyCountNumber) { [int][Math]::Round([double]$nr5ManagedApplyCountNumber) } else { $null }
                        managedNr5PositionApplyCount = if ($null -ne $nr5ManagedPositionApplyCountNumber) { [int][Math]::Round([double]$nr5ManagedPositionApplyCountNumber) } else { $null }
                        managedNr5PolicyApplyCount = if ($null -ne $nr5ManagedPolicyApplyCountNumber) { [int][Math]::Round([double]$nr5ManagedPolicyApplyCountNumber) } else { $null }
                        lastApplyReason = $nr5ManagedLastApplyReason
                    }) | Out-Null
                }
            }
            if ($null -ne $nr5LearnedFramesNumber) {
                $nr5PreviousLearnedFrames = $nr5LearnedFramesNumber
            }
            if ($null -ne $nr5ManagedChannelGenerationNumber) {
                $nr5PreviousManagedChannelGeneration = $nr5ManagedChannelGenerationNumber
            }
            if ($null -ne $nr5ManagedApplyCountNumber) {
                $nr5PreviousManagedApplyCount = $nr5ManagedApplyCountNumber
            }
            if ($null -ne $nr5ManagedPositionApplyCountNumber) {
                $nr5PreviousManagedPositionApplyCount = $nr5ManagedPositionApplyCountNumber
            }
            if ($null -ne $nr5ManagedPolicyApplyCountNumber) {
                $nr5PreviousManagedPolicyApplyCount = $nr5ManagedPolicyApplyCountNumber
            }
            Add-Number $nr5InputValues (Get-JsonValue $nr5 "inputDbfs")
            Add-Number $nr5OutputValues (Get-JsonValue $nr5 "outputDbfs")
            Add-Number $nr5MeanGainValues (Get-JsonValue $nr5 "meanGain")
            Add-Number $nr5FloorReductionValues (Get-JsonValue $nr5 "floorReductionDb")
            Add-Number $nr5DynamicRangeValues (Get-JsonValue $nr5 "dynamicRangeDb")
            $nr5SignalConfidence = Get-JsonValue $nr5 "signalConfidence"
            $nr5AgcGate = Get-JsonValue $nr5 "agcGate"
            Add-Number $nr5SignalConfidenceValues $nr5SignalConfidence
            Add-Number $nr5AgcGateValues $nr5AgcGate
            $nr5SignalProbability = Get-JsonValue $nr5 "signalProbability"
            $nr5TextureFill = Get-JsonValue $nr5 "textureFill"
            $nr5MaskSmoothing = Get-JsonValue $nr5 "maskSmoothing"
            Add-Number $nr5SignalProbabilityValues $nr5SignalProbability
            Add-Number $nr5TextureFillValues $nr5TextureFill
            Add-Number $nr5MaskSmoothingValues $nr5MaskSmoothing
            if ($null -ne $nr5SignalProbability -and $null -ne $nr5TextureFill -and $null -ne $nr5MaskSmoothing) {
                $nr5ProbabilityDiagnosticCount++
            }
            $nr5LevelDrive = Get-JsonValue $nr5 "levelDrive"
            $nr5RecoveryDrive = Get-JsonValue $nr5 "recoveryDrive"
            $nr5WeakSignalMemory = Get-JsonValue $nr5 "weakSignalMemory"
            $nr5MakeupGainDb = Get-JsonValue $nr5 "makeupGainDb"
            Add-Number $nr5LevelDriveValues $nr5LevelDrive
            Add-Number $nr5RecoveryDriveValues $nr5RecoveryDrive
            Add-Number $nr5WeakSignalMemoryValues $nr5WeakSignalMemory
            Add-Number $nr5MakeupGainDbValues $nr5MakeupGainDb
            if ($null -ne $nr5LevelDrive -and $null -ne $nr5RecoveryDrive -and $null -ne $nr5MakeupGainDb) {
                $nr5AgcDiagnosticCount++
            }
            $nr5OutputPeakDbfs = Get-JsonValue $nr5 "outputPeakDbfs"
            $nr5PeakEvidence = Get-JsonValue $nr5 "peakEvidence"
            $nr5PeakLimitDbfs = Get-JsonValue $nr5 "peakLimitDbfs"
            $nr5PeakReductionDb = Get-JsonValue $nr5 "peakReductionDb"
            Add-Number $nr5OutputPeakDbfsValues $nr5OutputPeakDbfs
            Add-Number $nr5PeakEvidenceValues $nr5PeakEvidence
            Add-Number $nr5PeakLimitDbfsValues $nr5PeakLimitDbfs
            Add-Number $nr5PeakReductionDbValues $nr5PeakReductionDb
            if ($null -ne $nr5OutputPeakDbfs -and $null -ne $nr5PeakEvidence -and
                $null -ne $nr5PeakLimitDbfs -and $null -ne $nr5PeakReductionDb) {
                $nr5PeakDiagnosticCount++
            }
            $nr5AdjacentNoiseUsable = Get-JsonValue $nr5 "adjacentNoiseUsable"
            $nr5AdjacentNoiseTrust = Get-JsonValue $nr5 "adjacentNoiseTrust"
            $nr5AdjacentNoiseDrive = Get-JsonValue $nr5 "adjacentNoiseDrive"
            $nr5AdjacentNoiseFloor = Get-JsonValue $nr5 "adjacentNoiseFloorDb"
            $nr5AdjacentNoiseRejected = Get-JsonValue $nr5 "adjacentNoiseRejectedPct"
            $nr5AdjacentNoiseSideBalance = Get-JsonValue $nr5 "adjacentNoiseSideBalance"
            $nr5AdjacentNoiseAsymmetry = Get-JsonValue $nr5 "adjacentNoiseAsymmetryDb"
            Add-Number $nr5AdjacentNoiseTrustValues $nr5AdjacentNoiseTrust
            Add-Number $nr5AdjacentNoiseDriveValues $nr5AdjacentNoiseDrive
            Add-Number $nr5AdjacentNoiseFloorValues $nr5AdjacentNoiseFloor
            Add-Number $nr5AdjacentNoiseRejectedValues $nr5AdjacentNoiseRejected
            Add-Number $nr5AdjacentNoiseSideBalanceValues $nr5AdjacentNoiseSideBalance
            Add-Number $nr5AdjacentNoiseAsymmetryValues $nr5AdjacentNoiseAsymmetry
            if (Test-Truthy $nr5AdjacentNoiseUsable) {
                $nr5AdjacentNoiseUsableCount++
            }
            $nr5AdjacentNoiseDriveNumber = Get-NumericValue $nr5AdjacentNoiseDrive
            if ($null -ne $nr5AdjacentNoiseDriveNumber -and $nr5AdjacentNoiseDriveNumber -gt 0.001) {
                $nr5AdjacentNoiseDriveCount++
            }

            if ((Test-NrModeName $requestedNrMode "Nr5") -and (Test-NrModeName $effectiveNrMode "Nr5") -and
                (Test-Truthy (Get-JsonValue $nr5 "run"))) {
                $nr5AlignedCount++
            }

            $nr5InputDbfs = Get-NumericValue (Get-JsonValue $nr5 "inputDbfs")
            $nr5OutputDbfs = Get-NumericValue (Get-JsonValue $nr5 "outputDbfs")
            $nr5ConfidenceNumber = Get-NumericValue $nr5SignalConfidence
            $nr5AgcGateNumber = Get-NumericValue $nr5AgcGate
            $nr5SignalProbabilityNumber = Get-NumericValue $nr5SignalProbability
            $nr5TextureFillNumber = Get-NumericValue $nr5TextureFill
            $nr5MaskSmoothingNumber = Get-NumericValue $nr5MaskSmoothing
            $nr5RecoveryDriveNumber = Get-NumericValue $nr5RecoveryDrive
            $nr5WeakSignalMemoryNumber = Get-NumericValue $nr5WeakSignalMemory
            $nr5MakeupGainDbNumber = Get-NumericValue $nr5MakeupGainDb
            $nr5PeakEvidenceNumber = Get-NumericValue $nr5PeakEvidence
            $runtimeAudioRmsDbfsNumber = Get-NumericValue (Get-JsonValue $runtime "audioRmsDbfs")
            $runtimeIsRxAudio = Test-RuntimeRxAudio $runtime
            $runtimeLevelerInputRmsDbfsNumber = Get-NumericValue (Get-JsonValue $runtime "rxAudioLevelerInputRmsDbfs")
            $runtimeLevelerOutputRmsDbfsNumber = Get-NumericValue (Get-JsonValue $runtime "rxAudioLevelerOutputRmsDbfs")
            $runtimeLevelerAppliedGainDbNumber = Get-NumericValue (Get-JsonValue $runtime "rxAudioLevelerAppliedGainDb")
            $runtimeLevelerPauseHoldBlocksNumber = Get-NumericValue (Get-JsonValue $runtime "rxAudioLevelerPauseHoldBlocks")
            $runtimeLevelerNr5SpeechHoldBlocksNumber = Get-NumericValue (Get-JsonValue $runtime "rxAudioLevelerNr5SpeechHoldBlocks")
            $runtimeFinalAudioRmsDbfsNumber = if (-not $runtimeIsRxAudio) {
                $null
            }
            elseif ($null -ne $runtimeLevelerOutputRmsDbfsNumber) {
                $runtimeLevelerOutputRmsDbfsNumber
            }
            else {
                $runtimeAudioRmsDbfsNumber
            }
            $rxAudioLevelerBoostSlewLimited = Test-Truthy (Get-JsonValue $runtime "rxAudioLevelerBoostSlewLimited")
            $rxAudioLevelerPeakLimited = Test-Truthy (Get-JsonValue $runtime "rxAudioLevelerPeakLimited")
            $rxAudioLevelerOutputLimited = Test-Truthy (Get-JsonValue $runtime "rxAudioLevelerOutputLimited")
            $nr5AudioInputDeltaDb = $null
            $nr5AudioOutputDeltaDb = $null
            $nr5AudioAlignedAfterLeveler = $false
            $nr5AudioLevelerNormalized = $false
            $nr5AudioAlignmentMismatch = $false
            if ($null -ne $nr5OutputDbfs -and $null -ne $runtimeLevelerInputRmsDbfsNumber) {
                $nr5AudioInputDeltaDb = [Math]::Round($runtimeLevelerInputRmsDbfsNumber - $nr5OutputDbfs, 1)
                Add-Number $nr5AudioInputDeltaValues $nr5AudioInputDeltaDb
                $nr5AudioAlignmentSampleCount++
            }
            if ($null -ne $nr5OutputDbfs -and $null -ne $runtimeLevelerOutputRmsDbfsNumber) {
                $nr5AudioOutputDeltaDb = [Math]::Round($runtimeLevelerOutputRmsDbfsNumber - $nr5OutputDbfs, 1)
                Add-Number $nr5AudioOutputDeltaValues $nr5AudioOutputDeltaDb
                if ([Math]::Abs($nr5AudioOutputDeltaDb) -le 12.0) {
                    $nr5AudioAlignedAfterLeveler = $true
                    $nr5AudioAlignedAfterLevelerCount++
                }
            }
            $nr5AudioLevelerNormalized = Test-Nr5LevelerNormalizedSpeechAlignment `
                -Nr5OutputDbfs $nr5OutputDbfs `
                -LevelerInputRmsDbfs $runtimeLevelerInputRmsDbfsNumber `
                -LevelerOutputRmsDbfs $runtimeLevelerOutputRmsDbfsNumber `
                -LevelerAppliedGainDb $runtimeLevelerAppliedGainDbNumber `
                -LevelerBoostSlewLimited $rxAudioLevelerBoostSlewLimited `
                -LevelerPeakLimited $rxAudioLevelerPeakLimited `
                -LevelerOutputLimited $rxAudioLevelerOutputLimited
            if ($nr5AudioLevelerNormalized) {
                $nr5AudioLevelerNormalizedCount++
                $nr5AudioLevelerNormalizedSamples.Add([ordered]@{
                    sampleIndex = [int](Get-JsonValue $sample "sampleIndex")
                    nr5OutputDbfs = $nr5OutputDbfs
                    rxAudioLevelerInputRmsDbfs = $runtimeLevelerInputRmsDbfsNumber
                    rxAudioLevelerOutputRmsDbfs = $runtimeLevelerOutputRmsDbfsNumber
                    rxAudioLevelerAppliedGainDb = $runtimeLevelerAppliedGainDbNumber
                    inputDeltaDb = $nr5AudioInputDeltaDb
                    outputDeltaDb = $nr5AudioOutputDeltaDb
                    audioRmsDbfs = $runtimeAudioRmsDbfsNumber
                    audioLastSeq = Get-JsonValue $runtime "audioLastSeq"
                    audioFramesBroadcast = Get-JsonValue $runtime "audioFramesBroadcast"
                    audioAgeMs = Get-JsonValue $runtime "audioAgeMs"
                }) | Out-Null
            }
            if ($null -ne $nr5AudioInputDeltaDb) {
                if ([Math]::Abs($nr5AudioInputDeltaDb) -gt 12.0 -and
                    -not $nr5AudioAlignedAfterLeveler -and
                    -not $nr5AudioLevelerNormalized) {
                    $nr5AudioAlignmentMismatch = $true
                    $nr5AudioAlignmentMismatchCount++
                    $nr5AudioAlignmentMismatchSamples.Add([ordered]@{
                        sampleIndex = [int](Get-JsonValue $sample "sampleIndex")
                        nr5OutputDbfs = $nr5OutputDbfs
                        rxAudioLevelerInputRmsDbfs = $runtimeLevelerInputRmsDbfsNumber
                        rxAudioLevelerOutputRmsDbfs = $runtimeLevelerOutputRmsDbfsNumber
                        inputDeltaDb = $nr5AudioInputDeltaDb
                        outputDeltaDb = $nr5AudioOutputDeltaDb
                        deltaDb = if ($null -ne $nr5AudioOutputDeltaDb) { $nr5AudioOutputDeltaDb } else { $nr5AudioInputDeltaDb }
                        audioRmsDbfs = $runtimeAudioRmsDbfsNumber
                        audioLastSeq = Get-JsonValue $runtime "audioLastSeq"
                        audioFramesBroadcast = Get-JsonValue $runtime "audioFramesBroadcast"
                        audioAgeMs = Get-JsonValue $runtime "audioAgeMs"
                    }) | Out-Null
                }
            }
            if ($null -ne $nr5InputDbfs -and $null -ne $nr5OutputDbfs) {
                $nr5InputOutputXValues.Add([double]$nr5InputDbfs) | Out-Null
                $nr5InputOutputYValues.Add([double]$nr5OutputDbfs) | Out-Null
                $nr5OutputMinusInputValues.Add([double]($nr5OutputDbfs - $nr5InputDbfs)) | Out-Null
            }
            $isLowEvidenceWeakInput = ($null -ne $nr5InputDbfs -and
                $nr5InputDbfs -le $nr5LowEvidenceInputThresholdDbfs -and
                $null -ne $nr5ConfidenceNumber -and
                $nr5ConfidenceNumber -le $nr5LowEvidenceConfidenceThreshold -and
                $null -ne $nr5SignalProbabilityNumber -and
                $nr5SignalProbabilityNumber -le $nr5LowEvidenceProbabilityThreshold -and
                $null -ne $nr5AgcGateNumber -and
                $nr5AgcGateNumber -le $nr5LowEvidenceAgcGateThreshold)
            if ($isLowEvidenceWeakInput) {
                $nr5LowEvidenceSampleCount++
                $nr5OutputLiftedLowEvidence = ($null -ne $nr5OutputDbfs -and
                    $nr5OutputDbfs -ge $nr5LowEvidenceOutputThresholdDbfs)
                $runtimeLiftedLowEvidence = (-not $nr5AudioAlignmentMismatch -and
                    $null -ne $runtimeFinalAudioRmsDbfsNumber -and
                    $runtimeFinalAudioRmsDbfsNumber -ge $nr5LowEvidenceAudioThresholdDbfs)
                $nr5LiftedLowEvidence = $nr5OutputLiftedLowEvidence -or $runtimeLiftedLowEvidence
                if ($nr5AudioAlignmentMismatch) {
                    $nr5LowEvidenceAlignmentMismatchCount++
                }
                if ($nr5LiftedLowEvidence) {
                    $nr5LowEvidenceLiftCount++
                    $nr5LowEvidenceLiftSamples.Add([ordered]@{
                        sampleIndex = [int](Get-JsonValue $sample "sampleIndex")
                        inputDbfs = $nr5InputDbfs
                        outputDbfs = $nr5OutputDbfs
                        signalConfidence = $nr5ConfidenceNumber
                        agcGate = $nr5AgcGateNumber
                        signalProbability = $nr5SignalProbabilityNumber
                        textureFill = $nr5TextureFillNumber
                        maskSmoothing = $nr5MaskSmoothingNumber
                        recoveryDrive = $nr5RecoveryDriveNumber
                        weakSignalMemory = $nr5WeakSignalMemoryNumber
                        makeupGainDb = $nr5MakeupGainDbNumber
                        audioRmsDbfs = $runtimeAudioRmsDbfsNumber
                        rxAudioLevelerInputRmsDbfs = $runtimeLevelerInputRmsDbfsNumber
                        rxAudioLevelerOutputRmsDbfs = $runtimeLevelerOutputRmsDbfsNumber
                        rxAudioLevelerAppliedGainDb = $runtimeLevelerAppliedGainDbNumber
                        rxAudioLevelerPauseHoldBlocks = $runtimeLevelerPauseHoldBlocksNumber
                        rxAudioLevelerNr5SpeechHoldBlocks = $runtimeLevelerNr5SpeechHoldBlocksNumber
                        nr5AudioInputDeltaDb = $nr5AudioInputDeltaDb
                        nr5AudioAlignmentMismatch = $nr5AudioAlignmentMismatch
                    }) | Out-Null
                }
                elseif (-not $nr5AudioAlignmentMismatch -and $null -ne $nr5OutputDbfs -and $nr5OutputDbfs -le -35.0) {
                    $nr5LowEvidenceSuppressedCount++
                    $nr5LowEvidenceSuppressedSamples.Add([ordered]@{
                        sampleIndex = [int](Get-JsonValue $sample "sampleIndex")
                        inputDbfs = $nr5InputDbfs
                        outputDbfs = $nr5OutputDbfs
                        signalConfidence = $nr5ConfidenceNumber
                        agcGate = $nr5AgcGateNumber
                        signalProbability = $nr5SignalProbabilityNumber
                        textureFill = $nr5TextureFillNumber
                        maskSmoothing = $nr5MaskSmoothingNumber
                        recoveryDrive = $nr5RecoveryDriveNumber
                        weakSignalMemory = $nr5WeakSignalMemoryNumber
                        makeupGainDb = $nr5MakeupGainDbNumber
                        audioRmsDbfs = $runtimeAudioRmsDbfsNumber
                        rxAudioLevelerInputRmsDbfs = $runtimeLevelerInputRmsDbfsNumber
                        rxAudioLevelerOutputRmsDbfs = $runtimeLevelerOutputRmsDbfsNumber
                        rxAudioLevelerAppliedGainDb = $runtimeLevelerAppliedGainDbNumber
                        rxAudioLevelerPauseHoldBlocks = $runtimeLevelerPauseHoldBlocksNumber
                        rxAudioLevelerNr5SpeechHoldBlocks = $runtimeLevelerNr5SpeechHoldBlocksNumber
                        nr5AudioInputDeltaDb = $nr5AudioInputDeltaDb
                        nr5AudioAlignmentMismatch = $nr5AudioAlignmentMismatch
                    }) | Out-Null
                }
            }
            if ($null -ne $nr5InputDbfs -and $nr5InputDbfs -le -30.0) {
                $nr5WeakInputCount++
                if ($null -ne $nr5OutputDbfs) {
                    $nr5WeakOutputValues.Add([double]$nr5OutputDbfs) | Out-Null
                }
                Add-Number $nr5WeakFinalAudioValues $runtimeFinalAudioRmsDbfsNumber
                $nr5SpeechQualifiedWeakInput = (
                    $runtimeIsRxAudio -and
                    -not $isLowEvidenceWeakInput -and
                    -not $nr5AudioAlignmentMismatch -and
                    $null -ne $nr5OutputDbfs -and
                    $nr5OutputDbfs -ge -40.0 -and
                    ($sampleHasPassbandEvidencePeak -or
                        ($null -ne $nr5PeakEvidenceNumber -and $nr5PeakEvidenceNumber -ge 0.08) -or
                        ($null -ne $nr5ConfidenceNumber -and $nr5ConfidenceNumber -ge 0.30 -and
                            $null -ne $nr5SignalProbabilityNumber -and $nr5SignalProbabilityNumber -ge 0.14)))
                if ($nr5SpeechQualifiedWeakInput) {
                    $nr5SpeechQualifiedWeakOutputValues.Add([double]$nr5OutputDbfs) | Out-Null
                    Add-Number $nr5SpeechQualifiedWeakFinalAudioValues $runtimeFinalAudioRmsDbfsNumber
                }
                $nr5PassbandQualifiedWeakInput = (
                    $nr5SpeechQualifiedWeakInput -and
                    $sampleHasNearPassbandPeak -and
                    $null -ne $runtimeFinalAudioRmsDbfsNumber -and
                    $runtimeFinalAudioRmsDbfsNumber -ge $nr5WeakDropoutFinalAudibleThresholdDbfs)
                if ($nr5PassbandQualifiedWeakInput) {
                    $nr5PassbandQualifiedWeakOutputValues.Add([double]$nr5OutputDbfs) | Out-Null
                    Add-Number $nr5PassbandQualifiedWeakFinalAudioValues $runtimeFinalAudioRmsDbfsNumber
                }
                $nr5WeakInputSample = New-Nr5ClassifiedInputSample `
                    -Class "weak" `
                    -Sample $sample `
                    -InputDbfs $nr5InputDbfs `
                    -OutputDbfs $nr5OutputDbfs `
                    -FinalAudioRmsDbfs $runtimeFinalAudioRmsDbfsNumber `
                    -LevelerInputRmsDbfs $runtimeLevelerInputRmsDbfsNumber `
                    -LevelerOutputRmsDbfs $runtimeLevelerOutputRmsDbfsNumber `
                    -LevelerAppliedGainDb $runtimeLevelerAppliedGainDbNumber `
                    -SignalConfidence $nr5ConfidenceNumber `
                    -AgcGate $nr5AgcGateNumber `
                    -SignalProbability $nr5SignalProbabilityNumber `
                    -PeakEvidence $nr5PeakEvidenceNumber `
                    -MakeupGainDb $nr5MakeupGainDbNumber `
                    -RecoveryDrive $nr5RecoveryDriveNumber `
                    -WeakSignalMemory $nr5WeakSignalMemoryNumber `
                    -NearPassbandPeak $sampleHasNearPassbandPeak `
                    -FilterPassbandPeak $sampleHasFilterPassbandPeak `
                    -PassbandEvidencePeak $sampleHasPassbandEvidencePeak `
                    -NearestFilterPassbandDistanceHz $frontendNearestFilterPassbandDistanceHz `
                    -NearestPeak $nearestFrontendPeak `
                    -StrongestPeak $strongestFrontendPeak `
                    -SpeechQualified $nr5SpeechQualifiedWeakInput `
                    -PassbandQualified $nr5PassbandQualifiedWeakInput `
                    -LowEvidence $isLowEvidenceWeakInput `
                    -AudioAlignmentMismatch $nr5AudioAlignmentMismatch `
                    -AudioInputDeltaDb $nr5AudioInputDeltaDb
                $nr5WeakInputSamples.Add($nr5WeakInputSample) | Out-Null
                if ($nr5SpeechQualifiedWeakInput) {
                    $nr5SpeechQualifiedWeakInputSamples.Add($nr5WeakInputSample) | Out-Null
                }
                if ($nr5PassbandQualifiedWeakInput) {
                    $nr5PassbandQualifiedWeakInputSamples.Add($nr5WeakInputSample) | Out-Null
                }
                if ($null -ne $nr5OutputDbfs -and $nr5OutputDbfs -ge -30.0) {
                    $nr5WeakRecoveredCount++
                }
                if ($null -ne $nr5OutputDbfs -and $nr5OutputDbfs -lt ($nr5InputDbfs - 1.0)) {
                    $nr5WeakBelowInputCount++
                }
                if ($null -ne $nr5OutputDbfs -and $nr5OutputDbfs -ge -31.5 -and $nr5OutputDbfs -le -20.0) {
                    $nr5WeakNearTargetCount++
                }
                if (-not $isLowEvidenceWeakInput -and -not $nr5AudioAlignmentMismatch -and
                    $null -ne $nr5OutputDbfs -and $nr5OutputDbfs -le -35.0) {
                    $nr5WeakDropoutCount++
                    $nr5WeakDropoutFinalAudible = (
                        $null -ne $runtimeFinalAudioRmsDbfsNumber -and
                        $runtimeFinalAudioRmsDbfsNumber -ge $nr5WeakDropoutFinalAudibleThresholdDbfs)
                    $nr5WeakDropoutNativeLifted = ($nr5OutputDbfs -ge ($nr5InputDbfs + $nr5WeakDropoutNativeLiftThresholdDb))
                    $nr5WeakDropoutBelowInput = ($nr5OutputDbfs -lt ($nr5InputDbfs - $nr5WeakDropoutBelowInputThresholdDb))
                    $nr5WeakDropoutCandidateLoss = (-not $nr5WeakDropoutFinalAudible -and
                        -not $nr5WeakDropoutNativeLifted -and
                        $nr5WeakDropoutBelowInput)
                    $nr5WeakDropoutClass = if ($nr5WeakDropoutFinalAudible) {
                        "final-audible"
                    }
                    elseif ($nr5WeakDropoutNativeLifted) {
                        "native-lifted-still-low"
                    }
                    elseif ($nr5WeakDropoutCandidateLoss) {
                        "candidate-weak-loss"
                    }
                    else {
                        "strict-low-output"
                    }
                    if ($nr5WeakDropoutFinalAudible) {
                        $nr5WeakDropoutFinalAudibleCount++
                    }
                    if ($nr5WeakDropoutNativeLifted) {
                        $nr5WeakDropoutNativeLiftedCount++
                    }
                    if ($nr5WeakDropoutCandidateLoss) {
                        $nr5WeakDropoutCandidateLossCount++
                    }
                    $nr5WeakDropoutSample = [ordered]@{
                        sampleIndex = [int](Get-JsonValue $sample "sampleIndex")
                        dropoutClass = $nr5WeakDropoutClass
                        inputDbfs = $nr5InputDbfs
                        outputDbfs = $nr5OutputDbfs
                        signalConfidence = $nr5ConfidenceNumber
                        agcGate = $nr5AgcGateNumber
                        signalProbability = $nr5SignalProbabilityNumber
                        textureFill = $nr5TextureFillNumber
                        maskSmoothing = $nr5MaskSmoothingNumber
                        recoveryDrive = $nr5RecoveryDriveNumber
                        weakSignalMemory = $nr5WeakSignalMemoryNumber
                        makeupGainDb = $nr5MakeupGainDbNumber
                        audioRmsDbfs = $runtimeAudioRmsDbfsNumber
                        rxAudioLevelerInputRmsDbfs = $runtimeLevelerInputRmsDbfsNumber
                        rxAudioLevelerOutputRmsDbfs = $runtimeLevelerOutputRmsDbfsNumber
                        finalAudible = $nr5WeakDropoutFinalAudible
                        nativeLifted = $nr5WeakDropoutNativeLifted
                        candidateWeakLoss = $nr5WeakDropoutCandidateLoss
                        nr5AudioInputDeltaDb = $nr5AudioInputDeltaDb
                        nr5AudioAlignmentMismatch = $nr5AudioAlignmentMismatch
                    }
                    $nr5WeakDropoutSamples.Add($nr5WeakDropoutSample) | Out-Null
                    if ($nr5WeakDropoutFinalAudible) {
                        $nr5WeakDropoutFinalAudibleSamples.Add($nr5WeakDropoutSample) | Out-Null
                    }
                    if ($nr5WeakDropoutCandidateLoss) {
                        $nr5WeakDropoutCandidateLossSamples.Add($nr5WeakDropoutSample) | Out-Null
                    }
                }
            }
            elseif ($null -ne $nr5InputDbfs -and $nr5InputDbfs -ge $nr5StrongInputThresholdDbfs) {
                $nr5StrongInputCount++
                if ($null -ne $nr5OutputDbfs) {
                    $nr5StrongOutputValues.Add([double]$nr5OutputDbfs) | Out-Null
                }
                Add-Number $nr5StrongFinalAudioValues $runtimeFinalAudioRmsDbfsNumber
                $nr5SpeechQualifiedStrongInput = (
                    -not $nr5AudioAlignmentMismatch -and
                    $null -ne $nr5OutputDbfs -and
                    $nr5OutputDbfs -ge -26.0 -and
                    $null -ne $runtimeFinalAudioRmsDbfsNumber -and
                    $runtimeFinalAudioRmsDbfsNumber -ge -30.0 -and
                    ($sampleHasNearPassbandPeak -or
                        ($null -ne $nr5PeakEvidenceNumber -and $nr5PeakEvidenceNumber -ge 0.08) -or
                        ($null -ne $nr5ConfidenceNumber -and $nr5ConfidenceNumber -ge 0.30 -and
                            $null -ne $nr5SignalProbabilityNumber -and $nr5SignalProbabilityNumber -ge 0.14)))
                if ($nr5SpeechQualifiedStrongInput) {
                    $nr5SpeechQualifiedStrongOutputValues.Add([double]$nr5OutputDbfs) | Out-Null
                    Add-Number $nr5SpeechQualifiedStrongFinalAudioValues $runtimeFinalAudioRmsDbfsNumber
                }
                $nr5PassbandQualifiedStrongInput = (
                    $nr5SpeechQualifiedStrongInput -and
                    $sampleHasNearPassbandPeak)
                if ($nr5PassbandQualifiedStrongInput) {
                    $nr5PassbandQualifiedStrongOutputValues.Add([double]$nr5OutputDbfs) | Out-Null
                    Add-Number $nr5PassbandQualifiedStrongFinalAudioValues $runtimeFinalAudioRmsDbfsNumber
                }
                $nr5StrongInputSample = New-Nr5ClassifiedInputSample `
                    -Class "strong" `
                    -Sample $sample `
                    -InputDbfs $nr5InputDbfs `
                    -OutputDbfs $nr5OutputDbfs `
                    -FinalAudioRmsDbfs $runtimeFinalAudioRmsDbfsNumber `
                    -LevelerInputRmsDbfs $runtimeLevelerInputRmsDbfsNumber `
                    -LevelerOutputRmsDbfs $runtimeLevelerOutputRmsDbfsNumber `
                    -LevelerAppliedGainDb $runtimeLevelerAppliedGainDbNumber `
                    -SignalConfidence $nr5ConfidenceNumber `
                    -AgcGate $nr5AgcGateNumber `
                    -SignalProbability $nr5SignalProbabilityNumber `
                    -PeakEvidence $nr5PeakEvidenceNumber `
                    -MakeupGainDb $nr5MakeupGainDbNumber `
                    -RecoveryDrive $nr5RecoveryDriveNumber `
                    -WeakSignalMemory $nr5WeakSignalMemoryNumber `
                    -NearPassbandPeak $sampleHasNearPassbandPeak `
                    -FilterPassbandPeak $sampleHasFilterPassbandPeak `
                    -PassbandEvidencePeak $sampleHasPassbandEvidencePeak `
                    -NearestFilterPassbandDistanceHz $frontendNearestFilterPassbandDistanceHz `
                    -NearestPeak $nearestFrontendPeak `
                    -StrongestPeak $strongestFrontendPeak `
                    -SpeechQualified $nr5SpeechQualifiedStrongInput `
                    -PassbandQualified $nr5PassbandQualifiedStrongInput `
                    -LowEvidence $false `
                    -AudioAlignmentMismatch $nr5AudioAlignmentMismatch `
                    -AudioInputDeltaDb $nr5AudioInputDeltaDb
                $nr5StrongInputSamples.Add($nr5StrongInputSample) | Out-Null
                if ($nr5SpeechQualifiedStrongInput) {
                    $nr5SpeechQualifiedStrongInputSamples.Add($nr5StrongInputSample) | Out-Null
                }
                if ($nr5PassbandQualifiedStrongInput) {
                    $nr5PassbandQualifiedStrongInputSamples.Add($nr5StrongInputSample) | Out-Null
                }
            }
            elseif ($null -ne $nr5InputDbfs -and $nr5InputDbfs -ge $nr5NearStrongInputThresholdDbfs) {
                $nr5NearStrongInputCount++
                if ($null -ne $nr5OutputDbfs) {
                    $nr5NearStrongOutputValues.Add([double]$nr5OutputDbfs) | Out-Null
                }
                Add-Number $nr5NearStrongFinalAudioValues $runtimeFinalAudioRmsDbfsNumber
                $nr5SpeechQualifiedNearStrongInput = (
                    -not $nr5AudioAlignmentMismatch -and
                    $null -ne $nr5OutputDbfs -and
                    $nr5OutputDbfs -ge -28.0 -and
                    $null -ne $runtimeFinalAudioRmsDbfsNumber -and
                    $runtimeFinalAudioRmsDbfsNumber -ge -35.0 -and
                    ($sampleHasNearPassbandPeak -or
                        ($null -ne $nr5PeakEvidenceNumber -and $nr5PeakEvidenceNumber -ge 0.08) -or
                        ($null -ne $nr5ConfidenceNumber -and $nr5ConfidenceNumber -ge 0.30 -and
                            $null -ne $nr5SignalProbabilityNumber -and $nr5SignalProbabilityNumber -ge 0.14)))
                if ($nr5SpeechQualifiedNearStrongInput) {
                    $nr5SpeechQualifiedNearStrongOutputValues.Add([double]$nr5OutputDbfs) | Out-Null
                    Add-Number $nr5SpeechQualifiedNearStrongFinalAudioValues $runtimeFinalAudioRmsDbfsNumber
                }
                $nr5PassbandQualifiedNearStrongInput = (
                    $nr5SpeechQualifiedNearStrongInput -and
                    $sampleHasPassbandEvidencePeak)
                if ($nr5PassbandQualifiedNearStrongInput) {
                    $nr5PassbandQualifiedNearStrongOutputValues.Add([double]$nr5OutputDbfs) | Out-Null
                    Add-Number $nr5PassbandQualifiedNearStrongFinalAudioValues $runtimeFinalAudioRmsDbfsNumber
                }

                $nr5NearStrongInputSamples.Add([ordered]@{
                    sampleIndex = [int](Get-JsonValue $sample "sampleIndex")
                    inputDbfs = $nr5InputDbfs
                    outputDbfs = $nr5OutputDbfs
                    finalAudioRmsDbfs = $runtimeFinalAudioRmsDbfsNumber
                    rxAudioLevelerInputRmsDbfs = $runtimeLevelerInputRmsDbfsNumber
                    rxAudioLevelerOutputRmsDbfs = $runtimeLevelerOutputRmsDbfsNumber
                    distanceToStrongThresholdDb = [Math]::Round([Math]::Max(0.0, [double]$nr5StrongInputThresholdDbfs - [double]$nr5InputDbfs), 3)
                    signalConfidence = $nr5ConfidenceNumber
                    agcGate = $nr5AgcGateNumber
                    signalProbability = $nr5SignalProbabilityNumber
                    peakEvidence = $nr5PeakEvidenceNumber
                    nearPassbandPeak = $sampleHasNearPassbandPeak
                    filterPassbandPeak = $sampleHasFilterPassbandPeak
                    passbandEvidencePeak = $sampleHasPassbandEvidencePeak
                    nearestFilterPassbandDistanceHz = $frontendNearestFilterPassbandDistanceHz
                    nearest = Convert-FrontendTopPeak $nearestFrontendPeak
                    strongest = Convert-FrontendTopPeak $strongestFrontendPeak
                    speechQualified = $nr5SpeechQualifiedNearStrongInput
                    passbandQualified = $nr5PassbandQualifiedNearStrongInput
                }) | Out-Null
            }
            if ($null -ne $nr5MakeupGainDbNumber -and $nr5MakeupGainDbNumber -ge 12.0) {
                $nr5HotMakeupCount++
                $nr5HotMakeupSamples.Add([ordered]@{
                    sampleIndex = [int](Get-JsonValue $sample "sampleIndex")
                    inputDbfs = $nr5InputDbfs
                    outputDbfs = $nr5OutputDbfs
                    signalConfidence = $nr5ConfidenceNumber
                    agcGate = $nr5AgcGateNumber
                    signalProbability = $nr5SignalProbabilityNumber
                    textureFill = $nr5TextureFillNumber
                    maskSmoothing = $nr5MaskSmoothingNumber
                    recoveryDrive = $nr5RecoveryDriveNumber
                    weakSignalMemory = $nr5WeakSignalMemoryNumber
                    makeupGainDb = $nr5MakeupGainDbNumber
                }) | Out-Null
            }
        }

        if ($null -ne $runtime) {
            $runtimeCount++
            Add-Count $runtimeStatusCounts ([string](Get-JsonValue $runtime "status"))
            Add-Count $audioStatusCounts ([string](Get-JsonValue $runtime "audioStatus"))
            $runtimeAgcGainDbNumber = Get-NumericValue (Get-JsonValue $runtime "agcGainDb")
            Add-Number $agcValues $runtimeAgcGainDbNumber
            Add-Number $headroomValues (Get-JsonValue $runtime "adcHeadroomDb")
            $runtimeAudioRmsDbfsNumber = Get-NumericValue (Get-JsonValue $runtime "audioRmsDbfs")
            $runtimeIsRxAudio = Test-RuntimeRxAudio $runtime
            if ($runtimeIsRxAudio) {
                Add-Number $rmsValues $runtimeAudioRmsDbfsNumber
                Add-Number $peakValues (Get-JsonValue $runtime "audioPeakDbfs")
            }
            $audioIsFloor = ($runtimeIsRxAudio -and $null -ne $runtimeAudioRmsDbfsNumber -and
                $runtimeAudioRmsDbfsNumber -le $signalFloorAudioThresholdDbfs)
            $audioIsActive = ($runtimeIsRxAudio -and $null -ne $runtimeAudioRmsDbfsNumber -and
                $runtimeAudioRmsDbfsNumber -ge $signalActiveAudioThresholdDbfs)
            $voiceLikeEvidence = ($null -ne $nr5ConfidenceNumber -and $nr5ConfidenceNumber -ge $signalEvidenceConfidenceThreshold) -or
                ($null -ne $nr5SignalProbabilityNumber -and $nr5SignalProbabilityNumber -ge $signalEvidenceProbabilityThreshold) -or
                ($null -ne $nr5AgcGateNumber -and $nr5AgcGateNumber -ge $signalEvidenceAgcGateThreshold)
            if ($audioIsFloor) {
                $signalFloorAudioCount++
                Add-Number $floorAudioRmsValues $runtimeAudioRmsDbfsNumber
            }
            if ($audioIsActive) {
                $signalActiveAudioCount++
                Add-Number $activeAudioRmsValues $runtimeAudioRmsDbfsNumber
                Add-Number $activeAgcValues $runtimeAgcGainDbNumber
                if ($previousAudioWasFloor) {
                    $signalIntermittentBurstCount++
                }
            }
            if ($voiceLikeEvidence) {
                $signalVoiceLikeEvidenceCount++
                if ($runtimeIsRxAudio) {
                    Add-Number $voiceLikeAudioRmsValues $runtimeAudioRmsDbfsNumber
                    Add-Number $voiceLikeAgcValues $runtimeAgcGainDbNumber
                }
            }
            if ($audioIsFloor -and -not $voiceLikeEvidence) {
                $signalQuietNoEvidenceCount++
                Add-Number $quietNoEvidenceAudioRmsValues $runtimeAudioRmsDbfsNumber
                Add-Number $quietNoEvidenceAgcValues $runtimeAgcGainDbNumber
            }
            $previousAudioWasFloor = $audioIsFloor
            $rxAudioLevelerInputRmsDbfs = Get-JsonValue $runtime "rxAudioLevelerInputRmsDbfs"
            $rxAudioLevelerOutputRmsDbfs = Get-JsonValue $runtime "rxAudioLevelerOutputRmsDbfs"
            $rxAudioLevelerInputPeakDbfs = Get-JsonValue $runtime "rxAudioLevelerInputPeakDbfs"
            $rxAudioLevelerOutputPeakDbfs = Get-JsonValue $runtime "rxAudioLevelerOutputPeakDbfs"
            $runtimePassbandAudioRmsDbfsNumber = $null
            if ($runtimeIsRxAudio) {
                $runtimePassbandAudioRmsDbfsNumber = Get-NumericValue $rxAudioLevelerOutputRmsDbfs
                if ($null -eq $runtimePassbandAudioRmsDbfsNumber) {
                    $runtimePassbandAudioRmsDbfsNumber = $runtimeAudioRmsDbfsNumber
                }
            }
            if ($null -ne $runtimePassbandAudioRmsDbfsNumber) {
                $passbandAudioRecord = [ordered]@{
                    sampleIndex = [int](Get-JsonValue $sample "sampleIndex")
                    sampledUtc = Get-JsonValue $sample "sampledUtc"
                    audioRmsDbfs = $runtimeAudioRmsDbfsNumber
                    finalAudioRmsDbfs = $runtimePassbandAudioRmsDbfsNumber
                    rxAudioLevelerInputRmsDbfs = Get-NumericValue $rxAudioLevelerInputRmsDbfs
                    rxAudioLevelerOutputRmsDbfs = Get-NumericValue $rxAudioLevelerOutputRmsDbfs
                    nearest = Convert-FrontendTopPeak $nearestFrontendPeak
                    strongest = Convert-FrontendTopPeak $strongestFrontendPeak
                }
                if ($sampleHasPassbandEvidencePeak) {
                    Add-Number $passbandAudioRmsValues $runtimePassbandAudioRmsDbfsNumber
                    $passbandAudioSamples.Add($passbandAudioRecord) | Out-Null
                    if ($runtimePassbandAudioRmsDbfsNumber -ge $signalActiveAudioThresholdDbfs) {
                        Add-Number $passbandActiveAudioRmsValues $runtimePassbandAudioRmsDbfsNumber
                    }
                    if ($runtimePassbandAudioRmsDbfsNumber -le $signalFloorAudioThresholdDbfs) {
                        Add-Number $passbandFloorAudioRmsValues $runtimePassbandAudioRmsDbfsNumber
                    }
                }
                elseif ($frontendTopPeaks.Count -gt 0) {
                    Add-Number $offPassbandAudioRmsValues $runtimePassbandAudioRmsDbfsNumber
                    $offPassbandAudioSamples.Add($passbandAudioRecord) | Out-Null
                }
            }
            $rxAudioLevelerDesiredGainDb = Get-JsonValue $runtime "rxAudioLevelerDesiredGainDb"
            $rxAudioLevelerAppliedGainDb = Get-JsonValue $runtime "rxAudioLevelerAppliedGainDb"
            $rxAudioLevelerGainDeltaDb = Get-JsonValue $runtime "rxAudioLevelerGainDeltaDb"
            $rxAudioLevelerPeakHeadroomDb = Get-JsonValue $runtime "rxAudioLevelerPeakHeadroomDb"
            $rxAudioLevelerPreLimitPeakDbfs = Get-JsonValue $runtime "rxAudioLevelerPreLimitPeakDbfs"
            $rxAudioLevelerOutputLimitReductionDb = Get-JsonValue $runtime "rxAudioLevelerOutputLimitReductionDb"
            $rxAudioLevelerOutputLimitSampleCount = Get-JsonValue $runtime "rxAudioLevelerOutputLimitSampleCount"
            $rxAudioLevelerPauseHoldBlocks = Get-JsonValue $runtime "rxAudioLevelerPauseHoldBlocks"
            $rxAudioLevelerNr5SpeechHoldBlocks = Get-JsonValue $runtime "rxAudioLevelerNr5SpeechHoldBlocks"
            Add-Number $rxAudioLevelerInputRmsValues $rxAudioLevelerInputRmsDbfs
            Add-Number $rxAudioLevelerOutputRmsValues $rxAudioLevelerOutputRmsDbfs
            Add-Number $rxAudioLevelerInputPeakValues $rxAudioLevelerInputPeakDbfs
            Add-Number $rxAudioLevelerOutputPeakValues $rxAudioLevelerOutputPeakDbfs
            Add-Number $rxAudioLevelerDesiredGainValues $rxAudioLevelerDesiredGainDb
            Add-Number $rxAudioLevelerAppliedGainValues $rxAudioLevelerAppliedGainDb
            Add-Number $rxAudioLevelerGainDeltaValues $rxAudioLevelerGainDeltaDb
            Add-Number $rxAudioLevelerPeakHeadroomValues $rxAudioLevelerPeakHeadroomDb
            Add-Number $rxAudioLevelerPreLimitPeakValues $rxAudioLevelerPreLimitPeakDbfs
            Add-Number $rxAudioLevelerOutputLimitReductionValues $rxAudioLevelerOutputLimitReductionDb
            Add-Number $rxAudioLevelerOutputLimitSampleCountValues $rxAudioLevelerOutputLimitSampleCount
            Add-Number $rxAudioLevelerPauseHoldBlockValues $rxAudioLevelerPauseHoldBlocks
            Add-Number $rxAudioLevelerNr5SpeechHoldBlockValues $rxAudioLevelerNr5SpeechHoldBlocks
            if ($null -ne $rxAudioLevelerInputRmsDbfs -and $null -ne $rxAudioLevelerOutputRmsDbfs -and
                $null -ne $rxAudioLevelerDesiredGainDb -and $null -ne $rxAudioLevelerAppliedGainDb) {
                $rxAudioLevelerDiagnosticCount++
            }
            $rxAudioLevelerBoostSlewLimited = Test-Truthy (Get-JsonValue $runtime "rxAudioLevelerBoostSlewLimited")
            $rxAudioLevelerPeakLimited = Test-Truthy (Get-JsonValue $runtime "rxAudioLevelerPeakLimited")
            $rxAudioLevelerOutputLimited = Test-Truthy (Get-JsonValue $runtime "rxAudioLevelerOutputLimited")
            $rxAudioLevelerConstrained = ($rxAudioLevelerBoostSlewLimited -or $rxAudioLevelerPeakLimited -or $rxAudioLevelerOutputLimited)
            $rxAudioLevelerConstrainedSample = $null
            if ($rxAudioLevelerConstrained) {
                Add-Number $levelerConstrainedAgcValues $runtimeAgcGainDbNumber
                $rxAudioLevelerConstrainedSample = [ordered]@{
                    sampleIndex = Get-JsonValue $sample "sampleIndex"
                    sampledUtc = Get-JsonValue $sample "sampledUtc"
                    audioRmsDbfs = Get-NumericValue (Get-JsonValue $runtime "audioRmsDbfs")
                    audioPeakDbfs = Get-NumericValue (Get-JsonValue $runtime "audioPeakDbfs")
                    nr5InputDbfs = Get-NumericValue (Get-JsonValue $nr5 "inputDbfs")
                    nr5OutputDbfs = Get-NumericValue (Get-JsonValue $nr5 "outputDbfs")
                    inputRmsDbfs = Get-NumericValue $rxAudioLevelerInputRmsDbfs
                    outputRmsDbfs = Get-NumericValue $rxAudioLevelerOutputRmsDbfs
                    inputPeakDbfs = Get-NumericValue $rxAudioLevelerInputPeakDbfs
                    outputPeakDbfs = Get-NumericValue $rxAudioLevelerOutputPeakDbfs
                    desiredGainDb = Get-NumericValue $rxAudioLevelerDesiredGainDb
                    appliedGainDb = Get-NumericValue $rxAudioLevelerAppliedGainDb
                    gainDeltaDb = Get-NumericValue $rxAudioLevelerGainDeltaDb
                    peakHeadroomDb = Get-NumericValue $rxAudioLevelerPeakHeadroomDb
                    preLimitPeakDbfs = Get-NumericValue $rxAudioLevelerPreLimitPeakDbfs
                    outputLimitReductionDb = Get-NumericValue $rxAudioLevelerOutputLimitReductionDb
                    outputLimitSampleCount = Get-NumericValue $rxAudioLevelerOutputLimitSampleCount
                    pauseHoldBlocks = Get-NumericValue $rxAudioLevelerPauseHoldBlocks
                    nr5SpeechHoldBlocks = Get-NumericValue $rxAudioLevelerNr5SpeechHoldBlocks
                    boostSlewLimited = $rxAudioLevelerBoostSlewLimited
                    peakLimited = $rxAudioLevelerPeakLimited
                    outputLimited = $rxAudioLevelerOutputLimited
                }
                $rxAudioLevelerConstrainedSamples.Add($rxAudioLevelerConstrainedSample) | Out-Null
            }
            if ($rxAudioLevelerBoostSlewLimited) {
                $rxAudioLevelerBoostSlewLimitedCount++
                $rxAudioLevelerBoostSlewLimitedSamples.Add($rxAudioLevelerConstrainedSample) | Out-Null
            }
            if ($rxAudioLevelerPeakLimited) {
                $rxAudioLevelerPeakLimitedCount++
                $rxAudioLevelerPeakLimitedSamples.Add($rxAudioLevelerConstrainedSample) | Out-Null
            }
            if ($rxAudioLevelerOutputLimited) {
                $rxAudioLevelerOutputLimitedCount++
                $rxAudioLevelerOutputLimitedSamples.Add($rxAudioLevelerConstrainedSample) | Out-Null
            }
            Add-Number $backlogValues (Get-JsonValue $runtime "monitorBacklogSamples")
            Add-Number $rxMetersAgeValues (Get-JsonValue $runtime "rxMetersAgeMs")
            Add-Number $audioAgeValues (Get-JsonValue $runtime "audioAgeMs")

            if (Test-Truthy (Get-JsonValue $runtime "rxMetersFresh")) {
                $rxMetersFreshCount++
            }
            if (Test-Truthy (Get-JsonValue $runtime "audioFresh")) {
                $audioFreshCount++
            }
            if (Test-Truthy (Get-JsonValue $runtime "txMonitorRequested")) {
                $txMonitorCount++
            }
            if (Test-Truthy (Get-JsonValue $runtime "squelchEnabled")) {
                $squelchEnabledCount++
                if (-not (Test-Truthy (Get-JsonValue $runtime "squelchOpen"))) {
                    $squelchClosedCount++
                }
            }
            if (Test-Truthy (Get-JsonValue $runtime "squelchTailActive")) {
                $squelchTailCount++
            }
        }

        $sampleSummaries.Add((New-SampleSummary $sample)) | Out-Null
    }

    $agcStats = Get-NumberStats $agcValues
    $activeAgcStats = Get-NumberStats $activeAgcValues
    $voiceLikeAgcStats = Get-NumberStats $voiceLikeAgcValues
    $quietNoEvidenceAgcStats = Get-NumberStats $quietNoEvidenceAgcValues
    $levelerConstrainedAgcStats = Get-NumberStats $levelerConstrainedAgcValues
    $headroomStats = Get-NumberStats $headroomValues
    $rmsStats = Get-NumberStats $rmsValues
    $peakStats = Get-NumberStats $peakValues
    $floorAudioRmsStats = Get-NumberStats $floorAudioRmsValues
    $activeAudioRmsStats = Get-NumberStats $activeAudioRmsValues
    $voiceLikeAudioRmsStats = Get-NumberStats $voiceLikeAudioRmsValues
    $quietNoEvidenceAudioRmsStats = Get-NumberStats $quietNoEvidenceAudioRmsValues
    $passbandAudioRmsStats = Get-NumberStats $passbandAudioRmsValues
    $passbandActiveAudioRmsStats = Get-NumberStats $passbandActiveAudioRmsValues
    $passbandFloorAudioRmsStats = Get-NumberStats $passbandFloorAudioRmsValues
    $offPassbandAudioRmsStats = Get-NumberStats $offPassbandAudioRmsValues
    $rxAudioLevelerInputRmsStats = Get-NumberStats $rxAudioLevelerInputRmsValues
    $rxAudioLevelerOutputRmsStats = Get-NumberStats $rxAudioLevelerOutputRmsValues
    $rxAudioLevelerInputPeakStats = Get-NumberStats $rxAudioLevelerInputPeakValues
    $rxAudioLevelerOutputPeakStats = Get-NumberStats $rxAudioLevelerOutputPeakValues
    $rxAudioLevelerDesiredGainStats = Get-NumberStats $rxAudioLevelerDesiredGainValues
    $rxAudioLevelerAppliedGainStats = Get-NumberStats $rxAudioLevelerAppliedGainValues
    $rxAudioLevelerGainDeltaStats = Get-NumberStats $rxAudioLevelerGainDeltaValues
    $rxAudioLevelerPeakHeadroomStats = Get-NumberStats $rxAudioLevelerPeakHeadroomValues
    $rxAudioLevelerPreLimitPeakStats = Get-NumberStats $rxAudioLevelerPreLimitPeakValues
    $rxAudioLevelerOutputLimitReductionStats = Get-NumberStats $rxAudioLevelerOutputLimitReductionValues
    $rxAudioLevelerOutputLimitSampleCountStats = Get-NumberStats $rxAudioLevelerOutputLimitSampleCountValues
    $rxAudioLevelerPauseHoldBlockStats = Get-NumberStats $rxAudioLevelerPauseHoldBlockValues
    $rxAudioLevelerNr5SpeechHoldBlockStats = Get-NumberStats $rxAudioLevelerNr5SpeechHoldBlockValues
    $backlogStats = Get-NumberStats $backlogValues
    $frontendSceneAgeStats = Get-NumberStats $frontendSceneAgeValues
    $frontendTopPeakCountStats = Get-NumberStats $frontendTopPeakCountValues
    $frontendNearestTopPeakOffsetStats = Get-NumberStats $frontendNearestTopPeakOffsetValues
    $frontendNearestTopPeakAbsOffsetStats = Get-NumberStats $frontendNearestTopPeakAbsOffsetValues
    $frontendStrongestTopPeakSnrStats = Get-NumberStats $frontendStrongestTopPeakSnrValues
    $frontendNearestFilterPassbandDistanceStats = Get-NumberStats $frontendNearestFilterPassbandDistanceValues
    $frontendAdjacentNoiseBinStats = Get-NumberStats $frontendAdjacentNoiseBinValues
    $frontendAdjacentNoiseFloorStats = Get-NumberStats $frontendAdjacentNoiseFloorValues
    $frontendAdjacentNoiseP50Stats = Get-NumberStats $frontendAdjacentNoiseP50Values
    $frontendAdjacentNoiseSlopeStats = Get-NumberStats $frontendAdjacentNoiseSlopeValues
    $frontendAdjacentNoiseRejectedStats = Get-NumberStats $frontendAdjacentNoiseRejectedValues
    $rxMetersAgeStats = Get-NumberStats $rxMetersAgeValues
    $audioAgeStats = Get-NumberStats $audioAgeValues
    $nr5InputStats = Get-NumberStats $nr5InputValues
    $nr5OutputStats = Get-NumberStats $nr5OutputValues
    $nr5InputOutputStats = Get-PairStats $nr5InputOutputXValues $nr5InputOutputYValues
    $nr5OutputMinusInputStats = Get-NumberStats $nr5OutputMinusInputValues
    $nr5WeakOutputStats = Get-NumberStats $nr5WeakOutputValues
    $nr5StrongOutputStats = Get-NumberStats $nr5StrongOutputValues
    $nr5WeakFinalAudioStats = Get-NumberStats $nr5WeakFinalAudioValues
    $nr5StrongFinalAudioStats = Get-NumberStats $nr5StrongFinalAudioValues
    $nr5NearStrongOutputStats = Get-NumberStats $nr5NearStrongOutputValues
    $nr5NearStrongFinalAudioStats = Get-NumberStats $nr5NearStrongFinalAudioValues
    $nr5SpeechQualifiedWeakOutputStats = Get-NumberStats $nr5SpeechQualifiedWeakOutputValues
    $nr5SpeechQualifiedStrongOutputStats = Get-NumberStats $nr5SpeechQualifiedStrongOutputValues
    $nr5SpeechQualifiedWeakFinalAudioStats = Get-NumberStats $nr5SpeechQualifiedWeakFinalAudioValues
    $nr5SpeechQualifiedStrongFinalAudioStats = Get-NumberStats $nr5SpeechQualifiedStrongFinalAudioValues
    $nr5SpeechQualifiedNearStrongOutputStats = Get-NumberStats $nr5SpeechQualifiedNearStrongOutputValues
    $nr5SpeechQualifiedNearStrongFinalAudioStats = Get-NumberStats $nr5SpeechQualifiedNearStrongFinalAudioValues
    $nr5PassbandQualifiedWeakOutputStats = Get-NumberStats $nr5PassbandQualifiedWeakOutputValues
    $nr5PassbandQualifiedStrongOutputStats = Get-NumberStats $nr5PassbandQualifiedStrongOutputValues
    $nr5PassbandQualifiedWeakFinalAudioStats = Get-NumberStats $nr5PassbandQualifiedWeakFinalAudioValues
    $nr5PassbandQualifiedStrongFinalAudioStats = Get-NumberStats $nr5PassbandQualifiedStrongFinalAudioValues
    $nr5PassbandQualifiedNearStrongOutputStats = Get-NumberStats $nr5PassbandQualifiedNearStrongOutputValues
    $nr5PassbandQualifiedNearStrongFinalAudioStats = Get-NumberStats $nr5PassbandQualifiedNearStrongFinalAudioValues
    $nr5MeanGainStats = Get-NumberStats $nr5MeanGainValues
    $nr5FloorReductionStats = Get-NumberStats $nr5FloorReductionValues
    $nr5DynamicRangeStats = Get-NumberStats $nr5DynamicRangeValues
    $nr5SignalConfidenceStats = Get-NumberStats $nr5SignalConfidenceValues
    $nr5AgcGateStats = Get-NumberStats $nr5AgcGateValues
    $nr5SignalProbabilityStats = Get-NumberStats $nr5SignalProbabilityValues
    $nr5TextureFillStats = Get-NumberStats $nr5TextureFillValues
    $nr5MaskSmoothingStats = Get-NumberStats $nr5MaskSmoothingValues
    $nr5LevelDriveStats = Get-NumberStats $nr5LevelDriveValues
    $nr5RecoveryDriveStats = Get-NumberStats $nr5RecoveryDriveValues
    $nr5WeakSignalMemoryStats = Get-NumberStats $nr5WeakSignalMemoryValues
    $nr5MakeupGainDbStats = Get-NumberStats $nr5MakeupGainDbValues
    $nr5OutputPeakDbfsStats = Get-NumberStats $nr5OutputPeakDbfsValues
    $nr5PeakEvidenceStats = Get-NumberStats $nr5PeakEvidenceValues
    $nr5PeakLimitDbfsStats = Get-NumberStats $nr5PeakLimitDbfsValues
    $nr5PeakReductionDbStats = Get-NumberStats $nr5PeakReductionDbValues
    $nr5AdjacentNoiseTrustStats = Get-NumberStats $nr5AdjacentNoiseTrustValues
    $nr5AdjacentNoiseDriveStats = Get-NumberStats $nr5AdjacentNoiseDriveValues
    $nr5AdjacentNoiseFloorStats = Get-NumberStats $nr5AdjacentNoiseFloorValues
    $nr5AdjacentNoiseRejectedStats = Get-NumberStats $nr5AdjacentNoiseRejectedValues
    $nr5AdjacentNoiseSideBalanceStats = Get-NumberStats $nr5AdjacentNoiseSideBalanceValues
    $nr5AdjacentNoiseAsymmetryStats = Get-NumberStats $nr5AdjacentNoiseAsymmetryValues
    $nr5AudioInputDeltaStats = Get-NumberStats $nr5AudioInputDeltaValues
    $nr5AudioOutputDeltaStats = Get-NumberStats $nr5AudioOutputDeltaValues
    $nr5LearnedFrameStats = Get-NumberStats $nr5LearnedFrameValues
    $nr5ManagedChannelGenerationStats = Get-NumberStats $nr5ManagedChannelGenerationValues
    $nr5ManagedApplyCountStats = Get-NumberStats $nr5ManagedApplyCountValues
    $nr5ManagedPositionApplyCountStats = Get-NumberStats $nr5ManagedPositionApplyCountValues
    $nr5ManagedPolicyApplyCountStats = Get-NumberStats $nr5ManagedPolicyApplyCountValues
    $nr5ManagedNoopApplyCountStats = Get-NumberStats $nr5ManagedNoopApplyCountValues
    $nr5ManagedRunApplyCountStats = Get-NumberStats $nr5ManagedRunApplyCountValues
    $nr5LearnerResetSampleCount = @($nr5LearnerResetSamples.ToArray()).Count
    $nr5ManagedReapplySampleCount = @($nr5ManagedReapplySamples.ToArray()).Count
    $nr5ManagedCounterCoveragePct = Get-CountPercent -Count $nr5ManagedCounterSampleCount -SampleCount $nr5SampleCount
    $nr5LearnedFrameCoveragePct = Get-CountPercent -Count $nr5LearnedFrameSampleCount -SampleCount $nr5SampleCount
    $nr5ManagedCountersComplete = ($nr5SampleCount -gt 0 -and $nr5ManagedCounterSampleCount -eq $nr5SampleCount)
    $nr5LearnedFramesComplete = ($nr5SampleCount -gt 0 -and $nr5LearnedFrameSampleCount -eq $nr5SampleCount)
    $nr5LearnerMonotonic = ($nr5LearnerResetSampleCount -eq 0)
    $nr5LearnerStabilityStatus = if ($nr5SampleCount -eq 0) {
        "not-observed"
    }
    elseif (-not $nr5LearnedFramesComplete) {
        "learned-frames-missing"
    }
    elseif ($nr5LearnerResetSampleCount -gt 0) {
        "learner-reset-watch"
    }
    elseif ($nr5ManagedReapplySampleCount -gt 0) {
        "managed-reapply-watch"
    }
    elseif ($nr5ManagedGenerationChangeCount -gt 0) {
        "channel-generation-changed"
    }
    elseif ($nr5ManagedCounterSampleCount -eq 0) {
        "managed-counters-missing"
    }
    elseif (-not $nr5ManagedCountersComplete) {
        "managed-counters-partial"
    }
    elseif ([int]$nr5LearnedFrameStats["count"] -gt 0 -and [double]$nr5LearnedFrameStats["max"] -lt 20.0) {
        "warming"
    }
    else {
        "stable"
    }
    $nr5ManagedReplayEvidenceReady = (
        [string]::Equals($nr5LearnerStabilityStatus, "stable", [StringComparison]::OrdinalIgnoreCase) -and
        $nr5ManagedCountersComplete -and
        $nr5LearnedFramesComplete)
    $nr5WeakInputTopSamples = @($nr5WeakInputSamples.ToArray() |
        Sort-Object @{Expression = { $v = Get-NumericValue (Get-JsonValue $_ "finalAudioRmsDbfs"); if ($null -eq $v) { [double]::PositiveInfinity } else { [double]$v } }; Ascending = $true },
            @{Expression = { $v = Get-NumericValue (Get-JsonValue $_ "outputDbfs"); if ($null -eq $v) { [double]::PositiveInfinity } else { [double]$v } }; Ascending = $true } |
        Select-Object -First 8)
    $nr5StrongInputTopSamples = @($nr5StrongInputSamples.ToArray() |
        Sort-Object @{Expression = { $v = Get-NumericValue (Get-JsonValue $_ "finalAudioRmsDbfs"); if ($null -eq $v) { [double]::NegativeInfinity } else { [double]$v } }; Descending = $true },
            @{Expression = { $v = Get-NumericValue (Get-JsonValue $_ "outputDbfs"); if ($null -eq $v) { [double]::NegativeInfinity } else { [double]$v } }; Descending = $true } |
        Select-Object -First 8)
    $nr5SpeechQualifiedWeakInputTopSamples = @($nr5SpeechQualifiedWeakInputSamples.ToArray() |
        Sort-Object @{Expression = { $v = Get-NumericValue (Get-JsonValue $_ "finalAudioRmsDbfs"); if ($null -eq $v) { [double]::PositiveInfinity } else { [double]$v } }; Ascending = $true },
            @{Expression = { $v = Get-NumericValue (Get-JsonValue $_ "outputDbfs"); if ($null -eq $v) { [double]::PositiveInfinity } else { [double]$v } }; Ascending = $true } |
        Select-Object -First 8)
    $nr5SpeechQualifiedStrongInputTopSamples = @($nr5SpeechQualifiedStrongInputSamples.ToArray() |
        Sort-Object @{Expression = { $v = Get-NumericValue (Get-JsonValue $_ "finalAudioRmsDbfs"); if ($null -eq $v) { [double]::NegativeInfinity } else { [double]$v } }; Descending = $true },
            @{Expression = { $v = Get-NumericValue (Get-JsonValue $_ "outputDbfs"); if ($null -eq $v) { [double]::NegativeInfinity } else { [double]$v } }; Descending = $true } |
        Select-Object -First 8)
    $nr5PassbandQualifiedWeakInputTopSamples = @($nr5PassbandQualifiedWeakInputSamples.ToArray() |
        Sort-Object @{Expression = { $v = Get-NumericValue (Get-JsonValue $_ "finalAudioRmsDbfs"); if ($null -eq $v) { [double]::PositiveInfinity } else { [double]$v } }; Ascending = $true },
            @{Expression = { $v = Get-NumericValue (Get-JsonValue $_ "outputDbfs"); if ($null -eq $v) { [double]::PositiveInfinity } else { [double]$v } }; Ascending = $true } |
        Select-Object -First 8)
    $nr5PassbandQualifiedStrongInputTopSamples = @($nr5PassbandQualifiedStrongInputSamples.ToArray() |
        Sort-Object @{Expression = { $v = Get-NumericValue (Get-JsonValue $_ "finalAudioRmsDbfs"); if ($null -eq $v) { [double]::NegativeInfinity } else { [double]$v } }; Descending = $true },
            @{Expression = { $v = Get-NumericValue (Get-JsonValue $_ "outputDbfs"); if ($null -eq $v) { [double]::NegativeInfinity } else { [double]$v } }; Descending = $true } |
        Select-Object -First 8)
    $nr5WeakDropoutTopSamples = @($nr5WeakDropoutSamples.ToArray() | Sort-Object outputDbfs | Select-Object -First 8)
    $nr5WeakDropoutFinalAudibleTopSamples = @($nr5WeakDropoutFinalAudibleSamples.ToArray() |
        Sort-Object @{Expression = "audioRmsDbfs"; Descending = $true }, @{Expression = "outputDbfs"; Descending = $true } |
        Select-Object -First 8)
    $nr5WeakDropoutCandidateLossTopSamples = @($nr5WeakDropoutCandidateLossSamples.ToArray() |
        Sort-Object outputDbfs |
        Select-Object -First 8)
    $nr5HotMakeupTopSamples = @($nr5HotMakeupSamples.ToArray() | Sort-Object makeupGainDb -Descending | Select-Object -First 8)
    $nr5NearStrongInputTopSamples = @($nr5NearStrongInputSamples.ToArray() |
        Sort-Object @{Expression = "distanceToStrongThresholdDb"; Descending = $false }, @{Expression = "inputDbfs"; Descending = $true } |
        Select-Object -First 8)
    $nr5AudioAlignmentMismatchTopSamples = @($nr5AudioAlignmentMismatchSamples.ToArray() |
        Sort-Object @{Expression = { [Math]::Abs([double]$_.deltaDb) }; Descending = $true } |
        Select-Object -First 8)
    $nr5AudioLevelerNormalizedTopSamples = @($nr5AudioLevelerNormalizedSamples.ToArray() |
        Sort-Object @{Expression = { [Math]::Abs([double]$_.outputDeltaDb) }; Descending = $true } |
        Select-Object -First 8)
    $nr5LearnerResetTopSamples = @($nr5LearnerResetSamples.ToArray() |
        Sort-Object sampleIndex |
        Select-Object -First 8)
    $nr5ManagedReapplyTopSamples = @($nr5ManagedReapplySamples.ToArray() |
        Sort-Object sampleIndex |
        Select-Object -First 8)
    $nr5ManagedGenerationChangeTopSamples = @($nr5ManagedGenerationChangeSamples.ToArray() |
        Sort-Object sampleIndex |
        Select-Object -First 8)
    $nr5LowEvidenceLiftTopSamples = @($nr5LowEvidenceLiftSamples.ToArray() |
        Sort-Object @{Expression = "audioRmsDbfs"; Descending = $true }, @{Expression = "outputDbfs"; Descending = $true } |
        Select-Object -First 8)
    $nr5LowEvidenceSuppressedTopSamples = @($nr5LowEvidenceSuppressedSamples.ToArray() |
        Sort-Object outputDbfs |
        Select-Object -First 8)
    $rxAudioLevelerBoostSlewLimitedTopSamples = @($rxAudioLevelerBoostSlewLimitedSamples.ToArray() |
        Sort-Object @{Expression = "gainDeltaDb"; Descending = $true }, @{Expression = "outputRmsDbfs"; Descending = $true } |
        Select-Object -First 8)
    $rxAudioLevelerPeakLimitedTopSamples = @($rxAudioLevelerPeakLimitedSamples.ToArray() |
        Sort-Object peakHeadroomDb, @{Expression = "outputPeakDbfs"; Descending = $true } |
        Select-Object -First 8)
    $rxAudioLevelerOutputLimitedTopSamples = @($rxAudioLevelerOutputLimitedSamples.ToArray() |
        Sort-Object @{Expression = "outputLimitReductionDb"; Descending = $true }, @{Expression = "outputLimitSampleCount"; Descending = $true } |
        Select-Object -First 8)
    $passbandAudioTopSamples = @($passbandAudioSamples.ToArray() |
        Sort-Object @{Expression = "finalAudioRmsDbfs"; Descending = $true } |
        Select-Object -First 8)
    $offPassbandAudioTopSamples = @($offPassbandAudioSamples.ToArray() |
        Sort-Object @{Expression = "finalAudioRmsDbfs"; Descending = $true } |
        Select-Object -First 8)
    $frontendTopPeakTopSamples = @($frontendTopPeakSamples.ToArray() |
        Sort-Object @{Expression = { [double](Get-JsonValue (Get-JsonValue $_ "strongest") "snrDb") }; Descending = $true } |
        Select-Object -First 8)
    $frontendNearPassbandPeakTopSamples = @($frontendNearPassbandPeakSamples.ToArray() |
        Sort-Object @{Expression = { [Math]::Abs([double](Get-JsonValue (Get-JsonValue $_ "nearest") "offsetHz")) }; Ascending = $true } |
        Select-Object -First 8)
    $frontendFilterPassbandPeakTopSamples = @($frontendFilterPassbandPeakSamples.ToArray() |
        Sort-Object @{Expression = { [double](Get-JsonValue $_ "nearestFilterPassbandDistanceHz") }; Ascending = $true },
            @{Expression = { [double](Get-JsonValue (Get-JsonValue $_ "strongest") "snrDb") }; Descending = $true } |
        Select-Object -First 8)
    $frontendTuneCandidates = @(Get-FrontendTuneCandidates `
        -Samples @($frontendTopPeakSamples.ToArray()) `
        -MaxCount 6 `
        -TuneStepHz $TuneStepHz)
    $nr5NormalizationCompressionDb = $null
    $nr5WeakStrongOutputGapDb = $null
    $nr5WeakStrongFinalAudioGapDb = $null
    $nr5SpeechQualifiedWeakStrongOutputGapDb = $null
    $nr5SpeechQualifiedWeakStrongFinalAudioGapDb = $null
    $nr5PassbandQualifiedWeakStrongOutputGapDb = $null
    $nr5PassbandQualifiedWeakStrongFinalAudioGapDb = $null
    $nr5MixedWeakStrongGapThresholdDb = 6.0
    $nr5MixedWeakStrongFinalAudioGapThresholdDb = 4.0
    $nr5MinimumMixedWeakInputSampleCount = 3
    $nr5MinimumMixedStrongInputSampleCount = 3
    $nr5MinimumMixedEvidenceQualifiedWeakInputSampleCount = 3
    $nr5MinimumQualifiedWeakStrongInputSampleCount = 2
    $nr5SpeechQualifiedWeakStrongEvidenceReady = $false
    $nr5SpeechQualifiedWeakStrongOutputParityReady = $false
    $nr5SpeechQualifiedWeakStrongFinalAudioParityReady = $false
    $nr5SpeechQualifiedWeakStrongEvidenceStatus = "not-evaluated"
    $nr5PassbandQualifiedWeakStrongEvidenceReady = $false
    $nr5PassbandQualifiedWeakStrongOutputParityReady = $false
    $nr5PassbandQualifiedWeakStrongFinalAudioParityReady = $false
    $nr5PassbandQualifiedWeakStrongEvidenceStatus = "not-evaluated"
    $nr5MixedWeakStrongEvidenceReady = $false
    $nr5WeakStrongOutputParityReady = $false
    $nr5WeakStrongFinalAudioParityReady = $false
    $nr5MixedWeakStrongEvidenceStatus = "not-evaluated"
    $nr5LowEvidenceWeakInputCount = $nr5LowEvidenceSampleCount
    $nr5EvidenceQualifiedWeakInputCount = [Math]::Max(0, $nr5WeakInputCount - $nr5LowEvidenceWeakInputCount)
    $nr5LowEvidenceLiftPct = $null
    $nr5LowEvidenceSuppressedPct = $null
    $nr5AudioAlignmentMismatchPct = $null
    $nr5AudioAlignedAfterLevelerPct = $null
    $nr5AudioLevelerNormalizedPct = $null
    $nr5LowEvidenceAlignmentMismatchPct = $null
    $nr5WeakDropoutFinalAudiblePct = Get-CountPercent -Count $nr5WeakDropoutFinalAudibleCount -SampleCount $nr5WeakDropoutCount
    $nr5WeakDropoutNativeLiftedPct = Get-CountPercent -Count $nr5WeakDropoutNativeLiftedCount -SampleCount $nr5WeakDropoutCount
    $nr5WeakDropoutCandidateLossPct = Get-CountPercent -Count $nr5WeakDropoutCandidateLossCount -SampleCount $nr5WeakDropoutCount
    if ($null -ne $nr5InputStats["movement"] -and $null -ne $nr5OutputStats["movement"]) {
        $nr5NormalizationCompressionDb = [Math]::Round([double]$nr5InputStats["movement"] - [double]$nr5OutputStats["movement"], 3)
    }
    if ($null -ne $nr5WeakOutputStats["average"] -and $null -ne $nr5StrongOutputStats["average"]) {
        $nr5WeakStrongOutputGapDb = [Math]::Round([double]$nr5StrongOutputStats["average"] - [double]$nr5WeakOutputStats["average"], 3)
    }
    if ($null -ne $nr5WeakFinalAudioStats["average"] -and $null -ne $nr5StrongFinalAudioStats["average"]) {
        $nr5WeakStrongFinalAudioGapDb = [Math]::Round([double]$nr5StrongFinalAudioStats["average"] - [double]$nr5WeakFinalAudioStats["average"], 3)
    }
    if ($null -ne $nr5SpeechQualifiedWeakOutputStats["average"] -and $null -ne $nr5SpeechQualifiedStrongOutputStats["average"]) {
        $nr5SpeechQualifiedWeakStrongOutputGapDb = [Math]::Round([double]$nr5SpeechQualifiedStrongOutputStats["average"] - [double]$nr5SpeechQualifiedWeakOutputStats["average"], 3)
    }
    if ($null -ne $nr5SpeechQualifiedWeakFinalAudioStats["average"] -and $null -ne $nr5SpeechQualifiedStrongFinalAudioStats["average"]) {
        $nr5SpeechQualifiedWeakStrongFinalAudioGapDb = [Math]::Round([double]$nr5SpeechQualifiedStrongFinalAudioStats["average"] - [double]$nr5SpeechQualifiedWeakFinalAudioStats["average"], 3)
    }
    if ($null -ne $nr5PassbandQualifiedWeakOutputStats["average"] -and $null -ne $nr5PassbandQualifiedStrongOutputStats["average"]) {
        $nr5PassbandQualifiedWeakStrongOutputGapDb = [Math]::Round([double]$nr5PassbandQualifiedStrongOutputStats["average"] - [double]$nr5PassbandQualifiedWeakOutputStats["average"], 3)
    }
    if ($null -ne $nr5PassbandQualifiedWeakFinalAudioStats["average"] -and $null -ne $nr5PassbandQualifiedStrongFinalAudioStats["average"]) {
        $nr5PassbandQualifiedWeakStrongFinalAudioGapDb = [Math]::Round([double]$nr5PassbandQualifiedStrongFinalAudioStats["average"] - [double]$nr5PassbandQualifiedWeakFinalAudioStats["average"], 3)
    }
    if ($nr5SampleCount -le 0) {
        $nr5SpeechQualifiedWeakStrongEvidenceStatus = "no-nr5-samples"
    }
    elseif ($nr5SpeechQualifiedWeakOutputValues.Count -le 0 -and $nr5SpeechQualifiedStrongOutputValues.Count -le 0) {
        $nr5SpeechQualifiedWeakStrongEvidenceStatus = "missing-weak-and-strong-speech"
    }
    elseif ($nr5SpeechQualifiedWeakOutputValues.Count -le 0) {
        $nr5SpeechQualifiedWeakStrongEvidenceStatus = "missing-weak-speech"
    }
    elseif ($nr5SpeechQualifiedStrongOutputValues.Count -le 0) {
        $nr5SpeechQualifiedWeakStrongEvidenceStatus = "missing-strong-speech"
    }
    elseif ($nr5SpeechQualifiedWeakOutputValues.Count -lt $nr5MinimumQualifiedWeakStrongInputSampleCount -and
        $nr5SpeechQualifiedStrongOutputValues.Count -lt $nr5MinimumQualifiedWeakStrongInputSampleCount) {
        $nr5SpeechQualifiedWeakStrongEvidenceStatus = "insufficient-weak-and-strong-speech-evidence"
    }
    elseif ($nr5SpeechQualifiedWeakOutputValues.Count -lt $nr5MinimumQualifiedWeakStrongInputSampleCount) {
        $nr5SpeechQualifiedWeakStrongEvidenceStatus = "insufficient-weak-speech-evidence"
    }
    elseif ($nr5SpeechQualifiedStrongOutputValues.Count -lt $nr5MinimumQualifiedWeakStrongInputSampleCount) {
        $nr5SpeechQualifiedWeakStrongEvidenceStatus = "insufficient-strong-speech-evidence"
    }
    elseif ($null -ne $nr5SpeechQualifiedWeakStrongFinalAudioGapDb -and
        [Math]::Abs([double]$nr5SpeechQualifiedWeakStrongFinalAudioGapDb) -le $nr5MixedWeakStrongFinalAudioGapThresholdDb) {
        $nr5SpeechQualifiedWeakStrongEvidenceStatus = "ready-final-audio"
    }
    elseif ($null -ne $nr5SpeechQualifiedWeakStrongOutputGapDb -and
        [Math]::Abs([double]$nr5SpeechQualifiedWeakStrongOutputGapDb) -le $nr5MixedWeakStrongGapThresholdDb) {
        $nr5SpeechQualifiedWeakStrongEvidenceStatus = "ready-output"
    }
    else {
        $nr5SpeechQualifiedWeakStrongEvidenceStatus = "weak-strong-speech-gap-watch"
    }
    $nr5SpeechQualifiedWeakStrongSampleCountReady = ($nr5SpeechQualifiedWeakOutputValues.Count -ge $nr5MinimumQualifiedWeakStrongInputSampleCount -and
        $nr5SpeechQualifiedStrongOutputValues.Count -ge $nr5MinimumQualifiedWeakStrongInputSampleCount)
    $nr5SpeechQualifiedWeakStrongEvidenceReady = ($nr5SpeechQualifiedWeakStrongSampleCountReady -and (
        [string]::Equals($nr5SpeechQualifiedWeakStrongEvidenceStatus, "ready-final-audio", [StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($nr5SpeechQualifiedWeakStrongEvidenceStatus, "ready-output", [StringComparison]::OrdinalIgnoreCase)))
    $nr5SpeechQualifiedWeakStrongOutputParityReady = ($nr5SpeechQualifiedWeakStrongSampleCountReady -and
        $null -ne $nr5SpeechQualifiedWeakStrongOutputGapDb -and
        [Math]::Abs([double]$nr5SpeechQualifiedWeakStrongOutputGapDb) -le $nr5MixedWeakStrongGapThresholdDb)
    $nr5SpeechQualifiedWeakStrongFinalAudioParityReady = ($nr5SpeechQualifiedWeakStrongSampleCountReady -and
        $null -ne $nr5SpeechQualifiedWeakStrongFinalAudioGapDb -and
        [Math]::Abs([double]$nr5SpeechQualifiedWeakStrongFinalAudioGapDb) -le $nr5MixedWeakStrongFinalAudioGapThresholdDb)
    if ($nr5SampleCount -le 0) {
        $nr5PassbandQualifiedWeakStrongEvidenceStatus = "no-nr5-samples"
    }
    elseif ($nr5PassbandQualifiedWeakOutputValues.Count -le 0 -and $nr5PassbandQualifiedStrongOutputValues.Count -le 0) {
        $nr5PassbandQualifiedWeakStrongEvidenceStatus = "missing-weak-and-strong-passband-speech"
    }
    elseif ($nr5PassbandQualifiedWeakOutputValues.Count -le 0) {
        $nr5PassbandQualifiedWeakStrongEvidenceStatus = "missing-weak-passband-speech"
    }
    elseif ($nr5PassbandQualifiedStrongOutputValues.Count -le 0) {
        $nr5PassbandQualifiedWeakStrongEvidenceStatus = "missing-strong-passband-speech"
    }
    elseif ($nr5PassbandQualifiedWeakOutputValues.Count -lt $nr5MinimumQualifiedWeakStrongInputSampleCount -and
        $nr5PassbandQualifiedStrongOutputValues.Count -lt $nr5MinimumQualifiedWeakStrongInputSampleCount) {
        $nr5PassbandQualifiedWeakStrongEvidenceStatus = "insufficient-weak-and-strong-passband-speech-evidence"
    }
    elseif ($nr5PassbandQualifiedWeakOutputValues.Count -lt $nr5MinimumQualifiedWeakStrongInputSampleCount) {
        $nr5PassbandQualifiedWeakStrongEvidenceStatus = "insufficient-weak-passband-speech-evidence"
    }
    elseif ($nr5PassbandQualifiedStrongOutputValues.Count -lt $nr5MinimumQualifiedWeakStrongInputSampleCount) {
        $nr5PassbandQualifiedWeakStrongEvidenceStatus = "insufficient-strong-passband-speech-evidence"
    }
    elseif ($null -ne $nr5PassbandQualifiedWeakStrongFinalAudioGapDb -and
        [Math]::Abs([double]$nr5PassbandQualifiedWeakStrongFinalAudioGapDb) -le $nr5MixedWeakStrongFinalAudioGapThresholdDb) {
        $nr5PassbandQualifiedWeakStrongEvidenceStatus = "ready-final-audio"
    }
    elseif ($null -ne $nr5PassbandQualifiedWeakStrongOutputGapDb -and
        [Math]::Abs([double]$nr5PassbandQualifiedWeakStrongOutputGapDb) -le $nr5MixedWeakStrongGapThresholdDb) {
        $nr5PassbandQualifiedWeakStrongEvidenceStatus = "ready-output"
    }
    else {
        $nr5PassbandQualifiedWeakStrongEvidenceStatus = "weak-strong-passband-speech-gap-watch"
    }
    $nr5PassbandQualifiedWeakStrongSampleCountReady = ($nr5PassbandQualifiedWeakOutputValues.Count -ge $nr5MinimumQualifiedWeakStrongInputSampleCount -and
        $nr5PassbandQualifiedStrongOutputValues.Count -ge $nr5MinimumQualifiedWeakStrongInputSampleCount)
    $nr5PassbandQualifiedWeakStrongEvidenceReady = ($nr5PassbandQualifiedWeakStrongSampleCountReady -and (
        [string]::Equals($nr5PassbandQualifiedWeakStrongEvidenceStatus, "ready-final-audio", [StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($nr5PassbandQualifiedWeakStrongEvidenceStatus, "ready-output", [StringComparison]::OrdinalIgnoreCase)))
    $nr5PassbandQualifiedWeakStrongOutputParityReady = ($nr5PassbandQualifiedWeakStrongSampleCountReady -and
        $null -ne $nr5PassbandQualifiedWeakStrongOutputGapDb -and
        [Math]::Abs([double]$nr5PassbandQualifiedWeakStrongOutputGapDb) -le $nr5MixedWeakStrongGapThresholdDb)
    $nr5PassbandQualifiedWeakStrongFinalAudioParityReady = ($nr5PassbandQualifiedWeakStrongSampleCountReady -and
        $null -ne $nr5PassbandQualifiedWeakStrongFinalAudioGapDb -and
        [Math]::Abs([double]$nr5PassbandQualifiedWeakStrongFinalAudioGapDb) -le $nr5MixedWeakStrongFinalAudioGapThresholdDb)
    if ($nr5SampleCount -le 0) {
        $nr5MixedWeakStrongEvidenceStatus = "no-nr5-samples"
    }
    elseif ($nr5WeakInputCount -le 0 -and $nr5StrongInputCount -le 0) {
        $nr5MixedWeakStrongEvidenceStatus = "missing-weak-and-strong-input"
    }
    elseif ($nr5WeakInputCount -le 0) {
        $nr5MixedWeakStrongEvidenceStatus = "missing-weak-input"
    }
    elseif ($nr5StrongInputCount -le 0) {
        $nr5MixedWeakStrongEvidenceStatus = "missing-strong-input"
    }
    elseif ($nr5EvidenceQualifiedWeakInputCount -le 0) {
        $nr5MixedWeakStrongEvidenceStatus = "low-evidence-weak-input"
    }
    elseif ($nr5WeakInputCount -lt $nr5MinimumMixedWeakInputSampleCount -or
        $nr5EvidenceQualifiedWeakInputCount -lt $nr5MinimumMixedEvidenceQualifiedWeakInputSampleCount) {
        $nr5MixedWeakStrongEvidenceStatus = "insufficient-weak-input-evidence"
    }
    elseif ($nr5StrongInputCount -lt $nr5MinimumMixedStrongInputSampleCount) {
        $nr5MixedWeakStrongEvidenceStatus = "insufficient-strong-input-evidence"
    }
    elseif ($null -eq $nr5WeakStrongOutputGapDb -and $null -eq $nr5WeakStrongFinalAudioGapDb) {
        $nr5MixedWeakStrongEvidenceStatus = "missing-output-gap"
    }
    elseif ($nr5SpeechQualifiedWeakStrongFinalAudioParityReady -or
        $nr5PassbandQualifiedWeakStrongFinalAudioParityReady) {
        $nr5MixedWeakStrongEvidenceStatus = "ready-final-audio"
    }
    elseif ($null -ne $nr5WeakStrongFinalAudioGapDb -and
        [Math]::Abs([double]$nr5WeakStrongFinalAudioGapDb) -le $nr5MixedWeakStrongFinalAudioGapThresholdDb) {
        $nr5MixedWeakStrongEvidenceStatus = "ready-final-audio"
    }
    elseif ($null -ne $nr5WeakStrongOutputGapDb -and
        [Math]::Abs([double]$nr5WeakStrongOutputGapDb) -gt $nr5MixedWeakStrongGapThresholdDb) {
        $nr5MixedWeakStrongEvidenceStatus = "weak-strong-output-gap-watch"
    }
    elseif ($null -ne $nr5WeakStrongFinalAudioGapDb -and
        [Math]::Abs([double]$nr5WeakStrongFinalAudioGapDb) -gt $nr5MixedWeakStrongFinalAudioGapThresholdDb) {
        $nr5MixedWeakStrongEvidenceStatus = "weak-strong-output-gap-watch"
    }
    elseif ($null -eq $nr5WeakStrongOutputGapDb) {
        $nr5MixedWeakStrongEvidenceStatus = "missing-output-gap"
    }
    else {
        $nr5MixedWeakStrongEvidenceStatus = "ready"
    }
    $nr5MixedWeakStrongEvidenceReady =
        [string]::Equals($nr5MixedWeakStrongEvidenceStatus, "ready", [StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($nr5MixedWeakStrongEvidenceStatus, "ready-final-audio", [StringComparison]::OrdinalIgnoreCase)
    $nr5MixedWeakStrongInputEvidenceInsufficient =
        [string]::Equals($nr5MixedWeakStrongEvidenceStatus, "insufficient-weak-input-evidence", [StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($nr5MixedWeakStrongEvidenceStatus, "insufficient-strong-input-evidence", [StringComparison]::OrdinalIgnoreCase)
    $nr5WeakStrongOutputParityReady = ($null -ne $nr5WeakStrongOutputGapDb -and
        [Math]::Abs([double]$nr5WeakStrongOutputGapDb) -le $nr5MixedWeakStrongGapThresholdDb)
    $nr5WeakStrongFinalAudioParityReady = ($null -ne $nr5WeakStrongFinalAudioGapDb -and
        [Math]::Abs([double]$nr5WeakStrongFinalAudioGapDb) -le $nr5MixedWeakStrongFinalAudioGapThresholdDb)
    $nr5MixedWeakStrongOutputGapDirection = Get-GapDirection `
        -GapDb $nr5WeakStrongOutputGapDb `
        -ThresholdDb $nr5MixedWeakStrongGapThresholdDb
    $nr5MixedWeakStrongFinalAudioGapDirection = Get-GapDirection `
        -GapDb $nr5WeakStrongFinalAudioGapDb `
        -ThresholdDb $nr5MixedWeakStrongFinalAudioGapThresholdDb
    $nr5MixedWeakStrongOutputGapExcessDb = Get-GapExcessDb `
        -GapDb $nr5WeakStrongOutputGapDb `
        -ThresholdDb $nr5MixedWeakStrongGapThresholdDb
    $nr5MixedWeakStrongFinalAudioGapExcessDb = Get-GapExcessDb `
        -GapDb $nr5WeakStrongFinalAudioGapDb `
        -ThresholdDb $nr5MixedWeakStrongFinalAudioGapThresholdDb
    $nr5WeakOutputLiftNeededDb = $null
    $nr5WeakOutputTrimNeededDb = $null
    if ([string]::Equals($nr5MixedWeakStrongOutputGapDirection, "weak-too-low", [StringComparison]::OrdinalIgnoreCase)) {
        $nr5WeakOutputLiftNeededDb = $nr5MixedWeakStrongOutputGapExcessDb
        $nr5WeakOutputTrimNeededDb = 0.0
    }
    elseif ([string]::Equals($nr5MixedWeakStrongOutputGapDirection, "weak-too-hot", [StringComparison]::OrdinalIgnoreCase)) {
        $nr5WeakOutputLiftNeededDb = 0.0
        $nr5WeakOutputTrimNeededDb = $nr5MixedWeakStrongOutputGapExcessDb
    }
    elseif (-not [string]::Equals($nr5MixedWeakStrongOutputGapDirection, "unknown", [StringComparison]::OrdinalIgnoreCase)) {
        $nr5WeakOutputLiftNeededDb = 0.0
        $nr5WeakOutputTrimNeededDb = 0.0
    }
    $nr5WeakFinalAudioLiftNeededDb = $null
    $nr5WeakFinalAudioTrimNeededDb = $null
    if ([string]::Equals($nr5MixedWeakStrongFinalAudioGapDirection, "weak-too-low", [StringComparison]::OrdinalIgnoreCase)) {
        $nr5WeakFinalAudioLiftNeededDb = $nr5MixedWeakStrongFinalAudioGapExcessDb
        $nr5WeakFinalAudioTrimNeededDb = 0.0
    }
    elseif ([string]::Equals($nr5MixedWeakStrongFinalAudioGapDirection, "weak-too-hot", [StringComparison]::OrdinalIgnoreCase)) {
        $nr5WeakFinalAudioLiftNeededDb = 0.0
        $nr5WeakFinalAudioTrimNeededDb = $nr5MixedWeakStrongFinalAudioGapExcessDb
    }
    elseif (-not [string]::Equals($nr5MixedWeakStrongFinalAudioGapDirection, "unknown", [StringComparison]::OrdinalIgnoreCase)) {
        $nr5WeakFinalAudioLiftNeededDb = 0.0
        $nr5WeakFinalAudioTrimNeededDb = 0.0
    }
    $nr5MixedWeakStrongPreferredAction = switch ($nr5MixedWeakStrongEvidenceStatus) {
        "weak-strong-output-gap-watch" {
            if ([string]::Equals($nr5MixedWeakStrongFinalAudioGapDirection, "within-parity", [StringComparison]::OrdinalIgnoreCase)) {
                "post-leveler-parity-ready-inspect-native-gap-before-changing-makeup"
            }
            elseif ([string]::Equals($nr5MixedWeakStrongFinalAudioGapDirection, "weak-too-low", [StringComparison]::OrdinalIgnoreCase)) {
                "tune-bounded-weak-speech-lift-from-top-weak-and-strong-input-rows"
            }
            elseif ([string]::Equals($nr5MixedWeakStrongFinalAudioGapDirection, "weak-too-hot", [StringComparison]::OrdinalIgnoreCase)) {
                "tighten-weak-branch-boost-or-release-from-top-weak-input-rows"
            }
            else {
                "inspect-top-weak-and-strong-input-rows-before-changing-nr5-makeup"
            }
            break
        }
        "missing-strong-input" { "retune-or-extend-dwell-using-frontend-candidates-and-near-strong-rows"; break }
        "missing-weak-input" { "capture-weaker-or-fading-speech-before-volume-parity-tuning"; break }
        "missing-weak-and-strong-input" { "retune-or-extend-capture-before-using-trace-for-mixed-evidence"; break }
        "missing-output-gap" { "inspect-nr5-output-diagnostics-before-parity-tuning"; break }
        "low-evidence-weak-input" { "capture-speech-qualified-weak-input-before-accepting-final-audio-parity"; break }
        "insufficient-weak-input-evidence" { "extend-dwell-or-capture-more-qualified-weak-input-before-tuning"; break }
        "insufficient-strong-input-evidence" { "extend-dwell-or-capture-more-strong-input-before-tuning"; break }
        "ready-final-audio" { "post-leveler-final-audio-parity-ready"; break }
        "ready" {
            if ([string]::Equals($nr5MixedWeakStrongFinalAudioGapDirection, "within-parity", [StringComparison]::OrdinalIgnoreCase)) {
                "native-output-and-final-audio-parity-ready"
            }
            elseif ([string]::Equals($nr5MixedWeakStrongFinalAudioGapDirection, "weak-too-low", [StringComparison]::OrdinalIgnoreCase)) {
                "native-output-parity-ready-but-final-audio-needs-bounded-weak-lift"
            }
            elseif ([string]::Equals($nr5MixedWeakStrongFinalAudioGapDirection, "weak-too-hot", [StringComparison]::OrdinalIgnoreCase)) {
                "native-output-parity-ready-but-final-audio-weak-branch-too-hot"
            }
            else {
                "native-output-parity-ready-final-audio-unverified"
            }
            break
        }
        default { "collect-ready-nr5-mixed-weak-strong-trace"; break }
    }
    $nr5MixedWeakStrongTuningFocus = [ordered]@{
        status = $nr5MixedWeakStrongEvidenceStatus
        preferredAction = $nr5MixedWeakStrongPreferredAction
        weakInputSampleCount = $nr5WeakInputCount
        lowEvidenceWeakInputSampleCount = $nr5LowEvidenceWeakInputCount
        evidenceQualifiedWeakInputSampleCount = $nr5EvidenceQualifiedWeakInputCount
        strongInputSampleCount = $nr5StrongInputCount
        nearStrongInputSampleCount = $nr5NearStrongInputCount
        minimumWeakInputSampleCount = $nr5MinimumMixedWeakInputSampleCount
        minimumEvidenceQualifiedWeakInputSampleCount = $nr5MinimumMixedEvidenceQualifiedWeakInputSampleCount
        minimumStrongInputSampleCount = $nr5MinimumMixedStrongInputSampleCount
        minimumQualifiedWeakStrongInputSampleCount = $nr5MinimumQualifiedWeakStrongInputSampleCount
        weakInputSampleDeficit = [Math]::Max(0, $nr5MinimumMixedWeakInputSampleCount - $nr5WeakInputCount)
        evidenceQualifiedWeakInputSampleDeficit = [Math]::Max(0, $nr5MinimumMixedEvidenceQualifiedWeakInputSampleCount - $nr5EvidenceQualifiedWeakInputCount)
        strongInputSampleDeficit = [Math]::Max(0, $nr5MinimumMixedStrongInputSampleCount - $nr5StrongInputCount)
        speechQualifiedWeakInputSampleDeficit = [Math]::Max(0, $nr5MinimumQualifiedWeakStrongInputSampleCount - $nr5SpeechQualifiedWeakOutputValues.Count)
        speechQualifiedStrongInputSampleDeficit = [Math]::Max(0, $nr5MinimumQualifiedWeakStrongInputSampleCount - $nr5SpeechQualifiedStrongOutputValues.Count)
        passbandQualifiedWeakInputSampleDeficit = [Math]::Max(0, $nr5MinimumQualifiedWeakStrongInputSampleCount - $nr5PassbandQualifiedWeakOutputValues.Count)
        passbandQualifiedStrongInputSampleDeficit = [Math]::Max(0, $nr5MinimumQualifiedWeakStrongInputSampleCount - $nr5PassbandQualifiedStrongOutputValues.Count)
        outputGapDb = $nr5WeakStrongOutputGapDb
        outputGapDirection = $nr5MixedWeakStrongOutputGapDirection
        outputGapExcessDb = $nr5MixedWeakStrongOutputGapExcessDb
        weakOutputLiftNeededDb = $nr5WeakOutputLiftNeededDb
        weakOutputTrimNeededDb = $nr5WeakOutputTrimNeededDb
        finalAudioGapDb = $nr5WeakStrongFinalAudioGapDb
        finalAudioGapDirection = $nr5MixedWeakStrongFinalAudioGapDirection
        finalAudioGapExcessDb = $nr5MixedWeakStrongFinalAudioGapExcessDb
        weakFinalAudioLiftNeededDb = $nr5WeakFinalAudioLiftNeededDb
        weakFinalAudioTrimNeededDb = $nr5WeakFinalAudioTrimNeededDb
        speechQualifiedOutputGapDb = $nr5SpeechQualifiedWeakStrongOutputGapDb
        speechQualifiedFinalAudioGapDb = $nr5SpeechQualifiedWeakStrongFinalAudioGapDb
        speechQualifiedStatus = $nr5SpeechQualifiedWeakStrongEvidenceStatus
        passbandQualifiedOutputGapDb = $nr5PassbandQualifiedWeakStrongOutputGapDb
        passbandQualifiedFinalAudioGapDb = $nr5PassbandQualifiedWeakStrongFinalAudioGapDb
        passbandQualifiedStatus = $nr5PassbandQualifiedWeakStrongEvidenceStatus
        topWeakInputs = @($nr5WeakInputTopSamples)
        topStrongInputs = @($nr5StrongInputTopSamples)
        topSpeechQualifiedWeakInputs = @($nr5SpeechQualifiedWeakInputTopSamples)
        topSpeechQualifiedStrongInputs = @($nr5SpeechQualifiedStrongInputTopSamples)
        topPassbandQualifiedWeakInputs = @($nr5PassbandQualifiedWeakInputTopSamples)
        topPassbandQualifiedStrongInputs = @($nr5PassbandQualifiedStrongInputTopSamples)
    }
    if ($nr5LowEvidenceSampleCount -gt 0) {
        $nr5LowEvidenceLiftPct = [Math]::Round(100.0 * $nr5LowEvidenceLiftCount / $nr5LowEvidenceSampleCount, 1)
        $nr5LowEvidenceSuppressedPct = [Math]::Round(100.0 * $nr5LowEvidenceSuppressedCount / $nr5LowEvidenceSampleCount, 1)
        $nr5LowEvidenceAlignmentMismatchPct = [Math]::Round(100.0 * $nr5LowEvidenceAlignmentMismatchCount / $nr5LowEvidenceSampleCount, 1)
    }
    if ($nr5AudioAlignmentSampleCount -gt 0) {
        $nr5AudioAlignmentMismatchPct = [Math]::Round(100.0 * $nr5AudioAlignmentMismatchCount / $nr5AudioAlignmentSampleCount, 1)
        $nr5AudioAlignedAfterLevelerPct = [Math]::Round(100.0 * $nr5AudioAlignedAfterLevelerCount / $nr5AudioAlignmentSampleCount, 1)
        $nr5AudioLevelerNormalizedPct = [Math]::Round(100.0 * $nr5AudioLevelerNormalizedCount / $nr5AudioAlignmentSampleCount, 1)
    }
    $signalFloorAudioPct = $null
    $signalActiveAudioPct = $null
    $signalVoiceLikeEvidencePct = $null
    $signalQuietNoEvidencePct = $null
    $passbandActiveAudioPct = $null
    $passbandFloorAudioPct = $null
    $passbandAudioStatus = "not-evaluated"
    if ($runtimeCount -gt 0) {
        $signalFloorAudioPct = [Math]::Round(100.0 * $signalFloorAudioCount / $runtimeCount, 1)
        $signalActiveAudioPct = [Math]::Round(100.0 * $signalActiveAudioCount / $runtimeCount, 1)
        $signalVoiceLikeEvidencePct = [Math]::Round(100.0 * $signalVoiceLikeEvidenceCount / $runtimeCount, 1)
        $signalQuietNoEvidencePct = [Math]::Round(100.0 * $signalQuietNoEvidenceCount / $runtimeCount, 1)
    }
    if ($frontendTopPeakSampleCount -le 0) {
        $passbandAudioStatus = "no-frontend-peaks"
    }
    elseif ($passbandAudioRmsValues.Count -le 0) {
        $passbandAudioStatus = "no-passband-peaks"
    }
    else {
        $passbandActiveAudioPct = [Math]::Round(100.0 * $passbandActiveAudioRmsValues.Count / $passbandAudioRmsValues.Count, 1)
        $passbandFloorAudioPct = [Math]::Round(100.0 * $passbandFloorAudioRmsValues.Count / $passbandAudioRmsValues.Count, 1)
        if ($passbandActiveAudioRmsValues.Count -gt 0) {
            $passbandAudioStatus = "passband-active-audio"
        }
        elseif ($passbandFloorAudioRmsValues.Count -eq $passbandAudioRmsValues.Count) {
            $passbandAudioStatus = "passband-floor-only"
        }
        else {
            $passbandAudioStatus = "passband-low-audio"
        }
    }
    $passbandEvidenceMissing = ($nr5SampleCount -gt 0 -and
        $signalActiveAudioCount -gt 0 -and
        $frontendTopPeakSampleCount -gt 0 -and
        $passbandAudioRmsValues.Count -eq 0)
    $rxAudioLevelerConstrainedCount = @($rxAudioLevelerConstrainedSamples.ToArray()).Count
    $rxAudioLevelerConstrainedPct = $null
    $rxAudioLevelerBoostSlewLimitedPct = $null
    $rxAudioLevelerPeakLimitedPct = $null
    $rxAudioLevelerOutputLimitedPct = $null
    if ($rxAudioLevelerDiagnosticCount -gt 0) {
        $rxAudioLevelerConstrainedPct = [Math]::Round(100.0 * $rxAudioLevelerConstrainedCount / $rxAudioLevelerDiagnosticCount, 1)
        $rxAudioLevelerBoostSlewLimitedPct = [Math]::Round(100.0 * $rxAudioLevelerBoostSlewLimitedCount / $rxAudioLevelerDiagnosticCount, 1)
        $rxAudioLevelerPeakLimitedPct = [Math]::Round(100.0 * $rxAudioLevelerPeakLimitedCount / $rxAudioLevelerDiagnosticCount, 1)
        $rxAudioLevelerOutputLimitedPct = [Math]::Round(100.0 * $rxAudioLevelerOutputLimitedCount / $rxAudioLevelerDiagnosticCount, 1)
    }
    $agcTotalMovementThresholdDb = 12.0
    $agcActivePumpingThresholdDb = 6.0
    $agcQuietMovementThresholdDb = 6.0
    $agcLevelerConstrainedThresholdDb = 4.0
    $agcTotalMovementDb = if ([int]$agcStats["count"] -gt 1) { [double]$agcStats["movement"] } else { $null }
    $agcActiveMovementDb = if ([int]$activeAgcStats["count"] -gt 1) { [double]$activeAgcStats["movement"] } else { $null }
    $agcVoiceLikeMovementDb = if ([int]$voiceLikeAgcStats["count"] -gt 1) { [double]$voiceLikeAgcStats["movement"] } else { $null }
    $agcQuietNoEvidenceMovementDb = if ([int]$quietNoEvidenceAgcStats["count"] -gt 1) { [double]$quietNoEvidenceAgcStats["movement"] } else { $null }
    $agcLevelerConstrainedMovementDb = if ([int]$levelerConstrainedAgcStats["count"] -gt 1) { [double]$levelerConstrainedAgcStats["movement"] } else { $null }
    $agcActivePumpingRisk = (
        ($null -ne $agcActiveMovementDb -and $agcActiveMovementDb -gt $agcActivePumpingThresholdDb) -or
        ($null -ne $agcVoiceLikeMovementDb -and $agcVoiceLikeMovementDb -gt $agcActivePumpingThresholdDb))
    $agcWideMovement = ($null -ne $agcTotalMovementDb -and $agcTotalMovementDb -gt $agcTotalMovementThresholdDb)
    $agcQuietMovement = ($agcWideMovement -and
        $null -ne $agcQuietNoEvidenceMovementDb -and
        $agcQuietNoEvidenceMovementDb -gt $agcQuietMovementThresholdDb -and
        ($null -eq $agcActiveMovementDb -or $agcActiveMovementDb -le $agcActivePumpingThresholdDb))
    $agcLevelerConstrainedMovement = ($agcWideMovement -and
        $null -ne $agcLevelerConstrainedMovementDb -and
        $agcLevelerConstrainedMovementDb -gt $agcLevelerConstrainedThresholdDb)
    $agcStabilityStatus = if ([int]$agcStats["count"] -lt 2) {
        "insufficient-agc-samples"
    }
    elseif ($agcActivePumpingRisk) {
        "active-pumping-risk"
    }
    elseif ($agcLevelerConstrainedMovement) {
        "leveler-constrained-movement"
    }
    elseif ($agcQuietMovement) {
        "quiet-floor-movement"
    }
    elseif ($agcWideMovement) {
        "wide-level-transition"
    }
    else {
        "stable"
    }
    $rxStateVfoStats = Get-NumberStats $rxStateVfoValues
    $rxStateLoStats = Get-NumberStats $rxStateLoValues
    $rxStateSampleRateStats = Get-NumberStats $rxStateSampleRateValues
    $rxStateFrequencyDriftToleranceHz = 0.0
    $rxStateVfoDrift = ([int]$rxStateVfoStats["count"] -gt 1 -and
        [double]$rxStateVfoStats["movement"] -gt $rxStateFrequencyDriftToleranceHz)
    $rxStateLoDrift = ([int]$rxStateLoStats["count"] -gt 1 -and
        [double]$rxStateLoStats["movement"] -gt $rxStateFrequencyDriftToleranceHz)
    $rxStateSampleRateDrift = ([int]$rxStateSampleRateStats["count"] -gt 1 -and
        [double]$rxStateSampleRateStats["movement"] -gt 0.0)
    $rxStateModeDrift = (@($rxStateModeCounts.Keys).Count -gt 1)
    $rxStateFilterDrift = (@($rxStateFilterCounts.Keys).Count -gt 1)
    $rxStateCtunDrift = (@($rxStateCtunCounts.Keys).Count -gt 1)
    $rxStateStable = -not ($rxStateVfoDrift -or
        $rxStateLoDrift -or
        $rxStateSampleRateDrift -or
        $rxStateModeDrift -or
        $rxStateFilterDrift -or
        $rxStateCtunDrift)
    $rxStateBenchmarkReady = ($rxStateEvidenceSampleCount -eq 0 -or $rxStateStable)
    $rxStateStabilityStatus = if ($rxStateEvidenceSampleCount -eq 0) {
        "not-observed"
    }
    elseif ($rxStateStable) {
        "stable"
    }
    else {
        "rx-state-drift"
    }
    if (-not $rxStateBenchmarkReady) {
        $rxStateConstraintCount = [Math]::Max(1, $rxStateEvidenceSampleCount)
        if ($constraintCounts.ContainsKey("rx-state-drift")) {
            $constraintCounts["rx-state-drift"] = [int]$constraintCounts["rx-state-drift"] + $rxStateConstraintCount
        }
        else {
            $constraintCounts["rx-state-drift"] = $rxStateConstraintCount
        }
    }
    $constraintReadiness = @(ConvertTo-ReadinessCountArray -Map $constraintCounts -SampleCount $okCount)
    $hardConstraintReadiness = @(ConvertTo-ReadinessCountArray -Map $hardConstraintCounts -SampleCount $okCount)
    $statusReadiness = @(ConvertTo-ReadinessCountArray -Map $statusCounts -SampleCount $okCount)
    $topConstraint = Get-TopReadinessCount $constraintReadiness
    $topHardConstraint = Get-TopReadinessCount $hardConstraintReadiness
    $topStatus = Get-TopReadinessCount $statusReadiness
    $nr5LowEvidenceSuppressionDominates = ($nr5LowEvidenceSampleCount -gt 0 -and
        $null -ne $nr5LowEvidenceSuppressedPct -and
        [double]$nr5LowEvidenceSuppressedPct -ge 50.0 -and
        ($null -eq $nr5LowEvidenceLiftPct -or [double]$nr5LowEvidenceLiftPct -lt 20.0))
    $nr5NormalizationMotionIsFloorSuppression = ($nr5LowEvidenceSuppressionDominates -and
        $nr5WeakDropoutCandidateLossCount -eq 0 -and
        $nr5LowEvidenceLiftCount -eq 0)
    $quietIntermittentTrace = ($runtimeCount -gt 0 -and
        $null -ne $signalFloorAudioPct -and [double]$signalFloorAudioPct -ge 50.0 -and
        $null -ne $signalActiveAudioPct -and [double]$signalActiveAudioPct -ge 5.0 -and [double]$signalActiveAudioPct -le 45.0 -and
        $signalIntermittentBurstCount -gt 0)

    $summaryRecommendations = New-Object System.Collections.Generic.List[string]
    if ($okCount -eq 0) {
        $summaryRecommendations.Add("Start Zeus and verify /api/dsp/live-diagnostics is reachable before collecting live DSP evidence.") | Out-Null
    }
    if ($failedCount -gt 0) {
        $summaryRecommendations.Add("Repeat the trace after endpoint failures are resolved; missing samples weaken AGC and audio movement evidence.") | Out-Null
    }
    if ($hardBlockerSampleCount -gt 0) {
        if ($null -ne $topHardConstraint) {
            $hardName = [string](Get-JsonValue $topHardConstraint "name")
            $hardCount = [int](Get-JsonValue $topHardConstraint "count")
            $hardAction = [string](Get-JsonValue $topHardConstraint "action")
            $summaryRecommendations.Add("Clear hard live diagnostics gate '$hardName' seen in $hardCount/$okCount ok sample(s): $hardAction") | Out-Null
        }
        else {
            $summaryRecommendations.Add("Resolve hard live diagnostics blockers before using this trace as G2 acceptance evidence.") | Out-Null
        }
    }
    if (-not $rxStateBenchmarkReady) {
        $summaryRecommendations.Add((Get-CaptureReadinessAction "rx-state-drift")) | Out-Null
    }
    if ($null -ne $topConstraint) {
        $constraintName = [string](Get-JsonValue $topConstraint "name")
        $hardName = if ($null -eq $topHardConstraint) { "" } else { [string](Get-JsonValue $topHardConstraint "name") }
        if (-not [string]::Equals($constraintName, $hardName, [StringComparison]::OrdinalIgnoreCase)) {
            $constraintCount = [int](Get-JsonValue $topConstraint "count")
            $constraintAction = [string](Get-JsonValue $topConstraint "action")
            $summaryRecommendations.Add("Most common live diagnostics preflight constraint is '$constraintName' in $constraintCount/$okCount ok sample(s): $constraintAction") | Out-Null
        }
    }
    if ($runtimeCount -lt $okCount) {
        $summaryRecommendations.Add("Upgrade or restart the backend so every live diagnostics sample includes runtimeEvidence.") | Out-Null
    }
    if ($nr5SampleCount -gt 0 -and $nr5AlignedCount -lt $nr5SampleCount) {
        $summaryRecommendations.Add("Not every NR5 diagnostics sample was requested/effective NR5; reassert NR5 before judging NR5 DSP behavior.") | Out-Null
    }
    if ($nr5SampleCount -gt 0 -and $nr5AgcDiagnosticCount -lt $nr5SampleCount) {
        $summaryRecommendations.Add("Recapture this NR5 trace after restarting a backend that exports GetRXASPNRAgcDiagnostics; recovery-drive and makeup-gain evidence is missing.") | Out-Null
    }
    if ($nr5SampleCount -gt 0 -and $nr5ProbabilityDiagnosticCount -lt $nr5SampleCount) {
        $summaryRecommendations.Add("Recapture this NR5 trace after restarting a backend that exports GetRXASPNRProbabilityDiagnostics; signal-probability and mask-texture evidence is missing.") | Out-Null
    }
    if ($nr5SampleCount -gt 0 -and $nr5PeakDiagnosticCount -lt $nr5SampleCount) {
        $summaryRecommendations.Add("Recapture this NR5 trace after restarting a backend that exports GetRXASPNRPeakDiagnostics; output peak and adaptive-knee evidence is missing.") | Out-Null
    }
    if ($nr5LearnerResetSampleCount -gt 0) {
        $summaryRecommendations.Add("NR5 learned frames reset during this trace; inspect nr5LearnerStabilityWatch before using it as acceptance evidence.") | Out-Null
    }
    elseif ($nr5ManagedReapplySampleCount -gt 0) {
        $summaryRecommendations.Add("Managed NR5 position or policy was re-applied during this trace; inspect nr5LearnerStabilityWatch for state replay before judging NR5 weak-signal behavior.") | Out-Null
    }
    elseif ($nr5ManagedGenerationChangeCount -gt 0) {
        $summaryRecommendations.Add("NR5 channel generation changed during this trace; recapture after the channel lifecycle is stable before accepting learner stability evidence.") | Out-Null
    }
    elseif ($nr5SampleCount -gt 0 -and $nr5ManagedCounterSampleCount -lt $nr5SampleCount) {
        $summaryRecommendations.Add("Restart or rebuild the backend so every NR5 diagnostics sample exports managed replay counters before accepting NR5 learner-stability evidence.") | Out-Null
    }
    if ($frontendAdjacentNoiseUsableCount -gt 0 -and $nr5SampleCount -gt 0 -and $nr5AdjacentNoiseUsableCount -eq 0) {
        $summaryRecommendations.Add("Frontend adjacent-noise profiles were usable, but native NR5 did not report adjacent-profile trust; verify the WDSP DLL exports Set/GetRXASPNRAdjacentNoise* and that the pipeline is pushing the profile.") | Out-Null
    }
    if ($nr5AdjacentNoiseUsableCount -gt 0 -and
        [int]$nr5AdjacentNoiseSideBalanceStats["count"] -gt 0 -and
        [double]$nr5AdjacentNoiseSideBalanceStats["average"] -lt 0.45) {
        $summaryRecommendations.Add("Native NR5 adjacent-noise profile is mostly one-sided or imbalanced in this trace; avoid increasing adjacent suppression from this run without checking nearby QRM/splatter.") | Out-Null
    }
    if ($audioFreshCount -lt $runtimeCount) {
        $summaryRecommendations.Add("Restore fresh final audio before judging NR/AGC or external speech engines.") | Out-Null
    }
    if ($rxMetersFreshCount -lt $runtimeCount) {
        $summaryRecommendations.Add("Restore fresh RXA meters before using AGC gain or ADC headroom trends.") | Out-Null
    }
    switch ($agcStabilityStatus) {
        "active-pumping-risk" {
            $summaryRecommendations.Add("AGC gain moved more than 6 dB while audio was active or voice-like; run the agc-level-step/agc-pumping fixture and listen for pumping before tuning NR.") | Out-Null
        }
        "leveler-constrained-movement" {
            $summaryRecommendations.Add("AGC movement coincided with RX audio leveler constraints; inspect agcStabilityWatch and rxAudioLevelerWatch before changing AGC or NR5 makeup/release.") | Out-Null
        }
        "quiet-floor-movement" {
            $summaryRecommendations.Add("AGC gain moved mostly on quiet/no-evidence floor samples; inspect agcStabilityWatch.quietNoEvidenceAgcGainDb before treating it as audible pumping.") | Out-Null
        }
        "wide-level-transition" {
            $summaryRecommendations.Add("AGC gain moved more than 12 dB during the trace; compare active, voice-like, and quiet AGC buckets before tuning NR.") | Out-Null
        }
    }
    if (-not $nr5LowEvidenceSuppressionDominates -and [int]$rmsStats["count"] -gt 1 -and [double]$rmsStats["movement"] -gt 10.0) {
        $summaryRecommendations.Add("Final audio RMS moved more than 10 dB during the trace; pair this JSONL with audio render evidence before approving changes.") | Out-Null
    }
    if ([int]$rmsStats["count"] -gt 1 -and [double]$rmsStats["movement"] -gt 10.0) {
        $activeMovement = if ([int]$activeAudioRmsStats["count"] -gt 1) { [double]$activeAudioRmsStats["movement"] } else { $null }
        $floorMovement = if ([int]$floorAudioRmsStats["count"] -gt 1) { [double]$floorAudioRmsStats["movement"] } else { $null }
        if ($null -eq $activeMovement -or $activeMovement -le 6.0) {
            $summaryRecommendations.Add("Wide final-audio movement is mostly cross-bucket floor/active contrast; compare signalOccupancyWatch.activeAudioRmsDbfs and floorAudioRmsDbfs before treating it as pumping.") | Out-Null
        }
        elseif ($null -ne $floorMovement -and $floorMovement -gt 10.0) {
            $summaryRecommendations.Add("Floor-only audio movement is high; inspect signalOccupancyWatch.floorAudioRmsDbfs before increasing NR5 gain or release speed.") | Out-Null
        }
    }
    if ($quietIntermittentTrace) {
        $summaryRecommendations.Add("Trace is mostly quiet floor with intermittent signal bursts; use signalOccupancyWatch before interpreting wide audio movement as NR or leveler pumping.") | Out-Null
    }
    if ($runtimeCount -gt 0 -and $rxAudioLevelerDiagnosticCount -lt $runtimeCount) {
        $summaryRecommendations.Add("Recapture this trace after restarting a backend that exports RX audio leveler diagnostics; final loudness normalization evidence is incomplete.") | Out-Null
    }
    if ($rxAudioLevelerBoostSlewLimitedCount -gt 0) {
        $summaryRecommendations.Add("RX audio leveler boost slew limited one or more samples; inspect rxAudioLevelerWatch.topBoostSlewLimitedSamples before tuning weak-signal loudness.") | Out-Null
    }
    if ($rxAudioLevelerPeakLimitedCount -gt 0) {
        $summaryRecommendations.Add("RX audio leveler peak headroom limited one or more samples; inspect rxAudioLevelerWatch.topPeakLimitedSamples before increasing loudness target.") | Out-Null
    }
    if ($rxAudioLevelerOutputLimitedCount -gt 0) {
        $summaryRecommendations.Add("RX audio leveler output crest cap shaped one or more blocks; inspect rxAudioLevelerWatch.topOutputLimitedSamples before raising final loudness or release speed.") | Out-Null
    }
    if ($nr5AudioAlignmentMismatchCount -gt 0) {
        $summaryRecommendations.Add("NR5 output diagnostics diverged from both the pre-leveler and post-leveler audio on one or more samples; use nr5AudioAlignmentWatch before treating low-evidence lift/dropout rows as same-block DSP facts.") | Out-Null
    }
    if ($nr5AudioLevelerNormalizedCount -gt 0) {
        $summaryRecommendations.Add("NR5 weak speech was normalized to target loudness by the RX audio leveler on one or more samples; inspect nr5AudioAlignmentWatch.topLevelerNormalizedSamples before treating those rows as mismatches or dropouts.") | Out-Null
    }
    if ($nr5SpeechQualifiedWeakStrongEvidenceReady -and
        [string]::Equals($nr5MixedWeakStrongEvidenceStatus, "weak-strong-output-gap-watch", [StringComparison]::OrdinalIgnoreCase) -and
        $nr5NormalizationMotionIsFloorSuppression) {
        $summaryRecommendations.Add("Raw NR5 weak/strong averages are separated, but speech-qualified weak/strong final audio is within parity while floor suppression dominates; inspect nr5WeakSignalWatch.speechQualified* before raising NR5 floor or leveler gain.") | Out-Null
    }
    elseif ($nr5PassbandQualifiedWeakStrongEvidenceReady -and
        [string]::Equals($nr5MixedWeakStrongEvidenceStatus, "weak-strong-output-gap-watch", [StringComparison]::OrdinalIgnoreCase) -and
        $nr5NormalizationMotionIsFloorSuppression) {
        $summaryRecommendations.Add("Raw NR5 weak/strong averages are separated, but passband-qualified speech is normalized while adjacent or off-passband energy stays suppressed; inspect nr5WeakSignalWatch.passbandQualified* before raising NR5 floor or leveler gain.") | Out-Null
    }
    elseif ($nr5AudioAlignedAfterLevelerCount -gt 0 -and [int]$nr5AudioInputDeltaStats["count"] -gt 0 -and (
        ([double]$nr5AudioInputDeltaStats["min"] -lt -12.0) -or
        ([double]$nr5AudioInputDeltaStats["max"] -gt 12.0))) {
        $summaryRecommendations.Add("NR5 output aligns with post-leveler speech audio even though pre-leveler audio is lower; treat that delta as intended RX leveler gain, not a diagnostics mismatch.") | Out-Null
    }
    if ([int]$frontendAdjacentNoiseBinStats["count"] -eq 0) {
        $summaryRecommendations.Add("No frontend adjacent-noise profile was published in this trace; keep the frontend panadapter active before using adjacent-band noise as NR5 evidence.") | Out-Null
    }
    elseif ($frontendAdjacentNoiseUsableCount -eq 0) {
        $summaryRecommendations.Add("Frontend adjacent-noise samples were present but not marked usable; inspect adjacent-channel signals before using the profile for NR5 suppression tuning.") | Out-Null
    }
    if ($frontendTopPeakSampleCount -eq 0) {
        $summaryRecommendations.Add("Frontend top-peak evidence is missing; refresh the web frontend so NR5 traces record actual band peak locations before choosing a tuning window.") | Out-Null
    }
    elseif ($frontendFilterPassbandPeakSampleCount -eq 0 -and $frontendNearPassbandPeakSampleCount -gt 0) {
        $summaryRecommendations.Add("Frontend saw peaks near the dial, but none inside the RX filter passband; tune the signal into the active filter window before using this trace as on-signal NR5 evidence.") | Out-Null
    }
    elseif ($frontendNearPassbandPeakSampleCount -eq 0) {
        $summaryRecommendations.Add("Frontend saw band peaks, but none were within 3 kHz of the dial; retune toward frontendTopPeakWatch.topSamples before using this trace as on-signal NR5 evidence.") | Out-Null
    }
    $rxAudioLevelerCapNeedsReview = ($rxAudioLevelerOutputLimitedCount -gt 0 -and (
        ([int]$rxAudioLevelerOutputLimitReductionStats["count"] -gt 0 -and [double]$rxAudioLevelerOutputLimitReductionStats["max"] -gt 1.0) -or
        ([int]$rxAudioLevelerOutputLimitSampleCountStats["count"] -gt 0 -and [double]$rxAudioLevelerOutputLimitSampleCountStats["max"] -gt 8.0)))
    $rxAudioLevelerSettlingNeedsReview = ($rxAudioLevelerBoostSlewLimitedCount -gt 0 -and
        [int]$rmsStats["count"] -gt 1 -and [double]$rmsStats["movement"] -gt 4.0)
    $rxAudioLevelerHeadroomNeedsReview = ($rxAudioLevelerPeakLimitedCount -gt 0 -and
        [int]$rmsStats["count"] -gt 1 -and [double]$rmsStats["movement"] -gt 4.0)
    $nr5LowEvidenceLiftNeedsReview = ($nr5LowEvidenceLiftCount -ge 5 -or
        ($null -ne $nr5LowEvidenceLiftPct -and [double]$nr5LowEvidenceLiftPct -ge 20.0))
    $nr5OutputMotionNeedsReview = $false
    if ([int]$nr5OutputStats["count"] -gt 1 -and [double]$nr5OutputStats["movement"] -gt 6.0) {
        $nr5OutputMotionNeedsReview = ($nr5WeakDropoutCount -gt 0 `
            -or $nr5HotMakeupCount -gt 0 `
            -or ($null -ne $nr5NormalizationCompressionDb -and [double]$nr5NormalizationCompressionDb -lt -2.0) `
            -or ([int]$rmsStats["count"] -gt 1 -and [double]$rmsStats["movement"] -gt 6.0))
        if ($nr5LowEvidenceSuppressionDominates) {
            $nr5OutputMotionNeedsReview = $false
        }
    }
    if ($nr5OutputMotionNeedsReview) {
        if ($nr5WeakStrongFinalAudioParityReady -and
            [int]$rmsStats["count"] -gt 1 -and [double]$rmsStats["movement"] -le 4.0 -and
            $nr5WeakDropoutCandidateLossCount -eq 0 -and
            $rxAudioLevelerConstrainedCount -eq 0) {
            $nr5OutputMotionNeedsReview = $false
        }
    }
    if ($nr5OutputMotionNeedsReview) {
        $summaryRecommendations.Add("NR5 output RMS moved more than 6 dB during the trace; inspect nr5RecoveryDrive and nr5MakeupGainDb before tuning mask thresholds.") | Out-Null
    }
    if ($null -ne $nr5NormalizationCompressionDb -and [double]$nr5NormalizationCompressionDb -lt -2.0 -and
        -not $nr5NormalizationMotionIsFloorSuppression) {
        $summaryRecommendations.Add("NR5 output moved more than input during the trace; reduce makeup/recovery memory before increasing weak-signal gain.") | Out-Null
    }
    elseif ($null -ne $nr5NormalizationCompressionDb -and [double]$nr5NormalizationCompressionDb -lt -2.0 -and
        $nr5NormalizationMotionIsFloorSuppression) {
        $summaryRecommendations.Add("NR5 output movement is dominated by low-evidence floor suppression with no candidate weak-loss rows; do not reduce recovery/makeup based on normalization compression alone.") | Out-Null
    }
    if ($null -ne $nr5WeakStrongOutputGapDb -and [Math]::Abs([double]$nr5WeakStrongOutputGapDb) -gt 6.0 -and
        -not $nr5WeakStrongFinalAudioParityReady -and
        -not $nr5PassbandQualifiedWeakStrongEvidenceReady -and
        [string]::Equals($nr5MixedWeakStrongEvidenceStatus, "weak-strong-output-gap-watch", [StringComparison]::OrdinalIgnoreCase)) {
        $summaryRecommendations.Add("NR5 weak and strong outputs differ by more than 6 dB on average; tune normalization before judging faint-signal fidelity.") | Out-Null
    }
    elseif ($null -ne $nr5WeakStrongOutputGapDb -and [Math]::Abs([double]$nr5WeakStrongOutputGapDb) -gt 6.0 -and
        $nr5MixedWeakStrongInputEvidenceInsufficient) {
        $summaryRecommendations.Add("NR5 weak/strong output gap is visible, but the mixed trace has too few repeated weak or strong rows for tuning; extend dwell or recapture before changing NR5 makeup/leveler logic.") | Out-Null
    }
    elseif ($null -ne $nr5WeakStrongOutputGapDb -and [Math]::Abs([double]$nr5WeakStrongOutputGapDb) -gt 6.0 -and
        ($nr5WeakStrongFinalAudioParityReady -or $nr5PassbandQualifiedWeakStrongEvidenceReady)) {
        $summaryRecommendations.Add("Native NR5 weak/strong output differs, but post-leveler speech audio is within parity; judge volume from nr5WeakSignalWatch weak/strong final-audio fields before changing NR5 makeup.") | Out-Null
    }
    if ($nr5SampleCount -gt 0 -and [string]::Equals($nr5MixedWeakStrongEvidenceStatus, "missing-strong-input", [StringComparison]::OrdinalIgnoreCase) -and
        $nr5NearStrongInputCount -gt 0) {
        $summaryRecommendations.Add("NR5 trace has near-strong samples between $nr5NearStrongInputThresholdDbfs and $nr5StrongInputThresholdDbfs dBFS but no strict strong-input samples; inspect nr5WeakSignalWatch.topNearStrongInputs and retune/extend dwell around those peaks before rejecting this frequency neighborhood.") | Out-Null
    }
    elseif ($nr5SampleCount -gt 0 -and [string]::Equals($nr5MixedWeakStrongEvidenceStatus, "missing-strong-input", [StringComparison]::OrdinalIgnoreCase)) {
        $summaryRecommendations.Add("NR5 trace has weak-input samples but no strong-input samples; capture an active mixed weak+strong speech window before using this trace as volume-parity acceptance evidence.") | Out-Null
    }
    elseif ($nr5SampleCount -gt 0 -and [string]::Equals($nr5MixedWeakStrongEvidenceStatus, "missing-weak-input", [StringComparison]::OrdinalIgnoreCase)) {
        $summaryRecommendations.Add("NR5 trace has strong-input samples but no weak-input samples; capture a weaker station or fading speech window before using this trace as weak-signal parity evidence.") | Out-Null
    }
    elseif ($nr5SampleCount -gt 0 -and [string]::Equals($nr5MixedWeakStrongEvidenceStatus, "missing-weak-and-strong-input", [StringComparison]::OrdinalIgnoreCase)) {
        $summaryRecommendations.Add("NR5 trace did not include weak or strong classified input samples; retune or extend capture duration before using it as mixed-signal acceptance evidence.") | Out-Null
    }
    elseif ($nr5SampleCount -gt 0 -and [string]::Equals($nr5MixedWeakStrongEvidenceStatus, "missing-output-gap", [StringComparison]::OrdinalIgnoreCase)) {
        $summaryRecommendations.Add("NR5 trace has weak and strong input samples but no output gap statistic; inspect NR5 output diagnostics before using it as volume-parity evidence.") | Out-Null
    }
    elseif ($nr5SampleCount -gt 0 -and [string]::Equals($nr5MixedWeakStrongEvidenceStatus, "low-evidence-weak-input", [StringComparison]::OrdinalIgnoreCase)) {
        $summaryRecommendations.Add("NR5 mixed weak/strong final-audio parity only used low-evidence weak rows; capture speech-qualified weak input before accepting this as weak-signal parity evidence.") | Out-Null
    }
    elseif ($nr5SampleCount -gt 0 -and [string]::Equals($nr5MixedWeakStrongEvidenceStatus, "insufficient-weak-input-evidence", [StringComparison]::OrdinalIgnoreCase)) {
        $summaryRecommendations.Add("NR5 trace has mixed weak/strong samples, but fewer than $nr5MinimumMixedEvidenceQualifiedWeakInputSampleCount qualified weak rows; extend dwell through a weak-signal fade before using this trace for parity tuning or acceptance.") | Out-Null
    }
    elseif ($nr5SampleCount -gt 0 -and [string]::Equals($nr5MixedWeakStrongEvidenceStatus, "insufficient-strong-input-evidence", [StringComparison]::OrdinalIgnoreCase)) {
        $summaryRecommendations.Add("NR5 trace has weak samples, but fewer than $nr5MinimumMixedStrongInputSampleCount strong rows; extend dwell through a stronger speech burst before using this trace for mixed weak/strong parity.") | Out-Null
    }
    if ($nr5WeakDropoutCount -gt 0 -and
        $nr5WeakDropoutCandidateLossCount -eq 0 -and
        $nr5WeakDropoutFinalAudibleCount -eq $nr5WeakDropoutCount) {
        $summaryRecommendations.Add("NR5 strict weak-output dropout thresholds were hit, but every strict dropout row was already final-audio audible; treat these as recovered weak bursts unless subjective listening says otherwise.") | Out-Null
    }
    elseif ($nr5WeakDropoutCount -gt 0) {
        $summaryRecommendations.Add("NR5 strict weak-output dropouts were observed; inspect nr5WeakSignalWatch dropout classifications before increasing global makeup gain.") | Out-Null
    }
    if ($nr5WeakDropoutFinalAudibleCount -gt 0) {
        $summaryRecommendations.Add("Some strict NR5 weak-dropout rows were already final-audio audible after the RX leveler; use nr5WeakSignalWatch.topFinalAudibleWeakDropouts before treating them as true weak-signal loss.") | Out-Null
    }
    if ($nr5WeakDropoutCandidateLossCount -gt 0) {
        $summaryRecommendations.Add("NR5 candidate weak-loss rows remained below the input and were not final-audio audible; tune bounded weak-frame rescue from nr5WeakSignalWatch.topCandidateWeakLosses rather than lifting the whole noise floor.") | Out-Null
    }
    if ($nr5WeakDropoutCandidateLossCount -gt 0 -and [int]$nr5SignalProbabilityStats["count"] -gt 1 -and [double]$nr5SignalProbabilityStats["max"] -lt 0.30) {
        $summaryRecommendations.Add("NR5 candidate weak-loss rows coincided with low signal probability; tune probability/coherence opening before changing output AGC.") | Out-Null
    }
    if ($nr5WeakDropoutCandidateLossCount -gt 0 -and [int]$nr5SignalProbabilityStats["count"] -gt 1 -and [double]$nr5SignalProbabilityStats["max"] -ge 0.30 -and
        [int]$nr5TextureFillStats["count"] -gt 1 -and [double]$nr5TextureFillStats["max"] -lt 0.05) {
        $summaryRecommendations.Add("NR5 saw probable weak signal but texture fill stayed low during candidate weak-loss rows; tune mask hole-fill before raising persistent makeup gain.") | Out-Null
    }
    if ($nr5WeakBelowInputCount -gt 0 -and $nr5WeakDropoutCandidateLossCount -gt 0) {
        $summaryRecommendations.Add("Some weak NR5 samples left the output below the input; prefer bounded weak-frame rescue over persistent makeup gain.") | Out-Null
    }
    elseif ($nr5WeakBelowInputCount -gt 0 -and $nr5LowEvidenceSuppressionDominates) {
        $summaryRecommendations.Add("Most weak rows below input are dominated by low-evidence suppression; inspect candidate weak-loss rows before treating below-input samples as voice loss.") | Out-Null
    }
    if ($nr5HotMakeupCount -gt 0) {
        $summaryRecommendations.Add("NR5 makeup exceeded 12 dB on one or more samples; inspect nr5WeakSignalWatch.topHotMakeup before changing recovery attack/release.") | Out-Null
    }
    if ($nr5LowEvidenceLiftCount -gt 0) {
        $summaryRecommendations.Add("NR5 lifted low-evidence weak samples into the audible range; inspect nr5LowEvidenceLiftWatch.topLiftedSamples before treating the recovered audio as real weak-signal content.") | Out-Null
    }
    if ($nr5LowEvidenceAlignmentMismatchCount -gt 0) {
        $summaryRecommendations.Add("Some low-evidence NR5 rows had audio/NR5 alignment mismatch; prefer faster traces or matched offline capture before changing NR5 thresholds from those rows.") | Out-Null
    }
    if ($nr5LowEvidenceSuppressedCount -gt 0) {
        $summaryRecommendations.Add("NR5 suppressed low-evidence weak samples instead of normalizing them; confirm the trace is noise-only or adjacent-channel noise before using it as weak-signal loss evidence.") | Out-Null
    }
    if ([int]$nr5PeakReductionDbStats["count"] -gt 1 -and [double]$nr5PeakReductionDbStats["max"] -gt 3.0) {
        $summaryRecommendations.Add("NR5 adaptive peak shaping exceeded 3 dB on one or more samples; compare nr5OutputPeakDbfs with final audioPeakDbfs before tuning downstream level controls.") | Out-Null
    }
    if ([int]$nr5RecoveryDriveStats["count"] -gt 1 -and [double]$nr5RecoveryDriveStats["max"] -lt 0.20 -and
        [int]$rmsStats["count"] -gt 1 -and [double]$rmsStats["movement"] -gt 6.0) {
        $summaryRecommendations.Add("Final audio moved but NR5 recovery drive stayed low; improve confidence/gating before increasing makeup gain.") | Out-Null
    }
    if ([int]$nr5RecoveryDriveStats["count"] -gt 1 -and [double]$nr5RecoveryDriveStats["max"] -ge 0.20 -and
        [int]$nr5MakeupGainDbStats["count"] -gt 1 -and [double]$nr5MakeupGainDbStats["max"] -lt 1.5 -and
        [int]$rmsStats["count"] -gt 1 -and [double]$rmsStats["movement"] -gt 6.0) {
        $summaryRecommendations.Add("NR5 recovery drive engaged but makeup gain stayed low; tune the fast makeup path rather than the spectral mask.") | Out-Null
    }
    if ([int]$backlogStats["count"] -gt 0 -and [double]$backlogStats["max"] -gt 0) {
        $summaryRecommendations.Add("Monitor backlog appeared during the trace; drain or stop monitor injection before judging live audio fidelity.") | Out-Null
    }
    if ($summaryRecommendations.Count -eq 0) {
        $summaryRecommendations.Add("Store this JSONL trace with the modernization bundle and compare it with fixture metrics before changing DSP defaults.") | Out-Null
    }

    $trendStatus = "evidence-trace"
    if ($okCount -eq 0) {
        $trendStatus = "endpoint-unreachable"
    }
    elseif ($hardBlockerSampleCount -gt 0) {
        $trendStatus = "blocked"
    }
    elseif (-not $rxStateBenchmarkReady) {
        $trendStatus = "rx-state-drift"
    }
    elseif ($runtimeCount -lt $okCount) {
        $trendStatus = "runtime-evidence-missing"
    }
    elseif ($audioFreshCount -lt $runtimeCount) {
        $trendStatus = "final-audio-not-fresh"
    }
    elseif ($rxMetersFreshCount -lt $runtimeCount) {
        $trendStatus = "rx-meters-not-fresh"
    }
    elseif ($nr5SampleCount -gt 0 -and $nr5AgcDiagnosticCount -lt $nr5SampleCount) {
        $trendStatus = "nr5-agc-diagnostics-missing"
    }
    elseif ($nr5SampleCount -gt 0 -and $nr5ProbabilityDiagnosticCount -lt $nr5SampleCount) {
        $trendStatus = "nr5-probability-diagnostics-missing"
    }
    elseif ($nr5SampleCount -gt 0 -and $nr5PeakDiagnosticCount -lt $nr5SampleCount) {
        $trendStatus = "nr5-peak-diagnostics-missing"
    }
    elseif ($nr5LearnerResetSampleCount -gt 0) {
        $trendStatus = "nr5-learner-reset-watch"
    }
    elseif ($nr5ManagedReapplySampleCount -gt 0) {
        $trendStatus = "nr5-managed-reapply-watch"
    }
    elseif ($nr5ManagedGenerationChangeCount -gt 0) {
        $trendStatus = "nr5-channel-generation-changed"
    }
    elseif ($agcActivePumpingRisk) {
        $trendStatus = "agc-pumping-watch"
    }
    elseif ($agcWideMovement) {
        $trendStatus = "agc-movement-watch"
    }
    elseif ($passbandEvidenceMissing) {
        $trendStatus = "passband-evidence-missing"
    }
    elseif ($nr5MixedWeakStrongInputEvidenceInsufficient) {
        $trendStatus = "mixed-evidence-insufficient"
    }
    elseif ($nr5OutputMotionNeedsReview) {
        $trendStatus = "nr5-output-level-watch"
    }
    elseif ($nr5LowEvidenceLiftNeedsReview) {
        $trendStatus = "nr5-low-evidence-lift-watch"
    }
    elseif ($quietIntermittentTrace) {
        $trendStatus = "quiet-intermittent-signal-watch"
    }
    elseif (-not $nr5LowEvidenceSuppressionDominates -and [int]$rmsStats["count"] -gt 1 -and [double]$rmsStats["movement"] -gt 10.0) {
        $trendStatus = "audio-level-watch"
    }
    elseif ($rxAudioLevelerCapNeedsReview) {
        $trendStatus = "rx-leveler-cap-watch"
    }
    elseif ($rxAudioLevelerSettlingNeedsReview) {
        $trendStatus = "rx-leveler-settling-watch"
    }
    elseif ($rxAudioLevelerHeadroomNeedsReview) {
        $trendStatus = "rx-leveler-headroom-watch"
    }
    elseif ($readyCount -eq $okCount) {
        $trendStatus = "ready-trace"
    }

    $readyTrace = ($okCount -gt 0 -and
        $failedCount -eq 0 -and
        $hardBlockerSampleCount -eq 0 -and
        $runtimeCount -eq $okCount -and
        $audioFreshCount -eq $runtimeCount -and
        $rxMetersFreshCount -eq $runtimeCount -and
        $rxStateBenchmarkReady -and
        ($nr5SampleCount -eq 0 -or ($nr5AgcDiagnosticCount -eq $nr5SampleCount -and
            $nr5ProbabilityDiagnosticCount -eq $nr5SampleCount -and
            $nr5PeakDiagnosticCount -eq $nr5SampleCount)))
    $nr5TuningReadyTrace = ($okCount -gt 0 -and
        $failedCount -eq 0 -and
        $nr5SampleCount -eq $okCount -and
        $nr5AlignedCount -eq $okCount -and
        $nr5TuningReadyCount -eq $okCount -and
        $rxStateBenchmarkReady -and
        $nr5AgcDiagnosticCount -eq $nr5SampleCount -and
        $nr5ProbabilityDiagnosticCount -eq $nr5SampleCount -and
        $nr5PeakDiagnosticCount -eq $nr5SampleCount)
    $nr5TuningTraceStatus = if ($nr5TuningReadyTrace) {
        "ready-for-nr5-live-tuning"
    }
    elseif ($nr5SampleCount -eq 0) {
        "nr5-diagnostics-missing"
    }
    elseif ($nr5AlignedCount -lt $nr5SampleCount) {
        "nr5-mode-not-aligned"
    }
    elseif ($nr5TuningReadyCount -lt $okCount) {
        "nr5-tuning-preflight-required"
    }
    elseif ($nr5AgcDiagnosticCount -lt $nr5SampleCount) {
        "nr5-agc-diagnostics-missing"
    }
    elseif ($nr5ProbabilityDiagnosticCount -lt $nr5SampleCount) {
        "nr5-probability-diagnostics-missing"
    }
    elseif ($nr5PeakDiagnosticCount -lt $nr5SampleCount) {
        "nr5-peak-diagnostics-missing"
    }
    else {
        "nr5-tuning-watch"
    }

    $comparisonStateId = ([string]$ComparisonId).Trim().ToLowerInvariant()
    $comparisonStateStrict = ($comparisonStateId -eq "nr5-spnr" -or $comparisonStateId -eq "off-baseline")
    $comparisonStateReady = $true
    $comparisonStateStatus = "not-required"
    $comparisonStateNextAction = "No strict NR mode state proof is required for this comparison label."
    if ($comparisonStateId -eq "nr5-spnr") {
        $comparisonStateReady = ($okCount -gt 0 -and
            $nr5RequestedSampleCount -eq $okCount -and
            $nr5EffectiveSampleCount -eq $okCount -and
            $nr5AlignedCount -eq $okCount -and
            $nr5SampleCount -eq $okCount -and
            $nr5AgcDiagnosticCount -eq $nr5SampleCount -and
            $nr5ProbabilityDiagnosticCount -eq $nr5SampleCount -and
            $nr5PeakDiagnosticCount -eq $nr5SampleCount)
        if ($comparisonStateReady) {
            $comparisonStateStatus = "nr5-state-ready"
            $comparisonStateNextAction = "Use this trace as NR5/SPNR state-authenticated evidence."
        }
        elseif ($okCount -eq 0) {
            $comparisonStateStatus = "no-ok-samples"
            $comparisonStateNextAction = "Recapture after the live diagnostics endpoint returns successful samples."
        }
        elseif ($nr5EffectiveSampleCount -lt $okCount) {
            $comparisonStateStatus = "nr5-effective-missing"
            $comparisonStateNextAction = "Reassert NR5/SPNR and wait for effectiveNrMode=Nr5 before recapturing this comparison."
        }
        elseif ($nr5RequestedSampleCount -lt $okCount -or $nr5AlignedCount -lt $okCount) {
            $comparisonStateStatus = "nr5-request-effective-mismatch"
            $comparisonStateNextAction = "Wait for requested/effective NR mode alignment before using this trace as NR5 evidence."
        }
        elseif ($nr5SampleCount -lt $okCount) {
            $comparisonStateStatus = "nr5-diagnostics-missing"
            $comparisonStateNextAction = "Recapture after the backend publishes NR5/SPNR diagnostics for every successful sample."
        }
        else {
            $comparisonStateStatus = "nr5-diagnostics-incomplete"
            $comparisonStateNextAction = "Restart or update the backend so every NR5 sample includes AGC, probability, and peak diagnostics."
        }
    }
    elseif ($comparisonStateId -eq "off-baseline") {
        $comparisonStateReady = ($okCount -gt 0 -and
            $nrOffRequestedSampleCount -eq $okCount -and
            $nrOffEffectiveSampleCount -eq $okCount -and
            $nr5SampleCount -eq 0)
        if ($comparisonStateReady) {
            $comparisonStateStatus = "off-state-ready"
            $comparisonStateNextAction = "Use this trace as NR-off baseline evidence."
        }
        elseif ($okCount -eq 0) {
            $comparisonStateStatus = "no-ok-samples"
            $comparisonStateNextAction = "Recapture after the live diagnostics endpoint returns successful samples."
        }
        elseif ($nr5SampleCount -gt 0) {
            $comparisonStateStatus = "off-nr5-diagnostics-present"
            $comparisonStateNextAction = "Disable NR5/SPNR before recapturing the off-baseline comparison."
        }
        elseif ($nrOffEffectiveSampleCount -lt $okCount) {
            $comparisonStateStatus = "off-effective-missing"
            $comparisonStateNextAction = "Switch NR mode Off and wait for effectiveNrMode=Off before recapturing this comparison."
        }
        else {
            $comparisonStateStatus = "off-request-effective-mismatch"
            $comparisonStateNextAction = "Wait for requested/effective NR mode alignment before using this trace as Off evidence."
        }
    }
    elseif ($nrModeMismatchSampleCount -gt 0) {
        $comparisonStateStatus = "advisory-mode-mismatch"
        $comparisonStateNextAction = "Review requested/effective NR mode transitions before interpreting this advisory comparison."
    }

    $captureReadinessStatus = if ($okCount -eq 0) {
        "endpoint-unreachable"
    }
    elseif ($failedCount -gt 0) {
        "sample-failures"
    }
    elseif ($hardBlockerSampleCount -gt 0) {
        "blocked-hard-gate"
    }
    elseif ($readyTrace -and $constraintReadiness.Count -gt 0) {
        "ready-with-advisory"
    }
    elseif ($readyTrace) {
        "ready"
    }
    elseif ($constraintReadiness.Count -gt 0) {
        "preflight-review"
    }
    else {
        "evidence-trace"
    }

    $durationMs = [int]($CompletedUtc - $StartedUtc).TotalMilliseconds
    $squelchClosedPct = 0.0
    if ($squelchEnabledCount -gt 0) {
        $squelchClosedPct = [Math]::Round(100.0 * $squelchClosedCount / $squelchEnabledCount, 1)
    }

    return [ordered]@{
        schemaVersion = 1
        tool = "watch-dsp-live-diagnostics"
        generatedUtc = $CompletedUtc
        sourceMode = $SourceMode
        endpoint = $Endpoint
        label = if ([string]::IsNullOrWhiteSpace($Label)) { $null } else { $Label }
        scenarioId = if ([string]::IsNullOrWhiteSpace($ScenarioId)) { $null } else { $ScenarioId }
        comparisonId = if ([string]::IsNullOrWhiteSpace($ComparisonId)) { $null } else { $ComparisonId }
        inputPath = if ([string]::IsNullOrWhiteSpace($SourcePath)) { $null } else { $SourcePath }
        jsonlPath = if ([string]::IsNullOrWhiteSpace($LinePath)) { $null } else { $LinePath }
        tuneStepHz = $TuneStepHz
        rxChainFilterLowHz = $rxChainFilterLowHz
        rxChainFilterHighHz = $rxChainFilterHighHz
        rxChainFilterWidthHz = $rxChainFilterWidthHz
        rxChainFilterPresetName = $rxChainFilterPresetName
        radioVfoHz = $radioVfoHz
        radioLoHz = $radioLoHz
        radioMode = $radioMode
        radioCtunEnabled = $radioCtunEnabled
        radioSampleRate = $radioSampleRate
        startedUtc = $StartedUtc
        completedUtc = $CompletedUtc
        durationMs = $durationMs
        sampleCount = @($SampleRecords).Count
        okSampleCount = $okCount
        failedSampleCount = $failedCount
        readySampleCount = $readyCount
        readyForBenchmarkTrace = $readyTrace
        trendStatus = $trendStatus
        nr5TuningReadySampleCount = $nr5TuningReadyCount
        nr5TuningReadyTrace = $nr5TuningReadyTrace
        nr5TuningTraceStatus = $nr5TuningTraceStatus
        hardBlockerSampleCount = $hardBlockerSampleCount
        captureReadinessWatch = [ordered]@{
            status = $captureReadinessStatus
            preflightReady = $readyTrace
            hardGatePass = ($failedCount -eq 0 -and $hardBlockerSampleCount -eq 0)
            strictPreflightPass = ($failedCount -eq 0 -and $hardBlockerSampleCount -eq 0 -and $readyTrace)
            okSampleCount = $okCount
            failedSampleCount = $failedCount
            readySampleCount = $readyCount
            hardBlockerSampleCount = $hardBlockerSampleCount
            topConstraint = $topConstraint
            topHardConstraint = $topHardConstraint
            topStatus = $topStatus
            constraints = @($constraintReadiness)
            hardConstraints = @($hardConstraintReadiness)
            statuses = @($statusReadiness)
            actionItems = @($constraintReadiness | Select-Object -First 8)
        }
        runtimeEvidenceSampleCount = $runtimeCount
        rxMetersFreshSampleCount = $rxMetersFreshCount
        audioFreshSampleCount = $audioFreshCount
        txMonitorSampleCount = $txMonitorCount
        squelchEnabledSampleCount = $squelchEnabledCount
        squelchClosedSampleCount = $squelchClosedCount
        squelchClosedPct = $squelchClosedPct
        squelchTailSampleCount = $squelchTailCount
        latencyMs = Get-NumberStats $latencies
        readinessScore = Get-NumberStats $readinessScores
        agcGainDb = $agcStats
        rxStateStabilityWatch = [ordered]@{
            status = $rxStateStabilityStatus
            stable = $rxStateStable
            benchmarkReady = $rxStateBenchmarkReady
            evidenceSampleCount = $rxStateEvidenceSampleCount
            frequencyDriftToleranceHz = $rxStateFrequencyDriftToleranceHz
            tuneStepHz = $TuneStepHz
            vfoHz = $rxStateVfoStats
            radioLoHz = $rxStateLoStats
            radioSampleRateHz = $rxStateSampleRateStats
            modeCounts = @(ConvertTo-CountArray $rxStateModeCounts)
            filterCounts = @(ConvertTo-CountArray $rxStateFilterCounts)
            ctunCounts = @(ConvertTo-CountArray $rxStateCtunCounts)
            vfoDrift = $rxStateVfoDrift
            radioLoDrift = $rxStateLoDrift
            radioSampleRateDrift = $rxStateSampleRateDrift
            modeDrift = $rxStateModeDrift
            filterDrift = $rxStateFilterDrift
            ctunDrift = $rxStateCtunDrift
        }
        agcStabilityWatch = [ordered]@{
            status = $agcStabilityStatus
            pumpingRisk = $agcActivePumpingRisk
            totalMovementThresholdDb = $agcTotalMovementThresholdDb
            activePumpingThresholdDb = $agcActivePumpingThresholdDb
            quietMovementThresholdDb = $agcQuietMovementThresholdDb
            levelerConstrainedThresholdDb = $agcLevelerConstrainedThresholdDb
            totalAgcGainDb = $agcStats
            activeAgcGainDb = $activeAgcStats
            voiceLikeAgcGainDb = $voiceLikeAgcStats
            quietNoEvidenceAgcGainDb = $quietNoEvidenceAgcStats
            levelerConstrainedAgcGainDb = $levelerConstrainedAgcStats
            activeAudioSampleCount = $signalActiveAudioCount
            voiceLikeEvidenceSampleCount = $signalVoiceLikeEvidenceCount
            quietNoEvidenceSampleCount = $signalQuietNoEvidenceCount
            levelerConstrainedSampleCount = $rxAudioLevelerConstrainedCount
        }
        adcHeadroomDb = $headroomStats
        audioRmsDbfs = $rmsStats
        audioPeakDbfs = $peakStats
        signalOccupancyWatch = [ordered]@{
            floorAudioThresholdDbfs = $signalFloorAudioThresholdDbfs
            activeAudioThresholdDbfs = $signalActiveAudioThresholdDbfs
            voiceLikeConfidenceThreshold = $signalEvidenceConfidenceThreshold
            voiceLikeProbabilityThreshold = $signalEvidenceProbabilityThreshold
            voiceLikeAgcGateThreshold = $signalEvidenceAgcGateThreshold
            runtimeSampleCount = $runtimeCount
            floorAudioSampleCount = $signalFloorAudioCount
            floorAudioPct = $signalFloorAudioPct
            floorAudioRmsDbfs = $floorAudioRmsStats
            activeAudioSampleCount = $signalActiveAudioCount
            activeAudioPct = $signalActiveAudioPct
            activeAudioRmsDbfs = $activeAudioRmsStats
            voiceLikeEvidenceSampleCount = $signalVoiceLikeEvidenceCount
            voiceLikeEvidencePct = $signalVoiceLikeEvidencePct
            voiceLikeAudioRmsDbfs = $voiceLikeAudioRmsStats
            quietNoEvidenceSampleCount = $signalQuietNoEvidenceCount
            quietNoEvidencePct = $signalQuietNoEvidencePct
            quietNoEvidenceAudioRmsDbfs = $quietNoEvidenceAudioRmsStats
            intermittentBurstSampleCount = $signalIntermittentBurstCount
            quietIntermittentTrace = $quietIntermittentTrace
        }
        passbandAudioWatch = [ordered]@{
            status = $passbandAudioStatus
            passbandEvidenceMissing = $passbandEvidenceMissing
            nearPassbandThresholdHz = [int]$frontendNearPassbandThresholdHz
            filterPassbandEdgeToleranceHz = [int]$frontendFilterPassbandEdgeToleranceHz
            filterLowHz = $rxChainFilterLowHz
            filterHighHz = $rxChainFilterHighHz
            activeAudioThresholdDbfs = $signalActiveAudioThresholdDbfs
            floorAudioThresholdDbfs = $signalFloorAudioThresholdDbfs
            frontendTopPeakSampleCount = $frontendTopPeakSampleCount
            legacyNearPassbandPeakSampleCount = $frontendNearPassbandPeakSampleCount
            filterPassbandPeakSampleCount = $frontendFilterPassbandPeakSampleCount
            passbandPeakSampleCount = $passbandAudioRmsValues.Count
            offPassbandPeakSampleCount = $offPassbandAudioRmsValues.Count
            passbandActiveAudioSampleCount = $passbandActiveAudioRmsValues.Count
            passbandActiveAudioPct = $passbandActiveAudioPct
            passbandFloorAudioSampleCount = $passbandFloorAudioRmsValues.Count
            passbandFloorAudioPct = $passbandFloorAudioPct
            passbandAudioRmsDbfs = $passbandAudioRmsStats
            passbandActiveAudioRmsDbfs = $passbandActiveAudioRmsStats
            passbandFloorAudioRmsDbfs = $passbandFloorAudioRmsStats
            offPassbandAudioRmsDbfs = $offPassbandAudioRmsStats
            topPassbandSamples = @($passbandAudioTopSamples)
            topOffPassbandSamples = @($offPassbandAudioTopSamples)
        }
        rxAudioLevelerDiagnosticSampleCount = $rxAudioLevelerDiagnosticCount
        rxAudioLevelerBoostSlewLimitedSampleCount = $rxAudioLevelerBoostSlewLimitedCount
        rxAudioLevelerPeakLimitedSampleCount = $rxAudioLevelerPeakLimitedCount
        rxAudioLevelerOutputLimitedSampleCount = $rxAudioLevelerOutputLimitedCount
        rxAudioLevelerInputRmsDbfs = $rxAudioLevelerInputRmsStats
        rxAudioLevelerOutputRmsDbfs = $rxAudioLevelerOutputRmsStats
        rxAudioLevelerInputPeakDbfs = $rxAudioLevelerInputPeakStats
        rxAudioLevelerOutputPeakDbfs = $rxAudioLevelerOutputPeakStats
        rxAudioLevelerDesiredGainDb = $rxAudioLevelerDesiredGainStats
        rxAudioLevelerAppliedGainDb = $rxAudioLevelerAppliedGainStats
        rxAudioLevelerGainDeltaDb = $rxAudioLevelerGainDeltaStats
        rxAudioLevelerPeakHeadroomDb = $rxAudioLevelerPeakHeadroomStats
        rxAudioLevelerPreLimitPeakDbfs = $rxAudioLevelerPreLimitPeakStats
        rxAudioLevelerOutputLimitReductionDb = $rxAudioLevelerOutputLimitReductionStats
        rxAudioLevelerOutputLimitSampleCount = $rxAudioLevelerOutputLimitSampleCountStats
        rxAudioLevelerPauseHoldBlocks = $rxAudioLevelerPauseHoldBlockStats
        rxAudioLevelerNr5SpeechHoldBlocks = $rxAudioLevelerNr5SpeechHoldBlockStats
        rxAudioLevelerWatch = [ordered]@{
            diagnosticSampleCount = $rxAudioLevelerDiagnosticCount
            constrainedSampleCount = $rxAudioLevelerConstrainedCount
            constrainedPct = $rxAudioLevelerConstrainedPct
            boostSlewLimitedSampleCount = $rxAudioLevelerBoostSlewLimitedCount
            boostSlewLimitedPct = $rxAudioLevelerBoostSlewLimitedPct
            peakLimitedSampleCount = $rxAudioLevelerPeakLimitedCount
            peakLimitedPct = $rxAudioLevelerPeakLimitedPct
            outputLimitedSampleCount = $rxAudioLevelerOutputLimitedCount
            outputLimitedPct = $rxAudioLevelerOutputLimitedPct
            settlingMovementThresholdDb = 4.0
            capReductionThresholdDb = 1.0
            capSampleThreshold = 8.0
            topBoostSlewLimitedSamples = @($rxAudioLevelerBoostSlewLimitedTopSamples)
            topPeakLimitedSamples = @($rxAudioLevelerPeakLimitedTopSamples)
            topOutputLimitedSamples = @($rxAudioLevelerOutputLimitedTopSamples)
        }
        monitorBacklogSamples = $backlogStats
        frontendSceneAgeMs = $frontendSceneAgeStats
        frontendTopPeakWatch = [ordered]@{
            sampleCount = $frontendTopPeakSampleCount
            nearPassbandSampleCount = $frontendNearPassbandPeakSampleCount
            nearPassbandThresholdHz = [int]$frontendNearPassbandThresholdHz
            filterPassbandSampleCount = $frontendFilterPassbandPeakSampleCount
            filterPassbandEdgeToleranceHz = [int]$frontendFilterPassbandEdgeToleranceHz
            filterLowHz = $rxChainFilterLowHz
            filterHighHz = $rxChainFilterHighHz
            topPeakCount = $frontendTopPeakCountStats
            nearestOffsetHz = $frontendNearestTopPeakOffsetStats
            nearestAbsOffsetHz = $frontendNearestTopPeakAbsOffsetStats
            nearestFilterPassbandDistanceHz = $frontendNearestFilterPassbandDistanceStats
            strongestSnrDb = $frontendStrongestTopPeakSnrStats
            topSamples = @($frontendTopPeakTopSamples)
            topNearPassbandSamples = @($frontendNearPassbandPeakTopSamples)
            topFilterPassbandSamples = @($frontendFilterPassbandPeakTopSamples)
            tuneCandidates = @($frontendTuneCandidates)
        }
        frontendTuneCandidates = @($frontendTuneCandidates)
        frontendAdjacentNoiseProfile = [ordered]@{
            usableSampleCount = $frontendAdjacentNoiseUsableCount
            bins = $frontendAdjacentNoiseBinStats
            floorDb = $frontendAdjacentNoiseFloorStats
            p50Db = $frontendAdjacentNoiseP50Stats
            slopeDbPerKhz = $frontendAdjacentNoiseSlopeStats
            rejectedPct = $frontendAdjacentNoiseRejectedStats
        }
        nr5AdjacentNoiseProfile = [ordered]@{
            usableSampleCount = $nr5AdjacentNoiseUsableCount
            driveSampleCount = $nr5AdjacentNoiseDriveCount
            trust = $nr5AdjacentNoiseTrustStats
            drive = $nr5AdjacentNoiseDriveStats
            floorDb = $nr5AdjacentNoiseFloorStats
            rejectedPct = $nr5AdjacentNoiseRejectedStats
            sideBalance = $nr5AdjacentNoiseSideBalanceStats
            asymmetryDb = $nr5AdjacentNoiseAsymmetryStats
        }
        rxMetersAgeMs = $rxMetersAgeStats
        audioAgeMs = $audioAgeStats
        requestedNrModeCounts = @(ConvertTo-CountArray $requestedNrModeCounts)
        effectiveNrModeCounts = @(ConvertTo-CountArray $effectiveNrModeCounts)
        nrModeMismatchSampleCount = $nrModeMismatchSampleCount
        nrOffRequestedSampleCount = $nrOffRequestedSampleCount
        nrOffEffectiveSampleCount = $nrOffEffectiveSampleCount
        nr5RequestedSampleCount = $nr5RequestedSampleCount
        nr5EffectiveSampleCount = $nr5EffectiveSampleCount
        nr5SampleCount = $nr5SampleCount
        nr5AlignedSampleCount = $nr5AlignedCount
        nr5AgcDiagnosticSampleCount = $nr5AgcDiagnosticCount
        nr5ProbabilityDiagnosticSampleCount = $nr5ProbabilityDiagnosticCount
        nr5PeakDiagnosticSampleCount = $nr5PeakDiagnosticCount
        comparisonStateReadiness = [ordered]@{
            comparisonId = if ([string]::IsNullOrWhiteSpace($ComparisonId)) { $null } else { $ComparisonId }
            strict = $comparisonStateStrict
            ready = $comparisonStateReady
            status = $comparisonStateStatus
            nextAction = $comparisonStateNextAction
            okSampleCount = $okCount
            requestedNrModeCounts = @(ConvertTo-CountArray $requestedNrModeCounts)
            effectiveNrModeCounts = @(ConvertTo-CountArray $effectiveNrModeCounts)
            nrModeMismatchSampleCount = $nrModeMismatchSampleCount
            nrOffRequestedSampleCount = $nrOffRequestedSampleCount
            nrOffEffectiveSampleCount = $nrOffEffectiveSampleCount
            nr5RequestedSampleCount = $nr5RequestedSampleCount
            nr5EffectiveSampleCount = $nr5EffectiveSampleCount
            nr5SampleCount = $nr5SampleCount
            nr5AlignedSampleCount = $nr5AlignedCount
            nr5AgcDiagnosticSampleCount = $nr5AgcDiagnosticCount
            nr5ProbabilityDiagnosticSampleCount = $nr5ProbabilityDiagnosticCount
            nr5PeakDiagnosticSampleCount = $nr5PeakDiagnosticCount
        }
        nr5InputDbfs = $nr5InputStats
        nr5OutputDbfs = $nr5OutputStats
        nr5OutputMinusInputDb = $nr5OutputMinusInputStats
        nr5InputToOutput = $nr5InputOutputStats
        nr5MeanGain = $nr5MeanGainStats
        nr5FloorReductionDb = $nr5FloorReductionStats
        nr5DynamicRangeDb = $nr5DynamicRangeStats
        nr5SignalConfidence = $nr5SignalConfidenceStats
        nr5AgcGate = $nr5AgcGateStats
        nr5SignalProbability = $nr5SignalProbabilityStats
        nr5TextureFill = $nr5TextureFillStats
        nr5MaskSmoothing = $nr5MaskSmoothingStats
        nr5LevelDrive = $nr5LevelDriveStats
        nr5RecoveryDrive = $nr5RecoveryDriveStats
        nr5WeakSignalMemory = $nr5WeakSignalMemoryStats
        nr5MakeupGainDb = $nr5MakeupGainDbStats
        nr5OutputPeakDbfs = $nr5OutputPeakDbfsStats
        nr5PeakEvidence = $nr5PeakEvidenceStats
        nr5PeakLimitDbfs = $nr5PeakLimitDbfsStats
        nr5PeakReductionDb = $nr5PeakReductionDbStats
        nr5LearnerStabilityWatch = [ordered]@{
            status = $nr5LearnerStabilityStatus
            learnerMonotonic = $nr5LearnerMonotonic
            managedReplayEvidenceReady = $nr5ManagedReplayEvidenceReady
            nr5SampleCount = $nr5SampleCount
            learnedFrameSampleCount = $nr5LearnedFrameSampleCount
            learnedFrameCoveragePct = $nr5LearnedFrameCoveragePct
            managedCounterSampleCount = $nr5ManagedCounterSampleCount
            managedCounterCoveragePct = $nr5ManagedCounterCoveragePct
            learnerResetSampleCount = $nr5LearnerResetSampleCount
            managedReapplySampleCount = $nr5ManagedReapplySampleCount
            managedPositionReapplySampleCount = $nr5ManagedPositionReapplyCount
            managedPolicyReapplySampleCount = $nr5ManagedPolicyReapplyCount
            managedGenerationChangeSampleCount = $nr5ManagedGenerationChangeCount
            learnedFrames = $nr5LearnedFrameStats
            managedChannelGeneration = $nr5ManagedChannelGenerationStats
            managedNr5ApplyCount = $nr5ManagedApplyCountStats
            managedNr5PositionApplyCount = $nr5ManagedPositionApplyCountStats
            managedNr5PolicyApplyCount = $nr5ManagedPolicyApplyCountStats
            managedNr5NoopApplyCount = $nr5ManagedNoopApplyCountStats
            managedNr5RunApplyCount = $nr5ManagedRunApplyCountStats
            topLearnerResetSamples = @($nr5LearnerResetTopSamples)
            topManagedReapplySamples = @($nr5ManagedReapplyTopSamples)
            topManagedGenerationChangeSamples = @($nr5ManagedGenerationChangeTopSamples)
        }
        nr5AudioAlignmentWatch = [ordered]@{
            comparableSampleCount = $nr5AudioAlignmentSampleCount
            mismatchThresholdDb = 12.0
            mismatchSampleCount = $nr5AudioAlignmentMismatchCount
            mismatchPct = $nr5AudioAlignmentMismatchPct
            alignedAfterLevelerSampleCount = $nr5AudioAlignedAfterLevelerCount
            alignedAfterLevelerPct = $nr5AudioAlignedAfterLevelerPct
            levelerNormalizedSampleCount = $nr5AudioLevelerNormalizedCount
            levelerNormalizedPct = $nr5AudioLevelerNormalizedPct
            nr5OutputToLevelerInputDeltaDb = $nr5AudioInputDeltaStats
            nr5OutputToLevelerOutputDeltaDb = $nr5AudioOutputDeltaStats
            topMismatches = @($nr5AudioAlignmentMismatchTopSamples)
            topLevelerNormalizedSamples = @($nr5AudioLevelerNormalizedTopSamples)
        }
        nr5WeakSignalWatch = [ordered]@{
            weakInputThresholdDbfs = -30.0
            weakInputSampleCount = $nr5WeakInputCount
            lowEvidenceWeakInputSampleCount = $nr5LowEvidenceWeakInputCount
            evidenceQualifiedWeakInputSampleCount = $nr5EvidenceQualifiedWeakInputCount
            minimumMixedWeakInputSampleCount = $nr5MinimumMixedWeakInputSampleCount
            minimumMixedStrongInputSampleCount = $nr5MinimumMixedStrongInputSampleCount
            minimumMixedEvidenceQualifiedWeakInputSampleCount = $nr5MinimumMixedEvidenceQualifiedWeakInputSampleCount
            minimumQualifiedWeakStrongInputSampleCount = $nr5MinimumQualifiedWeakStrongInputSampleCount
            weakInputSampleDeficit = [Math]::Max(0, $nr5MinimumMixedWeakInputSampleCount - $nr5WeakInputCount)
            evidenceQualifiedWeakInputSampleDeficit = [Math]::Max(0, $nr5MinimumMixedEvidenceQualifiedWeakInputSampleCount - $nr5EvidenceQualifiedWeakInputCount)
            weakRecoveredSampleCount = $nr5WeakRecoveredCount
            weakNearTargetSampleCount = $nr5WeakNearTargetCount
            weakDropoutSampleCount = $nr5WeakDropoutCount
            weakDropoutFinalAudibleThresholdDbfs = $nr5WeakDropoutFinalAudibleThresholdDbfs
            weakDropoutFinalAudibleSampleCount = $nr5WeakDropoutFinalAudibleCount
            weakDropoutFinalAudiblePct = $nr5WeakDropoutFinalAudiblePct
            weakDropoutNativeLiftThresholdDb = $nr5WeakDropoutNativeLiftThresholdDb
            weakDropoutNativeLiftedSampleCount = $nr5WeakDropoutNativeLiftedCount
            weakDropoutNativeLiftedPct = $nr5WeakDropoutNativeLiftedPct
            weakDropoutBelowInputThresholdDb = $nr5WeakDropoutBelowInputThresholdDb
            weakDropoutCandidateLossSampleCount = $nr5WeakDropoutCandidateLossCount
            weakDropoutCandidateLossPct = $nr5WeakDropoutCandidateLossPct
            weakBelowInputSampleCount = $nr5WeakBelowInputCount
            strongInputThresholdDbfs = $nr5StrongInputThresholdDbfs
            strongInputSampleCount = $nr5StrongInputCount
            strongInputSampleDeficit = [Math]::Max(0, $nr5MinimumMixedStrongInputSampleCount - $nr5StrongInputCount)
            nearStrongInputThresholdDbfs = $nr5NearStrongInputThresholdDbfs
            nearStrongInputSampleCount = $nr5NearStrongInputCount
            weakOutputDbfs = $nr5WeakOutputStats
            strongOutputDbfs = $nr5StrongOutputStats
            nearStrongOutputDbfs = $nr5NearStrongOutputStats
            weakStrongOutputGapDb = $nr5WeakStrongOutputGapDb
            weakStrongOutputGapThresholdDb = $nr5MixedWeakStrongGapThresholdDb
            weakFinalAudioDbfs = $nr5WeakFinalAudioStats
            strongFinalAudioDbfs = $nr5StrongFinalAudioStats
            nearStrongFinalAudioDbfs = $nr5NearStrongFinalAudioStats
            weakStrongFinalAudioGapDb = $nr5WeakStrongFinalAudioGapDb
            weakStrongFinalAudioGapThresholdDb = $nr5MixedWeakStrongFinalAudioGapThresholdDb
            speechQualifiedWeakInputSampleCount = $nr5SpeechQualifiedWeakOutputValues.Count
            speechQualifiedStrongInputSampleCount = $nr5SpeechQualifiedStrongOutputValues.Count
            speechQualifiedNearStrongInputSampleCount = $nr5SpeechQualifiedNearStrongOutputValues.Count
            speechQualifiedWeakInputSampleDeficit = [Math]::Max(0, $nr5MinimumQualifiedWeakStrongInputSampleCount - $nr5SpeechQualifiedWeakOutputValues.Count)
            speechQualifiedStrongInputSampleDeficit = [Math]::Max(0, $nr5MinimumQualifiedWeakStrongInputSampleCount - $nr5SpeechQualifiedStrongOutputValues.Count)
            speechQualifiedWeakOutputDbfs = $nr5SpeechQualifiedWeakOutputStats
            speechQualifiedStrongOutputDbfs = $nr5SpeechQualifiedStrongOutputStats
            speechQualifiedNearStrongOutputDbfs = $nr5SpeechQualifiedNearStrongOutputStats
            speechQualifiedWeakStrongOutputGapDb = $nr5SpeechQualifiedWeakStrongOutputGapDb
            speechQualifiedWeakFinalAudioDbfs = $nr5SpeechQualifiedWeakFinalAudioStats
            speechQualifiedStrongFinalAudioDbfs = $nr5SpeechQualifiedStrongFinalAudioStats
            speechQualifiedNearStrongFinalAudioDbfs = $nr5SpeechQualifiedNearStrongFinalAudioStats
            speechQualifiedWeakStrongFinalAudioGapDb = $nr5SpeechQualifiedWeakStrongFinalAudioGapDb
            speechQualifiedMixedWeakStrongEvidenceReady = $nr5SpeechQualifiedWeakStrongEvidenceReady
            speechQualifiedWeakStrongOutputParityReady = $nr5SpeechQualifiedWeakStrongOutputParityReady
            speechQualifiedWeakStrongFinalAudioParityReady = $nr5SpeechQualifiedWeakStrongFinalAudioParityReady
            speechQualifiedMixedWeakStrongEvidenceStatus = $nr5SpeechQualifiedWeakStrongEvidenceStatus
            passbandQualifiedWeakInputSampleCount = $nr5PassbandQualifiedWeakOutputValues.Count
            passbandQualifiedStrongInputSampleCount = $nr5PassbandQualifiedStrongOutputValues.Count
            passbandQualifiedNearStrongInputSampleCount = $nr5PassbandQualifiedNearStrongOutputValues.Count
            passbandQualifiedWeakInputSampleDeficit = [Math]::Max(0, $nr5MinimumQualifiedWeakStrongInputSampleCount - $nr5PassbandQualifiedWeakOutputValues.Count)
            passbandQualifiedStrongInputSampleDeficit = [Math]::Max(0, $nr5MinimumQualifiedWeakStrongInputSampleCount - $nr5PassbandQualifiedStrongOutputValues.Count)
            passbandQualifiedWeakOutputDbfs = $nr5PassbandQualifiedWeakOutputStats
            passbandQualifiedStrongOutputDbfs = $nr5PassbandQualifiedStrongOutputStats
            passbandQualifiedNearStrongOutputDbfs = $nr5PassbandQualifiedNearStrongOutputStats
            passbandQualifiedWeakStrongOutputGapDb = $nr5PassbandQualifiedWeakStrongOutputGapDb
            passbandQualifiedWeakFinalAudioDbfs = $nr5PassbandQualifiedWeakFinalAudioStats
            passbandQualifiedStrongFinalAudioDbfs = $nr5PassbandQualifiedStrongFinalAudioStats
            passbandQualifiedNearStrongFinalAudioDbfs = $nr5PassbandQualifiedNearStrongFinalAudioStats
            passbandQualifiedWeakStrongFinalAudioGapDb = $nr5PassbandQualifiedWeakStrongFinalAudioGapDb
            passbandQualifiedMixedWeakStrongEvidenceReady = $nr5PassbandQualifiedWeakStrongEvidenceReady
            passbandQualifiedWeakStrongOutputParityReady = $nr5PassbandQualifiedWeakStrongOutputParityReady
            passbandQualifiedWeakStrongFinalAudioParityReady = $nr5PassbandQualifiedWeakStrongFinalAudioParityReady
            passbandQualifiedMixedWeakStrongEvidenceStatus = $nr5PassbandQualifiedWeakStrongEvidenceStatus
            mixedWeakStrongEvidenceReady = $nr5MixedWeakStrongEvidenceReady
            weakStrongOutputParityReady = $nr5WeakStrongOutputParityReady
            weakStrongFinalAudioParityReady = $nr5WeakStrongFinalAudioParityReady
            mixedWeakStrongEvidenceStatus = $nr5MixedWeakStrongEvidenceStatus
            mixedWeakStrongTuningFocus = $nr5MixedWeakStrongTuningFocus
            normalizationCompressionDb = $nr5NormalizationCompressionDb
            hotMakeupThresholdDb = 12.0
            hotMakeupSampleCount = $nr5HotMakeupCount
            normalizationMotionIsFloorSuppression = $nr5NormalizationMotionIsFloorSuppression
            topWeakInputs = @($nr5WeakInputTopSamples)
            topStrongInputs = @($nr5StrongInputTopSamples)
            topSpeechQualifiedWeakInputs = @($nr5SpeechQualifiedWeakInputTopSamples)
            topSpeechQualifiedStrongInputs = @($nr5SpeechQualifiedStrongInputTopSamples)
            topPassbandQualifiedWeakInputs = @($nr5PassbandQualifiedWeakInputTopSamples)
            topPassbandQualifiedStrongInputs = @($nr5PassbandQualifiedStrongInputTopSamples)
            topWeakDropouts = @($nr5WeakDropoutTopSamples)
            topFinalAudibleWeakDropouts = @($nr5WeakDropoutFinalAudibleTopSamples)
            topCandidateWeakLosses = @($nr5WeakDropoutCandidateLossTopSamples)
            topHotMakeup = @($nr5HotMakeupTopSamples)
            topNearStrongInputs = @($nr5NearStrongInputTopSamples)
        }
        nr5LowEvidenceLiftWatch = [ordered]@{
            weakInputThresholdDbfs = $nr5LowEvidenceInputThresholdDbfs
            signalConfidenceThreshold = $nr5LowEvidenceConfidenceThreshold
            signalProbabilityThreshold = $nr5LowEvidenceProbabilityThreshold
            agcGateThreshold = $nr5LowEvidenceAgcGateThreshold
            outputThresholdDbfs = $nr5LowEvidenceOutputThresholdDbfs
            audioThresholdDbfs = $nr5LowEvidenceAudioThresholdDbfs
            lowEvidenceSampleCount = $nr5LowEvidenceSampleCount
            liftedSampleCount = $nr5LowEvidenceLiftCount
            liftedPct = $nr5LowEvidenceLiftPct
            alignmentMismatchSampleCount = $nr5LowEvidenceAlignmentMismatchCount
            alignmentMismatchPct = $nr5LowEvidenceAlignmentMismatchPct
            suppressedSampleCount = $nr5LowEvidenceSuppressedCount
            suppressedPct = $nr5LowEvidenceSuppressedPct
            suppressionDominates = $nr5LowEvidenceSuppressionDominates
            topLiftedSamples = @($nr5LowEvidenceLiftTopSamples)
            topSuppressedSamples = @($nr5LowEvidenceSuppressedTopSamples)
        }
        statusCounts = @(ConvertTo-CountArray $statusCounts)
        qualityToneCounts = @(ConvertTo-CountArray $toneCounts)
        runtimeStatusCounts = @(ConvertTo-CountArray $runtimeStatusCounts)
        audioStatusCounts = @(ConvertTo-CountArray $audioStatusCounts)
        nr5TuningStatusCounts = @(ConvertTo-CountArray $nr5TuningStatusCounts)
        nr5TuningConstraintCounts = @(ConvertTo-CountArray $nr5TuningConstraintCounts)
        constraintCounts = @(ConvertTo-CountArray $constraintCounts)
        hardConstraintCounts = @(ConvertTo-CountArray $hardConstraintCounts)
        recommendations = @($summaryRecommendations.ToArray())
        liveRecommendedActions = @($recommendations.ToArray())
        sampleSummaries = @($sampleSummaries.ToArray())
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
if ($RealtimeEvery -lt 1) {
    throw "RealtimeEvery must be at least 1."
}

$base = Normalize-BaseUrl $BaseUrl
$endpoint = "$base/api/dsp/live-diagnostics"

Invoke-PwshRelaunchIfNeeded -ScriptPath $PSCommandPath

if ($SkipCertificateCheck) {
    Enable-CertificateBypass
}

$effectiveTuneStepHz = Resolve-TuneStepHz `
    -BaseUrl $base `
    -RequestedTuneStepHz $TuneStepHz `
    -AllowServerLookup ([string]::IsNullOrWhiteSpace($InputPath) -and -not $PlanOnly)

if ($PlanOnly) {
    [ordered]@{
        schemaVersion = 1
        tool = "watch-dsp-live-diagnostics"
        mode = "plan-only"
        endpoint = $endpoint
        samples = $Samples
        intervalMs = $IntervalMs
        tuneStepHz = $effectiveTuneStepHz
        label = if ([string]::IsNullOrWhiteSpace($Label)) { $null } else { $Label }
        scenarioId = if ([string]::IsNullOrWhiteSpace($ScenarioId)) { $null } else { $ScenarioId }
        comparisonId = if ([string]::IsNullOrWhiteSpace($ComparisonId)) { $null } else { $ComparisonId }
        outputs = @(
            "JSONL per-sample diagnostics trace",
            "JSON summary with runtime evidence, blockers, and AGC/audio/headroom movement",
            "captureReadinessWatch with top hard/soft preflight constraints and mapped action items",
            "Optional -Preflight and -FailOnHardGate exit codes for CI/operator capture gating",
            "NR5-specific input/output, confidence, gate, level-drive, recovery-drive, makeup-gain, audio-alignment mismatch, weak/strong normalization, and live tuning-readiness trends when NR5 diagnostics are present"
        )
        example = "powershell -NoProfile -ExecutionPolicy Bypass -File tools\watch-dsp-live-diagnostics.ps1 -BaseUrl $base -Samples 60 -IntervalMs 1000 -TuneStepHz $effectiveTuneStepHz -Label g2-nr5-weak-cw"
        preflightExample = "powershell -NoProfile -ExecutionPolicy Bypass -File tools\watch-dsp-live-diagnostics.ps1 -BaseUrl $base -Samples 15 -IntervalMs 500 -TuneStepHz $effectiveTuneStepHz -Preflight -Label g2-preflight"
        desktopExample = "powershell -NoProfile -ExecutionPolicy Bypass -File tools\watch-dsp-live-diagnostics.ps1 -BaseUrl https://localhost:6443 -SkipCertificateCheck -Samples 60 -IntervalMs 500 -TuneStepHz $effectiveTuneStepHz -Label nr5-live"
        realtimeExample = "powershell -NoProfile -ExecutionPolicy Bypass -File tools\watch-dsp-live-diagnostics.ps1 -BaseUrl https://localhost:6443 -SkipCertificateCheck -Samples 120 -IntervalMs 250 -TuneStepHz $effectiveTuneStepHz -Realtime -RealtimeEvery 4 -Label nr5-live-tune"
        offlineExample = "powershell -NoProfile -ExecutionPolicy Bypass -File tools\watch-dsp-live-diagnostics.ps1 -InputPath captures\dsp-live-diagnostics\trace.jsonl -JsonOnly"
        notes = @(
            "Read-only: the tool only calls GET /api/dsp/live-diagnostics.",
            "Use traces as runtime context beside offline fixture metrics, audio renders, spectrum captures, and operator notes.",
            "Use nr5AudioAlignmentWatch to reject mixed-frame rows before treating low-evidence lift/dropout rows as DSP behavior.",
            "Frontend tune candidates are snapped to -TuneStepHz; raw/exact FFT-bin-derived targets remain in rawSuggested* and exactSuggested* fields.",
            "-Preflight exits nonzero when samples fail, hard blockers appear, or readyForBenchmarkTrace is false.",
            "-FailOnHardGate exits nonzero only for failed samples or hard blockers.",
            "A ready trace does not approve changing DSP defaults by itself."
        )
    } | ConvertTo-Json -Depth 16
    exit 0
}

$repoRoot = Get-RepoRoot
$startedUtc = [DateTimeOffset]::UtcNow
$safeLabel = ConvertTo-SafeName $Label

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "captures\dsp-live-diagnostics"
}

if ([string]::IsNullOrWhiteSpace($InputPath)) {
    $stamp = $startedUtc.ToString("yyyyMMddTHHmmssfffZ")
    if ([string]::IsNullOrWhiteSpace($safeLabel)) {
        $captureName = $stamp
    }
    else {
        $captureName = "$stamp-$safeLabel"
    }

    $captureDir = Join-Path $OutputRoot $captureName
    if ([string]::IsNullOrWhiteSpace($ReportPath)) {
        $ReportPath = Join-Path $captureDir "live-diagnostics-watch.json"
    }
    if ([string]::IsNullOrWhiteSpace($JsonlPath)) {
        $JsonlPath = Join-Path $captureDir "live-diagnostics-trace.jsonl"
    }

    $sampleRecords = Invoke-LiveSamples -Endpoint $endpoint -Count $Samples -DelayMs $IntervalMs -RequestTimeoutSec $TimeoutSec -LinePath $JsonlPath
    $completedUtc = [DateTimeOffset]::UtcNow
    $report = Build-Report `
        -SampleRecords $sampleRecords `
        -SourceMode "live-endpoint" `
        -Endpoint $endpoint `
        -SourcePath "" `
        -LinePath $JsonlPath `
        -Label $Label `
        -ScenarioId $ScenarioId `
        -ComparisonId $ComparisonId `
        -StartedUtc $startedUtc `
        -CompletedUtc $completedUtc `
        -TuneStepHz $effectiveTuneStepHz
}
else {
    $resolvedInputPath = (Resolve-Path -LiteralPath $InputPath).Path
    if ([string]::IsNullOrWhiteSpace($ReportPath)) {
        $inputDir = Split-Path -Parent $resolvedInputPath
        $inputName = [System.IO.Path]::GetFileNameWithoutExtension($resolvedInputPath)
        $ReportPath = Join-Path $inputDir "$inputName.summary.json"
    }

    $sampleRecords = Read-InputSamples -Path $resolvedInputPath
    $completedUtc = [DateTimeOffset]::UtcNow
    $report = Build-Report `
        -SampleRecords $sampleRecords `
        -SourceMode "input-file" `
        -Endpoint $endpoint `
        -SourcePath $resolvedInputPath `
        -LinePath $resolvedInputPath `
        -Label $Label `
        -ScenarioId $ScenarioId `
        -ComparisonId $ComparisonId `
        -StartedUtc $startedUtc `
        -CompletedUtc $completedUtc `
        -TuneStepHz $effectiveTuneStepHz
}

Write-JsonFile -Path $ReportPath -Value $report

if ($JsonOnly) {
    $report | ConvertTo-Json -Depth 48
}
else {
    Write-Host "DSP live diagnostics watch summary: $ReportPath"
    if (-not [string]::IsNullOrWhiteSpace($JsonlPath)) {
        Write-Host "Trace: $JsonlPath"
    }
    Write-Host "Status: $($report["trendStatus"])"
    Write-Host "Capture readiness: $($report["captureReadinessWatch"]["status"]) (hardGatePass=$($report["captureReadinessWatch"]["hardGatePass"]), strictPreflightPass=$($report["captureReadinessWatch"]["strictPreflightPass"]))"
    Write-Host "NR5 tuning: $($report["nr5TuningTraceStatus"]) ($($report["nr5TuningReadySampleCount"])/$($report["okSampleCount"]) ready)"
    $learnerWatch = $report["nr5LearnerStabilityWatch"]
    if ($null -ne $learnerWatch) {
        Write-Host "NR5 learner: $($learnerWatch["status"]), learned frames $($learnerWatch["learnedFrames"]["min"])..$($learnerWatch["learnedFrames"]["max"]), position applies movement $($learnerWatch["managedNr5PositionApplyCount"]["movement"]), policy applies movement $($learnerWatch["managedNr5PolicyApplyCount"]["movement"])"
    }
    Write-Host "Samples: $($report["okSampleCount"]) ok, $($report["failedSampleCount"]) failed, $($report["hardBlockerSampleCount"]) with hard blockers"
    Write-Host "AGC movement dB: $($report["agcGainDb"]["movement"]) ($($report["agcStabilityWatch"]["status"])), audio RMS movement dB: $($report["audioRmsDbfs"]["movement"]), min ADC headroom dB: $($report["adcHeadroomDb"]["min"])"
    Write-Host "NR5 normalization: input movement dB $($report["nr5InputDbfs"]["movement"]), output movement dB $($report["nr5OutputDbfs"]["movement"]), weak/strong output gap dB $($report["nr5WeakSignalWatch"]["weakStrongOutputGapDb"]), weak/strong final-audio gap dB $($report["nr5WeakSignalWatch"]["weakStrongFinalAudioGapDb"]), mixed weak/strong status $($report["nr5WeakSignalWatch"]["mixedWeakStrongEvidenceStatus"])"
    Write-Host "NR5 qualified parity: speech final gap dB $($report["nr5WeakSignalWatch"]["speechQualifiedWeakStrongFinalAudioGapDb"]) ($($report["nr5WeakSignalWatch"]["speechQualifiedMixedWeakStrongEvidenceStatus"])), passband final gap dB $($report["nr5WeakSignalWatch"]["passbandQualifiedWeakStrongFinalAudioGapDb"]) ($($report["nr5WeakSignalWatch"]["passbandQualifiedMixedWeakStrongEvidenceStatus"]))"
    $tuneCandidates = @($report["frontendTuneCandidates"])
    if ($tuneCandidates.Count -gt 0) {
        $candidateText = @($tuneCandidates | Select-Object -First 5 | ForEach-Object {
            $snap = Get-JsonValue $_ "tuneSnapDeltaHz"
            $snapText = if ($null -eq $snap -or [long]$snap -eq 0) { "step" } else { "step, snap $snap Hz" }
            "$($_["suggestedVfoMhz"]) MHz ($snapText; peak $($_["peakFrequencyMhz"]) MHz, off $($_["peakOffsetHz"]) Hz, snr $($_["snrDb"]) dB)"
        }) -join "; "
        Write-Host "Frontend tune candidates: $candidateText"
    }
    $weakWatch = $report["nr5WeakSignalWatch"]
    if ($null -ne $weakWatch) {
        Write-Host "NR5 weak outcome: weak=$($weakWatch["weakInputSampleCount"]), recovered=$($weakWatch["weakRecoveredSampleCount"]), dropouts=$($weakWatch["weakDropoutSampleCount"]), finalAudible=$($weakWatch["weakDropoutFinalAudibleSampleCount"]), candidateLoss=$($weakWatch["weakDropoutCandidateLossSampleCount"]), hotMakeup=$($weakWatch["hotMakeupSampleCount"])"
        $mixedFocus = $weakWatch["mixedWeakStrongTuningFocus"]
        if ($null -ne $mixedFocus) {
            $outputGapExcessText = if ($null -eq $mixedFocus["outputGapExcessDb"]) { "n/a" } else { "$($mixedFocus["outputGapExcessDb"]) dB" }
            $finalAudioGapExcessText = if ($null -eq $mixedFocus["finalAudioGapExcessDb"]) { "n/a" } else { "$($mixedFocus["finalAudioGapExcessDb"]) dB" }
            Write-Host "NR5 mixed focus: action=$($mixedFocus["preferredAction"]), weak=$($mixedFocus["weakInputSampleCount"]), strong=$($mixedFocus["strongInputSampleCount"]), outputGapExcess=$outputGapExcessText ($($mixedFocus["outputGapDirection"])), finalAudioGapExcess=$finalAudioGapExcessText ($($mixedFocus["finalAudioGapDirection"])), speechFinalGap=$($mixedFocus["speechQualifiedFinalAudioGapDb"]) ($($mixedFocus["speechQualifiedStatus"])), passbandFinalGap=$($mixedFocus["passbandQualifiedFinalAudioGapDb"]) ($($mixedFocus["passbandQualifiedStatus"]))"
        }
    }
    $levelerWatch = $report["rxAudioLevelerWatch"]
    if ($null -ne $levelerWatch) {
        Write-Host "RX leveler safety: constrained=$($levelerWatch["constrainedSampleCount"]), boostSlew=$($levelerWatch["boostSlewLimitedSampleCount"]), peakLimited=$($levelerWatch["peakLimitedSampleCount"]), outputLimited=$($levelerWatch["outputLimitedSampleCount"])"
    }
}

$hardGatePass = Test-Truthy $report["captureReadinessWatch"]["hardGatePass"]
$strictPreflightPass = Test-Truthy $report["captureReadinessWatch"]["strictPreflightPass"]

if (-not $ContinueOnError -and [int]$report["okSampleCount"] -eq 0) {
    exit 1
}
if (-not $ContinueOnError -and $Preflight -and -not $strictPreflightPass) {
    exit 2
}
if (-not $ContinueOnError -and $FailOnHardGate -and -not $hardGatePass) {
    exit 2
}
