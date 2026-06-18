param(
    [string]$CandidatePath = "",

    [string]$SnapshotPath = "",

    [string]$BundleDir = "",

    [string]$ReportPath = "",

    [string]$MarkdownPath = "",

    [switch]$FailOnUnsafe,

    [switch]$NoMarkdown,

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

    $json = $Value | ConvertTo-Json -Depth 48
    Set-Content -LiteralPath $Path -Value $json -Encoding UTF8
}

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    $getFileHash = Get-Command Get-FileHash -ErrorAction SilentlyContinue
    if ($null -ne $getFileHash) {
        return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    }

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            $hashBytes = $sha256.ComputeHash($stream)
            return ([System.BitConverter]::ToString($hashBytes) -replace "-", "").ToLowerInvariant()
        }
        finally {
            $sha256.Dispose()
        }
    }
    finally {
        $stream.Dispose()
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

function Test-Truthy {
    param($Value)

    if ($null -eq $Value) {
        return $false
    }
    if ($Value -is [bool]) {
        return [bool]$Value
    }

    $text = ([string]$Value).Trim()
    return ($text -ieq "true" -or $text -eq "1" -or $text -ieq "yes")
}

function Get-NumericValueOrDefault {
    param(
        $Value,
        [double]$Default = 0
    )

    if ($null -eq $Value) {
        return $Default
    }

    $parsed = 0.0
    if ([double]::TryParse([string]$Value, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$parsed)) {
        return $parsed
    }

    return $Default
}

function ConvertTo-CandidateId {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return ""
    }

    return ($Value.Trim().ToLowerInvariant() -replace "[^a-z0-9]+", "-").Trim("-")
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
        $rootFull = [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Root).Path)
        $pathFull = [System.IO.Path]::GetFullPath($Path)
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

        if ([string]::IsNullOrWhiteSpace($relative) -or
            $relative -eq ".." -or
            $relative.StartsWith("../", [StringComparison]::Ordinal) -or
            $relative.StartsWith("..\", [StringComparison]::Ordinal) -or
            [System.IO.Path]::IsPathRooted($relative)) {
            return $Path
        }

        return $relative -replace "\\", "/"
    }
    catch {
        return $Path
    }
}

function Get-CandidateEntries {
    param($Value)

    if ($null -eq $Value) {
        return @()
    }

    if ($Value -is [System.Array]) {
        return @($Value)
    }

    foreach ($name in @("externalEngineCandidates", "candidates", "items", "engines")) {
        $items = @(Get-JsonArray $Value $name)
        if ($items.Count -gt 0) {
            return @($items)
        }
    }

    $id = ConvertTo-CandidateId ([string](Get-JsonValue $Value "id"))
    if (-not [string]::IsNullOrWhiteSpace($id)) {
        return @($Value)
    }

    return @()
}

function Test-TextHas {
    param(
        [string]$Text,
        [string[]]$Tokens
    )

    foreach ($token in $Tokens) {
        if ($Text -notlike "*$token*") {
            return $false
        }
    }

    return $true
}

function Get-RiskTier {
    param([string]$Text)

    $value = $Text.ToLowerInvariant()
    if ($value -like "*high*") { return "high" }
    if ($value -like "*medium*") { return "medium" }
    if ($value -like "*low*") { return "low" }
    if ([string]::IsNullOrWhiteSpace($Text)) { return "missing" }
    return "unknown"
}

function Get-RiskScore {
    param([string]$Tier)

    switch ($Tier) {
        "high" { return 3 }
        "medium" { return 2 }
        "low" { return 1 }
        default { return 2 }
    }
}

