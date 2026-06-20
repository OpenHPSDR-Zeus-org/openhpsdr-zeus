param(
    [string]$BaseUrl = "http://localhost:6060",

    [string]$FrontendUrl = "http://127.0.0.1:5173/?webgpuWaterfall=0",

    [string]$RadioEndpoint = "192.168.1.25:1024",

    [long]$FrequencyHz = 14260000,

    [switch]$UseCurrentRadioState,

    [string]$Mode = "USB",

    [int]$SampleRate = 384000,

    [int]$BoardId = 10,

    [int]$Samples = 24,

    [int]$IntervalMs = 500,

    [int]$TimeoutSec = 5,

    [int]$SettleMs = 3000,

    [int]$FrontendReadyTimeoutSec = 45,

    [string]$OutputRoot = "",

    [string]$LabelPrefix = "g2-frontend-rx-leveler-ab",

    [string]$CandidateProfile = "stable-speech-candidate",

    [string]$CaptureScriptPath = "",

    [string]$BrowserPath = "",

    [switch]$SkipBrowserLaunch,

    [switch]$LeaveBrowserOpen,

    [switch]$SkipCertificateCheck,

    [switch]$ContinueOnError,

    [switch]$PlanOnly,

    [switch]$RequireActiveAudio,

    [int]$MinActiveAudioSamples = 1,

    [int]$ActiveAudioReadyTimeoutSec = 45,

    [double]$ActiveAudioThresholdDbfs = -45.0,

    [switch]$RequirePassbandEvidence,

    [int]$MinPassbandPeakSamples = 1,

    [int]$PassbandReadyTimeoutSec = 45,

    [int]$FrontendNearPassbandThresholdHz = 3000,

    [int]$TuneStepHz = 1000,

    [int]$CaptureAttempts = 1,

    [int]$P2ClientLocalPort = 1025,

    [switch]$SkipP2SocketPreflight
)

$ErrorActionPreference = "Stop"

