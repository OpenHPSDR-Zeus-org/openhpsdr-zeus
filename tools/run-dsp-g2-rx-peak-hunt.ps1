param(
    [string]$BaseUrl = "http://localhost:6060",

    [switch]$AutoDiscoverBaseUrl,

    [int]$SamplesPerWindow = 24,

    [int]$IntervalMs = 250,

    [int]$WindowsPerPeak = 1,

    [int]$PassCount = 1,

    [int]$PassDelaySec = 0,

    [string[]]$CandidateFrequencyHz = @(),

    [string[]]$CandidateMHz = @(),

    [int]$OperatorTrendMaxCandidates = 4,

    [switch]$AutoPhoneCluster,

    [string]$AutoPhoneClusterSearchRoot = "",

    [int]$AutoPhoneClusterMaxCandidates = 12,

    [int]$AutoPhoneClusterLookbackHours = 12,

    [int]$AutoPhoneClusterMinSpeechSamples = 1,

    [long]$AutoPhoneClusterBandLowHz = 14150000,

    [long]$AutoPhoneClusterBandHighHz = 14350000,

    [int]$MaxPeaks = 6,

    [int]$PeakMergeHz = 1000,

    [int]$TuneStepHz = 1000,

    [long]$PeakRetuneLowHz = 0,

    [long]$PeakRetuneHighHz = 0,

    [int]$PeakRetunePaddingHz = 3000,

    [double]$MinPeakSnrDb = 8.0,

    [int]$SettleMs = 3000,

    [int]$TimeoutSec = 5,

    [string]$OutputRoot = "",

    [string]$ReportPath = "",

    [string]$Label = "",

    [string]$ComparisonId = "nr5-spnr",

    [string]$WatchScriptPath = "",

    [string]$Mode = "",

    [switch]$AllowRetune,

    [switch]$SkipCurrentVfo,

    [switch]$StopOnReady,

    [switch]$PlanOnly,

    [switch]$JsonOnly,

    [switch]$SkipCertificateCheck,

    [switch]$ContinueOnError
)

$ErrorActionPreference = "Stop"
$artifactPathSoftLimit = 240

function Get-RepoRoot {
    return (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
}

function Normalize-BaseUrl {
    param([Parameter(Mandatory = $true)][string]$Url)
    return $Url.TrimEnd("/")
}

function Test-AutoBaseUrlRequest {
    param([string]$Url)

    return [string]::Equals(([string]$Url).Trim(), "auto", [StringComparison]::OrdinalIgnoreCase)
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

function Test-ArtifactPathTooLong {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $false
    }

    return $Path.Length -gt $artifactPathSoftLimit
}

function Get-BundleRootFromArtifactsPath {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return ""
    }

    try {
        $fullPath = [System.IO.Path]::GetFullPath($Path)
        $normalized = $fullPath -replace "/", "\"
        $needle = "\artifacts\"
        $index = $normalized.LastIndexOf($needle, [StringComparison]::OrdinalIgnoreCase)
        if ($index -gt 0) {
            return $normalized.Substring(0, $index)
        }
    }
    catch {
        return ""
    }

    return ""
}