function New-CandidateSummary {
    param(
        $Candidate,
        [bool]$InSnapshot
    )

    $id = ConvertTo-CandidateId ([string](Get-JsonValue $Candidate "id"))
    $name = [string](Get-JsonValue $Candidate "name")
    $integrationPoint = [string](Get-JsonValue $Candidate "integrationPoint")
    $defaultState = [string](Get-JsonValue $Candidate "defaultState")
    $rolloutPolicy = [string](Get-JsonValue $Candidate "rolloutPolicy")
    $evaluationStage = [string](Get-JsonValue $Candidate "evaluationStage")
    $license = [string](Get-JsonValue $Candidate "license")
    $packagingStatus = [string](Get-JsonValue $Candidate "packagingStatus")
    $runtimeRisk = [string](Get-JsonValue $Candidate "runtimeRisk")
    $latencyRisk = [string](Get-JsonValue $Candidate "latencyRisk")
    $radioSafetyRisk = [string](Get-JsonValue $Candidate "radioSafetyRisk")
    $allowedSignalPaths = @(Get-JsonArray $Candidate "allowedSignalPaths")
    $forbiddenSignalPaths = @(Get-JsonArray $Candidate "forbiddenSignalPaths")
    $requiredControls = @(Get-JsonArray $Candidate "requiredControls")
    $fallbackPolicy = [string](Get-JsonValue $Candidate "fallbackPolicy")
    $benchmarks = @(Get-JsonArray $Candidate "requiredBenchmarks")
    $evidence = @(Get-JsonArray $Candidate "requiredEvidence")
    $blockers = @(Get-JsonArray $Candidate "blockers")
    $references = @(Get-JsonArray $Candidate "referenceUrls")
    $riskText = "$runtimeRisk $latencyRisk $radioSafetyRisk"
    $evidenceText = "$($evidence -join ' ') $radioSafetyRisk $integrationPoint"

    $issues = New-Object System.Collections.Generic.List[string]
    if ([string]::IsNullOrWhiteSpace($id)) { $issues.Add("id-missing") | Out-Null }
    if ($defaultState -ine "off") { $issues.Add("default-not-off") | Out-Null }
    if ($rolloutPolicy -notlike "*opt-in*" -or $rolloutPolicy -notlike "*bakeoff*") { $issues.Add("rollout-not-opt-in-bakeoff") | Out-Null }
    if ($integrationPoint -notlike "*post-demod*") { $issues.Add("integration-not-post-demod") | Out-Null }
    if ([string]::IsNullOrWhiteSpace($license)) { $issues.Add("license-missing") | Out-Null }
    if ([string]::IsNullOrWhiteSpace($packagingStatus)) { $issues.Add("packaging-status-missing") | Out-Null }
    if ([string]::IsNullOrWhiteSpace($runtimeRisk)) { $issues.Add("runtime-risk-missing") | Out-Null }
    if ([string]::IsNullOrWhiteSpace($latencyRisk)) { $issues.Add("latency-risk-missing") | Out-Null }
    if ([string]::IsNullOrWhiteSpace($radioSafetyRisk)) { $issues.Add("radio-safety-risk-missing") | Out-Null }
    if ([string]::IsNullOrWhiteSpace($evaluationStage)) { $issues.Add("evaluation-stage-missing") | Out-Null }
    if ($allowedSignalPaths.Count -le 0 -or ($allowedSignalPaths -join " ") -notlike "*post-demod*") { $issues.Add("allowed-paths-invalid") | Out-Null }
    if ($forbiddenSignalPaths.Count -le 0 -or
        ($forbiddenSignalPaths -join " ") -notlike "*raw-wdsp-iq*" -or
        ($forbiddenSignalPaths -join " ") -notlike "*puresignal*") { $issues.Add("forbidden-paths-invalid") | Out-Null }
    $requiredControlText = $requiredControls -join " "
    foreach ($requiredControlToken in @("operator-visible-opt-in", "clean-bypass-fallback", "no-raw-wdsp-iq-replacement", "no-tx-or-puresignal-coupling")) {
        if ($requiredControlText -notlike "*$requiredControlToken*") {
            $issues.Add("required-control-missing:$requiredControlToken") | Out-Null
        }
    }
    if ([string]::IsNullOrWhiteSpace($fallbackPolicy) -or ($fallbackPolicy -notlike "*fallback*" -and $fallbackPolicy -notlike "*fall back*" -and $fallbackPolicy -notlike "*bypass*")) { $issues.Add("fallback-policy-missing") | Out-Null }
    if ($benchmarks.Count -lt 3) { $issues.Add("benchmark-coverage-incomplete") | Out-Null }
    if ($evidence.Count -lt 3) { $issues.Add("evidence-coverage-incomplete") | Out-Null }
    if ($blockers.Count -le 0) { $issues.Add("blockers-missing") | Out-Null }
    if ($references.Count -le 0) { $issues.Add("references-missing") | Out-Null }
    if (-not $InSnapshot) { $issues.Add("snapshot-missing") | Out-Null }

    if ($id -eq "webrtc-apm" -and -not (Test-TextHas $evidenceText @("AEC", "AGC", "disabled"))) {
        $issues.Add("webrtc-aec-agc-disabled-gate-missing") | Out-Null
    }
    if ($id -eq "speexdsp" -and -not (Test-TextHas $evidenceText @("pumping"))) {
        $issues.Add("speex-no-pumping-gate-missing") | Out-Null
    }
    if (($id -eq "rnnoise" -or $id -eq "rmnoise" -or $id -eq "dpdfnet" -or $id -eq "deepfilternet" -or $id -eq "clearervoice-studio") -and
        -not ($evidenceText -like "*weak*" -and ($evidenceText -like "*CW*" -or $evidenceText -like "*carrier*" -or $evidenceText -like "*bypass*"))) {
        $issues.Add("neural-weak-signal-bypass-gate-missing") | Out-Null
    }
    if ($id -eq "rmnoise" -and -not (Test-TextHas $evidenceText @("consent", "privacy", "service", "fallback"))) {
        $issues.Add("rmnoise-consent-privacy-service-fallback-gate-missing") | Out-Null
    }

    $runtimeTier = Get-RiskTier $runtimeRisk
    $latencyTier = Get-RiskTier $latencyRisk
    $radioTier = Get-RiskTier $radioSafetyRisk
    $combinedRiskScore = (Get-RiskScore $runtimeTier) + (Get-RiskScore $latencyTier) + (Get-RiskScore $radioTier)
    $safeForBakeoff = ($issues.Count -eq 0)
    $integrationBlocked = ($blockers.Count -gt 0)

    return [ordered]@{
        id = $id
        name = $name
        family = [string](Get-JsonValue $Candidate "family")
        inSnapshot = $InSnapshot
        defaultState = $defaultState
        rolloutPolicy = $rolloutPolicy
        evaluationStage = $evaluationStage
        integrationPoint = $integrationPoint
        allowedSignalPaths = @($allowedSignalPaths)
        forbiddenSignalPaths = @($forbiddenSignalPaths)
        requiredControls = @($requiredControls)
        fallbackPolicy = $fallbackPolicy
        license = $license
        packagingStatus = $packagingStatus
        runtimeRisk = $runtimeRisk
        latencyRisk = $latencyRisk
        radioSafetyRisk = $radioSafetyRisk
        runtimeRiskTier = $runtimeTier
        latencyRiskTier = $latencyTier
        radioSafetyRiskTier = $radioTier
        combinedRiskScore = $combinedRiskScore
        requiredBenchmarkCount = $benchmarks.Count
        requiredEvidenceCount = $evidence.Count
        allowedSignalPathCount = $allowedSignalPaths.Count
        forbiddenSignalPathCount = $forbiddenSignalPaths.Count
        requiredControlCount = $requiredControls.Count
        blockerCount = $blockers.Count
        referenceUrlCount = $references.Count
        safeForBakeoff = $safeForBakeoff
        integrationBlocked = $integrationBlocked
        integrationStatus = if ($safeForBakeoff -and $integrationBlocked) { "opt-in-bakeoff-only-blocked-for-integration" } elseif ($safeForBakeoff) { "needs-explicit-approval-before-integration" } else { "unsafe-catalog-entry" }
        issues = @($issues.ToArray())
        requiredBenchmarks = @($benchmarks)
        requiredEvidence = @($evidence)
        blockers = @($blockers)
        referenceUrls = @($references)
    }
}