function Get-RepoRoot {
    return (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
}

function Normalize-BaseUrl {
    param([Parameter(Mandatory = $true)][string]$Url)
    return $Url.TrimEnd("/")
}

function Get-Timestamp {
    return (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmssZ")
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

function New-Directory {
    param([Parameter(Mandatory = $true)][string]$Path)
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    [System.IO.Directory]::CreateDirectory((ConvertTo-LongFileSystemPath -Path $fullPath)) | Out-Null
    return $fullPath
}

function Get-ObjectValue {
    param(
        $Object,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if ($null -eq $Object) {
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

function Get-LevelerCapabilityHint {
    param([Parameter(Mandatory = $true)][string]$Base)

    try {
        $diagnostics = Invoke-JsonGet -Url "$Base/api/dsp/live-diagnostics"
    }
    catch {
        return "Live diagnostics capability check also failed at $Base/api/dsp/live-diagnostics: $($_.Exception.Message)"
    }

    $status = [string](Get-ObjectValue $diagnostics "status")
    $capabilityStatus = [string](Get-ObjectValue $diagnostics "rxAudioLevelerCapabilityStatus")
    $apiAvailable = Get-ObjectValue $diagnostics "rxAudioLevelerProfileApiAvailable"
    $endpoint = [string](Get-ObjectValue $diagnostics "rxAudioLevelerProfileEndpoint")
    $candidate = [string](Get-ObjectValue $diagnostics "rxAudioLevelerCandidateProfile")
    if ([string]::IsNullOrWhiteSpace($endpoint)) {
        $endpoint = "/api/dsp/rx-audio-leveler-profile"
    }
    if ([string]::IsNullOrWhiteSpace($capabilityStatus)) {
        $capabilityStatus = "not-advertised"
    }
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        $candidate = $CandidateProfile
    }

    $apiAvailableText = if ($null -eq $apiAvailable) { "missing" } else { [string]$apiAvailable }
    return "Live diagnostics status='$status' reports rxAudioLevelerProfileApiAvailable=$apiAvailableText, rxAudioLevelerCapabilityStatus='$capabilityStatus', endpoint='$endpoint', candidateProfile='$candidate'. If these fields are missing or not candidate-profile-api-ready, the active backend is not the updated WDSP v2 profile-switch build."
}

function Get-StringArrayValue {
    param(
        $Object,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $value = Get-ObjectValue $Object $Name
    if ($null -eq $value) {
        return @()
    }

    if ($value -is [System.Array]) {
        return @($value | ForEach-Object { [string]$_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    }

    $text = [string]$value
    if ([string]::IsNullOrWhiteSpace($text)) {
        return @()
    }

    return @($text)
}

function Assert-LevelerProfileSupportsCandidate {
    param(
        $ProfileResponse,
        [Parameter(Mandatory = $true)][string]$Endpoint
    )

    if ([string]::Equals($CandidateProfile, "current", [StringComparison]::OrdinalIgnoreCase)) {
        return
    }

    $supportedProfiles = @(Get-StringArrayValue $ProfileResponse "supportedProfiles")
    foreach ($profile in $supportedProfiles) {
        if ([string]::Equals($profile, $CandidateProfile, [StringComparison]::OrdinalIgnoreCase)) {
            return
        }
    }

    $supportedText = if ($supportedProfiles.Count -gt 0) { $supportedProfiles -join ", " } else { "<none-advertised>" }
    throw "RX audio leveler candidate profile '$CandidateProfile' is not advertised by $Endpoint. Supported profiles: $supportedText. Start the updated WDSP v2 backend or pass -CandidateProfile with an advertised profile before running the G2 frontend A/B capture."
}

function Write-TextFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Value
    )

    $parent = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        [System.IO.Directory]::CreateDirectory((ConvertTo-LongFileSystemPath -Path $parent)) | Out-Null
    }

    [System.IO.File]::WriteAllText(
        (ConvertTo-LongFileSystemPath -Path $Path),
        $Value,
        [System.Text.Encoding]::UTF8)
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
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [Parameter(Mandatory = $true)][string]$Method
    )

    $args = @{
        Uri = $Url
        Method = $Method
        TimeoutSec = $TimeoutSec
    }

    if ($SkipCertificateCheck -and (Get-Command Invoke-RestMethod).Parameters.ContainsKey("SkipCertificateCheck")) {
        $args["SkipCertificateCheck"] = $true
    }

    return $args
}

function Invoke-JsonGet {
    param([Parameter(Mandatory = $true)][string]$Url)

    $args = New-RestArgs -Url $Url -Method "GET"
    return Invoke-RestMethod @args
}

function Invoke-JsonPost {
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [Parameter(Mandatory = $true)]$Body,
        [switch]$AllowAlreadyConnected
    )

    $args = New-RestArgs -Url $Url -Method "POST"
    $args["ContentType"] = "application/json"
    $args["Body"] = ($Body | ConvertTo-Json -Compress)

    try {
        return Invoke-RestMethod @args
    }
    catch {
        $statusCode = $null
        if ($null -ne $_.Exception.Response) {
            try { $statusCode = [int]$_.Exception.Response.StatusCode } catch { }
        }

        $message = $_.Exception.Message
        if ($_.ErrorDetails -and -not [string]::IsNullOrWhiteSpace($_.ErrorDetails.Message)) {
            $message = $_.ErrorDetails.Message
        }

        if ($AllowAlreadyConnected -and $statusCode -eq 409) {
            return [pscustomobject][ordered]@{
                alreadyConnected = $true
                error = $message
            }
        }

        throw
    }
}

function Resolve-BrowserPath {
    param([string]$RequestedPath)

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        if (Test-Path -LiteralPath $RequestedPath) {
            return (Resolve-Path -LiteralPath $RequestedPath).Path
        }

        throw "Frontend browser executable not found: $RequestedPath"
    }

    $candidates = @(
        "C:\Program Files\Google\Chrome\Application\chrome.exe",
        "C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
        "C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw "Frontend browser executable not found. Install Chrome/Edge, pass -BrowserPath, or use -SkipBrowserLaunch with an already-open Zeus frontend."
}

function Start-FrontendBrowser {
    param(
        [Parameter(Mandatory = $true)][string]$ExecutablePath,
        [Parameter(Mandatory = $true)][string]$Url,
        [Parameter(Mandatory = $true)][string]$RunRoot
    )

    $profileDir = New-Directory (Join-Path $RunRoot "browser-profile")
    $args = @(
        "--headless=new",
        "--disable-search-engine-choice-screen",
        "--use-gl=swiftshader",
        "--ignore-gpu-blocklist",
        "--window-size=1440,900",
        "--user-data-dir=$profileDir",
        $Url
    )

    return Start-Process `
        -FilePath $ExecutablePath `
        -ArgumentList $args `
        -WindowStyle Hidden `
        -PassThru
}

function Wait-FrontendEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$Base,
        [int]$TimeoutSeconds = 45
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $samples = New-Object System.Collections.Generic.List[object]
    $latestDiagnostics = $null
    $latestScene = $null
    do {
        try {
            $latestDiagnostics = Invoke-JsonGet -Url "$Base/api/dsp/live-diagnostics"
            $latestScene = Invoke-JsonGet -Url "$Base/api/radio/diagnostics/dsp-scene"
            $runtime = $latestDiagnostics.runtimeEvidence
            $sample = [ordered]@{
                sampledUtc = (Get-Date).ToUniversalTime().ToString("o")
                status = [string]$latestDiagnostics.status
                readinessScore = $latestDiagnostics.readinessScore
                readyForLiveBenchmark = [bool]$latestDiagnostics.readyForLiveBenchmark
                frontendSceneFresh = [bool]$latestDiagnostics.frontendSceneFresh
                frontendSceneStatus = [string]$latestDiagnostics.frontendSceneStatus
                frontendSceneAgeMs = $latestDiagnostics.frontendSceneAgeMs
                wdspActive = [bool]$latestDiagnostics.wdspActive
                audioFresh = if ($null -eq $runtime) { $false } else { [bool]$runtime.audioFresh }
                audioRmsDbfs = if ($null -eq $runtime) { $null } else { $runtime.audioRmsDbfs }
                rxMetersFresh = if ($null -eq $runtime) { $false } else { [bool]$runtime.rxMetersFresh }
                radioVfoHz = $latestDiagnostics.radioVfoHz
                radioLoHz = $latestDiagnostics.radioLoHz
                radioMode = [string]$latestDiagnostics.radioMode
                radioSampleRate = $latestDiagnostics.radioSampleRate
                sceneStatus = if ($null -eq $latestScene) { $null } else { [string]$latestScene.status }
                sceneFresh = if ($null -eq $latestScene) { $false } else { [bool]$latestScene.fresh }
                sceneAgeMs = if ($null -eq $latestScene) { $null } else { $latestScene.ageMs }
            }
            $samples.Add([pscustomobject]$sample) | Out-Null

            if ([bool]$latestDiagnostics.readyForLiveBenchmark -and
                [bool]$latestDiagnostics.frontendSceneFresh -and
                $null -ne $runtime -and
                [bool]$runtime.audioFresh) {
                break
            }
        }
        catch {
            $samples.Add([pscustomobject][ordered]@{
                sampledUtc = (Get-Date).ToUniversalTime().ToString("o")
                error = $_.Exception.Message
            }) | Out-Null
        }

        Start-Sleep -Milliseconds 1000
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    return [pscustomobject][ordered]@{
        ready = $null -ne $latestDiagnostics -and
            [bool]$latestDiagnostics.readyForLiveBenchmark -and
            [bool]$latestDiagnostics.frontendSceneFresh -and
            $null -ne $latestDiagnostics.runtimeEvidence -and
            [bool]$latestDiagnostics.runtimeEvidence.audioFresh
        diagnostics = $latestDiagnostics
        scene = $latestScene
        samples = @($samples.ToArray())
    }
}

function Get-NullableDouble {
    param($Value)

    if ($null -eq $Value) {
        return $null
    }

    try {
        return [double]$Value
    }
    catch {
        return $null
    }
}

function Normalize-TuneStepHz {
    param($Value)

    $numeric = Get-NullableDouble $Value
    if ($null -eq $numeric -or [double]::IsNaN([double]$numeric) -or [double]::IsInfinity([double]$numeric)) {
        return 1000
    }

    $step = [int][Math]::Round([double]$numeric)
    if ($step -le 0) {
        return 1000
    }

    return $step
}

function Quantize-HzToStep {
    param(
        [double]$Hz,
        [int]$StepHz
    )

    $step = Normalize-TuneStepHz $StepHz
    return [long]([Math]::Floor(($Hz / [double]$step) + 0.5) * [double]$step)
}

function Test-ActiveRxAudioRuntime {
    param(
        $Runtime,
        [double]$ThresholdDbfs
    )

    if ($null -eq $Runtime -or -not [bool]$Runtime.audioFresh) {
        return $false
    }
    if ($null -ne $Runtime.txMonitorRequested -and [bool]$Runtime.txMonitorRequested) {
        return $false
    }
    if ($null -ne $Runtime.audioSource -and -not [string]::Equals([string]$Runtime.audioSource, "rx", [StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }

    $audioRmsDbfs = Get-NullableDouble $Runtime.audioRmsDbfs
    return $null -ne $audioRmsDbfs -and $audioRmsDbfs -ge $ThresholdDbfs
}

function Wait-ActiveAudioEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$Base,
        [int]$TimeoutSeconds = 45,
        [int]$MinimumSamples = 1,
        [double]$ThresholdDbfs = -45.0
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $samples = New-Object System.Collections.Generic.List[object]
    $latestDiagnostics = $null
    $latestScene = $null
    $activeSampleCount = 0

    do {
        try {
            $latestDiagnostics = Invoke-JsonGet -Url "$Base/api/dsp/live-diagnostics"
            $latestScene = Invoke-JsonGet -Url "$Base/api/radio/diagnostics/dsp-scene"
            $runtime = $latestDiagnostics.runtimeEvidence
            $audioRmsDbfs = if ($null -eq $runtime) { $null } else { Get-NullableDouble $runtime.audioRmsDbfs }
            $active = Test-ActiveRxAudioRuntime -Runtime $runtime -ThresholdDbfs $ThresholdDbfs
            if ($active) {
                $activeSampleCount++
            }

            $samples.Add([pscustomobject][ordered]@{
                sampledUtc = (Get-Date).ToUniversalTime().ToString("o")
                status = [string]$latestDiagnostics.status
                readyForLiveBenchmark = [bool]$latestDiagnostics.readyForLiveBenchmark
                frontendSceneFresh = [bool]$latestDiagnostics.frontendSceneFresh
                wdspActive = [bool]$latestDiagnostics.wdspActive
                audioFresh = if ($null -eq $runtime) { $false } else { [bool]$runtime.audioFresh }
                audioSource = if ($null -eq $runtime) { $null } else { [string]$runtime.audioSource }
                txMonitorRequested = if ($null -eq $runtime -or $null -eq $runtime.txMonitorRequested) { $false } else { [bool]$runtime.txMonitorRequested }
                audioRmsDbfs = $audioRmsDbfs
                activeRxAudio = $active
                activeAudioThresholdDbfs = $ThresholdDbfs
                activeSampleCount = $activeSampleCount
                sceneFresh = if ($null -eq $latestScene) { $false } else { [bool]$latestScene.fresh }
            }) | Out-Null

            if ($activeSampleCount -ge $MinimumSamples) {
                break
            }
        }
        catch {
            $samples.Add([pscustomobject][ordered]@{
                sampledUtc = (Get-Date).ToUniversalTime().ToString("o")
                error = $_.Exception.Message
                activeRxAudio = $false
                activeSampleCount = $activeSampleCount
            }) | Out-Null
        }

        Start-Sleep -Milliseconds 500
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    return [pscustomobject][ordered]@{
        required = $true
        ready = $activeSampleCount -ge $MinimumSamples
        minActiveAudioSamples = $MinimumSamples
        activeAudioThresholdDbfs = $ThresholdDbfs
        activeSampleCount = $activeSampleCount
        timeoutSeconds = $TimeoutSeconds
        diagnostics = $latestDiagnostics
        scene = $latestScene
        samples = @($samples.ToArray())
        recommendation = if ($activeSampleCount -ge $MinimumSamples) {
            "Active RX audio was present before starting A/B capture."
        }
        else {
            "Tune to an active signal and rerun capture; A/B proof windows should not start from a silent receiver."
        }
    }
}

function Test-OffsetInFilterPassband {
    param(
        [double]$OffsetHz,
        $FilterLowHz,
        $FilterHighHz
    )

    $low = Get-NullableDouble $FilterLowHz
    $high = Get-NullableDouble $FilterHighHz
    if ($null -eq $low -or $null -eq $high) {
        return $false
    }

    $min = [Math]::Min([double]$low, [double]$high)
    $max = [Math]::Max([double]$low, [double]$high)
    return $OffsetHz -ge $min -and $OffsetHz -le $max
}

function Get-FrontendPeakOffsetHz {
    param(
        $Peak,
        $Diagnostics
    )

    $offset = Get-NullableDouble $Peak.offsetHz
    if ($null -ne $offset) {
        return $offset
    }

    $frequency = Get-NullableDouble $Peak.frequencyHz
    $vfo = Get-NullableDouble $Diagnostics.radioVfoHz
    if ($null -ne $frequency -and $null -ne $vfo) {
        return [double]$frequency - [double]$vfo
    }

    return $null
}

function Measure-PassbandEvidence {
    param(
        $Diagnostics,
        $Scene,
        [int]$NearThresholdHz,
        [int]$TuneStepHz = 1000
    )

    $peaks = if ($null -eq $Scene -or $null -eq $Scene.topPeaks) { @() } else { @($Scene.topPeaks) }
    $filterLow = if ($null -eq $Diagnostics) { $null } else { $Diagnostics.rxChainFilterLowHz }
    $filterHigh = if ($null -eq $Diagnostics) { $null } else { $Diagnostics.rxChainFilterHighHz }
    $filterKnown = $null -ne (Get-NullableDouble $filterLow) -and $null -ne (Get-NullableDouble $filterHigh)
    $currentVfo = if ($null -eq $Diagnostics) { $null } else { Get-NullableDouble $Diagnostics.radioVfoHz }
    $nearCount = 0
    $filterCount = 0
    $nearestOffset = $null
    $nearestAbsOffset = $null
    $topQualified = New-Object System.Collections.Generic.List[object]
    $tuneCandidatesByBucket = @{}

    foreach ($peak in $peaks) {
        $offset = Get-FrontendPeakOffsetHz -Peak $peak -Diagnostics $Diagnostics
        if ($null -eq $offset) {
            continue
        }

        $absOffset = [Math]::Abs([double]$offset)
        if ($null -eq $nearestAbsOffset -or $absOffset -lt $nearestAbsOffset) {
            $nearestAbsOffset = $absOffset
            $nearestOffset = [double]$offset
        }

        $nearPassband = $absOffset -le $NearThresholdHz
        $filterPassband = Test-OffsetInFilterPassband -OffsetHz ([double]$offset) -FilterLowHz $filterLow -FilterHighHz $filterHigh
        if ($nearPassband) {
            $nearCount++
        }
        if ($filterPassband) {
            $filterCount++
        }
        if (($filterKnown -and $filterPassband) -or (-not $filterKnown -and $nearPassband)) {
            $topQualified.Add([ordered]@{
                frequencyHz = $peak.frequencyHz
                offsetHz = [Math]::Round([double]$offset, 3)
                snrDb = $peak.snrDb
                dbfs = $peak.dbfs
                confidence = $peak.confidence
                filterPassband = $filterPassband
                nearPassband = $nearPassband
            }) | Out-Null
        }

        if ($filterKnown) {
            $low = [double](Get-NullableDouble $filterLow)
            $high = [double](Get-NullableDouble $filterHigh)
            $passLow = [Math]::Min($low, $high)
            $passHigh = [Math]::Max($low, $high)
            $frequency = Get-NullableDouble $peak.frequencyHz
            if ($null -eq $frequency -and $null -ne $currentVfo) {
                $frequency = [double]$currentVfo + [double]$offset
            }

            if ($passHigh -gt $passLow -and $null -ne $frequency) {
                $passbandCenterHz = ($passLow + $passHigh) / 2.0
                $rawSuggestedVfoHz = [long][Math]::Round([double]$frequency - $passbandCenterHz)
                $suggestedVfoHz = Quantize-HzToStep -Hz ([double]$rawSuggestedVfoHz) -StepHz $TuneStepHz
                if ($rawSuggestedVfoHz -gt 0 -and $suggestedVfoHz -gt 0) {
                    $distance = if ($filterPassband) {
                        0.0
                    }
                    elseif ([double]$offset -lt $passLow) {
                        [Math]::Round($passLow - [double]$offset, 3)
                    }
                    else {
                        [Math]::Round([double]$offset - $passHigh, 3)
                    }
                    $snr = Get-NullableDouble $peak.snrDb
                    $dbfs = Get-NullableDouble $peak.dbfs
                    $confidence = Get-NullableDouble $peak.confidence
                    $snrForScore = if ($null -eq $snr) { 0.0 } else { [double]$snr }
                    $score = [Math]::Round($snrForScore + ([Math]::Min(8.0, [double]$distance / 1000.0) * 0.75), 3)
                    $bucket = [string]$suggestedVfoHz
                    if (-not $tuneCandidatesByBucket.ContainsKey($bucket) -or [double]$tuneCandidatesByBucket[$bucket].score -lt $score) {
                        $tuneCandidatesByBucket[$bucket] = [ordered]@{
                            rank = 0
                            suggestedVfoHz = $suggestedVfoHz
                            suggestedVfoMhz = [Math]::Round($suggestedVfoHz / 1000000.0, 6)
                            suggestedVfoStepHz = Normalize-TuneStepHz $TuneStepHz
                            exactSuggestedVfoHz = $rawSuggestedVfoHz
                            exactSuggestedVfoMhz = [Math]::Round($rawSuggestedVfoHz / 1000000.0, 6)
                            tuneSnapDeltaHz = [long]($suggestedVfoHz - $rawSuggestedVfoHz)
                            currentVfoHz = if ($null -eq $currentVfo) { $null } else { [long][Math]::Round([double]$currentVfo) }
                            retuneDeltaHz = if ($null -eq $currentVfo) { $null } else { [long]($suggestedVfoHz - [long][Math]::Round([double]$currentVfo)) }
                            exactRetuneDeltaHz = if ($null -eq $currentVfo) { $null } else { [long]($rawSuggestedVfoHz - [long][Math]::Round([double]$currentVfo)) }
                            peakFrequencyHz = [long][Math]::Round([double]$frequency)
                            peakFrequencyMhz = [Math]::Round([double]$frequency / 1000000.0, 6)
                            peakOffsetHz = [long][Math]::Round([double]$offset)
                            filterLowHz = [Math]::Round($passLow, 1)
                            filterHighHz = [Math]::Round($passHigh, 1)
                            passbandCenterHz = [Math]::Round($passbandCenterHz, 1)
                            filterDistanceHz = [long][Math]::Round([double]$distance)
                            snrDb = if ($null -eq $snr) { $null } else { [Math]::Round([double]$snr, 1) }
                            dbfs = if ($null -eq $dbfs) { $null } else { [Math]::Round([double]$dbfs, 1) }
                            confidence = if ($null -eq $confidence) { $null } else { [Math]::Round([double]$confidence, 3) }
                            reason = if ($filterPassband) { "peak-already-in-passband" } else { "retune-to-center-frontend-peak" }
                            score = $score
                        }
                    }
                }
            }
        }
    }

    $qualifiedCount = if ($filterKnown) { $filterCount } else { $nearCount }
    $rank = 0
    $tuneCandidates = @($tuneCandidatesByBucket.Values |
        Sort-Object @{Expression = { [double]$_.score }; Descending = $true },
            @{Expression = { [double]$_.snrDb }; Descending = $true } |
        Select-Object -First 6 |
        ForEach-Object {
            $rank++
            $_["rank"] = $rank
            $_
        })
    $nearestTuneCandidate = @($tuneCandidatesByBucket.Values |
        Where-Object { $null -ne $_.retuneDeltaHz } |
        Sort-Object @{Expression = { [Math]::Abs([double]$_.retuneDeltaHz) }; Ascending = $true },
            @{Expression = { [double]$_.filterDistanceHz }; Ascending = $true },
            @{Expression = { [double]$_.snrDb }; Descending = $true } |
        Select-Object -First 1)

    return [ordered]@{
        qualified = $qualifiedCount -gt 0
        qualifiedPeakCount = $qualifiedCount
        filterPassbandKnown = $filterKnown
        filterLowHz = $filterLow
        filterHighHz = $filterHigh
        filterPassbandPeakCount = $filterCount
        nearPassbandPeakCount = $nearCount
        nearPassbandThresholdHz = $NearThresholdHz
        topPeakCount = $peaks.Count
        nearestOffsetHz = $nearestOffset
        nearestAbsOffsetHz = $nearestAbsOffset
        topQualifiedPeaks = @($topQualified.ToArray() | Select-Object -First 8)
        tuneCandidates = @($tuneCandidates)
        bestTuneCandidate = if ($tuneCandidates.Count -le 0) { $null } else { $tuneCandidates[0] }
        nearestTuneCandidate = if ($nearestTuneCandidate.Count -le 0) { $null } else { $nearestTuneCandidate[0] }
    }
}

function Wait-PassbandEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$Base,
        [int]$TimeoutSeconds = 45,
        [int]$MinimumSamples = 1,
        [int]$NearThresholdHz = 3000,
        [int]$TuneStepHz = 1000,
        [long]$OperatorFrequencyHz = 0
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $samples = New-Object System.Collections.Generic.List[object]
    $latestDiagnostics = $null
    $latestScene = $null
    $latestTuneCandidates = @()
    $bestTuneCandidate = $null
    $nearestTuneCandidate = $null
    $passbandSampleCount = 0

    do {
        try {
            $latestDiagnostics = Invoke-JsonGet -Url "$Base/api/dsp/live-diagnostics"
            $latestScene = Invoke-JsonGet -Url "$Base/api/radio/diagnostics/dsp-scene"
            $evidence = Measure-PassbandEvidence -Diagnostics $latestDiagnostics -Scene $latestScene -NearThresholdHz $NearThresholdHz -TuneStepHz $TuneStepHz
            $latestTuneCandidates = @($evidence.tuneCandidates)
            if ($null -ne $evidence.bestTuneCandidate) {
                $bestTuneCandidate = $evidence.bestTuneCandidate
            }
            if ($null -ne $evidence.nearestTuneCandidate) {
                $nearestTuneCandidate = $evidence.nearestTuneCandidate
            }
            if ([bool]$evidence.qualified) {
                $passbandSampleCount++
            }

            $samples.Add([pscustomobject][ordered]@{
                sampledUtc = (Get-Date).ToUniversalTime().ToString("o")
                status = [string]$latestDiagnostics.status
                readyForLiveBenchmark = [bool]$latestDiagnostics.readyForLiveBenchmark
                frontendSceneFresh = [bool]$latestDiagnostics.frontendSceneFresh
                sceneFresh = if ($null -eq $latestScene) { $false } else { [bool]$latestScene.fresh }
                passbandQualified = [bool]$evidence.qualified
                passbandSampleCount = $passbandSampleCount
                qualifiedPeakCount = [int]$evidence.qualifiedPeakCount
                filterPassbandKnown = [bool]$evidence.filterPassbandKnown
                filterPassbandPeakCount = [int]$evidence.filterPassbandPeakCount
                nearPassbandPeakCount = [int]$evidence.nearPassbandPeakCount
                topPeakCount = [int]$evidence.topPeakCount
                nearestOffsetHz = $evidence.nearestOffsetHz
                nearestAbsOffsetHz = $evidence.nearestAbsOffsetHz
                filterLowHz = $evidence.filterLowHz
                filterHighHz = $evidence.filterHighHz
                nearPassbandThresholdHz = $NearThresholdHz
                topQualifiedPeaks = @($evidence.topQualifiedPeaks)
                tuneCandidates = @($evidence.tuneCandidates | Select-Object -First 3)
                bestTuneCandidate = $evidence.bestTuneCandidate
                nearestTuneCandidate = $evidence.nearestTuneCandidate
            }) | Out-Null

            if ($passbandSampleCount -ge $MinimumSamples) {
                break
            }
        }
        catch {
            $samples.Add([pscustomobject][ordered]@{
                sampledUtc = (Get-Date).ToUniversalTime().ToString("o")
                error = $_.Exception.Message
                passbandQualified = $false
                passbandSampleCount = $passbandSampleCount
            }) | Out-Null
        }

        Start-Sleep -Milliseconds 500
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    $ready = $passbandSampleCount -ge $MinimumSamples
    $operatorFrequencyMhz = if ($OperatorFrequencyHz -gt 0) { [Math]::Round($OperatorFrequencyHz / 1000000.0, 6) } else { $null }
    $retuneCandidateAvailable = $null -ne $nearestTuneCandidate -or $null -ne $bestTuneCandidate

    return [pscustomobject][ordered]@{
        required = $true
        ready = $ready
        status = if ($ready) { "ready" } else { "manual-tuned-passband-incomplete" }
        minPassbandPeakSamples = $MinimumSamples
        passbandSampleCount = $passbandSampleCount
        timeoutSeconds = $TimeoutSeconds
        nearPassbandThresholdHz = $NearThresholdHz
        tuneStepHz = Normalize-TuneStepHz $TuneStepHz
        operatorFrequencyHz = if ($OperatorFrequencyHz -gt 0) { $OperatorFrequencyHz } else { $null }
        operatorFrequencyMhz = $operatorFrequencyMhz
        manualTuningAuthoritative = $true
        retuneRecommendationSuppressed = [bool]($retuneCandidateAvailable -and -not $ready)
        bestTuneCandidate = $bestTuneCandidate
        nearestTuneCandidate = $nearestTuneCandidate
        tuneCandidates = @($latestTuneCandidates)
        diagnostics = $latestDiagnostics
        scene = $latestScene
        samples = @($samples.ToArray())
        recommendation = if ($ready) {
            "Frontend tuned-passband peak evidence was present before starting A/B capture."
        }
        elseif ($OperatorFrequencyHz -gt 0) {
            "Manual tuning is authoritative for this RX leveler proof. Keep the operator VFO at $operatorFrequencyMhz MHz or extend dwell until frontend peaks qualify inside the RX filter passband; off-passband retune candidates are diagnostic hints only."
        }
        else {
            "Keep the operator-selected signal centered in the RX filter passband, then extend dwell until frontend passband peaks qualify before starting RX leveler A/B capture."
        }
    }
}

function Assert-LevelerProfileEndpoint {
    param([Parameter(Mandatory = $true)][string]$Base)

    try {
        $profile = Invoke-JsonGet -Url "$Base/api/dsp/rx-audio-leveler-profile"
    }
    catch {
        $capabilityHint = Get-LevelerCapabilityHint -Base $Base
        throw "RX audio leveler profile API is not reachable at $Base/api/dsp/rx-audio-leveler-profile. Start the updated WDSP v2 backend before running the G2 frontend A/B capture. $capabilityHint $($_.Exception.Message)"
    }

    if ($null -eq $profile -or
        [string]::IsNullOrWhiteSpace([string]$profile.profile) -or
        [string]::IsNullOrWhiteSpace([string]$profile.activeProfile)) {
        throw "RX audio leveler profile API response is missing profile/activeProfile fields at $Base/api/dsp/rx-audio-leveler-profile."
    }

    Assert-LevelerProfileSupportsCandidate -ProfileResponse $profile -Endpoint "$Base/api/dsp/rx-audio-leveler-profile"

    return $profile
}

function Wait-FrontendScenePreflight {
    param(
        [Parameter(Mandatory = $true)][string]$Base,
        [int]$TimeoutSeconds = 45
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $samples = New-Object System.Collections.Generic.List[object]
    $latestDiagnostics = $null
    $latestScene = $null
    do {
        try {
            $latestDiagnostics = Invoke-JsonGet -Url "$Base/api/dsp/live-diagnostics"
            $latestScene = Invoke-JsonGet -Url "$Base/api/radio/diagnostics/dsp-scene"
            $sample = [ordered]@{
                sampledUtc = (Get-Date).ToUniversalTime().ToString("o")
                frontendSceneFresh = [bool]$latestDiagnostics.frontendSceneFresh
                frontendSceneStatus = [string]$latestDiagnostics.frontendSceneStatus
                frontendSceneAgeMs = $latestDiagnostics.frontendSceneAgeMs
                sceneStatus = if ($null -eq $latestScene) { $null } else { [string]$latestScene.status }
                sceneFresh = if ($null -eq $latestScene) { $false } else { [bool]$latestScene.fresh }
                sceneAgeMs = if ($null -eq $latestScene) { $null } else { $latestScene.ageMs }
            }
            $samples.Add([pscustomobject]$sample) | Out-Null

            if ([bool]$latestDiagnostics.frontendSceneFresh -and
                $null -ne $latestScene -and
                [bool]$latestScene.fresh) {
                break
            }
        }
        catch {
            $samples.Add([pscustomobject][ordered]@{
                sampledUtc = (Get-Date).ToUniversalTime().ToString("o")
                error = $_.Exception.Message
            }) | Out-Null
        }

        Start-Sleep -Milliseconds 1000
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    return [pscustomobject][ordered]@{
        ready = $null -ne $latestDiagnostics -and
            [bool]$latestDiagnostics.frontendSceneFresh -and
            $null -ne $latestScene -and
            [bool]$latestScene.fresh
        diagnostics = $latestDiagnostics
        scene = $latestScene
        samples = @($samples.ToArray())
    }
}

function Get-CurrentRadioState {
    param([Parameter(Mandatory = $true)][string]$Base)

    $diagnostics = Invoke-JsonGet -Url "$Base/api/dsp/live-diagnostics"
    $vfo = Get-NullableDouble $diagnostics.radioVfoHz
    if ($null -eq $vfo -or $vfo -le 0) {
        throw "UseCurrentRadioState could not read a valid radioVfoHz from $Base/api/dsp/live-diagnostics."
    }

    $modeName = [string]$diagnostics.radioMode
    if ([string]::IsNullOrWhiteSpace($modeName)) {
        $modeName = $Mode
    }

    return [pscustomobject][ordered]@{
        frequencyHz = [long][Math]::Round([double]$vfo)
        mode = $modeName
        radioVfoHz = $diagnostics.radioVfoHz
        radioLoHz = $diagnostics.radioLoHz
        radioMode = [string]$diagnostics.radioMode
        radioCtunEnabled = $diagnostics.radioCtunEnabled
        radioSampleRate = $diagnostics.radioSampleRate
        readyForLiveBenchmark = [bool]$diagnostics.readyForLiveBenchmark
        wdspActive = [bool]$diagnostics.wdspActive
        frontendSceneFresh = [bool]$diagnostics.frontendSceneFresh
        generatedUtc = [string]$diagnostics.generatedUtc
    }
}

function Test-P2ClientSocketAvailability {
    param(
        [int]$LocalPort = 1025,
        [switch]$Skip
    )

    if ($Skip) {
        return [pscustomobject][ordered]@{
            ready = $true
            skipped = $true
            localPort = $LocalPort
            owners = @()
            diagnosticRecommendation = "P2 socket preflight was skipped by request."
        }
    }

    if ($LocalPort -lt 1 -or $LocalPort -gt 65535) {
        throw "P2ClientLocalPort must be between 1 and 65535."
    }

    if ($null -eq (Get-Command Get-NetUDPEndpoint -ErrorAction SilentlyContinue)) {
        return [pscustomobject][ordered]@{
            ready = $true
            skipped = $true
            localPort = $LocalPort
            owners = @()
            diagnosticRecommendation = "Get-NetUDPEndpoint is unavailable; P2 socket preflight was skipped."
        }
    }

    $owners = @(
        Get-NetUDPEndpoint -LocalPort $LocalPort -ErrorAction SilentlyContinue |
            ForEach-Object {
                $process = $null
                try {
                    $process = Get-CimInstance Win32_Process -Filter ("ProcessId={0}" -f $_.OwningProcess) -ErrorAction SilentlyContinue
                }
                catch {
                }

                [pscustomobject][ordered]@{
                    localAddress = [string]$_.LocalAddress
                    localPort = $_.LocalPort
                    owningProcess = $_.OwningProcess
                    processName = if ($null -eq $process) { $null } else { [string]$process.Name }
                    commandLine = if ($null -eq $process) { $null } else { [string]$process.CommandLine }
                }
            }
    )

    return [pscustomobject][ordered]@{
        ready = $owners.Count -eq 0
        skipped = $false
        localPort = $LocalPort
        owners = $owners
        diagnosticRecommendation = if ($owners.Count -eq 0) {
            "P2 client UDP socket is available."
        }
        else {
            "Stop or disconnect the process that owns the P2 client UDP socket before running isolated G2 capture."
        }
    }
}

function Test-LevelerResetAligned {
    param($Capture)

    if ($null -eq $Capture -or $null -eq $Capture.resetToCurrent) {
        return $false
    }

    $reset = $Capture.resetToCurrent
    return [bool]$reset.ready -and
        [bool]$reset.profileAligned -and
        [bool]$reset.activeProfileAligned -and
        [string]$reset.profile -eq "current" -and
        [string]$reset.activeProfile -eq "current"
}

function Test-LevelerProfileCurrentAligned {
    param($Profile)

    if ($null -eq $Profile) {
        return $false
    }

    return [string]$Profile.profile -eq "current" -and
        [string]$Profile.activeProfile -eq "current"
}

function Test-RecoverableLevelerAbEvidenceFailure {
    param($Capture)

    if ($null -eq $Capture) {
        return $false
    }
    if ($null -ne $Capture.PSObject.Properties["ok"] -and [bool]$Capture.ok) {
        return $false
    }
    if (-not (Test-LevelerResetAligned -Capture $Capture)) {
        return $false
    }

    $errorText = if ($null -eq $Capture.error) { "" } else { [string]$Capture.error }
    if ($errorText.StartsWith("Active RX audio evidence is required", [StringComparison]::OrdinalIgnoreCase) -or
        $errorText.StartsWith("Tuned passband evidence is required", [StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }

    $active = $Capture.activeAudioEvidence
    if ($RequireActiveAudio -and $null -ne $active -and -not [bool]$active.ready) {
        return $true
    }

    $passband = $Capture.passbandEvidence
    if ($RequirePassbandEvidence -and $null -ne $passband -and -not [bool]$passband.ready) {
        return $true
    }

    return $false
}

function New-LevelerAbAttemptRecord {
    param(
        [Parameter(Mandatory = $true)][int]$Attempt,
        $Capture,
        [string]$ErrorText = ""
    )

    return [ordered]@{
        attempt = $Attempt
        ok = if ($null -eq $Capture -or $null -eq $Capture.PSObject.Properties["ok"]) { $false } else { [bool]$Capture.ok }
        recoverable = Test-RecoverableLevelerAbEvidenceFailure -Capture $Capture
        error = if ([string]::IsNullOrWhiteSpace($ErrorText)) {
            if ($null -eq $Capture -or $null -eq $Capture.error) { $null } else { [string]$Capture.error }
        }
        else {
            $ErrorText
        }
        summaryPath = if ($null -eq $Capture -or $null -eq $Capture.summaryPath) { $null } else { [string]$Capture.summaryPath }
        resetAligned = Test-LevelerResetAligned -Capture $Capture
        activeAudioReady = if ($null -eq $Capture -or $null -eq $Capture.activeAudioEvidence) { $null } else { [bool]$Capture.activeAudioEvidence.ready }
        passbandReady = if ($null -eq $Capture -or $null -eq $Capture.passbandEvidence) { $null } else { [bool]$Capture.passbandEvidence.ready }
        currentActiveAudioSampleCount = if ($null -eq $Capture -or $null -eq $Capture.activeAudioEvidence) { $null } else { $Capture.activeAudioEvidence.currentActiveAudioSampleCount }
        candidateActiveAudioSampleCount = if ($null -eq $Capture -or $null -eq $Capture.activeAudioEvidence) { $null } else { $Capture.activeAudioEvidence.candidateActiveAudioSampleCount }
        currentPassbandPeakSampleCount = if ($null -eq $Capture -or $null -eq $Capture.passbandEvidence) { $null } else { $Capture.passbandEvidence.currentPassbandPeakSampleCount }
        candidatePassbandPeakSampleCount = if ($null -eq $Capture -or $null -eq $Capture.passbandEvidence) { $null } else { $Capture.passbandEvidence.candidatePassbandPeakSampleCount }
    }
}

function Invoke-LevelerAbCapture {
    param(
        [Parameter(Mandatory = $true)][string]$Base,
        [Parameter(Mandatory = $true)][string]$RunRoot,
        [bool]$ForceContinueOnError = $false
    )

    $scriptPath = $script:ResolvedCaptureScriptPath
    if (-not (Test-Path -LiteralPath $scriptPath)) {
        throw "RX leveler A/B capture script not found: $scriptPath"
    }

    $captureOutputRoot = New-Directory (Join-Path $RunRoot "rx-leveler-ab")
    $captureArgs = @{
        BaseUrl = $Base
        Samples = $Samples
        IntervalMs = $IntervalMs
        TimeoutSec = $TimeoutSec
        OutputRoot = $captureOutputRoot
        LabelPrefix = $LabelPrefix
        CandidateProfile = $CandidateProfile
        TuneStepHz = $TuneStepHz
        MinActiveAudioSamples = $MinActiveAudioSamples
        MinPassbandPeakSamples = $MinPassbandPeakSamples
    }

    if ($RequireActiveAudio) {
        $captureArgs["RequireActiveAudio"] = $true
    }
    if ($RequirePassbandEvidence) {
        $captureArgs["RequirePassbandEvidence"] = $true
    }
    if ($SkipCertificateCheck) {
        $captureArgs["SkipCertificateCheck"] = $true
    }
    if ($ContinueOnError -or $ForceContinueOnError) {
        $captureArgs["ContinueOnError"] = $true
    }

    $global:LASTEXITCODE = $null
    $output = & $scriptPath @captureArgs
    $captureSucceeded = $?
    $captureExitCode = $LASTEXITCODE
    if (-not $captureSucceeded -or ($null -ne $captureExitCode -and $captureExitCode -ne 0)) {
        $exitCodeText = if ($null -eq $captureExitCode) { "unknown" } else { [string]$captureExitCode }
        throw "capture-rx-leveler-ab failed with exit code $exitCodeText"
    }

    $jsonText = ($output | Out-String).Trim()
    if ([string]::IsNullOrWhiteSpace($jsonText)) {
        throw "capture-rx-leveler-ab did not emit a JSON summary."
    }

    return $jsonText | ConvertFrom-Json
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
if ($FrontendReadyTimeoutSec -lt 1) {
    throw "FrontendReadyTimeoutSec must be at least 1."
}
if ($FrequencyHz -le 0) {
    throw "FrequencyHz must be positive."
}
if ($P2ClientLocalPort -lt 1 -or $P2ClientLocalPort -gt 65535) {
    throw "P2ClientLocalPort must be between 1 and 65535."
}
if ($MinActiveAudioSamples -lt 1) {
    throw "MinActiveAudioSamples must be at least 1."
}
if ($ActiveAudioReadyTimeoutSec -lt 1) {
    throw "ActiveAudioReadyTimeoutSec must be at least 1."
}
if ($MinPassbandPeakSamples -lt 1) {
    throw "MinPassbandPeakSamples must be at least 1."
}
if ($PassbandReadyTimeoutSec -lt 1) {
    throw "PassbandReadyTimeoutSec must be at least 1."
}
if ($FrontendNearPassbandThresholdHz -lt 1) {
    throw "FrontendNearPassbandThresholdHz must be at least 1."
}
if ($CaptureAttempts -lt 1) {
    throw "CaptureAttempts must be at least 1."
}

if ($SkipCertificateCheck) {
    Enable-CertificateBypass
}

$repoRoot = Get-RepoRoot
$base = Normalize-BaseUrl $BaseUrl
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path (Join-Path $repoRoot "tmp") "wdsp-v2-live"
}

$script:ResolvedCaptureScriptPath = if ([string]::IsNullOrWhiteSpace($CaptureScriptPath)) {
    Join-Path $PSScriptRoot "capture-rx-leveler-ab.ps1"
}
else {
    (Resolve-Path -LiteralPath $CaptureScriptPath).Path
}

$plannedRunRoot = Join-Path $OutputRoot ("g2-frontend-rx-leveler-ab-" + (Get-Timestamp))
$summaryPath = Join-Path $plannedRunRoot "g2-frontend-rx-leveler-ab-summary.json"
$frontendProofPath = Join-Path $plannedRunRoot "frontend-proof.json"
$plan = [ordered]@{
    schemaVersion = 1
    tool = "capture-g2-frontend-rx-leveler-ab"
    mode = "plan-only"
    baseUrl = $base
    frontendUrl = $FrontendUrl
    radioEndpoint = $RadioEndpoint
    frequencyHz = $FrequencyHz
    useCurrentRadioState = [bool]$UseCurrentRadioState
    modeName = $Mode
    sampleRate = $SampleRate
    boardId = $BoardId
    samples = $Samples
    intervalMs = $IntervalMs
    frontendReadyTimeoutSec = $FrontendReadyTimeoutSec
    outputRoot = $plannedRunRoot
    summaryPath = $summaryPath
    frontendProofPath = $frontendProofPath
    captureScriptPath = $script:ResolvedCaptureScriptPath
    candidateProfile = $CandidateProfile
    currentProfile = "current"
    requireActiveAudio = [bool]$RequireActiveAudio
    minActiveAudioSamples = $MinActiveAudioSamples
    activeAudioReadyTimeoutSec = $ActiveAudioReadyTimeoutSec
    activeAudioThresholdDbfs = $ActiveAudioThresholdDbfs
    requirePassbandEvidence = [bool]$RequirePassbandEvidence
    minPassbandPeakSamples = $MinPassbandPeakSamples
    passbandReadyTimeoutSec = $PassbandReadyTimeoutSec
    frontendNearPassbandThresholdHz = $FrontendNearPassbandThresholdHz
    captureAttempts = $CaptureAttempts
    skipBrowserLaunch = [bool]$SkipBrowserLaunch
    tuneStepHz = $TuneStepHz
    p2ClientLocalPort = $P2ClientLocalPort
    skipP2SocketPreflight = [bool]$SkipP2SocketPreflight
    safety = @(
        "Requires updated backend RX leveler profile endpoint before A/B capture.",
        "Requires frontendSceneFresh and final RX audio fresh before starting profile A/B.",
        "Checks the P2 client UDP socket before browser/frontend setup and radio connect/tune unless explicitly skipped.",
        "Tunes exactly to FrequencyHz and does not run peak-hunt retune logic unless UseCurrentRadioState is set.",
        "UseCurrentRadioState reuses the operator-tuned VFO/mode and skips radio connect/tune posts.",
        "A/B capture resets the RX leveler profile to current in finally.",
        "No operator default DSP behavior is changed."
    )
}

if ($PlanOnly) {
    $plan | ConvertTo-Json -Depth 8
    exit 0
}

$runRoot = New-Directory $plannedRunRoot
$resolvedBrowserPath = ""
$browserProcess = $null
$frontendReady = $null
$activeAudioPreflight = $null
$passbandPreflight = $null
$frontendScenePreflight = $null
$profilePreflight = $null
$p2SocketPreflight = $null
$capture = $null
$connectResult = $null
$currentRadioState = $null
$targetFrequencyHz = $FrequencyHz
$targetMode = $Mode
$errorText = $null
$levelerResetAligned = $false
$levelerProfileMutationStarted = $false
$levelerProfileNoMutationAligned = $false
$captureAttemptRecords = New-Object System.Collections.Generic.List[object]

try {
    $p2SocketPreflight = Test-P2ClientSocketAvailability -LocalPort $P2ClientLocalPort -Skip:($SkipP2SocketPreflight -or $UseCurrentRadioState)
    if (-not [bool]$p2SocketPreflight.ready) {
        $ownerText = ($p2SocketPreflight.owners | ForEach-Object {
            $name = if ([string]::IsNullOrWhiteSpace([string]$_.processName)) { "process" } else { [string]$_.processName }
            "{0} pid={1} address={2}" -f $name, $_.owningProcess, $_.localAddress
        }) -join "; "
        throw "P2 client UDP port $P2ClientLocalPort is already in use: $ownerText. Stop or disconnect that session before running isolated G2 capture."
    }

    if (-not $SkipBrowserLaunch) {
        $resolvedBrowserPath = Resolve-BrowserPath -RequestedPath $BrowserPath
    }
    else {
        $resolvedBrowserPath = if ([string]::IsNullOrWhiteSpace($BrowserPath)) { "" } else { $BrowserPath }
    }

    if (-not $SkipBrowserLaunch) {
        $browserProcess = Start-FrontendBrowser -ExecutablePath $resolvedBrowserPath -Url $FrontendUrl -RunRoot $runRoot
    }

    Start-Sleep -Milliseconds ([Math]::Max(0, $SettleMs))

    $profilePreflight = Assert-LevelerProfileEndpoint -Base $base
    $levelerProfileNoMutationAligned = Test-LevelerProfileCurrentAligned -Profile $profilePreflight
    $frontendScenePreflight = Wait-FrontendScenePreflight -Base $base -TimeoutSeconds $FrontendReadyTimeoutSec
    if (-not [bool]$frontendScenePreflight.ready) {
        throw "Frontend scene did not become fresh before radio connect/tune within $FrontendReadyTimeoutSec seconds."
    }

    if ($UseCurrentRadioState) {
        $currentRadioState = Get-CurrentRadioState -Base $base
        $targetFrequencyHz = [long]$currentRadioState.frequencyHz
        $targetMode = [string]$currentRadioState.mode
        $connectResult = [pscustomobject][ordered]@{
            skipped = $true
            reason = "UseCurrentRadioState"
            adoptedFrequencyHz = $targetFrequencyHz
            adoptedMode = $targetMode
            radioVfoHz = $currentRadioState.radioVfoHz
            radioLoHz = $currentRadioState.radioLoHz
            radioCtunEnabled = $currentRadioState.radioCtunEnabled
        }
    }
    else {
        $connectResult = Invoke-JsonPost `
            -Url "$base/api/connect/p2" `
            -Body @{ endpoint = $RadioEndpoint; sampleRate = $SampleRate; boardId = $BoardId } `
            -AllowAlreadyConnected

        Invoke-JsonPost -Url "$base/api/radio/lo" -Body @{ hz = $targetFrequencyHz } | Out-Null
        Invoke-JsonPost -Url "$base/api/vfo" -Body @{ hz = $targetFrequencyHz } | Out-Null
        Invoke-JsonPost -Url "$base/api/mode" -Body @{ mode = $targetMode; receiver = 0 } | Out-Null
    }

    $frontendReady = Wait-FrontendEvidence -Base $base -TimeoutSeconds $FrontendReadyTimeoutSec
    $frontendProof = [ordered]@{
        schemaVersion = 1
        generatedUtc = (Get-Date).ToUniversalTime().ToString("o")
        ready = [bool]$frontendReady.ready
        baseUrl = $base
        frontendUrl = $FrontendUrl
        browserLaunched = -not [bool]$SkipBrowserLaunch
        browserPath = $resolvedBrowserPath
        browserProcessId = if ($null -eq $browserProcess) { $null } else { $browserProcess.Id }
        levelerProfilePreflight = $profilePreflight
        frontendScenePreflight = $frontendScenePreflight
        p2SocketPreflight = $p2SocketPreflight
        connect = $connectResult
        currentRadioState = $currentRadioState
        useCurrentRadioState = [bool]$UseCurrentRadioState
        target = [ordered]@{
            endpoint = $RadioEndpoint
            requestedFrequencyHz = $FrequencyHz
            frequencyHz = $targetFrequencyHz
            requestedMode = $Mode
            mode = $targetMode
            sampleRate = $SampleRate
            boardId = $BoardId
        }
        diagnostics = $frontendReady.diagnostics
        scene = $frontendReady.scene
        samples = @($frontendReady.samples)
    }
    Write-TextFile -Path $frontendProofPath -Value ($frontendProof | ConvertTo-Json -Depth 24)

    if (-not [bool]$frontendReady.ready) {
        throw "Frontend scene did not become benchmark-ready within $FrontendReadyTimeoutSec seconds. See $frontendProofPath."
    }

    if ($RequireActiveAudio) {
        $activeAudioPreflight = Wait-ActiveAudioEvidence `
            -Base $base `
            -TimeoutSeconds $ActiveAudioReadyTimeoutSec `
            -MinimumSamples $MinActiveAudioSamples `
            -ThresholdDbfs $ActiveAudioThresholdDbfs
        $frontendProof.activeAudioPreflight = $activeAudioPreflight
        Write-TextFile -Path $frontendProofPath -Value ($frontendProof | ConvertTo-Json -Depth 24)
        if (-not [bool]$activeAudioPreflight.ready) {
            throw "Active RX audio did not become ready before A/B capture within $ActiveAudioReadyTimeoutSec seconds. Required $MinActiveAudioSamples sample(s) at or above $ActiveAudioThresholdDbfs dBFS. See $frontendProofPath."
        }
    }

    if ($RequirePassbandEvidence) {
        $passbandPreflight = Wait-PassbandEvidence `
            -Base $base `
            -TimeoutSeconds $PassbandReadyTimeoutSec `
            -MinimumSamples $MinPassbandPeakSamples `
            -NearThresholdHz $FrontendNearPassbandThresholdHz `
            -TuneStepHz $TuneStepHz `
            -OperatorFrequencyHz $targetFrequencyHz
        $frontendProof.passbandPreflight = $passbandPreflight
        Write-TextFile -Path $frontendProofPath -Value ($frontendProof | ConvertTo-Json -Depth 24)
        if (-not [bool]$passbandPreflight.ready) {
            throw "Tuned passband frontend evidence did not become ready before A/B capture within $PassbandReadyTimeoutSec seconds. Required $MinPassbandPeakSamples passband-qualified sample(s). See $frontendProofPath."
        }
    }

    $levelerProfileMutationStarted = $true
    for ($attempt = 1; $attempt -le $CaptureAttempts; $attempt++) {
        $attemptError = ""
        try {
            $capture = Invoke-LevelerAbCapture `
                -Base $base `
                -RunRoot $runRoot `
                -ForceContinueOnError:($CaptureAttempts -gt 1)
        }
        catch {
            $attemptError = $_.Exception.Message
            $capture = $null
        }

        $levelerResetAligned = Test-LevelerResetAligned -Capture $capture
        $attemptRecord = New-LevelerAbAttemptRecord -Attempt $attempt -Capture $capture -ErrorText $attemptError
        $captureAttemptRecords.Add([pscustomobject]$attemptRecord) | Out-Null

        if ($null -eq $capture) {
            throw "RX leveler A/B capture attempt $attempt did not produce a summary: $attemptError"
        }

        if (-not $levelerResetAligned) {
            throw "RX leveler A/B capture attempt $attempt did not reset the active profile to current. See $($capture.summaryPath)."
        }

        if ($null -eq $capture.PSObject.Properties["ok"] -or [bool]$capture.ok) {
            break
        }

        $captureSummaryPath = if ($null -eq $capture.summaryPath) { "unknown summary path" } else { [string]$capture.summaryPath }
        $captureError = if ($null -eq $capture.error) { "no delegated error detail" } else { [string]$capture.error }
        $recoverable = Test-RecoverableLevelerAbEvidenceFailure -Capture $capture
        if (-not $recoverable -or $attempt -ge $CaptureAttempts) {
            throw "RX leveler A/B capture did not complete successfully after $attempt attempt(s): $captureError. See $captureSummaryPath."
        }

        Start-Sleep -Milliseconds ([Math]::Max(250, $IntervalMs))
    }
}
catch {
    $errorText = $_.Exception.Message
    if ($null -eq $frontendReady) {
        $frontendProof = [ordered]@{
            schemaVersion = 1
            generatedUtc = (Get-Date).ToUniversalTime().ToString("o")
            ready = $false
            baseUrl = $base
            frontendUrl = $FrontendUrl
            browserLaunched = -not [bool]$SkipBrowserLaunch
            browserPath = $resolvedBrowserPath
            browserProcessId = if ($null -eq $browserProcess) { $null } else { $browserProcess.Id }
            levelerProfilePreflight = $profilePreflight
            frontendScenePreflight = $frontendScenePreflight
            p2SocketPreflight = $p2SocketPreflight
            error = $errorText
        }
        Write-TextFile -Path $frontendProofPath -Value ($frontendProof | ConvertTo-Json -Depth 12)
    }
}
finally {
    if ($null -ne $browserProcess -and -not $LeaveBrowserOpen) {
        try {
            Stop-Process -Id $browserProcess.Id -Force -ErrorAction SilentlyContinue
        }
        catch {
        }
    }
}

$levelerProfileAlignedForResult = if ($levelerProfileMutationStarted) {
    $levelerResetAligned
}
else {
    $levelerProfileNoMutationAligned
}

$result = [ordered]@{
    schemaVersion = 1
    tool = "capture-g2-frontend-rx-leveler-ab"
    generatedUtc = (Get-Date).ToUniversalTime().ToString("o")
    ok = [string]::IsNullOrWhiteSpace($errorText) -and
        $null -ne $capture -and
        ($null -eq $capture.PSObject.Properties["ok"] -or [bool]$capture.ok) -and
        $null -ne $frontendReady -and
        [bool]$frontendReady.ready -and
        ((-not $RequirePassbandEvidence) -or ($null -ne $passbandPreflight -and [bool]$passbandPreflight.ready)) -and
        $levelerResetAligned
    error = $errorText
    baseUrl = $base
    frontendUrl = $FrontendUrl
    radioEndpoint = $RadioEndpoint
    requestedFrequencyHz = $FrequencyHz
    frequencyHz = $targetFrequencyHz
    useCurrentRadioState = [bool]$UseCurrentRadioState
    currentRadioState = $currentRadioState
    requestedMode = $Mode
    modeName = $targetMode
    sampleRate = $SampleRate
    boardId = $BoardId
    outputRoot = $runRoot
    summaryPath = $summaryPath
    frontendProofPath = $frontendProofPath
    browserLaunched = -not [bool]$SkipBrowserLaunch
    browserPath = $resolvedBrowserPath
    browserLeftOpen = [bool]$LeaveBrowserOpen
    levelerProfilePreflight = $profilePreflight
    frontendScenePreflightReady = if ($null -eq $frontendScenePreflight) { $false } else { [bool]$frontendScenePreflight.ready }
    p2SocketPreflightReady = if ($null -eq $p2SocketPreflight) { $false } else { [bool]$p2SocketPreflight.ready }
    p2SocketPreflight = $p2SocketPreflight
    frontendReady = if ($null -eq $frontendReady) { $false } else { [bool]$frontendReady.ready }
    activeAudioPreflight = $activeAudioPreflight
    passbandPreflight = $passbandPreflight
    levelerProfileMutationStarted = $levelerProfileMutationStarted
    levelerProfileNoMutationAligned = if ($levelerProfileMutationStarted) { $null } else { $levelerProfileNoMutationAligned }
    levelerProfileResetAligned = $levelerProfileAlignedForResult
    captureAttemptsRequested = $CaptureAttempts
    captureAttemptCount = $captureAttemptRecords.Count
    captureAttempts = @($captureAttemptRecords.ToArray())
    rxLevelerAbSummaryPath = if ($null -eq $capture) { $null } else { [string]$capture.summaryPath }
    rxLevelerAbOutputRoot = if ($null -eq $capture) { $null } else { [string]$capture.outputRoot }
    current = if ($null -eq $capture) { $null } else { $capture.current }
    candidate = if ($null -eq $capture) { $null } else { $capture.candidate }
    resetToCurrent = if ($null -eq $capture) { $null } else { $capture.resetToCurrent }
    activeAudioEvidence = if ($null -eq $capture) { $null } else { $capture.activeAudioEvidence }
    passbandEvidence = if ($null -eq $capture) { $null } else { $capture.passbandEvidence }
    requireActiveAudio = [bool]$RequireActiveAudio
    minActiveAudioSamples = $MinActiveAudioSamples
    activeAudioReadyTimeoutSec = $ActiveAudioReadyTimeoutSec
    activeAudioThresholdDbfs = $ActiveAudioThresholdDbfs
    requirePassbandEvidence = [bool]$RequirePassbandEvidence
    minPassbandPeakSamples = $MinPassbandPeakSamples
    passbandReadyTimeoutSec = $PassbandReadyTimeoutSec
    frontendNearPassbandThresholdHz = $FrontendNearPassbandThresholdHz
    recommendation = "Use this wrapper for repeatable frontend-backed G2 RX leveler A/B evidence; no default DSP behavior is changed."
}

$json = $result | ConvertTo-Json -Depth 24
Write-TextFile -Path $summaryPath -Value $json
$json

if (-not $result.ok -and -not $ContinueOnError) {
    exit 1
}
