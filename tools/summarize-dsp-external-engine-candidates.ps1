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

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
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
    $license = [string](Get-JsonValue $Candidate "license")
    $packagingStatus = [string](Get-JsonValue $Candidate "packagingStatus")
    $runtimeRisk = [string](Get-JsonValue $Candidate "runtimeRisk")
    $latencyRisk = [string](Get-JsonValue $Candidate "latencyRisk")
    $radioSafetyRisk = [string](Get-JsonValue $Candidate "radioSafetyRisk")
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
    if (($id -eq "rnnoise" -or $id -eq "deepfilternet") -and
        -not ($evidenceText -like "*weak*" -and ($evidenceText -like "*CW*" -or $evidenceText -like "*carrier*" -or $evidenceText -like "*bypass*"))) {
        $issues.Add("neural-weak-signal-bypass-gate-missing") | Out-Null
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
        integrationPoint = $integrationPoint
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

    return [ordered]@{
        candidateId = $id
        readyForBakeoff = $safeForBakeoff
        blockedForIntegration = $integrationBlocked
        integrationPoint = [string](Get-JsonValue $CandidateSummary "integrationPoint")
        defaultState = [string](Get-JsonValue $CandidateSummary "defaultState")
        scenarioCount = $scenarioRecords.Count
        scenarios = @($scenarioRecords.ToArray())
        requiredControls = @(
            "off-by-default",
            "post-demod-only",
            "clean-bypass-fallback",
            "operator-visible-opt-in"
        )
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

function New-ExternalBakeoffPlan {
    param($CandidateSummaries)

    $candidatePlans = New-Object System.Collections.Generic.List[object]
    foreach ($candidate in @($CandidateSummaries)) {
        $candidatePlans.Add((New-CandidateBakeoffPlan -CandidateSummary $candidate)) | Out-Null
    }

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
        candidatePlanCount = $candidatePlans.Count
        scenarioCount = $allScenarioIds.Count
        scenarioIds = @($allScenarioIds)
        candidatePlans = @($candidatePlans.ToArray())
        fixtureComparisonCommandTemplate = "powershell -NoProfile -ExecutionPolicy Bypass -File tools\compare-dsp-fixture-metrics.ps1 -BaselinePath <current-zeus-fixtures.json> -CandidatePath <external-engine-fixtures.json> -CandidateComparisonId candidate-external-engine-opt-in -FailOnRegression"
        liveMatrixCommandTemplate = "powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-live-diagnostics-matrix.ps1 -ComparisonId candidate-external-engine-opt-in -ScenarioIds <scenario-ids> -Samples 60 -IntervalMs 1000"
    }
}

function Build-MarkdownReport {
    param($Report)

    $candidateSummaries = @(Get-JsonArray $Report "candidateSummaries")
    $requiredCandidateIds = @(Get-JsonArray $Report "requiredCandidateIds")
    $bakeoffPlan = Get-JsonValue $Report "externalBakeoffPlan"

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# DSP External Engine Bakeoff Report") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add("- Ready for review: $(Get-JsonValue $Report "readyForReview")") | Out-Null
    $lines.Add("- Candidates: $(Get-JsonValue $Report "candidateCount")") | Out-Null
    $lines.Add("- Safe for bakeoff: $(Get-JsonValue $Report "safeForBakeoffCount")") | Out-Null
    $lines.Add("- Unsafe catalog entries: $(Get-JsonValue $Report "unsafeCandidateCount")") | Out-Null
    $lines.Add("- Missing required candidates: $(Get-JsonValue $Report "missingCandidateCount")") | Out-Null
    $lines.Add("- Integration-ready candidates: $(Get-JsonValue $Report "integrationReadyCandidateCount")") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add("## Candidate Matrix") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add("| Candidate | Status | Risk | Benchmarks | Evidence | Blockers | Issues |") | Out-Null
    $lines.Add("|---|---|---:|---:|---:|---:|---|") | Out-Null
    foreach ($candidate in $candidateSummaries) {
        $issueText = if (@(Get-JsonArray $candidate "issues").Count -eq 0) { "" } else { @(Get-JsonArray $candidate "issues") -join ", " }
        $lines.Add("| $(Get-JsonValue $candidate "id") | $(Get-JsonValue $candidate "integrationStatus") | $(Get-JsonValue $candidate "combinedRiskScore") | $(Get-JsonValue $candidate "requiredBenchmarkCount") | $(Get-JsonValue $candidate "requiredEvidenceCount") | $(Get-JsonValue $candidate "blockerCount") | $issueText |") | Out-Null
    }
    $lines.Add("") | Out-Null
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

$requiredCandidateIds = @("rnnoise", "deepfilternet", "speexdsp", "webrtc-apm")
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
$externalBakeoffPlan = New-ExternalBakeoffPlan -CandidateSummaries @($candidateSummaries.ToArray())

$report = [ordered]@{
    schemaVersion = 3
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