function ConvertTo-PortableBundlePath {
    param(
        [string]$Root,
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Root) -or [string]::IsNullOrWhiteSpace($Path)) {
        return $Path
    }

    try {
        $rootFull = [System.IO.Path]::GetFullPath($Root)
        $pathFull = [System.IO.Path]::GetFullPath($Path)
        $trimChars = [char[]]@([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
        $rootComparable = $rootFull.TrimEnd($trimChars)
        $pathComparable = $pathFull.TrimEnd($trimChars)
        if ([string]::Equals($rootComparable, $pathComparable, [StringComparison]::OrdinalIgnoreCase)) {
            return "."
        }

        if (-not $rootFull.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
            $rootFull = $rootFull + [System.IO.Path]::DirectorySeparatorChar
        }

        if (-not $pathFull.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) {
            return $Path
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

    if ($value -is [array]) {
        return @($value)
    }

    return @($value)
}

function Get-IntValue {
    param($Value)

    if ($null -eq $Value) {
        return 0
    }

    $result = 0
    if ([int]::TryParse(([string]$Value), [ref]$result)) {
        return $result
    }

    return 0
}

function Get-NullableLongValue {
    param($Value)

    if ($null -eq $Value) {
        return $null
    }

    $result = 0L
    if ([long]::TryParse(([string]$Value), [ref]$result)) {
        return $result
    }

    return $null
}

function Get-NullableDoubleValue {
    param($Value)

    if ($null -eq $Value) {
        return $null
    }

    $result = 0.0
    if ([double]::TryParse(([string]$Value), [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$result)) {
        return $result
    }

    return $null
}

function Normalize-TuneStepHz {
    param($Value)

    $step = 0
    if ($null -ne $Value) {
        [int]::TryParse(([string]$Value), [ref]$step) | Out-Null
    }

    if ($step -le 0) {
        return 1000
    }

    if ($step -lt 10) {
        return 10
    }

    if ($step -gt 100000) {
        return 100000
    }

    return $step
}

function Quantize-HzToStep {
    param(
        [double]$Hz,
        [int]$StepHz
    )

    $step = Normalize-TuneStepHz $StepHz
    return [long]([Math]::Round($Hz / [double]$step) * [double]$step)
}

function Get-FrontendPeakRetuneTargetOffsetHz {
    param(
        [string]$Mode = "",
        $FilterLowHz = $null,
        $FilterHighHz = $null
    )

    $low = Get-NullableDoubleValue $FilterLowHz
    $high = Get-NullableDoubleValue $FilterHighHz
    if ($null -ne $low -and $null -ne $high -and [Math]::Abs([double]$high - [double]$low) -gt 1.0) {
        return [Math]::Round(([double]$low + [double]$high) / 2.0, 1)
    }

    $normalizedMode = ([string]$Mode).Trim().ToUpperInvariant()
    switch ($normalizedMode) {
        { $_ -in @("LSB", "DIGL") } { return -1500.0 }
        { $_ -in @("USB", "DIGU") } { return 1500.0 }
        default { return 0.0 }
    }
}

function Normalize-PeakRetunePaddingHz {
    param($Value)

    $padding = 0
    if ($null -ne $Value) {
        [int]::TryParse(([string]$Value), [ref]$padding) | Out-Null
    }

    if ($padding -lt 0) {
        return 0
    }

    if ($padding -gt 100000) {
        return 100000
    }

    return $padding
}

function Get-PeakRetuneSpan {
    param(
        [object[]]$SeedCandidates,
        [long]$ExplicitLowHz,
        [long]$ExplicitHighHz,
        [int]$PaddingHz,
        [long]$FallbackLowHz = 0,
        [long]$FallbackHighHz = 0,
        [bool]$UseFallback = $false
    )

    $padding = Normalize-PeakRetunePaddingHz $PaddingHz
    $low = $null
    $high = $null
    $source = "unrestricted"

    if ($ExplicitLowHz -gt 0 -and $ExplicitHighHz -gt 0) {
        $low = [Math]::Min([long]$ExplicitLowHz, [long]$ExplicitHighHz)
        $high = [Math]::Max([long]$ExplicitLowHz, [long]$ExplicitHighHz)
        $source = "explicit"
    }
    else {
        $frequencies = New-Object System.Collections.Generic.List[long]
        foreach ($candidate in @($SeedCandidates)) {
            $frequencyHz = Get-NullableLongValue (Get-JsonValue $candidate "frequencyHz")
            if ($null -ne $frequencyHz -and $frequencyHz -gt 0) {
                $frequencies.Add([long]$frequencyHz) | Out-Null
            }
        }

        if ($frequencies.Count -gt 0) {
            $sorted = @($frequencies.ToArray() | Sort-Object)
            $low = [Math]::Max(1L, [long]$sorted[0] - [long]$padding)
            $high = [long]$sorted[$sorted.Count - 1] + [long]$padding
            $source = "candidate-span"
        }
        elseif ($UseFallback -and $FallbackLowHz -gt 0 -and $FallbackHighHz -gt 0) {
            $low = [Math]::Min([long]$FallbackLowHz, [long]$FallbackHighHz)
            $high = [Math]::Max([long]$FallbackLowHz, [long]$FallbackHighHz)
            $source = "fallback-band"
        }
    }

    return [pscustomobject][ordered]@{
        bounded = ($null -ne $low -and $null -ne $high)
        lowHz = $low
        highHz = $high
        paddingHz = $padding
        source = $source
    }
}

function Get-TrimmedStringValue {
    param($Value)

    if ($null -eq $Value) {
        return $null
    }

    $text = ([string]$Value).Trim()
    if ([string]::IsNullOrWhiteSpace($text)) {
        return $null
    }

    return $text
}

function Format-NullableDbText {
    param($Value)

    $number = Get-NullableDoubleValue $Value
    if ($null -eq $number) {
        return "n/a"
    }

    return ("{0:0.###} dB" -f [double]$number)
}

function Test-Truthy {
    param($Value)

    if ($null -eq $Value) {
        return $false
    }

    if ($Value -is [bool]) {
        return [bool]$Value
    }

    $text = ([string]$Value).Trim()
    return [string]::Equals($text, "true", [StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($text, "1", [StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($text, "yes", [StringComparison]::OrdinalIgnoreCase)
}

function Invoke-JsonGet {
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [int]$RequestTimeoutSec = 5,
        [switch]$SkipCertificate
    )

    $requestArgs = @{
        Uri = $Url
        TimeoutSec = $RequestTimeoutSec
        UseBasicParsing = $true
    }
    if ($SkipCertificate -and (Get-Command Invoke-WebRequest).Parameters.ContainsKey("SkipCertificateCheck")) {
        $requestArgs["SkipCertificateCheck"] = $true
    }

    $response = Invoke-WebRequest @requestArgs
    return $response.Content | ConvertFrom-Json
}

function Invoke-JsonPost {
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [Parameter(Mandatory = $true)]$Body,
        [int]$RequestTimeoutSec = 5,
        [switch]$SkipCertificate
    )

    $requestArgs = @{
        Uri = $Url
        Method = "Post"
        ContentType = "application/json"
        Body = ($Body | ConvertTo-Json -Depth 16)
        TimeoutSec = $RequestTimeoutSec
        UseBasicParsing = $true
    }
    if ($SkipCertificate -and (Get-Command Invoke-WebRequest).Parameters.ContainsKey("SkipCertificateCheck")) {
        $requestArgs["SkipCertificateCheck"] = $true
    }

    $response = Invoke-WebRequest @requestArgs
    if ([string]::IsNullOrWhiteSpace($response.Content)) {
        return $null
    }

    return $response.Content | ConvertFrom-Json
}

function Test-RadioStateDisconnected {
    param($State)

    $status = [string](Get-JsonValue $State "status")
    return [string]::Equals($status, "Disconnected", [StringComparison]::OrdinalIgnoreCase)
}

function Invoke-P2Reconnect {
    param(
        [Parameter(Mandatory = $true)][string]$BaseUrl,
        [Parameter(Mandatory = $true)][string]$Endpoint,
        [int]$SampleRate = 192000,
        [int]$BoardId = 10,
        [int]$RequestTimeoutSec = 5,
        [switch]$SkipCertificate
    )

    Invoke-JsonPost `
        -Url "$BaseUrl/api/connect/p2" `
        -Body @{ endpoint = $Endpoint; sampleRate = $SampleRate; boardId = $BoardId } `
        -RequestTimeoutSec $RequestTimeoutSec `
        -SkipCertificate:$SkipCertificate | Out-Null
}

function Restore-OriginalTuning {
    param(
        [Parameter(Mandatory = $true)][string]$BaseUrl,
        [Parameter(Mandatory = $true)][long]$OriginalVfo,
        $OriginalRadioLo = $null,
        [string]$OriginalMode = "",
        [string]$OriginalEndpoint = "",
        [int]$OriginalConnectSampleRate = 192000,
        [int]$OriginalBoardId = 10,
        [int]$RequestTimeoutSec = 5,
        [int]$SettleMs = 1000,
        [int]$MaxAttempts = 2,
        [switch]$SkipCertificate
    )

    $lastState = $null
    $lastError = $null
    $reconnectAttempted = $false
    $reconnectSucceeded = $false
    $reconnectError = $null
    $loRequired = ($null -ne $OriginalRadioLo -and [long]$OriginalRadioLo -gt 0)
    $modeRequired = -not [string]::IsNullOrWhiteSpace($OriginalMode)
    $sleepMs = [Math]::Max(0, $SettleMs)
    $canReconnect = -not [string]::IsNullOrWhiteSpace($OriginalEndpoint)

    for ($attempt = 1; $attempt -le [Math]::Max(1, $MaxAttempts); $attempt++) {
        try {
            if ($canReconnect) {
                try {
                    $preRestoreState = Invoke-JsonGet -Url "$BaseUrl/api/state" -RequestTimeoutSec $RequestTimeoutSec -SkipCertificate:$SkipCertificate
                    if (Test-RadioStateDisconnected $preRestoreState) {
                        $reconnectAttempted = $true
                        Invoke-P2Reconnect `
                            -BaseUrl $BaseUrl `
                            -Endpoint $OriginalEndpoint `
                            -SampleRate $OriginalConnectSampleRate `
                            -BoardId $OriginalBoardId `
                            -RequestTimeoutSec $RequestTimeoutSec `
                            -SkipCertificate:$SkipCertificate
                        $reconnectSucceeded = $true
                        Start-Sleep -Milliseconds $sleepMs
                    }
                }
                catch {
                    $reconnectError = $_.Exception.Message
                    $lastError = $reconnectError
                }
            }

            if ($loRequired) {
                Invoke-JsonPost -Url "$BaseUrl/api/radio/lo" -Body @{ hz = [long]$OriginalRadioLo } -RequestTimeoutSec $RequestTimeoutSec -SkipCertificate:$SkipCertificate | Out-Null
            }
            Invoke-JsonPost -Url "$BaseUrl/api/vfo" -Body @{ hz = $OriginalVfo } -RequestTimeoutSec $RequestTimeoutSec -SkipCertificate:$SkipCertificate | Out-Null
            if ($modeRequired) {
                Invoke-JsonPost -Url "$BaseUrl/api/mode" -Body @{ mode = $OriginalMode; receiver = 0 } -RequestTimeoutSec $RequestTimeoutSec -SkipCertificate:$SkipCertificate | Out-Null
            }
            Start-Sleep -Milliseconds $sleepMs

            $lastState = Invoke-JsonGet -Url "$BaseUrl/api/state" -RequestTimeoutSec $RequestTimeoutSec -SkipCertificate:$SkipCertificate
            $restoredVfoHz = Get-NullableLongValue (Get-JsonValue $lastState "vfoHz")
            $restoredRadioLoHz = Get-NullableLongValue (Get-JsonValue $lastState "radioLoHz")
            $restoredMode = [string](Get-JsonValue $lastState "mode")
            $vfoRestored = ($null -ne $restoredVfoHz -and [long]$restoredVfoHz -eq $OriginalVfo)
            $radioLoRestored = (-not $loRequired) -or ($null -ne $restoredRadioLoHz -and [long]$restoredRadioLoHz -eq [long]$OriginalRadioLo)
            $modeRestored = (-not $modeRequired) -or [string]::Equals($restoredMode, $OriginalMode, [StringComparison]::OrdinalIgnoreCase)

            if ($vfoRestored -and $radioLoRestored -and $modeRestored) {
                return [pscustomobject][ordered]@{
                    ok = $true
                    attempts = $attempt
                    state = $lastState
                    error = $null
                    reconnectAttempted = $reconnectAttempted
                    reconnectSucceeded = $reconnectSucceeded
                    reconnectError = $reconnectError
                }
            }
        }
        catch {
            $lastError = $_.Exception.Message
        }
    }

    return [pscustomobject][ordered]@{
        ok = $false
        attempts = [Math]::Max(1, $MaxAttempts)
        state = $lastState
        error = $lastError
        reconnectAttempted = $reconnectAttempted
        reconnectSucceeded = $reconnectSucceeded
        reconnectError = $reconnectError
    }
}

function Add-UniqueCandidateUrl {
    param(
        [System.Collections.Generic.List[string]]$Urls,
        [string]$Url
    )

    if ([string]::IsNullOrWhiteSpace($Url)) {
        return
    }

    $normalized = Normalize-BaseUrl $Url
    foreach ($existing in $Urls) {
        if ([string]::Equals($existing, $normalized, [StringComparison]::OrdinalIgnoreCase)) {
            return
        }
    }

    $Urls.Add($normalized) | Out-Null
}

function Get-LocalBackendCandidateUrls {
    $urls = New-Object System.Collections.Generic.List[string]
    $processPorts = New-Object System.Collections.Generic.List[int]

    try {
        $connections = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue)
        foreach ($connection in $connections) {
            $port = [int]$connection.LocalPort
            if ($port -le 0) {
                continue
            }

            $process = $null
            try {
                $process = Get-Process -Id $connection.OwningProcess -ErrorAction Stop
            }
            catch {
                continue
            }

            if ($null -eq $process -or $process.ProcessName -notlike "OpenhpsdrZeus*") {
                continue
            }

            if (-not $processPorts.Contains($port)) {
                $processPorts.Add($port) | Out-Null
            }
        }
    }
    catch {
    }

    foreach ($port in @($processPorts | Sort-Object)) {
        if ($port -eq 6443) {
            continue
        }

        Add-UniqueCandidateUrl -Urls $urls -Url ("http://127.0.0.1:{0}" -f $port)
    }

    foreach ($port in @(6060, 6061, 6080, 6081, 6090)) {
        Add-UniqueCandidateUrl -Urls $urls -Url ("http://127.0.0.1:{0}" -f $port)
    }

    if ($processPorts.Contains(6443)) {
        Add-UniqueCandidateUrl -Urls $urls -Url "https://127.0.0.1:6443"
    }

    return @($urls.ToArray())
}

function Resolve-ZeusBaseUrl {
    param(
        [Parameter(Mandatory = $true)][string]$RequestedUrl,
        [int]$RequestTimeoutSec = 5,
        [switch]$SkipCertificate,
        [switch]$AutoDiscover
    )

    $autoRequested = $AutoDiscover -or (Test-AutoBaseUrlRequest $RequestedUrl)
    $probeResults = New-Object System.Collections.Generic.List[object]
    if (-not $autoRequested) {
        return [pscustomobject][ordered]@{
            requestedBaseUrl = $RequestedUrl
            baseUrl = Normalize-BaseUrl $RequestedUrl
            autoDiscoverRequested = $false
            autoDiscovered = $false
            autoDiscoverError = ""
            probeResults = @()
        }
    }

    $candidateUrls = @(Get-LocalBackendCandidateUrls)
    foreach ($candidateUrl in $candidateUrls) {
        $probe = [ordered]@{
            baseUrl = $candidateUrl
            ok = $false
            connectionStatus = ""
            vfoHz = $null
            error = ""
        }

        try {
            $diagnostics = Invoke-JsonGet -Url "$candidateUrl/api/radio/diagnostics" -RequestTimeoutSec ([Math]::Max(1, [Math]::Min(2, $RequestTimeoutSec))) -SkipCertificate:$SkipCertificate
            $probe["connectionStatus"] = [string](Get-JsonValue $diagnostics "connectionStatus")
            $probe["vfoHz"] = Get-NullableLongValue (Get-JsonValue $diagnostics "vfoHz")
            if ($null -ne $probe["vfoHz"] -and [long]$probe["vfoHz"] -gt 0) {
                $probe["ok"] = $true
                $probeResults.Add([pscustomobject]$probe) | Out-Null
                return [pscustomobject][ordered]@{
                    requestedBaseUrl = $RequestedUrl
                    baseUrl = $candidateUrl
                    autoDiscoverRequested = $true
                    autoDiscovered = $true
                    autoDiscoverError = ""
                    probeResults = @($probeResults.ToArray())
                }
            }

            $probe["error"] = "Probe returned diagnostics without a valid vfoHz."
        }
        catch {
            $probe["error"] = $_.Exception.Message
        }

        $probeResults.Add([pscustomobject]$probe) | Out-Null
    }

    $fallback = "http://127.0.0.1:6060"
    return [pscustomobject][ordered]@{
        requestedBaseUrl = $RequestedUrl
        baseUrl = $fallback
        autoDiscoverRequested = $true
        autoDiscovered = $false
        autoDiscoverError = "No local OpenhpsdrZeus backend answered /api/radio/diagnostics with a valid vfoHz."
        probeResults = @($probeResults.ToArray())
    }
}

function ConvertTo-PeakCandidate {
    param(
        $Peak,
        [int]$Rank,
        [string]$Source,
        [long]$OriginalVfo = 0,
        [int]$StepHz = 1000,
        [double]$RetuneTargetOffsetHz = 0.0
    )

    $exactFrequencyHz = Get-NullableLongValue (Get-JsonValue $Peak "frequencyHz")
    if ($null -eq $exactFrequencyHz -or $exactFrequencyHz -le 0) {
        return $null
    }

    $effectiveStepHz = Normalize-TuneStepHz $StepHz
    $exactRetuneVfoHz = [long][Math]::Round([double]$exactFrequencyHz - [double]$RetuneTargetOffsetHz)
    if ($exactRetuneVfoHz -le 0) {
        $exactRetuneVfoHz = [long]$exactFrequencyHz
    }

    $frequencyHz = Quantize-HzToStep -Hz ([double]$exactRetuneVfoHz) -StepHz $effectiveStepHz
    if ($frequencyHz -le 0) {
        $frequencyHz = [long]$exactRetuneVfoHz
    }

    $exactOffsetHz = Get-NullableLongValue (Get-JsonValue $Peak "offsetHz")
    if ($OriginalVfo -gt 0) {
        $exactOffsetHz = [long]$exactFrequencyHz - [long]$OriginalVfo
    }

    $offsetHz = $null
    if ($OriginalVfo -gt 0) {
        $offsetHz = [long]$frequencyHz - [long]$OriginalVfo
    }
    elseif ($null -ne $exactOffsetHz) {
        $offsetHz = [long]$exactOffsetHz + ([long]$frequencyHz - [long]$exactFrequencyHz)
    }

    return [ordered]@{
        rank = $Rank
        source = $Source
        frequencyHz = $frequencyHz
        exactFrequencyHz = [long]$exactFrequencyHz
        offsetHz = $offsetHz
        exactOffsetHz = $exactOffsetHz
        tuningStepHz = $effectiveStepHz
        tuneSnapDeltaHz = [long]$frequencyHz - [long]$exactRetuneVfoHz
        retuneTargetOffsetHz = [Math]::Round([double]$RetuneTargetOffsetHz, 1)
        exactRetuneVfoHz = [long]$exactRetuneVfoHz
        peakToRetunedVfoOffsetHz = [long]$exactFrequencyHz - [long]$frequencyHz
        retuneReason = if ([Math]::Abs([double]$RetuneTargetOffsetHz) -gt 0.1) { "retune-to-center-frontend-peak" } else { "retune-to-exact-frontend-peak" }
        snrDb = Get-NullableDoubleValue (Get-JsonValue $Peak "snrDb")
        dbfs = Get-NullableDoubleValue (Get-JsonValue $Peak "dbfs")
        confidence = Get-NullableDoubleValue (Get-JsonValue $Peak "confidence")
        coherent = Test-Truthy (Get-JsonValue $Peak "coherent")
    }
}

function Copy-RejectedPeakCandidate {
    param(
        $Candidate,
        [string]$Reason,
        [string]$Detail = "",
        $MergedWithFrequencyHz = $null,
        $RetuneLowHz = $null,
        $RetuneHighHz = $null,
        [string]$RetuneSpanSource = "",
        $MinimumSnrDb = $null,
        $PeakMergeHz = $null
    )

    if ($null -eq $Candidate) {
        return $null
    }

    return [pscustomobject][ordered]@{
        rank = Get-IntValue (Get-JsonValue $Candidate "rank")
        source = [string](Get-JsonValue $Candidate "source")
        frequencyHz = Get-NullableLongValue (Get-JsonValue $Candidate "frequencyHz")
        exactFrequencyHz = Get-NullableLongValue (Get-JsonValue $Candidate "exactFrequencyHz")
        offsetHz = Get-NullableLongValue (Get-JsonValue $Candidate "offsetHz")
        exactOffsetHz = Get-NullableLongValue (Get-JsonValue $Candidate "exactOffsetHz")
        tuningStepHz = Get-IntValue (Get-JsonValue $Candidate "tuningStepHz")
        tuneSnapDeltaHz = Get-NullableLongValue (Get-JsonValue $Candidate "tuneSnapDeltaHz")
        retuneTargetOffsetHz = Get-NullableDoubleValue (Get-JsonValue $Candidate "retuneTargetOffsetHz")
        exactRetuneVfoHz = Get-NullableLongValue (Get-JsonValue $Candidate "exactRetuneVfoHz")
        peakToRetunedVfoOffsetHz = Get-NullableLongValue (Get-JsonValue $Candidate "peakToRetunedVfoOffsetHz")
        retuneReason = [string](Get-JsonValue $Candidate "retuneReason")
        snrDb = Get-NullableDoubleValue (Get-JsonValue $Candidate "snrDb")
        dbfs = Get-NullableDoubleValue (Get-JsonValue $Candidate "dbfs")
        confidence = Get-NullableDoubleValue (Get-JsonValue $Candidate "confidence")
        coherent = Test-Truthy (Get-JsonValue $Candidate "coherent")
        rejectionReason = $Reason
        rejectionDetail = $Detail
        retuneLowHz = Get-NullableLongValue $RetuneLowHz
        retuneHighHz = Get-NullableLongValue $RetuneHighHz
        retuneSpanSource = $RetuneSpanSource
        minimumSnrDb = Get-NullableDoubleValue $MinimumSnrDb
        peakMergeHz = Get-IntValue $PeakMergeHz
        mergedWithFrequencyHz = Get-NullableLongValue $MergedWithFrequencyHz
        mergeDeltaHz = if ($null -ne $MergedWithFrequencyHz) { [Math]::Abs([long](Get-JsonValue $Candidate "frequencyHz") - [long]$MergedWithFrequencyHz) } else { $null }
    }
}

function Select-PeakCandidates {
    param(
        [object[]]$Peaks,
        [int]$Limit,
        [int]$MergeHz,
        [double]$MinimumSnrDb,
        [long]$OriginalVfo = 0,
        [int]$StepHz = 1000,
        $RetuneLowHz = $null,
        $RetuneHighHz = $null,
        [string]$RetuneSpanSource = "",
        [double]$RetuneTargetOffsetHz = 0.0,
        [System.Collections.Generic.List[object]]$RejectedCandidates = $null
    )

    $selected = New-Object System.Collections.Generic.List[object]
    if ($Limit -le 0) {
        return @()
    }

    $rank = 0
    foreach ($peak in @($Peaks | Sort-Object @{ Expression = { Get-NullableDoubleValue (Get-JsonValue $_ "snrDb") }; Descending = $true })) {
        $rank++
        $candidate = ConvertTo-PeakCandidate -Peak $peak -Rank $rank -Source "frontend-top-peak" -OriginalVfo $OriginalVfo -StepHz $StepHz -RetuneTargetOffsetHz $RetuneTargetOffsetHz
        if ($null -eq $candidate) {
            continue
        }

        if ($null -ne $RetuneLowHz -and [long]$candidate.frequencyHz -lt [long]$RetuneLowHz) {
            if ($null -ne $RejectedCandidates) {
                $RejectedCandidates.Add((Copy-RejectedPeakCandidate -Candidate $candidate -Reason "outside-retune-span-low" -Detail ("below $RetuneLowHz Hz") -RetuneLowHz $RetuneLowHz -RetuneHighHz $RetuneHighHz -RetuneSpanSource $RetuneSpanSource -MinimumSnrDb $MinimumSnrDb -PeakMergeHz $MergeHz)) | Out-Null
            }
            continue
        }

        if ($null -ne $RetuneHighHz -and [long]$candidate.frequencyHz -gt [long]$RetuneHighHz) {
            if ($null -ne $RejectedCandidates) {
                $RejectedCandidates.Add((Copy-RejectedPeakCandidate -Candidate $candidate -Reason "outside-retune-span-high" -Detail ("above $RetuneHighHz Hz") -RetuneLowHz $RetuneLowHz -RetuneHighHz $RetuneHighHz -RetuneSpanSource $RetuneSpanSource -MinimumSnrDb $MinimumSnrDb -PeakMergeHz $MergeHz)) | Out-Null
            }
            continue
        }

        $snr = Get-NullableDoubleValue $candidate.snrDb
        if ($null -ne $snr -and $snr -lt $MinimumSnrDb) {
            if ($null -ne $RejectedCandidates) {
                $RejectedCandidates.Add((Copy-RejectedPeakCandidate -Candidate $candidate -Reason "below-min-snr" -Detail ("snr $snr dB below $MinimumSnrDb dB") -RetuneLowHz $RetuneLowHz -RetuneHighHz $RetuneHighHz -RetuneSpanSource $RetuneSpanSource -MinimumSnrDb $MinimumSnrDb -PeakMergeHz $MergeHz)) | Out-Null
            }
            continue
        }

        if ($selected.Count -ge $Limit) {
            if ($null -ne $RejectedCandidates) {
                $RejectedCandidates.Add((Copy-RejectedPeakCandidate -Candidate $candidate -Reason "limit-reached" -Detail ("max peaks $Limit already selected") -RetuneLowHz $RetuneLowHz -RetuneHighHz $RetuneHighHz -RetuneSpanSource $RetuneSpanSource -MinimumSnrDb $MinimumSnrDb -PeakMergeHz $MergeHz)) | Out-Null
            }
            continue
        }

        $tooClose = $false
        $mergedWithFrequencyHz = $null
        foreach ($existing in $selected) {
            if ([Math]::Abs([long]$candidate.frequencyHz - [long]$existing.frequencyHz) -le $MergeHz) {
                $tooClose = $true
                $mergedWithFrequencyHz = [long]$existing.frequencyHz
                break
            }
        }

        if ($tooClose) {
            if ($null -ne $RejectedCandidates) {
                $RejectedCandidates.Add((Copy-RejectedPeakCandidate -Candidate $candidate -Reason "merged-with-selected-peak" -Detail ("within $MergeHz Hz of $mergedWithFrequencyHz Hz") -MergedWithFrequencyHz $mergedWithFrequencyHz -RetuneLowHz $RetuneLowHz -RetuneHighHz $RetuneHighHz -RetuneSpanSource $RetuneSpanSource -MinimumSnrDb $MinimumSnrDb -PeakMergeHz $MergeHz)) | Out-Null
            }
            continue
        }

        $selected.Add([pscustomobject]$candidate) | Out-Null
    }

    return @($selected.ToArray())
}

function Get-OperatorFrequencyCandidates {
    param(
        [string[]]$FrequencyHz,
        [string[]]$FrequencyMHz,
        $OriginalVfo = $null
    )

    $values = New-Object System.Collections.Generic.List[long]
    foreach ($rawHz in @($FrequencyHz)) {
        foreach ($token in @(([string]$rawHz) -split ",")) {
            $trimmed = $token.Trim()
            if ([string]::IsNullOrWhiteSpace($trimmed)) {
                continue
            }

            $hz = 0L
            if ([long]::TryParse($trimmed, [ref]$hz) -and $hz -gt 0) {
                $values.Add($hz) | Out-Null
            }
        }
    }

    foreach ($rawMhz in @($FrequencyMHz)) {
        foreach ($token in @(([string]$rawMhz) -split ",")) {
            $trimmed = $token.Trim()
            if ([string]::IsNullOrWhiteSpace($trimmed)) {
                continue
            }

            $mhz = 0.0
            if ([double]::TryParse($trimmed, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$mhz) -and $mhz -gt 0.0) {
                $values.Add([long][Math]::Round($mhz * 1000000.0)) | Out-Null
            }
        }
    }

    $seen = @{}
    $rank = 0
    $candidates = New-Object System.Collections.Generic.List[object]
    foreach ($candidateHz in $values) {
        $key = [string]$candidateHz
        if ($seen.ContainsKey($key)) {
            continue
        }

        $seen[$key] = $true
        $rank++
        $offset = $null
        if ($null -ne $OriginalVfo) {
            $offset = [long]$candidateHz - [long]$OriginalVfo
        }

        $candidates.Add([pscustomobject][ordered]@{
            rank = $rank
            source = "operator-frequency"
            frequencyHz = $candidateHz
            offsetHz = $offset
            snrDb = $null
            dbfs = $null
            confidence = $null
            coherent = $null
        }) | Out-Null
    }

    return $candidates.ToArray()
}

function Get-OperatorTrendNeighborCandidates {
    param(
        [object[]]$OperatorCandidates,
        $OriginalVfo = $null,
        [long]$BandLowHz,
        [long]$BandHighHz,
        [int]$MaxCandidates
    )

    if ($MaxCandidates -lt 1) {
        return @()
    }

    $anchorMap = @{}
    foreach ($candidate in @($OperatorCandidates)) {
        $frequencyHz = Get-NullableLongValue (Get-JsonValue $candidate "frequencyHz")
        if ($null -eq $frequencyHz -or $frequencyHz -lt $BandLowHz -or $frequencyHz -gt $BandHighHz) {
            continue
        }

        $roundedHz = [long]([Math]::Round([double]$frequencyHz / 1000.0) * 1000.0)
        $key = [string]$roundedHz
        if (-not $anchorMap.ContainsKey($key)) {
            $anchorMap[$key] = [pscustomobject][ordered]@{
                frequencyHz = $roundedHz
                rank = Get-IntValue (Get-JsonValue $candidate "rank")
            }
        }
    }

    if ($anchorMap.Count -le 0) {
        return @()
    }

    $neighborOffsetsHz = @(-10000, -7000, -5000, -3000, 3000, 5000, 7000, 10000)
    $neighborMap = @{}
    foreach ($anchor in @($anchorMap.Values | Sort-Object @{ Expression = { [int]$_.rank }; Ascending = $true }, @{ Expression = { [long]$_.frequencyHz }; Ascending = $true })) {
        foreach ($offsetHz in $neighborOffsetsHz) {
            $candidateHz = [long]([Math]::Round(([double]([long]$anchor.frequencyHz + [long]$offsetHz)) / 1000.0) * 1000.0)
            if ($candidateHz -lt $BandLowHz -or $candidateHz -gt $BandHighHz) {
                continue
            }

            $key = [string]$candidateHz
            if ($anchorMap.ContainsKey($key)) {
                continue
            }

            $offsetScore = [Math]::Max(0.0, 40.0 - ([Math]::Abs([double]$offsetHz) / 1000.0))
            if ($offsetScore -le 0.0) {
                continue
            }

            if (-not $neighborMap.ContainsKey($key)) {
                $neighborMap[$key] = [pscustomobject][ordered]@{
                    frequencyHz = $candidateHz
                    sourceFrequencyHz = [long]$anchor.frequencyHz
                    neighborOffsetHz = [long]$offsetHz
                    supportCount = 1
                    score = [Math]::Round($offsetScore, 3)
                    bestOffsetScore = [double]$offsetScore
                    anchorFrequencyHz = @([long]$anchor.frequencyHz)
                }
                continue
            }

            $entry = $neighborMap[$key]
            $entry.supportCount = [int]$entry.supportCount + 1
            $entry.score = [Math]::Round(([double]$entry.score + ([double]$offsetScore * 0.35)), 3)
            $entry.anchorFrequencyHz = @(@($entry.anchorFrequencyHz) + [long]$anchor.frequencyHz | Select-Object -Unique)
            if ([double]$entry.bestOffsetScore -lt [double]$offsetScore) {
                $entry.sourceFrequencyHz = [long]$anchor.frequencyHz
                $entry.neighborOffsetHz = [long]$offsetHz
                $entry.bestOffsetScore = [double]$offsetScore
            }
        }
    }

    if ($neighborMap.Count -le 0) {
        return @()
    }

    $sortedNeighbors = @($neighborMap.Values | Sort-Object @{ Expression = { [int]$_.supportCount }; Descending = $true }, @{ Expression = { [double]$_.score }; Descending = $true }, @{ Expression = { [long]$_.frequencyHz }; Ascending = $true })
    $rank = 0
    $selectedNeighborMap = @{}
    $selectedNeighborSourceCounts = @{}
    $candidates = New-Object System.Collections.Generic.List[object]
    foreach ($perSourceLimit in @(1, 2, 9999)) {
        if ($candidates.Count -ge $MaxCandidates) {
            break
        }

        foreach ($neighbor in $sortedNeighbors) {
            if ($candidates.Count -ge $MaxCandidates) {
                break
            }

            $neighborKey = [string]([long]$neighbor.frequencyHz)
            if ($selectedNeighborMap.ContainsKey($neighborKey)) {
                continue
            }

            $sourceKey = [string]([long]$neighbor.sourceFrequencyHz)
            $sourceCount = 0
            if ($selectedNeighborSourceCounts.ContainsKey($sourceKey)) {
                $sourceCount = [int]$selectedNeighborSourceCounts[$sourceKey]
            }
            if ($sourceCount -ge $perSourceLimit) {
                continue
            }

            $offset = $null
            if ($null -ne $OriginalVfo) {
                $offset = [long]$neighbor.frequencyHz - [long]$OriginalVfo
            }

            $rank++
            $selectedNeighborMap[$neighborKey] = $true
            $selectedNeighborSourceCounts[$sourceKey] = $sourceCount + 1
            $candidates.Add([pscustomobject][ordered]@{
                rank = $rank
                source = "operator-trend-neighbor"
                frequencyHz = [long]$neighbor.frequencyHz
                offsetHz = $offset
                snrDb = $null
                dbfs = $null
                confidence = $null
                coherent = $null
                evidenceScore = [double]$neighbor.score
                evidenceOperatorAnchorFrequencyHz = [long]$neighbor.sourceFrequencyHz
                evidenceOperatorAnchorFrequencyHzList = @($neighbor.anchorFrequencyHz | Sort-Object)
                evidenceOperatorAnchorCount = [int]$neighbor.supportCount
                evidenceNeighborOffsetHz = [long]$neighbor.neighborOffsetHz
            }) | Out-Null
        }
    }

    return @($candidates.ToArray())
}

function Get-AutoPhoneClusterCandidates {
    param(
        [string]$SearchRoot,
        [long]$OriginalVfo,
        [long]$BandLowHz,
        [long]$BandHighHz,
        [int]$MaxCandidates,
        [int]$LookbackHours,
        [int]$MinSpeechSamples
    )

    if ([string]::IsNullOrWhiteSpace($SearchRoot) -or -not (Test-Path -LiteralPath $SearchRoot -PathType Container)) {
        return @()
    }

    $thresholdUtc = [DateTime]::UtcNow.AddHours(-1 * [Math]::Max(1, $LookbackHours))
    $seedMap = @{}
    $reportFiles = @(Get-ChildItem -LiteralPath $SearchRoot -Recurse -Filter "g2-rx-peak-hunt-report.json" -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTimeUtc -ge $thresholdUtc } |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 40)

    foreach ($file in $reportFiles) {
        $report = $null
        try {
            $report = Get-Content -Raw -LiteralPath $file.FullName | ConvertFrom-Json
        }
        catch {
            continue
        }

        foreach ($run in @(Get-JsonArray $report "runs")) {
            if (-not (Test-Truthy (Get-JsonValue $run "ok"))) {
                continue
            }

            $retuneFrequencyHz = Get-NullableLongValue (Get-JsonValue $run "frequencyHz")
            $exactCandidateFrequencyHz = Get-NullableLongValue (Get-JsonValue $run "exactCandidateFrequencyHz")
            $frequencyHz = $exactCandidateFrequencyHz
            if ($null -eq $frequencyHz -or $frequencyHz -le 0) {
                $frequencyHz = $retuneFrequencyHz
            }

            if ($null -eq $frequencyHz -or $frequencyHz -lt $BandLowHz -or $frequencyHz -gt $BandHighHz) {
                continue
            }

            $speechWeak = Get-IntValue (Get-JsonValue $run "speechQualifiedWeakInputSampleCount")
            $speechStrong = Get-IntValue (Get-JsonValue $run "speechQualifiedStrongInputSampleCount")
            $passbandWeak = Get-IntValue (Get-JsonValue $run "passbandQualifiedWeakInputSampleCount")
            $passbandStrong = Get-IntValue (Get-JsonValue $run "passbandQualifiedStrongInputSampleCount")
            $nearPassband = Get-IntValue (Get-JsonValue $run "frontendNearPassbandSampleCount")
            $strongInput = Get-IntValue (Get-JsonValue $run "strongInputSampleCount")
            $candidateSource = [string](Get-JsonValue $run "candidateSource")
            $tuningAction = Get-TrimmedStringValue (Get-JsonValue $run "mixedWeakStrongTuningAction")
            $outputGapExcess = Get-NullableDoubleValue (Get-JsonValue $run "mixedWeakStrongOutputGapExcessDb")
            $finalAudioGapExcess = Get-NullableDoubleValue (Get-JsonValue $run "mixedWeakStrongFinalAudioGapExcessDb")
            if (($speechWeak + $speechStrong) -lt $MinSpeechSamples -and ($passbandWeak + $passbandStrong) -le 0 -and $nearPassband -le 0) {
                continue
            }

            $roundedHz = [long]([Math]::Round([double]$frequencyHz / 1000.0) * 1000.0)
            $score = ([double]$speechWeak * 5.0) +
                ([double]$speechStrong * 12.0) +
                ([double]$passbandWeak * 4.0) +
                ([double]$passbandStrong * 10.0) +
                ([double]$nearPassband * 1.0) +
                ([double]$strongInput * 8.0)
            if (Test-Truthy (Get-JsonValue $run "mixedWeakStrongEvidenceReady")) {
                $score += 100.0
            }
            if ([string]::Equals($candidateSource, "operator-frequency", [StringComparison]::OrdinalIgnoreCase)) {
                $score += 100.0
            }

            $key = [string]$roundedHz
            if (-not $seedMap.ContainsKey($key) -or [double]$seedMap[$key].score -lt $score) {
                $seedMap[$key] = [pscustomobject][ordered]@{
                    frequencyHz = $roundedHz
                    sourceFrequencyHz = $frequencyHz
                    sourceRetuneVfoHz = $retuneFrequencyHz
                    score = [Math]::Round($score, 3)
                    speechWeak = $speechWeak
                    speechStrong = $speechStrong
                    passbandWeak = $passbandWeak
                    passbandStrong = $passbandStrong
                    nearPassband = $nearPassband
                    candidateSource = $candidateSource
                    status = [string](Get-JsonValue $run "mixedWeakStrongEvidenceStatus")
                    tuningAction = $tuningAction
                    outputGapExcessDb = $outputGapExcess
                    finalAudioGapExcessDb = $finalAudioGapExcess
                    reportPath = $file.FullName
                }
            }
        }
    }

    $neighborReserve = 0
    if ($MaxCandidates -ge 8 -and $seedMap.Count -gt 0) {
        $neighborReserve = [Math]::Min(4, [Math]::Max(1, [int][Math]::Floor([double]$MaxCandidates * 0.25)))
    }
    $exactCandidateLimit = [Math]::Max(0, $MaxCandidates - $neighborReserve)

    $rank = 0
    $selectedFrequencyMap = @{}
    $candidates = New-Object System.Collections.Generic.List[object]
    foreach ($seed in @($seedMap.Values | Sort-Object @{ Expression = { [double]$_.score }; Descending = $true }, @{ Expression = { [long]$_.frequencyHz }; Ascending = $true } | Select-Object -First $exactCandidateLimit)) {
        $rank++
        $selectedFrequencyMap[[string]([long]$seed.frequencyHz)] = $true
        $candidates.Add([pscustomobject][ordered]@{
            rank = $rank
            source = "recent-phone-cluster"
            frequencyHz = [long]$seed.frequencyHz
            offsetHz = ([long]$seed.frequencyHz - [long]$OriginalVfo)
            snrDb = $null
            dbfs = $null
            confidence = $null
            coherent = $null
            evidenceScore = [double]$seed.score
            evidenceSpeechWeak = [int]$seed.speechWeak
            evidenceSpeechStrong = [int]$seed.speechStrong
            evidencePassbandWeak = [int]$seed.passbandWeak
            evidencePassbandStrong = [int]$seed.passbandStrong
            evidenceNearPassband = [int]$seed.nearPassband
            evidenceCandidateSource = [string]$seed.candidateSource
            evidenceStatus = [string]$seed.status
            evidenceTuningAction = $seed.tuningAction
            evidenceOutputGapExcessDb = $seed.outputGapExcessDb
            evidenceFinalAudioGapExcessDb = $seed.finalAudioGapExcessDb
            evidenceRetuneVfoHz = Get-NullableLongValue $seed.sourceRetuneVfoHz
            evidenceReportPath = [string]$seed.reportPath
        }) | Out-Null
    }

    if ($candidates.Count -lt $MaxCandidates -and $seedMap.Count -gt 0) {
        $neighborOffsetsHz = @(-10000, -7000, -5000, -3000, 3000, 5000, 7000, 10000)
        $neighborMap = @{}
        foreach ($seed in @($seedMap.Values | Sort-Object @{ Expression = { [double]$_.score }; Descending = $true }, @{ Expression = { [long]$_.frequencyHz }; Ascending = $true })) {
            foreach ($offsetHz in $neighborOffsetsHz) {
                $candidateHz = [long]([Math]::Round(([double]([long]$seed.frequencyHz + [long]$offsetHz)) / 1000.0) * 1000.0)
                if ($candidateHz -lt $BandLowHz -or $candidateHz -gt $BandHighHz) {
                    continue
                }

                $key = [string]$candidateHz
                if ($seedMap.ContainsKey($key) -or $selectedFrequencyMap.ContainsKey($key)) {
                    continue
                }

                $score = [Math]::Round(([double]$seed.score * 0.62) - ([Math]::Abs([double]$offsetHz) / 1000.0), 3)
                if ($score -le 0.0) {
                    continue
                }

                if (-not $neighborMap.ContainsKey($key) -or [double]$neighborMap[$key].score -lt $score) {
                    $neighborMap[$key] = [pscustomobject][ordered]@{
                        frequencyHz = $candidateHz
                        sourceFrequencyHz = [long]$seed.frequencyHz
                        sourceRetuneVfoHz = Get-NullableLongValue $seed.sourceRetuneVfoHz
                        score = $score
                        speechWeak = [int]$seed.speechWeak
                        speechStrong = [int]$seed.speechStrong
                        passbandWeak = [int]$seed.passbandWeak
                        passbandStrong = [int]$seed.passbandStrong
                        nearPassband = [int]$seed.nearPassband
                        candidateSource = [string]$seed.candidateSource
                        status = [string]$seed.status
                        tuningAction = $seed.tuningAction
                        outputGapExcessDb = $seed.outputGapExcessDb
                        finalAudioGapExcessDb = $seed.finalAudioGapExcessDb
                        reportPath = [string]$seed.reportPath
                        neighborOffsetHz = [long]$offsetHz
                    }
                }
            }
        }

        $sortedNeighbors = @($neighborMap.Values | Sort-Object @{ Expression = { [double]$_.score }; Descending = $true }, @{ Expression = { [long]$_.frequencyHz }; Ascending = $true })
        $selectedNeighborMap = @{}
        $selectedNeighborSourceCounts = @{}
        foreach ($perSourceLimit in @(1, 2, 9999)) {
            if ($candidates.Count -ge $MaxCandidates) {
                break
            }

            foreach ($seed in $sortedNeighbors) {
                if ($candidates.Count -ge $MaxCandidates) {
                    break
                }

                $neighborKey = [string]([long]$seed.frequencyHz)
                if ($selectedNeighborMap.ContainsKey($neighborKey)) {
                    continue
                }

                $sourceKey = [string]([long]$seed.sourceFrequencyHz)
                $sourceCount = 0
                if ($selectedNeighborSourceCounts.ContainsKey($sourceKey)) {
                    $sourceCount = [int]$selectedNeighborSourceCounts[$sourceKey]
                }
                if ($sourceCount -ge $perSourceLimit) {
                    continue
                }

                $rank++
                $selectedFrequencyMap[$neighborKey] = $true
                $selectedNeighborMap[$neighborKey] = $true
                $selectedNeighborSourceCounts[$sourceKey] = $sourceCount + 1
                $candidates.Add([pscustomobject][ordered]@{
                    rank = $rank
                    source = "recent-phone-cluster-neighbor"
                    frequencyHz = [long]$seed.frequencyHz
                    offsetHz = ([long]$seed.frequencyHz - [long]$OriginalVfo)
                    snrDb = $null
                    dbfs = $null
                    confidence = $null
                    coherent = $null
                    evidenceScore = [double]$seed.score
                    evidenceSpeechWeak = [int]$seed.speechWeak
                    evidenceSpeechStrong = [int]$seed.speechStrong
                    evidencePassbandWeak = [int]$seed.passbandWeak
                    evidencePassbandStrong = [int]$seed.passbandStrong
                    evidenceNearPassband = [int]$seed.nearPassband
                    evidenceCandidateSource = [string]$seed.candidateSource
                    evidenceStatus = [string]$seed.status
                    evidenceTuningAction = $seed.tuningAction
                    evidenceOutputGapExcessDb = $seed.outputGapExcessDb
                    evidenceFinalAudioGapExcessDb = $seed.finalAudioGapExcessDb
                    evidenceReportPath = [string]$seed.reportPath
                    evidenceNeighborOfFrequencyHz = [long]$seed.sourceFrequencyHz
                    evidenceNeighborOffsetHz = [long]$seed.neighborOffsetHz
                    evidenceRetuneVfoHz = Get-NullableLongValue $seed.sourceRetuneVfoHz
                }) | Out-Null
            }
        }
    }

    return @($candidates.ToArray())
}

function Add-CandidateIfDistinct {
    param(
        [AllowEmptyCollection()]
        [Parameter(Mandatory = $true)][System.Collections.Generic.List[object]]$Candidates,
        $Candidate,
        [int]$MergeHz
    )

    if ($null -eq $Candidate) {
        return $false
    }

    $frequencyHz = Get-NullableLongValue (Get-JsonValue $Candidate "frequencyHz")
    if ($null -eq $frequencyHz -or $frequencyHz -le 0) {
        return $false
    }

    foreach ($existing in $Candidates) {
        $existingHz = Get-NullableLongValue (Get-JsonValue $existing "frequencyHz")
        if ($null -ne $existingHz -and [Math]::Abs([long]$frequencyHz - [long]$existingHz) -le $MergeHz) {
            return $false
        }
    }

    $Candidates.Add($Candidate) | Out-Null
    return $true
}

function Set-CandidateProperty {
    param(
        [Parameter(Mandatory = $true)]$Candidate,
        [Parameter(Mandatory = $true)][string]$Name,
        $Value
    )

    if ($null -eq $Candidate.PSObject.Properties[$Name]) {
        Add-Member -InputObject $Candidate -NotePropertyName $Name -NotePropertyValue $Value -Force
        return
    }

    $Candidate.$Name = $Value
}

function ConvertTo-RetuneGeometryCandidate {
    param(
        $Candidate,
        [long]$OriginalVfo = 0,
        [int]$StepHz = 1000,
        [double]$RetuneTargetOffsetHz = 0.0,
        [string]$CenteredRetuneReason = "retune-to-center-candidate"
    )

    if ($null -eq $Candidate) {
        return $null
    }

    $frequencyHz = Get-NullableLongValue (Get-JsonValue $Candidate "frequencyHz")
    if ($null -eq $frequencyHz -or $frequencyHz -le 0) {
        return $null
    }

    $exactFrequencyHz = Get-NullableLongValue (Get-JsonValue $Candidate "exactFrequencyHz")
    if ($null -eq $exactFrequencyHz -or $exactFrequencyHz -le 0) {
        $exactFrequencyHz = [long]$frequencyHz
    }

    $effectiveStepHz = Normalize-TuneStepHz $StepHz
    $exactRetuneVfoHz = [long][Math]::Round([double]$exactFrequencyHz - [double]$RetuneTargetOffsetHz)
    if ($exactRetuneVfoHz -le 0) {
        $exactRetuneVfoHz = [long]$exactFrequencyHz
    }

    $retuneFrequencyHz = Quantize-HzToStep -Hz ([double]$exactRetuneVfoHz) -StepHz $effectiveStepHz
    if ($retuneFrequencyHz -le 0) {
        $retuneFrequencyHz = [long]$exactRetuneVfoHz
    }

    $exactOffsetHz = Get-NullableLongValue (Get-JsonValue $Candidate "exactOffsetHz")
    if ($OriginalVfo -gt 0) {
        $exactOffsetHz = [long]$exactFrequencyHz - [long]$OriginalVfo
    }

    $offsetHz = $null
    if ($OriginalVfo -gt 0) {
        $offsetHz = [long]$retuneFrequencyHz - [long]$OriginalVfo
    }

    $retuneReason = Get-TrimmedStringValue (Get-JsonValue $Candidate "retuneReason")
    if ([Math]::Abs([double]$RetuneTargetOffsetHz) -gt 0.1) {
        $retuneReason = $CenteredRetuneReason
    }
    elseif ($null -eq $retuneReason) {
        $retuneReason = if ([long]$retuneFrequencyHz -ne [long]$exactRetuneVfoHz) { "retune-to-stepped-candidate" } else { "retune-to-exact-candidate" }
    }

    $geometryKeys = @(
        "frequencyHz",
        "exactFrequencyHz",
        "offsetHz",
        "exactOffsetHz",
        "tuningStepHz",
        "tuneSnapDeltaHz",
        "retuneTargetOffsetHz",
        "exactRetuneVfoHz",
        "peakToRetunedVfoOffsetHz",
        "retuneReason"
    )
    $copy = [ordered]@{}
    foreach ($property in $Candidate.PSObject.Properties) {
        if ($geometryKeys -notcontains $property.Name) {
            $copy[$property.Name] = $property.Value
        }
    }

    $copy["frequencyHz"] = [long]$retuneFrequencyHz
    $copy["exactFrequencyHz"] = [long]$exactFrequencyHz
    $copy["offsetHz"] = $offsetHz
    $copy["exactOffsetHz"] = $exactOffsetHz
    $copy["tuningStepHz"] = $effectiveStepHz
    $copy["tuneSnapDeltaHz"] = [long]$retuneFrequencyHz - [long]$exactRetuneVfoHz
    $copy["retuneTargetOffsetHz"] = [Math]::Round([double]$RetuneTargetOffsetHz, 1)
    $copy["exactRetuneVfoHz"] = [long]$exactRetuneVfoHz
    $copy["peakToRetunedVfoOffsetHz"] = [long]$exactFrequencyHz - [long]$retuneFrequencyHz
    $copy["retuneReason"] = $retuneReason

    return [pscustomobject]$copy
}

function Normalize-RetuneCandidate {
    param(
        $Candidate,
        [long]$OriginalVfo = 0,
        [int]$StepHz = 1000
    )

    if ($null -eq $Candidate) {
        return $null
    }

    $frequencyHz = Get-NullableLongValue (Get-JsonValue $Candidate "frequencyHz")
    if ($null -eq $frequencyHz -or $frequencyHz -le 0) {
        return $null
    }

    $effectiveStepHz = Normalize-TuneStepHz $StepHz
    $exactFrequencyHz = Get-NullableLongValue (Get-JsonValue $Candidate "exactFrequencyHz")
    if ($null -eq $exactFrequencyHz -or $exactFrequencyHz -le 0) {
        $exactFrequencyHz = [long]$frequencyHz
    }

    $exactRetuneVfoHz = Get-NullableLongValue (Get-JsonValue $Candidate "exactRetuneVfoHz")
    if ($null -eq $exactRetuneVfoHz -or $exactRetuneVfoHz -le 0) {
        $exactRetuneVfoHz = [long]$frequencyHz
    }

    $normalizedFrequencyHz = Quantize-HzToStep -Hz ([double]$exactRetuneVfoHz) -StepHz $effectiveStepHz
    if ($normalizedFrequencyHz -le 0) {
        $normalizedFrequencyHz = [long]$exactRetuneVfoHz
    }

    $retuneTargetOffsetHz = Get-NullableDoubleValue (Get-JsonValue $Candidate "retuneTargetOffsetHz")
    if ($null -eq $retuneTargetOffsetHz) {
        $retuneTargetOffsetHz = 0.0
    }

    $retuneReason = Get-TrimmedStringValue (Get-JsonValue $Candidate "retuneReason")
    if ($null -eq $retuneReason) {
        $retuneReason = if ([long]$normalizedFrequencyHz -ne [long]$exactRetuneVfoHz) { "retune-to-stepped-candidate" } else { "retune-to-exact-candidate" }
    }

    $exactOffsetHz = Get-NullableLongValue (Get-JsonValue $Candidate "exactOffsetHz")
    if ($OriginalVfo -gt 0) {
        $exactOffsetHz = [long]$exactFrequencyHz - [long]$OriginalVfo
    }

    $offsetHz = $null
    if ($OriginalVfo -gt 0) {
        $offsetHz = [long]$normalizedFrequencyHz - [long]$OriginalVfo
    }

    Set-CandidateProperty -Candidate $Candidate -Name "frequencyHz" -Value ([long]$normalizedFrequencyHz)
    Set-CandidateProperty -Candidate $Candidate -Name "exactFrequencyHz" -Value ([long]$exactFrequencyHz)
    Set-CandidateProperty -Candidate $Candidate -Name "offsetHz" -Value $offsetHz
    Set-CandidateProperty -Candidate $Candidate -Name "exactOffsetHz" -Value $exactOffsetHz
    Set-CandidateProperty -Candidate $Candidate -Name "tuningStepHz" -Value $effectiveStepHz
    Set-CandidateProperty -Candidate $Candidate -Name "tuneSnapDeltaHz" -Value ([long]$normalizedFrequencyHz - [long]$exactRetuneVfoHz)
    Set-CandidateProperty -Candidate $Candidate -Name "retuneTargetOffsetHz" -Value ([Math]::Round([double]$retuneTargetOffsetHz, 1))
    Set-CandidateProperty -Candidate $Candidate -Name "exactRetuneVfoHz" -Value ([long]$exactRetuneVfoHz)
    Set-CandidateProperty -Candidate $Candidate -Name "peakToRetunedVfoOffsetHz" -Value ([long]$exactFrequencyHz - [long]$normalizedFrequencyHz)
    Set-CandidateProperty -Candidate $Candidate -Name "retuneReason" -Value $retuneReason

    return $Candidate
}

function Copy-RejectedRetuneCandidate {
    param(
        $Candidate,
        [string]$Reason,
        [string]$Detail = "",
        $RetuneLowHz = $null,
        $RetuneHighHz = $null,
        [string]$RetuneSpanSource = ""
    )

    if ($null -eq $Candidate) {
        return $null
    }

    return [pscustomobject][ordered]@{
        rank = Get-IntValue (Get-JsonValue $Candidate "rank")
        source = [string](Get-JsonValue $Candidate "source")
        frequencyHz = Get-NullableLongValue (Get-JsonValue $Candidate "frequencyHz")
        exactFrequencyHz = Get-NullableLongValue (Get-JsonValue $Candidate "exactFrequencyHz")
        offsetHz = Get-NullableLongValue (Get-JsonValue $Candidate "offsetHz")
        exactOffsetHz = Get-NullableLongValue (Get-JsonValue $Candidate "exactOffsetHz")
        tuningStepHz = Get-IntValue (Get-JsonValue $Candidate "tuningStepHz")
        tuneSnapDeltaHz = Get-NullableLongValue (Get-JsonValue $Candidate "tuneSnapDeltaHz")
        retuneTargetOffsetHz = Get-NullableDoubleValue (Get-JsonValue $Candidate "retuneTargetOffsetHz")
        exactRetuneVfoHz = Get-NullableLongValue (Get-JsonValue $Candidate "exactRetuneVfoHz")
        peakToRetunedVfoOffsetHz = Get-NullableLongValue (Get-JsonValue $Candidate "peakToRetunedVfoOffsetHz")
        retuneReason = [string](Get-JsonValue $Candidate "retuneReason")
        snrDb = Get-NullableDoubleValue (Get-JsonValue $Candidate "snrDb")
        dbfs = Get-NullableDoubleValue (Get-JsonValue $Candidate "dbfs")
        confidence = Get-NullableDoubleValue (Get-JsonValue $Candidate "confidence")
        coherent = Test-Truthy (Get-JsonValue $Candidate "coherent")
        rejectionReason = $Reason
        rejectionDetail = $Detail
        retuneLowHz = Get-NullableLongValue $RetuneLowHz
        retuneHighHz = Get-NullableLongValue $RetuneHighHz
        retuneSpanSource = $RetuneSpanSource
    }
}

function Add-RetuneCandidateIfAdmitted {
    param(
        [AllowEmptyCollection()]
        [Parameter(Mandatory = $true)][System.Collections.Generic.List[object]]$Candidates,
        $Candidate,
        [int]$MergeHz,
        [int]$StepHz = 1000,
        [long]$OriginalVfo = 0,
        $RetuneLowHz = $null,
        $RetuneHighHz = $null,
        [string]$RetuneSpanSource = "",
        [System.Collections.Generic.List[object]]$RejectedCandidates = $null
    )

    $normalizedCandidate = Normalize-RetuneCandidate -Candidate $Candidate -OriginalVfo $OriginalVfo -StepHz $StepHz
    if ($null -eq $normalizedCandidate) {
        return $false
    }

    $frequencyHz = Get-NullableLongValue (Get-JsonValue $normalizedCandidate "frequencyHz")
    if ($null -eq $frequencyHz -or $frequencyHz -le 0) {
        return $false
    }

    if ($null -ne $RetuneLowHz -and [long]$frequencyHz -lt [long]$RetuneLowHz) {
        if ($null -ne $RejectedCandidates) {
            $RejectedCandidates.Add((Copy-RejectedRetuneCandidate -Candidate $normalizedCandidate -Reason "outside-retune-span-low" -Detail ("below $RetuneLowHz Hz") -RetuneLowHz $RetuneLowHz -RetuneHighHz $RetuneHighHz -RetuneSpanSource $RetuneSpanSource)) | Out-Null
        }
        return $false
    }

    if ($null -ne $RetuneHighHz -and [long]$frequencyHz -gt [long]$RetuneHighHz) {
        if ($null -ne $RejectedCandidates) {
            $RejectedCandidates.Add((Copy-RejectedRetuneCandidate -Candidate $normalizedCandidate -Reason "outside-retune-span-high" -Detail ("above $RetuneHighHz Hz") -RetuneLowHz $RetuneLowHz -RetuneHighHz $RetuneHighHz -RetuneSpanSource $RetuneSpanSource)) | Out-Null
        }
        return $false
    }

    return Add-CandidateIfDistinct -Candidates $Candidates -Candidate $normalizedCandidate -MergeHz $MergeHz
}

function Copy-PeakCandidateForPass {
    param(
        $Candidate,
        [int]$Pass
    )

    if ($null -eq $Candidate) {
        return $null
    }

    return [pscustomobject][ordered]@{
        pass = $Pass
        rank = Get-IntValue (Get-JsonValue $Candidate "rank")
        source = [string](Get-JsonValue $Candidate "source")
        frequencyHz = Get-NullableLongValue (Get-JsonValue $Candidate "frequencyHz")
        exactFrequencyHz = Get-NullableLongValue (Get-JsonValue $Candidate "exactFrequencyHz")
        offsetHz = Get-NullableLongValue (Get-JsonValue $Candidate "offsetHz")
        exactOffsetHz = Get-NullableLongValue (Get-JsonValue $Candidate "exactOffsetHz")
        tuningStepHz = Get-IntValue (Get-JsonValue $Candidate "tuningStepHz")
        tuneSnapDeltaHz = Get-NullableLongValue (Get-JsonValue $Candidate "tuneSnapDeltaHz")
        retuneTargetOffsetHz = Get-NullableDoubleValue (Get-JsonValue $Candidate "retuneTargetOffsetHz")
        exactRetuneVfoHz = Get-NullableLongValue (Get-JsonValue $Candidate "exactRetuneVfoHz")
        peakToRetunedVfoOffsetHz = Get-NullableLongValue (Get-JsonValue $Candidate "peakToRetunedVfoOffsetHz")
        retuneReason = [string](Get-JsonValue $Candidate "retuneReason")
        snrDb = Get-NullableDoubleValue (Get-JsonValue $Candidate "snrDb")
        dbfs = Get-NullableDoubleValue (Get-JsonValue $Candidate "dbfs")
        confidence = Get-NullableDoubleValue (Get-JsonValue $Candidate "confidence")
        coherent = Test-Truthy (Get-JsonValue $Candidate "coherent")
    }
}

function Copy-RejectedPeakCandidateForPass {
    param(
        $Candidate,
        [int]$Pass
    )

    if ($null -eq $Candidate) {
        return $null
    }

    return [pscustomobject][ordered]@{
        pass = $Pass
        rank = Get-IntValue (Get-JsonValue $Candidate "rank")
        source = [string](Get-JsonValue $Candidate "source")
        frequencyHz = Get-NullableLongValue (Get-JsonValue $Candidate "frequencyHz")
        exactFrequencyHz = Get-NullableLongValue (Get-JsonValue $Candidate "exactFrequencyHz")
        offsetHz = Get-NullableLongValue (Get-JsonValue $Candidate "offsetHz")
        exactOffsetHz = Get-NullableLongValue (Get-JsonValue $Candidate "exactOffsetHz")
        tuningStepHz = Get-IntValue (Get-JsonValue $Candidate "tuningStepHz")
        tuneSnapDeltaHz = Get-NullableLongValue (Get-JsonValue $Candidate "tuneSnapDeltaHz")
        retuneTargetOffsetHz = Get-NullableDoubleValue (Get-JsonValue $Candidate "retuneTargetOffsetHz")
        exactRetuneVfoHz = Get-NullableLongValue (Get-JsonValue $Candidate "exactRetuneVfoHz")
        peakToRetunedVfoOffsetHz = Get-NullableLongValue (Get-JsonValue $Candidate "peakToRetunedVfoOffsetHz")
        retuneReason = [string](Get-JsonValue $Candidate "retuneReason")
        snrDb = Get-NullableDoubleValue (Get-JsonValue $Candidate "snrDb")
        dbfs = Get-NullableDoubleValue (Get-JsonValue $Candidate "dbfs")
        confidence = Get-NullableDoubleValue (Get-JsonValue $Candidate "confidence")
        coherent = Test-Truthy (Get-JsonValue $Candidate "coherent")
        rejectionReason = [string](Get-JsonValue $Candidate "rejectionReason")
        rejectionDetail = [string](Get-JsonValue $Candidate "rejectionDetail")
        retuneLowHz = Get-NullableLongValue (Get-JsonValue $Candidate "retuneLowHz")
        retuneHighHz = Get-NullableLongValue (Get-JsonValue $Candidate "retuneHighHz")
        retuneSpanSource = [string](Get-JsonValue $Candidate "retuneSpanSource")
        minimumSnrDb = Get-NullableDoubleValue (Get-JsonValue $Candidate "minimumSnrDb")
        peakMergeHz = Get-IntValue (Get-JsonValue $Candidate "peakMergeHz")
        mergedWithFrequencyHz = Get-NullableLongValue (Get-JsonValue $Candidate "mergedWithFrequencyHz")
        mergeDeltaHz = Get-NullableLongValue (Get-JsonValue $Candidate "mergeDeltaHz")
    }
}

function Copy-RejectedRetuneCandidateForPass {
    param(
        $Candidate,
        [int]$Pass
    )

    if ($null -eq $Candidate) {
        return $null
    }

    return [pscustomobject][ordered]@{
        pass = $Pass
        rank = Get-IntValue (Get-JsonValue $Candidate "rank")
        source = [string](Get-JsonValue $Candidate "source")
        frequencyHz = Get-NullableLongValue (Get-JsonValue $Candidate "frequencyHz")
        exactFrequencyHz = Get-NullableLongValue (Get-JsonValue $Candidate "exactFrequencyHz")
        offsetHz = Get-NullableLongValue (Get-JsonValue $Candidate "offsetHz")
        exactOffsetHz = Get-NullableLongValue (Get-JsonValue $Candidate "exactOffsetHz")
        tuningStepHz = Get-IntValue (Get-JsonValue $Candidate "tuningStepHz")
        tuneSnapDeltaHz = Get-NullableLongValue (Get-JsonValue $Candidate "tuneSnapDeltaHz")
        retuneTargetOffsetHz = Get-NullableDoubleValue (Get-JsonValue $Candidate "retuneTargetOffsetHz")
        exactRetuneVfoHz = Get-NullableLongValue (Get-JsonValue $Candidate "exactRetuneVfoHz")
        peakToRetunedVfoOffsetHz = Get-NullableLongValue (Get-JsonValue $Candidate "peakToRetunedVfoOffsetHz")
        retuneReason = [string](Get-JsonValue $Candidate "retuneReason")
        snrDb = Get-NullableDoubleValue (Get-JsonValue $Candidate "snrDb")
        dbfs = Get-NullableDoubleValue (Get-JsonValue $Candidate "dbfs")
        confidence = Get-NullableDoubleValue (Get-JsonValue $Candidate "confidence")
        coherent = Test-Truthy (Get-JsonValue $Candidate "coherent")
        rejectionReason = [string](Get-JsonValue $Candidate "rejectionReason")
        rejectionDetail = [string](Get-JsonValue $Candidate "rejectionDetail")
        retuneLowHz = Get-NullableLongValue (Get-JsonValue $Candidate "retuneLowHz")
        retuneHighHz = Get-NullableLongValue (Get-JsonValue $Candidate "retuneHighHz")
        retuneSpanSource = [string](Get-JsonValue $Candidate "retuneSpanSource")
    }
}

function Get-HuntScore {
    param(
        [int]$WeakInputSampleCount,
        [int]$StrongInputSampleCount,
        [int]$NearStrongInputSampleCount,
        $WeakStrongOutputGapDb,
        $WeakStrongFinalAudioGapDb,
        [bool]$MixedWeakStrongEvidenceReady,
        [bool]$ReadyForBenchmarkTrace,
        [int]$NearPassbandSampleCount,
        [int]$ExpectedSampleCount
    )

    $denominator = [Math]::Max(1, $ExpectedSampleCount)
    $score = ([Math]::Min(1.0, [double]$WeakInputSampleCount / [double]$denominator) * 30.0) +
        ([Math]::Min(1.0, [double]$StrongInputSampleCount / [double]$denominator) * 35.0)
    if ($StrongInputSampleCount -le 0 -and $NearStrongInputSampleCount -gt 0) {
        $score += [Math]::Min(1.0, [double]$NearStrongInputSampleCount / [double]$denominator) * 14.0
    }

    $gap = $WeakStrongFinalAudioGapDb
    if ($null -eq $gap) {
        $gap = $WeakStrongOutputGapDb
    }
    if ($null -ne $gap) {
        $score += [Math]::Max(0.0, 20.0 - ([Math]::Min(12.0, [Math]::Abs([double]$gap)) / 12.0 * 20.0))
    }

    if ($MixedWeakStrongEvidenceReady) {
        $score += 10.0
    }
    if ($ReadyForBenchmarkTrace) {
        $score += 5.0
    }
    if ($NearPassbandSampleCount -gt 0) {
        $score += 5.0
    }
    elseif ($StrongInputSampleCount -le 0) {
        $score -= 5.0
    }

    return [Math]::Round($score, 3)
}

function Invoke-WatchWindow {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [Parameter(Mandatory = $true)][string]$Base,
        [Parameter(Mandatory = $true)][string]$ScenarioId,
        [Parameter(Mandatory = $true)][string]$Comparison,
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string]$Report,
        [Parameter(Mandatory = $true)][string]$Jsonl,
        [int]$SampleCount,
        [int]$DelayMs,
        [int]$RequestTimeoutSec,
        [switch]$SkipCertificate
    )

    $args = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", $ScriptPath,
        "-BaseUrl", $Base,
        "-Samples", ([string]$SampleCount),
        "-IntervalMs", ([string]$DelayMs),
        "-TimeoutSec", ([string]$RequestTimeoutSec),
        "-Label", $Label,
        "-ScenarioId", $ScenarioId,
        "-ComparisonId", $Comparison,
        "-ReportPath", $Report,
        "-JsonlPath", $Jsonl,
        "-JsonOnly"
    )
    if ($SkipCertificate) {
        $args += "-SkipCertificateCheck"
    }

    $output = & powershell @args 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        return [ordered]@{
            ok = $false
            exitCode = $exitCode
            error = ($output -join [Environment]::NewLine)
            report = $null
        }
    }

    return [ordered]@{
        ok = $true
        exitCode = $exitCode
        error = $null
        report = Get-Content -Raw -LiteralPath $Report | ConvertFrom-Json
    }
}