function Get-BakeoffScenariosForCandidate {
    param($CandidateSummary)

    $id = [string](Get-JsonValue $CandidateSummary "id")
    switch ($id) {
        "rnnoise" {
            return @(
                [ordered]@{
                    scenarioId = "ssb-like-speech-post-demod"
                    purpose = "Measure speech-denoise benefit after WDSP demodulation."
                    acceptanceGates = @("Improves speech artifact score without weak-signal loss.", "No added clipping or audio RMS pumping.", "Latency remains inside the post-demod budget.")
                },
                [ordered]@{
                    scenarioId = "weak-cw-carrier-bypass"
                    purpose = "Prove weak CW/carrier and non-speech HF content bypass safely."
                    acceptanceGates = @("Weak-carrier SNR/SINAD does not regress.", "Bypass/fallback is clean and measurable.", "No speech model artifacts are introduced on CW.")
                },
                [ordered]@{
                    scenarioId = "noise-only-gating"
                    purpose = "Verify the speech denoiser does not create false-open noise artifacts."
                    acceptanceGates = @("Noise-only output stays bounded.", "No VAD chatter or burbling.", "Fallback remains bit-clean when disabled.")
                }
            )
        }
        "rmnoise" {
            return @(
                [ordered]@{
                    scenarioId = "ssb-like-speech-post-demod"
                    purpose = "Measure offline RM Noise speech-denoise benefit after WDSP demodulation."
                    acceptanceGates = @("Improves speech artifact score without weak-signal loss.", "No added clipping or audio RMS pumping.", "Offline fixture provenance is reproducible.")
                },
                [ordered]@{
                    scenarioId = "weak-cw-carrier-bypass"
                    purpose = "Prove weak CW/carrier and non-speech HF content bypass the service candidate safely."
                    acceptanceGates = @("Weak-carrier SNR/SINAD does not regress.", "Bypass/fallback is deterministic.", "No service-trained speech artifacts are introduced on CW.")
                },
                [ordered]@{
                    scenarioId = "service-unavailable-bypass"
                    purpose = "Verify network/service failure keeps current NR5 audio intact."
                    acceptanceGates = @("Service unavailable falls back immediately.", "No live cloud stream is required for RX.", "Operator consent and privacy gates are documented.")
                }
            )
        }
        "dpdfnet" {
            return @(
                [ordered]@{
                    scenarioId = "ssb-like-speech-post-demod"
                    purpose = "Measure causal neural speech enhancement after WDSP demodulation."
                    acceptanceGates = @("Improves weak SSB speech artifact score beyond NR5/SPNR.", "No weak-signal deletion or clipped peaks.", "No added audio RMS pumping.")
                },
                [ordered]@{
                    scenarioId = "weak-ssb-speech-latency"
                    purpose = "Bound 48 kHz streaming CPU and latency on weak speech."
                    acceptanceGates = @("Latency and allocations are measured on G2-class hardware.", "No realtime underrun or monitor backlog.", "Weak speech remains intelligible.")
                },
                [ordered]@{
                    scenarioId = "weak-cw-carrier-bypass"
                    purpose = "Prove the model bypasses non-speech HF content deterministically."
                    acceptanceGates = @("Weak-carrier SNR/SINAD does not regress.", "Bypass/fallback is deterministic.", "No raw IQ path replacement is used.")
                }
            )
        }
        "deepfilternet" {
            return @(
                [ordered]@{
                    scenarioId = "ssb-like-speech-post-demod"
                    purpose = "Measure full-band neural enhancement on demodulated speech."
                    acceptanceGates = @("Improves speech artifact score beyond NR5/SPNR.", "No weak-signal intelligibility loss.", "No musical-noise or burbling artifacts.")
                },
                [ordered]@{
                    scenarioId = "weak-ssb-speech-latency"
                    purpose = "Bound CPU and latency on weak speech."
                    acceptanceGates = @("Latency and allocation budgets are measured.", "No realtime underrun or monitor backlog.", "Weak speech remains intelligible.")
                },
                [ordered]@{
                    scenarioId = "weak-cw-carrier-bypass"
                    purpose = "Prove the model is bypassed for non-speech HF content."
                    acceptanceGates = @("Carrier preservation is neutral or better.", "Bypass selection is deterministic.", "No raw IQ path replacement is used.")
                }
            )
        }
        "clearervoice-studio" {
            return @(
                [ordered]@{
                    scenarioId = "offline-ssb-like-speech"
                    purpose = "Measure offline restoration on recorded demodulated SSB speech."
                    acceptanceGates = @("Improves speech artifact score without weak-signal deletion.", "No over-smoothing or pumping.", "Fixture provenance is reproducible.")
                },
                [ordered]@{
                    scenarioId = "weak-cw-carrier-bypass"
                    purpose = "Prove weak CW/carrier and non-speech HF content bypass the offline suite safely."
                    acceptanceGates = @("Weak-carrier SNR/SINAD does not regress.", "Bypass/fallback is deterministic.", "No speech-restoration artifacts are introduced on CW.")
                },
                [ordered]@{
                    scenarioId = "offline-recording-consent-provenance"
                    purpose = "Verify recording consent, model provenance, and offline-only constraints."
                    acceptanceGates = @("Operator recording consent is documented.", "Model provenance and hashes are recorded.", "No live realtime default path is required.")
                }
            )
        }
        "speexdsp" {
            return @(
                [ordered]@{
                    scenarioId = "ssb-like-speech-post-demod"
                    purpose = "Evaluate classic post-demod noise suppression as a lightweight baseline."
                    acceptanceGates = @("Denoise improves without pumping.", "WDSP AGC remains the only active AGC.", "No clipping or RMS movement regression.")
                },
                [ordered]@{
                    scenarioId = "agc-disabled-no-pumping"
                    purpose = "Prove Speex AGC/VAD behavior cannot fight WDSP AGC."
                    acceptanceGates = @("Speex AGC remains disabled.", "No VAD gate chatter.", "No NR5/SPNR level-stability regression.")
                },
                [ordered]@{
                    scenarioId = "noise-only-gating"
                    purpose = "Check denoise behavior on no-signal receive audio."
                    acceptanceGates = @("Noise floor does not false-open.", "No audible artifacts in operator notes.", "Fallback path is clean.")
                }
            )
        }
        "webrtc-apm" {
            return @(
                [ordered]@{
                    scenarioId = "ns-vad-only-post-demod"
                    purpose = "Evaluate WebRTC noise suppression/VAD with radio-unsafe modules disabled."
                    acceptanceGates = @("AEC remains disabled.", "AGC remains disabled.", "High-pass/default voice-chain behavior remains disabled unless separately approved.")
                },
                [ordered]@{
                    scenarioId = "ssb-like-speech-post-demod"
                    purpose = "Measure speech enhancement without corrupting radio gain staging."
                    acceptanceGates = @("No WDSP AGC interaction.", "No clipping or RMS movement regression.", "Speech artifact score improves.")
                },
                [ordered]@{
                    scenarioId = "weak-cw-carrier-bypass"
                    purpose = "Verify non-speech HF content bypasses WebRTC speech processing safely."
                    acceptanceGates = @("Weak carrier preservation is neutral or better.", "Bypass/fallback is deterministic.", "No raw IQ path replacement is used.")
                }
            )
        }
        default {
            return @(
                [ordered]@{
                    scenarioId = "ssb-like-speech-post-demod"
                    purpose = "Evaluate opt-in post-demod speech/audio behavior."
                    acceptanceGates = @("No weak-signal loss.", "No pumping or clipping regression.", "Fallback remains clean.")
                },
                [ordered]@{
                    scenarioId = "noise-only-gating"
                    purpose = "Check noise-only artifacts and fallback."
                    acceptanceGates = @("No false-open artifacts.", "No realtime budget regression.", "Candidate stays off by default.")
                }
            )
        }
    }
}

