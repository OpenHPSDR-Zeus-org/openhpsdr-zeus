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

    [switch]$AutoPhoneCluster,

    [string]$AutoPhoneClusterSearchRoot = "",

    [int]$AutoPhoneClusterMaxCandidates = 12,

    [int]$AutoPhoneClusterLookbackHours = 12,

    [int]$AutoPhoneClusterMinSpeechSamples = 1,

    [long]$AutoPhoneClusterBandLowHz = 14150000,

    [long]$AutoPhoneClusterBandHighHz = 14350000,

    [int]$MaxPeaks = 6,

    [int]$PeakMergeHz = 1000,

    [double]$MinPeakSnrDb = 8.0,

    [int]$SettleMs = 3000,

    [int]$TimeoutSec = 5,

    [string]$OutputRoot = "",

    [string]$ReportPath = "",

    [string]$Label = "",

    [string]$ComparisonId = "nr5-spnr",

    [string]$WatchScriptPath = "",

    [switch]$AllowRetune,

    [switch]$SkipCurrentVfo,

    [switch]$StopOnReady,

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

function Restore-OriginalTuning {
    param(
        [Parameter(Mandatory = $true)][string]$BaseUrl,
        [Parameter(Mandatory = $true)][long]$OriginalVfo,
        $OriginalRadioLo = $null,
        [int]$RequestTimeoutSec = 5,
        [int]$SettleMs = 1000,
        [int]$MaxAttempts = 2,
        [switch]$SkipCertificate
    )

    $lastState = $null
    $lastError = $null
    $loRequired = ($null -ne $OriginalRadioLo -and [long]$OriginalRadioLo -gt 0)
    $sleepMs = [Math]::Max(0, $SettleMs)

    for ($attempt = 1; $attempt -le [Math]::Max(1, $MaxAttempts); $attempt++) {
        try {
            if ($loRequired) {
                Invoke-JsonPost -Url "$BaseUrl/api/radio/lo" -Body @{ hz = [long]$OriginalRadioLo } -RequestTimeoutSec $RequestTimeoutSec -SkipCertificate:$SkipCertificate | Out-Null
            }
            Invoke-JsonPost -Url "$BaseUrl/api/vfo" -Body @{ hz = $OriginalVfo } -RequestTimeoutSec $RequestTimeoutSec -SkipCertificate:$SkipCertificate | Out-Null
            Start-Sleep -Milliseconds $sleepMs

            $lastState = Invoke-JsonGet -Url "$BaseUrl/api/state" -RequestTimeoutSec $RequestTimeoutSec -SkipCertificate:$SkipCertificate
            $restoredVfoHz = Get-NullableLongValue (Get-JsonValue $lastState "vfoHz")
            $restoredRadioLoHz = Get-NullableLongValue (Get-JsonValue $lastState "radioLoHz")
            $vfoRestored = ($null -ne $restoredVfoHz -and [long]$restoredVfoHz -eq $OriginalVfo)
            $radioLoRestored = (-not $loRequired) -or ($null -ne $restoredRadioLoHz -and [long]$restoredRadioLoHz -eq [long]$OriginalRadioLo)

            if ($vfoRestored -and $radioLoRestored) {
                return [pscustomobject][ordered]@{
                    ok = $true
                    attempts = $attempt
                    state = $lastState
                    error = $null
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
        [string]$Source
    )

    $frequencyHz = Get-NullableLongValue (Get-JsonValue $Peak "frequencyHz")
    if ($null -eq $frequencyHz -or $frequencyHz -le 0) {
        return $null
    }

    return [ordered]@{
        rank = $Rank
        source = $Source
        frequencyHz = $frequencyHz
        offsetHz = Get-NullableLongValue (Get-JsonValue $Peak "offsetHz")
        snrDb = Get-NullableDoubleValue (Get-JsonValue $Peak "snrDb")
        dbfs = Get-NullableDoubleValue (Get-JsonValue $Peak "dbfs")
        confidence = Get-NullableDoubleValue (Get-JsonValue $Peak "confidence")
        coherent = Test-Truthy (Get-JsonValue $Peak "coherent")
    }
}

function Select-PeakCandidates {
    param(
        [object[]]$Peaks,
        [int]$Limit,
        [int]$MergeHz,
        [double]$MinimumSnrDb
    )

    $selected = New-Object System.Collections.Generic.List[object]
    if ($Limit -le 0) {
        return @()
    }

    $rank = 0
    foreach ($peak in @($Peaks | Sort-Object @{ Expression = { Get-NullableDoubleValue (Get-JsonValue $_ "snrDb") }; Descending = $true })) {
        $rank++
        $candidate = ConvertTo-PeakCandidate -Peak $peak -Rank $rank -Source "frontend-top-peak"
        if ($null -eq $candidate) {
            continue
        }

        $snr = Get-NullableDoubleValue $candidate.snrDb
        if ($null -ne $snr -and $snr -lt $MinimumSnrDb) {
            continue
        }

        $tooClose = $false
        foreach ($existing in $selected) {
            if ([Math]::Abs([long]$candidate.frequencyHz - [long]$existing.frequencyHz) -le $MergeHz) {
                $tooClose = $true
                break
            }
        }

        if ($tooClose) {
            continue
        }

        $selected.Add([pscustomobject]$candidate) | Out-Null
        if ($selected.Count -ge $Limit) {
            break
        }
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

            $frequencyHz = Get-NullableLongValue (Get-JsonValue $run "frequencyHz")
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
                    score = [Math]::Round($score, 3)
                    speechWeak = $speechWeak
                    speechStrong = $speechStrong
                    passbandWeak = $passbandWeak
                    passbandStrong = $passbandStrong
                    nearPassband = $nearPassband
                    candidateSource = $candidateSource
                    status = [string](Get-JsonValue $run "mixedWeakStrongEvidenceStatus")
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
                        score = $score
                        speechWeak = [int]$seed.speechWeak
                        speechStrong = [int]$seed.speechStrong
                        passbandWeak = [int]$seed.passbandWeak
                        passbandStrong = [int]$seed.passbandStrong
                        nearPassband = [int]$seed.nearPassband
                        candidateSource = [string]$seed.candidateSource
                        status = [string]$seed.status
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
                    evidenceReportPath = [string]$seed.reportPath
                    evidenceNeighborOfFrequencyHz = [long]$seed.sourceFrequencyHz
                    evidenceNeighborOffsetHz = [long]$seed.neighborOffsetHz
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
        offsetHz = Get-NullableLongValue (Get-JsonValue $Candidate "offsetHz")
        snrDb = Get-NullableDoubleValue (Get-JsonValue $Candidate "snrDb")
        dbfs = Get-NullableDoubleValue (Get-JsonValue $Candidate "dbfs")
        confidence = Get-NullableDoubleValue (Get-JsonValue $Candidate "confidence")
        coherent = Test-Truthy (Get-JsonValue $Candidate "coherent")
    }
}

function Get-HuntScore {
    param(
        [int]$WeakInputSampleCount,
        [int]$StrongInputSampleCount,
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

if ($PassCount -lt 1) {
    $PassCount = 1
}
if ($PassDelaySec -lt 0) {
    $PassDelaySec = 0
}
$operatorCandidatesForPlan = @(Get-OperatorFrequencyCandidates -FrequencyHz $CandidateFrequencyHz -FrequencyMHz $CandidateMHz)
$operatorCandidateFrequencyHzForPlan = @($operatorCandidatesForPlan | ForEach-Object { [long]$_.frequencyHz })

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
        samplesPerWindow = $SamplesPerWindow
        intervalMs = $IntervalMs
        windowsPerPeak = $WindowsPerPeak
        passCount = $PassCount
        passDelaySec = $PassDelaySec
        candidateFrequencyHz = @($operatorCandidateFrequencyHzForPlan)
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
                "The tool does not approve DSP default changes; it only hunts for the missing G2 mixed weak+strong NR5/SPNR evidence window."
            )
        }
        example = "powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-g2-rx-peak-hunt.ps1 -BaseUrl auto -SamplesPerWindow 24 -IntervalMs 250 -MaxPeaks 6"
        retuneExample = "powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-g2-rx-peak-hunt.ps1 -BaseUrl auto -AllowRetune -StopOnReady -SamplesPerWindow 24 -IntervalMs 250 -MaxPeaks 6"
        operatorFrequencyExample = "powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-g2-rx-peak-hunt.ps1 -BaseUrl auto -AllowRetune -StopOnReady -CandidateMHz 14.260,14.243,14.287,14.152,14.227,14.240,14.270,14.277,14.300 -PassCount 2 -PassDelaySec 5"
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
    $originalRadioLo = Get-NullableLongValue (Get-JsonValue $initialState "radioLoHz")
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
        scanError = $initializationError
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
        allowRetune = [bool]$AllowRetune
        skipCurrentVfo = [bool]$SkipCurrentVfo
        stopOnReady = [bool]$StopOnReady
        samplesPerWindow = $SamplesPerWindow
        intervalMs = $IntervalMs
        windowsPerPeak = $WindowsPerPeak
        passCount = $PassCount
        passDelaySec = $PassDelaySec
        completedPassCount = 0
        scanPassCount = 0
        candidateFrequencyHz = @($operatorCandidateFrequencyHzForPlan)
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
            originalVfoRestoreAttempted = $false
            originalVfoRestored = $false
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
$latestScene = $null
$latestLive = $null
$allPeakCandidates = New-Object System.Collections.Generic.List[object]
$scanPasses = New-Object System.Collections.Generic.List[object]
$runs = New-Object System.Collections.Generic.List[object]
$retuneAttempts = New-Object System.Collections.Generic.List[object]
$restoredVfo = $null
$restoredRadioLo = $null
$restoreError = $null
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
        $passPeakCandidates = @(Select-PeakCandidates -Peaks $scenePeaks -Limit $MaxPeaks -MergeHz $PeakMergeHz -MinimumSnrDb $MinPeakSnrDb)
        $passPeakCandidatesForReport = New-Object System.Collections.Generic.List[object]
        foreach ($candidate in $passPeakCandidates) {
            $candidateForReport = Copy-PeakCandidateForPass -Candidate $candidate -Pass $pass
            if ($null -ne $candidateForReport) {
                $passPeakCandidatesForReport.Add($candidateForReport) | Out-Null
                $allPeakCandidates.Add($candidateForReport) | Out-Null
            }
        }

        $candidates = New-Object System.Collections.Generic.List[object]
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
                Add-CandidateIfDistinct -Candidates $candidates -Candidate $candidate -MergeHz $PeakMergeHz | Out-Null
            }
            foreach ($candidate in $autoPhoneClusterCandidates) {
                Add-CandidateIfDistinct -Candidates $candidates -Candidate $candidate -MergeHz $PeakMergeHz | Out-Null
            }
            foreach ($candidate in $passPeakCandidates) {
                Add-CandidateIfDistinct -Candidates $candidates -Candidate $candidate -MergeHz $PeakMergeHz | Out-Null
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
            autoPhoneCluster = [bool]$AutoPhoneCluster
            autoPhoneClusterCandidateCount = $autoPhoneClusterCandidates.Count
            peakCandidateCount = $passPeakCandidatesForReport.Count
            candidateCount = $candidates.Count
            plannedRunCount = $plannedForPass
            stoppedEarly = $false
        }

        $passDir = Join-Path $captureDir ("pass-{0:00}" -f $pass)
        New-Item -ItemType Directory -Force -Path $passDir | Out-Null
        foreach ($candidate in @($candidates.ToArray())) {
            $frequencyHz = [long]$candidate.frequencyHz
            $candidateSafeName = ConvertTo-SafeName ("{0}-{1}" -f $candidate.source, $frequencyHz)
            $candidateDir = Join-Path $passDir $candidateSafeName
            New-Item -ItemType Directory -Force -Path $candidateDir | Out-Null

            if (-not [string]::Equals([string]$candidate.source, "current-vfo", [StringComparison]::OrdinalIgnoreCase)) {
                $retuneUtc = [DateTimeOffset]::UtcNow
                $retuneRecord = [ordered]@{
                    pass = $pass
                    frequencyHz = $frequencyHz
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
                $windowDir = Join-Path $candidateDir ("window-{0:00}" -f $window)
                New-Item -ItemType Directory -Force -Path $windowDir | Out-Null

                $watchReport = Join-Path $windowDir "live-diagnostics-watch.json"
                $watchJsonl = Join-Path $windowDir "live-diagnostics-watch.jsonl"
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
                        candidateSource = [string]$candidate.source
                        window = $window
                        reportPath = $portableWatchReport
                        jsonlPath = $portableWatchJsonl
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
                $weakOutputGap = Get-NullableDoubleValue (Get-JsonValue $weak "weakStrongOutputGapDb")
                $weakFinalGap = Get-NullableDoubleValue (Get-JsonValue $weak "weakStrongFinalAudioGapDb")
                $speechQualifiedWeakInput = Get-IntValue (Get-JsonValue $weak "speechQualifiedWeakInputSampleCount")
                $speechQualifiedStrongInput = Get-IntValue (Get-JsonValue $weak "speechQualifiedStrongInputSampleCount")
                $passbandQualifiedWeakInput = Get-IntValue (Get-JsonValue $weak "passbandQualifiedWeakInputSampleCount")
                $passbandQualifiedStrongInput = Get-IntValue (Get-JsonValue $weak "passbandQualifiedStrongInputSampleCount")
                $frontendTopPeakWatch = Get-JsonValue $report "frontendTopPeakWatch"
                $frontendTopPeakSampleCount = Get-IntValue (Get-JsonValue $frontendTopPeakWatch "sampleCount")
                $frontendNearPassbandSampleCount = Get-IntValue (Get-JsonValue $frontendTopPeakWatch "nearPassbandSampleCount")
                $readyTrace = Test-Truthy (Get-JsonValue $report "readyForBenchmarkTrace")
                $mixedReady = Test-Truthy (Get-JsonValue $weak "mixedWeakStrongEvidenceReady")
                $score = Get-HuntScore `
                    -WeakInputSampleCount $weakInput `
                    -StrongInputSampleCount $strongInput `
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
                    window = $window
                    reportPath = $portableWatchReport
                    jsonlPath = $portableWatchJsonl
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
                    weakRecoveredSampleCount = Get-IntValue (Get-JsonValue $weak "weakRecoveredSampleCount")
                    weakDropoutSampleCount = Get-IntValue (Get-JsonValue $weak "weakDropoutSampleCount")
                    weakDropoutCandidateLossSampleCount = Get-IntValue (Get-JsonValue $weak "weakDropoutCandidateLossSampleCount")
                    hotMakeupSampleCount = Get-IntValue (Get-JsonValue $weak "hotMakeupSampleCount")
                    weakStrongOutputGapDb = $weakOutputGap
                    weakStrongFinalAudioGapDb = $weakFinalGap
                    speechQualifiedWeakInputSampleCount = $speechQualifiedWeakInput
                    speechQualifiedStrongInputSampleCount = $speechQualifiedStrongInput
                    speechQualifiedWeakStrongOutputGapDb = Get-NullableDoubleValue (Get-JsonValue $weak "speechQualifiedWeakStrongOutputGapDb")
                    speechQualifiedWeakStrongFinalAudioGapDb = Get-NullableDoubleValue (Get-JsonValue $weak "speechQualifiedWeakStrongFinalAudioGapDb")
                    speechQualifiedMixedWeakStrongEvidenceReady = Test-Truthy (Get-JsonValue $weak "speechQualifiedMixedWeakStrongEvidenceReady")
                    speechQualifiedWeakStrongOutputParityReady = Test-Truthy (Get-JsonValue $weak "speechQualifiedWeakStrongOutputParityReady")
                    speechQualifiedWeakStrongFinalAudioParityReady = Test-Truthy (Get-JsonValue $weak "speechQualifiedWeakStrongFinalAudioParityReady")
                    speechQualifiedMixedWeakStrongEvidenceStatus = [string](Get-JsonValue $weak "speechQualifiedMixedWeakStrongEvidenceStatus")
                    passbandQualifiedWeakInputSampleCount = $passbandQualifiedWeakInput
                    passbandQualifiedStrongInputSampleCount = $passbandQualifiedStrongInput
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
    try {
        $restoreResult = Restore-OriginalTuning `
            -BaseUrl $base `
            -OriginalVfo $originalVfo `
            -OriginalRadioLo $originalRadioLo `
            -RequestTimeoutSec $TimeoutSec `
            -SettleMs ([Math]::Min(1000, [Math]::Max(0, $SettleMs))) `
            -MaxAttempts 3 `
            -SkipCertificate:$SkipCertificateCheck
        $afterRestore = $restoreResult.state
        $restoredVfo = Get-NullableLongValue (Get-JsonValue $afterRestore "vfoHz")
        $restoredRadioLo = Get-NullableLongValue (Get-JsonValue $afterRestore "radioLoHz")
        if (-not (Test-Truthy $restoreResult.ok)) {
            throw "state did not return to original VFO/LO after restore attempts"
        }
    }
    catch {
        $restoreError = $_.Exception.Message
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
$candidateWeakLossTotal = 0
$hotMakeupTotal = 0
$hardBlockerTotal = 0
$pumpingRiskRunCount = 0
$mixedReadyRunCount = 0
$speechQualifiedWeakTotal = 0
$speechQualifiedStrongTotal = 0
$passbandQualifiedWeakTotal = 0
$passbandQualifiedStrongTotal = 0
$frontendNearPassbandTotal = 0
foreach ($run in $runArray) {
    $weakTotal += Get-IntValue $run.weakInputSampleCount
    $strongTotal += Get-IntValue $run.strongInputSampleCount
    $candidateWeakLossTotal += Get-IntValue $run.weakDropoutCandidateLossSampleCount
    $hotMakeupTotal += Get-IntValue $run.hotMakeupSampleCount
    $hardBlockerTotal += Get-IntValue $run.hardBlockerSampleCount
    $speechQualifiedWeakTotal += Get-IntValue $run.speechQualifiedWeakInputSampleCount
    $speechQualifiedStrongTotal += Get-IntValue $run.speechQualifiedStrongInputSampleCount
    $passbandQualifiedWeakTotal += Get-IntValue $run.passbandQualifiedWeakInputSampleCount
    $passbandQualifiedStrongTotal += Get-IntValue $run.passbandQualifiedStrongInputSampleCount
    $frontendNearPassbandTotal += Get-IntValue $run.frontendNearPassbandSampleCount
    if (Test-Truthy $run.agcPumpingRisk) {
        $pumpingRiskRunCount++
    }
    if (Test-Truthy $run.mixedWeakStrongEvidenceReady) {
        $mixedReadyRunCount++
    }
}

$peakCandidateArray = @($allPeakCandidates.ToArray())
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
if ($mixedReadyRunCount -gt 0 -and $null -ne $bestRun) {
    $recommendations.Add("A mixed weak+strong NR5/SPNR run was found; promote '$($bestRun.reportPath)' into live history and compare it against current-Zeus/Thetis-parity windows before tuning defaults.") | Out-Null
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

$completedUtc = [DateTimeOffset]::UtcNow
$reportObject = [ordered]@{
    schemaVersion = 1
    tool = "run-dsp-g2-rx-peak-hunt"
    generatedUtc = $completedUtc.ToString("o")
    startedUtc = $startedUtc.ToString("o")
    completedUtc = $completedUtc.ToString("o")
    durationMs = [int]($completedUtc - $startedUtc).TotalMilliseconds
    ok = ([string]::IsNullOrWhiteSpace($scanError) -and $null -eq $restoreError -and $runArray.Count -gt 0)
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
    allowRetune = [bool]$AllowRetune
    skipCurrentVfo = [bool]$SkipCurrentVfo
    stopOnReady = [bool]$StopOnReady
    samplesPerWindow = $SamplesPerWindow
    intervalMs = $IntervalMs
    windowsPerPeak = $WindowsPerPeak
    passCount = $PassCount
    passDelaySec = $PassDelaySec
    completedPassCount = $completedPassCount
    scanPassCount = $scanPassArray.Count
    candidateFrequencyHz = @($operatorCandidateFrequencyHz)
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
    safety = [ordered]@{
        rxOnly = $true
        txEndpointsTouched = $false
        vfoRetuneRequiresAllowRetune = $true
        originalVfoRestoreAttempted = $true
        originalVfoRestored = ($null -ne $restoredVfo -and [long]$restoredVfo -eq [long]$originalVfo)
        originalRadioLoRestoreAttempted = ($null -ne $originalRadioLo -and $originalRadioLo -gt 0)
        originalRadioLoRestored = ($null -ne $originalRadioLo -and $originalRadioLo -gt 0 -and $null -ne $restoredRadioLo -and [long]$restoredRadioLo -eq [long]$originalRadioLo)
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
    autoPhoneClusterCandidates = @($autoPhoneClusterCandidates)
    peakCandidates = @($peakCandidateArray)
    plannedRunCount = $plannedRunCount
    actualRunCount = $runArray.Count
    failedRunCount = @($runArray | Where-Object { -not (Test-Truthy $_.ok) }).Count
    mixedWeakStrongReady = ($mixedReadyRunCount -gt 0)
    mixedWeakStrongReadyRunCount = $mixedReadyRunCount
    weakInputSampleCount = $weakTotal
    strongInputSampleCount = $strongTotal
    speechQualifiedWeakInputSampleCount = $speechQualifiedWeakTotal
    speechQualifiedStrongInputSampleCount = $speechQualifiedStrongTotal
    passbandQualifiedWeakInputSampleCount = $passbandQualifiedWeakTotal
    passbandQualifiedStrongInputSampleCount = $passbandQualifiedStrongTotal
    frontendNearPassbandSampleCount = $frontendNearPassbandTotal
    candidateWeakLossSampleCount = $candidateWeakLossTotal
    hotMakeupSampleCount = $hotMakeupTotal
    hardBlockerSampleCount = $hardBlockerTotal
    agcPumpingRiskRunCount = $pumpingRiskRunCount
    bestRun = $bestRun
    retuneAttempts = @($retuneAttempts.ToArray())
    scanPasses = @($scanPassArray)
    stoppedEarly = $stoppedEarly
    runs = @($runArray)
    recommendations = @($recommendations.ToArray())
}

Write-JsonFile -Path $ReportPath -Value $reportObject

if ($JsonOnly) {
    $reportObject | ConvertTo-Json -Depth 64
}
else {
    Write-Host "G2 RX peak hunt report: $ReportPath"
    Write-Host "Original VFO: $originalVfo Hz; restored VFO: $restoredVfo Hz"
    Write-Host "Original radio LO: $originalRadioLo Hz; restored radio LO: $restoredRadioLo Hz"
    Write-Host "Runs: $($reportObject.actualRunCount), mixed weak+strong ready: $($reportObject.mixedWeakStrongReady), weak samples: $weakTotal, strong samples: $strongTotal"
    if ($null -ne $bestRun) {
        Write-Host "Best run: $($bestRun.frequencyHz) Hz score=$($bestRun.score) status=$($bestRun.mixedWeakStrongEvidenceStatus) report=$($bestRun.reportPath)"
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