$repoRoot = Get-RepoRoot
if ($SkipCertificateCheck) {
    Enable-CertificateBypass
}
$baseResolution = Resolve-ZeusBaseUrl -RequestedUrl $BaseUrl -RequestTimeoutSec $TimeoutSec -SkipCertificate:$SkipCertificateCheck -AutoDiscover:$AutoDiscoverBaseUrl
$base = Normalize-BaseUrl $baseResolution.baseUrl
$requestedBaseUrl = [string]$baseResolution.requestedBaseUrl
$baseUrlAutoDiscoverRequested = [bool]$baseResolution.autoDiscoverRequested
$baseUrlAutoDiscovered = [bool]$baseResolution.autoDiscovered
$baseUrlAutoDiscoverError = [string]$baseResolution.autoDiscoverError
$baseUrlProbeResults = @($baseResolution.probeResults)
$targetMode = $Mode.Trim().ToUpperInvariant()
$frontendPeakRetuneMode = if ([string]::IsNullOrWhiteSpace($targetMode)) { "" } else { $targetMode }
$frontendPeakRetuneFilterLowHz = $null
$frontendPeakRetuneFilterHighHz = $null
$frontendPeakRetuneTargetOffsetHz = Get-FrontendPeakRetuneTargetOffsetHz -Mode $frontendPeakRetuneMode
$effectiveTuneStepHz = Normalize-TuneStepHz $TuneStepHz
$effectivePeakRetunePaddingHz = Normalize-PeakRetunePaddingHz $PeakRetunePaddingHz
if ([string]::IsNullOrWhiteSpace($AutoPhoneClusterSearchRoot)) {
    $AutoPhoneClusterSearchRoot = Join-Path $repoRoot "tmp"
}
try {
    $AutoPhoneClusterSearchRoot = [System.IO.Path]::GetFullPath($AutoPhoneClusterSearchRoot)
}
catch {
}
if ($AutoPhoneClusterMaxCandidates -lt 0) {
    $AutoPhoneClusterMaxCandidates = 0
}
if ($AutoPhoneClusterLookbackHours -lt 1) {
    $AutoPhoneClusterLookbackHours = 1
}
if ($AutoPhoneClusterMinSpeechSamples -lt 0) {
    $AutoPhoneClusterMinSpeechSamples = 0
}
if ($OperatorTrendMaxCandidates -lt 0) {
    $OperatorTrendMaxCandidates = 0
}

