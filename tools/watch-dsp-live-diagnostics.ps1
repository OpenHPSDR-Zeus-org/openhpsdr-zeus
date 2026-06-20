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

function ConvertTo-LongFileSystemPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    if ([System.IO.Path]::DirectorySeparatorChar -ne "\") {
        return $Path
    }
    if ($Path.StartsWith("\\?\", [StringComparison]::Ordinal)) {
        return $Path
    }
    if ($Path.StartsWith("\\", [StringComparison]::Ordinal)) {
        return "\\?\UNC\" + $Path.Substring(2)
    }
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return "\\?\" + $Path
    }

    return $Path
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

    return Read-JsonText -Text ([System.IO.File]::ReadAllText((ConvertTo-LongFileSystemPath -Path $Path))) -Source $Path
}

function Write-JsonFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Value
    )

    $parent = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        [System.IO.Directory]::CreateDirectory((ConvertTo-LongFileSystemPath -Path $parent)) | Out-Null
    }

    $json = $Value | ConvertTo-Json -Depth 48
    [System.IO.File]::WriteAllText(
        (ConvertTo-LongFileSystemPath -Path $Path),
        $json,
        [System.Text.Encoding]::UTF8)
}

function Add-JsonLine {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Value
    )

    $parent = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        [System.IO.Directory]::CreateDirectory((ConvertTo-LongFileSystemPath -Path $parent)) | Out-Null
    }

    $json = ($Value | ConvertTo-Json -Depth 48 -Compress) + [Environment]::NewLine
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    $lastError = $null
    for ($attempt = 0; $attempt -lt 8; $attempt++) {
        try {
            $stream = [System.IO.File]::Open(
                (ConvertTo-LongFileSystemPath -Path $Path),
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
            $currentVfo = Get-NumericValue (Get-JsonValue $sample "currentVfoHz")
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
                currentVfoHz = if ($null -eq $currentVfo) { $null } else { [long][Math]::Round([double]$currentVfo) }
                retuneDeltaHz = if ($null -eq $currentVfo) { $null } else { [long]($suggestedVfoHz - [long][Math]::Round([double]$currentVfo)) }
                exactRetuneDeltaHz = if ($null -eq $currentVfo) { $null } else { [long]($rawSuggestedVfoHz - [long][Math]::Round([double]$currentVfo)) }
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

function New-CandidateClassifiedInputSample {
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
        candidateAudioAlignmentMismatch = $AudioAlignmentMismatch
        candidateAudioInputDeltaDb = Get-NumericValue $AudioInputDeltaDb
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
        "candidate" { return "Off" }
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

function Test-CandidateLevelerNormalizedSpeechAlignment {
    param(
        $CandidateOutputDbfs,
        $LevelerInputRmsDbfs,
        $LevelerOutputRmsDbfs,
        $LevelerAppliedGainDb,
        [bool]$LevelerBoostSlewLimited,
        [bool]$LevelerPeakLimited,
        [bool]$LevelerOutputLimited
    )

    $candidateOutput = Get-NumericValue $CandidateOutputDbfs
    $levelerInput = Get-NumericValue $LevelerInputRmsDbfs
    $levelerOutput = Get-NumericValue $LevelerOutputRmsDbfs
    if ($null -eq $candidateOutput -or $null -eq $levelerInput -or $null -eq $levelerOutput) {
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
        $candidateOutput -ge -43.0 -and
        $candidateOutput -le -30.0 -and
        $levelerInput -le -42.0 -and
        $levelerOutput -ge -23.5 -and
        $levelerOutput -le -15.0 -and
        ($levelerOutput - $candidateOutput) -ge 12.0 -and
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
        "candidate-under-test-exports-missing" { return "Recapture this comparison with the current runtime; this stale trace references diagnostics that are not available now." }
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
        "candidate-under-test-exports-missing",
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

    $resolved = [System.IO.Path]::GetFullPath($Path)
    if (-not [System.IO.File]::Exists((ConvertTo-LongFileSystemPath -Path $resolved))) {
        throw "Input path does not exist: $Path"
    }

    $records = New-Object System.Collections.Generic.List[object]
    $extension = [System.IO.Path]::GetExtension($resolved)

    if ($extension -ieq ".jsonl") {
        $index = 0
        foreach ($line in [System.IO.File]::ReadLines((ConvertTo-LongFileSystemPath -Path $resolved))) {
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
    $candidate = Get-JsonValue $diagnostics "candidateDspDiagnostics"
    $candidateInputDbfs = Get-NumericValue (Get-JsonValue $candidate "inputDbfs")
    $candidateOutputDbfs = Get-NumericValue (Get-JsonValue $candidate "outputDbfs")
    $candidateOutputMinusInputDb = $null
    if ($null -ne $candidateInputDbfs -and $null -ne $candidateOutputDbfs) {
        $candidateOutputMinusInputDb = [Math]::Round($candidateOutputDbfs - $candidateInputDbfs, 1)
    }
    $runtimeLevelerInputRmsDbfs = Get-NumericValue (Get-JsonValue $runtime "rxAudioLevelerInputRmsDbfs")
    $runtimeLevelerOutputRmsDbfs = Get-NumericValue (Get-JsonValue $runtime "rxAudioLevelerOutputRmsDbfs")
    $runtimeLevelerAppliedGainDb = Get-NumericValue (Get-JsonValue $runtime "rxAudioLevelerAppliedGainDb")
    $rxAudioLevelerBoostSlewLimited = Test-Truthy (Get-JsonValue $runtime "rxAudioLevelerBoostSlewLimited")
    $rxAudioLevelerPeakLimited = Test-Truthy (Get-JsonValue $runtime "rxAudioLevelerPeakLimited")
    $rxAudioLevelerOutputLimited = Test-Truthy (Get-JsonValue $runtime "rxAudioLevelerOutputLimited")
    $candidateAudioInputDeltaDb = $null
    if ($null -ne $candidateOutputDbfs -and $null -ne $runtimeLevelerInputRmsDbfs) {
        $candidateAudioInputDeltaDb = [Math]::Round($runtimeLevelerInputRmsDbfs - $candidateOutputDbfs, 1)
    }
    $candidateAudioOutputDeltaDb = $null
    if ($null -ne $candidateOutputDbfs -and $null -ne $runtimeLevelerOutputRmsDbfs) {
        $candidateAudioOutputDeltaDb = [Math]::Round($runtimeLevelerOutputRmsDbfs - $candidateOutputDbfs, 1)
    }
    $candidateAudioAlignedAfterLeveler = ($null -ne $candidateAudioOutputDeltaDb -and [Math]::Abs($candidateAudioOutputDeltaDb) -le 12.0)
    $candidateAudioLevelerNormalized = Test-CandidateLevelerNormalizedSpeechAlignment `
        -CandidateOutputDbfs $candidateOutputDbfs `
        -LevelerInputRmsDbfs $runtimeLevelerInputRmsDbfs `
        -LevelerOutputRmsDbfs $runtimeLevelerOutputRmsDbfs `
        -LevelerAppliedGainDb $runtimeLevelerAppliedGainDb `
        -LevelerBoostSlewLimited $rxAudioLevelerBoostSlewLimited `
        -LevelerPeakLimited $rxAudioLevelerPeakLimited `
        -LevelerOutputLimited $rxAudioLevelerOutputLimited
    $candidateAudioAlignmentMismatch = $false
    if ($null -ne $candidateAudioInputDeltaDb -and [Math]::Abs($candidateAudioInputDeltaDb) -gt 12.0 -and
        -not $candidateAudioAlignedAfterLeveler -and
        -not $candidateAudioLevelerNormalized) {
        $candidateAudioAlignmentMismatch = $true
    }
    $candidateTuning = Get-CandidateTuningReadiness $diagnostics
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
        readyForCandidateTuning = Test-Truthy $candidateTuning["ready"]
        candidateTuningStatus = [string]$candidateTuning["status"]
        candidateTuningReadinessSource = [string]$candidateTuning["source"]
        candidateTuningConstraints = @($candidateTuning["constraints"])
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
        rxAudioLevelerCandidateSpeechHoldBlocks = Get-JsonValue $runtime "rxAudioLevelerCandidateSpeechHoldBlocks"
        rxAudioLevelerCandidateSpeechHangoverBlocks = Get-JsonValue $runtime "rxAudioLevelerCandidateSpeechHangoverBlocks"
        rxAudioLevelerCandidateHybridSpeechPrior = Get-JsonValue $runtime "rxAudioLevelerCandidateHybridSpeechPrior"
        rxAudioLevelerCandidateNoSignalNoisePrior = Get-JsonValue $runtime "rxAudioLevelerCandidateNoSignalNoisePrior"
        rxAudioLevelerCandidateNoiseProfilePrior = Get-JsonValue $runtime "rxAudioLevelerCandidateNoiseProfilePrior"
        rxAudioLevelerCandidateNoSignalNoiseCap = Get-JsonValue $runtime "rxAudioLevelerCandidateNoSignalNoiseCap"
        rxAudioLevelerCandidateFarPeakNoiseCap = Get-JsonValue $runtime "rxAudioLevelerCandidateFarPeakNoiseCap"
        rxAudioLevelerCandidateNoProofNoiseCap = Get-JsonValue $runtime "rxAudioLevelerCandidateNoProofNoiseCap"
        rxAudioLevelerBoostSlewLimited = Get-JsonValue $runtime "rxAudioLevelerBoostSlewLimited"
        rxAudioLevelerPeakLimited = Get-JsonValue $runtime "rxAudioLevelerPeakLimited"
        rxAudioLevelerOutputLimited = Get-JsonValue $runtime "rxAudioLevelerOutputLimited"
        rxAudioLevelerRequestedProfile = Get-JsonValue $runtime "rxAudioLevelerRequestedProfile"
        rxAudioLevelerActiveProfile = Get-JsonValue $runtime "rxAudioLevelerActiveProfile"
        rxAudioLevelerExperimental = Get-JsonValue $runtime "rxAudioLevelerExperimental"
        rxAudioLevelerControlRmsValid = Get-JsonValue $runtime "rxAudioLevelerControlRmsValid"
        rxAudioLevelerControlRmsDbfs = Get-JsonValue $runtime "rxAudioLevelerControlRmsDbfs"
        rxAudioLevelerControlRmsHangDb = Get-JsonValue $runtime "rxAudioLevelerControlRmsHangDb"
        txOutputHeadroomRequestedProfile = Get-JsonValue $runtime "txOutputHeadroomRequestedProfile"
        txOutputHeadroomActiveProfile = Get-JsonValue $runtime "txOutputHeadroomActiveProfile"
        txOutputHeadroomExperimental = Get-JsonValue $runtime "txOutputHeadroomExperimental"
        txOutputHeadroomTrimDb = Get-JsonValue $runtime "txOutputHeadroomTrimDb"
        txOutputHeadroomPureSignalBypassed = Get-JsonValue $runtime "txOutputHeadroomPureSignalBypassed"
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
        candidateLearnedFrames = Get-JsonValue $candidate "learnedFrames"
        candidateInputDbfs = $candidateInputDbfs
        candidateOutputDbfs = $candidateOutputDbfs
        candidateOutputMinusInputDb = $candidateOutputMinusInputDb
        candidateAudioInputDeltaDb = $candidateAudioInputDeltaDb
        candidateAudioOutputDeltaDb = $candidateAudioOutputDeltaDb
        candidateAudioAlignedAfterLeveler = $candidateAudioAlignedAfterLeveler
        candidateAudioLevelerNormalized = $candidateAudioLevelerNormalized
        candidateAudioAlignmentMismatch = $candidateAudioAlignmentMismatch
        candidateMeanGain = Get-JsonValue $candidate "meanGain"
        candidateSignalConfidence = Get-JsonValue $candidate "signalConfidence"
        candidateAgcGate = Get-JsonValue $candidate "agcGate"
        candidateSignalProbability = Get-JsonValue $candidate "signalProbability"
        candidateTextureFill = Get-JsonValue $candidate "textureFill"
        candidateMaskSmoothing = Get-JsonValue $candidate "maskSmoothing"
        candidateLevelDrive = Get-JsonValue $candidate "levelDrive"
        candidateRecoveryDrive = Get-JsonValue $candidate "recoveryDrive"
        candidateWeakSignalMemory = Get-JsonValue $candidate "weakSignalMemory"
        candidateMakeupGainDb = Get-JsonValue $candidate "makeupGainDb"
        candidateOutputPeakDbfs = Get-JsonValue $candidate "outputPeakDbfs"
        candidatePeakEvidence = Get-JsonValue $candidate "peakEvidence"
        candidatePeakLimitDbfs = Get-JsonValue $candidate "peakLimitDbfs"
        candidatePeakReductionDb = Get-JsonValue $candidate "peakReductionDb"
        candidateManagedChannelGeneration = Get-JsonValue $candidate "managedChannelGeneration"
        candidateManagedCandidateApplyCount = Get-JsonValue $candidate "managedCandidateApplyCount"
        candidateManagedCandidatePositionApplyCount = Get-JsonValue $candidate "managedCandidatePositionApplyCount"
        candidateManagedCandidatePolicyApplyCount = Get-JsonValue $candidate "managedCandidatePolicyApplyCount"
        candidateManagedCandidateNoopApplyCount = Get-JsonValue $candidate "managedCandidateNoopApplyCount"
        candidateManagedCandidateRunApplyCount = Get-JsonValue $candidate "managedCandidateRunApplyCount"
        candidateManagedCandidateLastApplyReason = [string](Get-JsonValue $candidate "managedCandidateLastApplyReason")
        candidateAdjacentNoiseUsable = Get-JsonValue $candidate "adjacentNoiseUsable"
        candidateAdjacentNoiseBins = Get-JsonValue $candidate "adjacentNoiseBins"
        candidateAdjacentNoiseFloorDb = Get-JsonValue $candidate "adjacentNoiseFloorDb"
        candidateAdjacentNoiseTrust = Get-JsonValue $candidate "adjacentNoiseTrust"
        candidateAdjacentNoiseDrive = Get-JsonValue $candidate "adjacentNoiseDrive"
        candidateAdjacentNoiseRejectedPct = Get-JsonValue $candidate "adjacentNoiseRejectedPct"
        candidateAdjacentNoiseLeftBins = Get-JsonValue $candidate "adjacentNoiseLeftBins"
        candidateAdjacentNoiseRightBins = Get-JsonValue $candidate "adjacentNoiseRightBins"
        candidateAdjacentNoiseLeftFloorDb = Get-JsonValue $candidate "adjacentNoiseLeftFloorDb"
        candidateAdjacentNoiseRightFloorDb = Get-JsonValue $candidate "adjacentNoiseRightFloorDb"
        candidateAdjacentNoiseSideBalance = Get-JsonValue $candidate "adjacentNoiseSideBalance"
        candidateAdjacentNoiseAsymmetryDb = Get-JsonValue $candidate "adjacentNoiseAsymmetryDb"
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
    $candidateReadyText = if (Test-Truthy $summary["readyForCandidateTuning"]) { "candidate-tune=yes" } else { "candidate-tune=no" }
    $benchmarkText = if (Test-Truthy $summary["readyForLiveBenchmark"]) { "bench=yes" } else { "bench=no" }
    $weakText = ""
    $inputDb = Get-NumericValue $summary["candidateInputDbfs"]
    $outputDb = Get-NumericValue $summary["candidateOutputDbfs"]
    if ($null -ne $inputDb -and $null -ne $outputDb) {
        $delta = Get-NumericValue $summary["candidateOutputMinusInputDb"]
        $weakText = " in=$([Math]::Round($inputDb, 1)) out=$([Math]::Round($outputDb, 1)) delta=$delta"
    }
    $makeup = Get-NumericValue $summary["candidateMakeupGainDb"]
    $makeupText = if ($null -eq $makeup) { "" } else { " makeup=$([Math]::Round($makeup, 1))dB" }
    $probability = Get-NumericValue $summary["candidateSignalProbability"]
    $probabilityText = if ($null -eq $probability) { "" } else { " prob=$([Math]::Round($probability, 2))" }
    $memory = Get-NumericValue $summary["candidateWeakSignalMemory"]
    $memoryText = if ($null -eq $memory) { "" } else { " mem=$([Math]::Round($memory, 2))" }
    $peakReduction = Get-NumericValue $summary["candidatePeakReductionDb"]
    $peakText = if ($null -eq $peakReduction) { "" } else { " peakRed=$([Math]::Round($peakReduction, 1))dB" }
    $adjacentTrust = Get-NumericValue $summary["candidateAdjacentNoiseTrust"]
    $adjacentDrive = Get-NumericValue $summary["candidateAdjacentNoiseDrive"]
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
        $candidateReadyText,
        $benchmarkText,
        $weakText,
        $makeupText,
        $probabilityText,
        $memoryText,
        $peakText,
        $adjacentText,
        $levelerText)
}

function Get-CandidateTuningReadiness {
    param($Diagnostics)

    $endpointReady = Get-JsonValue $Diagnostics "readyForCandidateTuning"
    $endpointStatus = [string](Get-JsonValue $Diagnostics "candidateTuningStatus")
    $endpointConstraints = @(Get-JsonArray $Diagnostics "candidateTuningConstraints")
    if ($null -ne $endpointReady -or -not [string]::IsNullOrWhiteSpace($endpointStatus) -or $endpointConstraints.Count -gt 0) {
        return [ordered]@{
            ready = Test-Truthy $endpointReady
            status = if ([string]::IsNullOrWhiteSpace($endpointStatus)) { "candidate-tuning-watch" } else { $endpointStatus }
            constraints = $endpointConstraints
            source = "endpoint"
        }
    }

    $constraints = New-Object System.Collections.Generic.List[string]
    $requested = [string](Get-JsonValue $Diagnostics "requestedNrMode")
    $effective = [string](Get-JsonValue $Diagnostics "effectiveNrMode")
    $candidate = Get-JsonValue $Diagnostics "candidateDspDiagnostics"
    $runtime = Get-JsonValue $Diagnostics "runtimeEvidence"

    if ($requested -ne "Off") {
        $constraints.Add("candidate-not-requested") | Out-Null
    }
    if ($effective -ne "Off") {
        $constraints.Add("candidate-not-effective") | Out-Null
    }
    if ($null -ne $candidate) {
        if (-not (Test-Truthy (Get-JsonValue $candidate "run"))) {
            $constraints.Add("candidate-not-running") | Out-Null
        }
        $learned = Get-NumericValue (Get-JsonValue $candidate "learnedFrames")
        if ($null -eq $learned -or $learned -lt 20) {
            $constraints.Add("candidate-learning") | Out-Null
        }
        $agcRunValue = Get-JsonValue $candidate "agcRun"
        if ($null -ne $agcRunValue -and -not (Test-Truthy $agcRunValue)) {
            $constraints.Add("candidate-agc-disabled") | Out-Null
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
    $ready = ($uniqueConstraints.Count -eq 0)
    return [ordered]@{
        ready = $ready
        status = if ($ready) { "candidate-preflight-ready" } else { "candidate-tuning-preflight-required" }
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
    $candidateTuningStatusCounts = @{}
    $candidateTuningConstraintCounts = @{}
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
    $rxAudioLevelerCandidateSpeechHoldBlockValues = New-Object System.Collections.Generic.List[double]
    $rxAudioLevelerCandidateSpeechHangoverBlockValues = New-Object System.Collections.Generic.List[double]
    $rxAudioLevelerCandidateHybridSpeechPriorValues = New-Object System.Collections.Generic.List[double]
    $rxAudioLevelerCandidateNoSignalNoisePriorValues = New-Object System.Collections.Generic.List[double]
    $rxAudioLevelerCandidateNoiseProfilePriorValues = New-Object System.Collections.Generic.List[double]
    $rxAudioLevelerProfileCounts = @{}
    $txOutputHeadroomRequestedProfileCounts = @{}
    $txOutputHeadroomActiveProfileCounts = @{}
    $txOutputHeadroomTrimValues = New-Object System.Collections.Generic.List[double]
    $rxAudioLevelerControlRmsValues = New-Object System.Collections.Generic.List[double]
    $rxAudioLevelerControlRmsHangValues = New-Object System.Collections.Generic.List[double]
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
    $candidateInputValues = New-Object System.Collections.Generic.List[double]
    $candidateOutputValues = New-Object System.Collections.Generic.List[double]
    $candidateInputOutputXValues = New-Object System.Collections.Generic.List[double]
    $candidateInputOutputYValues = New-Object System.Collections.Generic.List[double]
    $candidateOutputMinusInputValues = New-Object System.Collections.Generic.List[double]
    $candidateWeakOutputValues = New-Object System.Collections.Generic.List[double]
    $candidateStrongOutputValues = New-Object System.Collections.Generic.List[double]
    $candidateWeakFinalAudioValues = New-Object System.Collections.Generic.List[double]
    $candidateStrongFinalAudioValues = New-Object System.Collections.Generic.List[double]
    $candidateNearStrongOutputValues = New-Object System.Collections.Generic.List[double]
    $candidateNearStrongFinalAudioValues = New-Object System.Collections.Generic.List[double]
    $candidateSpeechQualifiedWeakOutputValues = New-Object System.Collections.Generic.List[double]
    $candidateSpeechQualifiedStrongOutputValues = New-Object System.Collections.Generic.List[double]
    $candidateSpeechQualifiedNearStrongOutputValues = New-Object System.Collections.Generic.List[double]
    $candidateSpeechQualifiedNearStrongFinalAudioValues = New-Object System.Collections.Generic.List[double]
    $candidateSpeechQualifiedWeakFinalAudioValues = New-Object System.Collections.Generic.List[double]
    $candidateSpeechQualifiedStrongFinalAudioValues = New-Object System.Collections.Generic.List[double]
    $candidatePassbandQualifiedWeakOutputValues = New-Object System.Collections.Generic.List[double]
    $candidatePassbandQualifiedStrongOutputValues = New-Object System.Collections.Generic.List[double]
    $candidatePassbandQualifiedNearStrongOutputValues = New-Object System.Collections.Generic.List[double]
    $candidatePassbandQualifiedNearStrongFinalAudioValues = New-Object System.Collections.Generic.List[double]
    $candidatePassbandQualifiedWeakFinalAudioValues = New-Object System.Collections.Generic.List[double]
    $candidatePassbandQualifiedStrongFinalAudioValues = New-Object System.Collections.Generic.List[double]
    $candidateMeanGainValues = New-Object System.Collections.Generic.List[double]
    $candidateFloorReductionValues = New-Object System.Collections.Generic.List[double]
    $candidateDynamicRangeValues = New-Object System.Collections.Generic.List[double]
    $candidateSignalConfidenceValues = New-Object System.Collections.Generic.List[double]
    $candidateAgcGateValues = New-Object System.Collections.Generic.List[double]
    $candidateSignalProbabilityValues = New-Object System.Collections.Generic.List[double]
    $candidateTextureFillValues = New-Object System.Collections.Generic.List[double]
    $candidateMaskSmoothingValues = New-Object System.Collections.Generic.List[double]
    $candidateLevelDriveValues = New-Object System.Collections.Generic.List[double]
    $candidateRecoveryDriveValues = New-Object System.Collections.Generic.List[double]
    $candidateWeakSignalMemoryValues = New-Object System.Collections.Generic.List[double]
    $candidateMakeupGainDbValues = New-Object System.Collections.Generic.List[double]
    $candidateOutputPeakDbfsValues = New-Object System.Collections.Generic.List[double]
    $candidatePeakEvidenceValues = New-Object System.Collections.Generic.List[double]
    $candidatePeakLimitDbfsValues = New-Object System.Collections.Generic.List[double]
    $candidatePeakReductionDbValues = New-Object System.Collections.Generic.List[double]
    $candidateAdjacentNoiseTrustValues = New-Object System.Collections.Generic.List[double]
    $candidateAdjacentNoiseDriveValues = New-Object System.Collections.Generic.List[double]
    $candidateAdjacentNoiseFloorValues = New-Object System.Collections.Generic.List[double]
    $candidateAdjacentNoiseRejectedValues = New-Object System.Collections.Generic.List[double]
    $candidateAdjacentNoiseSideBalanceValues = New-Object System.Collections.Generic.List[double]
    $candidateAdjacentNoiseAsymmetryValues = New-Object System.Collections.Generic.List[double]
    $candidateAudioInputDeltaValues = New-Object System.Collections.Generic.List[double]
    $candidateAudioOutputDeltaValues = New-Object System.Collections.Generic.List[double]
    $candidateLearnedFrameValues = New-Object System.Collections.Generic.List[double]
    $candidateManagedChannelGenerationValues = New-Object System.Collections.Generic.List[double]
    $candidateManagedApplyCountValues = New-Object System.Collections.Generic.List[double]
    $candidateManagedPositionApplyCountValues = New-Object System.Collections.Generic.List[double]
    $candidateManagedPolicyApplyCountValues = New-Object System.Collections.Generic.List[double]
    $candidateManagedNoopApplyCountValues = New-Object System.Collections.Generic.List[double]
    $candidateManagedRunApplyCountValues = New-Object System.Collections.Generic.List[double]
    $sampleSummaries = New-Object System.Collections.Generic.List[object]
    $recommendations = New-Object System.Collections.Generic.List[string]
    $candidateWeakInputSamples = New-Object System.Collections.Generic.List[object]
    $candidateStrongInputSamples = New-Object System.Collections.Generic.List[object]
    $candidateSpeechQualifiedWeakInputSamples = New-Object System.Collections.Generic.List[object]
    $candidateSpeechQualifiedStrongInputSamples = New-Object System.Collections.Generic.List[object]
    $candidatePassbandQualifiedWeakInputSamples = New-Object System.Collections.Generic.List[object]
    $candidatePassbandQualifiedStrongInputSamples = New-Object System.Collections.Generic.List[object]
    $candidateWeakDropoutSamples = New-Object System.Collections.Generic.List[object]
    $candidateWeakDropoutFinalAudibleSamples = New-Object System.Collections.Generic.List[object]
    $candidateWeakDropoutCandidateLossSamples = New-Object System.Collections.Generic.List[object]
    $candidateHotMakeupSamples = New-Object System.Collections.Generic.List[object]
    $candidateNearStrongInputSamples = New-Object System.Collections.Generic.List[object]
    $candidateLowEvidenceLiftSamples = New-Object System.Collections.Generic.List[object]
    $candidateLowEvidenceSuppressedSamples = New-Object System.Collections.Generic.List[object]
    $candidateAudioAlignmentMismatchSamples = New-Object System.Collections.Generic.List[object]
    $candidateAudioLevelerNormalizedSamples = New-Object System.Collections.Generic.List[object]
    $candidateLearnerResetSamples = New-Object System.Collections.Generic.List[object]
    $candidateManagedReapplySamples = New-Object System.Collections.Generic.List[object]
    $candidateManagedGenerationChangeSamples = New-Object System.Collections.Generic.List[object]
    $rxAudioLevelerBoostSlewLimitedSamples = New-Object System.Collections.Generic.List[object]
    $rxAudioLevelerPeakLimitedSamples = New-Object System.Collections.Generic.List[object]
    $rxAudioLevelerOutputLimitedSamples = New-Object System.Collections.Generic.List[object]
    $rxAudioLevelerConstrainedSamples = New-Object System.Collections.Generic.List[object]
    $rxAudioLevelerCandidateNoSignalNoiseCapSamples = New-Object System.Collections.Generic.List[object]
    $rxAudioLevelerNormalStrengthControlRmsSamples = New-Object System.Collections.Generic.List[object]
    $candidateSpeechContinuitySamples = New-Object System.Collections.Generic.List[object]
    $candidateSpeechContinuityOutputValues = New-Object System.Collections.Generic.List[double]
    $candidateSpeechContinuityGainValues = New-Object System.Collections.Generic.List[double]
    $frontendTopPeakSamples = New-Object System.Collections.Generic.List[object]
    $frontendNearPassbandPeakSamples = New-Object System.Collections.Generic.List[object]
    $frontendFilterPassbandPeakSamples = New-Object System.Collections.Generic.List[object]
    $passbandAudioSamples = New-Object System.Collections.Generic.List[object]
    $offPassbandAudioSamples = New-Object System.Collections.Generic.List[object]
    $candidateFrontendStrongPassbandSubthresholdSamples = New-Object System.Collections.Generic.List[object]
    $candidateFrontendStrongPassbandSubthresholdDistanceValues = New-Object System.Collections.Generic.List[double]
    $frontendTopPeakSampleCount = 0
    $frontendNearPassbandPeakSampleCount = 0
    $frontendFilterPassbandPeakSampleCount = 0
    $frontendAdjacentNoiseUsableCount = 0
    $candidateAdjacentNoiseUsableCount = 0
    $candidateAdjacentNoiseDriveCount = 0

    $candidateLowEvidenceInputThresholdDbfs = -30.0
    $candidateLowEvidenceConfidenceThreshold = 0.32
    $candidateLowEvidenceProbabilityThreshold = 0.18
    $candidateLowEvidenceAgcGateThreshold = 0.50
    $candidateLowEvidenceOutputThresholdDbfs = -28.0
    $candidateLowEvidenceAudioThresholdDbfs = -20.0
    $signalFloorAudioThresholdDbfs = -70.0
    $signalActiveAudioThresholdDbfs = -45.0
    $signalEvidenceConfidenceThreshold = 0.30
    $signalEvidenceProbabilityThreshold = 0.16
    $signalEvidenceAgcGateThreshold = 0.30
    $candidateSpeechContinuityHybridThreshold = 0.25
    $candidateSpeechContinuityNoSignalMax = 0.18
    $candidateSpeechContinuityFadeThresholdDbfs = -28.0
    $candidateSpeechContinuityDropoutThresholdDbfs = -45.0
    $candidateSpeechContinuityOutputMovementThresholdDb = 6.0
    $candidateSpeechContinuityGainMovementThresholdDb = 8.0
    $rxAudioLevelerNormalStrengthControlRmsThresholdDbfs = -24.0
    $frontendNearPassbandThresholdHz = 3000.0
    $frontendFilterPassbandEdgeToleranceHz = 0.0
    $frontendStrongPassbandSnrThresholdDb = 20.0
    $frontendStrongPassbandDbfsThreshold = -85.0
    $candidateStrongInputThresholdDbfs = -22.0
    $candidateNearStrongInputThresholdDbfs = -26.0
    $candidateWeakDropoutFinalAudibleThresholdDbfs = $signalActiveAudioThresholdDbfs
    $candidateWeakDropoutNativeLiftThresholdDb = 6.0
    $candidateWeakDropoutBelowInputThresholdDb = 1.0

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
    $rxAudioLevelerCandidateNoSignalNoiseCapCount = 0
    $rxAudioLevelerCandidateFarPeakNoiseCapCount = 0
    $rxAudioLevelerCandidateNoProofNoiseCapCount = 0
    $rxAudioLevelerExperimentalSampleCount = 0
    $rxAudioLevelerControlRmsValidCount = 0
    $rxAudioLevelerNormalStrengthControlRmsValidCount = 0
    $txOutputHeadroomExperimentalSampleCount = 0
    $txOutputHeadroomPureSignalBypassedSampleCount = 0
    $candidateSpeechContinuitySampleCount = 0
    $candidateSpeechContinuityFadeCount = 0
    $candidateSpeechContinuityDropoutCount = 0
    $rxAudioLevelerBoostSlewLimitedCount = 0
    $rxAudioLevelerPeakLimitedCount = 0
    $rxAudioLevelerOutputLimitedCount = 0
    $hardBlockerSampleCount = 0
    $candidateSampleCount = 0
    $candidateAlignedCount = 0
    $candidateTuningReadyCount = 0
    $candidateAgcDiagnosticCount = 0
    $candidateProbabilityDiagnosticCount = 0
    $candidatePeakDiagnosticCount = 0
    $candidateLearnedFrameSampleCount = 0
    $candidateManagedCounterSampleCount = 0
    $candidateManagedPositionReapplyCount = 0
    $candidateManagedPolicyReapplyCount = 0
    $candidateManagedGenerationChangeCount = 0
    $candidateAudioAlignmentSampleCount = 0
    $candidateAudioAlignedAfterLevelerCount = 0
    $candidateAudioLevelerNormalizedCount = 0
    $candidateAudioAlignmentMismatchCount = 0
    $candidateWeakInputCount = 0
    $candidateWeakRecoveredCount = 0
    $candidateWeakDropoutCount = 0
    $candidateWeakDropoutFinalAudibleCount = 0
    $candidateWeakDropoutNativeLiftedCount = 0
    $candidateWeakDropoutCandidateLossCount = 0
    $candidateWeakBelowInputCount = 0
    $candidateWeakNearTargetCount = 0
    $candidateStrongInputCount = 0
    $candidateNearStrongInputCount = 0
    $candidateHotMakeupCount = 0
    $candidateRequestedSampleCount = 0
    $candidateEffectiveSampleCount = 0
    $nrOffRequestedSampleCount = 0
    $nrOffEffectiveSampleCount = 0
    $nrModeMismatchSampleCount = 0
    $candidateLowEvidenceSampleCount = 0
    $candidateLowEvidenceLiftCount = 0
    $candidateLowEvidenceAlignmentMismatchCount = 0
    $candidateLowEvidenceSuppressedCount = 0
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
    $candidatePreviousLearnedFrames = $null
    $candidatePreviousManagedChannelGeneration = $null
    $candidatePreviousManagedApplyCount = $null
    $candidatePreviousManagedPositionApplyCount = $null
    $candidatePreviousManagedPolicyApplyCount = $null

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
        $candidate = Get-JsonValue $diagnostics "candidateDspDiagnostics"
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
        if (Test-NrModeName $requestedNrMode "Off") {
            $candidateRequestedSampleCount++
        }
        if (Test-NrModeName $effectiveNrMode "Off") {
            $candidateEffectiveSampleCount++
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
        $candidateTuning = Get-CandidateTuningReadiness $diagnostics
        if (Test-Truthy $candidateTuning["ready"]) {
            $candidateTuningReadyCount++
        }
        Add-Count $candidateTuningStatusCounts ([string]$candidateTuning["status"])
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
                currentVfoHz = $sampleRadioVfoHz
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
        foreach ($constraint in @($candidateTuning["constraints"])) {
            Add-Count $candidateTuningConstraintCounts ([string]$constraint)
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

        $candidateConfidenceNumber = $null
        $candidateAgcGateNumber = $null
        $candidateSignalProbabilityNumber = $null
        $candidateTextureFillNumber = $null
        $candidateMaskSmoothingNumber = $null
        $candidateRecoveryDriveNumber = $null
        $candidateWeakSignalMemoryNumber = $null
        $candidateMakeupGainDbNumber = $null
        $candidatePeakEvidenceNumber = $null

        if ($null -ne $candidate) {
            $candidateSampleCount++
            $candidateLearnedFramesNumber = Get-NumericValue (Get-JsonValue $candidate "learnedFrames")
            $candidateManagedChannelGenerationNumber = Get-NumericValue (Get-JsonValue $candidate "managedChannelGeneration")
            $candidateManagedApplyCountNumber = Get-NumericValue (Get-JsonValue $candidate "managedCandidateApplyCount")
            $candidateManagedPositionApplyCountNumber = Get-NumericValue (Get-JsonValue $candidate "managedCandidatePositionApplyCount")
            $candidateManagedPolicyApplyCountNumber = Get-NumericValue (Get-JsonValue $candidate "managedCandidatePolicyApplyCount")
            $candidateManagedNoopApplyCountNumber = Get-NumericValue (Get-JsonValue $candidate "managedCandidateNoopApplyCount")
            $candidateManagedRunApplyCountNumber = Get-NumericValue (Get-JsonValue $candidate "managedCandidateRunApplyCount")
            $candidateManagedLastApplyReason = [string](Get-JsonValue $candidate "managedCandidateLastApplyReason")
            Add-Number $candidateLearnedFrameValues $candidateLearnedFramesNumber
            Add-Number $candidateManagedChannelGenerationValues $candidateManagedChannelGenerationNumber
            Add-Number $candidateManagedApplyCountValues $candidateManagedApplyCountNumber
            Add-Number $candidateManagedPositionApplyCountValues $candidateManagedPositionApplyCountNumber
            Add-Number $candidateManagedPolicyApplyCountValues $candidateManagedPolicyApplyCountNumber
            Add-Number $candidateManagedNoopApplyCountValues $candidateManagedNoopApplyCountNumber
            Add-Number $candidateManagedRunApplyCountValues $candidateManagedRunApplyCountNumber
            if ($null -ne $candidateLearnedFramesNumber) {
                $candidateLearnedFrameSampleCount++
            }
            $sampleHasManagedCounters = (
                $null -ne $candidateManagedPositionApplyCountNumber -and
                $null -ne $candidateManagedPolicyApplyCountNumber)
            if ($sampleHasManagedCounters) {
                $candidateManagedCounterSampleCount++
            }
            $candidateSameManagedGeneration = $true
            if ($null -ne $candidatePreviousManagedChannelGeneration -and $null -ne $candidateManagedChannelGenerationNumber -and
                [double]$candidateManagedChannelGenerationNumber -ne [double]$candidatePreviousManagedChannelGeneration) {
                $candidateSameManagedGeneration = $false
                $candidateManagedGenerationChangeCount++
                $candidateManagedGenerationChangeSamples.Add([ordered]@{
                    sampleIndex = [int](Get-JsonValue $sample "sampleIndex")
                    sampledUtc = Get-JsonValue $sample "sampledUtc"
                    previousGeneration = [int][Math]::Round([double]$candidatePreviousManagedChannelGeneration)
                    generation = [int][Math]::Round([double]$candidateManagedChannelGenerationNumber)
                    previousLearnedFrames = if ($null -ne $candidatePreviousLearnedFrames) { [int][Math]::Round([double]$candidatePreviousLearnedFrames) } else { $null }
                    learnedFrames = if ($null -ne $candidateLearnedFramesNumber) { [int][Math]::Round([double]$candidateLearnedFramesNumber) } else { $null }
                    lastApplyReason = $candidateManagedLastApplyReason
                }) | Out-Null
            }
            if ($null -ne $candidatePreviousLearnedFrames -and $null -ne $candidateLearnedFramesNumber -and
                [double]$candidateLearnedFramesNumber -lt [double]$candidatePreviousLearnedFrames -and
                $candidateSameManagedGeneration) {
                $candidateLearnerResetSamples.Add([ordered]@{
                    sampleIndex = [int](Get-JsonValue $sample "sampleIndex")
                    sampledUtc = Get-JsonValue $sample "sampledUtc"
                    previousLearnedFrames = [int][Math]::Round([double]$candidatePreviousLearnedFrames)
                    learnedFrames = [int][Math]::Round([double]$candidateLearnedFramesNumber)
                    channelGeneration = if ($null -ne $candidateManagedChannelGenerationNumber) { [int][Math]::Round([double]$candidateManagedChannelGenerationNumber) } else { $null }
                    managedCandidateApplyCount = if ($null -ne $candidateManagedApplyCountNumber) { [int][Math]::Round([double]$candidateManagedApplyCountNumber) } else { $null }
                    managedCandidatePositionApplyCount = if ($null -ne $candidateManagedPositionApplyCountNumber) { [int][Math]::Round([double]$candidateManagedPositionApplyCountNumber) } else { $null }
                    managedCandidatePolicyApplyCount = if ($null -ne $candidateManagedPolicyApplyCountNumber) { [int][Math]::Round([double]$candidateManagedPolicyApplyCountNumber) } else { $null }
                    lastApplyReason = $candidateManagedLastApplyReason
                }) | Out-Null
            }
            $candidateManagedApplyDelta = $null
            $candidateManagedPositionApplyDelta = $null
            $candidateManagedPolicyApplyDelta = $null
            if ($candidateSameManagedGeneration) {
                if ($null -ne $candidatePreviousManagedApplyCount -and $null -ne $candidateManagedApplyCountNumber) {
                    $candidateManagedApplyDelta = [double]$candidateManagedApplyCountNumber - [double]$candidatePreviousManagedApplyCount
                }
                if ($null -ne $candidatePreviousManagedPositionApplyCount -and $null -ne $candidateManagedPositionApplyCountNumber) {
                    $candidateManagedPositionApplyDelta = [double]$candidateManagedPositionApplyCountNumber - [double]$candidatePreviousManagedPositionApplyCount
                }
                if ($null -ne $candidatePreviousManagedPolicyApplyCount -and $null -ne $candidateManagedPolicyApplyCountNumber) {
                    $candidateManagedPolicyApplyDelta = [double]$candidateManagedPolicyApplyCountNumber - [double]$candidatePreviousManagedPolicyApplyCount
                }
                if (($null -ne $candidateManagedPositionApplyDelta -and $candidateManagedPositionApplyDelta -gt 0.0) -or
                    ($null -ne $candidateManagedPolicyApplyDelta -and $candidateManagedPolicyApplyDelta -gt 0.0)) {
                    if ($null -ne $candidateManagedPositionApplyDelta -and $candidateManagedPositionApplyDelta -gt 0.0) {
                        $candidateManagedPositionReapplyCount++
                    }
                    if ($null -ne $candidateManagedPolicyApplyDelta -and $candidateManagedPolicyApplyDelta -gt 0.0) {
                        $candidateManagedPolicyReapplyCount++
                    }
                    $candidateManagedReapplySamples.Add([ordered]@{
                        sampleIndex = [int](Get-JsonValue $sample "sampleIndex")
                        sampledUtc = Get-JsonValue $sample "sampledUtc"
                        channelGeneration = if ($null -ne $candidateManagedChannelGenerationNumber) { [int][Math]::Round([double]$candidateManagedChannelGenerationNumber) } else { $null }
                        learnedFrames = if ($null -ne $candidateLearnedFramesNumber) { [int][Math]::Round([double]$candidateLearnedFramesNumber) } else { $null }
                        managedCandidateApplyDelta = if ($null -ne $candidateManagedApplyDelta) { [int][Math]::Round([double]$candidateManagedApplyDelta) } else { $null }
                        managedCandidatePositionApplyDelta = if ($null -ne $candidateManagedPositionApplyDelta) { [int][Math]::Round([double]$candidateManagedPositionApplyDelta) } else { $null }
                        managedCandidatePolicyApplyDelta = if ($null -ne $candidateManagedPolicyApplyDelta) { [int][Math]::Round([double]$candidateManagedPolicyApplyDelta) } else { $null }
                        managedCandidateApplyCount = if ($null -ne $candidateManagedApplyCountNumber) { [int][Math]::Round([double]$candidateManagedApplyCountNumber) } else { $null }
                        managedCandidatePositionApplyCount = if ($null -ne $candidateManagedPositionApplyCountNumber) { [int][Math]::Round([double]$candidateManagedPositionApplyCountNumber) } else { $null }
                        managedCandidatePolicyApplyCount = if ($null -ne $candidateManagedPolicyApplyCountNumber) { [int][Math]::Round([double]$candidateManagedPolicyApplyCountNumber) } else { $null }
                        lastApplyReason = $candidateManagedLastApplyReason
                    }) | Out-Null
                }
            }
            if ($null -ne $candidateLearnedFramesNumber) {
                $candidatePreviousLearnedFrames = $candidateLearnedFramesNumber
            }
            if ($null -ne $candidateManagedChannelGenerationNumber) {
                $candidatePreviousManagedChannelGeneration = $candidateManagedChannelGenerationNumber
            }
            if ($null -ne $candidateManagedApplyCountNumber) {
                $candidatePreviousManagedApplyCount = $candidateManagedApplyCountNumber
            }
            if ($null -ne $candidateManagedPositionApplyCountNumber) {
                $candidatePreviousManagedPositionApplyCount = $candidateManagedPositionApplyCountNumber
            }
            if ($null -ne $candidateManagedPolicyApplyCountNumber) {
                $candidatePreviousManagedPolicyApplyCount = $candidateManagedPolicyApplyCountNumber
            }
            Add-Number $candidateInputValues (Get-JsonValue $candidate "inputDbfs")
            Add-Number $candidateOutputValues (Get-JsonValue $candidate "outputDbfs")
            Add-Number $candidateMeanGainValues (Get-JsonValue $candidate "meanGain")
            Add-Number $candidateFloorReductionValues (Get-JsonValue $candidate "floorReductionDb")
            Add-Number $candidateDynamicRangeValues (Get-JsonValue $candidate "dynamicRangeDb")
            $candidateSignalConfidence = Get-JsonValue $candidate "signalConfidence"
            $candidateAgcGate = Get-JsonValue $candidate "agcGate"
            Add-Number $candidateSignalConfidenceValues $candidateSignalConfidence
            Add-Number $candidateAgcGateValues $candidateAgcGate
            $candidateSignalProbability = Get-JsonValue $candidate "signalProbability"
            $candidateTextureFill = Get-JsonValue $candidate "textureFill"
            $candidateMaskSmoothing = Get-JsonValue $candidate "maskSmoothing"
            Add-Number $candidateSignalProbabilityValues $candidateSignalProbability
            Add-Number $candidateTextureFillValues $candidateTextureFill
            Add-Number $candidateMaskSmoothingValues $candidateMaskSmoothing
            if ($null -ne $candidateSignalProbability -and $null -ne $candidateTextureFill -and $null -ne $candidateMaskSmoothing) {
                $candidateProbabilityDiagnosticCount++
            }
            $candidateLevelDrive = Get-JsonValue $candidate "levelDrive"
            $candidateRecoveryDrive = Get-JsonValue $candidate "recoveryDrive"
            $candidateWeakSignalMemory = Get-JsonValue $candidate "weakSignalMemory"
            $candidateMakeupGainDb = Get-JsonValue $candidate "makeupGainDb"
            Add-Number $candidateLevelDriveValues $candidateLevelDrive
            Add-Number $candidateRecoveryDriveValues $candidateRecoveryDrive
            Add-Number $candidateWeakSignalMemoryValues $candidateWeakSignalMemory
            Add-Number $candidateMakeupGainDbValues $candidateMakeupGainDb
            if ($null -ne $candidateLevelDrive -and $null -ne $candidateRecoveryDrive -and $null -ne $candidateMakeupGainDb) {
                $candidateAgcDiagnosticCount++
            }
            $candidateOutputPeakDbfs = Get-JsonValue $candidate "outputPeakDbfs"
            $candidatePeakEvidence = Get-JsonValue $candidate "peakEvidence"
            $candidatePeakLimitDbfs = Get-JsonValue $candidate "peakLimitDbfs"
            $candidatePeakReductionDb = Get-JsonValue $candidate "peakReductionDb"
            Add-Number $candidateOutputPeakDbfsValues $candidateOutputPeakDbfs
            Add-Number $candidatePeakEvidenceValues $candidatePeakEvidence
            Add-Number $candidatePeakLimitDbfsValues $candidatePeakLimitDbfs
            Add-Number $candidatePeakReductionDbValues $candidatePeakReductionDb
            if ($null -ne $candidateOutputPeakDbfs -and $null -ne $candidatePeakEvidence -and
                $null -ne $candidatePeakLimitDbfs -and $null -ne $candidatePeakReductionDb) {
                $candidatePeakDiagnosticCount++
            }
            $candidateAdjacentNoiseUsable = Get-JsonValue $candidate "adjacentNoiseUsable"
            $candidateAdjacentNoiseTrust = Get-JsonValue $candidate "adjacentNoiseTrust"
            $candidateAdjacentNoiseDrive = Get-JsonValue $candidate "adjacentNoiseDrive"
            $candidateAdjacentNoiseFloor = Get-JsonValue $candidate "adjacentNoiseFloorDb"
            $candidateAdjacentNoiseRejected = Get-JsonValue $candidate "adjacentNoiseRejectedPct"
            $candidateAdjacentNoiseSideBalance = Get-JsonValue $candidate "adjacentNoiseSideBalance"
            $candidateAdjacentNoiseAsymmetry = Get-JsonValue $candidate "adjacentNoiseAsymmetryDb"
            Add-Number $candidateAdjacentNoiseTrustValues $candidateAdjacentNoiseTrust
            Add-Number $candidateAdjacentNoiseDriveValues $candidateAdjacentNoiseDrive
            Add-Number $candidateAdjacentNoiseFloorValues $candidateAdjacentNoiseFloor
            Add-Number $candidateAdjacentNoiseRejectedValues $candidateAdjacentNoiseRejected
            Add-Number $candidateAdjacentNoiseSideBalanceValues $candidateAdjacentNoiseSideBalance
            Add-Number $candidateAdjacentNoiseAsymmetryValues $candidateAdjacentNoiseAsymmetry
            if (Test-Truthy $candidateAdjacentNoiseUsable) {
                $candidateAdjacentNoiseUsableCount++
            }
            $candidateAdjacentNoiseDriveNumber = Get-NumericValue $candidateAdjacentNoiseDrive
            if ($null -ne $candidateAdjacentNoiseDriveNumber -and $candidateAdjacentNoiseDriveNumber -gt 0.001) {
                $candidateAdjacentNoiseDriveCount++
            }

            if ((Test-NrModeName $requestedNrMode "Off") -and (Test-NrModeName $effectiveNrMode "Off") -and
                (Test-Truthy (Get-JsonValue $candidate "run"))) {
                $candidateAlignedCount++
            }

            $candidateInputDbfs = Get-NumericValue (Get-JsonValue $candidate "inputDbfs")
            $candidateOutputDbfs = Get-NumericValue (Get-JsonValue $candidate "outputDbfs")
            $candidateConfidenceNumber = Get-NumericValue $candidateSignalConfidence
            $candidateAgcGateNumber = Get-NumericValue $candidateAgcGate
            $candidateSignalProbabilityNumber = Get-NumericValue $candidateSignalProbability
            $candidateTextureFillNumber = Get-NumericValue $candidateTextureFill
            $candidateMaskSmoothingNumber = Get-NumericValue $candidateMaskSmoothing
            $candidateRecoveryDriveNumber = Get-NumericValue $candidateRecoveryDrive
            $candidateWeakSignalMemoryNumber = Get-NumericValue $candidateWeakSignalMemory
            $candidateMakeupGainDbNumber = Get-NumericValue $candidateMakeupGainDb
            $candidatePeakEvidenceNumber = Get-NumericValue $candidatePeakEvidence
            $runtimeAudioRmsDbfsNumber = Get-NumericValue (Get-JsonValue $runtime "audioRmsDbfs")
            $runtimeIsRxAudio = Test-RuntimeRxAudio $runtime
            $runtimeLevelerInputRmsDbfsNumber = Get-NumericValue (Get-JsonValue $runtime "rxAudioLevelerInputRmsDbfs")
            $runtimeLevelerOutputRmsDbfsNumber = Get-NumericValue (Get-JsonValue $runtime "rxAudioLevelerOutputRmsDbfs")
            $runtimeLevelerAppliedGainDbNumber = Get-NumericValue (Get-JsonValue $runtime "rxAudioLevelerAppliedGainDb")
            $runtimeLevelerPauseHoldBlocksNumber = Get-NumericValue (Get-JsonValue $runtime "rxAudioLevelerPauseHoldBlocks")
            $runtimeLevelerCandidateSpeechHoldBlocksNumber = Get-NumericValue (Get-JsonValue $runtime "rxAudioLevelerCandidateSpeechHoldBlocks")
            $runtimeLevelerCandidateSpeechHangoverBlocksNumber = Get-NumericValue (Get-JsonValue $runtime "rxAudioLevelerCandidateSpeechHangoverBlocks")
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
            $candidateAudioInputDeltaDb = $null
            $candidateAudioOutputDeltaDb = $null
            $candidateAudioAlignedAfterLeveler = $false
            $candidateAudioLevelerNormalized = $false
            $candidateAudioAlignmentMismatch = $false
            if ($null -ne $candidateOutputDbfs -and $null -ne $runtimeLevelerInputRmsDbfsNumber) {
                $candidateAudioInputDeltaDb = [Math]::Round($runtimeLevelerInputRmsDbfsNumber - $candidateOutputDbfs, 1)
                Add-Number $candidateAudioInputDeltaValues $candidateAudioInputDeltaDb
                $candidateAudioAlignmentSampleCount++
            }
            if ($null -ne $candidateOutputDbfs -and $null -ne $runtimeLevelerOutputRmsDbfsNumber) {
                $candidateAudioOutputDeltaDb = [Math]::Round($runtimeLevelerOutputRmsDbfsNumber - $candidateOutputDbfs, 1)
                Add-Number $candidateAudioOutputDeltaValues $candidateAudioOutputDeltaDb
                if ([Math]::Abs($candidateAudioOutputDeltaDb) -le 12.0) {
                    $candidateAudioAlignedAfterLeveler = $true
                    $candidateAudioAlignedAfterLevelerCount++
                }
            }
            $candidateAudioLevelerNormalized = Test-CandidateLevelerNormalizedSpeechAlignment `
                -CandidateOutputDbfs $candidateOutputDbfs `
                -LevelerInputRmsDbfs $runtimeLevelerInputRmsDbfsNumber `
                -LevelerOutputRmsDbfs $runtimeLevelerOutputRmsDbfsNumber `
                -LevelerAppliedGainDb $runtimeLevelerAppliedGainDbNumber `
                -LevelerBoostSlewLimited $rxAudioLevelerBoostSlewLimited `
                -LevelerPeakLimited $rxAudioLevelerPeakLimited `
                -LevelerOutputLimited $rxAudioLevelerOutputLimited
            if ($candidateAudioLevelerNormalized) {
                $candidateAudioLevelerNormalizedCount++
                $candidateAudioLevelerNormalizedSamples.Add([ordered]@{
                    sampleIndex = [int](Get-JsonValue $sample "sampleIndex")
                    candidateOutputDbfs = $candidateOutputDbfs
                    rxAudioLevelerInputRmsDbfs = $runtimeLevelerInputRmsDbfsNumber
                    rxAudioLevelerOutputRmsDbfs = $runtimeLevelerOutputRmsDbfsNumber
                    rxAudioLevelerAppliedGainDb = $runtimeLevelerAppliedGainDbNumber
                    inputDeltaDb = $candidateAudioInputDeltaDb
                    outputDeltaDb = $candidateAudioOutputDeltaDb
                    audioRmsDbfs = $runtimeAudioRmsDbfsNumber
                    audioLastSeq = Get-JsonValue $runtime "audioLastSeq"
                    audioFramesBroadcast = Get-JsonValue $runtime "audioFramesBroadcast"
                    audioAgeMs = Get-JsonValue $runtime "audioAgeMs"
                }) | Out-Null
            }
            if ($null -ne $candidateAudioInputDeltaDb) {
                if ([Math]::Abs($candidateAudioInputDeltaDb) -gt 12.0 -and
                    -not $candidateAudioAlignedAfterLeveler -and
                    -not $candidateAudioLevelerNormalized) {
                    $candidateAudioAlignmentMismatch = $true
                    $candidateAudioAlignmentMismatchCount++
                    $candidateAudioAlignmentMismatchSamples.Add([ordered]@{
                        sampleIndex = [int](Get-JsonValue $sample "sampleIndex")
                        candidateOutputDbfs = $candidateOutputDbfs
                        rxAudioLevelerInputRmsDbfs = $runtimeLevelerInputRmsDbfsNumber
                        rxAudioLevelerOutputRmsDbfs = $runtimeLevelerOutputRmsDbfsNumber
                        inputDeltaDb = $candidateAudioInputDeltaDb
                        outputDeltaDb = $candidateAudioOutputDeltaDb
                        deltaDb = if ($null -ne $candidateAudioOutputDeltaDb) { $candidateAudioOutputDeltaDb } else { $candidateAudioInputDeltaDb }
                        audioRmsDbfs = $runtimeAudioRmsDbfsNumber
                        audioLastSeq = Get-JsonValue $runtime "audioLastSeq"
                        audioFramesBroadcast = Get-JsonValue $runtime "audioFramesBroadcast"
                        audioAgeMs = Get-JsonValue $runtime "audioAgeMs"
                    }) | Out-Null
                }
            }
            if ($null -ne $candidateInputDbfs -and $null -ne $candidateOutputDbfs) {
                $candidateInputOutputXValues.Add([double]$candidateInputDbfs) | Out-Null
                $candidateInputOutputYValues.Add([double]$candidateOutputDbfs) | Out-Null
                $candidateOutputMinusInputValues.Add([double]($candidateOutputDbfs - $candidateInputDbfs)) | Out-Null
            }
            $frontendPassbandEvidencePeak = $null
            if ($sampleHasFilterPassbandPeak) {
                $frontendPassbandEvidencePeak = $frontendNearestFilterPassbandPeak
            }
            elseif ($sampleHasPassbandEvidencePeak) {
                $frontendPassbandEvidencePeak = $nearestFrontendPeak
            }
            $frontendPassbandPeakSnrDb = Get-NumericValue (Get-JsonValue $frontendPassbandEvidencePeak "snrDb")
            $frontendPassbandPeakDbfs = Get-NumericValue (Get-JsonValue $frontendPassbandEvidencePeak "dbfs")
            $frontendPassbandPeakLooksStrong = ($sampleHasPassbandEvidencePeak -and
                (($null -ne $frontendPassbandPeakSnrDb -and [double]$frontendPassbandPeakSnrDb -ge $frontendStrongPassbandSnrThresholdDb) -or
                    ($null -ne $frontendPassbandPeakDbfs -and [double]$frontendPassbandPeakDbfs -ge $frontendStrongPassbandDbfsThreshold)))
            if ($frontendPassbandPeakLooksStrong -and
                $null -ne $candidateInputDbfs -and
                [double]$candidateInputDbfs -lt $candidateStrongInputThresholdDbfs) {
                $distanceToStrongThresholdDb = [Math]::Round([Math]::Max(0.0, [double]$candidateStrongInputThresholdDbfs - [double]$candidateInputDbfs), 3)
                $distanceToNearStrongThresholdDb = [Math]::Round([Math]::Max(0.0, [double]$candidateNearStrongInputThresholdDbfs - [double]$candidateInputDbfs), 3)
                $candidateFrontendStrongPassbandSubthresholdDistanceValues.Add([double]$distanceToStrongThresholdDb) | Out-Null
                $candidateFrontendStrongPassbandSubthresholdSamples.Add([ordered]@{
                    sampleIndex = [int](Get-JsonValue $sample "sampleIndex")
                    inputDbfs = $candidateInputDbfs
                    outputDbfs = $candidateOutputDbfs
                    finalAudioRmsDbfs = $runtimeFinalAudioRmsDbfsNumber
                    inputClass = if ([double]$candidateInputDbfs -ge $candidateNearStrongInputThresholdDbfs) {
                        "near-strong"
                    }
                    elseif ([double]$candidateInputDbfs -le $candidateLowEvidenceInputThresholdDbfs) {
                        "weak"
                    }
                    else {
                        "mid-subthreshold"
                    }
                    distanceToStrongThresholdDb = $distanceToStrongThresholdDb
                    distanceToNearStrongThresholdDb = $distanceToNearStrongThresholdDb
                    frontendStrongPassbandSnrThresholdDb = $frontendStrongPassbandSnrThresholdDb
                    frontendStrongPassbandDbfsThreshold = $frontendStrongPassbandDbfsThreshold
                    frontendPassbandPeak = Convert-FrontendTopPeak $frontendPassbandEvidencePeak
                    strongest = Convert-FrontendTopPeak $strongestFrontendPeak
                    nearPassbandPeak = $sampleHasNearPassbandPeak
                    filterPassbandPeak = $sampleHasFilterPassbandPeak
                    passbandEvidencePeak = $sampleHasPassbandEvidencePeak
                    nearestFilterPassbandDistanceHz = $frontendNearestFilterPassbandDistanceHz
                    signalConfidence = $candidateConfidenceNumber
                    agcGate = $candidateAgcGateNumber
                    signalProbability = $candidateSignalProbabilityNumber
                    peakEvidence = $candidatePeakEvidenceNumber
                    audioAlignmentMismatch = $candidateAudioAlignmentMismatch
                }) | Out-Null
            }
            $isLowEvidenceWeakInput = ($null -ne $candidateInputDbfs -and
                $candidateInputDbfs -le $candidateLowEvidenceInputThresholdDbfs -and
                $null -ne $candidateConfidenceNumber -and
                $candidateConfidenceNumber -le $candidateLowEvidenceConfidenceThreshold -and
                $null -ne $candidateSignalProbabilityNumber -and
                $candidateSignalProbabilityNumber -le $candidateLowEvidenceProbabilityThreshold -and
                $null -ne $candidateAgcGateNumber -and
                $candidateAgcGateNumber -le $candidateLowEvidenceAgcGateThreshold)
            if ($isLowEvidenceWeakInput) {
                $candidateLowEvidenceSampleCount++
                $candidateOutputLiftedLowEvidence = ($null -ne $candidateOutputDbfs -and
                    $candidateOutputDbfs -ge $candidateLowEvidenceOutputThresholdDbfs)
                $runtimeLiftedLowEvidence = (-not $candidateAudioAlignmentMismatch -and
                    $null -ne $runtimeFinalAudioRmsDbfsNumber -and
                    $runtimeFinalAudioRmsDbfsNumber -ge $candidateLowEvidenceAudioThresholdDbfs)
                $candidateLiftedLowEvidence = $candidateOutputLiftedLowEvidence -or $runtimeLiftedLowEvidence
                if ($candidateAudioAlignmentMismatch) {
                    $candidateLowEvidenceAlignmentMismatchCount++
                }
                if ($candidateLiftedLowEvidence) {
                    $candidateLowEvidenceLiftCount++
                    $candidateLowEvidenceLiftSamples.Add([ordered]@{
                        sampleIndex = [int](Get-JsonValue $sample "sampleIndex")
                        inputDbfs = $candidateInputDbfs
                        outputDbfs = $candidateOutputDbfs
                        signalConfidence = $candidateConfidenceNumber
                        agcGate = $candidateAgcGateNumber
                        signalProbability = $candidateSignalProbabilityNumber
                        textureFill = $candidateTextureFillNumber
                        maskSmoothing = $candidateMaskSmoothingNumber
                        recoveryDrive = $candidateRecoveryDriveNumber
                        weakSignalMemory = $candidateWeakSignalMemoryNumber
                        makeupGainDb = $candidateMakeupGainDbNumber
                        audioRmsDbfs = $runtimeAudioRmsDbfsNumber
                        rxAudioLevelerInputRmsDbfs = $runtimeLevelerInputRmsDbfsNumber
                        rxAudioLevelerOutputRmsDbfs = $runtimeLevelerOutputRmsDbfsNumber
                        rxAudioLevelerAppliedGainDb = $runtimeLevelerAppliedGainDbNumber
                        rxAudioLevelerPauseHoldBlocks = $runtimeLevelerPauseHoldBlocksNumber
                        rxAudioLevelerCandidateSpeechHoldBlocks = $runtimeLevelerCandidateSpeechHoldBlocksNumber
                        rxAudioLevelerCandidateSpeechHangoverBlocks = $runtimeLevelerCandidateSpeechHangoverBlocksNumber
                        candidateAudioInputDeltaDb = $candidateAudioInputDeltaDb
                        candidateAudioAlignmentMismatch = $candidateAudioAlignmentMismatch
                    }) | Out-Null
                }
                elseif (-not $candidateAudioAlignmentMismatch -and $null -ne $candidateOutputDbfs -and $candidateOutputDbfs -le -35.0) {
                    $candidateLowEvidenceSuppressedCount++
                    $candidateLowEvidenceSuppressedSamples.Add([ordered]@{
                        sampleIndex = [int](Get-JsonValue $sample "sampleIndex")
                        inputDbfs = $candidateInputDbfs
                        outputDbfs = $candidateOutputDbfs
                        signalConfidence = $candidateConfidenceNumber
                        agcGate = $candidateAgcGateNumber
                        signalProbability = $candidateSignalProbabilityNumber
                        textureFill = $candidateTextureFillNumber
                        maskSmoothing = $candidateMaskSmoothingNumber
                        recoveryDrive = $candidateRecoveryDriveNumber
                        weakSignalMemory = $candidateWeakSignalMemoryNumber
                        makeupGainDb = $candidateMakeupGainDbNumber
                        audioRmsDbfs = $runtimeAudioRmsDbfsNumber
                        rxAudioLevelerInputRmsDbfs = $runtimeLevelerInputRmsDbfsNumber
                        rxAudioLevelerOutputRmsDbfs = $runtimeLevelerOutputRmsDbfsNumber
                        rxAudioLevelerAppliedGainDb = $runtimeLevelerAppliedGainDbNumber
                        rxAudioLevelerPauseHoldBlocks = $runtimeLevelerPauseHoldBlocksNumber
                        rxAudioLevelerCandidateSpeechHoldBlocks = $runtimeLevelerCandidateSpeechHoldBlocksNumber
                        rxAudioLevelerCandidateSpeechHangoverBlocks = $runtimeLevelerCandidateSpeechHangoverBlocksNumber
                        candidateAudioInputDeltaDb = $candidateAudioInputDeltaDb
                        candidateAudioAlignmentMismatch = $candidateAudioAlignmentMismatch
                    }) | Out-Null
                }
            }
            if ($null -ne $candidateInputDbfs -and $candidateInputDbfs -le -30.0) {
                $candidateWeakInputCount++
                if ($null -ne $candidateOutputDbfs) {
                    $candidateWeakOutputValues.Add([double]$candidateOutputDbfs) | Out-Null
                }
                Add-Number $candidateWeakFinalAudioValues $runtimeFinalAudioRmsDbfsNumber
                $candidateSpeechQualifiedWeakInput = (
                    $runtimeIsRxAudio -and
                    -not $isLowEvidenceWeakInput -and
                    -not $candidateAudioAlignmentMismatch -and
                    $null -ne $candidateOutputDbfs -and
                    $candidateOutputDbfs -ge -40.0 -and
                    ($sampleHasPassbandEvidencePeak -or
                        ($null -ne $candidatePeakEvidenceNumber -and $candidatePeakEvidenceNumber -ge 0.08) -or
                        ($null -ne $candidateConfidenceNumber -and $candidateConfidenceNumber -ge 0.30 -and
                            $null -ne $candidateSignalProbabilityNumber -and $candidateSignalProbabilityNumber -ge 0.14)))
                if ($candidateSpeechQualifiedWeakInput) {
                    $candidateSpeechQualifiedWeakOutputValues.Add([double]$candidateOutputDbfs) | Out-Null
                    Add-Number $candidateSpeechQualifiedWeakFinalAudioValues $runtimeFinalAudioRmsDbfsNumber
                }
                $candidatePassbandQualifiedWeakInput = (
                    $candidateSpeechQualifiedWeakInput -and
                    $sampleHasNearPassbandPeak -and
                    $null -ne $runtimeFinalAudioRmsDbfsNumber -and
                    $runtimeFinalAudioRmsDbfsNumber -ge $candidateWeakDropoutFinalAudibleThresholdDbfs)
                if ($candidatePassbandQualifiedWeakInput) {
                    $candidatePassbandQualifiedWeakOutputValues.Add([double]$candidateOutputDbfs) | Out-Null
                    Add-Number $candidatePassbandQualifiedWeakFinalAudioValues $runtimeFinalAudioRmsDbfsNumber
                }
                $candidateWeakInputSample = New-CandidateClassifiedInputSample `
                    -Class "weak" `
                    -Sample $sample `
                    -InputDbfs $candidateInputDbfs `
                    -OutputDbfs $candidateOutputDbfs `
                    -FinalAudioRmsDbfs $runtimeFinalAudioRmsDbfsNumber `
                    -LevelerInputRmsDbfs $runtimeLevelerInputRmsDbfsNumber `
                    -LevelerOutputRmsDbfs $runtimeLevelerOutputRmsDbfsNumber `
                    -LevelerAppliedGainDb $runtimeLevelerAppliedGainDbNumber `
                    -SignalConfidence $candidateConfidenceNumber `
                    -AgcGate $candidateAgcGateNumber `
                    -SignalProbability $candidateSignalProbabilityNumber `
                    -PeakEvidence $candidatePeakEvidenceNumber `
                    -MakeupGainDb $candidateMakeupGainDbNumber `
                    -RecoveryDrive $candidateRecoveryDriveNumber `
                    -WeakSignalMemory $candidateWeakSignalMemoryNumber `
                    -NearPassbandPeak $sampleHasNearPassbandPeak `
                    -FilterPassbandPeak $sampleHasFilterPassbandPeak `
                    -PassbandEvidencePeak $sampleHasPassbandEvidencePeak `
                    -NearestFilterPassbandDistanceHz $frontendNearestFilterPassbandDistanceHz `
                    -NearestPeak $nearestFrontendPeak `
                    -StrongestPeak $strongestFrontendPeak `
                    -SpeechQualified $candidateSpeechQualifiedWeakInput `
                    -PassbandQualified $candidatePassbandQualifiedWeakInput `
                    -LowEvidence $isLowEvidenceWeakInput `
                    -AudioAlignmentMismatch $candidateAudioAlignmentMismatch `
                    -AudioInputDeltaDb $candidateAudioInputDeltaDb
                $candidateWeakInputSamples.Add($candidateWeakInputSample) | Out-Null
                if ($candidateSpeechQualifiedWeakInput) {
                    $candidateSpeechQualifiedWeakInputSamples.Add($candidateWeakInputSample) | Out-Null
                }
                if ($candidatePassbandQualifiedWeakInput) {
                    $candidatePassbandQualifiedWeakInputSamples.Add($candidateWeakInputSample) | Out-Null
                }
                if ($null -ne $candidateOutputDbfs -and $candidateOutputDbfs -ge -30.0) {
                    $candidateWeakRecoveredCount++
                }
                if ($null -ne $candidateOutputDbfs -and $candidateOutputDbfs -lt ($candidateInputDbfs - 1.0)) {
                    $candidateWeakBelowInputCount++
                }
                if ($null -ne $candidateOutputDbfs -and $candidateOutputDbfs -ge -31.5 -and $candidateOutputDbfs -le -20.0) {
                    $candidateWeakNearTargetCount++
                }
                if (-not $isLowEvidenceWeakInput -and -not $candidateAudioAlignmentMismatch -and
                    $null -ne $candidateOutputDbfs -and $candidateOutputDbfs -le -35.0) {
                    $candidateWeakDropoutCount++
                    $candidateWeakDropoutFinalAudible = (
                        $null -ne $runtimeFinalAudioRmsDbfsNumber -and
                        $runtimeFinalAudioRmsDbfsNumber -ge $candidateWeakDropoutFinalAudibleThresholdDbfs)
                    $candidateWeakDropoutNativeLifted = ($candidateOutputDbfs -ge ($candidateInputDbfs + $candidateWeakDropoutNativeLiftThresholdDb))
                    $candidateWeakDropoutBelowInput = ($candidateOutputDbfs -lt ($candidateInputDbfs - $candidateWeakDropoutBelowInputThresholdDb))
                    $candidateWeakDropoutCandidateLoss = (-not $candidateWeakDropoutFinalAudible -and
                        -not $candidateWeakDropoutNativeLifted -and
                        $candidateWeakDropoutBelowInput)
                    $candidateWeakDropoutClass = if ($candidateWeakDropoutFinalAudible) {
                        "final-audible"
                    }
                    elseif ($candidateWeakDropoutNativeLifted) {
                        "native-lifted-still-low"
                    }
                    elseif ($candidateWeakDropoutCandidateLoss) {
                        "candidate-weak-loss"
                    }
                    else {
                        "strict-low-output"
                    }
                    if ($candidateWeakDropoutFinalAudible) {
                        $candidateWeakDropoutFinalAudibleCount++
                    }
                    if ($candidateWeakDropoutNativeLifted) {
                        $candidateWeakDropoutNativeLiftedCount++
                    }
                    if ($candidateWeakDropoutCandidateLoss) {
                        $candidateWeakDropoutCandidateLossCount++
                    }
                    $candidateWeakDropoutSample = [ordered]@{
                        sampleIndex = [int](Get-JsonValue $sample "sampleIndex")
                        dropoutClass = $candidateWeakDropoutClass
                        inputDbfs = $candidateInputDbfs
                        outputDbfs = $candidateOutputDbfs
                        signalConfidence = $candidateConfidenceNumber
                        agcGate = $candidateAgcGateNumber
                        signalProbability = $candidateSignalProbabilityNumber
                        textureFill = $candidateTextureFillNumber
                        maskSmoothing = $candidateMaskSmoothingNumber
                        recoveryDrive = $candidateRecoveryDriveNumber
                        weakSignalMemory = $candidateWeakSignalMemoryNumber
                        makeupGainDb = $candidateMakeupGainDbNumber
                        audioRmsDbfs = $runtimeAudioRmsDbfsNumber
                        rxAudioLevelerInputRmsDbfs = $runtimeLevelerInputRmsDbfsNumber
                        rxAudioLevelerOutputRmsDbfs = $runtimeLevelerOutputRmsDbfsNumber
                        finalAudible = $candidateWeakDropoutFinalAudible
                        nativeLifted = $candidateWeakDropoutNativeLifted
                        candidateWeakLoss = $candidateWeakDropoutCandidateLoss
                        candidateAudioInputDeltaDb = $candidateAudioInputDeltaDb
                        candidateAudioAlignmentMismatch = $candidateAudioAlignmentMismatch
                    }
                    $candidateWeakDropoutSamples.Add($candidateWeakDropoutSample) | Out-Null
                    if ($candidateWeakDropoutFinalAudible) {
                        $candidateWeakDropoutFinalAudibleSamples.Add($candidateWeakDropoutSample) | Out-Null
                    }
                    if ($candidateWeakDropoutCandidateLoss) {
                        $candidateWeakDropoutCandidateLossSamples.Add($candidateWeakDropoutSample) | Out-Null
                    }
                }
            }
            elseif ($null -ne $candidateInputDbfs -and $candidateInputDbfs -ge $candidateStrongInputThresholdDbfs) {
                $candidateStrongInputCount++
                if ($null -ne $candidateOutputDbfs) {
                    $candidateStrongOutputValues.Add([double]$candidateOutputDbfs) | Out-Null
                }
                Add-Number $candidateStrongFinalAudioValues $runtimeFinalAudioRmsDbfsNumber
                $candidateSpeechQualifiedStrongInput = (
                    -not $candidateAudioAlignmentMismatch -and
                    $null -ne $candidateOutputDbfs -and
                    $candidateOutputDbfs -ge -26.0 -and
                    $null -ne $runtimeFinalAudioRmsDbfsNumber -and
                    $runtimeFinalAudioRmsDbfsNumber -ge -30.0 -and
                    ($sampleHasNearPassbandPeak -or
                        ($null -ne $candidatePeakEvidenceNumber -and $candidatePeakEvidenceNumber -ge 0.08) -or
                        ($null -ne $candidateConfidenceNumber -and $candidateConfidenceNumber -ge 0.30 -and
                            $null -ne $candidateSignalProbabilityNumber -and $candidateSignalProbabilityNumber -ge 0.14)))
                if ($candidateSpeechQualifiedStrongInput) {
                    $candidateSpeechQualifiedStrongOutputValues.Add([double]$candidateOutputDbfs) | Out-Null
                    Add-Number $candidateSpeechQualifiedStrongFinalAudioValues $runtimeFinalAudioRmsDbfsNumber
                }
                $candidatePassbandQualifiedStrongInput = (
                    $candidateSpeechQualifiedStrongInput -and
                    $sampleHasNearPassbandPeak)
                if ($candidatePassbandQualifiedStrongInput) {
                    $candidatePassbandQualifiedStrongOutputValues.Add([double]$candidateOutputDbfs) | Out-Null
                    Add-Number $candidatePassbandQualifiedStrongFinalAudioValues $runtimeFinalAudioRmsDbfsNumber
                }
                $candidateStrongInputSample = New-CandidateClassifiedInputSample `
                    -Class "strong" `
                    -Sample $sample `
                    -InputDbfs $candidateInputDbfs `
                    -OutputDbfs $candidateOutputDbfs `
                    -FinalAudioRmsDbfs $runtimeFinalAudioRmsDbfsNumber `
                    -LevelerInputRmsDbfs $runtimeLevelerInputRmsDbfsNumber `
                    -LevelerOutputRmsDbfs $runtimeLevelerOutputRmsDbfsNumber `
                    -LevelerAppliedGainDb $runtimeLevelerAppliedGainDbNumber `
                    -SignalConfidence $candidateConfidenceNumber `
                    -AgcGate $candidateAgcGateNumber `
                    -SignalProbability $candidateSignalProbabilityNumber `
                    -PeakEvidence $candidatePeakEvidenceNumber `
                    -MakeupGainDb $candidateMakeupGainDbNumber `
                    -RecoveryDrive $candidateRecoveryDriveNumber `
                    -WeakSignalMemory $candidateWeakSignalMemoryNumber `
                    -NearPassbandPeak $sampleHasNearPassbandPeak `
                    -FilterPassbandPeak $sampleHasFilterPassbandPeak `
                    -PassbandEvidencePeak $sampleHasPassbandEvidencePeak `
                    -NearestFilterPassbandDistanceHz $frontendNearestFilterPassbandDistanceHz `
                    -NearestPeak $nearestFrontendPeak `
                    -StrongestPeak $strongestFrontendPeak `
                    -SpeechQualified $candidateSpeechQualifiedStrongInput `
                    -PassbandQualified $candidatePassbandQualifiedStrongInput `
                    -LowEvidence $false `
                    -AudioAlignmentMismatch $candidateAudioAlignmentMismatch `
                    -AudioInputDeltaDb $candidateAudioInputDeltaDb
                $candidateStrongInputSamples.Add($candidateStrongInputSample) | Out-Null
                if ($candidateSpeechQualifiedStrongInput) {
                    $candidateSpeechQualifiedStrongInputSamples.Add($candidateStrongInputSample) | Out-Null
                }
                if ($candidatePassbandQualifiedStrongInput) {
                    $candidatePassbandQualifiedStrongInputSamples.Add($candidateStrongInputSample) | Out-Null
                }
            }
            elseif ($null -ne $candidateInputDbfs -and $candidateInputDbfs -ge $candidateNearStrongInputThresholdDbfs) {
                $candidateNearStrongInputCount++
                if ($null -ne $candidateOutputDbfs) {
                    $candidateNearStrongOutputValues.Add([double]$candidateOutputDbfs) | Out-Null
                }
                Add-Number $candidateNearStrongFinalAudioValues $runtimeFinalAudioRmsDbfsNumber
                $candidateSpeechQualifiedNearStrongInput = (
                    -not $candidateAudioAlignmentMismatch -and
                    $null -ne $candidateOutputDbfs -and
                    $candidateOutputDbfs -ge -28.0 -and
                    $null -ne $runtimeFinalAudioRmsDbfsNumber -and
                    $runtimeFinalAudioRmsDbfsNumber -ge -35.0 -and
                    ($sampleHasNearPassbandPeak -or
                        ($null -ne $candidatePeakEvidenceNumber -and $candidatePeakEvidenceNumber -ge 0.08) -or
                        ($null -ne $candidateConfidenceNumber -and $candidateConfidenceNumber -ge 0.30 -and
                            $null -ne $candidateSignalProbabilityNumber -and $candidateSignalProbabilityNumber -ge 0.14)))
                if ($candidateSpeechQualifiedNearStrongInput) {
                    $candidateSpeechQualifiedNearStrongOutputValues.Add([double]$candidateOutputDbfs) | Out-Null
                    Add-Number $candidateSpeechQualifiedNearStrongFinalAudioValues $runtimeFinalAudioRmsDbfsNumber
                }
                $candidatePassbandQualifiedNearStrongInput = (
                    $candidateSpeechQualifiedNearStrongInput -and
                    $sampleHasPassbandEvidencePeak)
                if ($candidatePassbandQualifiedNearStrongInput) {
                    $candidatePassbandQualifiedNearStrongOutputValues.Add([double]$candidateOutputDbfs) | Out-Null
                    Add-Number $candidatePassbandQualifiedNearStrongFinalAudioValues $runtimeFinalAudioRmsDbfsNumber
                }

                $candidateNearStrongInputSamples.Add([ordered]@{
                    sampleIndex = [int](Get-JsonValue $sample "sampleIndex")
                    inputDbfs = $candidateInputDbfs
                    outputDbfs = $candidateOutputDbfs
                    finalAudioRmsDbfs = $runtimeFinalAudioRmsDbfsNumber
                    rxAudioLevelerInputRmsDbfs = $runtimeLevelerInputRmsDbfsNumber
                    rxAudioLevelerOutputRmsDbfs = $runtimeLevelerOutputRmsDbfsNumber
                    distanceToStrongThresholdDb = [Math]::Round([Math]::Max(0.0, [double]$candidateStrongInputThresholdDbfs - [double]$candidateInputDbfs), 3)
                    signalConfidence = $candidateConfidenceNumber
                    agcGate = $candidateAgcGateNumber
                    signalProbability = $candidateSignalProbabilityNumber
                    peakEvidence = $candidatePeakEvidenceNumber
                    nearPassbandPeak = $sampleHasNearPassbandPeak
                    filterPassbandPeak = $sampleHasFilterPassbandPeak
                    passbandEvidencePeak = $sampleHasPassbandEvidencePeak
                    nearestFilterPassbandDistanceHz = $frontendNearestFilterPassbandDistanceHz
                    nearest = Convert-FrontendTopPeak $nearestFrontendPeak
                    strongest = Convert-FrontendTopPeak $strongestFrontendPeak
                    speechQualified = $candidateSpeechQualifiedNearStrongInput
                    passbandQualified = $candidatePassbandQualifiedNearStrongInput
                }) | Out-Null
            }
            if ($null -ne $candidateMakeupGainDbNumber -and $candidateMakeupGainDbNumber -ge 12.0) {
                $candidateHotMakeupCount++
                $candidateHotMakeupSamples.Add([ordered]@{
                    sampleIndex = [int](Get-JsonValue $sample "sampleIndex")
                    inputDbfs = $candidateInputDbfs
                    outputDbfs = $candidateOutputDbfs
                    signalConfidence = $candidateConfidenceNumber
                    agcGate = $candidateAgcGateNumber
                    signalProbability = $candidateSignalProbabilityNumber
                    textureFill = $candidateTextureFillNumber
                    maskSmoothing = $candidateMaskSmoothingNumber
                    recoveryDrive = $candidateRecoveryDriveNumber
                    weakSignalMemory = $candidateWeakSignalMemoryNumber
                    makeupGainDb = $candidateMakeupGainDbNumber
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
            $voiceLikeEvidence = ($null -ne $candidateConfidenceNumber -and $candidateConfidenceNumber -ge $signalEvidenceConfidenceThreshold) -or
                ($null -ne $candidateSignalProbabilityNumber -and $candidateSignalProbabilityNumber -ge $signalEvidenceProbabilityThreshold) -or
                ($null -ne $candidateAgcGateNumber -and $candidateAgcGateNumber -ge $signalEvidenceAgcGateThreshold)
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
            $rxAudioLevelerRequestedProfileValue = Get-JsonValue $runtime "rxAudioLevelerRequestedProfile"
            $rxAudioLevelerActiveProfileValue = Get-JsonValue $runtime "rxAudioLevelerActiveProfile"
            $rxAudioLevelerRequestedProfile = [string]$rxAudioLevelerRequestedProfileValue
            $rxAudioLevelerActiveProfile = [string]$rxAudioLevelerActiveProfileValue
            $rxAudioLevelerProfileExplicit = -not [string]::IsNullOrWhiteSpace($rxAudioLevelerActiveProfile)
            if ([string]::IsNullOrWhiteSpace($rxAudioLevelerRequestedProfile)) {
                $rxAudioLevelerRequestedProfile = "current"
            }
            if ([string]::IsNullOrWhiteSpace($rxAudioLevelerActiveProfile)) {
                $rxAudioLevelerActiveProfile = $rxAudioLevelerRequestedProfile
            }
            $rxAudioLevelerExperimental = Test-Truthy (Get-JsonValue $runtime "rxAudioLevelerExperimental")
            $rxAudioLevelerControlRmsValid = Test-Truthy (Get-JsonValue $runtime "rxAudioLevelerControlRmsValid")
            $rxAudioLevelerControlRmsDbfs = Get-JsonValue $runtime "rxAudioLevelerControlRmsDbfs"
            $rxAudioLevelerControlRmsHangDb = Get-JsonValue $runtime "rxAudioLevelerControlRmsHangDb"
            $rxAudioLevelerInputRmsDbfsNumber = Get-NumericValue $rxAudioLevelerInputRmsDbfs
            $rxAudioLevelerProfileSample = ($rxAudioLevelerProfileExplicit -and ($runtimeIsRxAudio -or
                $null -ne $rxAudioLevelerInputRmsDbfs -or
                $null -ne $rxAudioLevelerOutputRmsDbfs))
            if ($rxAudioLevelerProfileSample) {
                Add-Count $rxAudioLevelerProfileCounts $rxAudioLevelerActiveProfile
                if ($rxAudioLevelerExperimental) {
                    $rxAudioLevelerExperimentalSampleCount++
                }
                if ($rxAudioLevelerControlRmsValid) {
                    $rxAudioLevelerControlRmsValidCount++
                    Add-Number $rxAudioLevelerControlRmsValues $rxAudioLevelerControlRmsDbfs
                    Add-Number $rxAudioLevelerControlRmsHangValues $rxAudioLevelerControlRmsHangDb
                    if ([string]::Equals($rxAudioLevelerActiveProfile, "stable-speech-candidate", [StringComparison]::OrdinalIgnoreCase) -and
                        $null -ne $rxAudioLevelerInputRmsDbfsNumber -and
                        [double]$rxAudioLevelerInputRmsDbfsNumber -ge $rxAudioLevelerNormalStrengthControlRmsThresholdDbfs) {
                        $rxAudioLevelerNormalStrengthControlRmsValidCount++
                        $rxAudioLevelerNormalStrengthControlRmsSamples.Add([ordered]@{
                            sampleIndex = Get-JsonValue $sample "sampleIndex"
                            sampledUtc = Get-JsonValue $sample "sampledUtc"
                            activeProfile = $rxAudioLevelerActiveProfile
                            inputRmsDbfs = $rxAudioLevelerInputRmsDbfsNumber
                            outputRmsDbfs = Get-NumericValue $rxAudioLevelerOutputRmsDbfs
                            controlRmsDbfs = Get-NumericValue $rxAudioLevelerControlRmsDbfs
                            controlRmsHangDb = Get-NumericValue $rxAudioLevelerControlRmsHangDb
                            thresholdDbfs = $rxAudioLevelerNormalStrengthControlRmsThresholdDbfs
                        }) | Out-Null
                    }
                }
            }
            $txOutputHeadroomRequestedProfileValue = Get-JsonValue $runtime "txOutputHeadroomRequestedProfile"
            $txOutputHeadroomActiveProfileValue = Get-JsonValue $runtime "txOutputHeadroomActiveProfile"
            $txOutputHeadroomRequestedProfile = [string]$txOutputHeadroomRequestedProfileValue
            $txOutputHeadroomActiveProfile = [string]$txOutputHeadroomActiveProfileValue
            $txOutputHeadroomProfileExplicit = -not [string]::IsNullOrWhiteSpace($txOutputHeadroomRequestedProfile) -or
                -not [string]::IsNullOrWhiteSpace($txOutputHeadroomActiveProfile)
            if ([string]::IsNullOrWhiteSpace($txOutputHeadroomRequestedProfile)) {
                $txOutputHeadroomRequestedProfile = "current"
            }
            if ([string]::IsNullOrWhiteSpace($txOutputHeadroomActiveProfile)) {
                $txOutputHeadroomActiveProfile = $txOutputHeadroomRequestedProfile
            }
            if ($txOutputHeadroomProfileExplicit) {
                Add-Count $txOutputHeadroomRequestedProfileCounts $txOutputHeadroomRequestedProfile
                Add-Count $txOutputHeadroomActiveProfileCounts $txOutputHeadroomActiveProfile
                if (Test-Truthy (Get-JsonValue $runtime "txOutputHeadroomExperimental")) {
                    $txOutputHeadroomExperimentalSampleCount++
                }
                if (Test-Truthy (Get-JsonValue $runtime "txOutputHeadroomPureSignalBypassed")) {
                    $txOutputHeadroomPureSignalBypassedSampleCount++
                }
                Add-Number $txOutputHeadroomTrimValues (Get-JsonValue $runtime "txOutputHeadroomTrimDb")
            }
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
            $rxAudioLevelerCandidateSpeechHoldBlocks = Get-JsonValue $runtime "rxAudioLevelerCandidateSpeechHoldBlocks"
            $rxAudioLevelerCandidateSpeechHangoverBlocks = Get-JsonValue $runtime "rxAudioLevelerCandidateSpeechHangoverBlocks"
            $rxAudioLevelerCandidateHybridSpeechPrior = Get-JsonValue $runtime "rxAudioLevelerCandidateHybridSpeechPrior"
            $rxAudioLevelerCandidateNoSignalNoisePrior = Get-JsonValue $runtime "rxAudioLevelerCandidateNoSignalNoisePrior"
            $rxAudioLevelerCandidateNoiseProfilePrior = Get-JsonValue $runtime "rxAudioLevelerCandidateNoiseProfilePrior"
            $rxAudioLevelerOutputRmsDbfsNumber = Get-NumericValue $rxAudioLevelerOutputRmsDbfs
            $rxAudioLevelerAppliedGainDbNumber = Get-NumericValue $rxAudioLevelerAppliedGainDb
            $rxAudioLevelerCandidateSpeechHoldBlocksNumber = Get-NumericValue $rxAudioLevelerCandidateSpeechHoldBlocks
            $rxAudioLevelerCandidateSpeechHangoverBlocksNumber = Get-NumericValue $rxAudioLevelerCandidateSpeechHangoverBlocks
            $rxAudioLevelerCandidateHybridSpeechPriorNumber = Get-NumericValue $rxAudioLevelerCandidateHybridSpeechPrior
            $rxAudioLevelerCandidateNoSignalNoisePriorNumber = Get-NumericValue $rxAudioLevelerCandidateNoSignalNoisePrior
            $rxAudioLevelerCandidateNoiseProfilePriorNumber = Get-NumericValue $rxAudioLevelerCandidateNoiseProfilePrior
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
            Add-Number $rxAudioLevelerCandidateSpeechHoldBlockValues $rxAudioLevelerCandidateSpeechHoldBlocks
            Add-Number $rxAudioLevelerCandidateSpeechHangoverBlockValues $rxAudioLevelerCandidateSpeechHangoverBlocks
            Add-Number $rxAudioLevelerCandidateHybridSpeechPriorValues $rxAudioLevelerCandidateHybridSpeechPrior
            Add-Number $rxAudioLevelerCandidateNoSignalNoisePriorValues $rxAudioLevelerCandidateNoSignalNoisePrior
            Add-Number $rxAudioLevelerCandidateNoiseProfilePriorValues $rxAudioLevelerCandidateNoiseProfilePrior
            if ($null -ne $rxAudioLevelerInputRmsDbfs -and $null -ne $rxAudioLevelerOutputRmsDbfs -and
                $null -ne $rxAudioLevelerDesiredGainDb -and $null -ne $rxAudioLevelerAppliedGainDb) {
                $rxAudioLevelerDiagnosticCount++
            }
            $rxAudioLevelerCandidateNoSignalNoiseCap = Test-Truthy (Get-JsonValue $runtime "rxAudioLevelerCandidateNoSignalNoiseCap")
            $rxAudioLevelerCandidateFarPeakNoiseCap = Test-Truthy (Get-JsonValue $runtime "rxAudioLevelerCandidateFarPeakNoiseCap")
            $rxAudioLevelerCandidateNoProofNoiseCap = Test-Truthy (Get-JsonValue $runtime "rxAudioLevelerCandidateNoProofNoiseCap")
            $candidateSpeechContinuityHeld =
                ($null -ne $rxAudioLevelerCandidateSpeechHoldBlocksNumber -and $rxAudioLevelerCandidateSpeechHoldBlocksNumber -ge 8.0) -or
                ($null -ne $rxAudioLevelerCandidateSpeechHangoverBlocksNumber -and $rxAudioLevelerCandidateSpeechHangoverBlocksNumber -ge 16.0)
            $candidateSpeechContinuityHybrid =
                $null -ne $rxAudioLevelerCandidateHybridSpeechPriorNumber -and
                $rxAudioLevelerCandidateHybridSpeechPriorNumber -ge $candidateSpeechContinuityHybridThreshold
            $candidateSpeechContinuityNoiseLow =
                $null -eq $rxAudioLevelerCandidateNoSignalNoisePriorNumber -or
                $rxAudioLevelerCandidateNoSignalNoisePriorNumber -le $candidateSpeechContinuityNoSignalMax
            $candidateSpeechContinuityFrame =
                $runtimeIsRxAudio -and
                $null -ne $rxAudioLevelerOutputRmsDbfsNumber -and
                $voiceLikeEvidence -and
                $candidateSpeechContinuityNoiseLow -and
                ($candidateSpeechContinuityHybrid -or $candidateSpeechContinuityHeld)
            if ($candidateSpeechContinuityFrame) {
                $candidateSpeechContinuitySampleCount++
                Add-Number $candidateSpeechContinuityOutputValues $rxAudioLevelerOutputRmsDbfsNumber
                Add-Number $candidateSpeechContinuityGainValues $rxAudioLevelerAppliedGainDbNumber
                if ($rxAudioLevelerOutputRmsDbfsNumber -le $candidateSpeechContinuityFadeThresholdDbfs) {
                    $candidateSpeechContinuityFadeCount++
                }
                if ($rxAudioLevelerOutputRmsDbfsNumber -le $candidateSpeechContinuityDropoutThresholdDbfs) {
                    $candidateSpeechContinuityDropoutCount++
                }
                $candidateSpeechContinuitySamples.Add([ordered]@{
                    sampleIndex = Get-JsonValue $sample "sampleIndex"
                    sampledUtc = Get-JsonValue $sample "sampledUtc"
                    audioRmsDbfs = Get-NumericValue (Get-JsonValue $runtime "audioRmsDbfs")
                    finalAudioRmsDbfs = $rxAudioLevelerOutputRmsDbfsNumber
                    candidateInputDbfs = Get-NumericValue (Get-JsonValue $candidate "inputDbfs")
                    candidateOutputDbfs = Get-NumericValue (Get-JsonValue $candidate "outputDbfs")
                    candidateSignalConfidence = $candidateConfidenceNumber
                    candidateSignalProbability = $candidateSignalProbabilityNumber
                    candidateAgcGate = $candidateAgcGateNumber
                    candidatePeakEvidence = $candidatePeakEvidenceNumber
                    appliedGainDb = $rxAudioLevelerAppliedGainDbNumber
                    candidateSpeechHoldBlocks = $rxAudioLevelerCandidateSpeechHoldBlocksNumber
                    candidateSpeechHangoverBlocks = $rxAudioLevelerCandidateSpeechHangoverBlocksNumber
                    candidateHybridSpeechPrior = $rxAudioLevelerCandidateHybridSpeechPriorNumber
                    candidateNoSignalNoisePrior = $rxAudioLevelerCandidateNoSignalNoisePriorNumber
                    candidateNoiseProfilePrior = $rxAudioLevelerCandidateNoiseProfilePriorNumber
                    belowFadeThreshold = ($rxAudioLevelerOutputRmsDbfsNumber -le $candidateSpeechContinuityFadeThresholdDbfs)
                    belowDropoutThreshold = ($rxAudioLevelerOutputRmsDbfsNumber -le $candidateSpeechContinuityDropoutThresholdDbfs)
                    nearest = Convert-FrontendTopPeak $nearestFrontendPeak
                }) | Out-Null
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
                    candidateInputDbfs = Get-NumericValue (Get-JsonValue $candidate "inputDbfs")
                    candidateOutputDbfs = Get-NumericValue (Get-JsonValue $candidate "outputDbfs")
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
                    candidateSpeechHoldBlocks = Get-NumericValue $rxAudioLevelerCandidateSpeechHoldBlocks
                    candidateSpeechHangoverBlocks = Get-NumericValue $rxAudioLevelerCandidateSpeechHangoverBlocks
                    candidateHybridSpeechPrior = Get-NumericValue $rxAudioLevelerCandidateHybridSpeechPrior
                    candidateNoSignalNoisePrior = Get-NumericValue $rxAudioLevelerCandidateNoSignalNoisePrior
                    candidateNoiseProfilePrior = Get-NumericValue $rxAudioLevelerCandidateNoiseProfilePrior
                    candidateNoSignalNoiseCap = $rxAudioLevelerCandidateNoSignalNoiseCap
                    candidateFarPeakNoiseCap = $rxAudioLevelerCandidateFarPeakNoiseCap
                    candidateNoProofNoiseCap = $rxAudioLevelerCandidateNoProofNoiseCap
                    boostSlewLimited = $rxAudioLevelerBoostSlewLimited
                    peakLimited = $rxAudioLevelerPeakLimited
                    outputLimited = $rxAudioLevelerOutputLimited
                }
                $rxAudioLevelerConstrainedSamples.Add($rxAudioLevelerConstrainedSample) | Out-Null
            }
            if ($rxAudioLevelerCandidateNoSignalNoiseCap) {
                $rxAudioLevelerCandidateNoSignalNoiseCapCount++
                $rxAudioLevelerCandidateNoSignalNoiseCapSamples.Add([ordered]@{
                    sampleIndex = Get-JsonValue $sample "sampleIndex"
                    sampledUtc = Get-JsonValue $sample "sampledUtc"
                    audioRmsDbfs = Get-NumericValue (Get-JsonValue $runtime "audioRmsDbfs")
                    candidateInputDbfs = Get-NumericValue (Get-JsonValue $candidate "inputDbfs")
                    candidateOutputDbfs = Get-NumericValue (Get-JsonValue $candidate "outputDbfs")
                    inputRmsDbfs = Get-NumericValue $rxAudioLevelerInputRmsDbfs
                    outputRmsDbfs = Get-NumericValue $rxAudioLevelerOutputRmsDbfs
                    desiredGainDb = Get-NumericValue $rxAudioLevelerDesiredGainDb
                    appliedGainDb = Get-NumericValue $rxAudioLevelerAppliedGainDb
                    candidateSpeechHoldBlocks = Get-NumericValue $rxAudioLevelerCandidateSpeechHoldBlocks
                    candidateSpeechHangoverBlocks = Get-NumericValue $rxAudioLevelerCandidateSpeechHangoverBlocks
                    candidateHybridSpeechPrior = Get-NumericValue $rxAudioLevelerCandidateHybridSpeechPrior
                    candidateNoSignalNoisePrior = Get-NumericValue $rxAudioLevelerCandidateNoSignalNoisePrior
                    candidateNoiseProfilePrior = Get-NumericValue $rxAudioLevelerCandidateNoiseProfilePrior
                    candidateFarPeakNoiseCap = $rxAudioLevelerCandidateFarPeakNoiseCap
                    candidateNoProofNoiseCap = $rxAudioLevelerCandidateNoProofNoiseCap
                }) | Out-Null
            }
            if ($rxAudioLevelerCandidateFarPeakNoiseCap) {
                $rxAudioLevelerCandidateFarPeakNoiseCapCount++
            }
            if ($rxAudioLevelerCandidateNoProofNoiseCap) {
                $rxAudioLevelerCandidateNoProofNoiseCapCount++
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
    $rxAudioLevelerCandidateSpeechHoldBlockStats = Get-NumberStats $rxAudioLevelerCandidateSpeechHoldBlockValues
    $rxAudioLevelerCandidateSpeechHangoverBlockStats = Get-NumberStats $rxAudioLevelerCandidateSpeechHangoverBlockValues
    $rxAudioLevelerCandidateHybridSpeechPriorStats = Get-NumberStats $rxAudioLevelerCandidateHybridSpeechPriorValues
    $rxAudioLevelerCandidateNoSignalNoisePriorStats = Get-NumberStats $rxAudioLevelerCandidateNoSignalNoisePriorValues
    $rxAudioLevelerCandidateNoiseProfilePriorStats = Get-NumberStats $rxAudioLevelerCandidateNoiseProfilePriorValues
    $rxAudioLevelerControlRmsStats = Get-NumberStats $rxAudioLevelerControlRmsValues
    $rxAudioLevelerControlRmsHangStats = Get-NumberStats $rxAudioLevelerControlRmsHangValues
    $candidateSpeechContinuityOutputStats = Get-NumberStats $candidateSpeechContinuityOutputValues
    $candidateSpeechContinuityGainStats = Get-NumberStats $candidateSpeechContinuityGainValues
    $backlogStats = Get-NumberStats $backlogValues
    $frontendSceneAgeStats = Get-NumberStats $frontendSceneAgeValues
    $frontendTopPeakCountStats = Get-NumberStats $frontendTopPeakCountValues
    $frontendNearestTopPeakOffsetStats = Get-NumberStats $frontendNearestTopPeakOffsetValues
    $frontendNearestTopPeakAbsOffsetStats = Get-NumberStats $frontendNearestTopPeakAbsOffsetValues
    $frontendStrongestTopPeakSnrStats = Get-NumberStats $frontendStrongestTopPeakSnrValues
    $frontendNearestFilterPassbandDistanceStats = Get-NumberStats $frontendNearestFilterPassbandDistanceValues
    $candidateFrontendStrongPassbandSubthresholdDistanceStats = Get-NumberStats $candidateFrontendStrongPassbandSubthresholdDistanceValues
    $frontendAdjacentNoiseBinStats = Get-NumberStats $frontendAdjacentNoiseBinValues
    $frontendAdjacentNoiseFloorStats = Get-NumberStats $frontendAdjacentNoiseFloorValues
    $frontendAdjacentNoiseP50Stats = Get-NumberStats $frontendAdjacentNoiseP50Values
    $frontendAdjacentNoiseSlopeStats = Get-NumberStats $frontendAdjacentNoiseSlopeValues
    $frontendAdjacentNoiseRejectedStats = Get-NumberStats $frontendAdjacentNoiseRejectedValues
    $rxMetersAgeStats = Get-NumberStats $rxMetersAgeValues
    $audioAgeStats = Get-NumberStats $audioAgeValues
    $candidateInputStats = Get-NumberStats $candidateInputValues
    $candidateOutputStats = Get-NumberStats $candidateOutputValues
    $candidateInputOutputStats = Get-PairStats $candidateInputOutputXValues $candidateInputOutputYValues
    $candidateOutputMinusInputStats = Get-NumberStats $candidateOutputMinusInputValues
    $candidateWeakOutputStats = Get-NumberStats $candidateWeakOutputValues
    $candidateStrongOutputStats = Get-NumberStats $candidateStrongOutputValues
    $candidateWeakFinalAudioStats = Get-NumberStats $candidateWeakFinalAudioValues
    $candidateStrongFinalAudioStats = Get-NumberStats $candidateStrongFinalAudioValues
    $candidateNearStrongOutputStats = Get-NumberStats $candidateNearStrongOutputValues
    $candidateNearStrongFinalAudioStats = Get-NumberStats $candidateNearStrongFinalAudioValues
    $candidateSpeechQualifiedWeakOutputStats = Get-NumberStats $candidateSpeechQualifiedWeakOutputValues
    $candidateSpeechQualifiedStrongOutputStats = Get-NumberStats $candidateSpeechQualifiedStrongOutputValues
    $candidateSpeechQualifiedWeakFinalAudioStats = Get-NumberStats $candidateSpeechQualifiedWeakFinalAudioValues
    $candidateSpeechQualifiedStrongFinalAudioStats = Get-NumberStats $candidateSpeechQualifiedStrongFinalAudioValues
    $candidateSpeechQualifiedNearStrongOutputStats = Get-NumberStats $candidateSpeechQualifiedNearStrongOutputValues
    $candidateSpeechQualifiedNearStrongFinalAudioStats = Get-NumberStats $candidateSpeechQualifiedNearStrongFinalAudioValues
    $candidatePassbandQualifiedWeakOutputStats = Get-NumberStats $candidatePassbandQualifiedWeakOutputValues
    $candidatePassbandQualifiedStrongOutputStats = Get-NumberStats $candidatePassbandQualifiedStrongOutputValues
    $candidatePassbandQualifiedWeakFinalAudioStats = Get-NumberStats $candidatePassbandQualifiedWeakFinalAudioValues
    $candidatePassbandQualifiedStrongFinalAudioStats = Get-NumberStats $candidatePassbandQualifiedStrongFinalAudioValues
    $candidatePassbandQualifiedNearStrongOutputStats = Get-NumberStats $candidatePassbandQualifiedNearStrongOutputValues
    $candidatePassbandQualifiedNearStrongFinalAudioStats = Get-NumberStats $candidatePassbandQualifiedNearStrongFinalAudioValues
    $candidateMeanGainStats = Get-NumberStats $candidateMeanGainValues
    $candidateFloorReductionStats = Get-NumberStats $candidateFloorReductionValues
    $candidateDynamicRangeStats = Get-NumberStats $candidateDynamicRangeValues
    $candidateSignalConfidenceStats = Get-NumberStats $candidateSignalConfidenceValues
    $candidateAgcGateStats = Get-NumberStats $candidateAgcGateValues
    $candidateSignalProbabilityStats = Get-NumberStats $candidateSignalProbabilityValues
    $candidateTextureFillStats = Get-NumberStats $candidateTextureFillValues
    $candidateMaskSmoothingStats = Get-NumberStats $candidateMaskSmoothingValues
    $candidateLevelDriveStats = Get-NumberStats $candidateLevelDriveValues
    $candidateRecoveryDriveStats = Get-NumberStats $candidateRecoveryDriveValues
    $candidateWeakSignalMemoryStats = Get-NumberStats $candidateWeakSignalMemoryValues
    $candidateMakeupGainDbStats = Get-NumberStats $candidateMakeupGainDbValues
    $candidateOutputPeakDbfsStats = Get-NumberStats $candidateOutputPeakDbfsValues
    $candidatePeakEvidenceStats = Get-NumberStats $candidatePeakEvidenceValues
    $candidatePeakLimitDbfsStats = Get-NumberStats $candidatePeakLimitDbfsValues
    $candidatePeakReductionDbStats = Get-NumberStats $candidatePeakReductionDbValues
    $candidateAdjacentNoiseTrustStats = Get-NumberStats $candidateAdjacentNoiseTrustValues
    $candidateAdjacentNoiseDriveStats = Get-NumberStats $candidateAdjacentNoiseDriveValues
    $candidateAdjacentNoiseFloorStats = Get-NumberStats $candidateAdjacentNoiseFloorValues
    $candidateAdjacentNoiseRejectedStats = Get-NumberStats $candidateAdjacentNoiseRejectedValues
    $candidateAdjacentNoiseSideBalanceStats = Get-NumberStats $candidateAdjacentNoiseSideBalanceValues
    $candidateAdjacentNoiseAsymmetryStats = Get-NumberStats $candidateAdjacentNoiseAsymmetryValues
    $candidateAudioInputDeltaStats = Get-NumberStats $candidateAudioInputDeltaValues
    $candidateAudioOutputDeltaStats = Get-NumberStats $candidateAudioOutputDeltaValues
    $candidateLearnedFrameStats = Get-NumberStats $candidateLearnedFrameValues
    $candidateManagedChannelGenerationStats = Get-NumberStats $candidateManagedChannelGenerationValues
    $candidateManagedApplyCountStats = Get-NumberStats $candidateManagedApplyCountValues
    $candidateManagedPositionApplyCountStats = Get-NumberStats $candidateManagedPositionApplyCountValues
    $candidateManagedPolicyApplyCountStats = Get-NumberStats $candidateManagedPolicyApplyCountValues
    $candidateManagedNoopApplyCountStats = Get-NumberStats $candidateManagedNoopApplyCountValues
    $candidateManagedRunApplyCountStats = Get-NumberStats $candidateManagedRunApplyCountValues
    $candidateLearnerResetSampleCount = @($candidateLearnerResetSamples.ToArray()).Count
    $candidateManagedReapplySampleCount = @($candidateManagedReapplySamples.ToArray()).Count
    $candidateManagedCounterCoveragePct = Get-CountPercent -Count $candidateManagedCounterSampleCount -SampleCount $candidateSampleCount
    $candidateLearnedFrameCoveragePct = Get-CountPercent -Count $candidateLearnedFrameSampleCount -SampleCount $candidateSampleCount
    $candidateManagedCountersComplete = ($candidateSampleCount -gt 0 -and $candidateManagedCounterSampleCount -eq $candidateSampleCount)
    $candidateLearnedFramesComplete = ($candidateSampleCount -gt 0 -and $candidateLearnedFrameSampleCount -eq $candidateSampleCount)
    $candidateLearnerMonotonic = ($candidateLearnerResetSampleCount -eq 0)
    $candidateLearnerStabilityStatus = if ($candidateSampleCount -eq 0) {
        "not-observed"
    }
    elseif (-not $candidateLearnedFramesComplete) {
        "learned-frames-missing"
    }
    elseif ($candidateLearnerResetSampleCount -gt 0) {
        "learner-reset-watch"
    }
    elseif ($candidateManagedReapplySampleCount -gt 0) {
        "managed-reapply-watch"
    }
    elseif ($candidateManagedGenerationChangeCount -gt 0) {
        "channel-generation-changed"
    }
    elseif ($candidateManagedCounterSampleCount -eq 0) {
        "managed-counters-missing"
    }
    elseif (-not $candidateManagedCountersComplete) {
        "managed-counters-partial"
    }
    elseif ([int]$candidateLearnedFrameStats["count"] -gt 0 -and [double]$candidateLearnedFrameStats["max"] -lt 20.0) {
        "warming"
    }
    else {
        "stable"
    }
    $candidateManagedReplayEvidenceReady = (
        [string]::Equals($candidateLearnerStabilityStatus, "stable", [StringComparison]::OrdinalIgnoreCase) -and
        $candidateManagedCountersComplete -and
        $candidateLearnedFramesComplete)
    $candidateWeakInputTopSamples = @($candidateWeakInputSamples.ToArray() |
        Sort-Object @{Expression = { $v = Get-NumericValue (Get-JsonValue $_ "finalAudioRmsDbfs"); if ($null -eq $v) { [double]::PositiveInfinity } else { [double]$v } }; Ascending = $true },
            @{Expression = { $v = Get-NumericValue (Get-JsonValue $_ "outputDbfs"); if ($null -eq $v) { [double]::PositiveInfinity } else { [double]$v } }; Ascending = $true } |
        Select-Object -First 8)
    $candidateStrongInputTopSamples = @($candidateStrongInputSamples.ToArray() |
        Sort-Object @{Expression = { $v = Get-NumericValue (Get-JsonValue $_ "finalAudioRmsDbfs"); if ($null -eq $v) { [double]::NegativeInfinity } else { [double]$v } }; Descending = $true },
            @{Expression = { $v = Get-NumericValue (Get-JsonValue $_ "outputDbfs"); if ($null -eq $v) { [double]::NegativeInfinity } else { [double]$v } }; Descending = $true } |
        Select-Object -First 8)
    $candidateSpeechQualifiedWeakInputTopSamples = @($candidateSpeechQualifiedWeakInputSamples.ToArray() |
        Sort-Object @{Expression = { $v = Get-NumericValue (Get-JsonValue $_ "finalAudioRmsDbfs"); if ($null -eq $v) { [double]::PositiveInfinity } else { [double]$v } }; Ascending = $true },
            @{Expression = { $v = Get-NumericValue (Get-JsonValue $_ "outputDbfs"); if ($null -eq $v) { [double]::PositiveInfinity } else { [double]$v } }; Ascending = $true } |
        Select-Object -First 8)
    $candidateSpeechQualifiedStrongInputTopSamples = @($candidateSpeechQualifiedStrongInputSamples.ToArray() |
        Sort-Object @{Expression = { $v = Get-NumericValue (Get-JsonValue $_ "finalAudioRmsDbfs"); if ($null -eq $v) { [double]::NegativeInfinity } else { [double]$v } }; Descending = $true },
            @{Expression = { $v = Get-NumericValue (Get-JsonValue $_ "outputDbfs"); if ($null -eq $v) { [double]::NegativeInfinity } else { [double]$v } }; Descending = $true } |
        Select-Object -First 8)
    $candidatePassbandQualifiedWeakInputTopSamples = @($candidatePassbandQualifiedWeakInputSamples.ToArray() |
        Sort-Object @{Expression = { $v = Get-NumericValue (Get-JsonValue $_ "finalAudioRmsDbfs"); if ($null -eq $v) { [double]::PositiveInfinity } else { [double]$v } }; Ascending = $true },
            @{Expression = { $v = Get-NumericValue (Get-JsonValue $_ "outputDbfs"); if ($null -eq $v) { [double]::PositiveInfinity } else { [double]$v } }; Ascending = $true } |
        Select-Object -First 8)
    $candidatePassbandQualifiedStrongInputTopSamples = @($candidatePassbandQualifiedStrongInputSamples.ToArray() |
        Sort-Object @{Expression = { $v = Get-NumericValue (Get-JsonValue $_ "finalAudioRmsDbfs"); if ($null -eq $v) { [double]::NegativeInfinity } else { [double]$v } }; Descending = $true },
            @{Expression = { $v = Get-NumericValue (Get-JsonValue $_ "outputDbfs"); if ($null -eq $v) { [double]::NegativeInfinity } else { [double]$v } }; Descending = $true } |
        Select-Object -First 8)
    $candidateWeakDropoutTopSamples = @($candidateWeakDropoutSamples.ToArray() | Sort-Object outputDbfs | Select-Object -First 8)
    $candidateWeakDropoutFinalAudibleTopSamples = @($candidateWeakDropoutFinalAudibleSamples.ToArray() |
        Sort-Object @{Expression = "audioRmsDbfs"; Descending = $true }, @{Expression = "outputDbfs"; Descending = $true } |
        Select-Object -First 8)
    $candidateWeakDropoutCandidateLossTopSamples = @($candidateWeakDropoutCandidateLossSamples.ToArray() |
        Sort-Object outputDbfs |
        Select-Object -First 8)
    $candidateHotMakeupTopSamples = @($candidateHotMakeupSamples.ToArray() | Sort-Object makeupGainDb -Descending | Select-Object -First 8)
    $candidateNearStrongInputTopSamples = @($candidateNearStrongInputSamples.ToArray() |
        Sort-Object @{Expression = "distanceToStrongThresholdDb"; Descending = $false }, @{Expression = "inputDbfs"; Descending = $true } |
        Select-Object -First 8)
    $candidateFrontendStrongPassbandSubthresholdTopSamples = @($candidateFrontendStrongPassbandSubthresholdSamples.ToArray() |
        Sort-Object @{Expression = "distanceToStrongThresholdDb"; Descending = $false },
            @{Expression = { $v = Get-NumericValue (Get-JsonValue (Get-JsonValue $_ "frontendPassbandPeak") "snrDb"); if ($null -eq $v) { [double]::NegativeInfinity } else { [double]$v } }; Descending = $true } |
        Select-Object -First 8)
    $candidateAudioAlignmentMismatchTopSamples = @($candidateAudioAlignmentMismatchSamples.ToArray() |
        Sort-Object @{Expression = { [Math]::Abs([double]$_.deltaDb) }; Descending = $true } |
        Select-Object -First 8)
    $candidateAudioLevelerNormalizedTopSamples = @($candidateAudioLevelerNormalizedSamples.ToArray() |
        Sort-Object @{Expression = { [Math]::Abs([double]$_.outputDeltaDb) }; Descending = $true } |
        Select-Object -First 8)
    $candidateLearnerResetTopSamples = @($candidateLearnerResetSamples.ToArray() |
        Sort-Object sampleIndex |
        Select-Object -First 8)
    $candidateManagedReapplyTopSamples = @($candidateManagedReapplySamples.ToArray() |
        Sort-Object sampleIndex |
        Select-Object -First 8)
    $candidateManagedGenerationChangeTopSamples = @($candidateManagedGenerationChangeSamples.ToArray() |
        Sort-Object sampleIndex |
        Select-Object -First 8)
    $candidateLowEvidenceLiftTopSamples = @($candidateLowEvidenceLiftSamples.ToArray() |
        Sort-Object @{Expression = "audioRmsDbfs"; Descending = $true }, @{Expression = "outputDbfs"; Descending = $true } |
        Select-Object -First 8)
    $candidateLowEvidenceSuppressedTopSamples = @($candidateLowEvidenceSuppressedSamples.ToArray() |
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
    $candidateSpeechContinuityTopSamples = @($candidateSpeechContinuitySamples.ToArray() |
        Sort-Object @{Expression = "belowDropoutThreshold"; Descending = $true },
            @{Expression = "belowFadeThreshold"; Descending = $true },
            @{Expression = { $v = Get-NumericValue (Get-JsonValue $_ "finalAudioRmsDbfs"); if ($null -eq $v) { [double]::PositiveInfinity } else { [double]$v } }; Ascending = $true } |
        Select-Object -First 8)
    $rxAudioLevelerCandidateNoSignalNoiseCapTopSamples = @($rxAudioLevelerCandidateNoSignalNoiseCapSamples.ToArray() |
        Sort-Object @{Expression = "candidateNoiseProfilePrior"; Descending = $true }, @{Expression = "candidateNoSignalNoisePrior"; Descending = $true } |
        Select-Object -First 8)
    $rxAudioLevelerNormalStrengthControlRmsTopSamples = @($rxAudioLevelerNormalStrengthControlRmsSamples.ToArray() |
        Sort-Object @{Expression = "inputRmsDbfs"; Descending = $true }, @{Expression = "controlRmsHangDb"; Descending = $true } |
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
    $frontendNearestTuneCandidate = @(Get-FrontendTuneCandidates `
        -Samples @($frontendTopPeakSamples.ToArray()) `
        -MaxCount 64 `
        -TuneStepHz $TuneStepHz |
        Where-Object { $null -ne (Get-JsonValue $_ "retuneDeltaHz") } |
        Sort-Object @{Expression = { [Math]::Abs([double](Get-JsonValue $_ "retuneDeltaHz")) }; Ascending = $true },
            @{Expression = { [double](Get-JsonValue $_ "filterDistanceHz") }; Ascending = $true },
            @{Expression = { [double](Get-JsonValue $_ "snrDb") }; Descending = $true } |
        Select-Object -First 1)
    $frontendNearestTuneCandidateValue = if ($frontendNearestTuneCandidate.Count -le 0) { $null } else { $frontendNearestTuneCandidate[0] }
    $candidateNormalizationCompressionDb = $null
    $candidateWeakStrongOutputGapDb = $null
    $candidateWeakStrongFinalAudioGapDb = $null
    $candidateSpeechQualifiedWeakStrongOutputGapDb = $null
    $candidateSpeechQualifiedWeakStrongFinalAudioGapDb = $null
    $candidatePassbandQualifiedWeakStrongOutputGapDb = $null
    $candidatePassbandQualifiedWeakStrongFinalAudioGapDb = $null
    $candidateMixedWeakStrongGapThresholdDb = 6.0
    $candidateMixedWeakStrongFinalAudioGapThresholdDb = 4.0
    $candidateMinimumMixedWeakInputSampleCount = 3
    $candidateMinimumMixedStrongInputSampleCount = 3
    $candidateMinimumMixedEvidenceQualifiedWeakInputSampleCount = 3
    $candidateMinimumQualifiedWeakStrongInputSampleCount = 2
    $candidateSpeechQualifiedWeakStrongEvidenceReady = $false
    $candidateSpeechQualifiedWeakStrongOutputParityReady = $false
    $candidateSpeechQualifiedWeakStrongFinalAudioParityReady = $false
    $candidateSpeechQualifiedWeakStrongEvidenceStatus = "not-evaluated"
    $candidatePassbandQualifiedWeakStrongEvidenceReady = $false
    $candidatePassbandQualifiedWeakStrongOutputParityReady = $false
    $candidatePassbandQualifiedWeakStrongFinalAudioParityReady = $false
    $candidatePassbandQualifiedWeakStrongEvidenceStatus = "not-evaluated"
    $candidateMixedWeakStrongEvidenceReady = $false
    $candidateWeakStrongOutputParityReady = $false
    $candidateWeakStrongFinalAudioParityReady = $false
    $candidateMixedWeakStrongEvidenceStatus = "not-evaluated"
    $candidateLowEvidenceWeakInputCount = $candidateLowEvidenceSampleCount
    $candidateEvidenceQualifiedWeakInputCount = [Math]::Max(0, $candidateWeakInputCount - $candidateLowEvidenceWeakInputCount)
    $candidateLowEvidenceLiftPct = $null
    $candidateLowEvidenceSuppressedPct = $null
    $candidateAudioAlignmentMismatchPct = $null
    $candidateAudioAlignedAfterLevelerPct = $null
    $candidateAudioLevelerNormalizedPct = $null
    $candidateLowEvidenceAlignmentMismatchPct = $null
    $candidateWeakDropoutFinalAudiblePct = Get-CountPercent -Count $candidateWeakDropoutFinalAudibleCount -SampleCount $candidateWeakDropoutCount
    $candidateWeakDropoutNativeLiftedPct = Get-CountPercent -Count $candidateWeakDropoutNativeLiftedCount -SampleCount $candidateWeakDropoutCount
    $candidateWeakDropoutCandidateLossPct = Get-CountPercent -Count $candidateWeakDropoutCandidateLossCount -SampleCount $candidateWeakDropoutCount
    if ($null -ne $candidateInputStats["movement"] -and $null -ne $candidateOutputStats["movement"]) {
        $candidateNormalizationCompressionDb = [Math]::Round([double]$candidateInputStats["movement"] - [double]$candidateOutputStats["movement"], 3)
    }
    if ($null -ne $candidateWeakOutputStats["average"] -and $null -ne $candidateStrongOutputStats["average"]) {
        $candidateWeakStrongOutputGapDb = [Math]::Round([double]$candidateStrongOutputStats["average"] - [double]$candidateWeakOutputStats["average"], 3)
    }
    if ($null -ne $candidateWeakFinalAudioStats["average"] -and $null -ne $candidateStrongFinalAudioStats["average"]) {
        $candidateWeakStrongFinalAudioGapDb = [Math]::Round([double]$candidateStrongFinalAudioStats["average"] - [double]$candidateWeakFinalAudioStats["average"], 3)
    }
    if ($null -ne $candidateSpeechQualifiedWeakOutputStats["average"] -and $null -ne $candidateSpeechQualifiedStrongOutputStats["average"]) {
        $candidateSpeechQualifiedWeakStrongOutputGapDb = [Math]::Round([double]$candidateSpeechQualifiedStrongOutputStats["average"] - [double]$candidateSpeechQualifiedWeakOutputStats["average"], 3)
    }
    if ($null -ne $candidateSpeechQualifiedWeakFinalAudioStats["average"] -and $null -ne $candidateSpeechQualifiedStrongFinalAudioStats["average"]) {
        $candidateSpeechQualifiedWeakStrongFinalAudioGapDb = [Math]::Round([double]$candidateSpeechQualifiedStrongFinalAudioStats["average"] - [double]$candidateSpeechQualifiedWeakFinalAudioStats["average"], 3)
    }
    if ($null -ne $candidatePassbandQualifiedWeakOutputStats["average"] -and $null -ne $candidatePassbandQualifiedStrongOutputStats["average"]) {
        $candidatePassbandQualifiedWeakStrongOutputGapDb = [Math]::Round([double]$candidatePassbandQualifiedStrongOutputStats["average"] - [double]$candidatePassbandQualifiedWeakOutputStats["average"], 3)
    }
    if ($null -ne $candidatePassbandQualifiedWeakFinalAudioStats["average"] -and $null -ne $candidatePassbandQualifiedStrongFinalAudioStats["average"]) {
        $candidatePassbandQualifiedWeakStrongFinalAudioGapDb = [Math]::Round([double]$candidatePassbandQualifiedStrongFinalAudioStats["average"] - [double]$candidatePassbandQualifiedWeakFinalAudioStats["average"], 3)
    }
    if ($candidateSampleCount -le 0) {
        $candidateSpeechQualifiedWeakStrongEvidenceStatus = "no-candidate-samples"
    }
    elseif ($candidateSpeechQualifiedWeakOutputValues.Count -le 0 -and $candidateSpeechQualifiedStrongOutputValues.Count -le 0) {
        $candidateSpeechQualifiedWeakStrongEvidenceStatus = "missing-weak-and-strong-speech"
    }
    elseif ($candidateSpeechQualifiedWeakOutputValues.Count -le 0) {
        $candidateSpeechQualifiedWeakStrongEvidenceStatus = "missing-weak-speech"
    }
    elseif ($candidateSpeechQualifiedStrongOutputValues.Count -le 0) {
        $candidateSpeechQualifiedWeakStrongEvidenceStatus = "missing-strong-speech"
    }
    elseif ($candidateSpeechQualifiedWeakOutputValues.Count -lt $candidateMinimumQualifiedWeakStrongInputSampleCount -and
        $candidateSpeechQualifiedStrongOutputValues.Count -lt $candidateMinimumQualifiedWeakStrongInputSampleCount) {
        $candidateSpeechQualifiedWeakStrongEvidenceStatus = "insufficient-weak-and-strong-speech-evidence"
    }
    elseif ($candidateSpeechQualifiedWeakOutputValues.Count -lt $candidateMinimumQualifiedWeakStrongInputSampleCount) {
        $candidateSpeechQualifiedWeakStrongEvidenceStatus = "insufficient-weak-speech-evidence"
    }
    elseif ($candidateSpeechQualifiedStrongOutputValues.Count -lt $candidateMinimumQualifiedWeakStrongInputSampleCount) {
        $candidateSpeechQualifiedWeakStrongEvidenceStatus = "insufficient-strong-speech-evidence"
    }
    elseif ($null -ne $candidateSpeechQualifiedWeakStrongFinalAudioGapDb -and
        [Math]::Abs([double]$candidateSpeechQualifiedWeakStrongFinalAudioGapDb) -le $candidateMixedWeakStrongFinalAudioGapThresholdDb) {
        $candidateSpeechQualifiedWeakStrongEvidenceStatus = "ready-final-audio"
    }
    elseif ($null -ne $candidateSpeechQualifiedWeakStrongOutputGapDb -and
        [Math]::Abs([double]$candidateSpeechQualifiedWeakStrongOutputGapDb) -le $candidateMixedWeakStrongGapThresholdDb) {
        $candidateSpeechQualifiedWeakStrongEvidenceStatus = "ready-output"
    }
    else {
        $candidateSpeechQualifiedWeakStrongEvidenceStatus = "weak-strong-speech-gap-watch"
    }
    $candidateSpeechQualifiedWeakStrongSampleCountReady = ($candidateSpeechQualifiedWeakOutputValues.Count -ge $candidateMinimumQualifiedWeakStrongInputSampleCount -and
        $candidateSpeechQualifiedStrongOutputValues.Count -ge $candidateMinimumQualifiedWeakStrongInputSampleCount)
    $candidateSpeechQualifiedWeakStrongEvidenceReady = ($candidateSpeechQualifiedWeakStrongSampleCountReady -and (
        [string]::Equals($candidateSpeechQualifiedWeakStrongEvidenceStatus, "ready-final-audio", [StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($candidateSpeechQualifiedWeakStrongEvidenceStatus, "ready-output", [StringComparison]::OrdinalIgnoreCase)))
    $candidateSpeechQualifiedWeakStrongOutputParityReady = ($candidateSpeechQualifiedWeakStrongSampleCountReady -and
        $null -ne $candidateSpeechQualifiedWeakStrongOutputGapDb -and
        [Math]::Abs([double]$candidateSpeechQualifiedWeakStrongOutputGapDb) -le $candidateMixedWeakStrongGapThresholdDb)
    $candidateSpeechQualifiedWeakStrongFinalAudioParityReady = ($candidateSpeechQualifiedWeakStrongSampleCountReady -and
        $null -ne $candidateSpeechQualifiedWeakStrongFinalAudioGapDb -and
        [Math]::Abs([double]$candidateSpeechQualifiedWeakStrongFinalAudioGapDb) -le $candidateMixedWeakStrongFinalAudioGapThresholdDb)
    if ($candidateSampleCount -le 0) {
        $candidatePassbandQualifiedWeakStrongEvidenceStatus = "no-candidate-samples"
    }
    elseif ($candidatePassbandQualifiedWeakOutputValues.Count -le 0 -and $candidatePassbandQualifiedStrongOutputValues.Count -le 0) {
        $candidatePassbandQualifiedWeakStrongEvidenceStatus = "missing-weak-and-strong-passband-speech"
    }
    elseif ($candidatePassbandQualifiedWeakOutputValues.Count -le 0) {
        $candidatePassbandQualifiedWeakStrongEvidenceStatus = "missing-weak-passband-speech"
    }
    elseif ($candidatePassbandQualifiedStrongOutputValues.Count -le 0) {
        $candidatePassbandQualifiedWeakStrongEvidenceStatus = "missing-strong-passband-speech"
    }
    elseif ($candidatePassbandQualifiedWeakOutputValues.Count -lt $candidateMinimumQualifiedWeakStrongInputSampleCount -and
        $candidatePassbandQualifiedStrongOutputValues.Count -lt $candidateMinimumQualifiedWeakStrongInputSampleCount) {
        $candidatePassbandQualifiedWeakStrongEvidenceStatus = "insufficient-weak-and-strong-passband-speech-evidence"
    }
    elseif ($candidatePassbandQualifiedWeakOutputValues.Count -lt $candidateMinimumQualifiedWeakStrongInputSampleCount) {
        $candidatePassbandQualifiedWeakStrongEvidenceStatus = "insufficient-weak-passband-speech-evidence"
    }
    elseif ($candidatePassbandQualifiedStrongOutputValues.Count -lt $candidateMinimumQualifiedWeakStrongInputSampleCount) {
        $candidatePassbandQualifiedWeakStrongEvidenceStatus = "insufficient-strong-passband-speech-evidence"
    }
    elseif ($null -ne $candidatePassbandQualifiedWeakStrongFinalAudioGapDb -and
        [Math]::Abs([double]$candidatePassbandQualifiedWeakStrongFinalAudioGapDb) -le $candidateMixedWeakStrongFinalAudioGapThresholdDb) {
        $candidatePassbandQualifiedWeakStrongEvidenceStatus = "ready-final-audio"
    }
    elseif ($null -ne $candidatePassbandQualifiedWeakStrongOutputGapDb -and
        [Math]::Abs([double]$candidatePassbandQualifiedWeakStrongOutputGapDb) -le $candidateMixedWeakStrongGapThresholdDb) {
        $candidatePassbandQualifiedWeakStrongEvidenceStatus = "ready-output"
    }
    else {
        $candidatePassbandQualifiedWeakStrongEvidenceStatus = "weak-strong-passband-speech-gap-watch"
    }
    $candidatePassbandQualifiedWeakStrongSampleCountReady = ($candidatePassbandQualifiedWeakOutputValues.Count -ge $candidateMinimumQualifiedWeakStrongInputSampleCount -and
        $candidatePassbandQualifiedStrongOutputValues.Count -ge $candidateMinimumQualifiedWeakStrongInputSampleCount)
    $candidatePassbandQualifiedWeakStrongEvidenceReady = ($candidatePassbandQualifiedWeakStrongSampleCountReady -and (
        [string]::Equals($candidatePassbandQualifiedWeakStrongEvidenceStatus, "ready-final-audio", [StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($candidatePassbandQualifiedWeakStrongEvidenceStatus, "ready-output", [StringComparison]::OrdinalIgnoreCase)))
    $candidatePassbandQualifiedWeakStrongOutputParityReady = ($candidatePassbandQualifiedWeakStrongSampleCountReady -and
        $null -ne $candidatePassbandQualifiedWeakStrongOutputGapDb -and
        [Math]::Abs([double]$candidatePassbandQualifiedWeakStrongOutputGapDb) -le $candidateMixedWeakStrongGapThresholdDb)
    $candidatePassbandQualifiedWeakStrongFinalAudioParityReady = ($candidatePassbandQualifiedWeakStrongSampleCountReady -and
        $null -ne $candidatePassbandQualifiedWeakStrongFinalAudioGapDb -and
        [Math]::Abs([double]$candidatePassbandQualifiedWeakStrongFinalAudioGapDb) -le $candidateMixedWeakStrongFinalAudioGapThresholdDb)
    if ($candidateSampleCount -le 0) {
        $candidateMixedWeakStrongEvidenceStatus = "no-candidate-samples"
    }
    elseif ($candidateWeakInputCount -le 0 -and $candidateStrongInputCount -le 0) {
        $candidateMixedWeakStrongEvidenceStatus = "missing-weak-and-strong-input"
    }
    elseif ($candidateWeakInputCount -le 0) {
        $candidateMixedWeakStrongEvidenceStatus = "missing-weak-input"
    }
    elseif ($candidateStrongInputCount -le 0) {
        $candidateMixedWeakStrongEvidenceStatus = "missing-strong-input"
    }
    elseif ($candidateEvidenceQualifiedWeakInputCount -le 0) {
        $candidateMixedWeakStrongEvidenceStatus = "low-evidence-weak-input"
    }
    elseif ($candidateSpeechQualifiedWeakStrongFinalAudioParityReady -or
        $candidatePassbandQualifiedWeakStrongFinalAudioParityReady) {
        $candidateMixedWeakStrongEvidenceStatus = "ready-final-audio"
    }
    elseif ($null -ne $candidateWeakStrongFinalAudioGapDb -and
        [Math]::Abs([double]$candidateWeakStrongFinalAudioGapDb) -le $candidateMixedWeakStrongFinalAudioGapThresholdDb) {
        $candidateMixedWeakStrongEvidenceStatus = "ready-final-audio"
    }
    elseif ($candidateWeakInputCount -lt $candidateMinimumMixedWeakInputSampleCount -or
        $candidateEvidenceQualifiedWeakInputCount -lt $candidateMinimumMixedEvidenceQualifiedWeakInputSampleCount) {
        $candidateMixedWeakStrongEvidenceStatus = "insufficient-weak-input-evidence"
    }
    elseif ($candidateStrongInputCount -lt $candidateMinimumMixedStrongInputSampleCount) {
        $candidateMixedWeakStrongEvidenceStatus = "insufficient-strong-input-evidence"
    }
    elseif ($null -eq $candidateWeakStrongOutputGapDb -and $null -eq $candidateWeakStrongFinalAudioGapDb) {
        $candidateMixedWeakStrongEvidenceStatus = "missing-output-gap"
    }
    elseif ($null -ne $candidateWeakStrongOutputGapDb -and
        [Math]::Abs([double]$candidateWeakStrongOutputGapDb) -gt $candidateMixedWeakStrongGapThresholdDb) {
        $candidateMixedWeakStrongEvidenceStatus = "weak-strong-output-gap-watch"
    }
    elseif ($null -ne $candidateWeakStrongFinalAudioGapDb -and
        [Math]::Abs([double]$candidateWeakStrongFinalAudioGapDb) -gt $candidateMixedWeakStrongFinalAudioGapThresholdDb) {
        $candidateMixedWeakStrongEvidenceStatus = "weak-strong-output-gap-watch"
    }
    elseif ($null -eq $candidateWeakStrongOutputGapDb) {
        $candidateMixedWeakStrongEvidenceStatus = "missing-output-gap"
    }
    else {
        $candidateMixedWeakStrongEvidenceStatus = "ready"
    }
    $candidateMixedWeakStrongEvidenceReady =
        [string]::Equals($candidateMixedWeakStrongEvidenceStatus, "ready", [StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($candidateMixedWeakStrongEvidenceStatus, "ready-final-audio", [StringComparison]::OrdinalIgnoreCase)
    $candidateMixedWeakStrongInputEvidenceInsufficient =
        [string]::Equals($candidateMixedWeakStrongEvidenceStatus, "insufficient-weak-input-evidence", [StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($candidateMixedWeakStrongEvidenceStatus, "insufficient-strong-input-evidence", [StringComparison]::OrdinalIgnoreCase)
    $candidateWeakStrongOutputParityReady = ($null -ne $candidateWeakStrongOutputGapDb -and
        [Math]::Abs([double]$candidateWeakStrongOutputGapDb) -le $candidateMixedWeakStrongGapThresholdDb)
    $candidateWeakStrongFinalAudioParityReady = ($null -ne $candidateWeakStrongFinalAudioGapDb -and
        [Math]::Abs([double]$candidateWeakStrongFinalAudioGapDb) -le $candidateMixedWeakStrongFinalAudioGapThresholdDb)
    $candidateMixedWeakStrongOutputGapDirection = Get-GapDirection `
        -GapDb $candidateWeakStrongOutputGapDb `
        -ThresholdDb $candidateMixedWeakStrongGapThresholdDb
    $candidateMixedWeakStrongFinalAudioGapDirection = Get-GapDirection `
        -GapDb $candidateWeakStrongFinalAudioGapDb `
        -ThresholdDb $candidateMixedWeakStrongFinalAudioGapThresholdDb
    $candidateMixedWeakStrongOutputGapExcessDb = Get-GapExcessDb `
        -GapDb $candidateWeakStrongOutputGapDb `
        -ThresholdDb $candidateMixedWeakStrongGapThresholdDb
    $candidateMixedWeakStrongFinalAudioGapExcessDb = Get-GapExcessDb `
        -GapDb $candidateWeakStrongFinalAudioGapDb `
        -ThresholdDb $candidateMixedWeakStrongFinalAudioGapThresholdDb
    $candidateWeakOutputLiftNeededDb = $null
    $candidateWeakOutputTrimNeededDb = $null
    if ([string]::Equals($candidateMixedWeakStrongOutputGapDirection, "weak-too-low", [StringComparison]::OrdinalIgnoreCase)) {
        $candidateWeakOutputLiftNeededDb = $candidateMixedWeakStrongOutputGapExcessDb
        $candidateWeakOutputTrimNeededDb = 0.0
    }
    elseif ([string]::Equals($candidateMixedWeakStrongOutputGapDirection, "weak-too-hot", [StringComparison]::OrdinalIgnoreCase)) {
        $candidateWeakOutputLiftNeededDb = 0.0
        $candidateWeakOutputTrimNeededDb = $candidateMixedWeakStrongOutputGapExcessDb
    }
    elseif (-not [string]::Equals($candidateMixedWeakStrongOutputGapDirection, "unknown", [StringComparison]::OrdinalIgnoreCase)) {
        $candidateWeakOutputLiftNeededDb = 0.0
        $candidateWeakOutputTrimNeededDb = 0.0
    }
    $candidateWeakFinalAudioLiftNeededDb = $null
    $candidateWeakFinalAudioTrimNeededDb = $null
    if ([string]::Equals($candidateMixedWeakStrongFinalAudioGapDirection, "weak-too-low", [StringComparison]::OrdinalIgnoreCase)) {
        $candidateWeakFinalAudioLiftNeededDb = $candidateMixedWeakStrongFinalAudioGapExcessDb
        $candidateWeakFinalAudioTrimNeededDb = 0.0
    }
    elseif ([string]::Equals($candidateMixedWeakStrongFinalAudioGapDirection, "weak-too-hot", [StringComparison]::OrdinalIgnoreCase)) {
        $candidateWeakFinalAudioLiftNeededDb = 0.0
        $candidateWeakFinalAudioTrimNeededDb = $candidateMixedWeakStrongFinalAudioGapExcessDb
    }
    elseif (-not [string]::Equals($candidateMixedWeakStrongFinalAudioGapDirection, "unknown", [StringComparison]::OrdinalIgnoreCase)) {
        $candidateWeakFinalAudioLiftNeededDb = 0.0
        $candidateWeakFinalAudioTrimNeededDb = 0.0
    }
    $candidateMixedWeakStrongPreferredAction = switch ($candidateMixedWeakStrongEvidenceStatus) {
        "weak-strong-output-gap-watch" {
            if ([string]::Equals($candidateMixedWeakStrongFinalAudioGapDirection, "within-parity", [StringComparison]::OrdinalIgnoreCase)) {
                "post-leveler-parity-ready-inspect-native-gap-before-changing-makeup"
            }
            elseif ([string]::Equals($candidateMixedWeakStrongFinalAudioGapDirection, "weak-too-low", [StringComparison]::OrdinalIgnoreCase)) {
                "tune-bounded-weak-speech-lift-from-top-weak-and-strong-input-rows"
            }
            elseif ([string]::Equals($candidateMixedWeakStrongFinalAudioGapDirection, "weak-too-hot", [StringComparison]::OrdinalIgnoreCase)) {
                "tighten-weak-branch-boost-or-release-from-top-weak-input-rows"
            }
            else {
                "inspect-top-weak-and-strong-input-rows-before-changing-candidate-makeup"
            }
            break
        }
        "missing-strong-input" { "retune-or-extend-dwell-using-frontend-candidates-and-near-strong-rows"; break }
        "missing-weak-input" { "capture-weaker-or-fading-speech-before-volume-parity-tuning"; break }
        "missing-weak-and-strong-input" { "retune-or-extend-capture-before-using-trace-for-mixed-evidence"; break }
        "missing-output-gap" { "inspect-candidate-output-diagnostics-before-parity-tuning"; break }
        "low-evidence-weak-input" { "capture-speech-qualified-weak-input-before-accepting-final-audio-parity"; break }
        "insufficient-weak-input-evidence" { "extend-dwell-or-capture-more-qualified-weak-input-before-tuning"; break }
        "insufficient-strong-input-evidence" { "extend-dwell-or-capture-more-strong-input-before-tuning"; break }
        "ready-final-audio" { "post-leveler-final-audio-parity-ready"; break }
        "ready" {
            if ([string]::Equals($candidateMixedWeakStrongFinalAudioGapDirection, "within-parity", [StringComparison]::OrdinalIgnoreCase)) {
                "native-output-and-final-audio-parity-ready"
            }
            elseif ([string]::Equals($candidateMixedWeakStrongFinalAudioGapDirection, "weak-too-low", [StringComparison]::OrdinalIgnoreCase)) {
                "native-output-parity-ready-but-final-audio-needs-bounded-weak-lift"
            }
            elseif ([string]::Equals($candidateMixedWeakStrongFinalAudioGapDirection, "weak-too-hot", [StringComparison]::OrdinalIgnoreCase)) {
                "native-output-parity-ready-but-final-audio-weak-branch-too-hot"
            }
            else {
                "native-output-parity-ready-final-audio-unverified"
            }
            break
        }
        default { "collect-ready-candidate-mixed-weak-strong-trace"; break }
    }
    if ([string]::Equals($candidateMixedWeakStrongEvidenceStatus, "missing-strong-input", [StringComparison]::OrdinalIgnoreCase) -and
        $candidateFrontendStrongPassbandSubthresholdSamples.Count -gt 0) {
        $candidateMixedWeakStrongPreferredAction = "inspect-frontend-strong-passband-subthreshold-inputs-before-changing-dsp"
    }
    $candidateMixedWeakStrongTuningFocus = [ordered]@{
        status = $candidateMixedWeakStrongEvidenceStatus
        preferredAction = $candidateMixedWeakStrongPreferredAction
        weakInputSampleCount = $candidateWeakInputCount
        lowEvidenceWeakInputSampleCount = $candidateLowEvidenceWeakInputCount
        evidenceQualifiedWeakInputSampleCount = $candidateEvidenceQualifiedWeakInputCount
        strongInputSampleCount = $candidateStrongInputCount
        nearStrongInputSampleCount = $candidateNearStrongInputCount
        frontendStrongPassbandCandidateSubthresholdSampleCount = $candidateFrontendStrongPassbandSubthresholdSamples.Count
        minimumWeakInputSampleCount = $candidateMinimumMixedWeakInputSampleCount
        minimumEvidenceQualifiedWeakInputSampleCount = $candidateMinimumMixedEvidenceQualifiedWeakInputSampleCount
        minimumStrongInputSampleCount = $candidateMinimumMixedStrongInputSampleCount
        minimumQualifiedWeakStrongInputSampleCount = $candidateMinimumQualifiedWeakStrongInputSampleCount
        weakInputSampleDeficit = [Math]::Max(0, $candidateMinimumMixedWeakInputSampleCount - $candidateWeakInputCount)
        evidenceQualifiedWeakInputSampleDeficit = [Math]::Max(0, $candidateMinimumMixedEvidenceQualifiedWeakInputSampleCount - $candidateEvidenceQualifiedWeakInputCount)
        strongInputSampleDeficit = [Math]::Max(0, $candidateMinimumMixedStrongInputSampleCount - $candidateStrongInputCount)
        speechQualifiedWeakInputSampleDeficit = [Math]::Max(0, $candidateMinimumQualifiedWeakStrongInputSampleCount - $candidateSpeechQualifiedWeakOutputValues.Count)
        speechQualifiedStrongInputSampleDeficit = [Math]::Max(0, $candidateMinimumQualifiedWeakStrongInputSampleCount - $candidateSpeechQualifiedStrongOutputValues.Count)
        passbandQualifiedWeakInputSampleDeficit = [Math]::Max(0, $candidateMinimumQualifiedWeakStrongInputSampleCount - $candidatePassbandQualifiedWeakOutputValues.Count)
        passbandQualifiedStrongInputSampleDeficit = [Math]::Max(0, $candidateMinimumQualifiedWeakStrongInputSampleCount - $candidatePassbandQualifiedStrongOutputValues.Count)
        outputGapDb = $candidateWeakStrongOutputGapDb
        outputGapDirection = $candidateMixedWeakStrongOutputGapDirection
        outputGapExcessDb = $candidateMixedWeakStrongOutputGapExcessDb
        weakOutputLiftNeededDb = $candidateWeakOutputLiftNeededDb
        weakOutputTrimNeededDb = $candidateWeakOutputTrimNeededDb
        finalAudioGapDb = $candidateWeakStrongFinalAudioGapDb
        finalAudioGapDirection = $candidateMixedWeakStrongFinalAudioGapDirection
        finalAudioGapExcessDb = $candidateMixedWeakStrongFinalAudioGapExcessDb
        weakFinalAudioLiftNeededDb = $candidateWeakFinalAudioLiftNeededDb
        weakFinalAudioTrimNeededDb = $candidateWeakFinalAudioTrimNeededDb
        speechQualifiedOutputGapDb = $candidateSpeechQualifiedWeakStrongOutputGapDb
        speechQualifiedFinalAudioGapDb = $candidateSpeechQualifiedWeakStrongFinalAudioGapDb
        speechQualifiedStatus = $candidateSpeechQualifiedWeakStrongEvidenceStatus
        passbandQualifiedOutputGapDb = $candidatePassbandQualifiedWeakStrongOutputGapDb
        passbandQualifiedFinalAudioGapDb = $candidatePassbandQualifiedWeakStrongFinalAudioGapDb
        passbandQualifiedStatus = $candidatePassbandQualifiedWeakStrongEvidenceStatus
        topWeakInputs = @($candidateWeakInputTopSamples)
        topStrongInputs = @($candidateStrongInputTopSamples)
        topSpeechQualifiedWeakInputs = @($candidateSpeechQualifiedWeakInputTopSamples)
        topSpeechQualifiedStrongInputs = @($candidateSpeechQualifiedStrongInputTopSamples)
        topPassbandQualifiedWeakInputs = @($candidatePassbandQualifiedWeakInputTopSamples)
        topPassbandQualifiedStrongInputs = @($candidatePassbandQualifiedStrongInputTopSamples)
        topFrontendStrongPassbandCandidateSubthresholdInputs = @($candidateFrontendStrongPassbandSubthresholdTopSamples)
    }
    if ($candidateLowEvidenceSampleCount -gt 0) {
        $candidateLowEvidenceLiftPct = [Math]::Round(100.0 * $candidateLowEvidenceLiftCount / $candidateLowEvidenceSampleCount, 1)
        $candidateLowEvidenceSuppressedPct = [Math]::Round(100.0 * $candidateLowEvidenceSuppressedCount / $candidateLowEvidenceSampleCount, 1)
        $candidateLowEvidenceAlignmentMismatchPct = [Math]::Round(100.0 * $candidateLowEvidenceAlignmentMismatchCount / $candidateLowEvidenceSampleCount, 1)
    }
    if ($candidateAudioAlignmentSampleCount -gt 0) {
        $candidateAudioAlignmentMismatchPct = [Math]::Round(100.0 * $candidateAudioAlignmentMismatchCount / $candidateAudioAlignmentSampleCount, 1)
        $candidateAudioAlignedAfterLevelerPct = [Math]::Round(100.0 * $candidateAudioAlignedAfterLevelerCount / $candidateAudioAlignmentSampleCount, 1)
        $candidateAudioLevelerNormalizedPct = [Math]::Round(100.0 * $candidateAudioLevelerNormalizedCount / $candidateAudioAlignmentSampleCount, 1)
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
    $passbandEvidenceMissing = ($candidateSampleCount -gt 0 -and
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
    $candidateLowEvidenceSuppressionDominates = ($candidateLowEvidenceSampleCount -gt 0 -and
        $null -ne $candidateLowEvidenceSuppressedPct -and
        [double]$candidateLowEvidenceSuppressedPct -ge 50.0 -and
        ($null -eq $candidateLowEvidenceLiftPct -or [double]$candidateLowEvidenceLiftPct -lt 20.0))
    $candidateNormalizationMotionIsFloorSuppression = ($candidateLowEvidenceSuppressionDominates -and
        $candidateWeakDropoutCandidateLossCount -eq 0 -and
        $candidateLowEvidenceLiftCount -eq 0)
    $quietIntermittentTrace = ($runtimeCount -gt 0 -and
        $null -ne $signalFloorAudioPct -and [double]$signalFloorAudioPct -ge 50.0 -and
        $null -ne $signalActiveAudioPct -and [double]$signalActiveAudioPct -ge 5.0 -and [double]$signalActiveAudioPct -le 45.0 -and
        $signalIntermittentBurstCount -gt 0)

    $candidateSpeechContinuityFadePct = Get-CountPercent -Count $candidateSpeechContinuityFadeCount -SampleCount $candidateSpeechContinuitySampleCount
    $candidateSpeechContinuityDropoutPct = Get-CountPercent -Count $candidateSpeechContinuityDropoutCount -SampleCount $candidateSpeechContinuitySampleCount
    $candidateSpeechContinuityOutputMovementDb = if ([int]$candidateSpeechContinuityOutputStats["count"] -gt 1) { [double]$candidateSpeechContinuityOutputStats["movement"] } else { $null }
    $candidateSpeechContinuityGainMovementDb = if ([int]$candidateSpeechContinuityGainStats["count"] -gt 1) { [double]$candidateSpeechContinuityGainStats["movement"] } else { $null }
    $candidateSpeechContinuityStatus = if ($candidateSampleCount -eq 0 -or $candidateSpeechContinuitySampleCount -eq 0) {
        "not-observed"
    }
elseif ($candidateSpeechContinuityDropoutCount -gt 0) {
        "speech-dropout-risk"
    }
    elseif ($candidateSpeechContinuityFadeCount -gt 0) {
        "speech-fade-risk"
    }
    elseif (($null -ne $candidateSpeechContinuityOutputMovementDb -and $candidateSpeechContinuityOutputMovementDb -gt $candidateSpeechContinuityOutputMovementThresholdDb) -or
        ($null -ne $candidateSpeechContinuityGainMovementDb -and $candidateSpeechContinuityGainMovementDb -gt $candidateSpeechContinuityGainMovementThresholdDb)) {
        "speech-pumping-risk"
    }
    else {
        "stable"
    }
    $candidateSpeechContinuityNeedsReview = -not (
        [string]::Equals($candidateSpeechContinuityStatus, "stable", [StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($candidateSpeechContinuityStatus, "not-observed", [StringComparison]::OrdinalIgnoreCase))

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
    if ($candidateSampleCount -gt 0 -and $candidateAlignedCount -lt $candidateSampleCount) {
        $summaryRecommendations.Add("Not every comparison diagnostics sample matched the requested comparison state; reassert the comparison setting before judging behavior.") | Out-Null
    }
    if ($candidateSampleCount -gt 0 -and $candidateAgcDiagnosticCount -lt $candidateSampleCount) {
        $summaryRecommendations.Add("Recapture this comparison trace after restarting the backend; recovery-drive and makeup-gain evidence is missing.") | Out-Null
    }
    if ($candidateSampleCount -gt 0 -and $candidateProbabilityDiagnosticCount -lt $candidateSampleCount) {
        $summaryRecommendations.Add("Recapture this comparison trace after restarting the backend; signal-probability and texture evidence is missing.") | Out-Null
    }
    if ($candidateSampleCount -gt 0 -and $candidatePeakDiagnosticCount -lt $candidateSampleCount) {
        $summaryRecommendations.Add("Recapture this comparison trace after restarting the backend; output peak and adaptive-knee evidence is missing.") | Out-Null
    }
    if ($candidateLearnerResetSampleCount -gt 0) {
        $summaryRecommendations.Add("Comparison learned-frame counters reset during this trace; inspect candidateLearnerStabilityWatch before using it as acceptance evidence.") | Out-Null
    }
    elseif ($candidateManagedReapplySampleCount -gt 0) {
        $summaryRecommendations.Add("Managed Candidate position or policy was re-applied during this trace; inspect candidateLearnerStabilityWatch for state replay before judging Candidate weak-signal behavior.") | Out-Null
    }
    elseif ($candidateManagedGenerationChangeCount -gt 0) {
        $summaryRecommendations.Add("Comparison channel generation changed during this trace; recapture after the channel lifecycle is stable before accepting learner stability evidence.") | Out-Null
    }
    elseif ($candidateSampleCount -gt 0 -and $candidateManagedCounterSampleCount -lt $candidateSampleCount) {
        $summaryRecommendations.Add("Restart or rebuild the backend so every comparison diagnostics sample includes managed replay counters before accepting learner-stability evidence.") | Out-Null
    }
    if ($frontendAdjacentNoiseUsableCount -gt 0 -and $candidateSampleCount -gt 0 -and $candidateAdjacentNoiseUsableCount -eq 0) {
        $summaryRecommendations.Add("Frontend adjacent-noise profiles were usable, but the comparison diagnostics did not report adjacent-profile trust; verify the runtime is publishing adjacent-noise evidence.") | Out-Null
    }
    if ($candidateAdjacentNoiseUsableCount -gt 0 -and
        [int]$candidateAdjacentNoiseSideBalanceStats["count"] -gt 0 -and
        [double]$candidateAdjacentNoiseSideBalanceStats["average"] -lt 0.45) {
        $summaryRecommendations.Add("Native Candidate adjacent-noise profile is mostly one-sided or imbalanced in this trace; avoid increasing adjacent suppression from this run without checking nearby QRM/splatter.") | Out-Null
    }
    if ($rxAudioLevelerCandidateNoSignalNoiseCapCount -gt 0) {
        $summaryRecommendations.Add("Comparison no-signal noise cap fired in this trace; inspect rxAudioLevelerWatch candidate prior fields and topCandidateNoSignalNoiseCapSamples before judging weak-signal muting.") | Out-Null
    }
    if ($rxAudioLevelerNormalStrengthControlRmsValidCount -gt 0) {
        $summaryRecommendations.Add("Stable-speech RX leveler control memory was active on normal-strength input; inspect rxAudioLevelerWatch.topNormalStrengthControlRmsSamples before accepting the candidate as no-pumping evidence.") | Out-Null
    }
    switch ($candidateSpeechContinuityStatus) {
        "speech-dropout-risk" {
            $summaryRecommendations.Add("Comparison speech-continuity samples dropped below audible final audio; tune held weak-speech rescue or leveler release before tightening noise gates.") | Out-Null
        }
        "speech-fade-risk" {
            $summaryRecommendations.Add("Comparison speech-continuity samples faded below the final-audio target; tune held weak-speech rescue or leveler release before tightening noise gates.") | Out-Null
        }
        "speech-pumping-risk" {
            $summaryRecommendations.Add("Comparison speech-continuity output or applied gain movement is high; tune leveler release/hold before raising makeup or suppression.") | Out-Null
        }
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
            $summaryRecommendations.Add("AGC movement coincided with RX audio leveler constraints; inspect agcStabilityWatch and rxAudioLevelerWatch before changing AGC or Candidate makeup/release.") | Out-Null
        }
        "quiet-floor-movement" {
            $summaryRecommendations.Add("AGC gain moved mostly on quiet/no-evidence floor samples; inspect agcStabilityWatch.quietNoEvidenceAgcGainDb before treating it as audible pumping.") | Out-Null
        }
        "wide-level-transition" {
            $summaryRecommendations.Add("AGC gain moved more than 12 dB during the trace; compare active, voice-like, and quiet AGC buckets before tuning NR.") | Out-Null
        }
    }
    if (-not $candidateLowEvidenceSuppressionDominates -and [int]$rmsStats["count"] -gt 1 -and [double]$rmsStats["movement"] -gt 10.0) {
        $summaryRecommendations.Add("Final audio RMS moved more than 10 dB during the trace; pair this JSONL with audio render evidence before approving changes.") | Out-Null
    }
    if ([int]$rmsStats["count"] -gt 1 -and [double]$rmsStats["movement"] -gt 10.0) {
        $activeMovement = if ([int]$activeAudioRmsStats["count"] -gt 1) { [double]$activeAudioRmsStats["movement"] } else { $null }
        $floorMovement = if ([int]$floorAudioRmsStats["count"] -gt 1) { [double]$floorAudioRmsStats["movement"] } else { $null }
        if ($null -eq $activeMovement -or $activeMovement -le 6.0) {
            $summaryRecommendations.Add("Wide final-audio movement is mostly cross-bucket floor/active contrast; compare signalOccupancyWatch.activeAudioRmsDbfs and floorAudioRmsDbfs before treating it as pumping.") | Out-Null
        }
        elseif ($null -ne $floorMovement -and $floorMovement -gt 10.0) {
            $summaryRecommendations.Add("Floor-only audio movement is high; inspect signalOccupancyWatch.floorAudioRmsDbfs before increasing Candidate gain or release speed.") | Out-Null
        }
    }
    if ($quietIntermittentTrace) {
        $summaryRecommendations.Add("Trace is mostly quiet floor with intermittent signal bursts; use signalOccupancyWatch before interpreting wide audio movement as NR or leveler pumping.") | Out-Null
    }
    if ($runtimeCount -gt 0 -and $rxAudioLevelerDiagnosticCount -lt $runtimeCount) {
        $summaryRecommendations.Add("Recapture this trace after restarting a backend that exports RX audio leveler diagnostics; final loudness normalization evidence is incomplete.") | Out-Null
    }
    if ($rxAudioLevelerExperimentalSampleCount -gt 0) {
        $summaryRecommendations.Add("RX audio leveler experimental profile was active; pair this trace with a matched current-profile baseline before promoting leveler changes.") | Out-Null
    }
    if ($txOutputHeadroomExperimentalSampleCount -gt 0) {
        $summaryRecommendations.Add("TX output headroom experimental profile was requested or active; pair this trace with a matched current-profile TX baseline before promoting TX headroom changes.") | Out-Null
    }
    if ($txOutputHeadroomPureSignalBypassedSampleCount -gt 0) {
        $summaryRecommendations.Add("TX output headroom candidate was bypassed while PureSignal was armed; use this as bypass proof, not as effective headroom-trim proof.") | Out-Null
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
    if ($candidateAudioAlignmentMismatchCount -gt 0) {
        $summaryRecommendations.Add("Candidate output diagnostics diverged from both the pre-leveler and post-leveler audio on one or more samples; use candidateAudioAlignmentWatch before treating low-evidence lift/dropout rows as same-block DSP facts.") | Out-Null
    }
    if ($candidateAudioLevelerNormalizedCount -gt 0) {
        $summaryRecommendations.Add("Candidate weak speech was normalized to target loudness by the RX audio leveler on one or more samples; inspect candidateAudioAlignmentWatch.topLevelerNormalizedSamples before treating those rows as mismatches or dropouts.") | Out-Null
    }
    if ($candidateSpeechQualifiedWeakStrongEvidenceReady -and
        [string]::Equals($candidateMixedWeakStrongEvidenceStatus, "weak-strong-output-gap-watch", [StringComparison]::OrdinalIgnoreCase) -and
        $candidateNormalizationMotionIsFloorSuppression) {
        $summaryRecommendations.Add("Raw Candidate weak/strong averages are separated, but speech-qualified weak/strong final audio is within parity while floor suppression dominates; inspect candidateWeakSignalWatch.speechQualified* before raising Candidate floor or leveler gain.") | Out-Null
    }
    elseif ($candidatePassbandQualifiedWeakStrongEvidenceReady -and
        [string]::Equals($candidateMixedWeakStrongEvidenceStatus, "weak-strong-output-gap-watch", [StringComparison]::OrdinalIgnoreCase) -and
        $candidateNormalizationMotionIsFloorSuppression) {
        $summaryRecommendations.Add("Raw Candidate weak/strong averages are separated, but passband-qualified speech is normalized while adjacent or off-passband energy stays suppressed; inspect candidateWeakSignalWatch.passbandQualified* before raising Candidate floor or leveler gain.") | Out-Null
    }
    elseif ($candidateAudioAlignedAfterLevelerCount -gt 0 -and [int]$candidateAudioInputDeltaStats["count"] -gt 0 -and (
        ([double]$candidateAudioInputDeltaStats["min"] -lt -12.0) -or
        ([double]$candidateAudioInputDeltaStats["max"] -gt 12.0))) {
        $summaryRecommendations.Add("Candidate output aligns with post-leveler speech audio even though pre-leveler audio is lower; treat that delta as intended RX leveler gain, not a diagnostics mismatch.") | Out-Null
    }
    if ([int]$frontendAdjacentNoiseBinStats["count"] -eq 0) {
        $summaryRecommendations.Add("No frontend adjacent-noise profile was published in this trace; keep the frontend panadapter active before using adjacent-band noise as Candidate evidence.") | Out-Null
    }
    elseif ($frontendAdjacentNoiseUsableCount -eq 0) {
        $summaryRecommendations.Add("Frontend adjacent-noise samples were present but not marked usable; inspect adjacent-channel signals before using the profile for Candidate suppression tuning.") | Out-Null
    }
    if ($frontendTopPeakSampleCount -eq 0) {
        $summaryRecommendations.Add("Frontend top-peak evidence is missing; refresh the web frontend so comparison traces record actual band peak locations before choosing a tuning window.") | Out-Null
    }
    elseif ($frontendFilterPassbandPeakSampleCount -eq 0 -and $frontendNearPassbandPeakSampleCount -gt 0) {
        if ($null -ne $frontendNearestTuneCandidateValue) {
            $summaryRecommendations.Add("Frontend saw peaks near the dial, but none inside the RX filter passband; nearest stepped retune target is $($frontendNearestTuneCandidateValue["suggestedVfoMhz"]) MHz (delta $($frontendNearestTuneCandidateValue["retuneDeltaHz"]) Hz) before using this trace as on-signal Candidate evidence.") | Out-Null
        }
        else {
            $summaryRecommendations.Add("Frontend saw peaks near the dial, but none inside the RX filter passband; tune the signal into the active filter window before using this trace as on-signal Candidate evidence.") | Out-Null
        }
    }
    elseif ($frontendNearPassbandPeakSampleCount -eq 0) {
        if ($null -ne $frontendNearestTuneCandidateValue) {
            $summaryRecommendations.Add("Frontend saw band peaks, but none were within 3 kHz of the dial; nearest stepped retune target is $($frontendNearestTuneCandidateValue["suggestedVfoMhz"]) MHz (delta $($frontendNearestTuneCandidateValue["retuneDeltaHz"]) Hz) before using this trace as on-signal Candidate evidence.") | Out-Null
        }
        else {
            $summaryRecommendations.Add("Frontend saw band peaks, but none were within 3 kHz of the dial; retune toward frontendTopPeakWatch.topSamples before using this trace as on-signal Candidate evidence.") | Out-Null
        }
    }
    $rxAudioLevelerConstrainedMaxAbsGainDeltaDb = $null
    $rxAudioLevelerConstrainedDesiredGainValues = New-Object System.Collections.Generic.List[double]
    $rxAudioLevelerConstrainedAppliedGainValues = New-Object System.Collections.Generic.List[double]
    $rxAudioLevelerConstrainedPeakHeadroomValues = New-Object System.Collections.Generic.List[double]
    $rxAudioLevelerConstrainedPreLimitPeakValues = New-Object System.Collections.Generic.List[double]
    foreach ($sample in @($rxAudioLevelerConstrainedSamples.ToArray())) {
        $gainDelta = Get-NumericValue $sample["gainDeltaDb"]
        if ($null -ne $gainDelta) {
            $absGainDelta = [Math]::Round([Math]::Abs([double]$gainDelta), 3)
            if ($null -eq $rxAudioLevelerConstrainedMaxAbsGainDeltaDb -or
                $absGainDelta -gt [double]$rxAudioLevelerConstrainedMaxAbsGainDeltaDb) {
                $rxAudioLevelerConstrainedMaxAbsGainDeltaDb = $absGainDelta
            }
        }
        Add-Number $rxAudioLevelerConstrainedDesiredGainValues $sample["desiredGainDb"]
        Add-Number $rxAudioLevelerConstrainedAppliedGainValues $sample["appliedGainDb"]
        Add-Number $rxAudioLevelerConstrainedPeakHeadroomValues $sample["peakHeadroomDb"]
        Add-Number $rxAudioLevelerConstrainedPreLimitPeakValues $sample["preLimitPeakDbfs"]
    }
    $rxAudioLevelerConstrainedDesiredGainStats = Get-NumberStats $rxAudioLevelerConstrainedDesiredGainValues
    $rxAudioLevelerConstrainedAppliedGainStats = Get-NumberStats $rxAudioLevelerConstrainedAppliedGainValues
    $rxAudioLevelerConstrainedPeakHeadroomStats = Get-NumberStats $rxAudioLevelerConstrainedPeakHeadroomValues
    $rxAudioLevelerConstrainedPreLimitPeakStats = Get-NumberStats $rxAudioLevelerConstrainedPreLimitPeakValues
    $rxAudioLevelerCapNeedsReview = ($rxAudioLevelerOutputLimitedCount -gt 0 -and (
        ([int]$rxAudioLevelerOutputLimitReductionStats["count"] -gt 0 -and [double]$rxAudioLevelerOutputLimitReductionStats["max"] -gt 1.0) -or
        ([int]$rxAudioLevelerOutputLimitSampleCountStats["count"] -gt 0 -and [double]$rxAudioLevelerOutputLimitSampleCountStats["max"] -gt 8.0)))
    $rxAudioLevelerSettlingNeedsReview = ($rxAudioLevelerBoostSlewLimitedCount -gt 0 -and
        [int]$rmsStats["count"] -gt 1 -and [double]$rmsStats["movement"] -gt 4.0)
    $rxAudioLevelerHeadroomNeedsReview = ($rxAudioLevelerPeakLimitedCount -gt 0 -and
        [int]$rmsStats["count"] -gt 1 -and [double]$rmsStats["movement"] -gt 4.0)
    $rxAudioLevelerSparseConstraintPctThreshold = 5.0
    $rxAudioLevelerFinalAudioParityReady = ($candidateMixedWeakStrongEvidenceReady -and (
        $candidateWeakStrongFinalAudioParityReady -or
        $candidateSpeechQualifiedWeakStrongFinalAudioParityReady -or
        $candidatePassbandQualifiedWeakStrongFinalAudioParityReady))
    $rxAudioLevelerSparseConstraintEvidence = ($rxAudioLevelerDiagnosticCount -gt 0 -and
        $null -ne $rxAudioLevelerConstrainedPct -and [double]$rxAudioLevelerConstrainedPct -le $rxAudioLevelerSparseConstraintPctThreshold -and
        ($null -eq $rxAudioLevelerBoostSlewLimitedPct -or [double]$rxAudioLevelerBoostSlewLimitedPct -le $rxAudioLevelerSparseConstraintPctThreshold) -and
        ($null -eq $rxAudioLevelerPeakLimitedPct -or [double]$rxAudioLevelerPeakLimitedPct -le $rxAudioLevelerSparseConstraintPctThreshold) -and
        ($null -eq $rxAudioLevelerOutputLimitedPct -or [double]$rxAudioLevelerOutputLimitedPct -le 0.0))
    $rxAudioLevelerMaxAbsGainDeltaAdvisoryThresholdDb = 24.0
    $rxAudioLevelerMaxGainAdvisoryThresholdDb = 36.0
    $rxAudioLevelerMinPeakHeadroomAdvisoryThresholdDb = 20.0
    $rxAudioLevelerMaxPreLimitPeakAdvisoryThresholdDbfs = -4.0
    $rxAudioLevelerSeverityWithinAdvisoryBounds = (
        ($null -eq $rxAudioLevelerConstrainedMaxAbsGainDeltaDb -or [double]$rxAudioLevelerConstrainedMaxAbsGainDeltaDb -le $rxAudioLevelerMaxAbsGainDeltaAdvisoryThresholdDb) -and
        ([int]$rxAudioLevelerConstrainedDesiredGainStats["count"] -le 0 -or [double]$rxAudioLevelerConstrainedDesiredGainStats["max"] -le $rxAudioLevelerMaxGainAdvisoryThresholdDb) -and
        ([int]$rxAudioLevelerConstrainedAppliedGainStats["count"] -le 0 -or [double]$rxAudioLevelerConstrainedAppliedGainStats["max"] -le $rxAudioLevelerMaxGainAdvisoryThresholdDb) -and
        ([int]$rxAudioLevelerConstrainedPeakHeadroomStats["count"] -le 0 -or [double]$rxAudioLevelerConstrainedPeakHeadroomStats["min"] -ge $rxAudioLevelerMinPeakHeadroomAdvisoryThresholdDb) -and
        ([int]$rxAudioLevelerConstrainedPreLimitPeakStats["count"] -le 0 -or [double]$rxAudioLevelerConstrainedPreLimitPeakStats["max"] -le $rxAudioLevelerMaxPreLimitPeakAdvisoryThresholdDbfs))
    $rxAudioLevelerSafetyAdvisoryOnly = ($rxAudioLevelerFinalAudioParityReady -and
        $rxAudioLevelerSparseConstraintEvidence -and
        $rxAudioLevelerSeverityWithinAdvisoryBounds -and
        $rxAudioLevelerOutputLimitedCount -eq 0 -and
        $candidateWeakDropoutCandidateLossCount -eq 0 -and
        $candidateHotMakeupCount -eq 0 -and
        -not $agcActivePumpingRisk -and
        -not $agcWideMovement)
    $rxAudioLevelerSettlingAdvisoryOnly = ($rxAudioLevelerSettlingNeedsReview -and $rxAudioLevelerSafetyAdvisoryOnly)
    $rxAudioLevelerHeadroomAdvisoryOnly = ($rxAudioLevelerHeadroomNeedsReview -and $rxAudioLevelerSafetyAdvisoryOnly)
    if ($rxAudioLevelerSettlingAdvisoryOnly -or $rxAudioLevelerHeadroomAdvisoryOnly) {
        $summaryRecommendations.Add("RX audio leveler constraints were sparse, output limiting was absent, and qualified Candidate final-audio parity is ready; store this as advisory evidence and avoid tuning leveler gain unless repeated traces or listening show pumping.") | Out-Null
    }
    $candidateLowEvidenceLiftNeedsReview = ($candidateLowEvidenceLiftCount -ge 5 -or
        ($null -ne $candidateLowEvidenceLiftPct -and [double]$candidateLowEvidenceLiftPct -ge 20.0))
    $candidateOutputMotionNeedsReview = $false
    if ([int]$candidateOutputStats["count"] -gt 1 -and [double]$candidateOutputStats["movement"] -gt 6.0) {
        $candidateOutputMotionNeedsReview = ($candidateWeakDropoutCount -gt 0 `
            -or $candidateHotMakeupCount -gt 0 `
            -or ($null -ne $candidateNormalizationCompressionDb -and [double]$candidateNormalizationCompressionDb -lt -2.0) `
            -or ([int]$rmsStats["count"] -gt 1 -and [double]$rmsStats["movement"] -gt 6.0))
        if ($candidateLowEvidenceSuppressionDominates) {
            $candidateOutputMotionNeedsReview = $false
        }
    }
    if ($candidateOutputMotionNeedsReview) {
        if ($candidateWeakStrongFinalAudioParityReady -and
            [int]$rmsStats["count"] -gt 1 -and [double]$rmsStats["movement"] -le 4.0 -and
            $candidateWeakDropoutCandidateLossCount -eq 0 -and
            $rxAudioLevelerConstrainedCount -eq 0) {
            $candidateOutputMotionNeedsReview = $false
        }
    }
    if ($candidateOutputMotionNeedsReview) {
        $summaryRecommendations.Add("Candidate output RMS moved more than 6 dB during the trace; inspect candidateRecoveryDrive and candidateMakeupGainDb before tuning mask thresholds.") | Out-Null
    }
    if ($null -ne $candidateNormalizationCompressionDb -and [double]$candidateNormalizationCompressionDb -lt -2.0 -and
        -not $candidateNormalizationMotionIsFloorSuppression) {
        $summaryRecommendations.Add("Candidate output moved more than input during the trace; reduce makeup/recovery memory before increasing weak-signal gain.") | Out-Null
    }
    elseif ($null -ne $candidateNormalizationCompressionDb -and [double]$candidateNormalizationCompressionDb -lt -2.0 -and
        $candidateNormalizationMotionIsFloorSuppression) {
        $summaryRecommendations.Add("Candidate output movement is dominated by low-evidence floor suppression with no candidate weak-loss rows; do not reduce recovery/makeup based on normalization compression alone.") | Out-Null
    }
    if ($null -ne $candidateWeakStrongOutputGapDb -and [Math]::Abs([double]$candidateWeakStrongOutputGapDb) -gt 6.0 -and
        -not $candidateWeakStrongFinalAudioParityReady -and
        -not $candidatePassbandQualifiedWeakStrongEvidenceReady -and
        [string]::Equals($candidateMixedWeakStrongEvidenceStatus, "weak-strong-output-gap-watch", [StringComparison]::OrdinalIgnoreCase)) {
        $summaryRecommendations.Add("Candidate weak and strong outputs differ by more than 6 dB on average; tune normalization before judging faint-signal fidelity.") | Out-Null
    }
    elseif ($null -ne $candidateWeakStrongOutputGapDb -and [Math]::Abs([double]$candidateWeakStrongOutputGapDb) -gt 6.0 -and
        $candidateMixedWeakStrongInputEvidenceInsufficient) {
        $summaryRecommendations.Add("Candidate weak/strong output gap is visible, but the mixed trace has too few repeated weak or strong rows for tuning; extend dwell or recapture before changing Candidate makeup/leveler logic.") | Out-Null
    }
    elseif ($null -ne $candidateWeakStrongOutputGapDb -and [Math]::Abs([double]$candidateWeakStrongOutputGapDb) -gt 6.0 -and
        ($candidateWeakStrongFinalAudioParityReady -or $candidatePassbandQualifiedWeakStrongEvidenceReady)) {
        $summaryRecommendations.Add("Native Candidate weak/strong output differs, but post-leveler speech audio is within parity; judge volume from candidateWeakSignalWatch weak/strong final-audio fields before changing Candidate makeup.") | Out-Null
    }
    if ($candidateSampleCount -gt 0 -and [string]::Equals($candidateMixedWeakStrongEvidenceStatus, "missing-strong-input", [StringComparison]::OrdinalIgnoreCase) -and
        $candidateFrontendStrongPassbandSubthresholdSamples.Count -gt 0) {
        $summaryRecommendations.Add("Frontend-strong passband peaks were present, but comparison input stayed below the strict $candidateStrongInputThresholdDbfs dBFS strong threshold; inspect candidateWeakSignalWatch.topFrontendStrongPassbandCandidateSubthresholdInputs before treating this as no strong RF or changing DSP defaults.") | Out-Null
    }
    elseif ($candidateSampleCount -gt 0 -and [string]::Equals($candidateMixedWeakStrongEvidenceStatus, "missing-strong-input", [StringComparison]::OrdinalIgnoreCase) -and
        $candidateNearStrongInputCount -gt 0) {
        $summaryRecommendations.Add("Candidate trace has near-strong samples between $candidateNearStrongInputThresholdDbfs and $candidateStrongInputThresholdDbfs dBFS but no strict strong-input samples; inspect candidateWeakSignalWatch.topNearStrongInputs and retune/extend dwell around those peaks before rejecting this frequency neighborhood.") | Out-Null
    }
    elseif ($candidateSampleCount -gt 0 -and [string]::Equals($candidateMixedWeakStrongEvidenceStatus, "missing-strong-input", [StringComparison]::OrdinalIgnoreCase)) {
        $summaryRecommendations.Add("Candidate trace has weak-input samples but no strong-input samples; capture an active mixed weak+strong speech window before using this trace as volume-parity acceptance evidence.") | Out-Null
    }
    elseif ($candidateSampleCount -gt 0 -and [string]::Equals($candidateMixedWeakStrongEvidenceStatus, "missing-weak-input", [StringComparison]::OrdinalIgnoreCase)) {
        $summaryRecommendations.Add("Candidate trace has strong-input samples but no weak-input samples; capture a weaker station or fading speech window before using this trace as weak-signal parity evidence.") | Out-Null
    }
    elseif ($candidateSampleCount -gt 0 -and [string]::Equals($candidateMixedWeakStrongEvidenceStatus, "missing-weak-and-strong-input", [StringComparison]::OrdinalIgnoreCase)) {
        $summaryRecommendations.Add("Candidate trace did not include weak or strong classified input samples; retune or extend capture duration before using it as mixed-signal acceptance evidence.") | Out-Null
    }
    elseif ($candidateSampleCount -gt 0 -and [string]::Equals($candidateMixedWeakStrongEvidenceStatus, "missing-output-gap", [StringComparison]::OrdinalIgnoreCase)) {
        $summaryRecommendations.Add("Candidate trace has weak and strong input samples but no output gap statistic; inspect Candidate output diagnostics before using it as volume-parity evidence.") | Out-Null
    }
    elseif ($candidateSampleCount -gt 0 -and [string]::Equals($candidateMixedWeakStrongEvidenceStatus, "low-evidence-weak-input", [StringComparison]::OrdinalIgnoreCase)) {
        $summaryRecommendations.Add("Candidate mixed weak/strong final-audio parity only used low-evidence weak rows; capture speech-qualified weak input before accepting this as weak-signal parity evidence.") | Out-Null
    }
    elseif ($candidateSampleCount -gt 0 -and [string]::Equals($candidateMixedWeakStrongEvidenceStatus, "insufficient-weak-input-evidence", [StringComparison]::OrdinalIgnoreCase)) {
        $summaryRecommendations.Add("Candidate trace has mixed weak/strong samples, but fewer than $candidateMinimumMixedEvidenceQualifiedWeakInputSampleCount qualified weak rows; extend dwell through a weak-signal fade before using this trace for parity tuning or acceptance.") | Out-Null
    }
    elseif ($candidateSampleCount -gt 0 -and [string]::Equals($candidateMixedWeakStrongEvidenceStatus, "insufficient-strong-input-evidence", [StringComparison]::OrdinalIgnoreCase)) {
        $summaryRecommendations.Add("Candidate trace has weak samples, but fewer than $candidateMinimumMixedStrongInputSampleCount strong rows; extend dwell through a stronger speech burst before using this trace for mixed weak/strong parity.") | Out-Null
    }
    if ($candidateWeakDropoutCount -gt 0 -and
        $candidateWeakDropoutCandidateLossCount -eq 0 -and
        $candidateWeakDropoutFinalAudibleCount -eq $candidateWeakDropoutCount) {
        $summaryRecommendations.Add("Candidate strict weak-output dropout thresholds were hit, but every strict dropout row was already final-audio audible; treat these as recovered weak bursts unless subjective listening says otherwise.") | Out-Null
    }
    elseif ($candidateWeakDropoutCount -gt 0) {
        $summaryRecommendations.Add("Candidate strict weak-output dropouts were observed; inspect candidateWeakSignalWatch dropout classifications before increasing global makeup gain.") | Out-Null
    }
    if ($candidateWeakDropoutFinalAudibleCount -gt 0) {
        $summaryRecommendations.Add("Some strict Candidate weak-dropout rows were already final-audio audible after the RX leveler; use candidateWeakSignalWatch.topFinalAudibleWeakDropouts before treating them as true weak-signal loss.") | Out-Null
    }
    if ($candidateWeakDropoutCandidateLossCount -gt 0) {
        $summaryRecommendations.Add("Candidate candidate weak-loss rows remained below the input and were not final-audio audible; tune bounded weak-frame rescue from candidateWeakSignalWatch.topCandidateWeakLosses rather than lifting the whole noise floor.") | Out-Null
    }
    if ($candidateWeakDropoutCandidateLossCount -gt 0 -and [int]$candidateSignalProbabilityStats["count"] -gt 1 -and [double]$candidateSignalProbabilityStats["max"] -lt 0.30) {
        $summaryRecommendations.Add("Candidate candidate weak-loss rows coincided with low signal probability; tune probability/coherence opening before changing output AGC.") | Out-Null
    }
    if ($candidateWeakDropoutCandidateLossCount -gt 0 -and [int]$candidateSignalProbabilityStats["count"] -gt 1 -and [double]$candidateSignalProbabilityStats["max"] -ge 0.30 -and
        [int]$candidateTextureFillStats["count"] -gt 1 -and [double]$candidateTextureFillStats["max"] -lt 0.05) {
        $summaryRecommendations.Add("Candidate saw probable weak signal but texture fill stayed low during candidate weak-loss rows; tune mask hole-fill before raising persistent makeup gain.") | Out-Null
    }
    if ($candidateWeakBelowInputCount -gt 0 -and $candidateWeakDropoutCandidateLossCount -gt 0) {
        $summaryRecommendations.Add("Some weak comparison samples left the output below the input; prefer bounded weak-frame rescue over persistent makeup gain.") | Out-Null
    }
    elseif ($candidateWeakBelowInputCount -gt 0 -and $candidateLowEvidenceSuppressionDominates) {
        $summaryRecommendations.Add("Most weak rows below input are dominated by low-evidence suppression; inspect candidate weak-loss rows before treating below-input samples as voice loss.") | Out-Null
    }
    if ($candidateHotMakeupCount -gt 0) {
        $summaryRecommendations.Add("Candidate makeup exceeded 12 dB on one or more samples; inspect candidateWeakSignalWatch.topHotMakeup before changing recovery attack/release.") | Out-Null
    }
    if ($candidateLowEvidenceLiftCount -gt 0) {
        $summaryRecommendations.Add("Candidate lifted low-evidence weak samples into the audible range; inspect candidateLowEvidenceLiftWatch.topLiftedSamples before treating the recovered audio as real weak-signal content.") | Out-Null
    }
    if ($candidateLowEvidenceAlignmentMismatchCount -gt 0) {
        $summaryRecommendations.Add("Some low-evidence Candidate rows had audio/Candidate alignment mismatch; prefer faster traces or matched offline capture before changing Candidate thresholds from those rows.") | Out-Null
    }
    if ($candidateLowEvidenceSuppressedCount -gt 0) {
        $summaryRecommendations.Add("Candidate suppressed low-evidence weak samples instead of normalizing them; confirm the trace is noise-only or adjacent-channel noise before using it as weak-signal loss evidence.") | Out-Null
    }
    if ([int]$candidatePeakReductionDbStats["count"] -gt 1 -and [double]$candidatePeakReductionDbStats["max"] -gt 3.0) {
        $summaryRecommendations.Add("Candidate adaptive peak shaping exceeded 3 dB on one or more samples; compare candidateOutputPeakDbfs with final audioPeakDbfs before tuning downstream level controls.") | Out-Null
    }
    if ([int]$candidateRecoveryDriveStats["count"] -gt 1 -and [double]$candidateRecoveryDriveStats["max"] -lt 0.20 -and
        [int]$rmsStats["count"] -gt 1 -and [double]$rmsStats["movement"] -gt 6.0) {
        $summaryRecommendations.Add("Final audio moved but Candidate recovery drive stayed low; improve confidence/gating before increasing makeup gain.") | Out-Null
    }
    if ([int]$candidateRecoveryDriveStats["count"] -gt 1 -and [double]$candidateRecoveryDriveStats["max"] -ge 0.20 -and
        [int]$candidateMakeupGainDbStats["count"] -gt 1 -and [double]$candidateMakeupGainDbStats["max"] -lt 1.5 -and
        [int]$rmsStats["count"] -gt 1 -and [double]$rmsStats["movement"] -gt 6.0) {
        $summaryRecommendations.Add("Candidate recovery drive engaged but makeup gain stayed low; tune the fast makeup path rather than the spectral mask.") | Out-Null
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
    elseif ($candidateSampleCount -gt 0 -and $candidateAgcDiagnosticCount -lt $candidateSampleCount) {
        $trendStatus = "candidate-agc-diagnostics-missing"
    }
    elseif ($candidateSampleCount -gt 0 -and $candidateProbabilityDiagnosticCount -lt $candidateSampleCount) {
        $trendStatus = "candidate-probability-diagnostics-missing"
    }
    elseif ($candidateSampleCount -gt 0 -and $candidatePeakDiagnosticCount -lt $candidateSampleCount) {
        $trendStatus = "candidate-peak-diagnostics-missing"
    }
    elseif ($candidateLearnerResetSampleCount -gt 0) {
        $trendStatus = "candidate-learner-reset-watch"
    }
    elseif ($candidateManagedReapplySampleCount -gt 0) {
        $trendStatus = "candidate-managed-reapply-watch"
    }
    elseif ($candidateManagedGenerationChangeCount -gt 0) {
        $trendStatus = "candidate-channel-generation-changed"
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
    elseif ($candidateMixedWeakStrongInputEvidenceInsufficient) {
        $trendStatus = "mixed-evidence-insufficient"
    }
    elseif ($candidateOutputMotionNeedsReview) {
        $trendStatus = "candidate-output-level-watch"
    }
    elseif ($candidateLowEvidenceLiftNeedsReview) {
        $trendStatus = "candidate-low-evidence-lift-watch"
    }
    elseif ($quietIntermittentTrace) {
        $trendStatus = "quiet-intermittent-signal-watch"
    }
    elseif (-not $candidateLowEvidenceSuppressionDominates -and [int]$rmsStats["count"] -gt 1 -and [double]$rmsStats["movement"] -gt 10.0) {
        $trendStatus = "audio-level-watch"
    }
    elseif ($rxAudioLevelerCapNeedsReview) {
        $trendStatus = "rx-leveler-cap-watch"
    }
    elseif ($rxAudioLevelerSettlingNeedsReview -and -not $rxAudioLevelerSettlingAdvisoryOnly) {
        $trendStatus = "rx-leveler-settling-watch"
    }
    elseif ($rxAudioLevelerHeadroomNeedsReview -and -not $rxAudioLevelerHeadroomAdvisoryOnly) {
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
        ($candidateSampleCount -eq 0 -or ($candidateAgcDiagnosticCount -eq $candidateSampleCount -and
            $candidateProbabilityDiagnosticCount -eq $candidateSampleCount -and
            $candidatePeakDiagnosticCount -eq $candidateSampleCount)))
    $candidateTuningReadyTrace = ($okCount -gt 0 -and $candidateTuningReadyCount -eq $okCount)
    $candidateTuningTraceStatus = if ($candidateTuningReadyTrace) {
        "candidate-preflight-ready"
    }
    elseif ($okCount -eq 0) {
        "no-ok-samples"
    }
    elseif ($candidateTuningReadyCount -gt 0) {
        "candidate-preflight-partial"
    }
    else {
        "candidate-tuning-preflight-required"
    }

    $comparisonStateId = ([string]$ComparisonId).Trim().ToLowerInvariant()
    $comparisonStateStrict = ($comparisonStateId -eq "candidate-under-test" -or $comparisonStateId -eq "off-baseline")
    $comparisonStateReady = $true
    $comparisonStateStatus = "not-required"
    $comparisonStateNextAction = "No strict NR mode state proof is required for this comparison label."
    if ($comparisonStateId -eq "candidate-under-test") {
        $comparisonStateReady = ($readyTrace -and $candidateTuningReadyTrace)
        if ($comparisonStateReady) {
            $comparisonStateStatus = "candidate-preflight-ready"
            $comparisonStateNextAction = "Use this trace as comparison-under-test evidence only after current-Zeus and Thetis-parity baselines are captured."
        }
        elseif ($okCount -eq 0) {
            $comparisonStateStatus = "no-ok-samples"
            $comparisonStateNextAction = "Recapture after the live diagnostics endpoint returns successful samples."
        }
        else {
            $comparisonStateStatus = $candidateTuningTraceStatus
            $comparisonStateNextAction = "Resolve comparison preflight constraints, then recapture before comparing the under-test path."
        }
    }
    elseif ($comparisonStateId -eq "off-baseline") {
        $comparisonStateReady = ($okCount -gt 0 -and
            $nrOffRequestedSampleCount -eq $okCount -and
            $nrOffEffectiveSampleCount -eq $okCount -and
            $candidateSampleCount -eq 0)
        if ($comparisonStateReady) {
            $comparisonStateStatus = "off-state-ready"
            $comparisonStateNextAction = "Use this trace as NR-off baseline evidence."
        }
        elseif ($okCount -eq 0) {
            $comparisonStateStatus = "no-ok-samples"
            $comparisonStateNextAction = "Recapture after the live diagnostics endpoint returns successful samples."
        }
        elseif ($candidateSampleCount -gt 0) {
            $comparisonStateStatus = "off-candidate-diagnostics-present"
            $comparisonStateNextAction = "Discard this stale Candidate-era trace and recapture the off-baseline comparison with current WDSP v2 builds."
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
        candidateTuningReadySampleCount = $candidateTuningReadyCount
        candidateTuningReadyTrace = $candidateTuningReadyTrace
        candidateTuningTraceStatus = $candidateTuningTraceStatus
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
        rxAudioLevelerCandidateSpeechHoldBlocks = $rxAudioLevelerCandidateSpeechHoldBlockStats
        rxAudioLevelerCandidateSpeechHangoverBlocks = $rxAudioLevelerCandidateSpeechHangoverBlockStats
        rxAudioLevelerCandidateHybridSpeechPrior = $rxAudioLevelerCandidateHybridSpeechPriorStats
        rxAudioLevelerCandidateNoSignalNoisePrior = $rxAudioLevelerCandidateNoSignalNoisePriorStats
        rxAudioLevelerCandidateNoiseProfilePrior = $rxAudioLevelerCandidateNoiseProfilePriorStats
        rxAudioLevelerProfileCounts = @(ConvertTo-CountArray $rxAudioLevelerProfileCounts)
        rxAudioLevelerExperimentalSampleCount = $rxAudioLevelerExperimentalSampleCount
        rxAudioLevelerControlRmsValidSampleCount = $rxAudioLevelerControlRmsValidCount
        rxAudioLevelerControlRmsDbfs = $rxAudioLevelerControlRmsStats
        rxAudioLevelerControlRmsHangDb = $rxAudioLevelerControlRmsHangStats
        rxAudioLevelerNormalStrengthControlRmsValidSampleCount = $rxAudioLevelerNormalStrengthControlRmsValidCount
        txOutputHeadroomRequestedProfileCounts = @(ConvertTo-CountArray $txOutputHeadroomRequestedProfileCounts)
        txOutputHeadroomActiveProfileCounts = @(ConvertTo-CountArray $txOutputHeadroomActiveProfileCounts)
        txOutputHeadroomExperimentalSampleCount = $txOutputHeadroomExperimentalSampleCount
        txOutputHeadroomPureSignalBypassedSampleCount = $txOutputHeadroomPureSignalBypassedSampleCount
        txOutputHeadroomTrimDb = Get-NumberStats $txOutputHeadroomTrimValues
        txOutputHeadroomWatch = [ordered]@{
            requestedProfileCounts = @(ConvertTo-CountArray $txOutputHeadroomRequestedProfileCounts)
            activeProfileCounts = @(ConvertTo-CountArray $txOutputHeadroomActiveProfileCounts)
            experimentalSampleCount = $txOutputHeadroomExperimentalSampleCount
            pureSignalBypassedSampleCount = $txOutputHeadroomPureSignalBypassedSampleCount
            trimDb = Get-NumberStats $txOutputHeadroomTrimValues
            recommendation = "Use headroom-trim-candidate active-profile samples as effective TX output trim evidence; use PureSignal-bypassed samples only as bypass safety evidence."
        }
        rxAudioLevelerWatch = [ordered]@{
            diagnosticSampleCount = $rxAudioLevelerDiagnosticCount
            profileCounts = @(ConvertTo-CountArray $rxAudioLevelerProfileCounts)
            experimentalSampleCount = $rxAudioLevelerExperimentalSampleCount
            controlRmsValidSampleCount = $rxAudioLevelerControlRmsValidCount
            controlRmsDbfs = $rxAudioLevelerControlRmsStats
            controlRmsHangDb = $rxAudioLevelerControlRmsHangStats
            normalStrengthControlRmsThresholdDbfs = $rxAudioLevelerNormalStrengthControlRmsThresholdDbfs
            normalStrengthControlRmsValidSampleCount = $rxAudioLevelerNormalStrengthControlRmsValidCount
            topNormalStrengthControlRmsSamples = @($rxAudioLevelerNormalStrengthControlRmsTopSamples)
            constrainedSampleCount = $rxAudioLevelerConstrainedCount
            constrainedPct = $rxAudioLevelerConstrainedPct
            candidateNoSignalNoiseCapSampleCount = $rxAudioLevelerCandidateNoSignalNoiseCapCount
            candidateFarPeakNoiseCapSampleCount = $rxAudioLevelerCandidateFarPeakNoiseCapCount
            candidateNoProofNoiseCapSampleCount = $rxAudioLevelerCandidateNoProofNoiseCapCount
            candidateHybridSpeechPrior = $rxAudioLevelerCandidateHybridSpeechPriorStats
            candidateSpeechHangoverBlocks = $rxAudioLevelerCandidateSpeechHangoverBlockStats
            candidateNoSignalNoisePrior = $rxAudioLevelerCandidateNoSignalNoisePriorStats
            candidateNoiseProfilePrior = $rxAudioLevelerCandidateNoiseProfilePriorStats
            boostSlewLimitedSampleCount = $rxAudioLevelerBoostSlewLimitedCount
            boostSlewLimitedPct = $rxAudioLevelerBoostSlewLimitedPct
            peakLimitedSampleCount = $rxAudioLevelerPeakLimitedCount
            peakLimitedPct = $rxAudioLevelerPeakLimitedPct
            outputLimitedSampleCount = $rxAudioLevelerOutputLimitedCount
            outputLimitedPct = $rxAudioLevelerOutputLimitedPct
            settlingMovementThresholdDb = 4.0
            capReductionThresholdDb = 1.0
            capSampleThreshold = 8.0
            sparseConstraintAdvisoryPctThreshold = $rxAudioLevelerSparseConstraintPctThreshold
            maxAbsGainDeltaAdvisoryThresholdDb = $rxAudioLevelerMaxAbsGainDeltaAdvisoryThresholdDb
            maxGainAdvisoryThresholdDb = $rxAudioLevelerMaxGainAdvisoryThresholdDb
            minPeakHeadroomAdvisoryThresholdDb = $rxAudioLevelerMinPeakHeadroomAdvisoryThresholdDb
            maxPreLimitPeakAdvisoryThresholdDbfs = $rxAudioLevelerMaxPreLimitPeakAdvisoryThresholdDbfs
            finalAudioParityReady = $rxAudioLevelerFinalAudioParityReady
            sparseConstraintEvidence = $rxAudioLevelerSparseConstraintEvidence
            severityWithinAdvisoryBounds = $rxAudioLevelerSeverityWithinAdvisoryBounds
            constrainedMaxAbsGainDeltaDb = $rxAudioLevelerConstrainedMaxAbsGainDeltaDb
            constrainedDesiredGainDb = $rxAudioLevelerConstrainedDesiredGainStats
            constrainedAppliedGainDb = $rxAudioLevelerConstrainedAppliedGainStats
            constrainedPeakHeadroomDb = $rxAudioLevelerConstrainedPeakHeadroomStats
            constrainedPreLimitPeakDbfs = $rxAudioLevelerConstrainedPreLimitPeakStats
            safetyAdvisoryOnly = $rxAudioLevelerSafetyAdvisoryOnly
            settlingNeedsReview = $rxAudioLevelerSettlingNeedsReview
            settlingAdvisoryOnly = $rxAudioLevelerSettlingAdvisoryOnly
            headroomNeedsReview = $rxAudioLevelerHeadroomNeedsReview
            headroomAdvisoryOnly = $rxAudioLevelerHeadroomAdvisoryOnly
            topBoostSlewLimitedSamples = @($rxAudioLevelerBoostSlewLimitedTopSamples)
            topPeakLimitedSamples = @($rxAudioLevelerPeakLimitedTopSamples)
            topOutputLimitedSamples = @($rxAudioLevelerOutputLimitedTopSamples)
            topCandidateNoSignalNoiseCapSamples = @($rxAudioLevelerCandidateNoSignalNoiseCapTopSamples)
        }
        candidateSpeechContinuityWatch = [ordered]@{
            status = $candidateSpeechContinuityStatus
            needsReview = $candidateSpeechContinuityNeedsReview
            sampleCount = $candidateSpeechContinuitySampleCount
            fadeSampleCount = $candidateSpeechContinuityFadeCount
            fadePct = $candidateSpeechContinuityFadePct
            dropoutSampleCount = $candidateSpeechContinuityDropoutCount
            dropoutPct = $candidateSpeechContinuityDropoutPct
            hybridSpeechPriorThreshold = $candidateSpeechContinuityHybridThreshold
            noSignalNoisePriorMax = $candidateSpeechContinuityNoSignalMax
            fadeThresholdDbfs = $candidateSpeechContinuityFadeThresholdDbfs
            dropoutThresholdDbfs = $candidateSpeechContinuityDropoutThresholdDbfs
            outputMovementThresholdDb = $candidateSpeechContinuityOutputMovementThresholdDb
            gainMovementThresholdDb = $candidateSpeechContinuityGainMovementThresholdDb
            finalAudioRmsDbfs = $candidateSpeechContinuityOutputStats
            appliedGainDb = $candidateSpeechContinuityGainStats
            outputMovementDb = $candidateSpeechContinuityOutputMovementDb
            appliedGainMovementDb = $candidateSpeechContinuityGainMovementDb
            topSamples = @($candidateSpeechContinuityTopSamples)
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
            nearestTuneCandidate = $frontendNearestTuneCandidateValue
        }
        frontendTuneCandidates = @($frontendTuneCandidates)
        frontendNearestTuneCandidate = $frontendNearestTuneCandidateValue
        frontendAdjacentNoiseProfile = [ordered]@{
            usableSampleCount = $frontendAdjacentNoiseUsableCount
            bins = $frontendAdjacentNoiseBinStats
            floorDb = $frontendAdjacentNoiseFloorStats
            p50Db = $frontendAdjacentNoiseP50Stats
            slopeDbPerKhz = $frontendAdjacentNoiseSlopeStats
            rejectedPct = $frontendAdjacentNoiseRejectedStats
        }
        candidateAdjacentNoiseProfile = [ordered]@{
            usableSampleCount = $candidateAdjacentNoiseUsableCount
            driveSampleCount = $candidateAdjacentNoiseDriveCount
            trust = $candidateAdjacentNoiseTrustStats
            drive = $candidateAdjacentNoiseDriveStats
            floorDb = $candidateAdjacentNoiseFloorStats
            rejectedPct = $candidateAdjacentNoiseRejectedStats
            sideBalance = $candidateAdjacentNoiseSideBalanceStats
            asymmetryDb = $candidateAdjacentNoiseAsymmetryStats
        }
        rxMetersAgeMs = $rxMetersAgeStats
        audioAgeMs = $audioAgeStats
        requestedNrModeCounts = @(ConvertTo-CountArray $requestedNrModeCounts)
        effectiveNrModeCounts = @(ConvertTo-CountArray $effectiveNrModeCounts)
        nrModeMismatchSampleCount = $nrModeMismatchSampleCount
        nrOffRequestedSampleCount = $nrOffRequestedSampleCount
        nrOffEffectiveSampleCount = $nrOffEffectiveSampleCount
        candidateRequestedSampleCount = $candidateRequestedSampleCount
        candidateEffectiveSampleCount = $candidateEffectiveSampleCount
        candidateSampleCount = $candidateSampleCount
        candidateAlignedSampleCount = $candidateAlignedCount
        candidateAgcDiagnosticSampleCount = $candidateAgcDiagnosticCount
        candidateProbabilityDiagnosticSampleCount = $candidateProbabilityDiagnosticCount
        candidatePeakDiagnosticSampleCount = $candidatePeakDiagnosticCount
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
            candidateRequestedSampleCount = $candidateRequestedSampleCount
            candidateEffectiveSampleCount = $candidateEffectiveSampleCount
            candidateSampleCount = $candidateSampleCount
            candidateAlignedSampleCount = $candidateAlignedCount
            candidateAgcDiagnosticSampleCount = $candidateAgcDiagnosticCount
            candidateProbabilityDiagnosticSampleCount = $candidateProbabilityDiagnosticCount
            candidatePeakDiagnosticSampleCount = $candidatePeakDiagnosticCount
        }
        candidateInputDbfs = $candidateInputStats
        candidateOutputDbfs = $candidateOutputStats
        candidateOutputMinusInputDb = $candidateOutputMinusInputStats
        candidateInputToOutput = $candidateInputOutputStats
        candidateMeanGain = $candidateMeanGainStats
        candidateFloorReductionDb = $candidateFloorReductionStats
        candidateDynamicRangeDb = $candidateDynamicRangeStats
        candidateSignalConfidence = $candidateSignalConfidenceStats
        candidateAgcGate = $candidateAgcGateStats
        candidateSignalProbability = $candidateSignalProbabilityStats
        candidateTextureFill = $candidateTextureFillStats
        candidateMaskSmoothing = $candidateMaskSmoothingStats
        candidateLevelDrive = $candidateLevelDriveStats
        candidateRecoveryDrive = $candidateRecoveryDriveStats
        candidateWeakSignalMemory = $candidateWeakSignalMemoryStats
        candidateMakeupGainDb = $candidateMakeupGainDbStats
        candidateOutputPeakDbfs = $candidateOutputPeakDbfsStats
        candidatePeakEvidence = $candidatePeakEvidenceStats
        candidatePeakLimitDbfs = $candidatePeakLimitDbfsStats
        candidatePeakReductionDb = $candidatePeakReductionDbStats
        candidateLearnerStabilityWatch = [ordered]@{
            status = $candidateLearnerStabilityStatus
            learnerMonotonic = $candidateLearnerMonotonic
            managedReplayEvidenceReady = $candidateManagedReplayEvidenceReady
            candidateSampleCount = $candidateSampleCount
            learnedFrameSampleCount = $candidateLearnedFrameSampleCount
            learnedFrameCoveragePct = $candidateLearnedFrameCoveragePct
            managedCounterSampleCount = $candidateManagedCounterSampleCount
            managedCounterCoveragePct = $candidateManagedCounterCoveragePct
            learnerResetSampleCount = $candidateLearnerResetSampleCount
            managedReapplySampleCount = $candidateManagedReapplySampleCount
            managedPositionReapplySampleCount = $candidateManagedPositionReapplyCount
            managedPolicyReapplySampleCount = $candidateManagedPolicyReapplyCount
            managedGenerationChangeSampleCount = $candidateManagedGenerationChangeCount
            learnedFrames = $candidateLearnedFrameStats
            managedChannelGeneration = $candidateManagedChannelGenerationStats
            managedCandidateApplyCount = $candidateManagedApplyCountStats
            managedCandidatePositionApplyCount = $candidateManagedPositionApplyCountStats
            managedCandidatePolicyApplyCount = $candidateManagedPolicyApplyCountStats
            managedCandidateNoopApplyCount = $candidateManagedNoopApplyCountStats
            managedCandidateRunApplyCount = $candidateManagedRunApplyCountStats
            topLearnerResetSamples = @($candidateLearnerResetTopSamples)
            topManagedReapplySamples = @($candidateManagedReapplyTopSamples)
            topManagedGenerationChangeSamples = @($candidateManagedGenerationChangeTopSamples)
        }
        candidateAudioAlignmentWatch = [ordered]@{
            comparableSampleCount = $candidateAudioAlignmentSampleCount
            mismatchThresholdDb = 12.0
            mismatchSampleCount = $candidateAudioAlignmentMismatchCount
            mismatchPct = $candidateAudioAlignmentMismatchPct
            alignedAfterLevelerSampleCount = $candidateAudioAlignedAfterLevelerCount
            alignedAfterLevelerPct = $candidateAudioAlignedAfterLevelerPct
            levelerNormalizedSampleCount = $candidateAudioLevelerNormalizedCount
            levelerNormalizedPct = $candidateAudioLevelerNormalizedPct
            candidateOutputToLevelerInputDeltaDb = $candidateAudioInputDeltaStats
            candidateOutputToLevelerOutputDeltaDb = $candidateAudioOutputDeltaStats
            topMismatches = @($candidateAudioAlignmentMismatchTopSamples)
            topLevelerNormalizedSamples = @($candidateAudioLevelerNormalizedTopSamples)
        }
        candidateWeakSignalWatch = [ordered]@{
            weakInputThresholdDbfs = -30.0
            weakInputSampleCount = $candidateWeakInputCount
            lowEvidenceWeakInputSampleCount = $candidateLowEvidenceWeakInputCount
            evidenceQualifiedWeakInputSampleCount = $candidateEvidenceQualifiedWeakInputCount
            minimumMixedWeakInputSampleCount = $candidateMinimumMixedWeakInputSampleCount
            minimumMixedStrongInputSampleCount = $candidateMinimumMixedStrongInputSampleCount
            minimumMixedEvidenceQualifiedWeakInputSampleCount = $candidateMinimumMixedEvidenceQualifiedWeakInputSampleCount
            minimumQualifiedWeakStrongInputSampleCount = $candidateMinimumQualifiedWeakStrongInputSampleCount
            weakInputSampleDeficit = [Math]::Max(0, $candidateMinimumMixedWeakInputSampleCount - $candidateWeakInputCount)
            evidenceQualifiedWeakInputSampleDeficit = [Math]::Max(0, $candidateMinimumMixedEvidenceQualifiedWeakInputSampleCount - $candidateEvidenceQualifiedWeakInputCount)
            weakRecoveredSampleCount = $candidateWeakRecoveredCount
            weakNearTargetSampleCount = $candidateWeakNearTargetCount
            weakDropoutSampleCount = $candidateWeakDropoutCount
            weakDropoutFinalAudibleThresholdDbfs = $candidateWeakDropoutFinalAudibleThresholdDbfs
            weakDropoutFinalAudibleSampleCount = $candidateWeakDropoutFinalAudibleCount
            weakDropoutFinalAudiblePct = $candidateWeakDropoutFinalAudiblePct
            weakDropoutNativeLiftThresholdDb = $candidateWeakDropoutNativeLiftThresholdDb
            weakDropoutNativeLiftedSampleCount = $candidateWeakDropoutNativeLiftedCount
            weakDropoutNativeLiftedPct = $candidateWeakDropoutNativeLiftedPct
            weakDropoutBelowInputThresholdDb = $candidateWeakDropoutBelowInputThresholdDb
            weakDropoutCandidateLossSampleCount = $candidateWeakDropoutCandidateLossCount
            weakDropoutCandidateLossPct = $candidateWeakDropoutCandidateLossPct
            weakBelowInputSampleCount = $candidateWeakBelowInputCount
            strongInputThresholdDbfs = $candidateStrongInputThresholdDbfs
            strongInputSampleCount = $candidateStrongInputCount
            strongInputSampleDeficit = [Math]::Max(0, $candidateMinimumMixedStrongInputSampleCount - $candidateStrongInputCount)
            nearStrongInputThresholdDbfs = $candidateNearStrongInputThresholdDbfs
            nearStrongInputSampleCount = $candidateNearStrongInputCount
            frontendStrongPassbandSnrThresholdDb = $frontendStrongPassbandSnrThresholdDb
            frontendStrongPassbandDbfsThreshold = $frontendStrongPassbandDbfsThreshold
            frontendStrongPassbandCandidateSubthresholdSampleCount = $candidateFrontendStrongPassbandSubthresholdSamples.Count
            frontendStrongPassbandCandidateSubthresholdDistanceToStrongDb = $candidateFrontendStrongPassbandSubthresholdDistanceStats
            weakOutputDbfs = $candidateWeakOutputStats
            strongOutputDbfs = $candidateStrongOutputStats
            nearStrongOutputDbfs = $candidateNearStrongOutputStats
            weakStrongOutputGapDb = $candidateWeakStrongOutputGapDb
            weakStrongOutputGapThresholdDb = $candidateMixedWeakStrongGapThresholdDb
            weakFinalAudioDbfs = $candidateWeakFinalAudioStats
            strongFinalAudioDbfs = $candidateStrongFinalAudioStats
            nearStrongFinalAudioDbfs = $candidateNearStrongFinalAudioStats
            weakStrongFinalAudioGapDb = $candidateWeakStrongFinalAudioGapDb
            weakStrongFinalAudioGapThresholdDb = $candidateMixedWeakStrongFinalAudioGapThresholdDb
            speechQualifiedWeakInputSampleCount = $candidateSpeechQualifiedWeakOutputValues.Count
            speechQualifiedStrongInputSampleCount = $candidateSpeechQualifiedStrongOutputValues.Count
            speechQualifiedNearStrongInputSampleCount = $candidateSpeechQualifiedNearStrongOutputValues.Count
            speechQualifiedWeakInputSampleDeficit = [Math]::Max(0, $candidateMinimumQualifiedWeakStrongInputSampleCount - $candidateSpeechQualifiedWeakOutputValues.Count)
            speechQualifiedStrongInputSampleDeficit = [Math]::Max(0, $candidateMinimumQualifiedWeakStrongInputSampleCount - $candidateSpeechQualifiedStrongOutputValues.Count)
            speechQualifiedWeakOutputDbfs = $candidateSpeechQualifiedWeakOutputStats
            speechQualifiedStrongOutputDbfs = $candidateSpeechQualifiedStrongOutputStats
            speechQualifiedNearStrongOutputDbfs = $candidateSpeechQualifiedNearStrongOutputStats
            speechQualifiedWeakStrongOutputGapDb = $candidateSpeechQualifiedWeakStrongOutputGapDb
            speechQualifiedWeakFinalAudioDbfs = $candidateSpeechQualifiedWeakFinalAudioStats
            speechQualifiedStrongFinalAudioDbfs = $candidateSpeechQualifiedStrongFinalAudioStats
            speechQualifiedNearStrongFinalAudioDbfs = $candidateSpeechQualifiedNearStrongFinalAudioStats
            speechQualifiedWeakStrongFinalAudioGapDb = $candidateSpeechQualifiedWeakStrongFinalAudioGapDb
            speechQualifiedMixedWeakStrongEvidenceReady = $candidateSpeechQualifiedWeakStrongEvidenceReady
            speechQualifiedWeakStrongOutputParityReady = $candidateSpeechQualifiedWeakStrongOutputParityReady
            speechQualifiedWeakStrongFinalAudioParityReady = $candidateSpeechQualifiedWeakStrongFinalAudioParityReady
            speechQualifiedMixedWeakStrongEvidenceStatus = $candidateSpeechQualifiedWeakStrongEvidenceStatus
            passbandQualifiedWeakInputSampleCount = $candidatePassbandQualifiedWeakOutputValues.Count
            passbandQualifiedStrongInputSampleCount = $candidatePassbandQualifiedStrongOutputValues.Count
            passbandQualifiedNearStrongInputSampleCount = $candidatePassbandQualifiedNearStrongOutputValues.Count
            passbandQualifiedWeakInputSampleDeficit = [Math]::Max(0, $candidateMinimumQualifiedWeakStrongInputSampleCount - $candidatePassbandQualifiedWeakOutputValues.Count)
            passbandQualifiedStrongInputSampleDeficit = [Math]::Max(0, $candidateMinimumQualifiedWeakStrongInputSampleCount - $candidatePassbandQualifiedStrongOutputValues.Count)
            passbandQualifiedWeakOutputDbfs = $candidatePassbandQualifiedWeakOutputStats
            passbandQualifiedStrongOutputDbfs = $candidatePassbandQualifiedStrongOutputStats
            passbandQualifiedNearStrongOutputDbfs = $candidatePassbandQualifiedNearStrongOutputStats
            passbandQualifiedWeakStrongOutputGapDb = $candidatePassbandQualifiedWeakStrongOutputGapDb
            passbandQualifiedWeakFinalAudioDbfs = $candidatePassbandQualifiedWeakFinalAudioStats
            passbandQualifiedStrongFinalAudioDbfs = $candidatePassbandQualifiedStrongFinalAudioStats
            passbandQualifiedNearStrongFinalAudioDbfs = $candidatePassbandQualifiedNearStrongFinalAudioStats
            passbandQualifiedWeakStrongFinalAudioGapDb = $candidatePassbandQualifiedWeakStrongFinalAudioGapDb
            passbandQualifiedMixedWeakStrongEvidenceReady = $candidatePassbandQualifiedWeakStrongEvidenceReady
            passbandQualifiedWeakStrongOutputParityReady = $candidatePassbandQualifiedWeakStrongOutputParityReady
            passbandQualifiedWeakStrongFinalAudioParityReady = $candidatePassbandQualifiedWeakStrongFinalAudioParityReady
            passbandQualifiedMixedWeakStrongEvidenceStatus = $candidatePassbandQualifiedWeakStrongEvidenceStatus
            mixedWeakStrongEvidenceReady = $candidateMixedWeakStrongEvidenceReady
            weakStrongOutputParityReady = $candidateWeakStrongOutputParityReady
            weakStrongFinalAudioParityReady = $candidateWeakStrongFinalAudioParityReady
            mixedWeakStrongEvidenceStatus = $candidateMixedWeakStrongEvidenceStatus
            mixedWeakStrongTuningFocus = $candidateMixedWeakStrongTuningFocus
            normalizationCompressionDb = $candidateNormalizationCompressionDb
            hotMakeupThresholdDb = 12.0
            hotMakeupSampleCount = $candidateHotMakeupCount
            normalizationMotionIsFloorSuppression = $candidateNormalizationMotionIsFloorSuppression
            topWeakInputs = @($candidateWeakInputTopSamples)
            topStrongInputs = @($candidateStrongInputTopSamples)
            topSpeechQualifiedWeakInputs = @($candidateSpeechQualifiedWeakInputTopSamples)
            topSpeechQualifiedStrongInputs = @($candidateSpeechQualifiedStrongInputTopSamples)
            topPassbandQualifiedWeakInputs = @($candidatePassbandQualifiedWeakInputTopSamples)
            topPassbandQualifiedStrongInputs = @($candidatePassbandQualifiedStrongInputTopSamples)
            topWeakDropouts = @($candidateWeakDropoutTopSamples)
            topFinalAudibleWeakDropouts = @($candidateWeakDropoutFinalAudibleTopSamples)
            topCandidateWeakLosses = @($candidateWeakDropoutCandidateLossTopSamples)
            topHotMakeup = @($candidateHotMakeupTopSamples)
            topNearStrongInputs = @($candidateNearStrongInputTopSamples)
            topFrontendStrongPassbandCandidateSubthresholdInputs = @($candidateFrontendStrongPassbandSubthresholdTopSamples)
        }
        candidateLowEvidenceLiftWatch = [ordered]@{
            weakInputThresholdDbfs = $candidateLowEvidenceInputThresholdDbfs
            signalConfidenceThreshold = $candidateLowEvidenceConfidenceThreshold
            signalProbabilityThreshold = $candidateLowEvidenceProbabilityThreshold
            agcGateThreshold = $candidateLowEvidenceAgcGateThreshold
            outputThresholdDbfs = $candidateLowEvidenceOutputThresholdDbfs
            audioThresholdDbfs = $candidateLowEvidenceAudioThresholdDbfs
            lowEvidenceSampleCount = $candidateLowEvidenceSampleCount
            liftedSampleCount = $candidateLowEvidenceLiftCount
            liftedPct = $candidateLowEvidenceLiftPct
            alignmentMismatchSampleCount = $candidateLowEvidenceAlignmentMismatchCount
            alignmentMismatchPct = $candidateLowEvidenceAlignmentMismatchPct
            suppressedSampleCount = $candidateLowEvidenceSuppressedCount
            suppressedPct = $candidateLowEvidenceSuppressedPct
            suppressionDominates = $candidateLowEvidenceSuppressionDominates
            topLiftedSamples = @($candidateLowEvidenceLiftTopSamples)
            topSuppressedSamples = @($candidateLowEvidenceSuppressedTopSamples)
        }
        statusCounts = @(ConvertTo-CountArray $statusCounts)
        qualityToneCounts = @(ConvertTo-CountArray $toneCounts)
        runtimeStatusCounts = @(ConvertTo-CountArray $runtimeStatusCounts)
        audioStatusCounts = @(ConvertTo-CountArray $audioStatusCounts)
        candidateTuningStatusCounts = @(ConvertTo-CountArray $candidateTuningStatusCounts)
        candidateTuningConstraintCounts = @(ConvertTo-CountArray $candidateTuningConstraintCounts)
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
            "Comparison-specific input/output, confidence, gate, level-drive, recovery-drive, makeup-gain, audio-alignment mismatch, weak/strong normalization, and live tuning-readiness trends when comparison diagnostics are present"
        )
        example = "powershell -NoProfile -ExecutionPolicy Bypass -File tools\watch-dsp-live-diagnostics.ps1 -BaseUrl $base -Samples 60 -IntervalMs 1000 -TuneStepHz $effectiveTuneStepHz -Label g2-candidate-weak-cw"
        preflightExample = "powershell -NoProfile -ExecutionPolicy Bypass -File tools\watch-dsp-live-diagnostics.ps1 -BaseUrl $base -Samples 15 -IntervalMs 500 -TuneStepHz $effectiveTuneStepHz -Preflight -Label g2-preflight"
        desktopExample = "powershell -NoProfile -ExecutionPolicy Bypass -File tools\watch-dsp-live-diagnostics.ps1 -BaseUrl https://localhost:6443 -SkipCertificateCheck -Samples 60 -IntervalMs 500 -TuneStepHz $effectiveTuneStepHz -Label candidate-live"
        realtimeExample = "powershell -NoProfile -ExecutionPolicy Bypass -File tools\watch-dsp-live-diagnostics.ps1 -BaseUrl https://localhost:6443 -SkipCertificateCheck -Samples 120 -IntervalMs 250 -TuneStepHz $effectiveTuneStepHz -Realtime -RealtimeEvery 4 -Label candidate-live-tune"
        offlineExample = "powershell -NoProfile -ExecutionPolicy Bypass -File tools\watch-dsp-live-diagnostics.ps1 -InputPath captures\dsp-live-diagnostics\trace.jsonl -JsonOnly"
        notes = @(
            "Read-only: the tool only calls GET /api/dsp/live-diagnostics.",
            "Use traces as runtime context beside offline fixture metrics, audio renders, spectrum captures, and operator notes.",
            "Use candidateAudioAlignmentWatch to reject mixed-frame rows before treating low-evidence lift/dropout rows as DSP behavior.",
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
    Write-Host "Candidate tuning: $($report["candidateTuningTraceStatus"]) ($($report["candidateTuningReadySampleCount"])/$($report["okSampleCount"]) ready)"
    $learnerWatch = $report["candidateLearnerStabilityWatch"]
    if ($null -ne $learnerWatch) {
        Write-Host "Candidate learner: $($learnerWatch["status"]), learned frames $($learnerWatch["learnedFrames"]["min"])..$($learnerWatch["learnedFrames"]["max"]), position applies movement $($learnerWatch["managedCandidatePositionApplyCount"]["movement"]), policy applies movement $($learnerWatch["managedCandidatePolicyApplyCount"]["movement"])"
    }
    Write-Host "Samples: $($report["okSampleCount"]) ok, $($report["failedSampleCount"]) failed, $($report["hardBlockerSampleCount"]) with hard blockers"
    Write-Host "AGC movement dB: $($report["agcGainDb"]["movement"]) ($($report["agcStabilityWatch"]["status"])), audio RMS movement dB: $($report["audioRmsDbfs"]["movement"]), min ADC headroom dB: $($report["adcHeadroomDb"]["min"])"
    Write-Host "Candidate normalization: input movement dB $($report["candidateInputDbfs"]["movement"]), output movement dB $($report["candidateOutputDbfs"]["movement"]), weak/strong output gap dB $($report["candidateWeakSignalWatch"]["weakStrongOutputGapDb"]), weak/strong final-audio gap dB $($report["candidateWeakSignalWatch"]["weakStrongFinalAudioGapDb"]), mixed weak/strong status $($report["candidateWeakSignalWatch"]["mixedWeakStrongEvidenceStatus"])"
    Write-Host "Candidate qualified parity: speech final gap dB $($report["candidateWeakSignalWatch"]["speechQualifiedWeakStrongFinalAudioGapDb"]) ($($report["candidateWeakSignalWatch"]["speechQualifiedMixedWeakStrongEvidenceStatus"])), passband final gap dB $($report["candidateWeakSignalWatch"]["passbandQualifiedWeakStrongFinalAudioGapDb"]) ($($report["candidateWeakSignalWatch"]["passbandQualifiedMixedWeakStrongEvidenceStatus"]))"
    $tuneCandidates = @($report["frontendTuneCandidates"])
    if ($tuneCandidates.Count -gt 0) {
        $candidateText = @($tuneCandidates | Select-Object -First 5 | ForEach-Object {
            $snap = Get-JsonValue $_ "tuneSnapDeltaHz"
            $snapText = if ($null -eq $snap -or [long]$snap -eq 0) { "step" } else { "step, snap $snap Hz" }
            "$($_["suggestedVfoMhz"]) MHz ($snapText; peak $($_["peakFrequencyMhz"]) MHz, off $($_["peakOffsetHz"]) Hz, snr $($_["snrDb"]) dB)"
        }) -join "; "
        Write-Host "Frontend tune candidates: $candidateText"
    }
    $nearestTuneCandidate = Get-JsonValue $report "frontendNearestTuneCandidate"
    if ($null -ne $nearestTuneCandidate) {
        Write-Host "Nearest frontend tune target: $($nearestTuneCandidate["suggestedVfoMhz"]) MHz (delta $($nearestTuneCandidate["retuneDeltaHz"]) Hz, peak $($nearestTuneCandidate["peakFrequencyMhz"]) MHz, off $($nearestTuneCandidate["peakOffsetHz"]) Hz)"
    }
    $weakWatch = $report["candidateWeakSignalWatch"]
    if ($null -ne $weakWatch) {
        Write-Host "Candidate weak outcome: weak=$($weakWatch["weakInputSampleCount"]), recovered=$($weakWatch["weakRecoveredSampleCount"]), dropouts=$($weakWatch["weakDropoutSampleCount"]), finalAudible=$($weakWatch["weakDropoutFinalAudibleSampleCount"]), candidateLoss=$($weakWatch["weakDropoutCandidateLossSampleCount"]), hotMakeup=$($weakWatch["hotMakeupSampleCount"])"
        $mixedFocus = $weakWatch["mixedWeakStrongTuningFocus"]
        if ($null -ne $mixedFocus) {
            $outputGapExcessText = if ($null -eq $mixedFocus["outputGapExcessDb"]) { "n/a" } else { "$($mixedFocus["outputGapExcessDb"]) dB" }
            $finalAudioGapExcessText = if ($null -eq $mixedFocus["finalAudioGapExcessDb"]) { "n/a" } else { "$($mixedFocus["finalAudioGapExcessDb"]) dB" }
            Write-Host "Candidate mixed focus: action=$($mixedFocus["preferredAction"]), weak=$($mixedFocus["weakInputSampleCount"]), strong=$($mixedFocus["strongInputSampleCount"]), outputGapExcess=$outputGapExcessText ($($mixedFocus["outputGapDirection"])), finalAudioGapExcess=$finalAudioGapExcessText ($($mixedFocus["finalAudioGapDirection"])), speechFinalGap=$($mixedFocus["speechQualifiedFinalAudioGapDb"]) ($($mixedFocus["speechQualifiedStatus"])), passbandFinalGap=$($mixedFocus["passbandQualifiedFinalAudioGapDb"]) ($($mixedFocus["passbandQualifiedStatus"]))"
        }
    }
    $levelerWatch = $report["rxAudioLevelerWatch"]
    if ($null -ne $levelerWatch) {
        $profileCounts = @($levelerWatch["profileCounts"])
        $profileText = (($profileCounts | ForEach-Object { "$($_["name"])=$($_["count"])" }) -join ",")
        if ([string]::IsNullOrWhiteSpace($profileText)) {
            $profileText = "current=0"
        }
        Write-Host "RX leveler safety: profiles=$profileText, experimental=$($levelerWatch["experimentalSampleCount"]), constrained=$($levelerWatch["constrainedSampleCount"]), boostSlew=$($levelerWatch["boostSlewLimitedSampleCount"]), peakLimited=$($levelerWatch["peakLimitedSampleCount"]), outputLimited=$($levelerWatch["outputLimitedSampleCount"])"
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