function New-CandidateBakeoffPlan {
    param($CandidateSummary)

    $id = [string](Get-JsonValue $CandidateSummary "id")
    $safeForBakeoff = [bool](Get-JsonValue $CandidateSummary "safeForBakeoff")
    $integrationBlocked = [bool](Get-JsonValue $CandidateSummary "integrationBlocked")
    $scenarios = @(Get-BakeoffScenariosForCandidate -CandidateSummary $CandidateSummary)
    $scenarioRecords = New-Object System.Collections.Generic.List[object]
    $priority = 1
    foreach ($scenario in @($scenarios)) {
        $scenarioRecords.Add([ordered]@{
            priority = $priority
            scenarioId = [string](Get-JsonValue $scenario "scenarioId")
            purpose = [string](Get-JsonValue $scenario "purpose")
            requiredComparisons = @("current-zeus", "nr5-spnr", "candidate-external-engine-opt-in")
            acceptanceGates = @(Get-JsonArray $scenario "acceptanceGates")
        }) | Out-Null
        $priority++
    }
    $planRequiredControls = @((@(
            "off-by-default",
            "post-demod-only",
            "clean-bypass-fallback",
            "operator-visible-opt-in"
        ) + @(Get-JsonArray $CandidateSummary "requiredControls")) | Select-Object -Unique)

    return [ordered]@{
        candidateId = $id
        readyForBakeoff = $safeForBakeoff
        blockedForIntegration = $integrationBlocked
        integrationPoint = [string](Get-JsonValue $CandidateSummary "integrationPoint")
        defaultState = [string](Get-JsonValue $CandidateSummary "defaultState")
        evaluationStage = [string](Get-JsonValue $CandidateSummary "evaluationStage")
        allowedSignalPaths = @(Get-JsonArray $CandidateSummary "allowedSignalPaths")
        forbiddenSignalPaths = @(Get-JsonArray $CandidateSummary "forbiddenSignalPaths")
        fallbackPolicy = [string](Get-JsonValue $CandidateSummary "fallbackPolicy")
        scenarioCount = $scenarioRecords.Count
        scenarios = @($scenarioRecords.ToArray())
        requiredControls = @($planRequiredControls)
        packageAndRuntimeGates = @(
            "license-review-complete",
            "runtime-package-reviewed",
            "cpu-latency-measured-on-g2",
            "allocation-budget-measured"
        )
        radioSafetyGates = @(
            "no-raw-wdsp-iq-replacement",
            "no-default-behavior-change",
            "no-tx-or-puresignal-coupling",
            "cross-radio-validation-before-graduation"
        )
    }
}