if ($PassCount -lt 1) {
    $PassCount = 1
}
if ($PassDelaySec -lt 0) {
    $PassDelaySec = 0
}
$operatorCandidatesForPlan = @(Get-OperatorFrequencyCandidates -FrequencyHz $CandidateFrequencyHz -FrequencyMHz $CandidateMHz)
$operatorCandidateFrequencyHzForPlan = @($operatorCandidatesForPlan | ForEach-Object { [long]$_.frequencyHz })
$operatorTrendCandidatesForPlan = @(Get-OperatorTrendNeighborCandidates `
        -OperatorCandidates $operatorCandidatesForPlan `
        -OriginalVfo $null `
        -BandLowHz $AutoPhoneClusterBandLowHz `
        -BandHighHz $AutoPhoneClusterBandHighHz `
        -MaxCandidates $OperatorTrendMaxCandidates)
$operatorTrendCandidateFrequencyHzForPlan = @($operatorTrendCandidatesForPlan | ForEach-Object { [long]$_.frequencyHz })
$peakRetuneSpanForPlan = Get-PeakRetuneSpan `
    -SeedCandidates @($operatorCandidatesForPlan + $operatorTrendCandidatesForPlan) `
    -ExplicitLowHz $PeakRetuneLowHz `
    -ExplicitHighHz $PeakRetuneHighHz `
    -PaddingHz $effectivePeakRetunePaddingHz `
    -FallbackLowHz $AutoPhoneClusterBandLowHz `
    -FallbackHighHz $AutoPhoneClusterBandHighHz `
    -UseFallback ([bool]$AutoPhoneCluster)

if ([string]::IsNullOrWhiteSpace($WatchScriptPath)) {
    $WatchScriptPath = Join-Path $repoRoot "tools\watch-dsp-live-diagnostics.ps1"
}
$resolvedWatchScript = (Resolve-Path -LiteralPath $WatchScriptPath).Path

if ($PlanOnly) {
    [ordered]@{
        schemaVersion = 1
        tool = "run-dsp-g2-rx-peak-hunt"
        mode = "plan-only"
        requestedBaseUrl = $requestedBaseUrl
        baseUrl = $base
        baseUrlAutoDiscoverRequested = $baseUrlAutoDiscoverRequested
        baseUrlAutoDiscovered = $baseUrlAutoDiscovered
        baseUrlAutoDiscoverError = $baseUrlAutoDiscoverError
        baseUrlProbeResults = @($baseUrlProbeResults)
        targetMode = if ([string]::IsNullOrWhiteSpace($targetMode)) { $null } else { $targetMode }
        samplesPerWindow = $SamplesPerWindow
        intervalMs = $IntervalMs
        windowsPerPeak = $WindowsPerPeak
        passCount = $PassCount
        passDelaySec = $PassDelaySec
        tuneStepHz = $effectiveTuneStepHz
        frontendPeakRetuneMode = if ([string]::IsNullOrWhiteSpace($frontendPeakRetuneMode)) { $null } else { $frontendPeakRetuneMode }
        frontendPeakRetuneTargetOffsetHz = $frontendPeakRetuneTargetOffsetHz
        peakRetuneSpan = $peakRetuneSpanForPlan
        candidateFrequencyHz = @($operatorCandidateFrequencyHzForPlan)
        operatorTrendMaxCandidates = $OperatorTrendMaxCandidates
        operatorTrendCandidateFrequencyHz = @($operatorTrendCandidateFrequencyHzForPlan)
        operatorTrendCandidateCount = $operatorTrendCandidatesForPlan.Count
        operatorTrendCandidates = @($operatorTrendCandidatesForPlan)
        autoPhoneCluster = [bool]$AutoPhoneCluster
        autoPhoneClusterSearchRoot = $AutoPhoneClusterSearchRoot
        autoPhoneClusterMaxCandidates = $AutoPhoneClusterMaxCandidates
        autoPhoneClusterLookbackHours = $AutoPhoneClusterLookbackHours
        autoPhoneClusterMinSpeechSamples = $AutoPhoneClusterMinSpeechSamples
        autoPhoneClusterBandLowHz = $AutoPhoneClusterBandLowHz
        autoPhoneClusterBandHighHz = $AutoPhoneClusterBandHighHz
        maxPeaks = $MaxPeaks
        peakMergeHz = $PeakMergeHz
        minPeakSnrDb = $MinPeakSnrDb
        settleMs = $SettleMs
        allowRetune = [bool]$AllowRetune
        skipCurrentVfo = [bool]$SkipCurrentVfo
        outputs = @(
            "JSON report with frontend peak candidates, per-window watch summaries, and best mixed weak+strong run",
            "Per-window watch-dsp-live-diagnostics JSON and JSONL evidence",
            "Optional RX-only VFO retune/restore evidence when -AllowRetune is supplied"
        )
        safety = [ordered]@{
            txSafe = $true
            txEndpointsTouched = $false
            retuneRequiresAllowRetune = $true
            restoreOriginalVfo = $true
            notes = @(
                "Without -AllowRetune the tool only captures the current VFO and lists candidate frontend peaks/operator frequencies.",
                "With -AllowRetune the tool posts only RX tuning endpoints, waits for RX settling, delegates evidence capture to watch-dsp-live-diagnostics, then restores the original VFO and radio LO in a verified finally block.",
                "Frontend peak and operator-style retunes are passband-centered for LSB/USB when mode/filter data is available, then snapped to -TuneStepHz; exact signal targets remain in exactFrequencyHz, exactRetuneVfoHz, peakToRetunedVfoOffsetHz, and tuneSnapDeltaHz fields.",
                "Frontend peak retunes are bounded by -PeakRetuneLowHz/-PeakRetuneHighHz when supplied, otherwise by the candidate-frequency span plus -PeakRetunePaddingHz when operator/cluster candidates exist.",
                "The tool does not approve DSP default changes; it only hunts for the missing G2 mixed weak+strong NR5/SPNR evidence window."
            )
        }
        example = "powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-g2-rx-peak-hunt.ps1 -BaseUrl auto -SamplesPerWindow 24 -IntervalMs 250 -MaxPeaks 6"
        retuneExample = "powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-g2-rx-peak-hunt.ps1 -BaseUrl auto -AllowRetune -StopOnReady -SamplesPerWindow 24 -IntervalMs 250 -MaxPeaks 6"
        operatorFrequencyExample = "powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-g2-rx-peak-hunt.ps1 -BaseUrl auto -Mode USB -AllowRetune -StopOnReady -CandidateMHz 14.260,14.243,14.287,14.152,14.227,14.240,14.270,14.277,14.300 -OperatorTrendMaxCandidates 8 -PassCount 2 -PassDelaySec 5"
        autoPhoneClusterExample = "powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-g2-rx-peak-hunt.ps1 -BaseUrl auto -AllowRetune -StopOnReady -AutoPhoneCluster -MaxPeaks 8"
        desktopExample = "powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-g2-rx-peak-hunt.ps1 -BaseUrl https://localhost:6443 -SkipCertificateCheck -AllowRetune -StopOnReady"
    } | ConvertTo-Json -Depth 16
    exit 0
}

$startedUtc = [DateTimeOffset]::UtcNow
$safeLabel = ConvertTo-SafeName $Label
if ([string]::IsNullOrWhiteSpace($safeLabel)) {
    $safeLabel = "g2-rx-peak-hunt"
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "captures\dsp-live-peak-hunt"
}
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)

