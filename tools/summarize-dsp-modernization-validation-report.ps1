param(
    [string]$ValidationReportPath = "",

    [string]$BundleDir = "",

    [string]$ReportPath = "",

    [string]$MarkdownPath = "",

    [switch]$FailOnIssues,

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

function Test-Truthy {
    param($Value)

    if ($null -eq $Value) {
        return $false
    }
    if ($Value -is [bool]) {
        return [bool]$Value
    }

    $text = ([string]$Value).Trim()
    if ([string]::IsNullOrWhiteSpace($text)) {
        return $false
    }

    return @("1", "true", "yes", "y", "ready", "ok") -contains $text.ToLowerInvariant()
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

function Add-Count {
    param(
        [hashtable]$Counts,
        [string]$Key
    )

    if ([string]::IsNullOrWhiteSpace($Key)) {
        $Key = "unknown"
    }

    if (-not $Counts.ContainsKey($Key)) {
        $Counts[$Key] = 0
    }
    $Counts[$Key] = [int]$Counts[$Key] + 1
}

function Convert-CountsToRecords {
    param(
        [hashtable]$Counts,
        [string]$NameField = "name"
    )

    $records = New-Object System.Collections.Generic.List[object]
    foreach ($key in @($Counts.Keys | Sort-Object)) {
        $record = [ordered]@{}
        $record[$NameField] = [string]$key
        $record["count"] = [int]$Counts[$key]
        $records.Add($record) | Out-Null
    }

    return @($records.ToArray())
}

function Get-IssueCodeCounts {
    param(
        $Issues,
        [Parameter(Mandatory = $true)][string]$Severity
    )

    $counts = @{}
    foreach ($issue in @($Issues)) {
        $code = [string](Get-JsonValue $issue "code")
        Add-Count -Counts $counts -Key $code
    }

    $records = New-Object System.Collections.Generic.List[object]
    foreach ($key in @($counts.Keys | Sort-Object)) {
        $records.Add([ordered]@{
            severity = $Severity
            code = [string]$key
            count = [int]$counts[$key]
        }) | Out-Null
    }

    return @($records.ToArray())
}

function Get-LiveSourceRemediations {
    param($Record)

    $actions = New-Object System.Collections.Generic.List[string]
    $sourceStatus = [string](Get-JsonValue $Record "sourceStatus")
    $summaryHashStatus = [string](Get-JsonValue $Record "summaryHashStatus")
    $jsonlHashStatus = [string](Get-JsonValue $Record "jsonlHashStatus")

    switch ($sourceStatus) {
        "path-missing" { $actions.Add("Regenerate live diagnostics history with -BundleDir so each trace carries a bundle-relative watcher summary path.") | Out-Null }
        "file-missing" { $actions.Add("Restore the referenced watch-dsp-live-diagnostics summary or rerun history after the capture bundle contains the final summary files.") | Out-Null }
        "json-invalid" { $actions.Add("Replace or regenerate the watcher summary; it is not valid JSON.") | Out-Null }
        "tool-invalid" { $actions.Add("Use watch-dsp-live-diagnostics.ps1 output as the source summary before rebuilding live history.") | Out-Null }
        "conversion-invalid" { $actions.Add("Regenerate the watcher summary from a valid live diagnostics trace; required fields cannot be reconstructed.") | Out-Null }
        "content-mismatch" { $actions.Add("Rerun summarize-dsp-live-diagnostics-history.ps1 after source summaries are finalized; investigate hand edits or stale copied trace fields.") | Out-Null }
    }

    switch ($summaryHashStatus) {
        "missing" { $actions.Add("Rerun summarize-dsp-live-diagnostics-history.ps1 with the current schema so summarySha256 is recorded.") | Out-Null }
        "mismatch" { $actions.Add("Regenerate the history report after the watcher summary is finalized; the recorded summarySha256 is stale.") | Out-Null }
    }

    switch ($jsonlHashStatus) {
        "file-missing" { $actions.Add("Restore the JSONL trace referenced by the watcher summary before using this history as evidence.") | Out-Null }
        "missing" { $actions.Add("Rerun summarize-dsp-live-diagnostics-history.ps1 so jsonlSha256 is recorded for the source trace.") | Out-Null }
        "mismatch" { $actions.Add("Regenerate the history report after the JSONL trace is finalized; the recorded jsonlSha256 is stale.") | Out-Null }
    }

    if ($actions.Count -eq 0 -and -not (Test-Truthy (Get-JsonValue $Record "ok"))) {
        $actions.Add("Inspect the validation report errors for this trace and regenerate the affected evidence.") | Out-Null
    }

    return @($actions.ToArray())
}

function Convert-LiveSourceRecord {
    param($Record)

    $actions = @(Get-LiveSourceRemediations -Record $Record)
    return [ordered]@{
        artifactId = [string](Get-JsonValue $Record "artifactId")
        traceId = [string](Get-JsonValue $Record "traceId")
        scenarioId = [string](Get-JsonValue $Record "scenarioId")
        comparisonId = [string](Get-JsonValue $Record "comparisonId")
        label = [string](Get-JsonValue $Record "label")
        path = [string](Get-JsonValue $Record "path")
        jsonlPath = [string](Get-JsonValue $Record "jsonlPath")
        ok = Test-Truthy (Get-JsonValue $Record "ok")
        sourceStatus = [string](Get-JsonValue $Record "sourceStatus")
        summaryHashStatus = [string](Get-JsonValue $Record "summaryHashStatus")
        jsonlHashStatus = [string](Get-JsonValue $Record "jsonlHashStatus")
        summarySha256 = [string](Get-JsonValue $Record "summarySha256")
        actualSummarySha256 = [string](Get-JsonValue $Record "actualSummarySha256")
        jsonlSha256 = [string](Get-JsonValue $Record "jsonlSha256")
        actualJsonlSha256 = [string](Get-JsonValue $Record "actualJsonlSha256")
        remediation = if ($actions.Count -eq 0) { "" } else { [string]$actions[0] }
        remediations = @($actions)
    }
}

function Convert-ReferencedFileRecord {
    param($Record)

    return [ordered]@{
        artifactId = [string](Get-JsonValue $Record "artifactId")
        artifactKind = [string](Get-JsonValue $Record "artifactKind")
        sourceType = [string](Get-JsonValue $Record "sourceType")
        path = [string](Get-JsonValue $Record "path")
        summaryPath = [string](Get-JsonValue $Record "summaryPath")
        traceId = [string](Get-JsonValue $Record "traceId")
        scenarioId = [string](Get-JsonValue $Record "scenarioId")
        comparisonId = [string](Get-JsonValue $Record "comparisonId")
        ok = Test-Truthy (Get-JsonValue $Record "ok")
        hashStatus = [string](Get-JsonValue $Record "hashStatus")
        summaryHashStatus = [string](Get-JsonValue $Record "summaryHashStatus")
        summaryTracePathStatus = [string](Get-JsonValue $Record "summaryTracePathStatus")
        summaryMetadataStatus = [string](Get-JsonValue $Record "summaryMetadataStatus")
        sourceStatus = [string](Get-JsonValue $Record "sourceStatus")
        jsonlHashStatus = [string](Get-JsonValue $Record "jsonlHashStatus")
    }
}

function Test-LiveSourceProblem {
    param($Record)

    if (-not (Test-Truthy (Get-JsonValue $Record "ok"))) {
        return $true
    }

    $sourceStatus = [string](Get-JsonValue $Record "sourceStatus")
    $summaryHashStatus = [string](Get-JsonValue $Record "summaryHashStatus")
    $jsonlHashStatus = [string](Get-JsonValue $Record "jsonlHashStatus")
    if ($sourceStatus -ne "matched") {
        return $true
    }
    if ($summaryHashStatus -ne "matched") {
        return $true
    }
    if ($jsonlHashStatus -ne "matched" -and $jsonlHashStatus -ne "not-applicable") {
        return $true
    }

    return $false
}

function Test-ReferencedFileProblem {
    param($Record)

    if (-not (Test-Truthy (Get-JsonValue $Record "ok"))) {
        return $true
    }

    foreach ($field in @("hashStatus", "summaryHashStatus", "summaryTracePathStatus", "sourceStatus", "jsonlHashStatus")) {
        $value = [string](Get-JsonValue $Record $field)
        if ([string]::IsNullOrWhiteSpace($value)) {
            continue
        }
        if ($value -in @("mismatch", "missing", "file-missing", "path-missing", "json-invalid", "tool-invalid", "conversion-invalid", "content-mismatch")) {
            return $true
        }
    }

    return $false
}

function New-EvidenceGateRecord {
    param(
        [Parameter(Mandatory = $true)][string]$GateId,
        [Parameter(Mandatory = $true)][string]$Name,
        [bool]$Ready,
        [string]$Status = "",
        [bool]$RequiredForAcceptance = $true,
        [string]$Detail = "",
        [string]$Remediation = ""
    )

    if ([string]::IsNullOrWhiteSpace($Status)) {
        $Status = if ($Ready) { "ready" } else { "not-ready" }
    }

    return [ordered]@{
        gateId = $GateId
        name = $Name
        ready = $Ready
        status = $Status
        requiredForAcceptance = $RequiredForAcceptance
        detail = $Detail
        remediation = $Remediation
    }
}

function Get-EvidenceGateRecords {
    param(
        [Parameter(Mandatory = $true)]$Validation,
        [Parameter(Mandatory = $true)][bool]$ValidationOk,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$LiveTraceSourceStatus,
        [Parameter(Mandatory = $true)][int]$ReferencedProblemCount,
        [Parameter(Mandatory = $true)][int]$LiveSourceProblemCount
    )

    $gates = New-Object System.Collections.Generic.List[object]

    $validationStatus = if ($ValidationOk) { "ready" } else { "failed" }
    $gates.Add((New-EvidenceGateRecord `
                -GateId "validation-report" `
                -Name "Strict bundle validation" `
                -Ready:$ValidationOk `
                -Status $validationStatus `
                -Detail "errors=$(Get-JsonValue $Validation "errorCount"); warnings=$(Get-JsonValue $Validation "warningCount")" `
                -Remediation "Resolve validation errors before using this bundle as DSP modernization evidence.")) | Out-Null

    $hardwareStatus = [string](Get-JsonValue $Validation "hardwareEvidenceStatus")
    $hardwareReady = [string]::Equals($hardwareStatus, "g2-hardware-evidence-ready", [StringComparison]::OrdinalIgnoreCase)
    $gates.Add((New-EvidenceGateRecord `
                -GateId "g2-hardware" `
                -Name "G2 hardware evidence" `
                -Ready:$hardwareReady `
                -Status $hardwareStatus `
                -Detail "target=$(Get-JsonValue $Validation "hardwareTarget"); captureTarget=$(Get-JsonValue $Validation "captureHardwareTarget"); diagnosticsPresent=$(Get-JsonValue $Validation "hardwareDiagnosticsPresent")" `
                -Remediation "Capture connected G2 hardware diagnostics and keep benchmark-plan/capture-manifest targets aligned.")) | Out-Null

    $nativeSymbolReady = Test-Truthy (Get-JsonValue $Validation "nativeSymbolAuditReady")
    $nativeSymbolStatus = if ($nativeSymbolReady) { "ready" } else { "not-ready" }
    $gates.Add((New-EvidenceGateRecord `
                -GateId "wdsp-native-symbol-audit" `
                -Name "WDSP native symbol audit" `
                -Ready:$nativeSymbolReady `
                -Status $nativeSymbolStatus `
                -Detail "present=$(Get-JsonValue $Validation "nativeSymbolAuditPresent"); imports=$(Get-JsonValue $Validation "nativeSymbolAuditImportedSymbolCount"); sourceMissing=$(Get-JsonValue $Validation "nativeSymbolAuditSourceMissingRequiredCount"); signatureMismatches=$(Get-JsonValue $Validation "nativeSymbolAuditSignatureMismatchCount"); binaryMissing=$(Get-JsonValue $Validation "nativeSymbolAuditBinaryMissingRequiredCount")" `
                -Remediation "Run audit-wdsp-native-symbols.ps1 with -RequireBinaryExports and resolve missing exports/signature drift.")) | Out-Null

    $nativeRuntimeReady = Test-Truthy (Get-JsonValue $Validation "nativeRuntimeArtifactAuditReadyForWinX64Package")
    $nativeRuntimeStatus = if ($nativeRuntimeReady) { "ready" } else { "not-ready" }
    $gates.Add((New-EvidenceGateRecord `
                -GateId "wdsp-runtime-artifact-audit" `
                -Name "WDSP runtime artifact audit" `
                -Ready:$nativeRuntimeReady `
                -Status $nativeRuntimeStatus `
                -Detail "present=$(Get-JsonValue $Validation "nativeRuntimeArtifactAuditPresent"); artifacts=$(Get-JsonValue $Validation "nativeRuntimeArtifactAuditArtifactCount"); pendingRids=$(Get-JsonValue $Validation "nativeRuntimeArtifactAuditPendingRidCount"); winX64Sha=$(Get-JsonValue $Validation "nativeRuntimeArtifactAuditWinX64NativeSha256")" `
                -Remediation "Run audit-wdsp-runtime-artifacts.ps1 and package the required win-x64 WDSP runtime artifact.")) | Out-Null

    $metricCatalogStatus = [string](Get-JsonValue $Validation "metricCatalogStatus")
    $metricCatalogReady = [string]::Equals($metricCatalogStatus, "ready", [StringComparison]::OrdinalIgnoreCase)
    $gates.Add((New-EvidenceGateRecord `
                -GateId "benchmark-metric-catalog" `
                -Name "Benchmark metric catalog" `
                -Ready:$metricCatalogReady `
                -Status $metricCatalogStatus `
                -Detail "metrics=$(Get-JsonValue $Validation "metricCatalogMetricCount"); missingRequired=$(Get-JsonValue $Validation "metricCatalogMissingRequiredMetricCount")" `
                -Remediation "Capture or update benchmark-metric-catalog.json so required metrics and directions are explicit.")) | Out-Null

    $externalCandidateStatus = [string](Get-JsonValue $Validation "externalEngineCandidateStatus")
    $externalCandidateReady = [string]::Equals($externalCandidateStatus, "opt-in-gated", [StringComparison]::OrdinalIgnoreCase)
    $gates.Add((New-EvidenceGateRecord `
                -GateId "external-engine-candidates" `
                -Name "External DSP/ML candidate catalog" `
                -Ready:$externalCandidateReady `
                -Status $externalCandidateStatus `
                -RequiredForAcceptance:$false `
                -Detail "candidates=$(Get-JsonValue $Validation "externalEngineCandidateCount"); missing=$(Get-JsonValue $Validation "externalEngineCandidateMissingCount"); unsafe=$(Get-JsonValue $Validation "externalEngineCandidateUnsafeCount"); snapshotMismatch=$(Get-JsonValue $Validation "externalEngineCandidateSnapshotMismatchCount")" `
                -Remediation "Keep RNNoise/DeepFilterNet/SpeexDSP/WebRTC entries opt-in, licensed, packaged, and safety-gated before any bakeoff.")) | Out-Null

    $externalBakeoffRequired = Test-Truthy (Get-JsonValue $Validation "externalEngineBakeoffRequiredByScope")
    $externalBakeoffPresent = Test-Truthy (Get-JsonValue $Validation "externalEngineBakeoffReportPresent")
    $externalBakeoffReady = Test-Truthy (Get-JsonValue $Validation "externalEngineBakeoffReady")
    $externalBakeoffStatus = if (-not $externalBakeoffRequired -and -not $externalBakeoffPresent) { "not-required" } elseif ($externalBakeoffReady) { "ready" } else { "not-ready" }
    $gates.Add((New-EvidenceGateRecord `
                -GateId "external-engine-bakeoff" `
                -Name "External DSP/ML bakeoff report" `
                -Ready:($externalBakeoffReady -or (-not $externalBakeoffRequired -and -not $externalBakeoffPresent)) `
                -Status $externalBakeoffStatus `
                -RequiredForAcceptance:$externalBakeoffRequired `
                -Detail "requiredByScope=$externalBakeoffRequired; present=$externalBakeoffPresent; safe=$(Get-JsonValue $Validation "externalEngineBakeoffSafeForBakeoffCount"); blocked=$(Get-JsonValue $Validation "externalEngineBakeoffBlockedCandidateCount")" `
                -Remediation "Generate external-engine-bakeoff-report only for scoped opt-in comparisons, then resolve unsafe/missing candidate evidence.")) | Out-Null

    $metricComparisonReady = Test-Truthy (Get-JsonValue $Validation "metricComparisonReady")
    $metricComparisonStatus = if ($metricComparisonReady) { "ready" } else { "not-ready" }
    $gates.Add((New-EvidenceGateRecord `
                -GateId "fixture-metric-comparison" `
                -Name "Offline fixture metric comparison" `
                -Ready:$metricComparisonReady `
                -Status $metricComparisonStatus `
                -Detail "present=$(Get-JsonValue $Validation "metricComparisonPresent"); sourceEngine=$(Get-JsonValue $Validation "offlineFixtureMetricsEvidenceEngine"); comparisonEngine=$(Get-JsonValue $Validation "metricComparisonEvidenceEngine"); wdspBacked=$(Get-JsonValue $Validation "metricComparisonWdspBackedEvidence"); sourceHash=$(Get-JsonValue $Validation "metricComparisonSourceMetricsHashStatus"); runtimeHash=$(Get-JsonValue $Validation "metricComparisonWdspRuntimeHashStatus"); runtimeArtifactHash=$(Get-JsonValue $Validation "offlineFixtureMetricsRuntimeArtifactHashStatus"); scope=$(Get-JsonValue $Validation "metricComparisonFixtureScenarioScope"); skippedNonFixture=$(Get-JsonValue $Validation "metricComparisonSkippedNonFixtureScenarioCount"); regressions=$(Get-JsonValue $Validation "metricComparisonRegressionCount"); gateFailures=$(Get-JsonValue $Validation "metricComparisonGateFailureCount"); missingScenarios=$(Get-JsonValue $Validation "metricComparisonMissingScenarioCount"); missingCurrent=$(Get-JsonValue $Validation "metricComparisonMissingCurrentBaselineCount"); missingThetis=$(Get-JsonValue $Validation "metricComparisonMissingThetisBaselineCount"); missingCandidates=$(Get-JsonValue $Validation "metricComparisonMissingCandidateCount"); missingValues=$(Get-JsonValue $Validation "metricComparisonMissingMetricValueCount")" `
                -Remediation "Run run-dsp-wdsp-fixture-matrix.ps1 and resolve fixture, runtime, or metric regressions before acceptance review.")) | Out-Null

    $liveTraceComparisonReady = Test-Truthy (Get-JsonValue $Validation "liveTraceComparisonReady")
    $liveTraceComparisonStatus = if ($liveTraceComparisonReady) { "ready" } else { "not-ready" }
    $gates.Add((New-EvidenceGateRecord `
                -GateId "live-trace-comparison" `
                -Name "Live trace comparison" `
                -Ready:$liveTraceComparisonReady `
                -Status $liveTraceComparisonStatus `
                -Detail "present=$(Get-JsonValue $Validation "liveTraceComparisonPresent"); regressions=$(Get-JsonValue $Validation "liveTraceComparisonRegressionCount"); gateFailures=$(Get-JsonValue $Validation "liveTraceComparisonGateFailureCount"); missingMetrics=$(Get-JsonValue $Validation "liveTraceComparisonMissingMetricDetailCount")" `
                -Remediation "Compare baseline/current-Zeus and candidate live traces or matrices, then resolve regressions and gate failures.")) | Out-Null

    $liveHistoryPresent = Test-Truthy (Get-JsonValue $Validation "liveDiagnosticsHistoryPresent")
    $liveHistoryReady = Test-Truthy (Get-JsonValue $Validation "liveDiagnosticsHistoryReady")
    $liveHistorySourceReady = ($LiveTraceSourceStatus -eq "hash-ready" -or (-not $liveHistoryPresent -and [string]::IsNullOrWhiteSpace($LiveTraceSourceStatus)))
    $liveHistoryGateReady = if (-not $liveHistoryPresent -and [string]::IsNullOrWhiteSpace($LiveTraceSourceStatus)) {
        $true
    }
    else {
        ($liveHistoryReady -and $liveHistorySourceReady -and $LiveSourceProblemCount -eq 0)
    }
    $liveHistoryGateStatus = if (-not $liveHistoryPresent -and [string]::IsNullOrWhiteSpace($LiveTraceSourceStatus)) {
        "not-present"
    }
    else {
        $LiveTraceSourceStatus
    }
    $gates.Add((New-EvidenceGateRecord `
                -GateId "live-history-provenance" `
                -Name "Live diagnostics history provenance" `
                -Ready:$liveHistoryGateReady `
                -Status $liveHistoryGateStatus `
                -RequiredForAcceptance:$false `
                -Detail "present=$liveHistoryPresent; ready=$liveHistoryReady; traceSources=$(Get-JsonValue $Validation "liveDiagnosticsHistoryTraceSourceCheckedCount"); sourceProblems=$LiveSourceProblemCount" `
                -Remediation "Regenerate live diagnostics history after watcher summaries and JSONL traces are finalized; fix source/hash mismatches before using history for tuning decisions.")) | Out-Null

    $referencedFileStatus = if ($ReferencedProblemCount -eq 0) { "ready" } else { "not-ready" }
    $gates.Add((New-EvidenceGateRecord `
                -GateId "referenced-file-provenance" `
                -Name "Referenced artifact file provenance" `
                -Ready:($ReferencedProblemCount -eq 0) `
                -Status $referencedFileStatus `
                -RequiredForAcceptance:$false `
                -Detail "problemCount=$ReferencedProblemCount" `
                -Remediation "Inspect failedReferencedFiles for stale hashes, missing traces, or summary path mismatches.")) | Out-Null

    return @($gates.ToArray())
}

function Get-EvidenceGateById {
    param(
        $Gates,
        [Parameter(Mandatory = $true)][string]$GateId
    )

    foreach ($gate in @($Gates)) {
        if ([string]::Equals([string](Get-JsonValue $gate "gateId"), $GateId, [StringComparison]::OrdinalIgnoreCase)) {
            return $gate
        }
    }

    return $null
}

function Test-EvidenceGateReady {
    param(
        $Gates,
        [Parameter(Mandatory = $true)][string]$GateId
    )

    $gate = Get-EvidenceGateById -Gates $Gates -GateId $GateId
    if ($null -eq $gate) {
        return $false
    }

    return Test-Truthy (Get-JsonValue $gate "ready")
}

function Get-BlockingEvidenceGateIds {
    param(
        $Gates,
        [switch]$RequiredOnly
    )

    $ids = New-Object System.Collections.Generic.List[string]
    foreach ($gate in @($Gates)) {
        if (Test-Truthy (Get-JsonValue $gate "ready")) {
            continue
        }
        if ($RequiredOnly -and -not (Test-Truthy (Get-JsonValue $gate "requiredForAcceptance"))) {
            continue
        }

        $ids.Add([string](Get-JsonValue $gate "gateId")) | Out-Null
    }

    return @($ids.ToArray())
}

function New-AcceptanceReadinessRecord {
    param(
        [Parameter(Mandatory = $true)][string]$StageId,
        [Parameter(Mandatory = $true)][string]$Name,
        [bool]$Ready,
        [string]$Status,
        [bool]$BlocksDefaultChange,
        [string]$Detail,
        [string]$NextAction,
        [string[]]$BlockingGateIds = @()
    )

    return [ordered]@{
        stageId = $StageId
        name = $Name
        ready = $Ready
        status = $Status
        blocksDefaultBehaviorChange = $BlocksDefaultChange
        detail = $Detail
        nextAction = $NextAction
        blockingGateIds = @($BlockingGateIds)
    }
}

function Get-AcceptanceReadinessRecords {
    param(
        [Parameter(Mandatory = $true)]$Validation,
        [Parameter(Mandatory = $true)]$EvidenceGates,
        [Parameter(Mandatory = $true)][string]$ReportStatus,
        [Parameter(Mandatory = $true)][int]$ReferencedProblemCount,
        [Parameter(Mandatory = $true)][bool]$LiveHistoryProvenanceNeedsAttention
    )

    $stages = New-Object System.Collections.Generic.List[object]
    $requiredBlockingGateIds = @(Get-BlockingEvidenceGateIds -Gates $EvidenceGates -RequiredOnly)
    $allBlockingGateIds = @(Get-BlockingEvidenceGateIds -Gates $EvidenceGates)

    $triageReady = [string]::Equals($ReportStatus, "ready", [StringComparison]::OrdinalIgnoreCase)
    $stages.Add((New-AcceptanceReadinessRecord `
                -StageId "validation-triage-clean" `
                -Name "Validation triage clean" `
                -Ready:$triageReady `
                -Status $ReportStatus `
                -BlocksDefaultChange:$true `
                -Detail "requiredGateProblems=$($requiredBlockingGateIds.Count); referencedFileProblems=$ReferencedProblemCount; liveHistoryProvenanceNeedsAttention=$LiveHistoryProvenanceNeedsAttention" `
                -NextAction $(if ($triageReady) { "Proceed to G2/on-air evidence review; default behavior still requires cross-radio validation." } else { "Resolve the triage status and rerun validation before treating the bundle as acceptance evidence." }) `
                -BlockingGateIds $requiredBlockingGateIds)) | Out-Null

    $g2Ready = ($triageReady `
            -and (Test-EvidenceGateReady -Gates $EvidenceGates -GateId "g2-hardware") `
            -and (Test-EvidenceGateReady -Gates $EvidenceGates -GateId "wdsp-native-symbol-audit") `
            -and (Test-EvidenceGateReady -Gates $EvidenceGates -GateId "wdsp-runtime-artifact-audit") `
            -and (Test-EvidenceGateReady -Gates $EvidenceGates -GateId "benchmark-metric-catalog") `
            -and (Test-EvidenceGateReady -Gates $EvidenceGates -GateId "fixture-metric-comparison") `
            -and (Test-EvidenceGateReady -Gates $EvidenceGates -GateId "live-trace-comparison"))
    $g2Status = if ($g2Ready) { "ready-for-g2-review" } else { "blocked-prerequisites" }
    $stages.Add((New-AcceptanceReadinessRecord `
                -StageId "g2-first-pass-evidence" `
                -Name "G2 first-pass evidence" `
                -Ready:$g2Ready `
                -Status $g2Status `
                -BlocksDefaultChange:$true `
                -Detail "hardware=$(Get-JsonValue $Validation "hardwareEvidenceStatus"); fixtureReady=$(Get-JsonValue $Validation "metricComparisonReady"); liveTraceReady=$(Get-JsonValue $Validation "liveTraceComparisonReady")" `
                -NextAction $(if ($g2Ready) { "Use this as first-cycle G2 review evidence, then capture non-G2 cross-radio evidence before graduation." } else { "Complete G2 hardware, WDSP audit, fixture, and live-trace gates before first-pass review." }) `
                -BlockingGateIds $requiredBlockingGateIds)) | Out-Null

    $candidateComparisonReady = ($g2Ready -and
        (Test-Truthy (Get-JsonValue $Validation "metricComparisonReady")) -and
        (Test-Truthy (Get-JsonValue $Validation "liveTraceComparisonReady")))
    $candidateStatus = if ($candidateComparisonReady) { "ready-for-opt-in-candidate-review" } else { "blocked-benchmark-evidence" }
    $stages.Add((New-AcceptanceReadinessRecord `
                -StageId "opt-in-candidate-comparison" `
                -Name "Opt-in candidate comparison" `
                -Ready:$candidateComparisonReady `
                -Status $candidateStatus `
                -BlocksDefaultChange:$true `
                -Detail "metricRegressions=$(Get-JsonValue $Validation "metricComparisonRegressionCount"); liveRegressions=$(Get-JsonValue $Validation "liveTraceComparisonRegressionCount"); liveGateFailures=$(Get-JsonValue $Validation "liveTraceComparisonGateFailureCount"); historyCandidatePromotionReady=$(Get-JsonValue $Validation "liveDiagnosticsHistoryCandidatePromotionReady")" `
                -NextAction $(if ($candidateComparisonReady) { "Keep the candidate opt-in and review objective metrics plus operator notes; do not change defaults." } else { "Generate fixture and live trace comparisons that beat current Zeus/Thetis evidence before opt-in review." }) `
                -BlockingGateIds $requiredBlockingGateIds)) | Out-Null

    $externalRequired = Test-Truthy (Get-JsonValue $Validation "externalEngineBakeoffRequiredByScope")
    $externalPresent = Test-Truthy (Get-JsonValue $Validation "externalEngineBakeoffReportPresent")
    $externalReady = Test-Truthy (Get-JsonValue $Validation "externalEngineBakeoffReady")
    $externalStageReady = ($externalReady -or (-not $externalRequired -and -not $externalPresent))
    $externalStatus = if (-not $externalRequired -and -not $externalPresent) { "not-in-scope" } elseif ($externalReady) { "ready-for-bakeoff-review" } else { "blocked-bakeoff-evidence" }
    $externalBlockingGateIds = @()
    if (-not $externalStageReady) {
        $externalBlockingGateIds = @("external-engine-bakeoff")
    }
    $stages.Add((New-AcceptanceReadinessRecord `
                -StageId "external-dsp-ml-bakeoff" `
                -Name "External DSP/ML bakeoff" `
                -Ready:$externalStageReady `
                -Status $externalStatus `
                -BlocksDefaultChange:$false `
                -Detail "requiredByScope=$externalRequired; present=$externalPresent; ready=$externalReady; defaultBehaviorChangeReady=$(Get-JsonValue $Validation "externalEngineBakeoffPlanDefaultBehaviorChangeReady"); rawIqReplacementAllowed=$(Get-JsonValue $Validation "externalEngineBakeoffPlanRawWdspIqReplacementAllowed")" `
                -NextAction $(if ($externalStageReady) { "No external-engine action is required unless an opt-in comparison is in scope." } else { "Generate and validate the external-engine bakeoff report before external DSP/ML review." }) `
                -BlockingGateIds $externalBlockingGateIds)) | Out-Null

    $defaultGraduationStatus = if ($g2Ready) { "blocked-cross-radio-validation" } else { "blocked-g2-prerequisites" }
    $stages.Add((New-AcceptanceReadinessRecord `
                -StageId "default-dsp-graduation" `
                -Name "Default DSP behavior graduation" `
                -Ready:$false `
                -Status $defaultGraduationStatus `
                -BlocksDefaultChange:$true `
                -Detail "defaultBehaviorChangeReady=False; crossRadioValidationRequired=True; crossRadioValidationEvidenceStatus=not-captured; blockingGateCount=$($allBlockingGateIds.Count)" `
                -NextAction $(if ($g2Ready) { "Capture and validate non-G2 cross-radio evidence before any default behavior change approval." } else { "Finish G2 first-pass evidence before planning cross-radio/default-graduation review." }) `
                -BlockingGateIds $allBlockingGateIds)) | Out-Null

    return @($stages.ToArray())
}

function New-AcceptanceActionRecord {
    param(
        [Parameter(Mandatory = $true)][string]$ActionId,
        [int]$Priority,
        [Parameter(Mandatory = $true)][string]$StageId,
        [string]$GateId = "",
        [Parameter(Mandatory = $true)][string]$Category,
        [bool]$RequiredForAcceptance,
        [bool]$BlocksDefaultChange,
        [string]$Reason = "",
        [string]$CommandTemplate = "",
        [string[]]$CommandSteps = @(),
        [string]$ManualAction = "",
        [string]$ExpectedArtifact = "",
        [string]$FollowUp = ""
    )

    $normalizedCommandSteps = New-Object System.Collections.Generic.List[string]
    foreach ($step in @($CommandSteps)) {
        if (-not [string]::IsNullOrWhiteSpace($step)) {
            $normalizedCommandSteps.Add([string]$step) | Out-Null
        }
    }
    if ($normalizedCommandSteps.Count -eq 0 -and -not [string]::IsNullOrWhiteSpace($CommandTemplate)) {
        $normalizedCommandSteps.Add($CommandTemplate) | Out-Null
    }
    if ([string]::IsNullOrWhiteSpace($CommandTemplate) -and $normalizedCommandSteps.Count -gt 0) {
        $CommandTemplate = ($normalizedCommandSteps.ToArray() -join "; ")
    }

    return [ordered]@{
        actionId = $ActionId
        priority = $Priority
        stageId = $StageId
        gateId = $GateId
        category = $Category
        requiredForAcceptance = $RequiredForAcceptance
        blocksDefaultBehaviorChange = $BlocksDefaultChange
        reason = $Reason
        commandTemplate = $CommandTemplate
        commandStepCount = $normalizedCommandSteps.Count
        commandSteps = @($normalizedCommandSteps.ToArray())
        manualAction = $ManualAction
        expectedArtifact = $ExpectedArtifact
        followUp = $FollowUp
    }
}

function Add-AcceptanceActionForGate {
    param(
        [Parameter(Mandatory = $true)]$Actions,
        [Parameter(Mandatory = $true)]$Gate
    )

    $gateId = [string](Get-JsonValue $Gate "gateId")
    $gateRequired = Test-Truthy (Get-JsonValue $Gate "requiredForAcceptance")
    $reason = [string](Get-JsonValue $Gate "remediation")
    if ([string]::IsNullOrWhiteSpace($reason)) {
        $reason = [string](Get-JsonValue $Gate "detail")
    }

    switch ($gateId) {
        "validation-report" {
            $Actions.Add((New-AcceptanceActionRecord `
                        -ActionId "rerun-strict-validation" `
                        -Priority 10 `
                        -StageId "validation-triage-clean" `
                        -GateId $gateId `
                        -Category "validation" `
                        -RequiredForAcceptance:$true `
                        -BlocksDefaultChange:$true `
                        -Reason $reason `
                        -CommandTemplate 'powershell -NoProfile -ExecutionPolicy Bypass -File tools\validate-dsp-modernization-bundle.ps1 -BundleDir "$bundleDir" -RequireArtifactFiles' `
                        -ExpectedArtifact 'validation-report.json' `
                        -FollowUp "Rerun summarize-dsp-modernization-validation-report.ps1 with -FailOnIssues after validation passes.")) | Out-Null
        }
        "g2-hardware" {
            $Actions.Add((New-AcceptanceActionRecord `
                        -ActionId "capture-g2-hardware-evidence" `
                        -Priority 20 `
                        -StageId "g2-first-pass-evidence" `
                        -GateId $gateId `
                        -Category "hardware-evidence" `
                        -RequiredForAcceptance:$true `
                        -BlocksDefaultChange:$true `
                        -Reason $reason `
                        -CommandTemplate 'powershell -NoProfile -ExecutionPolicy Bypass -File tools\capture-dsp-modernization-bundle.ps1 -BaseUrl http://localhost:6060 -OutputRoot captures\dsp-modernization -Label g2-dsp-evidence' `
                        -ExpectedArtifact 'hardware-diagnostics.json' `
                        -FollowUp "Confirm benchmark-plan firstHardwareTarget and capture manifest hardwareTarget both remain G2.")) | Out-Null
        }
        "wdsp-native-symbol-audit" {
            $Actions.Add((New-AcceptanceActionRecord `
                        -ActionId "run-wdsp-native-symbol-audit" `
                        -Priority 30 `
                        -StageId "g2-first-pass-evidence" `
                        -GateId $gateId `
                        -Category "wdsp-audit" `
                        -RequiredForAcceptance:$true `
                        -BlocksDefaultChange:$true `
                        -Reason $reason `
                        -CommandTemplate 'powershell -NoProfile -ExecutionPolicy Bypass -File tools\audit-wdsp-native-symbols.ps1 -ReportPath "$bundleDir\artifacts\wdsp-native-symbol-audit.json" -RequireBinaryExports' `
                        -ExpectedArtifact 'artifacts/wdsp-native-symbol-audit.json' `
                        -FollowUp "Resolve missing exports or signature drift before reviewing DSP parity.")) | Out-Null
        }
        "wdsp-runtime-artifact-audit" {
            $Actions.Add((New-AcceptanceActionRecord `
                        -ActionId "run-wdsp-runtime-artifact-audit" `
                        -Priority 31 `
                        -StageId "g2-first-pass-evidence" `
                        -GateId $gateId `
                        -Category "wdsp-audit" `
                        -RequiredForAcceptance:$true `
                        -BlocksDefaultChange:$true `
                        -Reason $reason `
                        -CommandTemplate 'powershell -NoProfile -ExecutionPolicy Bypass -File tools\audit-wdsp-runtime-artifacts.ps1 -ReportPath "$bundleDir\artifacts\wdsp-runtime-artifact-audit.json" -FailOnMissingWinX64Nr5' `
                        -ExpectedArtifact 'artifacts/wdsp-runtime-artifact-audit.json' `
                        -FollowUp "Package the expected win-x64 WDSP runtime artifact before acceptance review.")) | Out-Null
        }
        "benchmark-metric-catalog" {
            $Actions.Add((New-AcceptanceActionRecord `
                        -ActionId "refresh-benchmark-metric-catalog" `
                        -Priority 40 `
                        -StageId "g2-first-pass-evidence" `
                        -GateId $gateId `
                        -Category "benchmark-fixture" `
                        -RequiredForAcceptance:$true `
                        -BlocksDefaultChange:$true `
                        -Reason $reason `
                        -CommandTemplate 'powershell -NoProfile -ExecutionPolicy Bypass -File tools\capture-dsp-modernization-bundle.ps1 -BaseUrl http://localhost:6060 -OutputRoot captures\dsp-modernization -Label metric-catalog-refresh' `
                        -ExpectedArtifact 'benchmark-metric-catalog.json' `
                        -FollowUp "Rerun strict validation after the metric catalog declares required metrics and directions.")) | Out-Null
        }
        "fixture-metric-comparison" {
            $fixtureComparisonCommandSteps = @(
                'powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-wdsp-fixture-matrix.ps1 -BundleDir "$bundleDir" -Force'
            )
            $Actions.Add((New-AcceptanceActionRecord `
                        -ActionId "run-offline-fixture-comparison" `
                        -Priority 50 `
                        -StageId "opt-in-candidate-comparison" `
                        -GateId $gateId `
                        -Category "benchmark-fixture" `
                        -RequiredForAcceptance:$true `
                        -BlocksDefaultChange:$true `
                        -Reason $reason `
                        -CommandSteps $fixtureComparisonCommandSteps `
                        -ExpectedArtifact 'artifacts/dsp-fixture-metric-comparison.json' `
                        -FollowUp "Resolve weak-signal, pumping, clipping, latency, or artifact regressions before live review; deterministic fixture evidence is only a schema fallback, not default-graduation proof.")) | Out-Null
        }
        "live-trace-comparison" {
            $liveMatrixCommandSteps = @(
                'powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-live-diagnostics-matrix.ps1 -BundleDir "$bundleDir" -ComparisonId current-zeus -IndexPath "$bundleDir\artifacts\live-diagnostics-trace-index.baseline.json" -ReportPath "$bundleDir\artifacts\live-diagnostics-matrix-report.baseline.json" -Samples 60 -IntervalMs 1000',
                'powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-live-diagnostics-matrix.ps1 -BundleDir "$bundleDir" -ComparisonId nr5-spnr -IndexPath "$bundleDir\artifacts\live-diagnostics-trace-index.candidate.json" -ReportPath "$bundleDir\artifacts\live-diagnostics-matrix-report.candidate.json" -Samples 60 -IntervalMs 1000',
                'powershell -NoProfile -ExecutionPolicy Bypass -File tools\compare-dsp-live-diagnostics-matrix.ps1 -BundleDir "$bundleDir" -BaselineIndexPath "$bundleDir\artifacts\live-diagnostics-trace-index.baseline.json" -CandidateIndexPath "$bundleDir\artifacts\live-diagnostics-trace-index.candidate.json" -ReportPath "$bundleDir\artifacts\live-diagnostics-trace-comparison.json" -FailOnRegression'
            )
            $Actions.Add((New-AcceptanceActionRecord `
                        -ActionId "capture-and-compare-live-matrix" `
                        -Priority 60 `
                        -StageId "opt-in-candidate-comparison" `
                        -GateId $gateId `
                        -Category "live-diagnostics" `
                        -RequiredForAcceptance:$true `
                        -BlocksDefaultChange:$true `
                        -Reason $reason `
                        -CommandSteps $liveMatrixCommandSteps `
                        -ExpectedArtifact 'artifacts/live-diagnostics-trace-comparison.json' `
                        -FollowUp "Mark live-diagnostics-trace-comparison required=true in artifact-manifest.json for acceptance review.")) | Out-Null
        }
        "external-engine-candidates" {
            $Actions.Add((New-AcceptanceActionRecord `
                        -ActionId "refresh-external-engine-catalog" `
                        -Priority 70 `
                        -StageId "external-dsp-ml-bakeoff" `
                        -GateId $gateId `
                        -Category "external-dsp-ml" `
                        -RequiredForAcceptance:$gateRequired `
                        -BlocksDefaultChange:$false `
                        -Reason $reason `
                        -CommandTemplate 'powershell -NoProfile -ExecutionPolicy Bypass -File tools\capture-dsp-modernization-bundle.ps1 -BaseUrl http://localhost:6060 -OutputRoot captures\dsp-modernization -Label external-candidate-refresh' `
                        -ExpectedArtifact 'external-engine-candidates.json' `
                        -FollowUp "Keep every external engine post-demod, opt-in, packaged, licensed, and off by default.")) | Out-Null
        }
        "external-engine-bakeoff" {
            $Actions.Add((New-AcceptanceActionRecord `
                        -ActionId "run-external-engine-bakeoff-summary" `
                        -Priority 71 `
                        -StageId "external-dsp-ml-bakeoff" `
                        -GateId $gateId `
                        -Category "external-dsp-ml" `
                        -RequiredForAcceptance:$gateRequired `
                        -BlocksDefaultChange:$false `
                        -Reason $reason `
                        -CommandTemplate 'powershell -NoProfile -ExecutionPolicy Bypass -File tools\summarize-dsp-external-engine-candidates.ps1 -BundleDir "$bundleDir" -CandidatePath "$bundleDir\external-engine-candidates.json" -SnapshotPath "$bundleDir\modernization-snapshot.json" -ReportPath "$bundleDir\artifacts\external-engine-bakeoff-report.json" -FailOnUnsafe' `
                        -ExpectedArtifact 'artifacts/external-engine-bakeoff-report.json' `
                        -FollowUp "External DSP/ML remains opt-in and post-demod only until explicit radio-safety approval.")) | Out-Null
        }
        "live-history-provenance" {
            $Actions.Add((New-AcceptanceActionRecord `
                        -ActionId "regenerate-live-diagnostics-history" `
                        -Priority 80 `
                        -StageId "opt-in-candidate-comparison" `
                        -GateId $gateId `
                        -Category "live-diagnostics" `
                        -RequiredForAcceptance:$gateRequired `
                        -BlocksDefaultChange:$false `
                        -Reason $reason `
                        -CommandTemplate 'powershell -NoProfile -ExecutionPolicy Bypass -File tools\summarize-dsp-live-diagnostics-history.ps1 -BundleDir "$bundleDir" -ReportPath "$bundleDir\artifacts\live-diagnostics-history.json"' `
                        -ExpectedArtifact 'artifacts/live-diagnostics-history.json' `
                        -FollowUp "Use history only to choose candidate comparison windows; it does not approve defaults.")) | Out-Null
        }
        "referenced-file-provenance" {
            $Actions.Add((New-AcceptanceActionRecord `
                        -ActionId "repair-referenced-artifact-provenance" `
                        -Priority 90 `
                        -StageId "validation-triage-clean" `
                        -GateId $gateId `
                        -Category "artifact-provenance" `
                        -RequiredForAcceptance:$gateRequired `
                        -BlocksDefaultChange:$true `
                        -Reason $reason `
                        -CommandTemplate 'powershell -NoProfile -ExecutionPolicy Bypass -File tools\validate-dsp-modernization-bundle.ps1 -BundleDir "$bundleDir" -RequireArtifactFiles' `
                        -ExpectedArtifact 'validation-report.json' `
                        -FollowUp "Inspect failedReferencedFiles in the triage report, restore stale/missing inputs, and rerun validation.")) | Out-Null
        }
    }
}

function Get-AcceptanceActionPlanRecords {
    param(
        [Parameter(Mandatory = $true)]$EvidenceGates,
        [Parameter(Mandatory = $true)]$AcceptanceReadiness
    )

    $actions = New-Object System.Collections.Generic.List[object]
    foreach ($gate in @($EvidenceGates)) {
        if (Test-Truthy (Get-JsonValue $gate "ready")) {
            continue
        }

        Add-AcceptanceActionForGate -Actions $actions -Gate $gate
    }

    $g2Stage = $null
    foreach ($stage in @($AcceptanceReadiness)) {
        if ([string](Get-JsonValue $stage "stageId") -eq "g2-first-pass-evidence") {
            $g2Stage = $stage
            break
        }
    }

    if ($null -ne $g2Stage -and (Test-Truthy (Get-JsonValue $g2Stage "ready"))) {
        $actions.Add((New-AcceptanceActionRecord `
                    -ActionId "capture-cross-radio-validation" `
                    -Priority 100 `
                    -StageId "default-dsp-graduation" `
                    -Category "cross-radio-validation" `
                    -RequiredForAcceptance:$true `
                    -BlocksDefaultChange:$true `
                    -Reason "Default DSP behavior cannot graduate from G2-only evidence." `
                    -ManualAction "Repeat the accepted fixture/live evidence bundle on at least one non-G2 radio target and attach that validation before any default behavior change approval." `
                    -ExpectedArtifact "cross-radio validation bundle" `
                    -FollowUp "After cross-radio evidence exists, rerun strict validation and triage before asking for default behavior approval.")) | Out-Null
    }

    return @($actions.ToArray() | Sort-Object `
            @{ Expression = { Get-JsonValue $_ "priority" } }, `
            @{ Expression = { Get-JsonValue $_ "actionId" } })
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

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# DSP Modernization Validation Triage") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add("- Validation OK: $(Get-JsonValue $Report "validationOk")") | Out-Null
    $lines.Add("- Triage status: $(Get-JsonValue $Report "status")") | Out-Null
    $lines.Add("- Errors: $(Get-JsonValue $Report "errorCount")") | Out-Null
    $lines.Add("- Warnings: $(Get-JsonValue $Report "warningCount")") | Out-Null
    $lines.Add("- Referenced-file problems: $(Get-JsonValue $Report "artifactReferencedFileProblemCount")") | Out-Null
    $lines.Add("- Live-history trace source status: $(Get-JsonValue $Report "liveDiagnosticsHistoryTraceSourceStatus")") | Out-Null
    $lines.Add("- Live-history trace source problems: $(Get-JsonValue $Report "liveHistoryTraceSourceProblemCount")") | Out-Null
    $lines.Add("") | Out-Null

    $recommendations = @(Get-JsonArray $Report "recommendations")
    if ($recommendations.Count -gt 0) {
        $lines.Add("## Recommendations") | Out-Null
        $lines.Add("") | Out-Null
        foreach ($recommendation in $recommendations) {
            $lines.Add("- $recommendation") | Out-Null
        }
        $lines.Add("") | Out-Null
    }

    $evidenceGates = @(Get-JsonArray $Report "evidenceGates")
    if ($evidenceGates.Count -gt 0) {
        $lines.Add("## Evidence Gates") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("| Gate | Ready | Status | Required | Detail | Action |") | Out-Null
        $lines.Add("|---|---:|---|---:|---|---|") | Out-Null
        foreach ($gate in $evidenceGates) {
            $lines.Add("| $(Format-MarkdownCell (Get-JsonValue $gate "name")) | $(Get-JsonValue $gate "ready") | $(Format-MarkdownCell (Get-JsonValue $gate "status")) | $(Get-JsonValue $gate "requiredForAcceptance") | $(Format-MarkdownCell (Get-JsonValue $gate "detail")) | $(Format-MarkdownCell (Get-JsonValue $gate "remediation")) |") | Out-Null
        }
        $lines.Add("") | Out-Null
    }

    $fixtureMissingScenarios = @(Get-JsonArray $Report "metricComparisonMissingScenarios")
    $fixtureMissingScenarioCount = [int](Get-JsonValue $Report "metricComparisonMissingScenarioCount")
    $fixtureMissingCurrentCount = [int](Get-JsonValue $Report "metricComparisonMissingCurrentBaselineCount")
    $fixtureMissingThetisCount = [int](Get-JsonValue $Report "metricComparisonMissingThetisBaselineCount")
    $fixtureMissingCandidateCount = [int](Get-JsonValue $Report "metricComparisonMissingCandidateCount")
    $fixtureMissingMetricValueCount = [int](Get-JsonValue $Report "metricComparisonMissingMetricValueCount")
    $fixtureWdspBacked = Test-Truthy (Get-JsonValue $Report "metricComparisonWdspBackedEvidence")
    if ($fixtureMissingScenarioCount -gt 0 -or
        $fixtureMissingCurrentCount -gt 0 -or
        $fixtureMissingThetisCount -gt 0 -or
        $fixtureMissingCandidateCount -gt 0 -or
        $fixtureMissingMetricValueCount -gt 0 -or
        -not $fixtureWdspBacked) {
        $lines.Add("## Fixture Metric Coverage") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("- Source evidence engine: $(Format-MarkdownCell (Get-JsonValue $Report "offlineFixtureMetricsEvidenceEngine"))") | Out-Null
        $lines.Add("- Comparison evidence engine: $(Format-MarkdownCell (Get-JsonValue $Report "metricComparisonEvidenceEngine"))") | Out-Null
        $lines.Add("- WDSP-backed evidence: $fixtureWdspBacked") | Out-Null
        $lines.Add("- Source metrics path: $(Format-MarkdownCell (Get-JsonValue $Report "offlineFixtureMetricsPath"))") | Out-Null
        $lines.Add("- Comparison metrics path: $(Format-MarkdownCell (Get-JsonValue $Report "metricComparisonSourceMetricsPath"))") | Out-Null
        $lines.Add("- Source metrics SHA-256: $(Format-MarkdownCell (Get-JsonValue $Report "offlineFixtureMetricsSha256"))") | Out-Null
        $lines.Add("- Comparison metrics SHA-256: $(Format-MarkdownCell (Get-JsonValue $Report "metricComparisonSourceMetricsSha256"))") | Out-Null
        $lines.Add("- Source hash status: $(Format-MarkdownCell (Get-JsonValue $Report "metricComparisonSourceMetricsHashStatus"))") | Out-Null
        $lines.Add("- Source WDSP runtime RID: $(Format-MarkdownCell (Get-JsonValue $Report "offlineFixtureMetricsWdspRuntimeRid"))") | Out-Null
        $lines.Add("- Source WDSP runtime SHA-256: $(Format-MarkdownCell (Get-JsonValue $Report "offlineFixtureMetricsWdspRuntimeSha256"))") | Out-Null
        $lines.Add("- Comparison WDSP runtime SHA-256: $(Format-MarkdownCell (Get-JsonValue $Report "metricComparisonWdspRuntimeSha256"))") | Out-Null
        $lines.Add("- Runtime hash status: $(Format-MarkdownCell (Get-JsonValue $Report "metricComparisonWdspRuntimeHashStatus"))") | Out-Null
        $lines.Add("- Runtime artifact hash status: $(Format-MarkdownCell (Get-JsonValue $Report "offlineFixtureMetricsRuntimeArtifactHashStatus"))") | Out-Null
        $lines.Add("- Packaged Win x64 WDSP SHA-256: $(Format-MarkdownCell (Get-JsonValue $Report "nativeRuntimeArtifactAuditWinX64NativeSha256"))") | Out-Null
        $lines.Add("- Scenario scope: $(Format-MarkdownCell (Get-JsonValue $Report "metricComparisonFixtureScenarioScope"))") | Out-Null
        $lines.Add("- Skipped non-fixture scenarios: $(Get-JsonValue $Report "metricComparisonSkippedNonFixtureScenarioCount")") | Out-Null
        $lines.Add("- Missing scenarios: $fixtureMissingScenarioCount") | Out-Null
        if ($fixtureMissingScenarios.Count -gt 0) {
            $lines.Add("- Missing scenario IDs: $(Format-MarkdownCell ($fixtureMissingScenarios -join ', '))") | Out-Null
        }
        $lines.Add("- Missing current-Zeus baselines: $fixtureMissingCurrentCount") | Out-Null
        $lines.Add("- Missing Thetis-parity baselines: $fixtureMissingThetisCount") | Out-Null
        $lines.Add("- Missing candidate scenario coverage: $fixtureMissingCandidateCount") | Out-Null
        $lines.Add("- Missing required metric values: $fixtureMissingMetricValueCount") | Out-Null
        $lines.Add("") | Out-Null
    }

    $acceptanceReadiness = @(Get-JsonArray $Report "acceptanceReadiness")
    if ($acceptanceReadiness.Count -gt 0) {
        $lines.Add("## Acceptance Readiness") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("- G2 first-pass ready: $(Get-JsonValue $Report "g2FirstPassAcceptanceReady")") | Out-Null
        $lines.Add("- Opt-in candidate comparison ready: $(Get-JsonValue $Report "candidateComparisonReady")") | Out-Null
        $lines.Add("- Default behavior change ready: $(Get-JsonValue $Report "defaultBehaviorChangeReady")") | Out-Null
        $lines.Add("- Cross-radio validation evidence: $(Get-JsonValue $Report "crossRadioValidationEvidenceStatus")") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("| Stage | Ready | Status | Blocks Default Change | Detail | Next Action | Blocking Gates |") | Out-Null
        $lines.Add("|---|---:|---|---:|---|---|---|") | Out-Null
        foreach ($stage in $acceptanceReadiness) {
            $blockingGateIds = @(Get-JsonArray $stage "blockingGateIds")
            $lines.Add("| $(Format-MarkdownCell (Get-JsonValue $stage "name")) | $(Get-JsonValue $stage "ready") | $(Format-MarkdownCell (Get-JsonValue $stage "status")) | $(Get-JsonValue $stage "blocksDefaultBehaviorChange") | $(Format-MarkdownCell (Get-JsonValue $stage "detail")) | $(Format-MarkdownCell (Get-JsonValue $stage "nextAction")) | $(Format-MarkdownCell ($blockingGateIds -join ', ')) |") | Out-Null
        }
        $lines.Add("") | Out-Null
    }

    $acceptanceActionPlan = @(Get-JsonArray $Report "acceptanceActionPlan")
    if ($acceptanceActionPlan.Count -gt 0) {
        $lines.Add("## Acceptance Action Plan") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("- Action count: $(Get-JsonValue $Report "acceptanceActionPlanCount")") | Out-Null
        $lines.Add("- Required action count: $(Get-JsonValue $Report "acceptanceRequiredActionCount")") | Out-Null
        $lines.Add("- Manual action count: $(Get-JsonValue $Report "acceptanceManualActionCount")") | Out-Null
        $lines.Add("- Primary action: $(Get-JsonValue $Report "primaryAcceptanceActionId")") | Out-Null
        $primaryCommandOrManual = [string](Get-JsonValue $Report "primaryAcceptanceCommandTemplate")
        if ([string]::IsNullOrWhiteSpace($primaryCommandOrManual)) {
            $primaryCommandOrManual = [string](Get-JsonValue $Report "primaryAcceptanceManualAction")
        }
        if (-not [string]::IsNullOrWhiteSpace($primaryCommandOrManual)) {
            $lines.Add("- Primary command/manual action: $(Format-MarkdownCell $primaryCommandOrManual)") | Out-Null
        }
        $primaryCommandSteps = @(Get-JsonArray $Report "primaryAcceptanceCommandSteps")
        if ($primaryCommandSteps.Count -gt 1) {
            $lines.Add("- Primary command steps: $($primaryCommandSteps.Count)") | Out-Null
        }
        $lines.Add("") | Out-Null
        $lines.Add("| Priority | Action | Stage | Gate | Required | Category | Steps | Command / Manual Action | Follow-up |") | Out-Null
        $lines.Add("|---:|---|---|---|---:|---|---:|---|---|") | Out-Null
        foreach ($action in $acceptanceActionPlan) {
            $commandOrManual = [string](Get-JsonValue $action "commandTemplate")
            if ([string]::IsNullOrWhiteSpace($commandOrManual)) {
                $commandOrManual = [string](Get-JsonValue $action "manualAction")
            }
            $lines.Add("| $(Get-JsonValue $action "priority") | $(Format-MarkdownCell (Get-JsonValue $action "actionId")) | $(Format-MarkdownCell (Get-JsonValue $action "stageId")) | $(Format-MarkdownCell (Get-JsonValue $action "gateId")) | $(Get-JsonValue $action "requiredForAcceptance") | $(Format-MarkdownCell (Get-JsonValue $action "category")) | $(Get-JsonValue $action "commandStepCount") | $(Format-MarkdownCell $commandOrManual) | $(Format-MarkdownCell (Get-JsonValue $action "followUp")) |") | Out-Null
        }
        $lines.Add("") | Out-Null
    }

    $sourceStatusCounts = @(Get-JsonArray $Report "liveHistoryTraceSourceStatusCounts")
    if ($sourceStatusCounts.Count -gt 0) {
        $lines.Add("## Live-History Source Status") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("| Status | Count |") | Out-Null
        $lines.Add("|---|---:|") | Out-Null
        foreach ($item in $sourceStatusCounts) {
            $lines.Add("| $(Format-MarkdownCell (Get-JsonValue $item "sourceStatus")) | $(Get-JsonValue $item "count") |") | Out-Null
        }
        $lines.Add("") | Out-Null
    }

    $failedSources = @(Get-JsonArray $Report "failedLiveHistoryTraceSources")
    if ($failedSources.Count -gt 0) {
        $lines.Add("## Failed Live-History Trace Sources") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("| Trace | Scenario | Comparison | Source | Summary Hash | JSONL Hash | Action |") | Out-Null
        $lines.Add("|---|---|---|---|---|---|---|") | Out-Null
        foreach ($source in $failedSources) {
            $lines.Add("| $(Format-MarkdownCell (Get-JsonValue $source "traceId")) | $(Format-MarkdownCell (Get-JsonValue $source "scenarioId")) | $(Format-MarkdownCell (Get-JsonValue $source "comparisonId")) | $(Format-MarkdownCell (Get-JsonValue $source "sourceStatus")) | $(Format-MarkdownCell (Get-JsonValue $source "summaryHashStatus")) | $(Format-MarkdownCell (Get-JsonValue $source "jsonlHashStatus")) | $(Format-MarkdownCell (Get-JsonValue $source "remediation")) |") | Out-Null
        }
        $lines.Add("") | Out-Null
    }

    $failedReferencedFiles = @(Get-JsonArray $Report "failedReferencedFiles" | Where-Object {
            [string](Get-JsonValue $_ "sourceType") -ne "live-diagnostics-history-trace-source"
        })
    if ($failedReferencedFiles.Count -gt 0) {
        $lines.Add("## Failed Referenced Files") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("| Artifact | Kind | Source | Path | Summary | Hash | Summary Hash | Trace Path | Metadata | Source Status | JSONL Hash |") | Out-Null
        $lines.Add("|---|---|---|---|---|---|---|---|---|---|---|") | Out-Null
        foreach ($file in $failedReferencedFiles) {
            $lines.Add("| $(Format-MarkdownCell (Get-JsonValue $file "artifactId")) | $(Format-MarkdownCell (Get-JsonValue $file "artifactKind")) | $(Format-MarkdownCell (Get-JsonValue $file "sourceType")) | $(Format-MarkdownCell (Get-JsonValue $file "path")) | $(Format-MarkdownCell (Get-JsonValue $file "summaryPath")) | $(Format-MarkdownCell (Get-JsonValue $file "hashStatus")) | $(Format-MarkdownCell (Get-JsonValue $file "summaryHashStatus")) | $(Format-MarkdownCell (Get-JsonValue $file "summaryTracePathStatus")) | $(Format-MarkdownCell (Get-JsonValue $file "summaryMetadataStatus")) | $(Format-MarkdownCell (Get-JsonValue $file "sourceStatus")) | $(Format-MarkdownCell (Get-JsonValue $file "jsonlHashStatus")) |") | Out-Null
        }
        $lines.Add("") | Out-Null
    }

    $issueCounts = @(Get-JsonArray $Report "issueCodeCounts")
    if ($issueCounts.Count -gt 0) {
        $lines.Add("## Validation Issue Codes") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("| Severity | Code | Count |") | Out-Null
        $lines.Add("|---|---|---:|") | Out-Null
        foreach ($issue in $issueCounts) {
            $lines.Add("| $(Format-MarkdownCell (Get-JsonValue $issue "severity")) | $(Format-MarkdownCell (Get-JsonValue $issue "code")) | $(Get-JsonValue $issue "count") |") | Out-Null
        }
        $lines.Add("") | Out-Null
    }

    $lines.Add("This triage report is review tooling only. It does not approve DSP defaults, raw WDSP IQ replacement, or on-air acceptance by itself.") | Out-Null

    return @($lines.ToArray())
}

$bundlePath = ""
if (-not [string]::IsNullOrWhiteSpace($BundleDir)) {
    $bundlePath = (Resolve-Path -LiteralPath $BundleDir).Path
}

if ([string]::IsNullOrWhiteSpace($ValidationReportPath)) {
    if ([string]::IsNullOrWhiteSpace($bundlePath)) {
        throw "Specify -ValidationReportPath or -BundleDir."
    }
    $ValidationReportPath = Join-Path $bundlePath "validation-report.json"
}

$validationReportFullPath = (Resolve-Path -LiteralPath $ValidationReportPath).Path
if ([string]::IsNullOrWhiteSpace($bundlePath)) {
    $bundlePath = Split-Path -Parent $validationReportFullPath
}

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $reportDir = Split-Path -Parent $validationReportFullPath
    $ReportPath = Join-Path $reportDir "validation-triage-report.json"
}
if (-not $NoMarkdown -and [string]::IsNullOrWhiteSpace($MarkdownPath)) {
    $reportDir = Split-Path -Parent $ReportPath
    $reportName = [System.IO.Path]::GetFileNameWithoutExtension($ReportPath)
    $MarkdownPath = Join-Path $reportDir "$reportName.md"
}

$validation = Read-JsonFile $validationReportFullPath
$referencedFiles = @(Get-JsonArray $validation "artifactReferencedFiles")
$liveHistorySourceRecords = @($referencedFiles | Where-Object {
        [string](Get-JsonValue $_ "sourceType") -eq "live-diagnostics-history-trace-source"
    })

$sourceTypeCounts = @{}
$referencedProblemRecords = New-Object System.Collections.Generic.List[object]
foreach ($record in $referencedFiles) {
    $sourceType = [string](Get-JsonValue $record "sourceType")
    if ([string]::IsNullOrWhiteSpace($sourceType)) {
        $sourceType = [string](Get-JsonValue $record "artifactKind")
    }
    Add-Count -Counts $sourceTypeCounts -Key $sourceType

    if (Test-ReferencedFileProblem -Record $record) {
        $referencedProblemRecords.Add((Convert-ReferencedFileRecord -Record $record)) | Out-Null
    }
}

$liveSourceStatusCounts = @{}
$liveSummaryHashStatusCounts = @{}
$liveJsonlHashStatusCounts = @{}
$failedLiveSources = New-Object System.Collections.Generic.List[object]
foreach ($record in $liveHistorySourceRecords) {
    Add-Count -Counts $liveSourceStatusCounts -Key ([string](Get-JsonValue $record "sourceStatus"))
    Add-Count -Counts $liveSummaryHashStatusCounts -Key ([string](Get-JsonValue $record "summaryHashStatus"))
    Add-Count -Counts $liveJsonlHashStatusCounts -Key ([string](Get-JsonValue $record "jsonlHashStatus"))

    if (Test-LiveSourceProblem -Record $record) {
        $failedLiveSources.Add((Convert-LiveSourceRecord -Record $record)) | Out-Null
    }
}

$errors = @(Get-JsonArray $validation "errors")
$warnings = @(Get-JsonArray $validation "warnings")
$issueCodeCounts = @()
$issueCodeCounts += @(Get-IssueCodeCounts -Issues $errors -Severity "error")
$issueCodeCounts += @(Get-IssueCodeCounts -Issues $warnings -Severity "warning")

$recommendations = New-Object System.Collections.Generic.List[string]
$validationOk = Test-Truthy (Get-JsonValue $validation "ok")
$liveTraceSourceStatus = [string](Get-JsonValue $validation "liveDiagnosticsHistoryTraceSourceStatus")
$evidenceGates = @(Get-EvidenceGateRecords `
        -Validation $validation `
        -ValidationOk:$validationOk `
        -LiveTraceSourceStatus $liveTraceSourceStatus `
        -ReferencedProblemCount $referencedProblemRecords.Count `
        -LiveSourceProblemCount $failedLiveSources.Count)
$evidenceGateProblems = @($evidenceGates | Where-Object { -not (Test-Truthy (Get-JsonValue $_ "ready")) })
$requiredEvidenceGateProblems = @($evidenceGateProblems | Where-Object { Test-Truthy (Get-JsonValue $_ "requiredForAcceptance") })
$liveHistoryPresent = Test-Truthy (Get-JsonValue $validation "liveDiagnosticsHistoryPresent")
$liveHistoryGate = @($evidenceGates | Where-Object { [string](Get-JsonValue $_ "gateId") -eq "live-history-provenance" } | Select-Object -First 1)
$liveHistoryGateReady = $false
if ($liveHistoryGate.Count -gt 0) {
    $liveHistoryGateReady = Test-Truthy (Get-JsonValue -Object $liveHistoryGate[0] -Name "ready")
}
$liveHistoryProvenanceNeedsAttention = ($failedLiveSources.Count -gt 0)
if ($liveHistoryPresent -and -not $liveHistoryGateReady) {
    $liveHistoryProvenanceNeedsAttention = $true
}

if (-not $validationOk) {
    $recommendations.Add("Resolve validation errors before treating this bundle as DSP modernization evidence.") | Out-Null
}
if ($failedLiveSources.Count -gt 0) {
    $recommendations.Add("Fix failed live-history source records first; downstream promotion and tuning summaries depend on trustworthy watcher-summary/JSONL provenance.") | Out-Null
}
if ($liveTraceSourceStatus -eq "source-incomplete") {
    $recommendations.Add("Restore missing watcher summaries or JSONL traces, then rerun summarize-dsp-live-diagnostics-history.ps1 with -BundleDir.") | Out-Null
}
elseif ($liveTraceSourceStatus -eq "hash-invalid") {
    $recommendations.Add("Rerun summarize-dsp-live-diagnostics-history.ps1 after watcher summaries and JSONL traces are finalized so schema v9 hashes match current bytes.") | Out-Null
}
elseif ($liveTraceSourceStatus -eq "hash-incomplete") {
    $recommendations.Add("Upgrade or rerun live-history generation so every trace carries summarySha256 and jsonlSha256 provenance.") | Out-Null
}
if ($referencedProblemRecords.Count -gt $failedLiveSources.Count) {
    $recommendations.Add("Inspect non-history artifactReferencedFiles failures; trace indexes and matrix reports may also reference stale or missing evidence.") | Out-Null
}
$metricComparisonMissingScenarios = @(Get-JsonArray $validation "metricComparisonMissingScenarios")
$metricComparisonMissingScenarioCount = [int](Get-JsonValue $validation "metricComparisonMissingScenarioCount")
if ($metricComparisonMissingScenarioCount -gt 0) {
    $scenarioText = if ($metricComparisonMissingScenarios.Count -gt 0) {
        $metricComparisonMissingScenarios -join ', '
    }
    else {
        "$metricComparisonMissingScenarioCount scenario(s)"
    }
    $recommendations.Add("Fill offline fixture metrics for the missing benchmark-plan scenario coverage before G2 acceptance review: $scenarioText.") | Out-Null
}
if ((Test-Truthy (Get-JsonValue $validation "metricComparisonPresent")) -and
    -not (Test-Truthy (Get-JsonValue $validation "metricComparisonWdspBackedEvidence"))) {
    $engineText = [string](Get-JsonValue $validation "metricComparisonEvidenceEngine")
    if ([string]::IsNullOrWhiteSpace($engineText)) {
        $engineText = "missing"
    }
    $recommendations.Add("Regenerate offline fixture evidence with run-dsp-wdsp-fixture-matrix.ps1; current fixture comparison evidenceEngine='$engineText' is not acceptable for DSP graduation.") | Out-Null
}
if ((Test-Truthy (Get-JsonValue $validation "offlineFixtureMetricsPresent")) -and
    -not (Test-Truthy (Get-JsonValue $validation "offlineFixtureMetricsWdspBackedEvidence"))) {
    $engineText = [string](Get-JsonValue $validation "offlineFixtureMetricsEvidenceEngine")
    if ([string]::IsNullOrWhiteSpace($engineText)) {
        $engineText = "missing"
    }
    $recommendations.Add("Regenerate artifacts/offline-fixture-metrics.json with run-dsp-wdsp-fixture-matrix.ps1; current source metrics evidenceEngine='$engineText' is not WDSP-backed.") | Out-Null
}
$sourceHashStatus = [string](Get-JsonValue $validation "metricComparisonSourceMetricsHashStatus")
if ((Test-Truthy (Get-JsonValue $validation "metricComparisonPresent")) -and
    -not [string]::IsNullOrWhiteSpace($sourceHashStatus) -and
    -not [string]::Equals($sourceHashStatus, "match", [StringComparison]::OrdinalIgnoreCase)) {
    $recommendations.Add("Rerun run-dsp-wdsp-fixture-matrix.ps1 after regenerating or finalizing offline fixture metrics; source metrics hash status is '$sourceHashStatus'.") | Out-Null
}
$runtimeHashStatus = [string](Get-JsonValue $validation "metricComparisonWdspRuntimeHashStatus")
if ((Test-Truthy (Get-JsonValue $validation "metricComparisonPresent")) -and
    -not [string]::IsNullOrWhiteSpace($runtimeHashStatus) -and
    -not [string]::Equals($runtimeHashStatus, "match", [StringComparison]::OrdinalIgnoreCase) -and
    -not [string]::Equals($runtimeHashStatus, "not-wdsp-backed", [StringComparison]::OrdinalIgnoreCase)) {
    $recommendations.Add("Regenerate the WDSP fixture matrix from the current offline fixture metrics; WDSP runtime hash status is '$runtimeHashStatus'.") | Out-Null
}
$runtimeArtifactHashStatus = [string](Get-JsonValue $validation "offlineFixtureMetricsRuntimeArtifactHashStatus")
if ((Test-Truthy (Get-JsonValue $validation "offlineFixtureMetricsPresent")) -and
    -not [string]::IsNullOrWhiteSpace($runtimeArtifactHashStatus) -and
    -not [string]::Equals($runtimeArtifactHashStatus, "match", [StringComparison]::OrdinalIgnoreCase) -and
    -not [string]::Equals($runtimeArtifactHashStatus, "not-wdsp-backed", [StringComparison]::OrdinalIgnoreCase)) {
    $recommendations.Add("Regenerate WDSP fixture evidence from the packaged runtime under review or refresh wdsp-runtime-artifact-audit; runtime artifact hash status is '$runtimeArtifactHashStatus'.") | Out-Null
}
if ($requiredEvidenceGateProblems.Count -gt 0) {
    $gateNames = @($requiredEvidenceGateProblems | ForEach-Object { [string](Get-JsonValue $_ "name") })
    $recommendations.Add("Resolve required evidence gates before acceptance review: $($gateNames -join ', ').") | Out-Null
}
if ($recommendations.Count -eq 0) {
    $recommendations.Add("No validation-triage issues were found; continue with required benchmark, G2/on-air, and cross-radio acceptance gates before any default DSP behavior change.") | Out-Null
}

$status = "ready"
if (-not $validationOk) {
    $status = "validation-needs-attention"
}
elseif ($liveHistoryProvenanceNeedsAttention) {
    $status = "live-history-provenance-needs-attention"
}
elseif ($referencedProblemRecords.Count -gt 0) {
    $status = "referenced-files-need-attention"
}
elseif ($requiredEvidenceGateProblems.Count -gt 0) {
    $status = "evidence-gates-need-attention"
}

$acceptanceReadiness = @(Get-AcceptanceReadinessRecords `
        -Validation $validation `
        -EvidenceGates $evidenceGates `
        -ReportStatus $status `
        -ReferencedProblemCount $referencedProblemRecords.Count `
        -LiveHistoryProvenanceNeedsAttention:$liveHistoryProvenanceNeedsAttention)
$acceptanceReadyStages = @($acceptanceReadiness | Where-Object { Test-Truthy (Get-JsonValue $_ "ready") })
$g2FirstPassStage = Get-JsonArray ([ordered]@{ acceptanceReadiness = $acceptanceReadiness }) "acceptanceReadiness" | Where-Object {
    [string](Get-JsonValue $_ "stageId") -eq "g2-first-pass-evidence"
} | Select-Object -First 1
$candidateComparisonStage = Get-JsonArray ([ordered]@{ acceptanceReadiness = $acceptanceReadiness }) "acceptanceReadiness" | Where-Object {
    [string](Get-JsonValue $_ "stageId") -eq "opt-in-candidate-comparison"
} | Select-Object -First 1
$acceptanceActionPlan = @(Get-AcceptanceActionPlanRecords `
        -EvidenceGates $evidenceGates `
        -AcceptanceReadiness $acceptanceReadiness)
$acceptanceRequiredActions = @($acceptanceActionPlan | Where-Object { Test-Truthy (Get-JsonValue $_ "requiredForAcceptance") })
$acceptanceManualActions = @($acceptanceActionPlan | Where-Object {
        -not [string]::IsNullOrWhiteSpace([string](Get-JsonValue $_ "manualAction"))
    })
$acceptanceActionCategoryCounts = @{}
foreach ($action in $acceptanceActionPlan) {
    Add-Count -Counts $acceptanceActionCategoryCounts -Key ([string](Get-JsonValue $action "category"))
}
$primaryAcceptanceAction = $null
if ($acceptanceActionPlan.Count -gt 0) {
    $primaryAcceptanceAction = $acceptanceActionPlan[0]
}
$primaryAcceptanceCommandTemplate = [string](Get-JsonValue $primaryAcceptanceAction "commandTemplate")
$primaryAcceptanceManualAction = [string](Get-JsonValue $primaryAcceptanceAction "manualAction")
$primaryAcceptanceCommandSteps = @(Get-JsonArray $primaryAcceptanceAction "commandSteps")

$report = [ordered]@{
    schemaVersion = 1
    tool = "summarize-dsp-modernization-validation-report"
    generatedUtc = [DateTimeOffset]::UtcNow
    validationReportPath = ConvertTo-PortablePath -Root $bundlePath -Path $validationReportFullPath
    bundleDir = $bundlePath
    validationOk = $validationOk
    status = $status
    errorCount = [int](Get-JsonValue $validation "errorCount")
    warningCount = [int](Get-JsonValue $validation "warningCount")
    artifactReferencedFileCount = $referencedFiles.Count
    artifactReferencedFileProblemCount = $referencedProblemRecords.Count
    liveDiagnosticsHistoryTraceSourceStatus = $liveTraceSourceStatus
    liveDiagnosticsHistoryTraceSourceCheckedCount = [int](Get-JsonValue $validation "liveDiagnosticsHistoryTraceSourceCheckedCount")
    liveDiagnosticsHistoryTraceSourceMissingCount = [int](Get-JsonValue $validation "liveDiagnosticsHistoryTraceSourceMissingCount")
    liveDiagnosticsHistoryTraceSourceInvalidCount = [int](Get-JsonValue $validation "liveDiagnosticsHistoryTraceSourceInvalidCount")
    liveDiagnosticsHistoryTraceSourceJsonlMissingCount = [int](Get-JsonValue $validation "liveDiagnosticsHistoryTraceSourceJsonlMissingCount")
    liveDiagnosticsHistoryTraceSourceSummaryHashPresentCount = [int](Get-JsonValue $validation "liveDiagnosticsHistoryTraceSourceSummaryHashPresentCount")
    liveDiagnosticsHistoryTraceSourceSummaryHashMissingCount = [int](Get-JsonValue $validation "liveDiagnosticsHistoryTraceSourceSummaryHashMissingCount")
    liveDiagnosticsHistoryTraceSourceSummaryHashMismatchCount = [int](Get-JsonValue $validation "liveDiagnosticsHistoryTraceSourceSummaryHashMismatchCount")
    liveDiagnosticsHistoryTraceSourceJsonlHashPresentCount = [int](Get-JsonValue $validation "liveDiagnosticsHistoryTraceSourceJsonlHashPresentCount")
    liveDiagnosticsHistoryTraceSourceJsonlHashMissingCount = [int](Get-JsonValue $validation "liveDiagnosticsHistoryTraceSourceJsonlHashMissingCount")
    liveDiagnosticsHistoryTraceSourceJsonlHashMismatchCount = [int](Get-JsonValue $validation "liveDiagnosticsHistoryTraceSourceJsonlHashMismatchCount")
    liveHistoryTraceSourceCount = $liveHistorySourceRecords.Count
    liveHistoryTraceSourceProblemCount = $failedLiveSources.Count
    sourceTypeCounts = @(Convert-CountsToRecords -Counts $sourceTypeCounts -NameField "sourceType")
    liveHistoryTraceSourceStatusCounts = @(Convert-CountsToRecords -Counts $liveSourceStatusCounts -NameField "sourceStatus")
    liveHistoryTraceSummaryHashStatusCounts = @(Convert-CountsToRecords -Counts $liveSummaryHashStatusCounts -NameField "summaryHashStatus")
    liveHistoryTraceJsonlHashStatusCounts = @(Convert-CountsToRecords -Counts $liveJsonlHashStatusCounts -NameField "jsonlHashStatus")
    evidenceGateCount = $evidenceGates.Count
    evidenceGateProblemCount = $evidenceGateProblems.Count
    requiredEvidenceGateProblemCount = $requiredEvidenceGateProblems.Count
    evidenceGates = @($evidenceGates)
    offlineFixtureMetricsPresent = Test-Truthy (Get-JsonValue $validation "offlineFixtureMetricsPresent")
    offlineFixtureMetricsEvidenceEngine = [string](Get-JsonValue $validation "offlineFixtureMetricsEvidenceEngine")
    offlineFixtureMetricsEvidenceTool = [string](Get-JsonValue $validation "offlineFixtureMetricsEvidenceTool")
    offlineFixtureMetricsWdspBackedEvidence = Test-Truthy (Get-JsonValue $validation "offlineFixtureMetricsWdspBackedEvidence")
    offlineFixtureMetricsSyntheticFallbackEvidence = Test-Truthy (Get-JsonValue $validation "offlineFixtureMetricsSyntheticFallbackEvidence")
    offlineFixtureMetricsScenarioCount = [int](Get-JsonValue $validation "offlineFixtureMetricsScenarioCount")
    offlineFixtureMetricsComparisonCount = [int](Get-JsonValue $validation "offlineFixtureMetricsComparisonCount")
    offlineFixtureMetricsPath = [string](Get-JsonValue $validation "offlineFixtureMetricsPath")
    offlineFixtureMetricsSha256 = [string](Get-JsonValue $validation "offlineFixtureMetricsSha256")
    offlineFixtureMetricsWdspRuntimeRid = [string](Get-JsonValue $validation "offlineFixtureMetricsWdspRuntimeRid")
    offlineFixtureMetricsWdspRuntimePath = [string](Get-JsonValue $validation "offlineFixtureMetricsWdspRuntimePath")
    offlineFixtureMetricsWdspRuntimePathKind = [string](Get-JsonValue $validation "offlineFixtureMetricsWdspRuntimePathKind")
    offlineFixtureMetricsWdspRuntimeFileName = [string](Get-JsonValue $validation "offlineFixtureMetricsWdspRuntimeFileName")
    offlineFixtureMetricsWdspRuntimeLength = [int64](Get-JsonValue $validation "offlineFixtureMetricsWdspRuntimeLength")
    offlineFixtureMetricsWdspRuntimeSha256 = [string](Get-JsonValue $validation "offlineFixtureMetricsWdspRuntimeSha256")
    offlineFixtureMetricsWdspRuntimeStatus = [string](Get-JsonValue $validation "offlineFixtureMetricsWdspRuntimeStatus")
    offlineFixtureMetricsRuntimeArtifactHashStatus = [string](Get-JsonValue $validation "offlineFixtureMetricsRuntimeArtifactHashStatus")
    metricComparisonPresent = Test-Truthy (Get-JsonValue $validation "metricComparisonPresent")
    metricComparisonReady = Test-Truthy (Get-JsonValue $validation "metricComparisonReady")
    metricComparisonReportReadyForReview = Test-Truthy (Get-JsonValue $validation "metricComparisonReportReadyForReview")
    metricComparisonMetricCoverageReadyForReview = Test-Truthy (Get-JsonValue $validation "metricComparisonMetricCoverageReadyForReview")
    metricComparisonEvidenceEngine = [string](Get-JsonValue $validation "metricComparisonEvidenceEngine")
    metricComparisonEvidenceTool = [string](Get-JsonValue $validation "metricComparisonEvidenceTool")
    metricComparisonWdspBackedEvidence = Test-Truthy (Get-JsonValue $validation "metricComparisonWdspBackedEvidence")
    metricComparisonSyntheticFallbackEvidence = Test-Truthy (Get-JsonValue $validation "metricComparisonSyntheticFallbackEvidence")
    metricComparisonSourceMetricsPath = [string](Get-JsonValue $validation "metricComparisonSourceMetricsPath")
    metricComparisonSourceMetricsSha256 = [string](Get-JsonValue $validation "metricComparisonSourceMetricsSha256")
    metricComparisonSourceMetricsHashStatus = [string](Get-JsonValue $validation "metricComparisonSourceMetricsHashStatus")
    metricComparisonWdspRuntimeRid = [string](Get-JsonValue $validation "metricComparisonWdspRuntimeRid")
    metricComparisonWdspRuntimePath = [string](Get-JsonValue $validation "metricComparisonWdspRuntimePath")
    metricComparisonWdspRuntimePathKind = [string](Get-JsonValue $validation "metricComparisonWdspRuntimePathKind")
    metricComparisonWdspRuntimeFileName = [string](Get-JsonValue $validation "metricComparisonWdspRuntimeFileName")
    metricComparisonWdspRuntimeLength = [int64](Get-JsonValue $validation "metricComparisonWdspRuntimeLength")
    metricComparisonWdspRuntimeSha256 = [string](Get-JsonValue $validation "metricComparisonWdspRuntimeSha256")
    metricComparisonWdspRuntimeStatus = [string](Get-JsonValue $validation "metricComparisonWdspRuntimeStatus")
    metricComparisonWdspRuntimeIdentityReadyForReview = Test-Truthy (Get-JsonValue $validation "metricComparisonWdspRuntimeIdentityReadyForReview")
    metricComparisonWdspRuntimeHashStatus = [string](Get-JsonValue $validation "metricComparisonWdspRuntimeHashStatus")
    metricComparisonFixtureScenarioScope = [string](Get-JsonValue $validation "metricComparisonFixtureScenarioScope")
    metricComparisonSkippedNonFixtureScenarioCount = [int](Get-JsonValue $validation "metricComparisonSkippedNonFixtureScenarioCount")
    metricComparisonSkippedNonFixtureScenarioIds = @(Get-JsonArray $validation "metricComparisonSkippedNonFixtureScenarioIds")
    metricComparisonRegressionCount = [int](Get-JsonValue $validation "metricComparisonRegressionCount")
    metricComparisonGateFailureCount = [int](Get-JsonValue $validation "metricComparisonGateFailureCount")
    metricComparisonMissingScenarioCount = [int](Get-JsonValue $validation "metricComparisonMissingScenarioCount")
    metricComparisonMissingScenarios = @(Get-JsonArray $validation "metricComparisonMissingScenarios")
    metricComparisonMissingCurrentBaselineCount = [int](Get-JsonValue $validation "metricComparisonMissingCurrentBaselineCount")
    metricComparisonMissingThetisBaselineCount = [int](Get-JsonValue $validation "metricComparisonMissingThetisBaselineCount")
    metricComparisonMissingCandidateCount = [int](Get-JsonValue $validation "metricComparisonMissingCandidateCount")
    metricComparisonMissingMetricValueCount = [int](Get-JsonValue $validation "metricComparisonMissingMetricValueCount")
    metricComparisonCandidateComparisonCount = [int](Get-JsonValue $validation "metricComparisonCandidateComparisonCount")
    nativeRuntimeArtifactAuditPresent = Test-Truthy (Get-JsonValue $validation "nativeRuntimeArtifactAuditPresent")
    nativeRuntimeArtifactAuditReadyForWinX64Package = Test-Truthy (Get-JsonValue $validation "nativeRuntimeArtifactAuditReadyForWinX64Package")
    nativeRuntimeArtifactAuditPendingRidCount = [int](Get-JsonValue $validation "nativeRuntimeArtifactAuditPendingRidCount")
    nativeRuntimeArtifactAuditArtifactCount = [int](Get-JsonValue $validation "nativeRuntimeArtifactAuditArtifactCount")
    nativeRuntimeArtifactAuditWinX64NativePath = [string](Get-JsonValue $validation "nativeRuntimeArtifactAuditWinX64NativePath")
    nativeRuntimeArtifactAuditWinX64NativeLength = [int64](Get-JsonValue $validation "nativeRuntimeArtifactAuditWinX64NativeLength")
    nativeRuntimeArtifactAuditWinX64NativeSha256 = [string](Get-JsonValue $validation "nativeRuntimeArtifactAuditWinX64NativeSha256")
    acceptanceReadinessStageCount = $acceptanceReadiness.Count
    acceptanceReadinessReadyStageCount = $acceptanceReadyStages.Count
    g2FirstPassAcceptanceReady = Test-Truthy (Get-JsonValue $g2FirstPassStage "ready")
    candidateComparisonReady = Test-Truthy (Get-JsonValue $candidateComparisonStage "ready")
    defaultBehaviorChangeReady = $false
    defaultGraduationReady = $false
    crossRadioValidationRequired = $true
    crossRadioValidationEvidenceStatus = "not-captured"
    acceptanceReadiness = @($acceptanceReadiness)
    acceptanceActionPlanCount = $acceptanceActionPlan.Count
    acceptanceRequiredActionCount = $acceptanceRequiredActions.Count
    acceptanceManualActionCount = $acceptanceManualActions.Count
    acceptanceActionCategoryCounts = @(Convert-CountsToRecords -Counts $acceptanceActionCategoryCounts -NameField "category")
    primaryAcceptanceActionId = [string](Get-JsonValue $primaryAcceptanceAction "actionId")
    primaryAcceptanceActionPriority = Get-JsonValue $primaryAcceptanceAction "priority"
    primaryAcceptanceActionStageId = [string](Get-JsonValue $primaryAcceptanceAction "stageId")
    primaryAcceptanceActionGateId = [string](Get-JsonValue $primaryAcceptanceAction "gateId")
    primaryAcceptanceActionCategory = [string](Get-JsonValue $primaryAcceptanceAction "category")
    primaryAcceptanceActionRequired = Test-Truthy (Get-JsonValue $primaryAcceptanceAction "requiredForAcceptance")
    primaryAcceptanceActionManual = -not [string]::IsNullOrWhiteSpace($primaryAcceptanceManualAction)
    primaryAcceptanceCommandTemplate = $primaryAcceptanceCommandTemplate
    primaryAcceptanceCommandStepCount = $primaryAcceptanceCommandSteps.Count
    primaryAcceptanceCommandSteps = @($primaryAcceptanceCommandSteps)
    primaryAcceptanceManualAction = $primaryAcceptanceManualAction
    primaryAcceptanceExpectedArtifact = [string](Get-JsonValue $primaryAcceptanceAction "expectedArtifact")
    primaryAcceptanceFollowUp = [string](Get-JsonValue $primaryAcceptanceAction "followUp")
    acceptanceActionPlan = @($acceptanceActionPlan)
    issueCodeCounts = @($issueCodeCounts)
    failedLiveHistoryTraceSources = @($failedLiveSources.ToArray())
    failedReferencedFiles = @($referencedProblemRecords.ToArray())
    recommendations = @($recommendations.ToArray())
}

Write-JsonFile -Path $ReportPath -Value $report

if (-not $NoMarkdown) {
    Set-Content -LiteralPath $MarkdownPath -Value (Build-MarkdownReport -Report $report) -Encoding UTF8
}

if ($JsonOnly) {
    $report | ConvertTo-Json -Depth 64
}
else {
    Write-Host "DSP modernization validation triage written: $ReportPath"
    if (-not $NoMarkdown) {
        Write-Host "Markdown: $MarkdownPath"
    }
    Write-Host "Status: $($report.status), validation OK: $($report.validationOk), referenced-file problems: $($report.artifactReferencedFileProblemCount)"
}

if ($FailOnIssues -and $report.status -ne "ready") {
    exit 1
}