function Get-ExternalBakeoffEvaluationOrder {
    param($CandidateSummaries)

    $orderedCandidates = @($CandidateSummaries | Sort-Object `
            @{ Expression = { if (Test-Truthy (Get-JsonValue $_ "safeForBakeoff")) { 0 } else { 1 } } }, `
            @{ Expression = { [int](Get-NumericValueOrDefault (Get-JsonValue $_ "combinedRiskScore") 999999) } }, `
            @{ Expression = { [int](Get-NumericValueOrDefault (Get-JsonValue $_ "blockerCount") 999999) } }, `
            @{ Expression = { @(Get-JsonArray $_ "issues").Count } }, `
            @{ Expression = { ConvertTo-CandidateId ([string](Get-JsonValue $_ "id")) } })

    $records = New-Object System.Collections.Generic.List[object]
    $priority = 1
    foreach ($candidate in @($orderedCandidates)) {
        $candidateId = ConvertTo-CandidateId ([string](Get-JsonValue $candidate "id"))
        if ([string]::IsNullOrWhiteSpace($candidateId)) {
            continue
        }

        $readyForBakeoff = Test-Truthy (Get-JsonValue $candidate "safeForBakeoff")
        $blockedForIntegration = Test-Truthy (Get-JsonValue $candidate "integrationBlocked")
        $combinedRiskScore = [int](Get-NumericValueOrDefault (Get-JsonValue $candidate "combinedRiskScore") 999999)
        $blockerCount = [int](Get-NumericValueOrDefault (Get-JsonValue $candidate "blockerCount") 999999)
        $issueCount = @(Get-JsonArray $candidate "issues").Count

        $records.Add([ordered]@{
                priority = $priority
                candidateId = $candidateId
                readyForBakeoff = $readyForBakeoff
                blockedForIntegration = $blockedForIntegration
                combinedRiskScore = $combinedRiskScore
                blockerCount = $blockerCount
                issueCount = $issueCount
                runtimeRiskTier = [string](Get-JsonValue $candidate "runtimeRiskTier")
                latencyRiskTier = [string](Get-JsonValue $candidate "latencyRiskTier")
                radioSafetyRiskTier = [string](Get-JsonValue $candidate "radioSafetyRiskTier")
                rationale = if ($readyForBakeoff) {
                    "safe opt-in post-demod candidate; evaluate lower-risk entries first"
                }
                else {
                    "unsafe catalog entry; fix issues before bakeoff"
                }
            }) | Out-Null
        $priority++
    }

    return @($records.ToArray())
}

function Get-ExternalBakeoffFirstSafeCandidateId {
    param($EvaluationOrder)

    foreach ($record in @($EvaluationOrder)) {
        if (Test-Truthy (Get-JsonValue $record "readyForBakeoff")) {
            return ConvertTo-CandidateId ([string](Get-JsonValue $record "candidateId"))
        }
    }

    return ""
}

function New-ExternalBakeoffPlan {
    param(
        $CandidateSummaries,
        $EvaluationOrder = @()
    )

    $candidatePlans = New-Object System.Collections.Generic.List[object]
    foreach ($candidate in @($CandidateSummaries)) {
        $candidatePlans.Add((New-CandidateBakeoffPlan -CandidateSummary $candidate)) | Out-Null
    }

    $evaluationOrderRecords = @($EvaluationOrder)
    if ($evaluationOrderRecords.Count -eq 0 -and @($CandidateSummaries).Count -gt 0) {
        $evaluationOrderRecords = @(Get-ExternalBakeoffEvaluationOrder -CandidateSummaries $CandidateSummaries)
    }
    $firstSafeCandidateId = Get-ExternalBakeoffFirstSafeCandidateId -EvaluationOrder $evaluationOrderRecords

    $allScenarioIds = @($candidatePlans.ToArray() | ForEach-Object {
        foreach ($scenario in @(Get-JsonArray $_ "scenarios")) {
            [string](Get-JsonValue $scenario "scenarioId")
        }
    } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)

    return [ordered]@{
        planScope = "opt-in-post-demod-bakeoff-only"
        defaultBehaviorChangeReady = $false
        rawWdspIqReplacementAllowed = $false
        txPathAllowed = $false
        requiredComparisons = @("current-zeus", "nr5-spnr", "candidate-external-engine-opt-in")
        requiredHardwareEvidence = @("G2 first-pass live evidence", "non-G2 cross-radio validation before graduation")
        requiredOperatorEvidence = @("speech artifact notes", "weak-signal readability notes", "noise-only artifact notes")
        firstSafeBakeoffCandidateId = $firstSafeCandidateId
        firstSafeBakeoffRationale = if ([string]::IsNullOrWhiteSpace($firstSafeCandidateId)) {
            "No external engine candidate is safe for bakeoff; fix unsafe catalog entries before evaluation."
        }
        else {
            "Start with '$firstSafeCandidateId' because it is safe-for-bakeoff and lowest risk in the deterministic opt-in order."
        }
        evaluationOrderCandidateIds = @($evaluationOrderRecords | ForEach-Object { ConvertTo-CandidateId ([string](Get-JsonValue $_ "candidateId")) } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        evaluationOrder = @($evaluationOrderRecords)
        candidatePlanCount = $candidatePlans.Count
        scenarioCount = $allScenarioIds.Count
        scenarioIds = @($allScenarioIds)
        candidatePlans = @($candidatePlans.ToArray())
        fixtureComparisonCommandTemplate = "powershell -NoProfile -ExecutionPolicy Bypass -File tools\compare-dsp-fixture-metrics.ps1 -BaselinePath <current-zeus-fixtures.json> -CandidatePath <external-engine-fixtures.json> -CandidateComparisonId candidate-external-engine-opt-in -FailOnRegression"
        liveMatrixCommandTemplate = "powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-live-diagnostics-matrix.ps1 -ComparisonId candidate-external-engine-opt-in -ScenarioIds <scenario-ids> -Samples 60 -IntervalMs 1000"
    }
}

function Format-MarkdownCell {
    param($Value)

    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) {
        return ""
    }

    return (($text -replace "\r?\n", " ") -replace "\|", "\\|")
}

function Build-MarkdownReport {
    param($Report)

    $candidateSummaries = @(Get-JsonArray $Report "candidateSummaries")
    $requiredCandidateIds = @(Get-JsonArray $Report "requiredCandidateIds")
    $bakeoffPlan = Get-JsonValue $Report "externalBakeoffPlan"
    $evaluationOrder = @(Get-JsonArray $Report "externalBakeoffEvaluationOrder")
    $evaluationOrderCandidateIds = @(Get-JsonArray $Report "externalBakeoffEvaluationOrderCandidateIds")

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# DSP External Engine Bakeoff Report") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add("- Ready for review: $(Get-JsonValue $Report "readyForReview")") | Out-Null
    $lines.Add("- Candidates: $(Get-JsonValue $Report "candidateCount")") | Out-Null
    $lines.Add("- Safe for bakeoff: $(Get-JsonValue $Report "safeForBakeoffCount")") | Out-Null
    $lines.Add("- First safe bakeoff candidate: $(Format-MarkdownCell (Get-JsonValue $Report "firstSafeBakeoffCandidateId"))") | Out-Null
    $lines.Add("- Evaluation order: $(Format-MarkdownCell ($evaluationOrderCandidateIds -join ', '))") | Out-Null
    $lines.Add("- Unsafe catalog entries: $(Get-JsonValue $Report "unsafeCandidateCount")") | Out-Null
    $lines.Add("- Missing required candidates: $(Get-JsonValue $Report "missingCandidateCount")") | Out-Null
    $missingCandidateIds = @(Get-JsonArray $Report "missingCandidateIds")
    if ($missingCandidateIds.Count -gt 0) {
        $lines.Add("- Missing required candidate IDs: $(Format-MarkdownCell ($missingCandidateIds -join ', '))") | Out-Null
    }
    $lines.Add("- Integration-ready candidates: $(Get-JsonValue $Report "integrationReadyCandidateCount")") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add("## Candidate Matrix") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add("| Candidate | Status | Risk | Benchmarks | Evidence | Controls | Forbidden Paths | Blockers | Fallback | Issues |") | Out-Null
    $lines.Add("|---|---|---:|---:|---:|---:|---:|---:|---|---|") | Out-Null
    foreach ($candidate in $candidateSummaries) {
        $issueText = if (@(Get-JsonArray $candidate "issues").Count -eq 0) { "" } else { @(Get-JsonArray $candidate "issues") -join ", " }
        $fallbackText = [string](Get-JsonValue $candidate "fallbackPolicy")
        $lines.Add("| $(Format-MarkdownCell (Get-JsonValue $candidate "id")) | $(Format-MarkdownCell (Get-JsonValue $candidate "integrationStatus")) | $(Get-JsonValue $candidate "combinedRiskScore") | $(Get-JsonValue $candidate "requiredBenchmarkCount") | $(Get-JsonValue $candidate "requiredEvidenceCount") | $(Get-JsonValue $candidate "requiredControlCount") | $(Get-JsonValue $candidate "forbiddenSignalPathCount") | $(Get-JsonValue $candidate "blockerCount") | $(Format-MarkdownCell $fallbackText) | $(Format-MarkdownCell $issueText) |") | Out-Null
    }
    $lines.Add("") | Out-Null
    if ($evaluationOrder.Count -gt 0) {
        $lines.Add("## Evaluation Order") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("| Priority | Candidate | Ready | Risk | Blockers | Issues | Rationale |") | Out-Null
        $lines.Add("|---:|---|---:|---:|---:|---:|---|") | Out-Null
        foreach ($record in $evaluationOrder) {
            $lines.Add("| $(Get-JsonValue $record "priority") | $(Format-MarkdownCell (Get-JsonValue $record "candidateId")) | $(Get-JsonValue $record "readyForBakeoff") | $(Get-JsonValue $record "combinedRiskScore") | $(Get-JsonValue $record "blockerCount") | $(Get-JsonValue $record "issueCount") | $(Format-MarkdownCell (Get-JsonValue $record "rationale")) |") | Out-Null
        }
        $lines.Add("") | Out-Null
    }
    $lines.Add("## Required External Candidates") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add(($requiredCandidateIds -join ", ")) | Out-Null
    $lines.Add("") | Out-Null
    if ($null -ne $bakeoffPlan) {
        $lines.Add("## Bakeoff Plan") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("- Scope: $(Get-JsonValue $bakeoffPlan "planScope")") | Out-Null
        $lines.Add("- Default behavior change ready: $(Get-JsonValue $bakeoffPlan "defaultBehaviorChangeReady")") | Out-Null
        $lines.Add("- Raw WDSP IQ replacement allowed: $(Get-JsonValue $bakeoffPlan "rawWdspIqReplacementAllowed")") | Out-Null
        $lines.Add("- Required comparisons: $((@(Get-JsonArray $bakeoffPlan "requiredComparisons") -join ', '))") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("| Candidate | Ready | Scenarios | Controls |") | Out-Null
        $lines.Add("|---|---:|---:|---|") | Out-Null
        foreach ($plan in @(Get-JsonArray $bakeoffPlan "candidatePlans")) {
            $lines.Add("| $(Get-JsonValue $plan "candidateId") | $(Get-JsonValue $plan "readyForBakeoff") | $(Get-JsonValue $plan "scenarioCount") | $((@(Get-JsonArray $plan "requiredControls") -join ', ')) |") | Out-Null
        }
        $lines.Add("") | Out-Null
    }
    $lines.Add("This report is a review gate for opt-in post-demod bakeoffs only. It does not approve a default DSP behavior change or any raw WDSP IQ replacement.") | Out-Null

    return @($lines.ToArray())
}

$bundlePath = ""
if (-not [string]::IsNullOrWhiteSpace($BundleDir)) {
    $bundlePath = (Resolve-Path -LiteralPath $BundleDir).Path
}

if ([string]::IsNullOrWhiteSpace($CandidatePath)) {
    if ([string]::IsNullOrWhiteSpace($bundlePath)) {
        throw "Specify -CandidatePath or -BundleDir."
    }
    $CandidatePath = Join-Path $bundlePath "external-engine-candidates.json"
}
if ([string]::IsNullOrWhiteSpace($SnapshotPath) -and -not [string]::IsNullOrWhiteSpace($bundlePath)) {
    $defaultSnapshotPath = Join-Path $bundlePath "modernization-snapshot.json"
    if (Test-Path -LiteralPath $defaultSnapshotPath -PathType Leaf) {
        $SnapshotPath = $defaultSnapshotPath
    }
}
if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    if (-not [string]::IsNullOrWhiteSpace($bundlePath)) {
        $ReportPath = Join-Path $bundlePath "artifacts\external-engine-bakeoff-report.json"
    }
    else {
        $ReportPath = Join-Path (Split-Path -Parent (Resolve-Path -LiteralPath $CandidatePath).Path) "external-engine-bakeoff-report.json"
    }
}
if (-not $NoMarkdown -and [string]::IsNullOrWhiteSpace($MarkdownPath)) {
    $reportDir = Split-Path -Parent $ReportPath
    $reportName = [System.IO.Path]::GetFileNameWithoutExtension($ReportPath)
    $MarkdownPath = Join-Path $reportDir "$reportName.md"
}

$candidateJson = Read-JsonFile (Resolve-Path -LiteralPath $CandidatePath).Path
$candidateEntries = @(Get-CandidateEntries $candidateJson)
$snapshotIds = @{}
$snapshotProvided = $false
if (-not [string]::IsNullOrWhiteSpace($SnapshotPath) -and (Test-Path -LiteralPath $SnapshotPath -PathType Leaf)) {
    $snapshotProvided = $true
    $snapshot = Read-JsonFile (Resolve-Path -LiteralPath $SnapshotPath).Path
    foreach ($candidate in (Get-CandidateEntries (Get-JsonValue $snapshot "externalEngineCandidates"))) {
        $snapshotId = ConvertTo-CandidateId ([string](Get-JsonValue $candidate "id"))
        if (-not [string]::IsNullOrWhiteSpace($snapshotId)) {
            $snapshotIds[$snapshotId] = $true
        }
    }
}

$requiredCandidateIds = @("rnnoise", "rmnoise", "dpdfnet", "deepfilternet", "clearervoice-studio", "speexdsp", "webrtc-apm")
$candidateSummaries = New-Object System.Collections.Generic.List[object]
$seen = @{}
foreach ($candidate in $candidateEntries) {
    $id = ConvertTo-CandidateId ([string](Get-JsonValue $candidate "id"))
    if (-not [string]::IsNullOrWhiteSpace($id)) {
        $seen[$id] = $true
    }
    $inSnapshot = ((-not $snapshotProvided) -or $snapshotIds.ContainsKey($id))
    $candidateSummaries.Add((New-CandidateSummary -Candidate $candidate -InSnapshot:$inSnapshot)) | Out-Null
}

$missingCandidateIds = New-Object System.Collections.Generic.List[string]
foreach ($requiredCandidateId in $requiredCandidateIds) {
    if (-not $seen.ContainsKey($requiredCandidateId)) {
        $missingCandidateIds.Add($requiredCandidateId) | Out-Null
    }
}

$unsafeCandidateCount = @($candidateSummaries.ToArray() | Where-Object { -not $_.safeForBakeoff }).Count
$safeForBakeoffCount = @($candidateSummaries.ToArray() | Where-Object { $_.safeForBakeoff }).Count
$blockedCount = @($candidateSummaries.ToArray() | Where-Object { $_.integrationBlocked }).Count
$integrationReadyCount = @($candidateSummaries.ToArray() | Where-Object { $_.safeForBakeoff -and -not $_.integrationBlocked }).Count
$snapshotMismatchCount = @($candidateSummaries.ToArray() | Where-Object { -not $_.inSnapshot }).Count
$readyForReview = ($candidateEntries.Count -gt 0 -and
    $missingCandidateIds.Count -eq 0 -and
    $unsafeCandidateCount -eq 0 -and
    $snapshotMismatchCount -eq 0)
$externalBakeoffEvaluationOrder = @(Get-ExternalBakeoffEvaluationOrder -CandidateSummaries @($candidateSummaries.ToArray()))
$firstSafeBakeoffCandidateId = Get-ExternalBakeoffFirstSafeCandidateId -EvaluationOrder $externalBakeoffEvaluationOrder
$externalBakeoffPlan = New-ExternalBakeoffPlan -CandidateSummaries @($candidateSummaries.ToArray()) -EvaluationOrder $externalBakeoffEvaluationOrder

$report = [ordered]@{
    schemaVersion = 4
    tool = "summarize-dsp-external-engine-candidates"
    generatedUtc = [DateTimeOffset]::UtcNow
    bundleRelativePaths = (-not [string]::IsNullOrWhiteSpace($bundlePath))
    candidatePath = ConvertTo-PortablePath -Root $bundlePath -Path (Resolve-Path -LiteralPath $CandidatePath).Path
    candidateSha256 = Get-FileSha256 (Resolve-Path -LiteralPath $CandidatePath).Path
    snapshotPath = if ([string]::IsNullOrWhiteSpace($SnapshotPath)) { $null } else { ConvertTo-PortablePath -Root $bundlePath -Path (Resolve-Path -LiteralPath $SnapshotPath).Path }
    snapshotSha256 = if ([string]::IsNullOrWhiteSpace($SnapshotPath)) { $null } else { Get-FileSha256 (Resolve-Path -LiteralPath $SnapshotPath).Path }
    reportPath = ConvertTo-PortablePath -Root $bundlePath -Path $ReportPath
    markdownPath = if ($NoMarkdown) { $null } else { ConvertTo-PortablePath -Root $bundlePath -Path $MarkdownPath }
    requiredCandidateIds = $requiredCandidateIds
    readyForReview = $readyForReview
    candidateCount = $candidateEntries.Count
    safeForBakeoffCount = $safeForBakeoffCount
    blockedCandidateCount = $blockedCount
    integrationReadyCandidateCount = $integrationReadyCount
    missingCandidateCount = $missingCandidateIds.Count
    missingCandidateIds = @($missingCandidateIds.ToArray())
    unsafeCandidateCount = $unsafeCandidateCount
    snapshotMismatchCount = $snapshotMismatchCount
    firstSafeBakeoffCandidateId = $firstSafeBakeoffCandidateId
    externalBakeoffEvaluationOrderCandidateIds = @($externalBakeoffEvaluationOrder | ForEach-Object { ConvertTo-CandidateId ([string](Get-JsonValue $_ "candidateId")) } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    externalBakeoffEvaluationOrder = @($externalBakeoffEvaluationOrder)
    candidateSummaries = @($candidateSummaries.ToArray())
    externalBakeoffPlan = $externalBakeoffPlan
    recommendations = if ($readyForReview) {
        @(
            "Use this report as opt-in external-engine bakeoff evidence only; do not enable any candidate by default.",
            "Clear package, license, CPU, latency, fallback, and HF weak-signal blockers before any integration review."
        )
    }
    else {
        @(
            "Do not run an external-engine bakeoff from this catalog until unsafe or missing candidate entries are fixed.",
            "Keep all external engines disabled and preserve WDSP/Thetis parity as the behavior authority."
        )
    }
}

Write-JsonFile -Path $ReportPath -Value $report

if (-not $NoMarkdown) {
    Set-Content -LiteralPath $MarkdownPath -Value (Build-MarkdownReport $report) -Encoding UTF8
}

if ($JsonOnly) {
    $report | ConvertTo-Json -Depth 48
}
else {
    if ($readyForReview) {
        Write-Host "DSP external engine bakeoff report passed: $CandidatePath"
    }
    else {
        Write-Host "DSP external engine bakeoff report found issues: $CandidatePath"
    }
    Write-Host "Report: $ReportPath"
    if (-not $NoMarkdown) {
        Write-Host "Markdown: $MarkdownPath"
    }
    Write-Host "Candidates: $($candidateEntries.Count), Safe for bakeoff: $safeForBakeoffCount, Unsafe: $unsafeCandidateCount, Missing: $($missingCandidateIds.Count)"
}

if ($FailOnUnsafe -and -not $readyForReview) {
    exit 1
}