$stamp = $startedUtc.ToString("yyyyMMddTHHmmssfffZ")
$captureDir = Join-Path $OutputRoot "$stamp-$safeLabel"
New-Item -ItemType Directory -Force -Path $captureDir | Out-Null

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path $captureDir "g2-rx-peak-hunt-report.json"
}
$ReportPath = [System.IO.Path]::GetFullPath($ReportPath)

$portableBundleRoot = Get-BundleRootFromArtifactsPath -Path $ReportPath
$bundleRelativePaths = -not [string]::IsNullOrWhiteSpace($portableBundleRoot)

try {
    $hardware = Invoke-JsonGet -Url "$base/api/radio/diagnostics" -RequestTimeoutSec $TimeoutSec -SkipCertificate:$SkipCertificateCheck
    $initialState = Invoke-JsonGet -Url "$base/api/state" -RequestTimeoutSec $TimeoutSec -SkipCertificate:$SkipCertificateCheck
    $originalVfo = Get-NullableLongValue (Get-JsonValue $hardware "vfoHz")
    if ($null -eq $originalVfo -or $originalVfo -le 0) {
        throw "Cannot determine original VFO from /api/radio/diagnostics."
    }
    $originalMode = [string](Get-JsonValue $hardware "mode")
    $originalRadioLo = Get-NullableLongValue (Get-JsonValue $initialState "radioLoHz")
    $originalEndpoint = [string](Get-JsonValue $hardware "endpoint")
    $originalConnectSampleRate = Get-IntValue (Get-JsonValue $hardware "sampleRate")
    if ($originalConnectSampleRate -le 0) {
        $originalConnectSampleRate = 192000
    }
    if (-not [string]::IsNullOrWhiteSpace($targetMode) -and
        -not [string]::Equals($targetMode, $originalMode, [StringComparison]::OrdinalIgnoreCase)) {
        Invoke-JsonPost -Url "$base/api/mode" -Body @{ mode = $targetMode; receiver = 0 } -RequestTimeoutSec $TimeoutSec -SkipCertificate:$SkipCertificateCheck | Out-Null
        Start-Sleep -Milliseconds ([Math]::Min(1000, [Math]::Max(0, $SettleMs)))
    }

    $retuneReferenceState = $initialState
    try {
        $retuneReferenceState = Invoke-JsonGet -Url "$base/api/state" -RequestTimeoutSec $TimeoutSec -SkipCertificate:$SkipCertificateCheck
    }
    catch {
        $retuneReferenceState = $initialState
    }

    $frontendPeakRetuneMode = if ([string]::IsNullOrWhiteSpace($targetMode)) { $originalMode } else { $targetMode }
    $frontendPeakRetuneFilterLowHz = Get-NullableLongValue (Get-JsonValue $retuneReferenceState "filterLowHz")
    $frontendPeakRetuneFilterHighHz = Get-NullableLongValue (Get-JsonValue $retuneReferenceState "filterHighHz")
    $frontendPeakRetuneTargetOffsetHz = Get-FrontendPeakRetuneTargetOffsetHz `
        -Mode $frontendPeakRetuneMode `
        -FilterLowHz $frontendPeakRetuneFilterLowHz `
        -FilterHighHz $frontendPeakRetuneFilterHighHz
}
catch {
    $initializationError = $_.Exception.Message
    $completedUtc = [DateTimeOffset]::UtcNow
    $reportObject = [ordered]@{
        schemaVersion = 1
        tool = "run-dsp-g2-rx-peak-hunt"
        generatedUtc = $completedUtc.ToString("o")
        startedUtc = $startedUtc.ToString("o")
        completedUtc = $completedUtc.ToString("o")
        durationMs = [int]($completedUtc - $startedUtc).TotalMilliseconds
        ok = $false
        evidenceAcceptanceStatus = "scan-not-ready"
        evidenceAcceptanceReady = $false
        evidenceAcceptanceReason = "The scan could not start because /api/radio/diagnostics was unavailable or missing VFO data: $initializationError."
        evidenceAcceptanceScope = "g2-rx-peak-hunt"
        wdspV2GraduationReady = $false
        wdspV2GraduationReason = "A G2 peak-hunt report is scouting evidence only; WDSP v2 graduation still requires matrix comparisons, offline fixture coverage, Thetis/current-Zeus parity review, on-air approval, and cross-radio validation."
        scanError = $initializationError
        requestedBaseUrl = $requestedBaseUrl
        baseUrl = $base
        baseUrlAutoDiscoverRequested = $baseUrlAutoDiscoverRequested
        baseUrlAutoDiscovered = $baseUrlAutoDiscovered
        baseUrlAutoDiscoverError = $baseUrlAutoDiscoverError
        baseUrlProbeResults = @($baseUrlProbeResults)
        outputDir = ConvertTo-PortableBundlePath -Root $portableBundleRoot -Path $captureDir
        bundleRelativePaths = [bool]$bundleRelativePaths
        targetMode = if ([string]::IsNullOrWhiteSpace($targetMode)) { $null } else { $targetMode }
        label = $Label
        comparisonId = $ComparisonId
        allowRetune = [bool]$AllowRetune
        skipCurrentVfo = [bool]$SkipCurrentVfo
        stopOnReady = [bool]$StopOnReady
        samplesPerWindow = $SamplesPerWindow
        intervalMs = $IntervalMs
        windowsPerPeak = $WindowsPerPeak
        passCount = $PassCount
        passDelaySec = $PassDelaySec
        tuneStepHz = $effectiveTuneStepHz
        frontendPeakRetuneMode = if ([string]::IsNullOrWhiteSpace($frontendPeakRetuneMode)) { $null } else { $frontendPeakRetuneMode }
        frontendPeakRetuneFilterLowHz = $frontendPeakRetuneFilterLowHz
        frontendPeakRetuneFilterHighHz = $frontendPeakRetuneFilterHighHz
        frontendPeakRetuneTargetOffsetHz = $frontendPeakRetuneTargetOffsetHz
        peakRetuneSpan = $peakRetuneSpanForPlan
        completedPassCount = 0
        scanPassCount = 0
        candidateFrequencyHz = @($operatorCandidateFrequencyHzForPlan)
        operatorTrendMaxCandidates = $OperatorTrendMaxCandidates
        operatorTrendCandidateFrequencyHz = @($operatorTrendCandidateFrequencyHzForPlan)
        operatorTrendCandidateCount = $operatorTrendCandidatesForPlan.Count
        operatorTrendCandidates = @($operatorTrendCandidatesForPlan)
        autoPhoneCluster = [bool]$AutoPhoneCluster
        autoPhoneClusterSearchRoot = $AutoPhoneClusterSearchRoot
        autoPhoneClusterMaxCandidates = $AutoPhoneClusterMaxCandidates
        autoPhoneClusterLookbackHours = $AutoPhoneClusterLookbackHours
        autoPhoneClusterMinSpeechSamples = $AutoPhoneClusterMinSpeechSamples
        autoPhoneClusterBandLowHz = $AutoPhoneClusterBandLowHz
        autoPhoneClusterBandHighHz = $AutoPhoneClusterBandHighHz
        autoPhoneClusterCandidateFrequencyHz = @()
        autoPhoneClusterCandidateCount = 0
        autoPhoneClusterExactCandidateCount = 0
        autoPhoneClusterNeighborCandidateCount = 0
        operatorCandidateCount = $operatorCandidatesForPlan.Count
        maxPeaks = $MaxPeaks
        peakMergeHz = $PeakMergeHz
        minPeakSnrDb = $MinPeakSnrDb
        settleMs = $SettleMs
        safety = [ordered]@{
            rxOnly = $true
            txEndpointsTouched = $false
            vfoRetuneRequiresAllowRetune = $true
            temporaryModeRequested = -not [string]::IsNullOrWhiteSpace($targetMode)
            targetMode = if ([string]::IsNullOrWhiteSpace($targetMode)) { $null } else { $targetMode }
            originalVfoRestoreAttempted = $false
            originalVfoRestored = $false
            originalModeRestoreAttempted = $false
            originalModeRestored = $false
            restoreError = "Original VFO unavailable; restore was not attempted."
        }
        hardware = [ordered]@{
            connectionStatus = ""
            endpoint = ""
            effectiveBoard = ""
            orionMkIIVariant = ""
            originalVfoHz = $null
            restoredVfoHz = $null
            originalRadioLoHz = $null
            restoredRadioLoHz = $null
            originalMode = ""
            restoredMode = ""
            mode = ""
            sampleRate = 0
        }
        liveDiagnostics = [ordered]@{
            status = ""
            readyForLiveBenchmark = $false
            wdspActive = $false
            wdspNativeLoadable = $false
            requestedNrMode = ""
            effectiveNrMode = ""
            readyForNr5Tuning = $false
            frontendSceneFresh = $false
        }
        frontendScene = [ordered]@{
            status = ""
            fresh = $false
            signalProfile = ""
            maxSnrDb = $null
            coherentMaxSnrDb = $null
            topPeakCount = 0
        }
        operatorCandidates = @($operatorCandidatesForPlan)
        peakCandidates = @()
        rejectedPeakCandidates = @()
        rejectedPeakCandidateCount = 0
        rejectedPeakCandidateReasonCounts = [ordered]@{}
        rejectedRetuneCandidates = @()
        rejectedRetuneCandidateCount = 0
        rejectedRetuneCandidateReasonCounts = [ordered]@{}
        plannedRunCount = 0
        actualRunCount = 0
        failedRunCount = 0
        mixedWeakStrongReady = $false
        mixedWeakStrongReadyRunCount = 0
        weakInputSampleCount = 0
        strongInputSampleCount = 0
        speechQualifiedWeakInputSampleCount = 0
        speechQualifiedStrongInputSampleCount = 0
        passbandQualifiedWeakInputSampleCount = 0
        passbandQualifiedStrongInputSampleCount = 0
        frontendNearPassbandSampleCount = 0
        candidateWeakLossSampleCount = 0
        hotMakeupSampleCount = 0
        hardBlockerSampleCount = 0
        agcPumpingRiskRunCount = 0
        bestRun = $null
        retuneAttempts = @()
        scanPasses = @()
        stoppedEarly = $false
        runs = @()
        recommendations = @(
            "The scan could not start because /api/radio/diagnostics was unavailable or missing VFO data: $initializationError.",
            "No VFO restore was attempted because the original VFO could not be determined."
        )
    }

    Write-JsonFile -Path $ReportPath -Value $reportObject
    if ($JsonOnly) {
        $reportObject | ConvertTo-Json -Depth 64
    }
    else {
        Write-Host "G2 RX peak hunt report: $ReportPath"
        Write-Host "Scan did not start: $initializationError"
    }

    if (-not $ContinueOnError) {
        exit 3
    }
    exit 0
}

$operatorCandidates = @(Get-OperatorFrequencyCandidates -FrequencyHz $CandidateFrequencyHz -FrequencyMHz $CandidateMHz -OriginalVfo $originalVfo)
$operatorCandidateFrequencyHz = @($operatorCandidates | ForEach-Object { [long]$_.frequencyHz })
$operatorTrendCandidates = @(Get-OperatorTrendNeighborCandidates `
        -OperatorCandidates $operatorCandidates `
        -OriginalVfo $originalVfo `
        -BandLowHz $AutoPhoneClusterBandLowHz `
        -BandHighHz $AutoPhoneClusterBandHighHz `
        -MaxCandidates $OperatorTrendMaxCandidates)
$operatorTrendCandidateFrequencyHz = @($operatorTrendCandidates | ForEach-Object { [long]$_.frequencyHz })
$autoPhoneClusterCandidates = @()
if ($AutoPhoneCluster) {
    $autoPhoneClusterCandidates = @(Get-AutoPhoneClusterCandidates `
            -SearchRoot $AutoPhoneClusterSearchRoot `
            -OriginalVfo $originalVfo `
            -BandLowHz $AutoPhoneClusterBandLowHz `
            -BandHighHz $AutoPhoneClusterBandHighHz `
            -MaxCandidates $AutoPhoneClusterMaxCandidates `
            -LookbackHours $AutoPhoneClusterLookbackHours `
            -MinSpeechSamples $AutoPhoneClusterMinSpeechSamples)
}
$autoPhoneClusterCandidateFrequencyHz = @($autoPhoneClusterCandidates | ForEach-Object { [long]$_.frequencyHz })
$autoPhoneClusterExactCandidateCount = @($autoPhoneClusterCandidates | Where-Object { [string](Get-JsonValue $_ "source") -eq "recent-phone-cluster" }).Count
$autoPhoneClusterNeighborCandidateCount = @($autoPhoneClusterCandidates | Where-Object { [string](Get-JsonValue $_ "source") -eq "recent-phone-cluster-neighbor" }).Count
$peakRetuneSpan = Get-PeakRetuneSpan `
    -SeedCandidates @($operatorCandidates + $autoPhoneClusterCandidates + $operatorTrendCandidates) `
    -ExplicitLowHz $PeakRetuneLowHz `
    -ExplicitHighHz $PeakRetuneHighHz `
    -PaddingHz $effectivePeakRetunePaddingHz `
    -FallbackLowHz $AutoPhoneClusterBandLowHz `
    -FallbackHighHz $AutoPhoneClusterBandHighHz `
    -UseFallback ([bool]$AutoPhoneCluster)
$latestScene = $null
$latestLive = $null
$allPeakCandidates = New-Object System.Collections.Generic.List[object]
$allRejectedPeakCandidates = New-Object System.Collections.Generic.List[object]
$allRejectedRetuneCandidates = New-Object System.Collections.Generic.List[object]
$scanPasses = New-Object System.Collections.Generic.List[object]
$runs = New-Object System.Collections.Generic.List[object]
$retuneAttempts = New-Object System.Collections.Generic.List[object]
$restoredVfo = $null
$restoredRadioLo = $null
$restoredMode = $null
$restoreError = $null
$restoreReconnectAttempted = $false
$restoreReconnectSucceeded = $false
$restoreReconnectError = $null
$stoppedEarly = $false
$completedPassCount = 0
$plannedRunCount = 0
$scanError = $null

try {
    for ($pass = 1; $pass -le $PassCount; $pass++) {
        $passStartedUtc = [DateTimeOffset]::UtcNow
        $passStoppedEarly = $false
        $passRestoreError = $null
        $passRestoredOriginalBeforeRefresh = $false

        if ($AllowRetune -and $pass -gt 1) {
            try {
                $passRestore = Restore-OriginalTuning `
                    -BaseUrl $base `
                    -OriginalVfo $originalVfo `
                    -OriginalRadioLo $originalRadioLo `
                    -OriginalMode $originalMode `
                    -OriginalEndpoint $originalEndpoint `
                    -OriginalConnectSampleRate $originalConnectSampleRate `
                    -RequestTimeoutSec $TimeoutSec `
                    -SettleMs ([Math]::Min(1000, [Math]::Max(0, $SettleMs))) `
                    -MaxAttempts 2 `
                    -SkipCertificate:$SkipCertificateCheck
                if (-not (Test-Truthy $passRestore.ok)) {
                    throw "state did not return to original VFO/LO before pass refresh"
                }
                $passRestoredOriginalBeforeRefresh = $true
            }
            catch {
                $passRestoreError = $_.Exception.Message
                if (-not $ContinueOnError) {
                    throw
                }
            }
        }

        $scene = Invoke-JsonGet -Url "$base/api/radio/diagnostics/dsp-scene" -RequestTimeoutSec $TimeoutSec -SkipCertificate:$SkipCertificateCheck
        $live = Invoke-JsonGet -Url "$base/api/dsp/live-diagnostics" -RequestTimeoutSec $TimeoutSec -SkipCertificate:$SkipCertificateCheck
        $latestScene = $scene
        $latestLive = $live

        $scenePeaks = @(Get-JsonArray $scene "topPeaks")
        $passRejectedPeakCandidates = New-Object System.Collections.Generic.List[object]
        $passPeakCandidates = @(Select-PeakCandidates -Peaks $scenePeaks -Limit $MaxPeaks -MergeHz $PeakMergeHz -MinimumSnrDb $MinPeakSnrDb -OriginalVfo $originalVfo -StepHz $effectiveTuneStepHz -RetuneLowHz $peakRetuneSpan.lowHz -RetuneHighHz $peakRetuneSpan.highHz -RetuneSpanSource $peakRetuneSpan.source -RetuneTargetOffsetHz $frontendPeakRetuneTargetOffsetHz -RejectedCandidates $passRejectedPeakCandidates)
        $passPeakCandidatesForReport = New-Object System.Collections.Generic.List[object]
        foreach ($candidate in $passPeakCandidates) {
            $candidateForReport = Copy-PeakCandidateForPass -Candidate $candidate -Pass $pass
            if ($null -ne $candidateForReport) {
                $passPeakCandidatesForReport.Add($candidateForReport) | Out-Null
                $allPeakCandidates.Add($candidateForReport) | Out-Null
            }
        }
        $passRejectedPeakCandidatesForReport = New-Object System.Collections.Generic.List[object]
        foreach ($candidate in @($passRejectedPeakCandidates.ToArray())) {
            $candidateForReport = Copy-RejectedPeakCandidateForPass -Candidate $candidate -Pass $pass
            if ($null -ne $candidateForReport) {
                $passRejectedPeakCandidatesForReport.Add($candidateForReport) | Out-Null
                $allRejectedPeakCandidates.Add($candidateForReport) | Out-Null
            }
        }

        $candidates = New-Object System.Collections.Generic.List[object]
        $passRejectedRetuneCandidates = New-Object System.Collections.Generic.List[object]
        if (-not $SkipCurrentVfo) {
            $candidates.Add([pscustomobject][ordered]@{
                rank = 0
                source = "current-vfo"
                frequencyHz = $originalVfo
                offsetHz = 0
                snrDb = $null
                dbfs = $null
                confidence = $null
                coherent = $null
            }) | Out-Null
        }

        if ($AllowRetune) {
            foreach ($candidate in $operatorCandidates) {
                $retuneCandidate = ConvertTo-RetuneGeometryCandidate -Candidate $candidate -OriginalVfo $originalVfo -StepHz $effectiveTuneStepHz -RetuneTargetOffsetHz $frontendPeakRetuneTargetOffsetHz -CenteredRetuneReason "retune-to-center-operator-frequency"
                Add-RetuneCandidateIfAdmitted -Candidates $candidates -Candidate $retuneCandidate -MergeHz $PeakMergeHz -StepHz $effectiveTuneStepHz -OriginalVfo $originalVfo -RetuneLowHz $peakRetuneSpan.lowHz -RetuneHighHz $peakRetuneSpan.highHz -RetuneSpanSource $peakRetuneSpan.source -RejectedCandidates $passRejectedRetuneCandidates | Out-Null
            }
            foreach ($candidate in $autoPhoneClusterCandidates) {
                $retuneCandidate = ConvertTo-RetuneGeometryCandidate -Candidate $candidate -OriginalVfo $originalVfo -StepHz $effectiveTuneStepHz -RetuneTargetOffsetHz $frontendPeakRetuneTargetOffsetHz -CenteredRetuneReason "retune-to-center-phone-cluster"
                Add-RetuneCandidateIfAdmitted -Candidates $candidates -Candidate $retuneCandidate -MergeHz $PeakMergeHz -StepHz $effectiveTuneStepHz -OriginalVfo $originalVfo -RetuneLowHz $peakRetuneSpan.lowHz -RetuneHighHz $peakRetuneSpan.highHz -RetuneSpanSource $peakRetuneSpan.source -RejectedCandidates $passRejectedRetuneCandidates | Out-Null
            }
            foreach ($candidate in $operatorTrendCandidates) {
                $retuneCandidate = ConvertTo-RetuneGeometryCandidate -Candidate $candidate -OriginalVfo $originalVfo -StepHz $effectiveTuneStepHz -RetuneTargetOffsetHz $frontendPeakRetuneTargetOffsetHz -CenteredRetuneReason "retune-to-center-operator-trend"
                Add-RetuneCandidateIfAdmitted -Candidates $candidates -Candidate $retuneCandidate -MergeHz $PeakMergeHz -StepHz $effectiveTuneStepHz -OriginalVfo $originalVfo -RetuneLowHz $peakRetuneSpan.lowHz -RetuneHighHz $peakRetuneSpan.highHz -RetuneSpanSource $peakRetuneSpan.source -RejectedCandidates $passRejectedRetuneCandidates | Out-Null
            }
            foreach ($candidate in $passPeakCandidates) {
                Add-RetuneCandidateIfAdmitted -Candidates $candidates -Candidate $candidate -MergeHz $PeakMergeHz -StepHz $effectiveTuneStepHz -OriginalVfo $originalVfo -RetuneLowHz $peakRetuneSpan.lowHz -RetuneHighHz $peakRetuneSpan.highHz -RetuneSpanSource $peakRetuneSpan.source -RejectedCandidates $passRejectedRetuneCandidates | Out-Null
            }
        }

        $passRejectedRetuneCandidatesForReport = New-Object System.Collections.Generic.List[object]
        foreach ($candidate in @($passRejectedRetuneCandidates.ToArray())) {
            $candidateForReport = Copy-RejectedRetuneCandidateForPass -Candidate $candidate -Pass $pass
            if ($null -ne $candidateForReport) {
                $passRejectedRetuneCandidatesForReport.Add($candidateForReport) | Out-Null
                $allRejectedRetuneCandidates.Add($candidateForReport) | Out-Null
            }
        }

        $plannedForPass = $candidates.Count * $WindowsPerPeak
        $plannedRunCount += $plannedForPass
        $passSummary = [ordered]@{
            pass = $pass
            startedUtc = $passStartedUtc.ToString("o")
            completedUtc = $null
            restoreOriginalBeforeRefresh = [bool]($AllowRetune -and $pass -gt 1)
            restoredOriginalBeforeRefresh = [bool]$passRestoredOriginalBeforeRefresh
            restoreOriginalBeforeRefreshError = $passRestoreError
            liveDiagnostics = [ordered]@{
                status = [string](Get-JsonValue $live "status")
                effectiveNrMode = [string](Get-JsonValue $live "effectiveNrMode")
                readyForNr5Tuning = Test-Truthy (Get-JsonValue $live "readyForNr5Tuning")
                frontendSceneFresh = Test-Truthy (Get-JsonValue $live "frontendSceneFresh")
            }
            frontendScene = [ordered]@{
                status = [string](Get-JsonValue $scene "status")
                fresh = Test-Truthy (Get-JsonValue $scene "fresh")
                signalProfile = [string](Get-JsonValue $scene "signalProfile")
                topPeakCount = $scenePeaks.Count
                maxSnrDb = Get-NullableDoubleValue (Get-JsonValue $scene "maxSnrDb")
                coherentMaxSnrDb = Get-NullableDoubleValue (Get-JsonValue $scene "coherentMaxSnrDb")
            }
            operatorCandidateCount = $operatorCandidates.Count
            operatorCaptureEligible = [bool]$AllowRetune
            operatorTrendCandidateCount = $operatorTrendCandidates.Count
            autoPhoneCluster = [bool]$AutoPhoneCluster
            autoPhoneClusterCandidateCount = $autoPhoneClusterCandidates.Count
            peakRetuneSpan = $peakRetuneSpan
            peakCandidateCount = $passPeakCandidatesForReport.Count
            rejectedPeakCandidateCount = $passRejectedPeakCandidatesForReport.Count
            rejectedRetuneCandidateCount = $passRejectedRetuneCandidatesForReport.Count
            candidateCount = $candidates.Count
            plannedRunCount = $plannedForPass
            stoppedEarly = $false
        }

        $passDir = Join-Path $captureDir ("pass-{0:00}" -f $pass)
        New-Item -ItemType Directory -Force -Path $passDir | Out-Null
        $candidateOrdinal = 0
        foreach ($candidate in @($candidates.ToArray())) {
            $candidateOrdinal++
            $frequencyHz = [long]$candidate.frequencyHz
            $candidateSafeName = ConvertTo-SafeName ("{0}-{1}" -f $candidate.source, $frequencyHz)
            $candidateDirName = $candidateSafeName
            $candidateDir = Join-Path $passDir $candidateSafeName
            $candidateProbePath = Join-Path (Join-Path $candidateDir "window-01") "live-diagnostics-watch.jsonl"
            if (Test-ArtifactPathTooLong $candidateProbePath) {
                $candidateDirName = "c{0:00}-{1}" -f $candidateOrdinal, $frequencyHz
                $candidateDir = Join-Path $passDir $candidateDirName
            }
            New-Item -ItemType Directory -Force -Path $candidateDir | Out-Null

            if (-not [string]::Equals([string]$candidate.source, "current-vfo", [StringComparison]::OrdinalIgnoreCase)) {
                $retuneUtc = [DateTimeOffset]::UtcNow
                $retuneRecord = [ordered]@{
                    pass = $pass
                    frequencyHz = $frequencyHz
                    exactFrequencyHz = Get-NullableLongValue (Get-JsonValue $candidate "exactFrequencyHz")
                    tuneStepHz = Get-IntValue (Get-JsonValue $candidate "tuningStepHz")
                    tuneSnapDeltaHz = Get-NullableLongValue (Get-JsonValue $candidate "tuneSnapDeltaHz")
                    retuneTargetOffsetHz = Get-NullableDoubleValue (Get-JsonValue $candidate "retuneTargetOffsetHz")
                    exactRetuneVfoHz = Get-NullableLongValue (Get-JsonValue $candidate "exactRetuneVfoHz")
                    peakToRetunedVfoOffsetHz = Get-NullableLongValue (Get-JsonValue $candidate "peakToRetunedVfoOffsetHz")
                    retuneReason = [string](Get-JsonValue $candidate "retuneReason")
                    source = [string]$candidate.source
                    startedUtc = $retuneUtc.ToString("o")
                    ok = $false
                    error = $null
                }
                try {
                    Invoke-JsonPost -Url "$base/api/vfo" -Body @{ hz = $frequencyHz } -RequestTimeoutSec $TimeoutSec -SkipCertificate:$SkipCertificateCheck | Out-Null
                    Start-Sleep -Milliseconds $SettleMs
                    $retuneRecord.ok = $true
                }
                catch {
                    $retuneRecord.error = $_.Exception.Message
                    if (-not $ContinueOnError) {
                        throw
                    }
                }
                finally {
                    $retuneRecord.completedUtc = ([DateTimeOffset]::UtcNow).ToString("o")
                    $retuneAttempts.Add([pscustomobject]$retuneRecord) | Out-Null
                }

                if (-not $retuneRecord.ok) {
                    continue
                }
            }

            for ($window = 1; $window -le $WindowsPerPeak; $window++) {
                $windowDirName = "window-{0:00}" -f $window
                $windowDir = Join-Path $candidateDir $windowDirName
                if (Test-ArtifactPathTooLong (Join-Path $windowDir "live-diagnostics-watch.jsonl")) {
                    $windowDirName = "w{0:00}" -f $window
                    $windowDir = Join-Path $candidateDir $windowDirName
                }
                New-Item -ItemType Directory -Force -Path $windowDir | Out-Null

                $watchReport = Join-Path $windowDir "live-diagnostics-watch.json"
                $watchJsonl = Join-Path $windowDir "live-diagnostics-watch.jsonl"
                if (Test-ArtifactPathTooLong $watchJsonl) {
                    $watchReport = Join-Path $windowDir "watch.json"
                    $watchJsonl = Join-Path $windowDir "trace.jsonl"
                }
                $artifactPathCompacted = -not [string]::Equals($candidateDirName, $candidateSafeName, [StringComparison]::OrdinalIgnoreCase) -or
                    -not [string]::Equals($windowDirName, ("window-{0:00}" -f $window), [StringComparison]::OrdinalIgnoreCase) -or
                    [string]::Equals([System.IO.Path]::GetFileName($watchReport), "watch.json", [StringComparison]::OrdinalIgnoreCase)
                $portableWatchReport = ConvertTo-PortableBundlePath -Root $portableBundleRoot -Path $watchReport
                $portableWatchJsonl = ConvertTo-PortableBundlePath -Root $portableBundleRoot -Path $watchJsonl
                $windowLabel = "{0}-pass-{1:00}-{2}-{3:00}" -f $safeLabel, $pass, $candidateSafeName, $window
                $watch = Invoke-WatchWindow `
                    -ScriptPath $resolvedWatchScript `
                    -Base $base `
                    -ScenarioId "g2-rx-peak-hunt" `
                    -Comparison $ComparisonId `
                    -Label $windowLabel `
                    -Report $watchReport `
                    -Jsonl $watchJsonl `
                    -SampleCount $SamplesPerWindow `
                    -DelayMs $IntervalMs `
                    -RequestTimeoutSec $TimeoutSec `
                    -SkipCertificate:$SkipCertificateCheck

                if (-not (Test-Truthy $watch.ok)) {
                    $runs.Add([pscustomobject][ordered]@{
                        ok = $false
                        pass = $pass
                        frequencyHz = $frequencyHz
                        exactCandidateFrequencyHz = Get-NullableLongValue (Get-JsonValue $candidate "exactFrequencyHz")
                        candidateSource = [string]$candidate.source
                        candidateTuneStepHz = Get-IntValue (Get-JsonValue $candidate "tuningStepHz")
                        candidateTuneSnapDeltaHz = Get-NullableLongValue (Get-JsonValue $candidate "tuneSnapDeltaHz")
                        candidateRetuneTargetOffsetHz = Get-NullableDoubleValue (Get-JsonValue $candidate "retuneTargetOffsetHz")
                        candidateExactRetuneVfoHz = Get-NullableLongValue (Get-JsonValue $candidate "exactRetuneVfoHz")
                        candidatePeakToRetunedVfoOffsetHz = Get-NullableLongValue (Get-JsonValue $candidate "peakToRetunedVfoOffsetHz")
                        candidateRetuneReason = [string](Get-JsonValue $candidate "retuneReason")
                        window = $window
                        reportPath = $portableWatchReport
                        jsonlPath = $portableWatchJsonl
                        artifactPathCompacted = [bool]$artifactPathCompacted
                        exitCode = $watch.exitCode
                        error = $watch.error
                        score = 0.0
                    }) | Out-Null

                    if (-not $ContinueOnError) {
                        throw "watch-dsp-live-diagnostics failed for $frequencyHz Hz pass ${pass} window ${window}: $($watch.error)"
                    }

                    continue
                }

                $report = $watch.report
                $weak = Get-JsonValue $report "nr5WeakSignalWatch"
                $weakInput = Get-IntValue (Get-JsonValue $weak "weakInputSampleCount")
                $strongInput = Get-IntValue (Get-JsonValue $weak "strongInputSampleCount")
                $nearStrongInput = Get-IntValue (Get-JsonValue $weak "nearStrongInputSampleCount")
                $weakOutputGap = Get-NullableDoubleValue (Get-JsonValue $weak "weakStrongOutputGapDb")
                $weakFinalGap = Get-NullableDoubleValue (Get-JsonValue $weak "weakStrongFinalAudioGapDb")
                $speechQualifiedWeakInput = Get-IntValue (Get-JsonValue $weak "speechQualifiedWeakInputSampleCount")
                $speechQualifiedStrongInput = Get-IntValue (Get-JsonValue $weak "speechQualifiedStrongInputSampleCount")
                $speechQualifiedNearStrongInput = Get-IntValue (Get-JsonValue $weak "speechQualifiedNearStrongInputSampleCount")
                $passbandQualifiedWeakInput = Get-IntValue (Get-JsonValue $weak "passbandQualifiedWeakInputSampleCount")
                $passbandQualifiedStrongInput = Get-IntValue (Get-JsonValue $weak "passbandQualifiedStrongInputSampleCount")
                $passbandQualifiedNearStrongInput = Get-IntValue (Get-JsonValue $weak "passbandQualifiedNearStrongInputSampleCount")
                $frontendTopPeakWatch = Get-JsonValue $report "frontendTopPeakWatch"
                $frontendTopPeakSampleCount = Get-IntValue (Get-JsonValue $frontendTopPeakWatch "sampleCount")
                $frontendNearPassbandSampleCount = Get-IntValue (Get-JsonValue $frontendTopPeakWatch "nearPassbandSampleCount")
                $readyTrace = Test-Truthy (Get-JsonValue $report "readyForBenchmarkTrace")
                $mixedReady = Test-Truthy (Get-JsonValue $weak "mixedWeakStrongEvidenceReady")
                $mixedFocus = Get-JsonValue $weak "mixedWeakStrongTuningFocus"
                $mixedFocusAction = Get-TrimmedStringValue (Get-JsonValue $mixedFocus "preferredAction")
                $mixedFocusStatus = Get-TrimmedStringValue (Get-JsonValue $mixedFocus "status")
                $mixedOutputGapDirection = Get-TrimmedStringValue (Get-JsonValue $mixedFocus "outputGapDirection")
                $mixedFinalAudioGapDirection = Get-TrimmedStringValue (Get-JsonValue $mixedFocus "finalAudioGapDirection")
                $mixedOutputGapExcess = Get-NullableDoubleValue (Get-JsonValue $mixedFocus "outputGapExcessDb")
                $mixedFinalAudioGapExcess = Get-NullableDoubleValue (Get-JsonValue $mixedFocus "finalAudioGapExcessDb")
                $mixedWeakOutputLiftNeeded = Get-NullableDoubleValue (Get-JsonValue $mixedFocus "weakOutputLiftNeededDb")
                $mixedWeakOutputTrimNeeded = Get-NullableDoubleValue (Get-JsonValue $mixedFocus "weakOutputTrimNeededDb")
                $mixedWeakFinalAudioLiftNeeded = Get-NullableDoubleValue (Get-JsonValue $mixedFocus "weakFinalAudioLiftNeededDb")
                $mixedWeakFinalAudioTrimNeeded = Get-NullableDoubleValue (Get-JsonValue $mixedFocus "weakFinalAudioTrimNeededDb")
                $mixedTopWeakInputCount = @(Get-JsonArray $mixedFocus "topWeakInputs").Count
                $mixedTopStrongInputCount = @(Get-JsonArray $mixedFocus "topStrongInputs").Count
                $mixedTopSpeechQualifiedWeakInputCount = @(Get-JsonArray $mixedFocus "topSpeechQualifiedWeakInputs").Count
                $mixedTopSpeechQualifiedStrongInputCount = @(Get-JsonArray $mixedFocus "topSpeechQualifiedStrongInputs").Count
                $mixedTopPassbandQualifiedWeakInputCount = @(Get-JsonArray $mixedFocus "topPassbandQualifiedWeakInputs").Count
                $mixedTopPassbandQualifiedStrongInputCount = @(Get-JsonArray $mixedFocus "topPassbandQualifiedStrongInputs").Count
                $score = Get-HuntScore `
                    -WeakInputSampleCount $weakInput `
                    -StrongInputSampleCount $strongInput `
                    -NearStrongInputSampleCount $nearStrongInput `
                    -WeakStrongOutputGapDb $weakOutputGap `
                    -WeakStrongFinalAudioGapDb $weakFinalGap `
                    -MixedWeakStrongEvidenceReady $mixedReady `
                    -ReadyForBenchmarkTrace $readyTrace `
                    -NearPassbandSampleCount $frontendNearPassbandSampleCount `
                    -ExpectedSampleCount $SamplesPerWindow

                $agc = Get-JsonValue $report "agcStabilityWatch"
                $leveler = Get-JsonValue $report "rxAudioLevelerWatch"
                $run = [ordered]@{
                    ok = $true
                    pass = $pass
                    frequencyHz = $frequencyHz
                    candidateSource = [string]$candidate.source
                    candidateRank = [int]$candidate.rank
                    candidateSnrDb = Get-NullableDoubleValue $candidate.snrDb
                    candidateOffsetHz = Get-NullableLongValue $candidate.offsetHz
                    exactCandidateFrequencyHz = Get-NullableLongValue (Get-JsonValue $candidate "exactFrequencyHz")
                    exactCandidateOffsetHz = Get-NullableLongValue (Get-JsonValue $candidate "exactOffsetHz")
                    candidateTuneStepHz = Get-IntValue (Get-JsonValue $candidate "tuningStepHz")
                    candidateTuneSnapDeltaHz = Get-NullableLongValue (Get-JsonValue $candidate "tuneSnapDeltaHz")
                    candidateRetuneTargetOffsetHz = Get-NullableDoubleValue (Get-JsonValue $candidate "retuneTargetOffsetHz")
                    candidateExactRetuneVfoHz = Get-NullableLongValue (Get-JsonValue $candidate "exactRetuneVfoHz")
                    candidatePeakToRetunedVfoOffsetHz = Get-NullableLongValue (Get-JsonValue $candidate "peakToRetunedVfoOffsetHz")
                    candidateRetuneReason = [string](Get-JsonValue $candidate "retuneReason")
                    window = $window
                    reportPath = $portableWatchReport
                    jsonlPath = $portableWatchJsonl
                    artifactPathCompacted = [bool]$artifactPathCompacted
                    trendStatus = [string](Get-JsonValue $report "trendStatus")
                    readyForBenchmarkTrace = $readyTrace
                    okSampleCount = Get-IntValue (Get-JsonValue $report "okSampleCount")
                    failedSampleCount = Get-IntValue (Get-JsonValue $report "failedSampleCount")
                    readySampleCount = Get-IntValue (Get-JsonValue $report "readySampleCount")
                    hardBlockerSampleCount = Get-IntValue (Get-JsonValue $report "hardBlockerSampleCount")
                    nr5TuningTraceStatus = [string](Get-JsonValue $report "nr5TuningTraceStatus")
                    nr5TuningReadySampleCount = Get-IntValue (Get-JsonValue $report "nr5TuningReadySampleCount")
                    agcStabilityStatus = [string](Get-JsonValue $agc "status")
                    agcPumpingRisk = Test-Truthy (Get-JsonValue $agc "pumpingRisk")
                    agcMovementDb = Get-NullableDoubleValue (Get-JsonValue (Get-JsonValue $report "agcGainDb") "movement")
                    audioRmsMovementDb = Get-NullableDoubleValue (Get-JsonValue (Get-JsonValue $report "audioRmsDbfs") "movement")
                    adcHeadroomMinDb = Get-NullableDoubleValue (Get-JsonValue (Get-JsonValue $report "adcHeadroomDb") "min")
                    weakInputSampleCount = $weakInput
                    strongInputSampleCount = $strongInput
                    nearStrongInputSampleCount = $nearStrongInput
                    weakRecoveredSampleCount = Get-IntValue (Get-JsonValue $weak "weakRecoveredSampleCount")
                    weakDropoutSampleCount = Get-IntValue (Get-JsonValue $weak "weakDropoutSampleCount")
                    weakDropoutCandidateLossSampleCount = Get-IntValue (Get-JsonValue $weak "weakDropoutCandidateLossSampleCount")
                    hotMakeupSampleCount = Get-IntValue (Get-JsonValue $weak "hotMakeupSampleCount")
                    weakStrongOutputGapDb = $weakOutputGap
                    weakStrongFinalAudioGapDb = $weakFinalGap
                    speechQualifiedWeakInputSampleCount = $speechQualifiedWeakInput
                    speechQualifiedStrongInputSampleCount = $speechQualifiedStrongInput
                    speechQualifiedNearStrongInputSampleCount = $speechQualifiedNearStrongInput
                    speechQualifiedWeakStrongOutputGapDb = Get-NullableDoubleValue (Get-JsonValue $weak "speechQualifiedWeakStrongOutputGapDb")
                    speechQualifiedWeakStrongFinalAudioGapDb = Get-NullableDoubleValue (Get-JsonValue $weak "speechQualifiedWeakStrongFinalAudioGapDb")
                    speechQualifiedMixedWeakStrongEvidenceReady = Test-Truthy (Get-JsonValue $weak "speechQualifiedMixedWeakStrongEvidenceReady")
                    speechQualifiedWeakStrongOutputParityReady = Test-Truthy (Get-JsonValue $weak "speechQualifiedWeakStrongOutputParityReady")
                    speechQualifiedWeakStrongFinalAudioParityReady = Test-Truthy (Get-JsonValue $weak "speechQualifiedWeakStrongFinalAudioParityReady")
                    speechQualifiedMixedWeakStrongEvidenceStatus = [string](Get-JsonValue $weak "speechQualifiedMixedWeakStrongEvidenceStatus")
                    passbandQualifiedWeakInputSampleCount = $passbandQualifiedWeakInput
                    passbandQualifiedStrongInputSampleCount = $passbandQualifiedStrongInput
                    passbandQualifiedNearStrongInputSampleCount = $passbandQualifiedNearStrongInput
                    passbandQualifiedWeakStrongOutputGapDb = Get-NullableDoubleValue (Get-JsonValue $weak "passbandQualifiedWeakStrongOutputGapDb")
                    passbandQualifiedWeakStrongFinalAudioGapDb = Get-NullableDoubleValue (Get-JsonValue $weak "passbandQualifiedWeakStrongFinalAudioGapDb")
                    passbandQualifiedMixedWeakStrongEvidenceReady = Test-Truthy (Get-JsonValue $weak "passbandQualifiedMixedWeakStrongEvidenceReady")
                    passbandQualifiedWeakStrongOutputParityReady = Test-Truthy (Get-JsonValue $weak "passbandQualifiedWeakStrongOutputParityReady")
                    passbandQualifiedWeakStrongFinalAudioParityReady = Test-Truthy (Get-JsonValue $weak "passbandQualifiedWeakStrongFinalAudioParityReady")
                    passbandQualifiedMixedWeakStrongEvidenceStatus = [string](Get-JsonValue $weak "passbandQualifiedMixedWeakStrongEvidenceStatus")
                    mixedWeakStrongEvidenceReady = $mixedReady
                    weakStrongOutputParityReady = Test-Truthy (Get-JsonValue $weak "weakStrongOutputParityReady")
                    weakStrongFinalAudioParityReady = Test-Truthy (Get-JsonValue $weak "weakStrongFinalAudioParityReady")
                    mixedWeakStrongEvidenceStatus = [string](Get-JsonValue $weak "mixedWeakStrongEvidenceStatus")
                    mixedWeakStrongTuningStatus = $mixedFocusStatus
                    mixedWeakStrongTuningAction = $mixedFocusAction
                    mixedWeakStrongOutputGapDirection = $mixedOutputGapDirection
                    mixedWeakStrongOutputGapExcessDb = $mixedOutputGapExcess
                    mixedWeakOutputLiftNeededDb = $mixedWeakOutputLiftNeeded
                    mixedWeakOutputTrimNeededDb = $mixedWeakOutputTrimNeeded
                    mixedWeakStrongFinalAudioGapDirection = $mixedFinalAudioGapDirection
                    mixedWeakStrongFinalAudioGapExcessDb = $mixedFinalAudioGapExcess
                    mixedWeakFinalAudioLiftNeededDb = $mixedWeakFinalAudioLiftNeeded
                    mixedWeakFinalAudioTrimNeededDb = $mixedWeakFinalAudioTrimNeeded
                    mixedWeakStrongTopWeakInputCount = $mixedTopWeakInputCount
                    mixedWeakStrongTopStrongInputCount = $mixedTopStrongInputCount
                    mixedWeakStrongTopSpeechQualifiedWeakInputCount = $mixedTopSpeechQualifiedWeakInputCount
                    mixedWeakStrongTopSpeechQualifiedStrongInputCount = $mixedTopSpeechQualifiedStrongInputCount
                    mixedWeakStrongTopPassbandQualifiedWeakInputCount = $mixedTopPassbandQualifiedWeakInputCount
                    mixedWeakStrongTopPassbandQualifiedStrongInputCount = $mixedTopPassbandQualifiedStrongInputCount
                    frontendTopPeakSampleCount = $frontendTopPeakSampleCount
                    frontendNearPassbandSampleCount = $frontendNearPassbandSampleCount
                    frontendNearPassbandThresholdHz = Get-IntValue (Get-JsonValue $frontendTopPeakWatch "nearPassbandThresholdHz")
                    frontendNearestTopPeakOffsetHz = Get-JsonValue $frontendTopPeakWatch "nearestOffsetHz"
                    frontendNearestTopPeakAbsOffsetHz = Get-JsonValue $frontendTopPeakWatch "nearestAbsOffsetHz"
                    frontendStrongestTopPeakSnrDb = Get-JsonValue $frontendTopPeakWatch "strongestSnrDb"
                    frontendTopNearPassbandSamples = @(Get-JsonArray $frontendTopPeakWatch "topNearPassbandSamples")
                    rxLevelerConstrainedSampleCount = Get-IntValue (Get-JsonValue $leveler "constrainedSampleCount")
                    rxLevelerBoostSlewLimitedSampleCount = Get-IntValue (Get-JsonValue $report "rxAudioLevelerBoostSlewLimitedSampleCount")
                    rxLevelerPeakLimitedSampleCount = Get-IntValue (Get-JsonValue $report "rxAudioLevelerPeakLimitedSampleCount")
                    rxLevelerOutputLimitedSampleCount = Get-IntValue (Get-JsonValue $report "rxAudioLevelerOutputLimitedSampleCount")
                    score = $score
                }
                $runs.Add([pscustomobject]$run) | Out-Null

                if ($StopOnReady -and $mixedReady) {
                    $stoppedEarly = $true
                    $passStoppedEarly = $true
                    break
                }
            }

            if ($stoppedEarly) {
                break
            }
        }

        $passSummary["completedUtc"] = ([DateTimeOffset]::UtcNow).ToString("o")
        $passSummary["stoppedEarly"] = $passStoppedEarly
        $scanPasses.Add([pscustomobject]$passSummary) | Out-Null
        $completedPassCount++

        if ($stoppedEarly) {
            break
        }

        if ($pass -lt $PassCount -and $PassDelaySec -gt 0) {
            Start-Sleep -Seconds $PassDelaySec
        }
    }
}
catch {
    $scanError = $_.Exception.Message
}
finally {
    $restoreResult = $null
    try {
        $restoreResult = Restore-OriginalTuning `
            -BaseUrl $base `
            -OriginalVfo $originalVfo `
            -OriginalRadioLo $originalRadioLo `
            -OriginalMode $originalMode `
            -OriginalEndpoint $originalEndpoint `
            -OriginalConnectSampleRate $originalConnectSampleRate `
            -RequestTimeoutSec $TimeoutSec `
            -SettleMs ([Math]::Min(1000, [Math]::Max(0, $SettleMs))) `
            -MaxAttempts 3 `
            -SkipCertificate:$SkipCertificateCheck
        $afterRestore = $restoreResult.state
        $restoredVfo = Get-NullableLongValue (Get-JsonValue $afterRestore "vfoHz")
        $restoredRadioLo = Get-NullableLongValue (Get-JsonValue $afterRestore "radioLoHz")
        $restoredMode = [string](Get-JsonValue $afterRestore "mode")
        $restoreReconnectAttempted = Test-Truthy $restoreResult.reconnectAttempted
        $restoreReconnectSucceeded = Test-Truthy $restoreResult.reconnectSucceeded
        $restoreReconnectError = [string]$restoreResult.reconnectError
        if (-not (Test-Truthy $restoreResult.ok)) {
            throw "state did not return to original VFO/LO after restore attempts"
        }
    }
    catch {
        $restoreError = $_.Exception.Message
        if ($null -ne $restoreResult) {
            $restoreReconnectAttempted = Test-Truthy $restoreResult.reconnectAttempted
            $restoreReconnectSucceeded = Test-Truthy $restoreResult.reconnectSucceeded
            $restoreReconnectError = [string]$restoreResult.reconnectError
        }
    }
}

$runArray = @($runs.ToArray())
$bestRun = $null
if ($runArray.Count -gt 0) {
    $bestRun = $runArray | Sort-Object `
            @{ Expression = { if (Test-Truthy $_.mixedWeakStrongEvidenceReady) { 0 } else { 1 } } }, `
            @{ Expression = { if (Test-Truthy $_.readyForBenchmarkTrace) { 0 } else { 1 } } }, `
            @{ Expression = { -1.0 * [double](Get-NullableDoubleValue $_.score) } }, `
            @{ Expression = { [string]$_.frequencyHz } } | Select-Object -First 1
}

$weakTotal = 0
$strongTotal = 0
$nearStrongTotal = 0
$candidateWeakLossTotal = 0
$hotMakeupTotal = 0
$hardBlockerTotal = 0
$pumpingRiskRunCount = 0
$mixedReadyRunCount = 0
$rxStateDriftRunCount = 0
$speechQualifiedWeakTotal = 0
$speechQualifiedStrongTotal = 0
$speechQualifiedNearStrongTotal = 0
$passbandQualifiedWeakTotal = 0
$passbandQualifiedStrongTotal = 0
$passbandQualifiedNearStrongTotal = 0
$frontendNearPassbandTotal = 0
$tuningActionCounts = @{}
foreach ($run in $runArray) {
    $weakTotal += Get-IntValue $run.weakInputSampleCount
    $strongTotal += Get-IntValue $run.strongInputSampleCount
    $nearStrongTotal += Get-IntValue $run.nearStrongInputSampleCount
    $candidateWeakLossTotal += Get-IntValue $run.weakDropoutCandidateLossSampleCount
    $hotMakeupTotal += Get-IntValue $run.hotMakeupSampleCount
    $hardBlockerTotal += Get-IntValue $run.hardBlockerSampleCount
    $speechQualifiedWeakTotal += Get-IntValue $run.speechQualifiedWeakInputSampleCount
    $speechQualifiedStrongTotal += Get-IntValue $run.speechQualifiedStrongInputSampleCount
    $speechQualifiedNearStrongTotal += Get-IntValue $run.speechQualifiedNearStrongInputSampleCount
    $passbandQualifiedWeakTotal += Get-IntValue $run.passbandQualifiedWeakInputSampleCount
    $passbandQualifiedStrongTotal += Get-IntValue $run.passbandQualifiedStrongInputSampleCount
    $passbandQualifiedNearStrongTotal += Get-IntValue $run.passbandQualifiedNearStrongInputSampleCount
    $frontendNearPassbandTotal += Get-IntValue $run.frontendNearPassbandSampleCount
    if (Test-Truthy $run.agcPumpingRisk) {
        $pumpingRiskRunCount++
    }
    if ([string]::Equals([string]$run.trendStatus, "rx-state-drift", [StringComparison]::OrdinalIgnoreCase)) {
        $rxStateDriftRunCount++
    }
    if (Test-Truthy $run.mixedWeakStrongEvidenceReady) {
        $mixedReadyRunCount++
    }
    $tuningAction = Get-TrimmedStringValue $run.mixedWeakStrongTuningAction
    if ($null -ne $tuningAction) {
        if (-not $tuningActionCounts.ContainsKey($tuningAction)) {
            $tuningActionCounts[$tuningAction] = 0
        }
        $tuningActionCounts[$tuningAction] = [int]$tuningActionCounts[$tuningAction] + 1
    }
}

$peakCandidateArray = @($allPeakCandidates.ToArray())
$rejectedPeakCandidateArray = @($allRejectedPeakCandidates.ToArray())
$rejectedRetuneCandidateArray = @($allRejectedRetuneCandidates.ToArray())
$rejectedPeakCandidateReasonCounts = [ordered]@{}
foreach ($candidate in $rejectedPeakCandidateArray) {
    $reason = Get-TrimmedStringValue (Get-JsonValue $candidate "rejectionReason")
    if ($null -eq $reason) {
        $reason = "unknown"
    }
    if (-not $rejectedPeakCandidateReasonCounts.Contains($reason)) {
        $rejectedPeakCandidateReasonCounts[$reason] = 0
    }
    $rejectedPeakCandidateReasonCounts[$reason] = [int]$rejectedPeakCandidateReasonCounts[$reason] + 1
}
$strongestRejectedPeakCandidate = $null
if ($rejectedPeakCandidateArray.Count -gt 0) {
    $strongestRejectedPeakCandidate = $rejectedPeakCandidateArray | Sort-Object `
            @{ Expression = { -1.0 * [double](Get-NullableDoubleValue (Get-JsonValue $_ "snrDb")) } }, `
            @{ Expression = { [long](Get-NullableLongValue (Get-JsonValue $_ "frequencyHz")) } } | Select-Object -First 1
}
$rejectedRetuneCandidateReasonCounts = [ordered]@{}
foreach ($candidate in $rejectedRetuneCandidateArray) {
    $reason = Get-TrimmedStringValue (Get-JsonValue $candidate "rejectionReason")
    if ($null -eq $reason) {
        $reason = "unknown"
    }
    if (-not $rejectedRetuneCandidateReasonCounts.Contains($reason)) {
        $rejectedRetuneCandidateReasonCounts[$reason] = 0
    }
    $rejectedRetuneCandidateReasonCounts[$reason] = [int]$rejectedRetuneCandidateReasonCounts[$reason] + 1
}
$scanPassArray = @($scanPasses.ToArray())
if ($null -eq $latestLive) {
    $latestLive = [pscustomobject]@{}
}
if ($null -eq $latestScene) {
    $latestScene = [pscustomobject]@{}
}

$recommendations = New-Object System.Collections.Generic.List[string]
if (-not [string]::IsNullOrWhiteSpace($scanError)) {
    $recommendations.Add("The scan stopped early because a request or watcher window failed: $scanError. The report was still written so VFO restore and partial evidence can be audited.") | Out-Null
}
if (-not $AllowRetune -and @($operatorCandidateFrequencyHz).Count -gt 0) {
    $recommendations.Add("Operator candidate frequencies were supplied but not captured because -AllowRetune was not supplied; rerun with -AllowRetune when RX VFO movement is acceptable.") | Out-Null
}
if (-not $AllowRetune -and $peakCandidateArray.Count -gt 0) {
    $recommendations.Add("Peak candidates were found but not captured because -AllowRetune was not supplied; rerun with -AllowRetune when RX VFO movement is acceptable.") | Out-Null
}
if ($MaxPeaks -gt 0 -and $peakCandidateArray.Count -le 0 -and (Test-Truthy $peakRetuneSpan.bounded)) {
    $recommendations.Add("Frontend peaks were bounded to $($peakRetuneSpan.lowHz)..$($peakRetuneSpan.highHz) Hz ($($peakRetuneSpan.source)); no in-span frontend peak candidates passed the SNR/merge filters.") | Out-Null
}
if ($null -ne $strongestRejectedPeakCandidate) {
    $recommendations.Add("Strongest rejected frontend peak was $($strongestRejectedPeakCandidate.frequencyHz) Hz exact $($strongestRejectedPeakCandidate.exactFrequencyHz) Hz, reason '$($strongestRejectedPeakCandidate.rejectionReason)' ($($strongestRejectedPeakCandidate.rejectionDetail)); use it for manual review only unless it is inside the intended retune span.") | Out-Null
}
if ($rejectedRetuneCandidateArray.Count -gt 0) {
    $recommendations.Add("One or more retune candidates were rejected before VFO movement; inspect rejectedRetuneCandidates before widening span bounds or adding new candidate frequencies.") | Out-Null
}
if ($mixedReadyRunCount -gt 0 -and $null -ne $bestRun) {
    $recommendations.Add("A mixed weak+strong NR5/SPNR run was found; promote '$($bestRun.reportPath)' into live history and compare it against current-Zeus/Thetis-parity windows before tuning defaults.") | Out-Null
}
elseif ($weakTotal -gt 0 -and $strongTotal -le 0 -and $nearStrongTotal -gt 0) {
    $recommendations.Add("The hunt found weak NR5/SPNR input plus near-strong samples but no strict strong-input speech; extend dwell or retune around the best near-strong run before calling the frequency neighborhood exhausted.") | Out-Null
}
elseif ($weakTotal -gt 0 -and $strongTotal -le 0) {
    $recommendations.Add("The hunt found weak NR5/SPNR input but no strong-input speech; continue scanning active SSB windows or retune manually before calling mixed weak+strong acceptance ready.") | Out-Null
}
elseif ($strongTotal -gt 0 -and $weakTotal -le 0) {
    $recommendations.Add("The hunt found strong input but no weak-input samples; include edge-of-readability or fading speech before using the run for weak-signal preservation evidence.") | Out-Null
}
elseif ($weakTotal -le 0 -and $strongTotal -le 0) {
    $recommendations.Add("No weak or strong NR5/SPNR input was captured; keep the panadapter/frontend scene fresh and retry during active band conditions.") | Out-Null
}
if ($weakTotal -gt 0 -and $strongTotal -gt 0 -and $speechQualifiedStrongTotal -le 0) {
    $recommendations.Add("The hunt found raw strong-input samples but none were speech-qualified; inspect frontend/passband evidence before treating this as mixed weak+strong speech.") | Out-Null
}
if ($strongTotal -le 0 -and $speechQualifiedNearStrongTotal -gt 0) {
    $recommendations.Add("One or more near-strong samples were speech-qualified; inspect per-window nr5WeakSignalWatch.topNearStrongInputs and rerun with longer dwell before changing NR5 thresholds.") | Out-Null
}
if ($frontendNearPassbandTotal -le 0) {
    $recommendations.Add("No near-passband frontend peak samples were captured; this scan may be off-signal even if raw weak-input counters moved.") | Out-Null
}
if ($candidateWeakLossTotal -gt 0) {
    $recommendations.Add("Candidate weak-loss samples appeared; inspect the per-window nr5WeakSignalWatch.topCandidateWeakLosses before increasing global makeup or changing defaults.") | Out-Null
}
if ($hotMakeupTotal -gt 0) {
    $recommendations.Add("Hot makeup samples appeared; inspect nr5WeakSignalWatch.topHotMakeup before changing recovery attack/release.") | Out-Null
}
if ($pumpingRiskRunCount -gt 0) {
    $recommendations.Add("One or more windows reported AGC pumping risk; reject those windows for promotion until AGC movement is explained.") | Out-Null
}
if ($hardBlockerTotal -gt 0) {
    $recommendations.Add("One or more windows had hard blockers; recapture after clearing endpoint/runtime blockers.") | Out-Null
}
if ($null -ne $bestRun -and -not [string]::IsNullOrWhiteSpace($bestRun.mixedWeakStrongTuningAction)) {
    $recommendations.Add("Best-run mixed focus action is '$($bestRun.mixedWeakStrongTuningAction)' (output gap excess $(Format-NullableDbText $bestRun.mixedWeakStrongOutputGapExcessDb), final-audio gap excess $(Format-NullableDbText $bestRun.mixedWeakStrongFinalAudioGapExcessDb)); use it to choose retune/longer dwell versus bounded NR5 weak-speech lift before changing defaults.") | Out-Null
}
if ($runArray | Where-Object { [string]::Equals([string]$_.mixedWeakStrongTuningAction, "tune-bounded-weak-speech-lift-from-top-weak-and-strong-input-rows", [StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1) {
    $recommendations.Add("At least one window requests bounded weak-speech lift; inspect that window's nr5WeakSignalWatch.mixedWeakStrongTuningFocus top weak/strong rows before changing NR5 or RX leveler thresholds.") | Out-Null
}

$scanOk = ([string]::IsNullOrWhiteSpace($scanError) -and $null -eq $restoreError -and $runArray.Count -gt 0)
$evidenceAcceptanceStatus = "scan-not-ready"
$evidenceAcceptanceReady = $false
$evidenceAcceptanceReason = "The scan did not produce auditable RX windows."
if (-not $scanOk) {
    if (-not [string]::IsNullOrWhiteSpace($scanError)) {
        $evidenceAcceptanceReason = "The scan failed before producing acceptance evidence: $scanError"
    }
    elseif ($null -ne $restoreError) {
        $evidenceAcceptanceReason = "The scan could not prove safe VFO/mode restoration: $restoreError"
    }
}
elseif ($mixedReadyRunCount -gt 0) {
    if ($pumpingRiskRunCount -gt 0) {
        $evidenceAcceptanceStatus = "mixed-ready-with-agc-pumping-risk"
        $evidenceAcceptanceReason = "At least one mixed weak+strong window was found, but one or more windows reported AGC pumping risk."
    }
    elseif ($hardBlockerTotal -gt 0) {
        $evidenceAcceptanceStatus = "mixed-ready-with-blockers"
        $evidenceAcceptanceReason = "At least one mixed weak+strong window was found, but the scan also included hard-blocked samples that must be reviewed."
    }
    else {
        $evidenceAcceptanceStatus = "mixed-ready"
        $evidenceAcceptanceReady = $true
        $evidenceAcceptanceReason = "At least one mixed weak+strong NR5/SPNR window is ready for promotion into live comparison history."
    }
}
elseif ($weakTotal -gt 0 -and $strongTotal -le 0) {
    if ($passbandQualifiedWeakTotal -gt 0 -and $frontendNearPassbandTotal -gt 0) {
        if ($rxStateDriftRunCount -gt 0) {
            $evidenceAcceptanceStatus = "weak-only-passband-with-drift"
            $evidenceAcceptanceReason = "Weak passband-qualified evidence exists, but no strong-input samples were captured and at least one run drifted RX state."
        }
        elseif ($hardBlockerTotal -gt 0) {
            $evidenceAcceptanceStatus = "weak-only-passband-with-blockers"
            $evidenceAcceptanceReason = "Weak passband-qualified evidence exists, but no strong-input samples were captured and hard-blocked samples were present."
        }
        else {
            $evidenceAcceptanceStatus = "weak-only-passband"
            $evidenceAcceptanceReason = "Weak passband-qualified evidence exists, but no strong-input samples were captured."
        }
    }
    elseif ($passbandQualifiedWeakTotal -gt 0 -or $frontendNearPassbandTotal -gt 0) {
        $evidenceAcceptanceStatus = "weak-only-passband-incomplete"
        $evidenceAcceptanceReason = "Weak evidence exists, but passband qualification is incomplete and no strong-input samples were captured."
    }
    else {
        $evidenceAcceptanceStatus = "weak-only-off-passband"
        $evidenceAcceptanceReason = "Weak NR5/SPNR input was observed, but no strong-input or passband-qualified evidence was captured."
    }
}
elseif ($strongTotal -gt 0 -and $weakTotal -le 0) {
    $evidenceAcceptanceStatus = "strong-only"
    $evidenceAcceptanceReason = "Strong-input evidence exists, but no weak-input samples were captured for weak-signal preservation."
}
else {
    $evidenceAcceptanceStatus = "no-weak-or-strong-evidence"
    $evidenceAcceptanceReason = "No weak or strong NR5/SPNR input was captured."
}

$wdspV2GraduationReason = "A G2 peak-hunt report is scouting evidence only; WDSP v2 graduation still requires matrix comparisons, offline fixture coverage, Thetis/current-Zeus parity review, on-air approval, and cross-radio validation."

$completedUtc = [DateTimeOffset]::UtcNow
$reportObject = [ordered]@{
    schemaVersion = 1
    tool = "run-dsp-g2-rx-peak-hunt"
    generatedUtc = $completedUtc.ToString("o")
    startedUtc = $startedUtc.ToString("o")
    completedUtc = $completedUtc.ToString("o")
    durationMs = [int]($completedUtc - $startedUtc).TotalMilliseconds
    ok = $scanOk
    evidenceAcceptanceStatus = $evidenceAcceptanceStatus
    evidenceAcceptanceReady = $evidenceAcceptanceReady
    evidenceAcceptanceReason = $evidenceAcceptanceReason
    evidenceAcceptanceScope = "g2-rx-peak-hunt"
    wdspV2GraduationReady = $false
    wdspV2GraduationReason = $wdspV2GraduationReason
    scanError = $scanError
    requestedBaseUrl = $requestedBaseUrl
    baseUrl = $base
    baseUrlAutoDiscoverRequested = $baseUrlAutoDiscoverRequested
    baseUrlAutoDiscovered = $baseUrlAutoDiscovered
    baseUrlAutoDiscoverError = $baseUrlAutoDiscoverError
    baseUrlProbeResults = @($baseUrlProbeResults)
    outputDir = ConvertTo-PortableBundlePath -Root $portableBundleRoot -Path $captureDir
    bundleRelativePaths = [bool]$bundleRelativePaths
    label = $Label
    comparisonId = $ComparisonId
    targetMode = if ([string]::IsNullOrWhiteSpace($targetMode)) { $null } else { $targetMode }
    allowRetune = [bool]$AllowRetune
    skipCurrentVfo = [bool]$SkipCurrentVfo
    stopOnReady = [bool]$StopOnReady
    samplesPerWindow = $SamplesPerWindow
    intervalMs = $IntervalMs
    windowsPerPeak = $WindowsPerPeak
    passCount = $PassCount
    passDelaySec = $PassDelaySec
    tuneStepHz = $effectiveTuneStepHz
    frontendPeakRetuneMode = if ([string]::IsNullOrWhiteSpace($frontendPeakRetuneMode)) { $null } else { $frontendPeakRetuneMode }
    frontendPeakRetuneFilterLowHz = $frontendPeakRetuneFilterLowHz
    frontendPeakRetuneFilterHighHz = $frontendPeakRetuneFilterHighHz
    frontendPeakRetuneTargetOffsetHz = $frontendPeakRetuneTargetOffsetHz
    peakRetuneSpan = $peakRetuneSpan
    completedPassCount = $completedPassCount
    scanPassCount = $scanPassArray.Count
    candidateFrequencyHz = @($operatorCandidateFrequencyHz)
    operatorTrendMaxCandidates = $OperatorTrendMaxCandidates
    operatorTrendCandidateFrequencyHz = @($operatorTrendCandidateFrequencyHz)
    operatorTrendCandidateCount = $operatorTrendCandidates.Count
    autoPhoneCluster = [bool]$AutoPhoneCluster
    autoPhoneClusterSearchRoot = $AutoPhoneClusterSearchRoot
    autoPhoneClusterMaxCandidates = $AutoPhoneClusterMaxCandidates
    autoPhoneClusterLookbackHours = $AutoPhoneClusterLookbackHours
    autoPhoneClusterMinSpeechSamples = $AutoPhoneClusterMinSpeechSamples
    autoPhoneClusterBandLowHz = $AutoPhoneClusterBandLowHz
    autoPhoneClusterBandHighHz = $AutoPhoneClusterBandHighHz
    autoPhoneClusterCandidateFrequencyHz = @($autoPhoneClusterCandidateFrequencyHz)
    autoPhoneClusterCandidateCount = $autoPhoneClusterCandidates.Count
    autoPhoneClusterExactCandidateCount = $autoPhoneClusterExactCandidateCount
    autoPhoneClusterNeighborCandidateCount = $autoPhoneClusterNeighborCandidateCount
    operatorCandidateCount = $operatorCandidates.Count
    maxPeaks = $MaxPeaks
    peakMergeHz = $PeakMergeHz
    minPeakSnrDb = $MinPeakSnrDb
    settleMs = $SettleMs
    originalVfoHz = $originalVfo
    restoredVfoHz = $restoredVfo
    originalRadioLoHz = $originalRadioLo
    restoredRadioLoHz = $restoredRadioLo
    originalMode = $originalMode
    restoredMode = $restoredMode
    originalEndpoint = $originalEndpoint
    safety = [ordered]@{
        rxOnly = $true
        txEndpointsTouched = $false
        vfoRetuneRequiresAllowRetune = $true
        temporaryModeRequested = -not [string]::IsNullOrWhiteSpace($targetMode)
        targetMode = if ([string]::IsNullOrWhiteSpace($targetMode)) { $null } else { $targetMode }
        originalVfoRestoreAttempted = $true
        originalVfoRestored = ($null -ne $restoredVfo -and [long]$restoredVfo -eq [long]$originalVfo)
        originalRadioLoRestoreAttempted = ($null -ne $originalRadioLo -and $originalRadioLo -gt 0)
        originalRadioLoRestored = ($null -ne $originalRadioLo -and $originalRadioLo -gt 0 -and $null -ne $restoredRadioLo -and [long]$restoredRadioLo -eq [long]$originalRadioLo)
        originalModeRestoreAttempted = -not [string]::IsNullOrWhiteSpace($originalMode)
        originalModeRestored = (-not [string]::IsNullOrWhiteSpace($originalMode) -and [string]::Equals([string]$restoredMode, [string]$originalMode, [StringComparison]::OrdinalIgnoreCase))
        restoreReconnectAttempted = $restoreReconnectAttempted
        restoreReconnectSucceeded = $restoreReconnectSucceeded
        restoreReconnectError = if ([string]::IsNullOrWhiteSpace($restoreReconnectError)) { $null } else { $restoreReconnectError }
        restoreError = $restoreError
    }
    hardware = [ordered]@{
        connectionStatus = [string](Get-JsonValue $hardware "connectionStatus")
        endpoint = [string](Get-JsonValue $hardware "endpoint")
        effectiveBoard = [string](Get-JsonValue $hardware "effectiveBoard")
        orionMkIIVariant = [string](Get-JsonValue $hardware "orionMkIIVariant")
        originalVfoHz = $originalVfo
        restoredVfoHz = $restoredVfo
        originalRadioLoHz = $originalRadioLo
        restoredRadioLoHz = $restoredRadioLo
        originalMode = $originalMode
        restoredMode = $restoredMode
        mode = [string](Get-JsonValue $hardware "mode")
        sampleRate = Get-IntValue (Get-JsonValue $hardware "sampleRate")
    }
    liveDiagnostics = [ordered]@{
        status = [string](Get-JsonValue $latestLive "status")
        readyForLiveBenchmark = Test-Truthy (Get-JsonValue $latestLive "readyForLiveBenchmark")
        wdspActive = Test-Truthy (Get-JsonValue $latestLive "wdspActive")
        wdspNativeLoadable = Test-Truthy (Get-JsonValue $latestLive "wdspNativeLoadable")
        requestedNrMode = [string](Get-JsonValue $latestLive "requestedNrMode")
        effectiveNrMode = [string](Get-JsonValue $latestLive "effectiveNrMode")
        readyForNr5Tuning = Test-Truthy (Get-JsonValue $latestLive "readyForNr5Tuning")
        frontendSceneFresh = Test-Truthy (Get-JsonValue $latestLive "frontendSceneFresh")
    }
    frontendScene = [ordered]@{
        status = [string](Get-JsonValue $latestScene "status")
        fresh = Test-Truthy (Get-JsonValue $latestScene "fresh")
        signalProfile = [string](Get-JsonValue $latestScene "signalProfile")
        maxSnrDb = Get-NullableDoubleValue (Get-JsonValue $latestScene "maxSnrDb")
        coherentMaxSnrDb = Get-NullableDoubleValue (Get-JsonValue $latestScene "coherentMaxSnrDb")
        topPeakCount = @(Get-JsonArray $latestScene "topPeaks").Count
    }
    operatorCandidates = @($operatorCandidates)
    operatorTrendCandidates = @($operatorTrendCandidates)
    autoPhoneClusterCandidates = @($autoPhoneClusterCandidates)
    peakCandidates = @($peakCandidateArray)
    rejectedPeakCandidateCount = $rejectedPeakCandidateArray.Count
    rejectedPeakCandidateReasonCounts = $rejectedPeakCandidateReasonCounts
    rejectedPeakCandidates = @($rejectedPeakCandidateArray)
    rejectedRetuneCandidateCount = $rejectedRetuneCandidateArray.Count
    rejectedRetuneCandidateReasonCounts = $rejectedRetuneCandidateReasonCounts
    rejectedRetuneCandidates = @($rejectedRetuneCandidateArray)
    plannedRunCount = $plannedRunCount
    actualRunCount = $runArray.Count
    failedRunCount = @($runArray | Where-Object { -not (Test-Truthy $_.ok) }).Count
    mixedWeakStrongReady = ($mixedReadyRunCount -gt 0)
    mixedWeakStrongReadyRunCount = $mixedReadyRunCount
    weakInputSampleCount = $weakTotal
    strongInputSampleCount = $strongTotal
    nearStrongInputSampleCount = $nearStrongTotal
    speechQualifiedWeakInputSampleCount = $speechQualifiedWeakTotal
    speechQualifiedStrongInputSampleCount = $speechQualifiedStrongTotal
    speechQualifiedNearStrongInputSampleCount = $speechQualifiedNearStrongTotal
    passbandQualifiedWeakInputSampleCount = $passbandQualifiedWeakTotal
    passbandQualifiedStrongInputSampleCount = $passbandQualifiedStrongTotal
    passbandQualifiedNearStrongInputSampleCount = $passbandQualifiedNearStrongTotal
    frontendNearPassbandSampleCount = $frontendNearPassbandTotal
    candidateWeakLossSampleCount = $candidateWeakLossTotal
    hotMakeupSampleCount = $hotMakeupTotal
    hardBlockerSampleCount = $hardBlockerTotal
    agcPumpingRiskRunCount = $pumpingRiskRunCount
    rxStateDriftRunCount = $rxStateDriftRunCount
    mixedWeakStrongTuningActionCounts = [ordered]@{}
    bestRun = $bestRun
    retuneAttempts = @($retuneAttempts.ToArray())
    scanPasses = @($scanPassArray)
    stoppedEarly = $stoppedEarly
    runs = @($runArray)
    recommendations = @($recommendations.ToArray())
}
foreach ($key in @($tuningActionCounts.Keys | Sort-Object)) {
    $reportObject.mixedWeakStrongTuningActionCounts[$key] = [int]$tuningActionCounts[$key]
}

Write-JsonFile -Path $ReportPath -Value $reportObject

if ($JsonOnly) {
    $reportObject | ConvertTo-Json -Depth 64
}
else {
    Write-Host "G2 RX peak hunt report: $ReportPath"
    Write-Host "Operator candidates: $($operatorCandidates.Count); operator-trend neighbors: $($operatorTrendCandidates.Count); auto phone candidates: $($autoPhoneClusterCandidates.Count)"
    Write-Host "Original VFO: $originalVfo Hz; restored VFO: $restoredVfo Hz"
    Write-Host "Original radio LO: $originalRadioLo Hz; restored radio LO: $restoredRadioLo Hz"
    if (-not [string]::IsNullOrWhiteSpace($targetMode)) {
        Write-Host "Temporary mode: $targetMode; original mode: $originalMode; restored mode: $restoredMode"
    }
    Write-Host "Runs: $($reportObject.actualRunCount), mixed weak+strong ready: $($reportObject.mixedWeakStrongReady), weak samples: $weakTotal, strong samples: $strongTotal, near-strong samples: $nearStrongTotal"
    Write-Host "Evidence acceptance: $($reportObject.evidenceAcceptanceStatus), ready=$($reportObject.evidenceAcceptanceReady), reason=$($reportObject.evidenceAcceptanceReason)"
    Write-Host "WDSP v2 graduation ready: $($reportObject.wdspV2GraduationReady) ($($reportObject.evidenceAcceptanceScope) evidence only)"
    if ($null -ne $bestRun) {
        Write-Host "Best run: $($bestRun.frequencyHz) Hz score=$($bestRun.score) status=$($bestRun.mixedWeakStrongEvidenceStatus) report=$($bestRun.reportPath)"
        if (-not [string]::IsNullOrWhiteSpace($bestRun.mixedWeakStrongTuningAction)) {
            Write-Host "Best mixed focus: action=$($bestRun.mixedWeakStrongTuningAction), outputGapExcess=$(Format-NullableDbText $bestRun.mixedWeakStrongOutputGapExcessDb) ($($bestRun.mixedWeakStrongOutputGapDirection)), finalAudioGapExcess=$(Format-NullableDbText $bestRun.mixedWeakStrongFinalAudioGapExcessDb) ($($bestRun.mixedWeakStrongFinalAudioGapDirection))"
        }
    }
}

if (-not $ContinueOnError -and $null -ne $restoreError) {
    exit 2
}
if (-not $ContinueOnError -and -not [string]::IsNullOrWhiteSpace($scanError)) {
    exit 3
}
if (-not $ContinueOnError -and $runArray.Count -eq 0) {
    exit 1
}
