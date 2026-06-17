param(
    [string]$ValidationReportPath = "",

    [string]$BundleDir = "",

    [string]$ReportPath = "",

    [string]$MarkdownPath = "",

    [switch]$FailOnIssues,

    [switch]$FailOnOptInDspBuildOutBlocked,

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

function Get-IntegerValueOrDefault {
    param(
        $Value,
        [int]$Default = 0
    )

    if ($null -eq $Value) {
        return $Default
    }

    $parsed = 0
    if ([int]::TryParse([string]$Value, [ref]$parsed)) {
        return $parsed
    }

    return $Default
}

function Test-LiveAcceptanceArtifactId {
    param([string]$ArtifactId)

    return @(
        "live-diagnostics-trace-comparison",
        "live-diagnostics-trace-comparison-thetis-parity",
        "live-diagnostics-trace-index",
        "live-diagnostics-matrix-report-off-baseline",
        "live-diagnostics-matrix-report-thetis-parity",
        "live-diagnostics-matrix-report-baseline",
        "live-diagnostics-matrix-report-candidate",
        "live-diagnostics-history"
    ) -contains $ArtifactId
}

function Get-LiveAcceptanceArtifactProblemRecords {
    param($Validation)

    $records = New-Object System.Collections.Generic.List[object]
    foreach ($artifact in @(Get-JsonArray $Validation "artifactFiles")) {
        $artifactId = [string](Get-JsonValue $artifact "id")
        if (-not (Test-LiveAcceptanceArtifactId $artifactId)) {
            continue
        }
        if (-not (Test-Truthy (Get-JsonValue $artifact "required"))) {
            continue
        }
        if (Test-Truthy (Get-JsonValue $artifact "ok")) {
            continue
        }

        $records.Add([ordered]@{
                id = $artifactId
                kind = [string](Get-JsonValue $artifact "kind")
                path = [string](Get-JsonValue $artifact "path")
                comparisonIds = @(Get-JsonArray $artifact "comparisonIds")
            }) | Out-Null
    }

    return @($records.ToArray())
}

function Get-LiveAcceptanceArtifactProblemIds {
    param($Records)

    return @($Records |
        ForEach-Object { [string](Get-JsonValue $_ "id") } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Sort-Object -Unique)
}

function Format-LiveAcceptanceArtifactProblemIds {
    param($Records)

    $ids = @(Get-LiveAcceptanceArtifactProblemIds -Records $Records)
    $text = ($ids | Select-Object -First 8) -join ", "
    if ([string]::IsNullOrWhiteSpace($text)) {
        return "none"
    }

    if ($ids.Count -gt 8) {
        return "$text, +$($ids.Count - 8) more"
    }

    return $text
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

function Get-CountRecordLabel {
    param(
        $Record,
        [Parameter(Mandatory = $true)][string]$NameField
    )

    $label = [string](Get-JsonValue $Record $NameField)
    if ([string]::IsNullOrWhiteSpace($label)) {
        $label = [string](Get-JsonValue $Record "name")
    }

    return $label
}

function Get-TopCountText {
    param(
        $Items,
        [Parameter(Mandatory = $true)][string]$NameField
    )

    $records = @($Items | Where-Object {
            -not [string]::IsNullOrWhiteSpace((Get-CountRecordLabel -Record $_ -NameField $NameField))
        })
    if ($records.Count -eq 0) {
        return ""
    }

    $top = @($records | Sort-Object `
            @{ Expression = { [int](Get-JsonValue $_ "count") }; Descending = $true },
            @{ Expression = { Get-CountRecordLabel -Record $_ -NameField $NameField }; Ascending = $true })[0]
    $label = Get-CountRecordLabel -Record $top -NameField $NameField
    $count = Get-JsonValue $top "count"
    if ($null -eq $count) {
        return $label
    }

    return "$label ($count)"
}

function Get-ExternalCandidateIds {
    param($Values)

    $ids = New-Object System.Collections.Generic.List[string]
    foreach ($value in @($Values)) {
        if ($null -eq $value) {
            continue
        }

        $id = ""
        if ($value -is [string]) {
            $id = [string]$value
        }
        else {
            $id = [string](Get-JsonValue $value "id")
            if ([string]::IsNullOrWhiteSpace($id)) {
                $id = [string](Get-JsonValue $value "candidateId")
            }
        }

        if (-not [string]::IsNullOrWhiteSpace($id)) {
            $ids.Add($id.Trim()) | Out-Null
        }
    }

    return @($ids.ToArray() | Sort-Object -Unique)
}

function Join-ExternalCandidateIds {
    param($Values)

    $ids = @(Get-ExternalCandidateIds $Values)
    if ($ids.Count -eq 0) {
        return ""
    }

    return ($ids -join ", ")
}

function Get-ExternalCandidateIssueSummaryText {
    param(
        $Details,
        [int]$MaxCandidates = 3,
        [int]$MaxIssues = 3
    )

    $items = New-Object System.Collections.Generic.List[string]
    foreach ($detail in @($Details | Select-Object -First $MaxCandidates)) {
        $id = [string](Get-JsonValue $detail "id")
        if ([string]::IsNullOrWhiteSpace($id)) {
            $id = "unknown"
        }

        $issues = @(Get-JsonArray $detail "issues" | Select-Object -First $MaxIssues)
        if ($issues.Count -gt 0) {
            $items.Add("${id}: $($issues -join ', ')") | Out-Null
            continue
        }

        $blockers = @(Get-JsonArray $detail "blockers" | Select-Object -First $MaxIssues)
        if ($blockers.Count -gt 0) {
            $items.Add("$id blockers: $($blockers -join ', ')") | Out-Null
            continue
        }

        $items.Add($id) | Out-Null
    }

    if ($items.Count -eq 0) {
        return ""
    }

    return ($items.ToArray() -join "; ")
}

function Get-ExternalIssueTopText {
    param($IssueCounts)

    return Get-TopCountText -Items @(Get-JsonArray ([ordered]@{ items = $IssueCounts }) "items") -NameField "issue"
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
        captureReadinessStatus = [string](Get-JsonValue $Record "captureReadinessStatus")
        hardGatePass = Get-JsonValue $Record "hardGatePass"
        strictPreflightPass = Get-JsonValue $Record "strictPreflightPass"
        topCaptureConstraintName = [string](Get-JsonValue $Record "topCaptureConstraintName")
        topCaptureConstraintCount = Get-JsonValue $Record "topCaptureConstraintCount"
        topCaptureHardConstraintName = [string](Get-JsonValue $Record "topCaptureHardConstraintName")
        topCaptureHardConstraintCount = Get-JsonValue $Record "topCaptureHardConstraintCount"
        captureReadinessFieldMismatchCount = Get-JsonValue $Record "captureReadinessFieldMismatchCount"
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
    $liveAcceptanceArtifactProblems = @(Get-LiveAcceptanceArtifactProblemRecords -Validation $Validation)
    $liveAcceptanceArtifactProblemText = Format-LiveAcceptanceArtifactProblemIds -Records $liveAcceptanceArtifactProblems
    $validationRemediation = "Resolve validation errors before using this bundle as DSP modernization evidence."
    if ($liveAcceptanceArtifactProblems.Count -gt 0) {
        $validationRemediation = "Resolve validation errors and capture/regenerate required live acceptance artifacts before using this bundle as DSP modernization evidence."
    }

    $gates.Add((New-EvidenceGateRecord `
                -GateId "validation-report" `
                -Name "Strict bundle validation" `
                -Ready:$ValidationOk `
                -Status $validationStatus `
                -Detail "errors=$(Get-JsonValue $Validation "errorCount"); warnings=$(Get-JsonValue $Validation "warningCount"); liveAcceptanceArtifactProblems=$($liveAcceptanceArtifactProblems.Count); liveAcceptanceArtifactProblemIds=$liveAcceptanceArtifactProblemText" `
                -Remediation $validationRemediation)) | Out-Null

    $hardwareStatus = [string](Get-JsonValue $Validation "hardwareEvidenceStatus")
    $hardwareReady = [string]::Equals($hardwareStatus, "g2-hardware-evidence-ready", [StringComparison]::OrdinalIgnoreCase)
    $gates.Add((New-EvidenceGateRecord `
                -GateId "g2-hardware" `
                -Name "G2 hardware evidence" `
                -Ready:$hardwareReady `
                -Status $hardwareStatus `
                -Detail "target=$(Get-JsonValue $Validation "hardwareTarget"); captureTarget=$(Get-JsonValue $Validation "captureHardwareTarget"); diagnosticsPresent=$(Get-JsonValue $Validation "hardwareDiagnosticsPresent")" `
                -Remediation "Capture connected G2 hardware diagnostics and keep benchmark-plan/capture-manifest targets aligned.")) | Out-Null

    $crossRadioStatus = [string](Get-JsonValue $Validation "crossRadioValidationEvidenceStatus")
    if ([string]::IsNullOrWhiteSpace($crossRadioStatus)) {
        $crossRadioStatus = "not-captured"
    }
    $crossRadioReady = Test-Truthy (Get-JsonValue $Validation "crossRadioValidationReady")
    $crossRadioTargets = (@(Get-JsonArray $Validation "crossRadioValidationNonG2TargetIds") | ForEach-Object { [string]$_ }) -join ", "
    $crossRadioScenarios = (@(Get-JsonArray $Validation "crossRadioValidationScenarioIds") | ForEach-Object { [string]$_ }) -join ", "
    $crossRadioComparisons = (@(Get-JsonArray $Validation "crossRadioValidationComparisonIds") | ForEach-Object { [string]$_ }) -join ", "
    $crossRadioSourceDetail = "sourceReports=$(Get-JsonValue $Validation "crossRadioValidationSourceReportCount"); nonG2Sources=$(Get-JsonValue $Validation "crossRadioValidationNonG2SourceReportCount"); readyNonG2Sources=$(Get-JsonValue $Validation "crossRadioValidationReadyNonG2SourceReportCount"); sourceBacked=$(Get-JsonValue $Validation "crossRadioValidationSourceBackedEvidenceReady")"
    $gates.Add((New-EvidenceGateRecord `
                -GateId "cross-radio-validation" `
                -Name "Cross-radio validation evidence" `
                -Ready:$crossRadioReady `
                -Status $crossRadioStatus `
                -RequiredForAcceptance:$false `
                -Detail "present=$(Get-JsonValue $Validation "crossRadioValidationPresent"); nonG2Targets=$(Get-JsonValue $Validation "crossRadioValidationNonG2TargetCount") [$crossRadioTargets]; scenarios=$(Get-JsonValue $Validation "crossRadioValidationScenarioCount") [$crossRadioScenarios]; comparisons=$(Get-JsonValue $Validation "crossRadioValidationComparisonCount") [$crossRadioComparisons]; $crossRadioSourceDetail; defaultApprovalClaimed=$(Get-JsonValue $Validation "crossRadioValidationDefaultBehaviorChangeApproved")" `
                -Remediation "Attach a source-backed cross-radio report from at least one non-G2 radio with clean metric, Zeus live-trace, and Thetis live-trace comparisons before default DSP graduation review.")) | Out-Null

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

    $nativeStageTimingReady = Test-Truthy (Get-JsonValue $Validation "nativeStageTimingReportReady")
    $nativeStageTimingStatus = [string](Get-JsonValue $Validation "nativeStageTimingReportStatus")
    if ([string]::IsNullOrWhiteSpace($nativeStageTimingStatus)) {
        $nativeStageTimingStatus = "not-captured"
    }
    $gates.Add((New-EvidenceGateRecord `
                -GateId "native-stage-timing-report" `
                -Name "WDSP native stage timing/allocation report" `
                -Ready:$nativeStageTimingReady `
                -Status $nativeStageTimingStatus `
                -Detail "present=$(Get-JsonValue $Validation "nativeStageTimingReportPresent"); runs=$(Get-JsonValue $Validation "nativeStageTimingRunCount"); stages=$(Get-JsonValue $Validation "nativeStageTimingStageRecordCount"); missingStageRuns=$(Get-JsonValue $Validation "nativeStageTimingMissingStageTimingRunCount"); missingAllocationRuns=$(Get-JsonValue $Validation "nativeStageTimingMissingAllocationProbeRunCount"); budgetFailures=$(Get-JsonValue $Validation "nativeStageTimingBudgetFailureCount"); metricsHash=$(Get-JsonValue $Validation "nativeStageTimingMetricsHashStatus"); runtimeHash=$(Get-JsonValue $Validation "nativeStageTimingWdspRuntimeHashStatus"); nativeCStatus=$(Get-JsonValue $Validation "nativeStageTimingNativeCStageInstrumentationStatus"); allocationProbe=$(Get-JsonValue $Validation "nativeStageTimingNativeAllocationProbeStatus")" `
                -Remediation "Run summarize-dsp-native-stage-timing.ps1 after WDSP-backed fixture evidence so stage timing, managed allocation deltas, source metrics hash, runtime hash, and native C instrumentation limitations are explicit.")) | Out-Null

    $sourceDriftReady = Test-Truthy (Get-JsonValue $Validation "wdspSourceDriftReportReady")
    $sourceDriftStatus = [string](Get-JsonValue $Validation "wdspSourceDriftReportStatus")
    if ([string]::IsNullOrWhiteSpace($sourceDriftStatus)) {
        $sourceDriftStatus = "not-captured"
    }
    $gates.Add((New-EvidenceGateRecord `
                -GateId "wdsp-source-drift-report" `
                -Name "WDSP source drift classification" `
                -Ready:$sourceDriftReady `
                -Status $sourceDriftStatus `
                -RequiredForAcceptance:$false `
                -Detail "present=$(Get-JsonValue $Validation "wdspSourceDriftReportPresent"); files=$(Get-JsonValue $Validation "wdspSourceDriftFileCount"); deltas=$(Get-JsonValue $Validation "wdspSourceDriftDeltaCount"); likelyDefects=$(Get-JsonValue $Validation "wdspSourceDriftLikelyDefectCount"); normalizedLineEndings=$(Get-JsonValue $Validation "wdspSourceDriftReportNormalizedLineEndings")" `
                -Remediation "Run compare-wdsp-source-drift.ps1 against the local Thetis WDSP source and resolve or classify likely-defect source drift before native WDSP modernization.")) | Out-Null

    $benchmarkPlanStatus = [string](Get-JsonValue $Validation "benchmarkPlanStatus")
    $benchmarkPlanReady = [string]::Equals($benchmarkPlanStatus, "ready", [StringComparison]::OrdinalIgnoreCase)
    $benchmarkMissingFamilies = @(Get-JsonArray $Validation "benchmarkPlanMissingAcceptanceScenarioFamilyIds")
    $benchmarkMissingFamiliesText = $benchmarkMissingFamilies -join ", "
    $benchmarkMissingContractText = @(
        "comparisons=$(Get-JsonValue $Validation "benchmarkPlanScenarioMissingRequiredComparisonCount")",
        "metrics=$(Get-JsonValue $Validation "benchmarkPlanScenarioMissingRequiredMetricCount")",
        "gates=$(Get-JsonValue $Validation "benchmarkPlanScenarioMissingAcceptanceGateCount")"
    ) -join "; "
    $gates.Add((New-EvidenceGateRecord `
                -GateId "benchmark-plan-coverage" `
                -Name "Benchmark plan acceptance coverage" `
                -Ready:$benchmarkPlanReady `
                -Status $benchmarkPlanStatus `
                -Detail "scenarios=$(Get-JsonValue $Validation "benchmarkPlanScenarioCount"); requiredFamilies=$(Get-JsonValue $Validation "benchmarkPlanRequiredAcceptanceScenarioFamilyCount"); coveredFamilies=$(Get-JsonValue $Validation "benchmarkPlanCoveredAcceptanceScenarioFamilyCount"); missingFamilies=$(Get-JsonValue $Validation "benchmarkPlanMissingAcceptanceScenarioFamilyCount"); missingFamilyIds=$benchmarkMissingFamiliesText; missingScenarioContracts=$benchmarkMissingContractText" `
                -Remediation "Update benchmark-plan.json so the acceptance matrix covers weak CW/carrier, SSB speech, fading, impulse noise, adjacent strong signal, noise-only gating, AGC pumping, squelch transitions, TX two-tone, TX voice-like audio, PureSignal-safe bypass, and lifecycle scenarios.")) | Out-Null

    $metricCatalogStatus = [string](Get-JsonValue $Validation "metricCatalogStatus")
    $metricCatalogReady = [string]::Equals($metricCatalogStatus, "ready", [StringComparison]::OrdinalIgnoreCase)
    $metricCatalogProblemIds = @(Get-JsonArray $Validation "metricCatalogContractProblemMetricIds") -join ", "
    $gates.Add((New-EvidenceGateRecord `
                -GateId "benchmark-metric-catalog" `
                -Name "Benchmark metric catalog" `
                -Ready:$metricCatalogReady `
                -Status $metricCatalogStatus `
                -Detail "metrics=$(Get-JsonValue $Validation "metricCatalogMetricCount"); required=$(Get-JsonValue $Validation "metricCatalogRequiredMetricCount"); missingRequired=$(Get-JsonValue $Validation "metricCatalogMissingRequiredMetricCount"); contractReady=$(Get-JsonValue $Validation "metricCatalogAcceptanceContractReady"); missingThreshold=$(Get-JsonValue $Validation "metricCatalogMissingThresholdCount"); missingComparator=$(Get-JsonValue $Validation "metricCatalogMissingComparatorCount"); invalidComparator=$(Get-JsonValue $Validation "metricCatalogInvalidComparatorCount"); missingUnit=$(Get-JsonValue $Validation "metricCatalogMissingUnitCount"); missingSafety=$(Get-JsonValue $Validation "metricCatalogMissingSafetyClassCount"); missingScope=$(Get-JsonValue $Validation "metricCatalogMissingAcceptanceScopeCount"); problemMetricIds=$metricCatalogProblemIds" `
                -Remediation "Capture or update benchmark-metric-catalog.json so required metrics declare direction, threshold, comparator, unit, safety class, and acceptance scope.")) | Out-Null

    $pureSignalSafeBypassReady = Test-Truthy (Get-JsonValue $Validation "pureSignalSafeBypassReportReady")
    $pureSignalSafeBypassPresent = Test-Truthy (Get-JsonValue $Validation "pureSignalSafeBypassReportPresent")
    $pureSignalSafeBypassStatus = [string](Get-JsonValue $Validation "pureSignalSafeBypassReportStatus")
    if ([string]::IsNullOrWhiteSpace($pureSignalSafeBypassStatus)) {
        $pureSignalSafeBypassStatus = if ($pureSignalSafeBypassPresent) { "not-ready" } else { "not-captured" }
    }
    $pureSignalMissingModes = (@(Get-JsonArray $Validation "pureSignalSafeBypassMissingModes") | ForEach-Object { [string]$_ }) -join ", "
    $gates.Add((New-EvidenceGateRecord `
                -GateId "puresignal-safe-bypass" `
                -Name "PureSignal safe-bypass TX bench" `
                -Ready:$pureSignalSafeBypassReady `
                -Status $pureSignalSafeBypassStatus `
                -RequiredForAcceptance:$false `
                -Detail "present=$pureSignalSafeBypassPresent; scenario=$(Get-JsonValue $Validation "pureSignalSafeBypassScenarioId"); hardware=$(Get-JsonValue $Validation "pureSignalSafeBypassHardwareTarget"); disabledReady=$(Get-JsonValue $Validation "pureSignalSafeBypassDisabledPathReady"); enabledReady=$(Get-JsonValue $Validation "pureSignalSafeBypassEnabledPathReady"); capturedModes=$(Get-JsonValue $Validation "pureSignalSafeBypassCapturedModeCount"); missingModes=$(Get-JsonValue $Validation "pureSignalSafeBypassMissingModeCount") [$pureSignalMissingModes]; feedbackMin=$(Get-JsonValue $Validation "pureSignalSafeBypassFeedbackStabilityMin"); feedbackThreshold=$(Get-JsonValue $Validation "pureSignalSafeBypassFeedbackStabilityThreshold"); txMonitorCouplingMax=$(Get-JsonValue $Validation "pureSignalSafeBypassTxMonitorCouplingMax"); txMonitorCouplingThreshold=$(Get-JsonValue $Validation "pureSignalSafeBypassTxMonitorCouplingThreshold"); clipping=$(Get-JsonValue $Validation "pureSignalSafeBypassClippingCountTotal"); gateFailures=$(Get-JsonValue $Validation "pureSignalSafeBypassGateFailureCount"); defaultStatePreserved=$(Get-JsonValue $Validation "pureSignalSafeBypassDefaultStatePreserved"); defaultChangeApproved=$(Get-JsonValue $Validation "pureSignalSafeBypassDefaultBehaviorChangeApproved")" `
                -Remediation "Capture G2 PureSignal disabled and enabled TX bench traces, then run summarize-dsp-puresignal-bench.ps1 so puresignal-safe-bypass-report proves default/bypass state, feedback stability, TX monitor isolation, and no clipping.")) | Out-Null

    $externalCandidateStatus = [string](Get-JsonValue $Validation "externalEngineCandidateStatus")
    $externalCandidateReady = [string]::Equals($externalCandidateStatus, "opt-in-gated", [StringComparison]::OrdinalIgnoreCase)
    $externalCandidateMissingIds = Join-ExternalCandidateIds @(Get-JsonArray $Validation "externalEngineCandidateMissingIds")
    $externalCandidateUnsafeIds = Join-ExternalCandidateIds @(Get-JsonArray $Validation "externalEngineCandidateUnsafeIds")
    $externalCandidateTopIssue = Get-ExternalIssueTopText @(Get-JsonArray $Validation "externalEngineCandidateIssueCounts")
    $gates.Add((New-EvidenceGateRecord `
                -GateId "external-engine-candidates" `
                -Name "External DSP/ML candidate catalog" `
                -Ready:$externalCandidateReady `
                -Status $externalCandidateStatus `
                -RequiredForAcceptance:$false `
                -Detail "candidates=$(Get-JsonValue $Validation "externalEngineCandidateCount"); missing=$(Get-JsonValue $Validation "externalEngineCandidateMissingCount"); missingIds=$externalCandidateMissingIds; unsafe=$(Get-JsonValue $Validation "externalEngineCandidateUnsafeCount"); unsafeIds=$externalCandidateUnsafeIds; topIssue=$externalCandidateTopIssue; snapshotMismatch=$(Get-JsonValue $Validation "externalEngineCandidateSnapshotMismatchCount")" `
                -Remediation "Keep RNNoise/DeepFilterNet/SpeexDSP/WebRTC entries opt-in, licensed, packaged, and safety-gated before any bakeoff.")) | Out-Null

    $externalBakeoffRequired = Test-Truthy (Get-JsonValue $Validation "externalEngineBakeoffRequiredByScope")
    $externalBakeoffPresent = Test-Truthy (Get-JsonValue $Validation "externalEngineBakeoffReportPresent")
    $externalBakeoffReady = Test-Truthy (Get-JsonValue $Validation "externalEngineBakeoffReady")
    $externalBakeoffStatus = if (-not $externalBakeoffRequired -and -not $externalBakeoffPresent) { "not-required" } elseif ($externalBakeoffReady) { "ready" } else { "not-ready" }
    $externalBakeoffMissingIds = Join-ExternalCandidateIds @(Get-JsonArray $Validation "externalEngineBakeoffMissingCandidateIds")
    $externalBakeoffUnsafeIds = Join-ExternalCandidateIds @(Get-JsonArray $Validation "externalEngineBakeoffUnsafeCandidateIds")
    $externalBakeoffBlockedIds = Join-ExternalCandidateIds @(Get-JsonArray $Validation "externalEngineBakeoffBlockedCandidateIds")
    $externalBakeoffTopIssue = Get-ExternalIssueTopText @(Get-JsonArray $Validation "externalEngineBakeoffCandidateIssueCounts")
    $externalBakeoffScopeTriggers = (@(Get-JsonArray $Validation "externalEngineBakeoffScopeTriggers") | ForEach-Object { [string]$_ }) -join ", "
    $externalBakeoffFirstSafeCandidateId = [string](Get-JsonValue $Validation "externalEngineBakeoffFirstSafeCandidateId")
    $externalBakeoffEvaluationOrderIds = Join-ExternalCandidateIds @(Get-JsonArray $Validation "externalEngineBakeoffEvaluationOrderCandidateIds")
    $externalBakeoffFirstSafeScenarioIds = (@(Get-JsonArray $Validation "externalEngineBakeoffFirstSafeScenarioIds") | ForEach-Object { [string]$_ }) -join ", "
    $gates.Add((New-EvidenceGateRecord `
                -GateId "external-engine-bakeoff" `
                -Name "External DSP/ML bakeoff report" `
                -Ready:($externalBakeoffReady -or (-not $externalBakeoffRequired -and -not $externalBakeoffPresent)) `
                -Status $externalBakeoffStatus `
                -RequiredForAcceptance:$externalBakeoffRequired `
                -Detail "requiredByScope=$externalBakeoffRequired; scopeTriggers=$externalBakeoffScopeTriggers; present=$externalBakeoffPresent; safe=$(Get-JsonValue $Validation "externalEngineBakeoffSafeForBakeoffCount"); firstSafe=$externalBakeoffFirstSafeCandidateId; firstSafeScenarios=$externalBakeoffFirstSafeScenarioIds; evaluationOrder=$externalBakeoffEvaluationOrderIds; missingIds=$externalBakeoffMissingIds; unsafeIds=$externalBakeoffUnsafeIds; blocked=$(Get-JsonValue $Validation "externalEngineBakeoffBlockedCandidateCount"); blockedIds=$externalBakeoffBlockedIds; topIssue=$externalBakeoffTopIssue" `
                -Remediation "Generate external-engine-bakeoff-report only for scoped opt-in comparisons, then resolve unsafe/missing candidate evidence.")) | Out-Null

    $externalCyclePresent = Test-Truthy (Get-JsonValue $Validation "externalEngineBakeoffCycleSummaryPresent")
    $externalCycleValid = Test-Truthy (Get-JsonValue $Validation "externalEngineBakeoffCycleSummaryValid")
    $externalCycleStatus = [string](Get-JsonValue $Validation "externalEngineBakeoffCycleStatus")
    if ([string]::IsNullOrWhiteSpace($externalCycleStatus)) {
        $externalCycleStatus = if ($externalCyclePresent) { "invalid" } else { "not-captured" }
    }
    $externalCycleReadyToExecute = Test-Truthy (Get-JsonValue $Validation "externalEngineBakeoffCycleReadyToExecute")
    $externalCycleExecuted = Test-Truthy (Get-JsonValue $Validation "externalEngineBakeoffCycleExecuted")
    $externalCycleReady = ($externalCycleValid -and ($externalCycleStatus -in @("ready", "succeeded")))
    $externalCycleGateStatus = if (-not $externalBakeoffReady) {
        "not-in-scope"
    }
    elseif (-not $externalCyclePresent) {
        "not-captured"
    }
    elseif (-not $externalCycleValid) {
        "invalid"
    }
    else {
        $externalCycleStatus
    }
    $gates.Add((New-EvidenceGateRecord `
                -GateId "external-engine-bakeoff-cycle" `
                -Name "External DSP/ML bakeoff cycle summary" `
                -Ready:((-not $externalBakeoffReady) -or $externalCycleReady) `
                -Status $externalCycleGateStatus `
                -RequiredForAcceptance:$false `
                -Detail "bakeoffReady=$externalBakeoffReady; present=$externalCyclePresent; valid=$externalCycleValid; status=$externalCycleStatus; mode=$(Get-JsonValue $Validation "externalEngineBakeoffCycleMode"); candidate=$(Get-JsonValue $Validation "externalEngineBakeoffCycleCandidateId"); comparison=$(Get-JsonValue $Validation "externalEngineBakeoffCycleComparisonId"); readyToExecute=$externalCycleReadyToExecute; executed=$externalCycleExecuted; missingPrerequisites=$(Get-JsonValue $Validation "externalEngineBakeoffCycleMissingPrerequisiteCount"); commandSteps=$(Get-JsonValue $Validation "externalEngineBakeoffCycleCommandStepCount"); expectedArtifacts=$(Get-JsonValue $Validation "externalEngineBakeoffCycleExpectedArtifactCount"); nonZeroExits=$(Get-JsonValue $Validation "externalEngineBakeoffCycleNonZeroExitCount"); sourceReady=$(Get-JsonValue $Validation "externalEngineBakeoffCycleSourceExternalBakeoffReady"); sourceAction=$(Get-JsonValue $Validation "externalEngineBakeoffCycleSourceExternalBakeoffActionPresent"); path=$(Get-JsonValue $Validation "externalEngineBakeoffCyclePath")" `
                -Remediation "Run run-dsp-external-engine-bakeoff.ps1 with -PlanOnly after the external bakeoff report is ready; use -Execute only after fixture metrics exist and the operator has enabled the post-demod candidate path.")) | Out-Null

    $metricComparisonReady = Test-Truthy (Get-JsonValue $Validation "metricComparisonReady")
    $metricComparisonStatus = if ($metricComparisonReady) { "ready" } else { "not-ready" }
    $gates.Add((New-EvidenceGateRecord `
                -GateId "fixture-metric-comparison" `
                -Name "Offline fixture metric comparison" `
                -Ready:$metricComparisonReady `
                -Status $metricComparisonStatus `
                -Detail "present=$(Get-JsonValue $Validation "metricComparisonPresent"); sourceEngine=$(Get-JsonValue $Validation "offlineFixtureMetricsEvidenceEngine"); comparisonEngine=$(Get-JsonValue $Validation "metricComparisonEvidenceEngine"); wdspBacked=$(Get-JsonValue $Validation "metricComparisonWdspBackedEvidence"); sourceHash=$(Get-JsonValue $Validation "metricComparisonSourceMetricsHashStatus"); runtimeHash=$(Get-JsonValue $Validation "metricComparisonWdspRuntimeHashStatus"); runtimeArtifactHash=$(Get-JsonValue $Validation "offlineFixtureMetricsRuntimeArtifactHashStatus"); scope=$(Get-JsonValue $Validation "metricComparisonFixtureScenarioScope"); skippedNonFixture=$(Get-JsonValue $Validation "metricComparisonSkippedNonFixtureScenarioCount"); catalogContractReady=$(Get-JsonValue $Validation "metricComparisonCatalogAcceptanceContractReady"); catalogContractProblems=$(Get-JsonValue $Validation "metricComparisonCatalogContractProblemMetricCount"); regressions=$(Get-JsonValue $Validation "metricComparisonRegressionCount"); gateFailures=$(Get-JsonValue $Validation "metricComparisonGateFailureCount"); missingScenarios=$(Get-JsonValue $Validation "metricComparisonMissingScenarioCount"); missingCurrent=$(Get-JsonValue $Validation "metricComparisonMissingCurrentBaselineCount"); missingThetis=$(Get-JsonValue $Validation "metricComparisonMissingThetisBaselineCount"); missingCandidates=$(Get-JsonValue $Validation "metricComparisonMissingCandidateCount"); missingValues=$(Get-JsonValue $Validation "metricComparisonMissingMetricValueCount")" `
                -Remediation "Run run-dsp-wdsp-fixture-matrix.ps1 and resolve fixture, runtime, or metric regressions before acceptance review.")) | Out-Null

    $liveTraceComparisonReady = Test-Truthy (Get-JsonValue $Validation "liveTraceComparisonReady")
    $liveTraceComparisonStatus = if ($liveTraceComparisonReady) { "ready" } else { "not-ready" }
    $liveTraceTopConstraint = Get-TopCountText `
        -Items @(Get-JsonArray $Validation "liveTraceComparisonCaptureReadinessCandidateTopConstraintCounts") `
        -NameField "constraint"
    $liveTraceTopHardGate = Get-TopCountText `
        -Items @(Get-JsonArray $Validation "liveTraceComparisonCaptureReadinessCandidateTopHardConstraintCounts") `
        -NameField "constraint"
    $gates.Add((New-EvidenceGateRecord `
                -GateId "live-trace-comparison" `
                -Name "Current-Zeus live trace comparison" `
                -Ready:$liveTraceComparisonReady `
                -Status $liveTraceComparisonStatus `
                -Detail "present=$(Get-JsonValue $Validation "liveTraceComparisonPresent"); regressions=$(Get-JsonValue $Validation "liveTraceComparisonRegressionCount"); gateFailures=$(Get-JsonValue $Validation "liveTraceComparisonGateFailureCount"); missingMetrics=$(Get-JsonValue $Validation "liveTraceComparisonMissingMetricDetailCount"); metricDefinitions=$(Get-JsonValue $Validation "liveTraceComparisonMetricDefinitionCount"); metricCatalogAlignment=$(Get-JsonValue $Validation "liveTraceComparisonMetricCatalogAlignmentStatus"); metricCatalogMissing=$(Get-JsonValue $Validation "liveTraceComparisonMetricCatalogMissingMetricCount"); metricCatalogDirectionMismatches=$(Get-JsonValue $Validation "liveTraceComparisonMetricCatalogDirectionMismatchCount"); metricCatalogThresholdMismatches=$(Get-JsonValue $Validation "liveTraceComparisonMetricCatalogThresholdMismatchCount"); metricCatalogSafetyMismatches=$(Get-JsonValue $Validation "liveTraceComparisonMetricCatalogSafetyClassMismatchCount"); hardGateFails=$(Get-JsonValue $Validation "liveTraceComparisonCaptureReadinessCandidateHardGateFailCount"); strictPreflightFails=$(Get-JsonValue $Validation "liveTraceComparisonCaptureReadinessCandidateStrictPreflightFailCount"); topConstraint=$liveTraceTopConstraint; topHardGate=$liveTraceTopHardGate; levelerConstrainedDelta=$(Get-JsonValue $Validation "liveTraceComparisonRxAudioLevelerConstrainedSampleDelta"); levelerConstrainedPctDelta=$(Get-JsonValue $Validation "liveTraceComparisonRxAudioLevelerConstrainedPctDelta"); levelerConstrainedRegressions=$(Get-JsonValue $Validation "liveTraceComparisonRxAudioLevelerConstrainedRegressionCount")" `
                -Remediation "Compare baseline/current-Zeus and candidate live traces or matrices, then resolve capture hard gates, DSP regressions, gate failures, RX leveler constrained-sample growth, and live metric catalog contract mismatches.")) | Out-Null

    $liveTraceThetisComparisonReady = Test-Truthy (Get-JsonValue $Validation "liveTraceThetisComparisonReady")
    $liveTraceThetisComparisonStatus = if ($liveTraceThetisComparisonReady) { "ready" } else { "not-ready" }
    $liveTraceThetisTopConstraint = Get-TopCountText `
        -Items @(Get-JsonArray $Validation "liveTraceThetisComparisonCaptureReadinessCandidateTopConstraintCounts") `
        -NameField "constraint"
    $liveTraceThetisTopHardGate = Get-TopCountText `
        -Items @(Get-JsonArray $Validation "liveTraceThetisComparisonCaptureReadinessCandidateTopHardConstraintCounts") `
        -NameField "constraint"
    $gates.Add((New-EvidenceGateRecord `
                -GateId "thetis-parity-live-comparison" `
                -Name "Thetis-parity live trace comparison" `
                -Ready:$liveTraceThetisComparisonReady `
                -Status $liveTraceThetisComparisonStatus `
                -Detail "present=$(Get-JsonValue $Validation "liveTraceThetisComparisonPresent"); regressions=$(Get-JsonValue $Validation "liveTraceThetisComparisonRegressionCount"); gateFailures=$(Get-JsonValue $Validation "liveTraceThetisComparisonGateFailureCount"); metricDefinitions=$(Get-JsonValue $Validation "liveTraceThetisComparisonMetricDefinitionCount"); metricCatalogAlignment=$(Get-JsonValue $Validation "liveTraceThetisComparisonMetricCatalogAlignmentStatus"); metricCatalogMissing=$(Get-JsonValue $Validation "liveTraceThetisComparisonMetricCatalogMissingMetricCount"); metricCatalogDirectionMismatches=$(Get-JsonValue $Validation "liveTraceThetisComparisonMetricCatalogDirectionMismatchCount"); metricCatalogThresholdMismatches=$(Get-JsonValue $Validation "liveTraceThetisComparisonMetricCatalogThresholdMismatchCount"); metricCatalogSafetyMismatches=$(Get-JsonValue $Validation "liveTraceThetisComparisonMetricCatalogSafetyClassMismatchCount"); hardGateFails=$(Get-JsonValue $Validation "liveTraceThetisComparisonCaptureReadinessCandidateHardGateFailCount"); strictPreflightFails=$(Get-JsonValue $Validation "liveTraceThetisComparisonCaptureReadinessCandidateStrictPreflightFailCount"); topConstraint=$liveTraceThetisTopConstraint; topHardGate=$liveTraceThetisTopHardGate" `
                -Remediation "Compare Thetis-parity baseline and candidate live matrices, then resolve WDSP-authority regressions, capture hard gates, gate failures, and live metric catalog contract mismatches before opt-in DSP review.")) | Out-Null

    $liveMatrixMixedWeakStrongReady = Test-Truthy (Get-JsonValue $Validation "liveMatrixMixedWeakStrongHuntReady")
    $liveMatrixMixedWeakStrongStatus = [string](Get-JsonValue $Validation "liveMatrixMixedWeakStrongStatus")
    if ([string]::IsNullOrWhiteSpace($liveMatrixMixedWeakStrongStatus)) {
        $liveMatrixMixedWeakStrongStatus = "not-evaluated"
    }
    $liveMatrixMixedWeakStrongBestRun = Get-JsonValue $Validation "liveMatrixMixedWeakStrongBestRun"
    $liveMatrixMixedWeakStrongBestText = if ($null -eq $liveMatrixMixedWeakStrongBestRun) {
        "none"
    }
    else {
        "$([string](Get-JsonValue $liveMatrixMixedWeakStrongBestRun "scenarioId"))/$([string](Get-JsonValue $liveMatrixMixedWeakStrongBestRun "comparisonId")) score=$(Get-JsonValue $liveMatrixMixedWeakStrongBestRun "mixedWeakStrongHuntScore") status=$(Get-JsonValue $liveMatrixMixedWeakStrongBestRun "mixedWeakStrongEvidenceStatus")"
    }
    $gates.Add((New-EvidenceGateRecord `
                -GateId "live-matrix-mixed-weak-strong-hunt" `
                -Name "Live matrix mixed weak/strong hunt" `
                -Ready:$liveMatrixMixedWeakStrongReady `
                -Status $liveMatrixMixedWeakStrongStatus `
                -RequiredForAcceptance:$false `
                -Detail "reports=$(Get-JsonValue $Validation "liveMatrixMixedWeakStrongReportCount"); schemaV2Reports=$(Get-JsonValue $Validation "liveMatrixMixedWeakStrongSchemaV2ReportCount"); readyReports=$(Get-JsonValue $Validation "liveMatrixMixedWeakStrongReadyReportCount"); mixedTraces=$(Get-JsonValue $Validation "liveMatrixMixedWeakStrongTraceCount"); readyTraces=$(Get-JsonValue $Validation "liveMatrixMixedWeakStrongReadyTraceCount"); missingRuns=$(Get-JsonValue $Validation "liveMatrixMixedWeakStrongMissingRunCount"); gapWatchRuns=$(Get-JsonValue $Validation "liveMatrixMixedWeakStrongGapWatchRunCount"); weakSamples=$(Get-JsonValue $Validation "liveMatrixMixedWeakStrongWeakInputSampleCount"); strongSamples=$(Get-JsonValue $Validation "liveMatrixMixedWeakStrongStrongInputSampleCount"); best=$liveMatrixMixedWeakStrongBestText" `
                -Remediation "Use the matrix best run as the next G2 live-history/comparison target, or keep scanning active SSB windows until a schema-v2 matrix reports mixed weak+strong hunt readiness.")) | Out-Null

    $manualTuneObserverPresent = Test-Truthy (Get-JsonValue $Validation "manualTuneObserverReportPresent")
    $manualTuneObserverReady = if ($manualTuneObserverPresent) {
        Test-Truthy (Get-JsonValue $Validation "manualTuneObserverReportReady")
    }
    else {
        $true
    }
    $manualTuneObserverStatus = [string](Get-JsonValue $Validation "manualTuneObserverReportStatus")
    if ([string]::IsNullOrWhiteSpace($manualTuneObserverStatus)) {
        $manualTuneObserverStatus = if ($manualTuneObserverPresent) { "not-ready" } else { "not-present" }
    }
    $manualTuneObserverBestFrequencyHz = [string](Get-JsonValue $Validation "manualTuneObserverBestFrequencyHz")
    $manualTuneObserverBestStatus = [string](Get-JsonValue $Validation "manualTuneObserverBestStatus")
    $manualTuneObserverBestText = if ([string]::IsNullOrWhiteSpace($manualTuneObserverBestFrequencyHz)) {
        "none"
    }
    else {
        "$manualTuneObserverBestFrequencyHz Hz status=$manualTuneObserverBestStatus"
    }
    $manualTuneObserverObservedVfoCount = Get-IntegerValueOrDefault (Get-JsonValue $Validation "manualTuneObserverObservedVfoCount")
    $manualTuneObserverBestObservedVfoHz = Get-JsonValue $Validation "manualTuneObserverBestObservedVfoHz"
    $manualTuneObserverBestObservedVfoStatus = [string](Get-JsonValue $Validation "manualTuneObserverBestObservedVfoStatus")
    $manualTuneObserverBestObservedVfoScore = Get-JsonValue $Validation "manualTuneObserverBestObservedVfoScore"
    $manualTuneObserverTuningHintCount = Get-IntegerValueOrDefault (Get-JsonValue $Validation "manualTuneObserverFrontendTuningHintPollCount")
    $manualTuneObserverSuggestedShift = Get-JsonValue $Validation "manualTuneObserverFrontendSuggestedDialShiftHz"
    $manualTuneObserverSuggestedVfoMhz = Get-JsonValue $Validation "manualTuneObserverFrontendSuggestedVfoMhz"
    $manualTuneObserverSuggestedReason = [string](Get-JsonValue $Validation "manualTuneObserverFrontendSuggestedTuneReason")
    $manualTuneObserverSuggestedDistance = Get-JsonValue $Validation "manualTuneObserverFrontendSuggestedFilterDistanceHz"
    $manualTuneObserverHintText = if ($manualTuneObserverTuningHintCount -le 0) {
        "none"
    }
    else {
        "polls=$manualTuneObserverTuningHintCount shiftHz=$manualTuneObserverSuggestedShift vfoMhz=$manualTuneObserverSuggestedVfoMhz reason=$manualTuneObserverSuggestedReason distanceHz=$manualTuneObserverSuggestedDistance"
    }
    $manualTuneObserverReferencedCaptureCount = Get-IntegerValueOrDefault (Get-JsonValue $Validation "manualTuneObserverReferencedCaptureCount")
    $manualTuneObserverReferencedCaptureReadyCount = Get-IntegerValueOrDefault (Get-JsonValue $Validation "manualTuneObserverReferencedCaptureReadyCount")
    $manualTuneObserverReferencedCaptureProblemCount = Get-IntegerValueOrDefault (Get-JsonValue $Validation "manualTuneObserverReferencedCaptureProblemCount")
    $manualTuneObserverMixedStatusCounts = @(Get-JsonArray $Validation "manualTuneObserverMixedWeakStrongEvidenceStatusCounts")
    $manualTuneObserverMixedStatusText = if ($manualTuneObserverMixedStatusCounts.Count -le 0) {
        "none"
    }
    else {
        ($manualTuneObserverMixedStatusCounts | ForEach-Object {
                "$([string](Get-JsonValue $_ "status"))=$(Get-JsonValue $_ "count")"
            }) -join ","
    }
    $manualTuneObserverWeakOnlyCount = Get-IntegerValueOrDefault (Get-JsonValue $Validation "manualTuneObserverWeakOnlyCaptureCount")
    $manualTuneObserverReadyWeakOnlyCount = Get-IntegerValueOrDefault (Get-JsonValue $Validation "manualTuneObserverReadyWeakOnlyCaptureCount")
    $manualTuneObserverNearStrongCandidateCount = Get-IntegerValueOrDefault (Get-JsonValue $Validation "manualTuneObserverNearStrongPromotionCandidateCaptureCount")
    $manualTuneObserverBestNearStrongCandidateVfoHz = Get-JsonValue $Validation "manualTuneObserverBestNearStrongPromotionCandidateVfoHz"
    $manualTuneObserverBestNearStrongCandidateDistanceDb = Get-JsonValue $Validation "manualTuneObserverBestNearStrongPromotionCandidateDistanceToStrongThresholdDb"
    $manualTuneObserverBestNearStrongCandidateReportPath = [string](Get-JsonValue $Validation "manualTuneObserverBestNearStrongPromotionCandidateReportPath")
    $gates.Add((New-EvidenceGateRecord `
                -GateId "manual-tune-observer" `
                -Name "Manual tune observer evidence" `
                -Ready:$manualTuneObserverReady `
                -Status $manualTuneObserverStatus `
                -RequiredForAcceptance:$false `
                -Detail "present=$manualTuneObserverPresent; ok=$(Get-JsonValue $Validation "manualTuneObserverOk"); scanError=$(Get-JsonValue $Validation "manualTuneObserverScanError"); baseUrl=$(Get-JsonValue $Validation "manualTuneObserverBaseUrl"); bundleRelativePaths=$(Get-JsonValue $Validation "manualTuneObserverBundleRelativePaths"); scenario=$(Get-JsonValue $Validation "manualTuneObserverScenarioId"); comparison=$(Get-JsonValue $Validation "manualTuneObserverComparisonId"); readOnly=$(Get-JsonValue $Validation "manualTuneObserverSafetyReadOnly"); apiWrites=$(Get-JsonValue $Validation "manualTuneObserverSafetyApiWrites"); retune=$(Get-JsonValue $Validation "manualTuneObserverSafetyRetune"); vfoWrites=$(Get-JsonValue $Validation "manualTuneObserverSafetyVfoWriteAttemptCount"); radioLoWrites=$(Get-JsonValue $Validation "manualTuneObserverSafetyRadioLoWriteAttemptCount"); txTouched=$(Get-JsonValue $Validation "manualTuneObserverSafetyTxEndpointsTouched"); polls=$(Get-JsonValue $Validation "manualTuneObserverPollSampleCount")/$(Get-JsonValue $Validation "manualTuneObserverPollCount"); observedVfos=$manualTuneObserverObservedVfoCount bestObserved=$manualTuneObserverBestObservedVfoHz/$manualTuneObserverBestObservedVfoStatus score=$manualTuneObserverBestObservedVfoScore; captures=$(Get-JsonValue $Validation "manualTuneObserverReadyCaptureCount")/$(Get-JsonValue $Validation "manualTuneObserverCaptureCount"); uniqueVfos=$(Get-JsonValue $Validation "manualTuneObserverUniqueCapturedVfoCount"); recapturedVfos=$(Get-JsonValue $Validation "manualTuneObserverRecapturedVfoCount"); maxPerVfo=$(Get-JsonValue $Validation "manualTuneObserverMaxCapturesPerVfo"); allowStaleScene=$(Get-JsonValue $Validation "manualTuneObserverAllowStaleSceneCapture"); staleScenePolls=$(Get-JsonValue $Validation "manualTuneObserverStaleScenePollCount"); staleSceneCaptures=$(Get-JsonValue $Validation "manualTuneObserverStaleSceneCaptureCount"); frontendNear/filter/off/mismatch=$(Get-JsonValue $Validation "manualTuneObserverFrontendNearPassbandPollCount")/$(Get-JsonValue $Validation "manualTuneObserverFrontendFilterPassbandPollCount")/$(Get-JsonValue $Validation "manualTuneObserverFrontendFilterOffPassbandPollCount")/$(Get-JsonValue $Validation "manualTuneObserverFrontendOffsetMismatchPollCount"); tuningHint=$manualTuneObserverHintText; captureQualifiedPolls=$(Get-JsonValue $Validation "manualTuneObserverCaptureQualifiedPollCount"); referencedCaptures=$manualTuneObserverReferencedCaptureReadyCount/$manualTuneObserverReferencedCaptureCount; referencedProblems=$manualTuneObserverReferencedCaptureProblemCount; mixedReady=$(Get-JsonValue $Validation "manualTuneObserverMixedWeakStrongReady"); weakSamples=$(Get-JsonValue $Validation "manualTuneObserverWeakInputSampleCount"); strongSamples=$(Get-JsonValue $Validation "manualTuneObserverStrongInputSampleCount"); nearStrongSamples=$(Get-JsonValue $Validation "manualTuneObserverNearStrongInputSampleCount"); weakOnly=$manualTuneObserverReadyWeakOnlyCount/$manualTuneObserverWeakOnlyCount; nearStrongCandidates=$manualTuneObserverNearStrongCandidateCount bestNearStrong=$manualTuneObserverBestNearStrongCandidateVfoHz distanceToStrongDb=$manualTuneObserverBestNearStrongCandidateDistanceDb report=$manualTuneObserverBestNearStrongCandidateReportPath; statusCounts=$manualTuneObserverMixedStatusText; speechWeakStrong=$(Get-JsonValue $Validation "manualTuneObserverSpeechQualifiedWeakInputSampleCount")/$(Get-JsonValue $Validation "manualTuneObserverSpeechQualifiedStrongInputSampleCount"); passbandWeakStrong=$(Get-JsonValue $Validation "manualTuneObserverPassbandQualifiedWeakInputSampleCount")/$(Get-JsonValue $Validation "manualTuneObserverPassbandQualifiedStrongInputSampleCount"); agcPumpingCaptures=$(Get-JsonValue $Validation "manualTuneObserverAgcPumpingRiskCaptureCount"); best=$manualTuneObserverBestText" `
                -Remediation "Use watch-dsp-manual-tune-observer.ps1 when the operator is tuning manually; if present evidence is invalid, resolve read-only/no-retune safety, missing portable watcher files, missing ready captures, or AGC pumping before promoting a window.")) | Out-Null

    $g2RxPeakHuntPresent = Test-Truthy (Get-JsonValue $Validation "g2RxPeakHuntReportPresent")
    $g2RxPeakHuntReady = if ($g2RxPeakHuntPresent) {
        Test-Truthy (Get-JsonValue $Validation "g2RxPeakHuntReportReady")
    }
    else {
        $true
    }
    $g2RxPeakHuntStatus = [string](Get-JsonValue $Validation "g2RxPeakHuntReportStatus")
    if ([string]::IsNullOrWhiteSpace($g2RxPeakHuntStatus)) {
        $g2RxPeakHuntStatus = if ($g2RxPeakHuntPresent) { "not-ready" } else { "not-present" }
    }
    $g2RxPeakHuntBestFrequencyHz = [string](Get-JsonValue $Validation "g2RxPeakHuntBestFrequencyHz")
    $g2RxPeakHuntBestStatus = [string](Get-JsonValue $Validation "g2RxPeakHuntBestStatus")
    $g2RxPeakHuntBestText = if ([string]::IsNullOrWhiteSpace($g2RxPeakHuntBestFrequencyHz)) {
        "none"
    }
    else {
        "$g2RxPeakHuntBestFrequencyHz Hz score=$(Get-JsonValue $Validation "g2RxPeakHuntBestScore") status=$g2RxPeakHuntBestStatus"
    }
    $g2RxPeakHuntReferencedWindowCount = Get-IntegerValueOrDefault (Get-JsonValue $Validation "g2RxPeakHuntReferencedWindowCount")
    $g2RxPeakHuntReferencedWindowReadyCount = Get-IntegerValueOrDefault (Get-JsonValue $Validation "g2RxPeakHuntReferencedWindowReadyCount")
    $g2RxPeakHuntReferencedWindowProblemCount = Get-IntegerValueOrDefault (Get-JsonValue $Validation "g2RxPeakHuntReferencedWindowProblemCount")
    $g2RxPeakHuntBaseUrl = [string](Get-JsonValue $Validation "g2RxPeakHuntBaseUrl")
    $g2RxPeakHuntRequestedBaseUrl = [string](Get-JsonValue $Validation "g2RxPeakHuntRequestedBaseUrl")
    $g2RxPeakHuntAutoPhoneCluster = Test-Truthy (Get-JsonValue $Validation "g2RxPeakHuntAutoPhoneCluster")
    $g2RxPeakHuntAutoPhoneClusterCandidateCount = Get-IntegerValueOrDefault (Get-JsonValue $Validation "g2RxPeakHuntAutoPhoneClusterCandidateCount")
    $g2RxPeakHuntAutoPhoneClusterExactCandidateCount = Get-IntegerValueOrDefault (Get-JsonValue $Validation "g2RxPeakHuntAutoPhoneClusterExactCandidateCount")
    $g2RxPeakHuntAutoPhoneClusterNeighborCandidateCount = Get-IntegerValueOrDefault (Get-JsonValue $Validation "g2RxPeakHuntAutoPhoneClusterNeighborCandidateCount")
    $g2RxPeakHuntAutoPhoneClusterBandLowHz = Get-JsonValue $Validation "g2RxPeakHuntAutoPhoneClusterBandLowHz"
    $g2RxPeakHuntAutoPhoneClusterBandHighHz = Get-JsonValue $Validation "g2RxPeakHuntAutoPhoneClusterBandHighHz"
    $gates.Add((New-EvidenceGateRecord `
                -GateId "g2-rx-peak-hunt" `
                -Name "G2 RX peak-hunt evidence" `
                -Ready:$g2RxPeakHuntReady `
                -Status $g2RxPeakHuntStatus `
                -RequiredForAcceptance:$false `
                -Detail "present=$g2RxPeakHuntPresent; ok=$(Get-JsonValue $Validation "g2RxPeakHuntOk"); scanError=$(Get-JsonValue $Validation "g2RxPeakHuntScanError"); requestedBaseUrl=$g2RxPeakHuntRequestedBaseUrl; baseUrl=$g2RxPeakHuntBaseUrl; autoBaseDiscovered=$(Get-JsonValue $Validation "g2RxPeakHuntBaseUrlAutoDiscovered"); autoPhoneCluster=$g2RxPeakHuntAutoPhoneCluster; autoPhoneClusterCandidates=$g2RxPeakHuntAutoPhoneClusterCandidateCount; autoPhoneClusterExactCandidates=$g2RxPeakHuntAutoPhoneClusterExactCandidateCount; autoPhoneClusterNeighborCandidates=$g2RxPeakHuntAutoPhoneClusterNeighborCandidateCount; autoPhoneClusterBandHz=$g2RxPeakHuntAutoPhoneClusterBandLowHz-$g2RxPeakHuntAutoPhoneClusterBandHighHz; allowRetune=$(Get-JsonValue $Validation "g2RxPeakHuntAllowRetune"); passes=$(Get-JsonValue $Validation "g2RxPeakHuntCompletedPassCount")/$(Get-JsonValue $Validation "g2RxPeakHuntPassCount"); operatorCandidates=$(Get-JsonValue $Validation "g2RxPeakHuntOperatorCandidateCount"); runs=$(Get-JsonValue $Validation "g2RxPeakHuntActualRunCount"); failedRuns=$(Get-JsonValue $Validation "g2RxPeakHuntFailedRunCount"); referencedWindows=$g2RxPeakHuntReferencedWindowReadyCount/$g2RxPeakHuntReferencedWindowCount; referencedProblems=$g2RxPeakHuntReferencedWindowProblemCount; mixedReady=$(Get-JsonValue $Validation "g2RxPeakHuntMixedWeakStrongReady"); weakSamples=$(Get-JsonValue $Validation "g2RxPeakHuntWeakInputSampleCount"); strongSamples=$(Get-JsonValue $Validation "g2RxPeakHuntStrongInputSampleCount"); nearStrongSamples=$(Get-JsonValue $Validation "g2RxPeakHuntNearStrongInputSampleCount"); speechWeakStrongNear=$(Get-JsonValue $Validation "g2RxPeakHuntSpeechQualifiedWeakInputSampleCount")/$(Get-JsonValue $Validation "g2RxPeakHuntSpeechQualifiedStrongInputSampleCount")/$(Get-JsonValue $Validation "g2RxPeakHuntSpeechQualifiedNearStrongInputSampleCount"); passbandWeakStrongNear=$(Get-JsonValue $Validation "g2RxPeakHuntPassbandQualifiedWeakInputSampleCount")/$(Get-JsonValue $Validation "g2RxPeakHuntPassbandQualifiedStrongInputSampleCount")/$(Get-JsonValue $Validation "g2RxPeakHuntPassbandQualifiedNearStrongInputSampleCount"); nearPassband=$(Get-JsonValue $Validation "g2RxPeakHuntFrontendNearPassbandSampleCount"); candidateWeakLoss=$(Get-JsonValue $Validation "g2RxPeakHuntCandidateWeakLossSampleCount"); hotMakeup=$(Get-JsonValue $Validation "g2RxPeakHuntHotMakeupSampleCount"); hardBlockers=$(Get-JsonValue $Validation "g2RxPeakHuntHardBlockerSampleCount"); agcPumpingRuns=$(Get-JsonValue $Validation "g2RxPeakHuntAgcPumpingRiskRunCount"); vfoRestored=$(Get-JsonValue $Validation "g2RxPeakHuntSafetyOriginalVfoRestored"); radioLoRestored=$(Get-JsonValue $Validation "g2RxPeakHuntSafetyOriginalRadioLoRestored"); best=$g2RxPeakHuntBestText" `
                -Remediation "Use run-dsp-g2-rx-peak-hunt.ps1 as RX-only scouting evidence before mixed weak+strong live-history recapture; if present evidence is invalid, resolve RX-only safety, VFO restore, hard blockers, or AGC pumping before promoting a window.")) | Out-Null

    $liveMatrixArtifactControlStatus = [string](Get-JsonValue $Validation "liveMatrixArtifactControlStatus")
    if ([string]::IsNullOrWhiteSpace($liveMatrixArtifactControlStatus)) {
        $liveMatrixArtifactControlStatus = "not-evaluated"
    }
    $liveMatrixArtifactControlReady = [string]::Equals($liveMatrixArtifactControlStatus, "clear", [StringComparison]::OrdinalIgnoreCase)
    $gates.Add((New-EvidenceGateRecord `
                -GateId "live-matrix-artifact-control" `
                -Name "Live matrix artifact-control advisory" `
                -Ready:$liveMatrixArtifactControlReady `
                -Status $liveMatrixArtifactControlStatus `
                -RequiredForAcceptance:$false `
                -Detail "reports=$(Get-JsonValue $Validation "liveMatrixArtifactControlReportCount"); schemaV3Reports=$(Get-JsonValue $Validation "liveMatrixArtifactControlSchemaV3ReportCount"); reviewRuns=$(Get-JsonValue $Validation "liveMatrixArtifactControlReviewRunCount"); riskScoreMax=$(Get-JsonValue $Validation "liveMatrixArtifactControlRiskScoreMax"); lowEvidenceLiftedSamples=$(Get-JsonValue $Validation "liveMatrixArtifactControlLowEvidenceLiftedSampleCount"); lowEvidenceLiftedPctMax=$(Get-JsonValue $Validation "liveMatrixArtifactControlLowEvidenceLiftedPctMax"); audioAlignmentMismatchPctMax=$(Get-JsonValue $Validation "liveMatrixArtifactControlAudioAlignmentMismatchPctMax")" `
                -Remediation "Prefer matrix windows with artifact-control status clear; if review runs exist, inspect low-evidence lift, audio alignment, and texture fill before promoting a window into live history.")) | Out-Null

    $liveHistoryPresent = Test-Truthy (Get-JsonValue $Validation "liveDiagnosticsHistoryPresent")
    $liveHistoryReady = Test-Truthy (Get-JsonValue $Validation "liveDiagnosticsHistoryReady")
    $liveHistoryCoverageStatus = [string](Get-JsonValue $Validation "liveDiagnosticsHistoryLiveExperimentCoverageStatus")
    $liveHistoryCoverageMissingCount = [int](Get-JsonValue $Validation "liveDiagnosticsHistoryLiveExperimentCoverageMissingComparisonCount")
    $liveHistoryCoverageMissingIds = @(Get-JsonArray $Validation "liveDiagnosticsHistoryLiveExperimentCoverageMissingComparisonIds")
    $liveHistoryAgcStatus = [string](Get-JsonValue $Validation "liveDiagnosticsHistoryAgcStabilityStatus")
    $liveHistoryAgcMissingCount = Get-IntegerValueOrDefault (Get-JsonValue $Validation "liveDiagnosticsHistoryAgcStabilityMissingTraceCount")
    $liveHistoryAgcPumpingRiskTraceCount = Get-IntegerValueOrDefault (Get-JsonValue $Validation "liveDiagnosticsHistoryAgcPumpingRiskTraceCount")
    $liveHistoryArtifactControlSignalCount = Get-IntegerValueOrDefault (Get-JsonValue $Validation "liveDiagnosticsHistoryArtifactControlSignalCount")
    $liveHistoryMixedWeakStrongReady = Test-Truthy (Get-JsonValue $Validation "liveDiagnosticsHistoryMixedWeakStrongEvidenceReady")
    $liveHistoryMixedWeakStrongStatus = [string](Get-JsonValue $Validation "liveDiagnosticsHistoryMixedWeakStrongEvidenceStatus")
    $liveHistoryMixedWeakStrongTraceCount = Get-IntegerValueOrDefault (Get-JsonValue $Validation "liveDiagnosticsHistoryMixedWeakStrongTraceCount")
    $liveHistoryMixedWeakStrongReadyTraceCount = Get-IntegerValueOrDefault (Get-JsonValue $Validation "liveDiagnosticsHistoryMixedWeakStrongReadyTraceCount")
    $liveHistoryMixedWeakStrongMissingTraceCount = Get-IntegerValueOrDefault (Get-JsonValue $Validation "liveDiagnosticsHistoryMixedWeakStrongMissingTraceCount")
    $liveHistoryMixedWeakStrongGapWatchTraceCount = Get-IntegerValueOrDefault (Get-JsonValue $Validation "liveDiagnosticsHistoryMixedWeakStrongGapWatchTraceCount")
    $liveHistoryCoverageReady = (-not $liveHistoryPresent -or (
            [string]::Equals($liveHistoryCoverageStatus, "complete", [StringComparison]::OrdinalIgnoreCase) -and
            $liveHistoryCoverageMissingCount -eq 0))
    $liveHistorySourceReady = ($LiveTraceSourceStatus -eq "hash-ready" -or (-not $liveHistoryPresent -and [string]::IsNullOrWhiteSpace($LiveTraceSourceStatus)))
    $liveHistoryGateReady = if (-not $liveHistoryPresent -and [string]::IsNullOrWhiteSpace($LiveTraceSourceStatus)) {
        $true
    }
    else {
        ($liveHistoryReady -and $liveHistorySourceReady -and $liveHistoryCoverageReady -and $LiveSourceProblemCount -eq 0)
    }
    $liveHistoryGateStatus = if (-not $liveHistoryPresent -and [string]::IsNullOrWhiteSpace($LiveTraceSourceStatus)) {
        "not-present"
    }
    elseif (-not $liveHistoryCoverageReady) {
        if ([string]::IsNullOrWhiteSpace($liveHistoryCoverageStatus)) {
            "coverage-missing"
        }
        else {
            "coverage-$liveHistoryCoverageStatus"
        }
    }
    elseif ([string]::Equals($liveHistoryAgcStatus, "agc-stability-missing", [StringComparison]::OrdinalIgnoreCase)) {
        "agc-stability-missing"
    }
    elseif (-not $liveHistoryReady) {
        "not-ready"
    }
    else {
        $LiveTraceSourceStatus
    }
    $liveHistoryCoverageMissingText = ($liveHistoryCoverageMissingIds | Select-Object -First 8) -join ", "
    if ([string]::IsNullOrWhiteSpace($liveHistoryCoverageMissingText)) {
        $liveHistoryCoverageMissingText = "none-listed"
    }
    $gates.Add((New-EvidenceGateRecord `
                -GateId "live-history-provenance" `
                -Name "Live diagnostics history provenance" `
                -Ready:$liveHistoryGateReady `
                -Status $liveHistoryGateStatus `
                -RequiredForAcceptance:$false `
                -Detail "present=$liveHistoryPresent; ready=$liveHistoryReady; traceSources=$(Get-JsonValue $Validation "liveDiagnosticsHistoryTraceSourceCheckedCount"); sourceProblems=$LiveSourceProblemCount; coverageStatus=$liveHistoryCoverageStatus; missingCoverage=$liveHistoryCoverageMissingCount; missingCoverageIds=$liveHistoryCoverageMissingText; agcStabilityStatus=$liveHistoryAgcStatus; agcMissingTraces=$liveHistoryAgcMissingCount; agcPumpingRiskTraces=$liveHistoryAgcPumpingRiskTraceCount; artifactControlSignals=$liveHistoryArtifactControlSignalCount; mixedWeakStrongStatus=$liveHistoryMixedWeakStrongStatus; mixedTraces=$liveHistoryMixedWeakStrongTraceCount; mixedReadyTraces=$liveHistoryMixedWeakStrongReadyTraceCount; mixedMissingTraces=$liveHistoryMixedWeakStrongMissingTraceCount; mixedGapWatchTraces=$liveHistoryMixedWeakStrongGapWatchTraceCount" `
                -Remediation "Regenerate live diagnostics history after watcher summaries and JSONL traces are finalized; capture complete off-baseline, thetis-parity, current-zeus, and nr5-spnr coverage with AGC stability plus mixed weak/strong evidence before using history for tuning decisions.")) | Out-Null

    $mixedWeakStrongGateStatus = if ($liveHistoryMixedWeakStrongReady) {
        "ready"
    }
    elseif ([string]::IsNullOrWhiteSpace($liveHistoryMixedWeakStrongStatus)) {
        "not-evaluated"
    }
    else {
        $liveHistoryMixedWeakStrongStatus
    }
    $gates.Add((New-EvidenceGateRecord `
                -GateId "live-history-mixed-weak-strong" `
                -Name "Live history mixed weak/strong evidence" `
                -Ready:$liveHistoryMixedWeakStrongReady `
                -Status $mixedWeakStrongGateStatus `
                -Detail "status=$liveHistoryMixedWeakStrongStatus; mixedTraces=$liveHistoryMixedWeakStrongTraceCount; readyTraces=$liveHistoryMixedWeakStrongReadyTraceCount; missingTraces=$liveHistoryMixedWeakStrongMissingTraceCount; gapWatchTraces=$liveHistoryMixedWeakStrongGapWatchTraceCount" `
                -Remediation "Capture at least one G2 NR5 live trace that contains both weak and strong speech samples with weak/strong output gap within the v14 parity threshold before claiming acceptance-ready volume normalization.")) | Out-Null

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
    $crossRadioStatus = [string](Get-JsonValue $Validation "crossRadioValidationEvidenceStatus")
    if ([string]::IsNullOrWhiteSpace($crossRadioStatus)) {
        $crossRadioStatus = "not-captured"
    }
    $crossRadioReady = Test-Truthy (Get-JsonValue $Validation "crossRadioValidationReady")
    $crossRadioTargets = (@(Get-JsonArray $Validation "crossRadioValidationNonG2TargetIds") | ForEach-Object { [string]$_ }) -join ", "

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

    $buildOutPrerequisiteGateIds = New-Object System.Collections.Generic.List[string]
    foreach ($gateId in @(
            "benchmark-plan-coverage",
            "benchmark-metric-catalog",
            "external-engine-candidates",
            "wdsp-source-drift-report",
            "wdsp-native-symbol-audit",
            "wdsp-runtime-artifact-audit")) {
        $buildOutPrerequisiteGateIds.Add($gateId) | Out-Null
    }
    $externalBakeoffGate = Get-EvidenceGateById -Gates $EvidenceGates -GateId "external-engine-bakeoff"
    if ($null -ne $externalBakeoffGate -and (Test-Truthy (Get-JsonValue $externalBakeoffGate "requiredForAcceptance"))) {
        $buildOutPrerequisiteGateIds.Add("external-engine-bakeoff") | Out-Null
    }
    $buildOutBlockingGateIds = New-Object System.Collections.Generic.List[string]
    foreach ($gateId in @($buildOutPrerequisiteGateIds.ToArray())) {
        if (-not (Test-EvidenceGateReady -Gates $EvidenceGates -GateId $gateId)) {
            $buildOutBlockingGateIds.Add($gateId) | Out-Null
        }
    }
    $buildOutReady = ($buildOutBlockingGateIds.Count -eq 0)
    $buildOutStatus = if ($buildOutReady) { "ready-for-opt-in-buildout" } else { "blocked-buildout-prerequisites" }
    $stages.Add((New-AcceptanceReadinessRecord `
                -StageId "opt-in-dsp-buildout-prerequisites" `
                -Name "Opt-in DSP build-out prerequisites" `
                -Ready:$buildOutReady `
                -Status $buildOutStatus `
                -BlocksDefaultChange:$false `
                -Detail "requiredGates=$((@($buildOutPrerequisiteGateIds.ToArray())) -join ', '); blockingGates=$((@($buildOutBlockingGateIds.ToArray())) -join ', ')" `
                -NextAction $(if ($buildOutReady) { "Start opt-in DSP implementation or bakeoff work behind explicit controls; keep defaults unchanged and gather G2 evidence before review." } else { "Complete benchmark plan/catalog, WDSP source drift classification, WDSP native/runtime audit, and external-candidate safety gates before starting new opt-in DSP build-out work." }) `
                -BlockingGateIds @($buildOutBlockingGateIds.ToArray()))) | Out-Null

    $g2Ready = ($triageReady `
            -and (Test-EvidenceGateReady -Gates $EvidenceGates -GateId "g2-hardware") `
            -and (Test-EvidenceGateReady -Gates $EvidenceGates -GateId "wdsp-native-symbol-audit") `
            -and (Test-EvidenceGateReady -Gates $EvidenceGates -GateId "wdsp-runtime-artifact-audit") `
            -and (Test-EvidenceGateReady -Gates $EvidenceGates -GateId "benchmark-metric-catalog") `
            -and (Test-EvidenceGateReady -Gates $EvidenceGates -GateId "fixture-metric-comparison") `
            -and (Test-EvidenceGateReady -Gates $EvidenceGates -GateId "live-trace-comparison") `
            -and (Test-EvidenceGateReady -Gates $EvidenceGates -GateId "thetis-parity-live-comparison"))
    $g2Status = if ($g2Ready) { "ready-for-g2-review" } else { "blocked-prerequisites" }
    $stages.Add((New-AcceptanceReadinessRecord `
                -StageId "g2-first-pass-evidence" `
                -Name "G2 first-pass evidence" `
                -Ready:$g2Ready `
                -Status $g2Status `
                -BlocksDefaultChange:$true `
                -Detail "hardware=$(Get-JsonValue $Validation "hardwareEvidenceStatus"); fixtureReady=$(Get-JsonValue $Validation "metricComparisonReady"); zeusLiveTraceReady=$(Get-JsonValue $Validation "liveTraceComparisonReady"); thetisLiveTraceReady=$(Get-JsonValue $Validation "liveTraceThetisComparisonReady")" `
                -NextAction $(if ($g2Ready) { "Use this as first-cycle G2 review evidence, then capture non-G2 cross-radio evidence before graduation." } else { "Complete G2 hardware, WDSP audit, fixture, current-Zeus live-trace, and Thetis-parity live-trace gates before first-pass review." }) `
                -BlockingGateIds $requiredBlockingGateIds)) | Out-Null

    $candidateComparisonReady = ($g2Ready -and
        (Test-Truthy (Get-JsonValue $Validation "metricComparisonReady")) -and
        (Test-Truthy (Get-JsonValue $Validation "liveTraceComparisonReady")) -and
        (Test-Truthy (Get-JsonValue $Validation "liveTraceThetisComparisonReady")))
    $candidateStatus = if ($candidateComparisonReady) { "ready-for-opt-in-candidate-review" } else { "blocked-benchmark-evidence" }
    $candidateTopCaptureConstraint = Get-TopCountText `
        -Items @(Get-JsonArray $Validation "liveTraceComparisonCaptureReadinessCandidateTopConstraintCounts") `
        -NameField "constraint"
    $candidateTopCaptureHardGate = Get-TopCountText `
        -Items @(Get-JsonArray $Validation "liveTraceComparisonCaptureReadinessCandidateTopHardConstraintCounts") `
        -NameField "constraint"
    $stages.Add((New-AcceptanceReadinessRecord `
                -StageId "opt-in-candidate-comparison" `
                -Name "Opt-in candidate comparison" `
                -Ready:$candidateComparisonReady `
                -Status $candidateStatus `
                -BlocksDefaultChange:$true `
                -Detail "metricRegressions=$(Get-JsonValue $Validation "metricComparisonRegressionCount"); zeusLiveRegressions=$(Get-JsonValue $Validation "liveTraceComparisonRegressionCount"); zeusLiveGateFailures=$(Get-JsonValue $Validation "liveTraceComparisonGateFailureCount"); thetisLiveRegressions=$(Get-JsonValue $Validation "liveTraceThetisComparisonRegressionCount"); thetisLiveGateFailures=$(Get-JsonValue $Validation "liveTraceThetisComparisonGateFailureCount"); captureHardGateFails=$(Get-JsonValue $Validation "liveTraceComparisonCaptureReadinessCandidateHardGateFailCount"); captureStrictPreflightFails=$(Get-JsonValue $Validation "liveTraceComparisonCaptureReadinessCandidateStrictPreflightFailCount"); topCaptureConstraint=$candidateTopCaptureConstraint; topCaptureHardGate=$candidateTopCaptureHardGate; levelerConstrainedRegressions=$(Get-JsonValue $Validation "liveTraceComparisonRxAudioLevelerConstrainedRegressionCount"); historyCandidatePromotionReady=$(Get-JsonValue $Validation "liveDiagnosticsHistoryCandidatePromotionReady")" `
                -NextAction $(if ($candidateComparisonReady) { "Keep the candidate opt-in and review objective metrics plus operator notes; do not change defaults." } else { "Clear capture-readiness hard gates, then generate fixture and live trace comparisons that beat current Zeus and Thetis evidence before opt-in review." }) `
                -BlockingGateIds $requiredBlockingGateIds)) | Out-Null

    $externalRequired = Test-Truthy (Get-JsonValue $Validation "externalEngineBakeoffRequiredByScope")
    $externalPresent = Test-Truthy (Get-JsonValue $Validation "externalEngineBakeoffReportPresent")
    $externalReady = Test-Truthy (Get-JsonValue $Validation "externalEngineBakeoffReady")
    $externalStageReady = ($externalReady -or (-not $externalRequired -and -not $externalPresent))
    $externalStatus = if (-not $externalRequired -and -not $externalPresent) { "not-in-scope" } elseif ($externalReady) { "ready-for-bakeoff-review" } else { "blocked-bakeoff-evidence" }
    $externalMissingIds = Join-ExternalCandidateIds @(Get-JsonArray $Validation "externalEngineBakeoffMissingCandidateIds")
    if ([string]::IsNullOrWhiteSpace($externalMissingIds)) {
        $externalMissingIds = Join-ExternalCandidateIds @(Get-JsonArray $Validation "externalEngineCandidateMissingIds")
    }
    $externalUnsafeIds = Join-ExternalCandidateIds @(Get-JsonArray $Validation "externalEngineBakeoffUnsafeCandidateIds")
    if ([string]::IsNullOrWhiteSpace($externalUnsafeIds)) {
        $externalUnsafeIds = Join-ExternalCandidateIds @(Get-JsonArray $Validation "externalEngineCandidateUnsafeIds")
    }
    $externalBlockedIds = Join-ExternalCandidateIds @(Get-JsonArray $Validation "externalEngineBakeoffBlockedCandidateIds")
    $externalTopIssue = Get-ExternalIssueTopText @(Get-JsonArray $Validation "externalEngineBakeoffCandidateIssueCounts")
    $externalScopeTriggers = (@(Get-JsonArray $Validation "externalEngineBakeoffScopeTriggers") | ForEach-Object { [string]$_ }) -join ", "
    $externalFirstSafeCandidateId = [string](Get-JsonValue $Validation "externalEngineBakeoffFirstSafeCandidateId")
    $externalFirstSafeScenarioIds = (@(Get-JsonArray $Validation "externalEngineBakeoffFirstSafeScenarioIds") | ForEach-Object { [string]$_ }) -join ", "
    $externalEvaluationOrderIds = Join-ExternalCandidateIds @(Get-JsonArray $Validation "externalEngineBakeoffEvaluationOrderCandidateIds")
    if ([string]::IsNullOrWhiteSpace($externalTopIssue)) {
        $externalTopIssue = Get-ExternalIssueTopText @(Get-JsonArray $Validation "externalEngineCandidateIssueCounts")
    }
    $externalNextAction = "No external-engine action is required unless an opt-in comparison is in scope."
    if (-not $externalStageReady) {
        if (-not [string]::IsNullOrWhiteSpace($externalMissingIds)) {
            $externalNextAction = "Add or regenerate required external-engine candidate entries: $externalMissingIds."
        }
        elseif (-not [string]::IsNullOrWhiteSpace($externalUnsafeIds)) {
            $externalNextAction = "Fix unsafe external-engine candidate entries before bakeoff: $externalUnsafeIds."
            if (-not [string]::IsNullOrWhiteSpace($externalTopIssue)) {
                $externalNextAction = "$externalNextAction Top issue: $externalTopIssue."
            }
        }
        elseif (-not $externalPresent) {
            $externalNextAction = "Generate and validate the external-engine bakeoff report before external DSP/ML review."
        }
        else {
            $externalNextAction = "Regenerate and validate the external-engine bakeoff report; blocked candidates remain integration-blocked: $externalBlockedIds."
        }
    }
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
                -Detail "requiredByScope=$externalRequired; scopeTriggers=$externalScopeTriggers; present=$externalPresent; ready=$externalReady; firstSafe=$externalFirstSafeCandidateId; firstSafeScenarios=$externalFirstSafeScenarioIds; evaluationOrder=$externalEvaluationOrderIds; missingIds=$externalMissingIds; unsafeIds=$externalUnsafeIds; blockedIds=$externalBlockedIds; topIssue=$externalTopIssue; defaultBehaviorChangeReady=$(Get-JsonValue $Validation "externalEngineBakeoffPlanDefaultBehaviorChangeReady"); rawIqReplacementAllowed=$(Get-JsonValue $Validation "externalEngineBakeoffPlanRawWdspIqReplacementAllowed")" `
                -NextAction $externalNextAction `
                -BlockingGateIds $externalBlockingGateIds)) | Out-Null

    $defaultGraduationStatus = if (-not $g2Ready) {
        "blocked-g2-prerequisites"
    }
    elseif (-not $crossRadioReady) {
        "blocked-cross-radio-validation"
    }
    else {
        "blocked-explicit-default-approval"
    }
    $defaultGraduationNextAction = if (-not $g2Ready) {
        "Finish G2 first-pass evidence before planning cross-radio/default-graduation review."
    }
    elseif (-not $crossRadioReady) {
        "Capture and validate non-G2 cross-radio evidence before any default behavior change approval."
    }
    else {
        "Review G2 and cross-radio evidence with explicit approval before any default behavior change."
    }
    $stages.Add((New-AcceptanceReadinessRecord `
                -StageId "default-dsp-graduation" `
                -Name "Default DSP behavior graduation" `
                -Ready:$false `
                -Status $defaultGraduationStatus `
                -BlocksDefaultChange:$true `
                -Detail "defaultBehaviorChangeReady=False; crossRadioValidationRequired=True; crossRadioValidationEvidenceStatus=$crossRadioStatus; crossRadioValidationReady=$crossRadioReady; crossRadioNonG2Targets=$crossRadioTargets; blockingGateCount=$($allBlockingGateIds.Count)" `
                -NextAction $defaultGraduationNextAction `
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
        [string[]]$ExpectedArtifacts = @(),
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

    $normalizedExpectedArtifacts = New-Object System.Collections.Generic.List[string]
    foreach ($artifact in @($ExpectedArtifacts)) {
        if (-not [string]::IsNullOrWhiteSpace($artifact)) {
            $artifactText = [string]$artifact
            if (-not $normalizedExpectedArtifacts.Contains($artifactText)) {
                $normalizedExpectedArtifacts.Add($artifactText) | Out-Null
            }
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedArtifact)) {
        if (-not $normalizedExpectedArtifacts.Contains($ExpectedArtifact)) {
            $normalizedExpectedArtifacts.Add($ExpectedArtifact) | Out-Null
        }
    }
    if ([string]::IsNullOrWhiteSpace($ExpectedArtifact) -and $normalizedExpectedArtifacts.Count -gt 0) {
        $ExpectedArtifact = $normalizedExpectedArtifacts[0]
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
        expectedArtifacts = @($normalizedExpectedArtifacts.ToArray())
        followUp = $FollowUp
    }
}

function Get-LiveMatrixAcceptanceCommandSteps {
    return @(
        'powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-live-diagnostics-matrix.ps1 -BundleDir "$bundleDir" -ComparisonId off-baseline -IndexPath "$bundleDir\artifacts\live-diagnostics-trace-index.off-baseline.json" -ReportPath "$bundleDir\artifacts\live-diagnostics-matrix-report.off-baseline.json" -Samples 60 -IntervalMs 1000',
        'powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-live-diagnostics-matrix.ps1 -BundleDir "$bundleDir" -ComparisonId thetis-parity -IndexPath "$bundleDir\artifacts\live-diagnostics-trace-index.thetis-parity.json" -ReportPath "$bundleDir\artifacts\live-diagnostics-matrix-report.thetis-parity.json" -Samples 60 -IntervalMs 1000',
        'powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-live-diagnostics-matrix.ps1 -BundleDir "$bundleDir" -ComparisonId current-zeus -IndexPath "$bundleDir\artifacts\live-diagnostics-trace-index.baseline.json" -ReportPath "$bundleDir\artifacts\live-diagnostics-matrix-report.baseline.json" -Samples 60 -IntervalMs 1000',
        'powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-live-diagnostics-matrix.ps1 -BundleDir "$bundleDir" -ComparisonId nr5-spnr -IndexPath "$bundleDir\artifacts\live-diagnostics-trace-index.candidate.json" -ReportPath "$bundleDir\artifacts\live-diagnostics-matrix-report.candidate.json" -Samples 60 -IntervalMs 1000',
        'powershell -NoProfile -ExecutionPolicy Bypass -File tools\summarize-dsp-live-diagnostics-history.ps1 -BundleDir "$bundleDir" -ReportPath "$bundleDir\artifacts\live-diagnostics-history.json"',
        'powershell -NoProfile -ExecutionPolicy Bypass -File tools\compare-dsp-live-diagnostics-matrix.ps1 -BundleDir "$bundleDir" -BaselineIndexPath "$bundleDir\artifacts\live-diagnostics-trace-index.baseline.json" -CandidateIndexPath "$bundleDir\artifacts\live-diagnostics-trace-index.candidate.json" -BaselineComparisonId current-zeus -CandidateComparisonId nr5-spnr -ReportPath "$bundleDir\artifacts\live-diagnostics-trace-comparison.json" -FailOnRegression',
        'powershell -NoProfile -ExecutionPolicy Bypass -File tools\compare-dsp-live-diagnostics-matrix.ps1 -BundleDir "$bundleDir" -BaselineIndexPath "$bundleDir\artifacts\live-diagnostics-trace-index.thetis-parity.json" -CandidateIndexPath "$bundleDir\artifacts\live-diagnostics-trace-index.candidate.json" -BaselineComparisonId thetis-parity -CandidateComparisonId nr5-spnr -ReportPath "$bundleDir\artifacts\live-diagnostics-trace-comparison.thetis-parity.json" -FailOnRegression',
        'powershell -NoProfile -ExecutionPolicy Bypass -File tools\new-dsp-artifact-manifest.ps1 -BundleDir "$bundleDir" -AcceptanceManifest -RequireLiveAcceptanceArtifacts -Force',
        'powershell -NoProfile -ExecutionPolicy Bypass -File tools\validate-dsp-modernization-bundle.ps1 -BundleDir "$bundleDir" -RequireArtifactFiles -ReportPath "$bundleDir\validation-report.json"',
        'powershell -NoProfile -ExecutionPolicy Bypass -File tools\summarize-dsp-modernization-validation-report.ps1 -BundleDir "$bundleDir" -ReportPath "$bundleDir\validation-triage-report.json" -MarkdownPath "$bundleDir\validation-triage-report.md" -FailOnIssues'
    )
}

function Get-LiveMatrixAcceptanceExpectedArtifacts {
    return @(
        'artifacts/live-diagnostics-trace-index.off-baseline.json',
        'artifacts/live-diagnostics-matrix-report.off-baseline.json',
        'artifacts/live-diagnostics-trace-index.thetis-parity.json',
        'artifacts/live-diagnostics-matrix-report.thetis-parity.json',
        'artifacts/live-diagnostics-trace-index.baseline.json',
        'artifacts/live-diagnostics-matrix-report.baseline.json',
        'artifacts/live-diagnostics-trace-index.candidate.json',
        'artifacts/live-diagnostics-matrix-report.candidate.json',
        'artifacts/live-diagnostics-history.json',
        'artifacts/live-diagnostics-trace-comparison.json',
        'artifacts/live-diagnostics-trace-comparison.thetis-parity.json',
        'artifact-manifest.json',
        'validation-report.json',
        'validation-triage-report.json',
        'validation-triage-report.md'
    )
}

function Add-LiveMatrixAcceptanceAction {
    param(
        [Parameter(Mandatory = $true)]$Actions,
        [string]$GateId = "live-trace-comparison",
        [string]$Reason = ""
    )

    $Actions.Add((New-AcceptanceActionRecord `
                -ActionId "capture-and-compare-live-matrix" `
                -Priority 60 `
                -StageId "opt-in-candidate-comparison" `
                -GateId $GateId `
                -Category "live-diagnostics" `
                -RequiredForAcceptance:$true `
                -BlocksDefaultChange:$true `
                -Reason $Reason `
                -CommandSteps @(Get-LiveMatrixAcceptanceCommandSteps) `
                -ExpectedArtifact 'artifacts/live-diagnostics-trace-comparison.json' `
                -ExpectedArtifacts @(Get-LiveMatrixAcceptanceExpectedArtifacts) `
                -FollowUp "Review validation-triage-report.json and validation-triage-report.md; do not proceed unless strict validation passes and no required acceptance gate remains blocked.")) | Out-Null
}

function Test-AcceptanceActionExists {
    param(
        [Parameter(Mandatory = $true)]$Actions,
        [Parameter(Mandatory = $true)][string]$ActionId
    )

    return @($Actions.ToArray() | Where-Object {
            [string]::Equals([string](Get-JsonValue $_ "actionId"), $ActionId, [StringComparison]::OrdinalIgnoreCase)
        }).Count -gt 0
}

function Add-AcceptanceActionForGate {
    param(
        [Parameter(Mandatory = $true)]$Actions,
        [Parameter(Mandatory = $true)]$Gate,
        $Validation = $null
    )

    $gateId = [string](Get-JsonValue $Gate "gateId")
    $gateRequired = Test-Truthy (Get-JsonValue $Gate "requiredForAcceptance")
    $reason = [string](Get-JsonValue $Gate "remediation")
    if ([string]::IsNullOrWhiteSpace($reason)) {
        $reason = [string](Get-JsonValue $Gate "detail")
    }

    switch ($gateId) {
        "validation-report" {
            if ($null -ne $Validation) {
                $liveAcceptanceArtifactProblems = @(Get-LiveAcceptanceArtifactProblemRecords -Validation $Validation)
                if ($liveAcceptanceArtifactProblems.Count -gt 0) {
                    $reason = "$reason Missing required live acceptance artifacts: $(Format-LiveAcceptanceArtifactProblemIds -Records $liveAcceptanceArtifactProblems)."
                }
            }

            $Actions.Add((New-AcceptanceActionRecord `
                        -ActionId "rerun-strict-validation" `
                        -Priority 95 `
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
        "native-stage-timing-report" {
            $Actions.Add((New-AcceptanceActionRecord `
                        -ActionId "summarize-native-stage-timing" `
                        -Priority 32 `
                        -StageId "opt-in-dsp-buildout-prerequisites" `
                        -GateId $gateId `
                        -Category "benchmark-fixture" `
                        -RequiredForAcceptance:$true `
                        -BlocksDefaultChange:$true `
                        -Reason $reason `
                        -CommandTemplate 'powershell -NoProfile -ExecutionPolicy Bypass -File tools\summarize-dsp-native-stage-timing.ps1 -BundleDir "$bundleDir" -MetricsPath "$bundleDir\artifacts\offline-fixture-metrics.json" -ReportPath "$bundleDir\artifacts\native-stage-timing-report.json" -Force -FailOnBudget' `
                        -ExpectedArtifact 'artifacts/native-stage-timing-report.json' `
                        -FollowUp "If this report still says native C instrumentation is pending, treat it as wrapper-level timing/allocation evidence only; do not claim internal WDSP allocator timing until native probes are added.")) | Out-Null
        }
        "wdsp-source-drift-report" {
            $Actions.Add((New-AcceptanceActionRecord `
                        -ActionId "classify-wdsp-source-drift" `
                        -Priority 33 `
                        -StageId "opt-in-dsp-buildout-prerequisites" `
                        -GateId $gateId `
                        -Category "wdsp-audit" `
                        -RequiredForAcceptance:$false `
                        -BlocksDefaultChange:$false `
                        -Reason $reason `
                        -CommandTemplate 'powershell -NoProfile -ExecutionPolicy Bypass -File tools\compare-wdsp-source-drift.ps1 -ReferenceDir "<local-thetis-wdsp>" -CandidateDir native\wdsp -ReportPath "$bundleDir\artifacts\wdsp-source-drift-report.json" -FailOnLikelyDefect' `
                        -ExpectedArtifact 'artifacts/wdsp-source-drift-report.json' `
                        -FollowUp "Replace <local-thetis-wdsp> with the local Thetis WDSP source path, then classify or resolve any likely-defect source drift before native WDSP code changes.")) | Out-Null
        }
        "benchmark-plan-coverage" {
            $missingFamilies = @(Get-JsonArray $Validation "benchmarkPlanMissingAcceptanceScenarioFamilyIds")
            $missingFamiliesText = $missingFamilies -join ", "
            $planReason = $reason
            if (-not [string]::IsNullOrWhiteSpace($missingFamiliesText)) {
                $planReason = "$planReason Missing required scenario families: $missingFamiliesText."
            }
            $Actions.Add((New-AcceptanceActionRecord `
                        -ActionId "refresh-benchmark-plan-coverage" `
                        -Priority 39 `
                        -StageId "g2-first-pass-evidence" `
                        -GateId $gateId `
                        -Category "benchmark-fixture" `
                        -RequiredForAcceptance:$true `
                        -BlocksDefaultChange:$true `
                        -Reason $planReason `
                        -CommandTemplate 'powershell -NoProfile -ExecutionPolicy Bypass -File tools\capture-dsp-modernization-bundle.ps1 -BaseUrl http://localhost:6060 -OutputRoot captures\dsp-modernization -Label benchmark-plan-coverage-refresh' `
                        -ExpectedArtifact 'benchmark-plan.json' `
                        -FollowUp "Ensure every required acceptance scenario family is present with requiredComparisons, requiredMetrics, and acceptanceGates before rerunning fixture/live comparisons.")) | Out-Null
        }
        "benchmark-metric-catalog" {
            $contractProblems = @(Get-JsonArray $Validation "metricCatalogContractProblemMetricIds")
            $catalogReason = $reason
            if ($contractProblems.Count -gt 0) {
                $catalogReason = "$catalogReason Contract problem metric IDs: $($contractProblems -join ', '). Missing fields: threshold=$(Get-JsonValue $Validation "metricCatalogMissingThresholdCount"), comparator=$(Get-JsonValue $Validation "metricCatalogMissingComparatorCount"), unit=$(Get-JsonValue $Validation "metricCatalogMissingUnitCount"), safetyClass=$(Get-JsonValue $Validation "metricCatalogMissingSafetyClassCount"), acceptanceScope=$(Get-JsonValue $Validation "metricCatalogMissingAcceptanceScopeCount")."
            }
            $Actions.Add((New-AcceptanceActionRecord `
                        -ActionId "refresh-benchmark-metric-catalog" `
                        -Priority 40 `
                        -StageId "g2-first-pass-evidence" `
                        -GateId $gateId `
                        -Category "benchmark-fixture" `
                        -RequiredForAcceptance:$true `
                        -BlocksDefaultChange:$true `
                        -Reason $catalogReason `
                        -CommandTemplate 'powershell -NoProfile -ExecutionPolicy Bypass -File tools\capture-dsp-modernization-bundle.ps1 -BaseUrl http://localhost:6060 -OutputRoot captures\dsp-modernization -Label metric-catalog-refresh' `
                        -ExpectedArtifact 'benchmark-metric-catalog.json' `
                        -FollowUp "Rerun strict validation after the metric catalog declares required metrics, directions, thresholds, comparators, units, safety classes, and acceptance scope.")) | Out-Null
        }
        "fixture-metric-comparison" {
            $fixtureReason = $reason
            $comparisonContractProblems = @(Get-JsonArray $Validation "metricComparisonCatalogContractProblemMetricIds")
            if ($comparisonContractProblems.Count -gt 0) {
                $fixtureReason = "$fixtureReason Metric comparison catalog contract problem IDs: $($comparisonContractProblems -join ', ')."
            }
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
                        -Reason $fixtureReason `
                        -CommandSteps $fixtureComparisonCommandSteps `
                        -ExpectedArtifact 'artifacts/dsp-fixture-metric-comparison.json' `
                        -FollowUp "Resolve weak-signal, pumping, clipping, latency, or artifact regressions before live review; deterministic fixture evidence is only a schema fallback, not default-graduation proof.")) | Out-Null
        }
        "live-trace-comparison" {
            $captureHardGateFailCount = [int](Get-JsonValue $Validation "liveTraceComparisonCaptureReadinessCandidateHardGateFailCount")
            $captureStrictPreflightFailCount = [int](Get-JsonValue $Validation "liveTraceComparisonCaptureReadinessCandidateStrictPreflightFailCount")
            $topCaptureConstraint = Get-TopCountText `
                -Items @(Get-JsonArray $Validation "liveTraceComparisonCaptureReadinessCandidateTopConstraintCounts") `
                -NameField "constraint"
            $topCaptureHardGate = Get-TopCountText `
                -Items @(Get-JsonArray $Validation "liveTraceComparisonCaptureReadinessCandidateTopHardConstraintCounts") `
                -NameField "constraint"

            if ($captureHardGateFailCount -gt 0) {
                $hardGateReason = "Candidate live comparison is blocked by $captureHardGateFailCount capture hard-gate failure(s)."
                if (-not [string]::IsNullOrWhiteSpace($topCaptureHardGate)) {
                    $hardGateReason = "$hardGateReason Top hard gate: $topCaptureHardGate."
                }
                if (-not [string]::IsNullOrWhiteSpace($topCaptureConstraint)) {
                    $hardGateReason = "$hardGateReason Top capture constraint: $topCaptureConstraint."
                }

                $Actions.Add((New-AcceptanceActionRecord `
                            -ActionId "clear-live-capture-readiness-hard-gates" `
                            -Priority 55 `
                            -StageId "opt-in-candidate-comparison" `
                            -GateId $gateId `
                            -Category "live-diagnostics" `
                            -RequiredForAcceptance:$true `
                            -BlocksDefaultChange:$true `
                            -Reason $hardGateReason `
                            -ManualAction "Clear the named live diagnostics capture hard gate, then recapture baseline and candidate live windows before interpreting DSP metric regressions as algorithm behavior." `
                            -ExpectedArtifact 'artifacts/live-diagnostics-trace-comparison.json' `
                            -FollowUp "Rerun compare-dsp-live-diagnostics-matrix.ps1 or compare-dsp-live-diagnostics-traces.ps1 with -BundleDir and -FailOnRegression, then rerun strict validation and triage.")) | Out-Null
            }
            elseif ($captureStrictPreflightFailCount -gt 0) {
                $preflightReason = "Candidate live comparison has $captureStrictPreflightFailCount strict-preflight failure(s)."
                if (-not [string]::IsNullOrWhiteSpace($topCaptureConstraint)) {
                    $preflightReason = "$preflightReason Top capture constraint: $topCaptureConstraint."
                }

                $Actions.Add((New-AcceptanceActionRecord `
                            -ActionId "clear-live-capture-readiness-advisories" `
                            -Priority 56 `
                            -StageId "opt-in-candidate-comparison" `
                            -GateId $gateId `
                            -Category "live-diagnostics" `
                            -RequiredForAcceptance:$true `
                            -BlocksDefaultChange:$true `
                            -Reason $preflightReason `
                            -ManualAction "Resolve or explicitly document the named live diagnostics capture advisory before using the candidate window for final acceptance evidence." `
                            -ExpectedArtifact 'artifacts/live-diagnostics-trace-comparison.json' `
                            -FollowUp "Rerun validation triage after the advisory is cleared or documented in the operator evidence notes.")) | Out-Null
            }

            if (-not (Test-AcceptanceActionExists -Actions $Actions -ActionId "capture-and-compare-live-matrix")) {
                Add-LiveMatrixAcceptanceAction -Actions $Actions -GateId $gateId -Reason $reason
            }
        }
        "thetis-parity-live-comparison" {
            $captureHardGateFailCount = [int](Get-JsonValue $Validation "liveTraceThetisComparisonCaptureReadinessCandidateHardGateFailCount")
            $captureStrictPreflightFailCount = [int](Get-JsonValue $Validation "liveTraceThetisComparisonCaptureReadinessCandidateStrictPreflightFailCount")
            $topCaptureConstraint = Get-TopCountText `
                -Items @(Get-JsonArray $Validation "liveTraceThetisComparisonCaptureReadinessCandidateTopConstraintCounts") `
                -NameField "constraint"
            $topCaptureHardGate = Get-TopCountText `
                -Items @(Get-JsonArray $Validation "liveTraceThetisComparisonCaptureReadinessCandidateTopHardConstraintCounts") `
                -NameField "constraint"

            if ($captureHardGateFailCount -gt 0) {
                $hardGateReason = "Thetis-parity live comparison is blocked by $captureHardGateFailCount capture hard-gate failure(s)."
                if (-not [string]::IsNullOrWhiteSpace($topCaptureHardGate)) {
                    $hardGateReason = "$hardGateReason Top hard gate: $topCaptureHardGate."
                }
                if (-not [string]::IsNullOrWhiteSpace($topCaptureConstraint)) {
                    $hardGateReason = "$hardGateReason Top capture constraint: $topCaptureConstraint."
                }

                $Actions.Add((New-AcceptanceActionRecord `
                            -ActionId "clear-thetis-live-capture-readiness-hard-gates" `
                            -Priority 57 `
                            -StageId "opt-in-candidate-comparison" `
                            -GateId $gateId `
                            -Category "live-diagnostics" `
                            -RequiredForAcceptance:$true `
                            -BlocksDefaultChange:$true `
                            -Reason $hardGateReason `
                            -ManualAction "Clear the named Thetis-parity live diagnostics capture hard gate, then recapture Thetis baseline and candidate live windows before interpreting DSP metric regressions as algorithm behavior." `
                            -ExpectedArtifact 'artifacts/live-diagnostics-trace-comparison.thetis-parity.json' `
                            -FollowUp "Rerun compare-dsp-live-diagnostics-matrix.ps1 with -BaselineComparisonId thetis-parity, -BundleDir, and -FailOnRegression, then rerun strict validation and triage.")) | Out-Null
            }
            elseif ($captureStrictPreflightFailCount -gt 0) {
                $preflightReason = "Thetis-parity live comparison has $captureStrictPreflightFailCount strict-preflight failure(s)."
                if (-not [string]::IsNullOrWhiteSpace($topCaptureConstraint)) {
                    $preflightReason = "$preflightReason Top capture constraint: $topCaptureConstraint."
                }

                $Actions.Add((New-AcceptanceActionRecord `
                            -ActionId "clear-thetis-live-capture-readiness-advisories" `
                            -Priority 58 `
                            -StageId "opt-in-candidate-comparison" `
                            -GateId $gateId `
                            -Category "live-diagnostics" `
                            -RequiredForAcceptance:$true `
                            -BlocksDefaultChange:$true `
                            -Reason $preflightReason `
                            -ManualAction "Resolve or explicitly document the named Thetis-parity live diagnostics capture advisory before using the candidate window for final acceptance evidence." `
                            -ExpectedArtifact 'artifacts/live-diagnostics-trace-comparison.thetis-parity.json' `
                            -FollowUp "Rerun validation triage after the advisory is cleared or documented in the operator evidence notes.")) | Out-Null
            }

            if (-not (Test-AcceptanceActionExists -Actions $Actions -ActionId "capture-and-compare-live-matrix")) {
                Add-LiveMatrixAcceptanceAction -Actions $Actions -GateId $gateId -Reason $reason
            }
        }
        "puresignal-safe-bypass" {
            $missingModes = (@(Get-JsonArray $Validation "pureSignalSafeBypassMissingModes") | ForEach-Object { [string]$_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join ", "
            $pureSignalReason = "$reason Status=$(Get-JsonValue $Validation "pureSignalSafeBypassReportStatus"), disabledReady=$(Get-JsonValue $Validation "pureSignalSafeBypassDisabledPathReady"), enabledReady=$(Get-JsonValue $Validation "pureSignalSafeBypassEnabledPathReady"), missingModes=$missingModes, clipping=$(Get-JsonValue $Validation "pureSignalSafeBypassClippingCountTotal"), gateFailures=$(Get-JsonValue $Validation "pureSignalSafeBypassGateFailureCount")."
            $Actions.Add((New-AcceptanceActionRecord `
                        -ActionId "capture-puresignal-safe-bypass-bench" `
                        -Priority 68 `
                        -StageId "g2-first-pass-evidence" `
                        -GateId $gateId `
                        -Category "tx-puresignal" `
                        -RequiredForAcceptance:$gateRequired `
                        -BlocksDefaultChange:$true `
                        -Reason $pureSignalReason `
                        -CommandSteps @(
                            'powershell -NoProfile -ExecutionPolicy Bypass -File tools\summarize-dsp-puresignal-bench.ps1 -BundleDir "$bundleDir" -DisabledTracePath "$bundleDir\artifacts\puresignal-disabled.json" -EnabledTracePath "$bundleDir\artifacts\puresignal-enabled.json" -ReportPath "$bundleDir\artifacts\puresignal-safe-bypass-report.json" -Force'
                        ) `
                        -ManualAction "On G2, capture TX/PureSignal bench traces with PureSignal disabled/bypassed and enabled before running the summary command. Do not route external DSP/ML, TX monitor audio, or default profile changes into the PureSignal feedback path." `
                        -ExpectedArtifact "artifacts/puresignal-safe-bypass-report.json" `
                        -FollowUp "Rerun strict validation and validation triage; TX profile graduation remains blocked until the report is ready and defaultBehaviorChangeApproved remains false.")) | Out-Null
        }
        "external-engine-candidates" {
            $externalMissingIds = Join-ExternalCandidateIds @(Get-JsonArray $Validation "externalEngineCandidateMissingIds")
            $externalUnsafeIds = Join-ExternalCandidateIds @(Get-JsonArray $Validation "externalEngineCandidateUnsafeIds")
            $externalTopIssue = Get-ExternalIssueTopText @(Get-JsonArray $Validation "externalEngineCandidateIssueCounts")
            $externalCandidateReason = $reason
            if (-not [string]::IsNullOrWhiteSpace($externalMissingIds)) {
                $externalCandidateReason = "$externalCandidateReason Missing required candidates: $externalMissingIds."
            }
            if (-not [string]::IsNullOrWhiteSpace($externalUnsafeIds)) {
                $externalCandidateReason = "$externalCandidateReason Unsafe candidates: $externalUnsafeIds."
            }
            if (-not [string]::IsNullOrWhiteSpace($externalTopIssue)) {
                $externalCandidateReason = "$externalCandidateReason Top issue: $externalTopIssue."
            }
            $Actions.Add((New-AcceptanceActionRecord `
                        -ActionId "refresh-external-engine-catalog" `
                        -Priority 70 `
                        -StageId "external-dsp-ml-bakeoff" `
                        -GateId $gateId `
                        -Category "external-dsp-ml" `
                        -RequiredForAcceptance:$gateRequired `
                        -BlocksDefaultChange:$false `
                        -Reason $externalCandidateReason `
                        -CommandTemplate 'powershell -NoProfile -ExecutionPolicy Bypass -File tools\capture-dsp-modernization-bundle.ps1 -BaseUrl http://localhost:6060 -OutputRoot captures\dsp-modernization -Label external-candidate-refresh' `
                        -ExpectedArtifact 'external-engine-candidates.json' `
                        -FollowUp "Keep every external engine post-demod, opt-in, packaged, licensed, and off by default.")) | Out-Null
        }
        "external-engine-bakeoff" {
            $externalMissingIds = Join-ExternalCandidateIds @(Get-JsonArray $Validation "externalEngineBakeoffMissingCandidateIds")
            if ([string]::IsNullOrWhiteSpace($externalMissingIds)) {
                $externalMissingIds = Join-ExternalCandidateIds @(Get-JsonArray $Validation "externalEngineCandidateMissingIds")
            }
            $externalUnsafeIds = Join-ExternalCandidateIds @(Get-JsonArray $Validation "externalEngineBakeoffUnsafeCandidateIds")
            if ([string]::IsNullOrWhiteSpace($externalUnsafeIds)) {
                $externalUnsafeIds = Join-ExternalCandidateIds @(Get-JsonArray $Validation "externalEngineCandidateUnsafeIds")
            }
            $externalBlockedIds = Join-ExternalCandidateIds @(Get-JsonArray $Validation "externalEngineBakeoffBlockedCandidateIds")
            $externalTopIssue = Get-ExternalIssueTopText @(Get-JsonArray $Validation "externalEngineBakeoffCandidateIssueCounts")
            if ([string]::IsNullOrWhiteSpace($externalTopIssue)) {
                $externalTopIssue = Get-ExternalIssueTopText @(Get-JsonArray $Validation "externalEngineCandidateIssueCounts")
            }
            $externalBakeoffReason = $reason
            if (-not [string]::IsNullOrWhiteSpace($externalMissingIds)) {
                $externalBakeoffReason = "$externalBakeoffReason Missing required candidates: $externalMissingIds."
            }
            if (-not [string]::IsNullOrWhiteSpace($externalUnsafeIds)) {
                $externalBakeoffReason = "$externalBakeoffReason Unsafe candidates: $externalUnsafeIds."
            }
            if (-not [string]::IsNullOrWhiteSpace($externalBlockedIds)) {
                $externalBakeoffReason = "$externalBakeoffReason Blocked-for-integration candidates: $externalBlockedIds."
            }
            if (-not [string]::IsNullOrWhiteSpace($externalTopIssue)) {
                $externalBakeoffReason = "$externalBakeoffReason Top issue: $externalTopIssue."
            }
            $Actions.Add((New-AcceptanceActionRecord `
                        -ActionId "run-external-engine-bakeoff-summary" `
                        -Priority 71 `
                        -StageId "external-dsp-ml-bakeoff" `
                        -GateId $gateId `
                        -Category "external-dsp-ml" `
                        -RequiredForAcceptance:$gateRequired `
                        -BlocksDefaultChange:$false `
                        -Reason $externalBakeoffReason `
                        -CommandTemplate 'powershell -NoProfile -ExecutionPolicy Bypass -File tools\summarize-dsp-external-engine-candidates.ps1 -BundleDir "$bundleDir" -CandidatePath "$bundleDir\external-engine-candidates.json" -SnapshotPath "$bundleDir\modernization-snapshot.json" -ReportPath "$bundleDir\artifacts\external-engine-bakeoff-report.json" -FailOnUnsafe' `
                        -ExpectedArtifact 'artifacts/external-engine-bakeoff-report.json' `
                        -FollowUp "External DSP/ML remains opt-in and post-demod only until missing/unsafe candidate IDs are cleared and explicit radio-safety approval exists.")) | Out-Null
        }
        "external-engine-bakeoff-cycle" {
            $externalFirstSafeCandidateId = [string](Get-JsonValue $Validation "externalEngineBakeoffFirstSafeCandidateId")
            if ([string]::IsNullOrWhiteSpace($externalFirstSafeCandidateId)) {
                $externalFirstSafeCandidateId = [string](Get-JsonValue $Validation "externalEngineBakeoffCycleCandidateId")
            }
            if ([string]::IsNullOrWhiteSpace($externalFirstSafeCandidateId)) {
                $externalFirstSafeCandidateId = "<external-bakeoff-candidate-id>"
            }

            $externalFirstSafeScenarioIds = @(Get-JsonArray $Validation "externalEngineBakeoffFirstSafeScenarioIds" | ForEach-Object { [string]$_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
            if ($externalFirstSafeScenarioIds.Count -eq 0) {
                $externalFirstSafeScenarioIds = @(Get-JsonArray $Validation "externalEngineBakeoffCycleScenarioIds" | ForEach-Object { [string]$_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
            }
            if ($externalFirstSafeScenarioIds.Count -eq 0) {
                $externalFirstSafeScenarioIds = @(Get-JsonArray $Validation "externalEngineBakeoffPlanScenarioIds" | ForEach-Object { [string]$_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
            }
            $scenarioRunnerArgument = if ($externalFirstSafeScenarioIds.Count -gt 0) {
                $externalFirstSafeScenarioIds -join " "
            }
            else {
                "<external-bakeoff-scenario-ids>"
            }

            $cycleStatus = [string](Get-JsonValue $Validation "externalEngineBakeoffCycleStatus")
            $cycleReason = "$reason Cycle status='$cycleStatus', valid=$(Get-JsonValue $Validation "externalEngineBakeoffCycleSummaryValid"), readyToExecute=$(Get-JsonValue $Validation "externalEngineBakeoffCycleReadyToExecute"), missingPrerequisites=$(Get-JsonValue $Validation "externalEngineBakeoffCycleMissingPrerequisiteCount"), nonZeroExits=$(Get-JsonValue $Validation "externalEngineBakeoffCycleNonZeroExitCount")."

            $Actions.Add((New-AcceptanceActionRecord `
                        -ActionId "run-first-safe-external-engine-bakeoff" `
                        -Priority 72 `
                        -StageId "external-dsp-ml-bakeoff" `
                        -GateId $gateId `
                        -Category "external-dsp-ml" `
                        -RequiredForAcceptance:$gateRequired `
                        -BlocksDefaultChange:$false `
                        -Reason $cycleReason `
                        -CommandSteps @(
                            "powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-external-engine-bakeoff.ps1 -BundleDir `"`$bundleDir`" -CandidateId $externalFirstSafeCandidateId -ScenarioIds $scenarioRunnerArgument -PlanOnly"
                        ) `
                        -ManualAction "Produce or enable only the post-demod, operator-opt-in '$externalFirstSafeCandidateId' candidate path before executing the runner plan. Start with -PlanOnly; use -Execute only after fixture metrics exist and the operator has intentionally enabled the candidate path. Do not route raw WDSP IQ, TX audio, TX monitor, or PureSignal feedback through the external engine." `
                        -ExpectedArtifact "artifacts/external-engine-bakeoff-cycle-summary.json" `
                        -ExpectedArtifacts @(
                            "artifacts/external-engine-bakeoff-cycle-summary.json",
                            "artifacts/external-engine-bakeoff-cycle-summary.md"
                        ) `
                        -FollowUp "Review the bakeoff cycle summary, then execute its child plan only as exploratory opt-in evidence. External DSP/ML remains post-demod and off by default until fixture metrics, G2 live evidence, operator notes, package/license review, and cross-radio validation all pass.")) | Out-Null
        }
        "live-matrix-artifact-control" {
            $artifactStatus = [string](Get-JsonValue $Validation "liveMatrixArtifactControlStatus")
            if ([string]::Equals($artifactStatus, "artifact-review", [StringComparison]::OrdinalIgnoreCase)) {
                $reviewRuns = Get-JsonValue $Validation "liveMatrixArtifactControlReviewRunCount"
                $riskScoreMax = Get-JsonValue $Validation "liveMatrixArtifactControlRiskScoreMax"
                $liftedSamples = Get-JsonValue $Validation "liveMatrixArtifactControlLowEvidenceLiftedSampleCount"
                $liftedPctMax = Get-JsonValue $Validation "liveMatrixArtifactControlLowEvidenceLiftedPctMax"
                $audioMismatchPctMax = Get-JsonValue $Validation "liveMatrixArtifactControlAudioAlignmentMismatchPctMax"
                $bestRun = Get-JsonValue $Validation "liveMatrixMixedWeakStrongBestRun"
                $bestScenarioId = [string](Get-JsonValue $bestRun "scenarioId")
                $bestComparisonId = [string](Get-JsonValue $bestRun "comparisonId")
                if ([string]::IsNullOrWhiteSpace($bestComparisonId)) {
                    $bestComparisonId = "nr5-spnr"
                }
                $scenarioArgument = if ([string]::IsNullOrWhiteSpace($bestScenarioId)) {
                    ""
                }
                else {
                    " -ScenarioIds $bestScenarioId"
                }
                $artifactReason = "Matrix artifact-control status is artifact-review: reviewRuns=$reviewRuns, riskScoreMax=$riskScoreMax, lowEvidenceLiftedSamples=$liftedSamples, lowEvidenceLiftedPctMax=$liftedPctMax, audioAlignmentMismatchPctMax=$audioMismatchPctMax."
                if ($null -ne $bestRun) {
                    $artifactReason = "$artifactReason Best mixed weak/strong candidate is scenario='$bestScenarioId', comparison='$bestComparisonId'; recapture or choose a cleaner window before promoting it into live history."
                }

                $artifactCommandSteps = @(
                    "powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-live-diagnostics-matrix.ps1 -BundleDir `"`$bundleDir`" -ComparisonId $bestComparisonId$scenarioArgument -IndexPath `"`$bundleDir\artifacts\live-diagnostics-trace-index.artifact-control-followup.json`" -ReportPath `"`$bundleDir\artifacts\live-diagnostics-matrix-report.artifact-control-followup.json`" -Samples 90 -IntervalMs 1000 -ContinueOnError",
                    'powershell -NoProfile -ExecutionPolicy Bypass -File tools\validate-dsp-modernization-bundle.ps1 -BundleDir "$bundleDir" -RequireArtifactFiles -ReportPath "$bundleDir\validation-report.json"',
                    'powershell -NoProfile -ExecutionPolicy Bypass -File tools\summarize-dsp-modernization-validation-report.ps1 -BundleDir "$bundleDir" -ReportPath "$bundleDir\validation-triage-report.json" -MarkdownPath "$bundleDir\validation-triage-report.md" -FailOnIssues'
                )

                $Actions.Add((New-AcceptanceActionRecord `
                            -ActionId "recapture-matrix-artifact-control-window" `
                            -Priority 77 `
                            -StageId "opt-in-candidate-comparison" `
                            -GateId $gateId `
                            -Category "live-diagnostics" `
                            -RequiredForAcceptance:$false `
                            -BlocksDefaultChange:$true `
                            -Reason $artifactReason `
                            -CommandSteps $artifactCommandSteps `
                            -ManualAction "On G2, prefer a cleaner active window before using the current best matrix run for NR5/SPNR tuning. Listen for metallic or burbling speech artifacts and avoid low-evidence lift or unsupported texture-fill windows." `
                            -ExpectedArtifact 'artifacts/live-diagnostics-matrix-report.artifact-control-followup.json' `
                            -ExpectedArtifacts @(
                                'artifacts/live-diagnostics-trace-index.artifact-control-followup.json',
                                'artifacts/live-diagnostics-matrix-report.artifact-control-followup.json',
                                'validation-report.json',
                                'validation-triage-report.json',
                                'validation-triage-report.md'
                            ) `
                            -FollowUp "Only promote a matrix window into live history after liveMatrixArtifactControlStatus is clear, or after operator notes explicitly justify using the artifact-review window as exploratory opt-in evidence.")) | Out-Null
            }
        }
        "live-history-mixed-weak-strong" {
            $matrixHuntReady = Test-Truthy (Get-JsonValue $Validation "liveMatrixMixedWeakStrongHuntReady")
            $matrixBestRun = Get-JsonValue $Validation "liveMatrixMixedWeakStrongBestRun"
            if ($matrixHuntReady -and $null -ne $matrixBestRun) {
                $bestScenarioId = [string](Get-JsonValue $matrixBestRun "scenarioId")
                $bestComparisonId = [string](Get-JsonValue $matrixBestRun "comparisonId")
                $bestScore = Get-JsonValue $matrixBestRun "mixedWeakStrongHuntScore"
                $bestGap = Get-JsonValue $matrixBestRun "weakStrongOutputGapDb"
                $bestStatus = [string](Get-JsonValue $matrixBestRun "mixedWeakStrongEvidenceStatus")
                $bestArtifactPath = [string](Get-JsonValue $matrixBestRun "artifactPath")
                if ([string]::IsNullOrWhiteSpace($bestComparisonId)) {
                    $bestComparisonId = "nr5-spnr"
                }
                $scenarioArgument = if ([string]::IsNullOrWhiteSpace($bestScenarioId)) {
                    ""
                }
                else {
                    " -ScenarioIds $bestScenarioId"
                }
                $matrixReason = "Live history mixed weak/strong evidence is not ready, but the matrix hunt found a candidate window: scenario='$bestScenarioId', comparison='$bestComparisonId', score=$bestScore, weakStrongOutputGapDb=$bestGap, status='$bestStatus', artifact='$bestArtifactPath'. Promote or recapture that G2 window into schema-v14 live history."
                $targetedCommandSteps = @(
                    "powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-live-diagnostics-matrix.ps1 -BundleDir `"`$bundleDir`" -ComparisonId $bestComparisonId$scenarioArgument -IndexPath `"`$bundleDir\artifacts\live-diagnostics-trace-index.mixed-weak-strong-followup.json`" -ReportPath `"`$bundleDir\artifacts\live-diagnostics-matrix-report.mixed-weak-strong-followup.json`" -Samples 90 -IntervalMs 1000 -ContinueOnError",
                    'powershell -NoProfile -ExecutionPolicy Bypass -File tools\summarize-dsp-live-diagnostics-history.ps1 -BundleDir "$bundleDir" -ReportPath "$bundleDir\artifacts\live-diagnostics-history.json"',
                    'powershell -NoProfile -ExecutionPolicy Bypass -File tools\validate-dsp-modernization-bundle.ps1 -BundleDir "$bundleDir" -RequireArtifactFiles -ReportPath "$bundleDir\validation-report.json"',
                    'powershell -NoProfile -ExecutionPolicy Bypass -File tools\summarize-dsp-modernization-validation-report.ps1 -BundleDir "$bundleDir" -ReportPath "$bundleDir\validation-triage-report.json" -MarkdownPath "$bundleDir\validation-triage-report.md" -FailOnIssues'
                )

                $Actions.Add((New-AcceptanceActionRecord `
                            -ActionId "promote-matrix-mixed-weak-strong-window" `
                            -Priority 78 `
                            -StageId "opt-in-candidate-comparison" `
                            -GateId $gateId `
                            -Category "live-diagnostics" `
                            -RequiredForAcceptance:$gateRequired `
                            -BlocksDefaultChange:$true `
                            -Reason $matrixReason `
                            -CommandSteps $targetedCommandSteps `
                            -ManualAction "On G2, recreate the active weak+strong signal window represented by the best matrix run before executing the targeted recapture. Keep NR5/SPNR opt-in and do not change defaults." `
                            -ExpectedArtifact 'artifacts/live-diagnostics-history.json' `
                            -ExpectedArtifacts @(
                                'artifacts/live-diagnostics-trace-index.mixed-weak-strong-followup.json',
                                'artifacts/live-diagnostics-matrix-report.mixed-weak-strong-followup.json',
                                'artifacts/live-diagnostics-history.json',
                                'validation-report.json',
                                'validation-triage-report.json',
                                'validation-triage-report.md'
                            ) `
                            -FollowUp "Acceptance remains blocked until strict validation reports liveDiagnosticsHistoryMixedWeakStrongEvidenceReady=true and the G2/Thetis/current-Zeus comparisons still pass.")) | Out-Null
            }
            else {
                $manualObserverPresent = Test-Truthy (Get-JsonValue $Validation "manualTuneObserverReportPresent")
                $manualObserverReady = Test-Truthy (Get-JsonValue $Validation "manualTuneObserverReportReady")
                $manualObserverValid = Test-Truthy (Get-JsonValue $Validation "manualTuneObserverReportValid")
                $manualObserverSchemaVersion = Get-IntegerValueOrDefault (Get-JsonValue $Validation "manualTuneObserverSchemaVersion")
                $manualObserverTool = [string](Get-JsonValue $Validation "manualTuneObserverTool")
                $manualObserverOk = Test-Truthy (Get-JsonValue $Validation "manualTuneObserverOk")
                $manualObserverScanError = [string](Get-JsonValue $Validation "manualTuneObserverScanError")
                $manualObserverMixedReady = Test-Truthy (Get-JsonValue $Validation "manualTuneObserverMixedWeakStrongReady")
                $manualObserverBundleRelativePaths = Test-Truthy (Get-JsonValue $Validation "manualTuneObserverBundleRelativePaths")
                $manualObserverSafetyRxOnly = Test-Truthy (Get-JsonValue $Validation "manualTuneObserverSafetyRxOnly")
                $manualObserverSafetyReadOnly = Test-Truthy (Get-JsonValue $Validation "manualTuneObserverSafetyReadOnly")
                $manualObserverSafetyApiWrites = Test-Truthy (Get-JsonValue $Validation "manualTuneObserverSafetyApiWrites")
                $manualObserverSafetyRetune = Test-Truthy (Get-JsonValue $Validation "manualTuneObserverSafetyRetune")
                $manualObserverSafetyTxTouched = Test-Truthy (Get-JsonValue $Validation "manualTuneObserverSafetyTxEndpointsTouched")
                $manualObserverVfoWrites = Get-IntegerValueOrDefault (Get-JsonValue $Validation "manualTuneObserverSafetyVfoWriteAttemptCount")
                $manualObserverRadioLoWrites = Get-IntegerValueOrDefault (Get-JsonValue $Validation "manualTuneObserverSafetyRadioLoWriteAttemptCount")
                $manualObserverCaptureCount = Get-IntegerValueOrDefault (Get-JsonValue $Validation "manualTuneObserverCaptureCount")
                $manualObserverReferencedCaptureCount = Get-IntegerValueOrDefault (Get-JsonValue $Validation "manualTuneObserverReferencedCaptureCount")
                $manualObserverReferencedCaptureReadyCount = Get-IntegerValueOrDefault (Get-JsonValue $Validation "manualTuneObserverReferencedCaptureReadyCount")
                $manualObserverReferencedCaptureProblemCount = Get-IntegerValueOrDefault (Get-JsonValue $Validation "manualTuneObserverReferencedCaptureProblemCount")
                $manualObserverAgcPumpingRiskCaptureCount = Get-IntegerValueOrDefault (Get-JsonValue $Validation "manualTuneObserverAgcPumpingRiskCaptureCount")
                $manualBestReportPath = [string](Get-JsonValue $Validation "manualTuneObserverBestReportPath")
                $manualBestJsonlPath = [string](Get-JsonValue $Validation "manualTuneObserverBestJsonlPath")
                $manualNearStrongCandidateCount = Get-IntegerValueOrDefault (Get-JsonValue $Validation "manualTuneObserverNearStrongPromotionCandidateCaptureCount")
                $manualNearStrongReportPath = [string](Get-JsonValue $Validation "manualTuneObserverBestNearStrongPromotionCandidateReportPath")
                $manualNearStrongVfoHz = Get-JsonValue $Validation "manualTuneObserverBestNearStrongPromotionCandidateVfoHz"
                $manualNearStrongVfoMhz = Get-JsonValue $Validation "manualTuneObserverBestNearStrongPromotionCandidateVfoMhz"
                $manualNearStrongDistance = Get-JsonValue $Validation "manualTuneObserverBestNearStrongPromotionCandidateDistanceToStrongThresholdDb"
                $manualNearStrongSampleCount = Get-IntegerValueOrDefault (Get-JsonValue $Validation "manualTuneObserverBestNearStrongPromotionCandidateNearStrongInputSampleCount")
                $manualNearStrongSpeechSampleCount = Get-IntegerValueOrDefault (Get-JsonValue $Validation "manualTuneObserverBestNearStrongPromotionCandidateSpeechQualifiedNearStrongInputSampleCount")
                $manualNearStrongPassbandSampleCount = Get-IntegerValueOrDefault (Get-JsonValue $Validation "manualTuneObserverBestNearStrongPromotionCandidatePassbandQualifiedNearStrongInputSampleCount")
                $manualNearStrongReadyWeakOnlyCount = Get-IntegerValueOrDefault (Get-JsonValue $Validation "manualTuneObserverReadyWeakOnlyCaptureCount")
                $manualNearStrongWeakOnlyCount = Get-IntegerValueOrDefault (Get-JsonValue $Validation "manualTuneObserverWeakOnlyCaptureCount")
                $manualNearStrongMissingStrongCount = Get-IntegerValueOrDefault (Get-JsonValue $Validation "manualTuneObserverMissingStrongInputCaptureCount")
                $manualNearStrongStatusCounts = @(Get-JsonArray $Validation "manualTuneObserverMixedWeakStrongEvidenceStatusCounts")
                $manualNearStrongStatusCountsText = if ($manualNearStrongStatusCounts.Count -le 0) {
                    "none"
                }
                else {
                    ($manualNearStrongStatusCounts | ForEach-Object {
                            "$([string](Get-JsonValue $_ "status"))=$(Get-JsonValue $_ "count")"
                        }) -join ","
                }
                $manualObserverPromotionReady =
                    $manualObserverPresent -and
                    $manualObserverReady -and
                    $manualObserverValid -and
                    $manualObserverMixedReady -and
                    $manualObserverBundleRelativePaths -and
                    $manualObserverSafetyReadOnly -and
                    (-not $manualObserverSafetyApiWrites) -and
                    (-not $manualObserverSafetyRetune) -and
                    (-not $manualObserverSafetyTxTouched) -and
                    $manualObserverVfoWrites -eq 0 -and
                    $manualObserverRadioLoWrites -eq 0 -and
                    $manualObserverReferencedCaptureReadyCount -gt 0 -and
                    $manualObserverReferencedCaptureProblemCount -eq 0 -and
                    $manualObserverAgcPumpingRiskCaptureCount -eq 0 -and
                    -not [string]::IsNullOrWhiteSpace($manualBestReportPath)
                $manualObserverNearStrongCandidateReady =
                    $manualObserverPresent -and
                    $manualObserverReady -and
                    $manualObserverValid -and
                    (-not $manualObserverMixedReady) -and
                    $manualObserverBundleRelativePaths -and
                    $manualObserverSafetyReadOnly -and
                    (-not $manualObserverSafetyApiWrites) -and
                    (-not $manualObserverSafetyRetune) -and
                    (-not $manualObserverSafetyTxTouched) -and
                    $manualObserverVfoWrites -eq 0 -and
                    $manualObserverRadioLoWrites -eq 0 -and
                    $manualObserverReferencedCaptureReadyCount -gt 0 -and
                    $manualObserverReferencedCaptureProblemCount -eq 0 -and
                    $manualObserverAgcPumpingRiskCaptureCount -eq 0 -and
                    $manualNearStrongCandidateCount -gt 0 -and
                    -not [string]::IsNullOrWhiteSpace($manualNearStrongReportPath)

                if ($manualObserverPromotionReady) {
                    $manualObserverStatus = [string](Get-JsonValue $Validation "manualTuneObserverReportStatus")
                    $manualBestFrequencyHz = Get-JsonValue $Validation "manualTuneObserverBestFrequencyHz"
                    $manualBestStatus = [string](Get-JsonValue $Validation "manualTuneObserverBestStatus")
                    $manualWeakSamples = Get-JsonValue $Validation "manualTuneObserverWeakInputSampleCount"
                    $manualStrongSamples = Get-JsonValue $Validation "manualTuneObserverStrongInputSampleCount"
                    $manualNearStrongSamples = Get-JsonValue $Validation "manualTuneObserverNearStrongInputSampleCount"
                    $manualSpeechWeakSamples = Get-JsonValue $Validation "manualTuneObserverSpeechQualifiedWeakInputSampleCount"
                    $manualSpeechStrongSamples = Get-JsonValue $Validation "manualTuneObserverSpeechQualifiedStrongInputSampleCount"
                    $manualPassbandWeakSamples = Get-JsonValue $Validation "manualTuneObserverPassbandQualifiedWeakInputSampleCount"
                    $manualPassbandStrongSamples = Get-JsonValue $Validation "manualTuneObserverPassbandQualifiedStrongInputSampleCount"
                    $manualObserverReason = "Live history mixed weak/strong evidence is not ready, but the read-only manual-tune observer captured a safe mixed-ready G2 window: status='$manualObserverStatus', bestFrequencyHz=$manualBestFrequencyHz, bestStatus='$manualBestStatus', reportPath='$manualBestReportPath', jsonlPath='$manualBestJsonlPath', weakSamples=$manualWeakSamples, strongSamples=$manualStrongSamples, nearStrongSamples=$manualNearStrongSamples, speechWeakStrong=$manualSpeechWeakSamples/$manualSpeechStrongSamples, passbandWeakStrong=$manualPassbandWeakSamples/$manualPassbandStrongSamples, referencedCaptures=$manualObserverReferencedCaptureReadyCount/$manualObserverReferencedCaptureCount, readOnly=$manualObserverSafetyReadOnly, apiWrites=$manualObserverSafetyApiWrites, retune=$manualObserverSafetyRetune, vfoWrites=$manualObserverVfoWrites, radioLoWrites=$manualObserverRadioLoWrites, txTouched=$manualObserverSafetyTxTouched. Promote that validated watcher summary into schema-v14 live history before tuning DSP behavior."
                    $manualExpectedArtifacts = @(
                        'artifacts/manual-tune-observer-report.json',
                        $manualBestReportPath,
                        $manualBestJsonlPath,
                        'artifacts/live-diagnostics-history.json',
                        'validation-report.json',
                        'validation-triage-report.json',
                        'validation-triage-report.md'
                    ) | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }
                    $manualCommandSteps = @(
                        'powershell -NoProfile -ExecutionPolicy Bypass -File tools\summarize-dsp-live-diagnostics-history.ps1 -BundleDir "$bundleDir" -ReportPath "$bundleDir\artifacts\live-diagnostics-history.json"',
                        'powershell -NoProfile -ExecutionPolicy Bypass -File tools\validate-dsp-modernization-bundle.ps1 -BundleDir "$bundleDir" -RequireArtifactFiles -ReportPath "$bundleDir\validation-report.json"',
                        'powershell -NoProfile -ExecutionPolicy Bypass -File tools\summarize-dsp-modernization-validation-report.ps1 -BundleDir "$bundleDir" -ReportPath "$bundleDir\validation-triage-report.json" -MarkdownPath "$bundleDir\validation-triage-report.md" -FailOnIssues'
                    )

                    $Actions.Add((New-AcceptanceActionRecord `
                                -ActionId "promote-manual-observer-mixed-weak-strong-window" `
                                -Priority 78 `
                                -StageId "opt-in-candidate-comparison" `
                                -GateId $gateId `
                                -Category "live-diagnostics" `
                                -RequiredForAcceptance:$gateRequired `
                                -BlocksDefaultChange:$true `
                                -Reason $manualObserverReason `
                                -CommandSteps $manualCommandSteps `
                                -ManualAction "Use the validated read-only manual observer capture at $manualBestFrequencyHz Hz as the live-history source. If the signal has moved and recapture is needed, keep manually tuning and rerun watch-dsp-manual-tune-observer; do not use retune/VFO-writing tools for this manual-observer promotion path." `
                                -ExpectedArtifact 'artifacts/live-diagnostics-history.json' `
                                -ExpectedArtifacts $manualExpectedArtifacts `
                                -FollowUp "Acceptance remains blocked until strict validation reports liveDiagnosticsHistoryMixedWeakStrongEvidenceReady=true and the G2/Thetis/current-Zeus comparisons still pass.")) | Out-Null
                }
                elseif ($manualObserverNearStrongCandidateReady) {
                    $manualNearStrongReason = "Live history mixed weak/strong evidence is not ready, but the read-only manual-tune observer captured a near-strong weak-only G2 window: candidateCount=$manualNearStrongCandidateCount, bestCandidateVfoHz=$manualNearStrongVfoHz, bestCandidateVfoMhz=$manualNearStrongVfoMhz, distanceToStrongThresholdDb=$manualNearStrongDistance, nearStrongSamples=$manualNearStrongSampleCount, speechNearStrongSamples=$manualNearStrongSpeechSampleCount, passbandNearStrongSamples=$manualNearStrongPassbandSampleCount, weakOnlyCaptures=$manualNearStrongReadyWeakOnlyCount/$manualNearStrongWeakOnlyCount, missingStrongCaptures=$manualNearStrongMissingStrongCount, statusCounts=$manualNearStrongStatusCountsText, reportPath='$manualNearStrongReportPath'. Recapture this exact manual window with longer dwell until strict strongInputSampleCount becomes non-zero before tuning DSP behavior."
                    $manualNearStrongExpectedArtifacts = @(
                        'artifacts/manual-tune-observer-report.json',
                        $manualNearStrongReportPath,
                        'artifacts/live-diagnostics-history.json',
                        'validation-report.json',
                        'validation-triage-report.json',
                        'validation-triage-report.md'
                    ) | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }
                    $manualNearStrongCommandSteps = @(
                        'powershell -NoProfile -ExecutionPolicy Bypass -File tools\watch-dsp-manual-tune-observer.ps1 -BaseUrl http://127.0.0.1:6060 -BundleDir "$bundleDir" -OutputRoot "$bundleDir\artifacts\manual-tune-observer" -ReportPath "$bundleDir\artifacts\manual-tune-observer-report.json" -PollCount 120 -StablePolls 2 -MaxCaptures 4 -MaxCapturesPerVfo 2 -RequireFrontendNearPassband -AllowStaleSceneCapture -CaptureSamples 32 -CaptureIntervalMs 250 -ContinueOnError',
                        'powershell -NoProfile -ExecutionPolicy Bypass -File tools\summarize-dsp-live-diagnostics-history.ps1 -BundleDir "$bundleDir" -ReportPath "$bundleDir\artifacts\live-diagnostics-history.json"',
                        'powershell -NoProfile -ExecutionPolicy Bypass -File tools\validate-dsp-modernization-bundle.ps1 -BundleDir "$bundleDir" -RequireArtifactFiles -ReportPath "$bundleDir\validation-report.json"',
                        'powershell -NoProfile -ExecutionPolicy Bypass -File tools\summarize-dsp-modernization-validation-report.ps1 -BundleDir "$bundleDir" -ReportPath "$bundleDir\validation-triage-report.json" -MarkdownPath "$bundleDir\validation-triage-report.md" -FailOnIssues'
                    )

                    $Actions.Add((New-AcceptanceActionRecord `
                                -ActionId "recapture-manual-observer-near-strong-window" `
                                -Priority 78 `
                                -StageId "opt-in-candidate-comparison" `
                                -GateId $gateId `
                                -Category "live-diagnostics" `
                                -RequiredForAcceptance:$gateRequired `
                                -BlocksDefaultChange:$true `
                                -Reason $manualNearStrongReason `
                                -CommandSteps $manualNearStrongCommandSteps `
                                -ManualAction "Stay manually tuned near $manualNearStrongVfoMhz MHz; extend dwell/recapture until strict strongInputSampleCount becomes non-zero. Keep this path read-only: no retune/VFO-writing tools." `
                                -ExpectedArtifact 'artifacts/manual-tune-observer-report.json' `
                                -ExpectedArtifacts $manualNearStrongExpectedArtifacts `
                                -FollowUp "If the recapture becomes mixed-ready, promote it through live history and rerun strict validation. If it stays weak-only/near-strong, preserve it as scouting evidence and continue collecting active windows.")) | Out-Null
                }
                else {
                    $manualObservedVfoCount = Get-IntegerValueOrDefault (Get-JsonValue $Validation "manualTuneObserverObservedVfoCount")
                    $manualBestObservedVfo = Get-JsonValue $Validation "manualTuneObserverBestObservedVfo"
                    $manualBestObservedVfoHz = Get-JsonValue $Validation "manualTuneObserverBestObservedVfoHz"
                    $manualBestObservedVfoMhz = Get-JsonValue $Validation "manualTuneObserverBestObservedVfoMhz"
                    $manualBestObservedVfoStatus = [string](Get-JsonValue $Validation "manualTuneObserverBestObservedVfoStatus")
                    $manualBestObservedVfoScore = Get-JsonValue $Validation "manualTuneObserverBestObservedVfoScore"
                    $manualBestObservedVfoHzNumber = 0.0
                    $manualBestObservedHasVfo =
                        [double]::TryParse([string]$manualBestObservedVfoHz, [ref]$manualBestObservedVfoHzNumber) -and
                        $manualBestObservedVfoHzNumber -gt 0.0
                    $manualBestObservedSuggestedVfoHz = Get-JsonValue $Validation "manualTuneObserverBestObservedVfoSuggestedVfoHz"
                    $manualBestObservedSuggestedVfoMhz = Get-JsonValue $Validation "manualTuneObserverBestObservedVfoSuggestedVfoMhz"
                    $manualBestObservedSuggestedShiftHz = Get-JsonValue $Validation "manualTuneObserverBestObservedVfoSuggestedDialShiftHz"
                    $manualBestObservedSuggestedReason = [string](Get-JsonValue $Validation "manualTuneObserverBestObservedVfoSuggestedTuneReason")
                    $manualBestObservedSuggestedVfoHzNumber = 0.0
                    $manualBestObservedSuggestedVfoMhzNumber = 0.0
                    $manualBestObservedHasSuggestedVfo =
                        [double]::TryParse([string]$manualBestObservedSuggestedVfoHz, [ref]$manualBestObservedSuggestedVfoHzNumber) -and
                        [double]::TryParse([string]$manualBestObservedSuggestedVfoMhz, [ref]$manualBestObservedSuggestedVfoMhzNumber) -and
                        $manualBestObservedSuggestedVfoHzNumber -gt 0.0 -and
                        $manualBestObservedSuggestedVfoMhzNumber -gt 0.0
                    $manualObserverScoutingMetadataUsable =
                        $manualObserverPresent -and
                        $manualObserverSchemaVersion -ge 1 -and
                        $manualObserverTool -eq "watch-dsp-manual-tune-observer" -and
                        $manualObserverOk -and
                        [string]::IsNullOrWhiteSpace($manualObserverScanError) -and
                        $manualObserverBundleRelativePaths
                    $manualObserverObservedTargetNoCapture =
                        (-not $manualObserverValid) -and
                        $manualObserverCaptureCount -eq 0 -and
                        $manualObserverReferencedCaptureCount -eq 0 -and
                        $manualObserverReferencedCaptureReadyCount -eq 0 -and
                        $manualObserverReferencedCaptureProblemCount -eq 0
                    $manualObserverObservedTargetSourceUsable =
                        $manualObserverValid -or
                        $manualObserverObservedTargetNoCapture
                    $manualObserverHasObservedTarget =
                        $manualObserverScoutingMetadataUsable -and
                        $manualObserverObservedTargetSourceUsable -and
                        $manualObserverSafetyRxOnly -and
                        $manualObserverSafetyReadOnly -and
                        (-not $manualObserverSafetyApiWrites) -and
                        (-not $manualObserverSafetyRetune) -and
                        (-not $manualObserverSafetyTxTouched) -and
                        $manualObserverVfoWrites -eq 0 -and
                        $manualObserverRadioLoWrites -eq 0 -and
                        $manualObservedVfoCount -gt 0 -and
                        $null -ne $manualBestObservedVfo -and
                        $manualBestObservedHasVfo

                    if ($manualObserverHasObservedTarget) {
                        $manualObservedTuneText = if ($manualBestObservedHasSuggestedVfo) {
                            "suggestedVfoMhz=$manualBestObservedSuggestedVfoMhz, suggestedVfoHz=$manualBestObservedSuggestedVfoHz, shiftHz=$manualBestObservedSuggestedShiftHz, reason='$manualBestObservedSuggestedReason'"
                        }
                        else {
                            "observedVfoHz=$manualBestObservedVfoHz, observedVfoMhz=$manualBestObservedVfoMhz"
                        }
                        $manualObservedReason = "Live history mixed weak/strong evidence is not ready, and the read-only manual-tune observer did not produce a promotable mixed-ready capture. It did identify an observed manual VFO target: reportValid=$manualObserverValid, captureCount=$manualObserverCaptureCount, referencedCaptures=$manualObserverReferencedCaptureReadyCount/$manualObserverReferencedCaptureCount, referencedProblems=$manualObserverReferencedCaptureProblemCount, observedVfoCount=$manualObservedVfoCount, bestObservedVfoHz=$manualBestObservedVfoHz, status='$manualBestObservedVfoStatus', score=$manualBestObservedVfoScore, $manualObservedTuneText. Use this as the next operator-tuned capture target; it is scouting guidance, not acceptance evidence."
                        $manualObservedCommandSteps = @(
                            'powershell -NoProfile -ExecutionPolicy Bypass -File tools\watch-dsp-manual-tune-observer.ps1 -BaseUrl http://127.0.0.1:6060 -BundleDir "$bundleDir" -OutputRoot "$bundleDir\artifacts\manual-tune-observer" -ReportPath "$bundleDir\artifacts\manual-tune-observer-report.json" -PollCount 90 -StablePolls 2 -MaxCaptures 4 -MaxCapturesPerVfo 2 -RequireFrontendNearPassband -AllowStaleSceneCapture -CaptureSamples 24 -CaptureIntervalMs 250 -ContinueOnError',
                            'powershell -NoProfile -ExecutionPolicy Bypass -File tools\summarize-dsp-live-diagnostics-history.ps1 -BundleDir "$bundleDir" -ReportPath "$bundleDir\artifacts\live-diagnostics-history.json"',
                            'powershell -NoProfile -ExecutionPolicy Bypass -File tools\validate-dsp-modernization-bundle.ps1 -BundleDir "$bundleDir" -RequireArtifactFiles -ReportPath "$bundleDir\validation-report.json"',
                            'powershell -NoProfile -ExecutionPolicy Bypass -File tools\summarize-dsp-modernization-validation-report.ps1 -BundleDir "$bundleDir" -ReportPath "$bundleDir\validation-triage-report.json" -MarkdownPath "$bundleDir\validation-triage-report.md" -FailOnIssues'
                        )
                        $manualObservedActionText = if ($manualBestObservedHasSuggestedVfo) {
                            "Manually tune G2 near $manualBestObservedSuggestedVfoMhz MHz (shift $manualBestObservedSuggestedShiftHz Hz from the observed VFO; reason '$manualBestObservedSuggestedReason'), wait for stable active audio, then rerun the manual observer command. Do not use retune/VFO-writing tools on this path."
                        }
                        else {
                            "Manually keep G2 near the best observed VFO $manualBestObservedVfoHz Hz, wait for stable active audio, then rerun the manual observer command. Do not use retune/VFO-writing tools on this path."
                        }

                        $Actions.Add((New-AcceptanceActionRecord `
                                    -ActionId "capture-manual-observer-best-observed-vfo" `
                                    -Priority 78 `
                                    -StageId "opt-in-candidate-comparison" `
                                    -GateId $gateId `
                                    -Category "live-diagnostics" `
                                    -RequiredForAcceptance:$gateRequired `
                                    -BlocksDefaultChange:$true `
                                    -Reason $manualObservedReason `
                                    -CommandSteps $manualObservedCommandSteps `
                                    -ManualAction $manualObservedActionText `
                                    -ExpectedArtifact 'artifacts/manual-tune-observer-report.json' `
                                    -ExpectedArtifacts @(
                                        'artifacts/manual-tune-observer-report.json',
                                        'artifacts/live-diagnostics-history.json',
                                        'validation-report.json',
                                        'validation-triage-report.json',
                                        'validation-triage-report.md'
                                    ) `
                                    -FollowUp "If the follow-up manual observer produces a mixed-ready capture, promote it through live history and rerun strict validation. If it remains tuning-hint or weak-only evidence, keep it as scouting data and continue collecting active windows.")) | Out-Null
                    }
                $peakHuntReason = ""
                $peakHuntManualContext = ""
                if (Test-Truthy (Get-JsonValue $Validation "g2RxPeakHuntReportPresent")) {
                    $peakHuntStatus = [string](Get-JsonValue $Validation "g2RxPeakHuntReportStatus")
                    $peakHuntWeakSamples = Get-IntegerValueOrDefault (Get-JsonValue $Validation "g2RxPeakHuntWeakInputSampleCount")
                    $peakHuntStrongSamples = Get-IntegerValueOrDefault (Get-JsonValue $Validation "g2RxPeakHuntStrongInputSampleCount")
                    $peakHuntBestFrequencyHz = [string](Get-JsonValue $Validation "g2RxPeakHuntBestFrequencyHz")
                    $peakHuntBestScore = Get-JsonValue $Validation "g2RxPeakHuntBestScore"
                    $peakHuntBestStatus = [string](Get-JsonValue $Validation "g2RxPeakHuntBestStatus")
                    $peakHuntBestReportPath = [string](Get-JsonValue $Validation "g2RxPeakHuntBestReportPath")
                    $peakHuntWindowReadyCount = Get-IntegerValueOrDefault (Get-JsonValue $Validation "g2RxPeakHuntReferencedWindowReadyCount")
                    $peakHuntWindowCount = Get-IntegerValueOrDefault (Get-JsonValue $Validation "g2RxPeakHuntReferencedWindowCount")
                    $peakHuntProblemCount = Get-IntegerValueOrDefault (Get-JsonValue $Validation "g2RxPeakHuntReferencedWindowProblemCount")
                    $peakHuntReason = " Latest G2 peak-hunt report status='$peakHuntStatus', weakSamples=$peakHuntWeakSamples, strongSamples=$peakHuntStrongSamples, referencedWindows=$peakHuntWindowReadyCount/$peakHuntWindowCount, referencedProblems=$peakHuntProblemCount, bestFrequencyHz=$peakHuntBestFrequencyHz, bestScore=$peakHuntBestScore, bestStatus='$peakHuntBestStatus', bestReportPath='$peakHuntBestReportPath'."
                    if ($peakHuntStatus -eq "weak-only" -or $peakHuntBestStatus -eq "missing-strong-input" -or ($peakHuntWeakSamples -gt 0 -and $peakHuntStrongSamples -le 0)) {
                        $peakHuntReason += " That weak-only or missing-strong-input scan is useful scouting evidence, but it cannot satisfy mixed weak+strong acceptance."
                        $peakHuntManualContext = " The latest G2 peak hunt was weak-only/missing strong input, so keep scanning or retune to a window with both weak and strong speech before promotion."
                    }
                    elseif ($peakHuntStatus -eq "invalid") {
                        $peakHuntReason += " The present peak-hunt report is invalid; recapture it with bundle-relative per-window watcher JSON/JSONL before using it as promotion evidence."
                        $peakHuntManualContext = " The present G2 peak-hunt report is invalid, so recapture it into the bundle before promoting a window."
                    }
                }
                $peakHuntCommandSteps = @(
                    'powershell -NoProfile -ExecutionPolicy Bypass -File tools\watch-dsp-manual-tune-observer.ps1 -BaseUrl http://127.0.0.1:6060 -BundleDir "$bundleDir" -OutputRoot "$bundleDir\artifacts\manual-tune-observer" -ReportPath "$bundleDir\artifacts\manual-tune-observer-report.json" -PollCount 60 -StablePolls 3 -MaxCaptures 4 -MaxCapturesPerVfo 2 -RequireFrontendNearPassband -AllowStaleSceneCapture -CaptureSamples 24 -CaptureIntervalMs 250 -ContinueOnError',
                    'powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-g2-rx-peak-hunt.ps1 -BaseUrl http://127.0.0.1:6060 -OutputRoot "$bundleDir\artifacts\g2-rx-peak-hunt" -ReportPath "$bundleDir\artifacts\g2-rx-peak-hunt-report.json" -AllowRetune -StopOnReady -SamplesPerWindow 24 -IntervalMs 250 -MaxPeaks 6',
                    'powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-live-diagnostics-matrix.ps1 -BundleDir "$bundleDir" -ComparisonId nr5-spnr -IndexPath "$bundleDir\artifacts\live-diagnostics-trace-index.mixed-weak-strong-followup.json" -ReportPath "$bundleDir\artifacts\live-diagnostics-matrix-report.mixed-weak-strong-followup.json" -Samples 90 -IntervalMs 1000 -ContinueOnError',
                    'powershell -NoProfile -ExecutionPolicy Bypass -File tools\summarize-dsp-live-diagnostics-history.ps1 -BundleDir "$bundleDir" -ReportPath "$bundleDir\artifacts\live-diagnostics-history.json"',
                    'powershell -NoProfile -ExecutionPolicy Bypass -File tools\validate-dsp-modernization-bundle.ps1 -BundleDir "$bundleDir" -RequireArtifactFiles -ReportPath "$bundleDir\validation-report.json"',
                    'powershell -NoProfile -ExecutionPolicy Bypass -File tools\summarize-dsp-modernization-validation-report.ps1 -BundleDir "$bundleDir" -ReportPath "$bundleDir\validation-triage-report.json" -MarkdownPath "$bundleDir\validation-triage-report.md" -FailOnIssues'
                )
                $Actions.Add((New-AcceptanceActionRecord `
                            -ActionId "capture-mixed-weak-strong-live-history" `
                            -Priority 79 `
                            -StageId "opt-in-candidate-comparison" `
                            -GateId $gateId `
                            -Category "live-diagnostics" `
                            -RequiredForAcceptance:$gateRequired `
                            -BlocksDefaultChange:$true `
                            -Reason "$reason$peakHuntReason" `
                            -CommandSteps $peakHuntCommandSteps `
                            -ManualAction "Use G2 on an active SSB/CW-adjacent window with both weak and strong speech samples. If the operator is tuning manually, start with watch-dsp-manual-tune-observer so no VFO/LO writes occur; otherwise use run-dsp-g2-rx-peak-hunt to scan current frontend peaks and omit -AllowRetune for a current-VFO-only dry run.$peakHuntManualContext Then capture NR5/SPNR live diagnostics, regenerate artifacts/live-diagnostics-history.json, and rerun strict validation. The v14 history must report mixedWeakStrongEvidenceStatus=ready." `
                            -ExpectedArtifact 'artifacts/live-diagnostics-history.json' `
                            -ExpectedArtifacts @(
                                'artifacts/manual-tune-observer-report.json',
                                'artifacts/g2-rx-peak-hunt-report.json',
                                'artifacts/live-diagnostics-trace-index.mixed-weak-strong-followup.json',
                                'artifacts/live-diagnostics-matrix-report.mixed-weak-strong-followup.json',
                                'artifacts/live-diagnostics-history.json',
                                'validation-report.json',
                                'validation-triage-report.json',
                                'validation-triage-report.md'
                            ) `
                            -FollowUp "Do not treat weak-only or quiet/intermittent captures as volume-normalization proof; they remain useful for weak-signal tuning but not for acceptance.")) | Out-Null
                }
            }
        }
        "live-history-provenance" {
            $gateStatus = [string](Get-JsonValue $Gate "status")
            $isCoverageRepair = $gateStatus.StartsWith("coverage-", [StringComparison]::OrdinalIgnoreCase)
            $historyCommandSteps = @()
            if ($isCoverageRepair) {
                $historyCommandSteps = @(
                    'powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-live-diagnostics-matrix.ps1 -BundleDir "$bundleDir" -ComparisonId off-baseline -IndexPath "$bundleDir\artifacts\live-diagnostics-trace-index.off-baseline.json" -ReportPath "$bundleDir\artifacts\live-diagnostics-matrix-report.off-baseline.json" -Samples 60 -IntervalMs 1000',
                    'powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-live-diagnostics-matrix.ps1 -BundleDir "$bundleDir" -ComparisonId thetis-parity -IndexPath "$bundleDir\artifacts\live-diagnostics-trace-index.thetis-parity.json" -ReportPath "$bundleDir\artifacts\live-diagnostics-matrix-report.thetis-parity.json" -Samples 60 -IntervalMs 1000',
                    'powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-live-diagnostics-matrix.ps1 -BundleDir "$bundleDir" -ComparisonId current-zeus -IndexPath "$bundleDir\artifacts\live-diagnostics-trace-index.baseline.json" -ReportPath "$bundleDir\artifacts\live-diagnostics-matrix-report.baseline.json" -Samples 60 -IntervalMs 1000',
                    'powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-live-diagnostics-matrix.ps1 -BundleDir "$bundleDir" -ComparisonId nr5-spnr -IndexPath "$bundleDir\artifacts\live-diagnostics-trace-index.candidate.json" -ReportPath "$bundleDir\artifacts\live-diagnostics-matrix-report.candidate.json" -Samples 60 -IntervalMs 1000',
                    'powershell -NoProfile -ExecutionPolicy Bypass -File tools\summarize-dsp-live-diagnostics-history.ps1 -BundleDir "$bundleDir" -ReportPath "$bundleDir\artifacts\live-diagnostics-history.json"'
                )
            }
            else {
                $historyCommandSteps = @(
                    'powershell -NoProfile -ExecutionPolicy Bypass -File tools\summarize-dsp-live-diagnostics-history.ps1 -BundleDir "$bundleDir" -ReportPath "$bundleDir\artifacts\live-diagnostics-history.json"'
                )
            }
            $Actions.Add((New-AcceptanceActionRecord `
                        -ActionId "regenerate-live-diagnostics-history" `
                        -Priority 80 `
                        -StageId "opt-in-candidate-comparison" `
                        -GateId $gateId `
                        -Category "live-diagnostics" `
                        -RequiredForAcceptance:$gateRequired `
                        -BlocksDefaultChange:$false `
                        -Reason $reason `
                        -CommandSteps $historyCommandSteps `
                        -ExpectedArtifact 'artifacts/live-diagnostics-history.json' `
                        -FollowUp "Use history only to choose candidate comparison windows; it does not approve defaults. Coverage repair must capture off-baseline, Thetis, current-Zeus, and NR5/SPNR before regenerating history.")) | Out-Null
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
        [Parameter(Mandatory = $true)]$AcceptanceReadiness,
        $Validation = $null
    )

    $actions = New-Object System.Collections.Generic.List[object]
    foreach ($gate in @($EvidenceGates)) {
        if (Test-Truthy (Get-JsonValue $gate "ready")) {
            continue
        }

        Add-AcceptanceActionForGate -Actions $actions -Gate $gate -Validation $Validation
    }

    if ($null -ne $Validation) {
        $liveAcceptanceArtifactProblems = @(Get-LiveAcceptanceArtifactProblemRecords -Validation $Validation)
        $hasLiveMatrixAction = @($actions.ToArray() | Where-Object {
                [string](Get-JsonValue $_ "actionId") -eq "capture-and-compare-live-matrix"
            }).Count -gt 0
        if ($liveAcceptanceArtifactProblems.Count -gt 0 -and -not $hasLiveMatrixAction) {
            $liveArtifactReason = "Required live acceptance artifacts are missing: $(Format-LiveAcceptanceArtifactProblemIds -Records $liveAcceptanceArtifactProblems). Capture the full live matrix, regenerate the required-live manifest, rerun strict validation, and review validation triage."
            Add-LiveMatrixAcceptanceAction -Actions $actions -GateId "validation-report" -Reason $liveArtifactReason
        }

        $externalBakeoffPresent = Test-Truthy (Get-JsonValue $Validation "externalEngineBakeoffReportPresent")
        $externalBakeoffReady = Test-Truthy (Get-JsonValue $Validation "externalEngineBakeoffReady")
        $externalFirstSafeCandidateId = [string](Get-JsonValue $Validation "externalEngineBakeoffFirstSafeCandidateId")
        if ($externalBakeoffPresent -and $externalBakeoffReady -and -not [string]::IsNullOrWhiteSpace($externalFirstSafeCandidateId) -and
            -not (Test-AcceptanceActionExists -Actions $actions -ActionId "run-first-safe-external-engine-bakeoff")) {
            $externalFirstSafeScenarioIds = @(Get-JsonArray $Validation "externalEngineBakeoffFirstSafeScenarioIds" | ForEach-Object { [string]$_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
            if ($externalFirstSafeScenarioIds.Count -eq 0) {
                $externalFirstSafeScenarioIds = @(Get-JsonArray $Validation "externalEngineBakeoffPlanScenarioIds" | ForEach-Object { [string]$_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
            }
            $scenarioArgument = if ($externalFirstSafeScenarioIds.Count -gt 0) {
                $externalFirstSafeScenarioIds -join ","
            }
            else {
                "<external-bakeoff-scenario-ids>"
            }
            $scenarioRunnerArgument = if ($externalFirstSafeScenarioIds.Count -gt 0) {
                $externalFirstSafeScenarioIds -join " "
            }
            else {
                "<external-bakeoff-scenario-ids>"
            }
            $fixtureTemplate = [string](Get-JsonValue $Validation "externalEngineBakeoffPlanFixtureComparisonCommandTemplate")
            $liveTemplate = [string](Get-JsonValue $Validation "externalEngineBakeoffPlanLiveMatrixCommandTemplate")
            $evaluationOrderIds = Join-ExternalCandidateIds @(Get-JsonArray $Validation "externalEngineBakeoffEvaluationOrderCandidateIds")
            $externalBakeoffReason = "External DSP/ML bakeoff report is ready. First safe opt-in candidate is '$externalFirstSafeCandidateId'; evaluation order is '$evaluationOrderIds'; first-safe scenarios are '$scenarioArgument'."
            if (-not [string]::IsNullOrWhiteSpace($fixtureTemplate)) {
                $externalBakeoffReason = "$externalBakeoffReason Fixture template: $fixtureTemplate."
            }
            if (-not [string]::IsNullOrWhiteSpace($liveTemplate)) {
                $externalBakeoffReason = "$externalBakeoffReason Live template: $liveTemplate."
            }

            $externalCommandSteps = @(
                "powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-external-engine-bakeoff.ps1 -BundleDir `"`$bundleDir`" -CandidateId $externalFirstSafeCandidateId -ScenarioIds $scenarioRunnerArgument -PlanOnly"
            )

            $actions.Add((New-AcceptanceActionRecord `
                        -ActionId "run-first-safe-external-engine-bakeoff" `
                        -Priority 72 `
                        -StageId "external-dsp-ml-bakeoff" `
                        -GateId "external-engine-bakeoff" `
                        -Category "external-dsp-ml" `
                        -RequiredForAcceptance:$false `
                        -BlocksDefaultChange:$false `
                        -Reason $externalBakeoffReason `
                        -CommandSteps $externalCommandSteps `
                        -ManualAction "Produce or enable only the post-demod, operator-opt-in '$externalFirstSafeCandidateId' candidate path before executing the runner plan. Start with -PlanOnly; use -Execute only after fixture metrics exist and the operator has intentionally enabled the candidate path. Do not route raw WDSP IQ, TX audio, TX monitor, or PureSignal feedback through the external engine." `
                        -ExpectedArtifact "artifacts/external-engine-bakeoff-cycle-summary.json" `
                        -ExpectedArtifacts @(
                            "artifacts/external-engine-bakeoff-cycle-summary.json",
                            "artifacts/external-engine-bakeoff-cycle-summary.md"
                        ) `
                        -FollowUp "Review the bakeoff cycle summary, then execute its child plan only as exploratory opt-in evidence. External DSP/ML remains post-demod and off by default until fixture metrics, G2 live evidence, operator notes, package/license review, and cross-radio validation all pass.")) | Out-Null
        }
    }

    $g2Stage = $null
    foreach ($stage in @($AcceptanceReadiness)) {
        if ([string](Get-JsonValue $stage "stageId") -eq "g2-first-pass-evidence") {
            $g2Stage = $stage
            break
        }
    }

    $crossRadioReady = Test-Truthy (Get-JsonValue $Validation "crossRadioValidationReady")
    if ($null -ne $g2Stage -and (Test-Truthy (Get-JsonValue $g2Stage "ready")) -and -not $crossRadioReady) {
        $actions.Add((New-AcceptanceActionRecord `
                    -ActionId "capture-cross-radio-validation" `
                    -Priority 100 `
                    -StageId "default-dsp-graduation" `
                    -Category "cross-radio-validation" `
                    -RequiredForAcceptance:$true `
                    -BlocksDefaultChange:$true `
                    -Reason "Default DSP behavior cannot graduate from G2-only evidence." `
                    -ManualAction "Repeat the accepted fixture/live evidence bundle on at least one non-G2 radio target, run summarize-dsp-cross-radio-validation.ps1 with the resulting source validation report, and attach the source-backed cross-radio report before any default behavior change approval." `
                    -ExpectedArtifact "artifacts/cross-radio-validation-report.json" `
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
        $requiredProblemGateIds = @((Get-JsonArray $Report "requiredEvidenceGateProblemIds") | ForEach-Object { [string]$_ })
        $advisoryProblemGateIds = @((Get-JsonArray $Report "advisoryEvidenceGateProblemIds") | ForEach-Object { [string]$_ })
        $requiredProblemGateSummary = "none"
        if ($requiredProblemGateIds.Count -gt 0) {
            $requiredProblemGateSummary = $requiredProblemGateIds -join ', '
        }
        $advisoryProblemGateSummary = "none"
        if ($advisoryProblemGateIds.Count -gt 0) {
            $advisoryProblemGateSummary = $advisoryProblemGateIds -join ', '
        }
        $lines.Add("- Gate problems: $(Get-JsonValue $Report "evidenceGateProblemCount") total / $(Get-JsonValue $Report "requiredEvidenceGateProblemCount") required / $(Get-JsonValue $Report "advisoryEvidenceGateProblemCount") advisory") | Out-Null
        $lines.Add("- Required problem gates: $(Format-MarkdownCell $requiredProblemGateSummary)") | Out-Null
        $lines.Add("- Advisory problem gates: $(Format-MarkdownCell $advisoryProblemGateSummary)") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("| Gate | Ready | Status | Required | Detail | Action |") | Out-Null
        $lines.Add("|---|---:|---|---:|---|---|") | Out-Null
        foreach ($gate in $evidenceGates) {
            $lines.Add("| $(Format-MarkdownCell (Get-JsonValue $gate "name")) | $(Get-JsonValue $gate "ready") | $(Format-MarkdownCell (Get-JsonValue $gate "status")) | $(Get-JsonValue $gate "requiredForAcceptance") | $(Format-MarkdownCell (Get-JsonValue $gate "detail")) | $(Format-MarkdownCell (Get-JsonValue $gate "remediation")) |") | Out-Null
        }
        $lines.Add("") | Out-Null
    }

    $benchmarkMissingFamilies = @(Get-JsonArray $Report "benchmarkPlanMissingAcceptanceScenarioFamilyIds")
    $benchmarkFamilyCoverage = @(Get-JsonArray $Report "benchmarkPlanScenarioFamilyCoverage")
    if ($benchmarkFamilyCoverage.Count -gt 0 -or $benchmarkMissingFamilies.Count -gt 0) {
        $lines.Add("## Benchmark Plan Coverage") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("- Status: $(Format-MarkdownCell (Get-JsonValue $Report "benchmarkPlanStatus"))") | Out-Null
        $lines.Add("- Scenarios: $(Get-JsonValue $Report "benchmarkPlanScenarioCount")") | Out-Null
        $lines.Add("- Acceptance scenario families covered: $(Get-JsonValue $Report "benchmarkPlanCoveredAcceptanceScenarioFamilyCount") / $(Get-JsonValue $Report "benchmarkPlanRequiredAcceptanceScenarioFamilyCount")") | Out-Null
        if ($benchmarkMissingFamilies.Count -gt 0) {
            $lines.Add("- Missing acceptance scenario families: $(Format-MarkdownCell ($benchmarkMissingFamilies -join ', '))") | Out-Null
        }
        $lines.Add("- Scenarios missing requiredComparisons: $(Get-JsonValue $Report "benchmarkPlanScenarioMissingRequiredComparisonCount")") | Out-Null
        $lines.Add("- Scenarios missing requiredMetrics: $(Get-JsonValue $Report "benchmarkPlanScenarioMissingRequiredMetricCount")") | Out-Null
        $lines.Add("- Scenarios missing acceptanceGates: $(Get-JsonValue $Report "benchmarkPlanScenarioMissingAcceptanceGateCount")") | Out-Null
        $lines.Add("") | Out-Null

        $missingRows = @($benchmarkFamilyCoverage | Where-Object { -not (Test-Truthy (Get-JsonValue $_ "covered")) })
        if ($missingRows.Count -gt 0) {
            $lines.Add("| Missing Family | Accepted Scenario IDs |") | Out-Null
            $lines.Add("|---|---|") | Out-Null
            foreach ($family in $missingRows) {
                $lines.Add("| $(Format-MarkdownCell (Get-JsonValue $family "familyId")) | $(Format-MarkdownCell ((@(Get-JsonArray $family "acceptedScenarioIds")) -join ', ')) |") | Out-Null
            }
            $lines.Add("") | Out-Null
        }
    }

    $metricCatalogProblemIds = @(Get-JsonArray $Report "metricCatalogContractProblemMetricIds")
    if (([int](Get-JsonValue $Report "metricCatalogMetricCount") -gt 0) -or $metricCatalogProblemIds.Count -gt 0) {
        $lines.Add("## Metric Catalog Contract") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("- Status: $(Format-MarkdownCell (Get-JsonValue $Report "metricCatalogStatus"))") | Out-Null
        $lines.Add("- Contract ready: $(Get-JsonValue $Report "metricCatalogAcceptanceContractReady")") | Out-Null
        $lines.Add("- Metrics: $(Get-JsonValue $Report "metricCatalogMetricCount")") | Out-Null
        $lines.Add("- Required metrics: $(Get-JsonValue $Report "metricCatalogRequiredMetricCount")") | Out-Null
        $lines.Add("- Missing required metrics: $(Get-JsonValue $Report "metricCatalogMissingRequiredMetricCount")") | Out-Null
        $lines.Add("- Missing thresholds: $(Get-JsonValue $Report "metricCatalogMissingThresholdCount")") | Out-Null
        $lines.Add("- Missing comparators: $(Get-JsonValue $Report "metricCatalogMissingComparatorCount")") | Out-Null
        $lines.Add("- Invalid comparators: $(Get-JsonValue $Report "metricCatalogInvalidComparatorCount")") | Out-Null
        $lines.Add("- Missing units: $(Get-JsonValue $Report "metricCatalogMissingUnitCount")") | Out-Null
        $lines.Add("- Missing safety classes: $(Get-JsonValue $Report "metricCatalogMissingSafetyClassCount")") | Out-Null
        $lines.Add("- Missing acceptance scopes: $(Get-JsonValue $Report "metricCatalogMissingAcceptanceScopeCount")") | Out-Null
        if ($metricCatalogProblemIds.Count -gt 0) {
            $lines.Add("- Problem metric IDs: $(Format-MarkdownCell ($metricCatalogProblemIds -join ', '))") | Out-Null
        }
        $lines.Add("") | Out-Null
    }

    $externalCandidateMissingIds = @(Get-JsonArray $Report "externalEngineCandidateMissingIds")
    $externalCandidateUnsafeDetails = @(Get-JsonArray $Report "externalEngineCandidateUnsafeDetails")
    $externalBakeoffMissingIds = @(Get-JsonArray $Report "externalEngineBakeoffMissingCandidateIds")
    $externalBakeoffUnsafeDetails = @(Get-JsonArray $Report "externalEngineBakeoffUnsafeCandidateDetails")
    $externalBakeoffBlockedDetails = @(Get-JsonArray $Report "externalEngineBakeoffBlockedCandidateDetails")
    $externalCandidateIssueCounts = @(Get-JsonArray $Report "externalEngineCandidateIssueCounts")
    $externalBakeoffIssueCounts = @(Get-JsonArray $Report "externalEngineBakeoffCandidateIssueCounts")
    if (([int](Get-JsonValue $Report "externalEngineCandidateCount") -gt 0) -or
        (Test-Truthy (Get-JsonValue $Report "externalEngineBakeoffRequiredByScope")) -or
        (Test-Truthy (Get-JsonValue $Report "externalEngineBakeoffReportPresent")) -or
        (Test-Truthy (Get-JsonValue $Report "externalEngineBakeoffCycleSummaryPresent")) -or
        $externalCandidateMissingIds.Count -gt 0 -or
        $externalBakeoffMissingIds.Count -gt 0 -or
        $externalCandidateUnsafeDetails.Count -gt 0 -or
        $externalBakeoffUnsafeDetails.Count -gt 0) {
        $lines.Add("## External DSP/ML Evidence") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("- Candidate catalog status: $(Format-MarkdownCell (Get-JsonValue $Report "externalEngineCandidateStatus"))") | Out-Null
        $lines.Add("- Catalog missing IDs: $(Format-MarkdownCell ((@(Get-ExternalCandidateIds $externalCandidateMissingIds)) -join ', '))") | Out-Null
        $lines.Add("- Catalog unsafe IDs: $(Format-MarkdownCell ((@(Get-ExternalCandidateIds (Get-JsonArray $Report "externalEngineCandidateUnsafeIds"))) -join ', '))") | Out-Null
        $lines.Add("- Catalog top issue: $(Format-MarkdownCell (Get-ExternalIssueTopText $externalCandidateIssueCounts))") | Out-Null
        $lines.Add("- Bakeoff required/present/ready: $(Get-JsonValue $Report "externalEngineBakeoffRequiredByScope") / $(Get-JsonValue $Report "externalEngineBakeoffReportPresent") / $(Get-JsonValue $Report "externalEngineBakeoffReady")") | Out-Null
        $lines.Add("- Bakeoff scope triggers: $(Format-MarkdownCell ((@(Get-JsonArray $Report "externalEngineBakeoffScopeTriggers") | ForEach-Object { [string]$_ }) -join ', '))") | Out-Null
        $lines.Add("- Bakeoff first safe candidate: $(Format-MarkdownCell (Get-JsonValue $Report "externalEngineBakeoffFirstSafeCandidateId"))") | Out-Null
        $lines.Add("- Bakeoff first safe scenarios: $(Format-MarkdownCell ((@(Get-JsonArray $Report "externalEngineBakeoffFirstSafeScenarioIds") | ForEach-Object { [string]$_ }) -join ', '))") | Out-Null
        $lines.Add("- Bakeoff evaluation order: $(Format-MarkdownCell ((@(Get-JsonArray $Report "externalEngineBakeoffEvaluationOrderCandidateIds") | ForEach-Object { [string]$_ }) -join ', '))") | Out-Null
        $lines.Add("- Bakeoff fixture command template: $(Format-MarkdownCell (Get-JsonValue $Report "externalEngineBakeoffPlanFixtureComparisonCommandTemplate"))") | Out-Null
        $lines.Add("- Bakeoff live matrix command template: $(Format-MarkdownCell (Get-JsonValue $Report "externalEngineBakeoffPlanLiveMatrixCommandTemplate"))") | Out-Null
        if (Test-Truthy (Get-JsonValue $Report "externalEngineBakeoffCycleSummaryPresent")) {
            $lines.Add("- Bakeoff cycle summary: status $(Format-MarkdownCell (Get-JsonValue $Report "externalEngineBakeoffCycleStatus")) / valid $(Get-JsonValue $Report "externalEngineBakeoffCycleSummaryValid") / mode $(Format-MarkdownCell (Get-JsonValue $Report "externalEngineBakeoffCycleMode")) / candidate $(Format-MarkdownCell (Get-JsonValue $Report "externalEngineBakeoffCycleCandidateId")) / ready $(Get-JsonValue $Report "externalEngineBakeoffCycleReadyToExecute") / executed $(Get-JsonValue $Report "externalEngineBakeoffCycleExecuted") / missing prereqs $(Get-JsonValue $Report "externalEngineBakeoffCycleMissingPrerequisiteCount") / nonzero exits $(Get-JsonValue $Report "externalEngineBakeoffCycleNonZeroExitCount") / path $(Format-MarkdownCell (Get-JsonValue $Report "externalEngineBakeoffCyclePath"))") | Out-Null
        }
        $lines.Add("- Bakeoff missing IDs: $(Format-MarkdownCell ((@(Get-ExternalCandidateIds $externalBakeoffMissingIds)) -join ', '))") | Out-Null
        $lines.Add("- Bakeoff unsafe IDs: $(Format-MarkdownCell ((@(Get-ExternalCandidateIds (Get-JsonArray $Report "externalEngineBakeoffUnsafeCandidateIds"))) -join ', '))") | Out-Null
        $lines.Add("- Bakeoff blocked-for-integration IDs: $(Format-MarkdownCell ((@(Get-ExternalCandidateIds (Get-JsonArray $Report "externalEngineBakeoffBlockedCandidateIds"))) -join ', '))") | Out-Null
        $lines.Add("- Bakeoff top issue: $(Format-MarkdownCell (Get-ExternalIssueTopText $externalBakeoffIssueCounts))") | Out-Null
        $lines.Add("") | Out-Null

        $externalDetailRows = @($externalCandidateUnsafeDetails + $externalBakeoffUnsafeDetails + $externalBakeoffBlockedDetails)
        if ($externalDetailRows.Count -gt 0) {
            $lines.Add("| Candidate | Source | Issues | Blockers |") | Out-Null
            $lines.Add("|---|---|---|---|") | Out-Null
            foreach ($detail in $externalDetailRows) {
                $source = if (Test-Truthy (Get-JsonValue $detail "safeForBakeoff")) { "blocked-for-integration" } else { "unsafe-or-catalog" }
                $lines.Add("| $(Format-MarkdownCell (Get-JsonValue $detail "id")) | $(Format-MarkdownCell $source) | $(Format-MarkdownCell ((@(Get-JsonArray $detail "issues") | Select-Object -First 4) -join ', ')) | $(Format-MarkdownCell ((@(Get-JsonArray $detail "blockers") | Select-Object -First 3) -join ', ')) |") | Out-Null
            }
            $lines.Add("") | Out-Null
        }
    }

    if ((Test-Truthy (Get-JsonValue $Report "wdspSourceDriftReportPresent")) -or
        -not (Test-Truthy (Get-JsonValue $Report "wdspSourceDriftReportReady"))) {
        $lines.Add("## WDSP Source Drift") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("- Present/ready/status: $(Get-JsonValue $Report "wdspSourceDriftReportPresent") / $(Get-JsonValue $Report "wdspSourceDriftReportReady") / $(Format-MarkdownCell (Get-JsonValue $Report "wdspSourceDriftReportStatus"))") | Out-Null
        $lines.Add("- Files reference/candidate/total: $(Get-JsonValue $Report "wdspSourceDriftReferenceFileCount") / $(Get-JsonValue $Report "wdspSourceDriftCandidateFileCount") / $(Get-JsonValue $Report "wdspSourceDriftFileCount")") | Out-Null
        $lines.Add("- Deltas / likely defects: $(Get-JsonValue $Report "wdspSourceDriftDeltaCount") / $(Get-JsonValue $Report "wdspSourceDriftLikelyDefectCount")") | Out-Null
        $lines.Add("- Normalized line endings: $(Get-JsonValue $Report "wdspSourceDriftReportNormalizedLineEndings")") | Out-Null
        $lines.Add("- Report path: $(Format-MarkdownCell (Get-JsonValue $Report "wdspSourceDriftReportPath"))") | Out-Null
        $lines.Add("") | Out-Null
    }

    $fixtureMissingScenarios = @(Get-JsonArray $Report "metricComparisonMissingScenarios")
    $fixtureMissingScenarioCount = [int](Get-JsonValue $Report "metricComparisonMissingScenarioCount")
    $fixtureMissingCurrentCount = [int](Get-JsonValue $Report "metricComparisonMissingCurrentBaselineCount")
    $fixtureMissingThetisCount = [int](Get-JsonValue $Report "metricComparisonMissingThetisBaselineCount")
    $fixtureMissingCandidateCount = [int](Get-JsonValue $Report "metricComparisonMissingCandidateCount")
    $fixtureMissingMetricValueCount = [int](Get-JsonValue $Report "metricComparisonMissingMetricValueCount")
    $fixtureWdspBacked = Test-Truthy (Get-JsonValue $Report "metricComparisonWdspBackedEvidence")
    $nativeStageTimingPresent = Test-Truthy (Get-JsonValue $Report "nativeStageTimingReportPresent")
    $nativeStageTimingReady = Test-Truthy (Get-JsonValue $Report "nativeStageTimingReportReady")
    if ($nativeStageTimingPresent -or -not $nativeStageTimingReady) {
        $lines.Add("## Native Stage Timing") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("- Status: $(Format-MarkdownCell (Get-JsonValue $Report "nativeStageTimingReportStatus")) / ready $nativeStageTimingReady / present $nativeStageTimingPresent") | Out-Null
        $lines.Add("- Runs/stage records: $(Get-JsonValue $Report "nativeStageTimingRunCount") / $(Get-JsonValue $Report "nativeStageTimingStageRecordCount")") | Out-Null
        $lines.Add("- Missing stage/allocation runs: $(Get-JsonValue $Report "nativeStageTimingMissingStageTimingRunCount") / $(Get-JsonValue $Report "nativeStageTimingMissingAllocationProbeRunCount")") | Out-Null
        $lines.Add("- Budget failures: $(Get-JsonValue $Report "nativeStageTimingBudgetFailureCount")") | Out-Null
        $lines.Add("- Max stage/run/allocation: $(Get-JsonValue $Report "nativeStageTimingMaxStageElapsedMs") ms / $(Get-JsonValue $Report "nativeStageTimingMaxRunElapsedMs") ms / $(Get-JsonValue $Report "nativeStageTimingMaxManagedAllocationBytes") bytes") | Out-Null
        $lines.Add("- Source metrics hash: $(Format-MarkdownCell (Get-JsonValue $Report "nativeStageTimingMetricsHashStatus")); runtime hash: $(Format-MarkdownCell (Get-JsonValue $Report "nativeStageTimingWdspRuntimeHashStatus"))") | Out-Null
        $lines.Add("- Native C instrumentation: $(Format-MarkdownCell (Get-JsonValue $Report "nativeStageTimingNativeCStageInstrumentationStatus")); allocation probe: $(Format-MarkdownCell (Get-JsonValue $Report "nativeStageTimingNativeAllocationProbeStatus"))") | Out-Null
        $lines.Add("") | Out-Null
    }

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

    $liveTracePresent = Test-Truthy (Get-JsonValue $Report "liveTraceComparisonPresent")
    $liveTraceStatusCounts = @(Get-JsonArray $Report "liveTraceComparisonCaptureReadinessCandidateStatusCounts")
    $liveTraceTopConstraints = @(Get-JsonArray $Report "liveTraceComparisonCaptureReadinessCandidateTopConstraintCounts")
    $liveTraceTopHardGates = @(Get-JsonArray $Report "liveTraceComparisonCaptureReadinessCandidateTopHardConstraintCounts")
    if ($liveTracePresent -and
        ([int](Get-JsonValue $Report "liveTraceComparisonCaptureReadinessScenarioComparisonCount") -gt 0 -or
            $liveTraceStatusCounts.Count -gt 0 -or
            $liveTraceTopConstraints.Count -gt 0 -or
            $liveTraceTopHardGates.Count -gt 0)) {
        $lines.Add("## Live Trace Capture Readiness") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("- Scenario comparisons: $(Get-JsonValue $Report "liveTraceComparisonCaptureReadinessScenarioComparisonCount")") | Out-Null
        $lines.Add("- Candidate hard-gate pass/fail: $(Get-JsonValue $Report "liveTraceComparisonCaptureReadinessCandidateHardGatePassCount") / $(Get-JsonValue $Report "liveTraceComparisonCaptureReadinessCandidateHardGateFailCount")") | Out-Null
        $lines.Add("- Candidate strict-preflight pass/fail: $(Get-JsonValue $Report "liveTraceComparisonCaptureReadinessCandidateStrictPreflightPassCount") / $(Get-JsonValue $Report "liveTraceComparisonCaptureReadinessCandidateStrictPreflightFailCount")") | Out-Null
        $lines.Add("- Top capture constraint: $(Format-MarkdownCell (Get-TopCountText -Items $liveTraceTopConstraints -NameField "constraint"))") | Out-Null
        $lines.Add("- Top capture hard gate: $(Format-MarkdownCell (Get-TopCountText -Items $liveTraceTopHardGates -NameField "constraint"))") | Out-Null
        $lines.Add("") | Out-Null

        if ($liveTraceStatusCounts.Count -gt 0) {
            $lines.Add("| Candidate capture status | Count |") | Out-Null
            $lines.Add("|---|---:|") | Out-Null
            foreach ($item in $liveTraceStatusCounts) {
                $lines.Add("| $(Format-MarkdownCell (Get-JsonValue $item "status")) | $(Get-JsonValue $item "count") |") | Out-Null
            }
            $lines.Add("") | Out-Null
        }
    }

    $liveMatrixMixedWeakStrongReportCount = Get-IntegerValueOrDefault (Get-JsonValue $Report "liveMatrixMixedWeakStrongReportCount")
    $liveMatrixMixedWeakStrongStatus = [string](Get-JsonValue $Report "liveMatrixMixedWeakStrongStatus")
    if ($liveMatrixMixedWeakStrongReportCount -gt 0 -or -not [string]::IsNullOrWhiteSpace($liveMatrixMixedWeakStrongStatus)) {
        $lines.Add("## Live Matrix Mixed Weak/Strong Hunt") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("- Hunt ready/status: $(Get-JsonValue $Report "liveMatrixMixedWeakStrongHuntReady") / $(Format-MarkdownCell $liveMatrixMixedWeakStrongStatus)") | Out-Null
        $lines.Add("- Reports/schema-v2/ready reports: $(Get-JsonValue $Report "liveMatrixMixedWeakStrongReportCount") / $(Get-JsonValue $Report "liveMatrixMixedWeakStrongSchemaV2ReportCount") / $(Get-JsonValue $Report "liveMatrixMixedWeakStrongReadyReportCount")") | Out-Null
        $lines.Add("- Mixed traces/ready/missing runs/gap-watch runs: $(Get-JsonValue $Report "liveMatrixMixedWeakStrongTraceCount") / $(Get-JsonValue $Report "liveMatrixMixedWeakStrongReadyTraceCount") / $(Get-JsonValue $Report "liveMatrixMixedWeakStrongMissingRunCount") / $(Get-JsonValue $Report "liveMatrixMixedWeakStrongGapWatchRunCount")") | Out-Null
        $lines.Add("- Weak/strong input samples: $(Get-JsonValue $Report "liveMatrixMixedWeakStrongWeakInputSampleCount") / $(Get-JsonValue $Report "liveMatrixMixedWeakStrongStrongInputSampleCount")") | Out-Null
        $bestRun = Get-JsonValue $Report "liveMatrixMixedWeakStrongBestRun"
        if ($null -ne $bestRun) {
            $lines.Add("- Best run: $(Format-MarkdownCell (Get-JsonValue $bestRun "scenarioId")) / $(Format-MarkdownCell (Get-JsonValue $bestRun "comparisonId")) score $(Get-JsonValue $bestRun "mixedWeakStrongHuntScore") status $(Format-MarkdownCell (Get-JsonValue $bestRun "mixedWeakStrongEvidenceStatus")) gap $(Get-JsonValue $bestRun "weakStrongOutputGapDb")") | Out-Null
            $lines.Add("- Best run report: $(Format-MarkdownCell (Get-JsonValue $bestRun "reportPath"))") | Out-Null
        }
        $statusCounts = @(Get-JsonArray $Report "liveMatrixMixedWeakStrongStatusCounts")
        if ($statusCounts.Count -gt 0) {
            $lines.Add("") | Out-Null
            $lines.Add("| Mixed weak/strong status | Count |") | Out-Null
            $lines.Add("|---|---:|") | Out-Null
            foreach ($status in $statusCounts) {
                $lines.Add("| $(Format-MarkdownCell (Get-JsonValue $status "name")) | $(Get-JsonValue $status "count") |") | Out-Null
            }
        }
        $lines.Add("") | Out-Null
    }

    if (Test-Truthy (Get-JsonValue $Report "manualTuneObserverReportPresent")) {
        $lines.Add("## Manual Tune Observer Evidence") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("- Report ready/status: $(Get-JsonValue $Report "manualTuneObserverReportReady") / $(Format-MarkdownCell (Get-JsonValue $Report "manualTuneObserverReportStatus"))") | Out-Null
        $lines.Add("- Report ok/scan error: $(Get-JsonValue $Report "manualTuneObserverOk") / $(Format-MarkdownCell (Get-JsonValue $Report "manualTuneObserverScanError"))") | Out-Null
        $lines.Add("- Bundle-relative paths: $(Get-JsonValue $Report "manualTuneObserverBundleRelativePaths")") | Out-Null
        $lines.Add("- Base URL/scenario/comparison: $(Format-MarkdownCell (Get-JsonValue $Report "manualTuneObserverBaseUrl")) / $(Format-MarkdownCell (Get-JsonValue $Report "manualTuneObserverScenarioId")) / $(Format-MarkdownCell (Get-JsonValue $Report "manualTuneObserverComparisonId"))") | Out-Null
        $lines.Add("- RX-only/read-only/API writes/retune/TX touched: $(Get-JsonValue $Report "manualTuneObserverSafetyRxOnly") / $(Get-JsonValue $Report "manualTuneObserverSafetyReadOnly") / $(Get-JsonValue $Report "manualTuneObserverSafetyApiWrites") / $(Get-JsonValue $Report "manualTuneObserverSafetyRetune") / $(Get-JsonValue $Report "manualTuneObserverSafetyTxEndpointsTouched")") | Out-Null
        $lines.Add("- VFO/radio LO write attempts: $(Get-JsonValue $Report "manualTuneObserverSafetyVfoWriteAttemptCount") / $(Get-JsonValue $Report "manualTuneObserverSafetyRadioLoWriteAttemptCount")") | Out-Null
        $lines.Add("- Polls observed/planned: $(Get-JsonValue $Report "manualTuneObserverPollSampleCount") / $(Get-JsonValue $Report "manualTuneObserverPollCount")") | Out-Null
        $lines.Add("- Observed VFOs/best/status/score: $(Get-JsonValue $Report "manualTuneObserverObservedVfoCount") / $(Format-MarkdownCell (Get-JsonValue $Report "manualTuneObserverBestObservedVfoHz")) Hz / $(Format-MarkdownCell (Get-JsonValue $Report "manualTuneObserverBestObservedVfoStatus")) / $(Format-MarkdownCell (Get-JsonValue $Report "manualTuneObserverBestObservedVfoScore"))") | Out-Null
        $lines.Add("- Captures ready/total/mixed ready: $(Get-JsonValue $Report "manualTuneObserverReadyCaptureCount") / $(Get-JsonValue $Report "manualTuneObserverCaptureCount") / $(Get-JsonValue $Report "manualTuneObserverMixedWeakStrongReadyCaptureCount")") | Out-Null
        $lines.Add("- Unique VFOs/recaptured VFOs/max captures per VFO: $(Get-JsonValue $Report "manualTuneObserverUniqueCapturedVfoCount") / $(Get-JsonValue $Report "manualTuneObserverRecapturedVfoCount") / $(Get-JsonValue $Report "manualTuneObserverMaxCapturesPerVfo")") | Out-Null
        $lines.Add("- Stale scene allowed/polls/captures: $(Get-JsonValue $Report "manualTuneObserverAllowStaleSceneCapture") / $(Get-JsonValue $Report "manualTuneObserverStaleScenePollCount") / $(Get-JsonValue $Report "manualTuneObserverStaleSceneCaptureCount")") | Out-Null
        $lines.Add("- Frontend near/filter/off/mismatch polls: $(Get-JsonValue $Report "manualTuneObserverFrontendNearPassbandPollCount") / $(Get-JsonValue $Report "manualTuneObserverFrontendFilterPassbandPollCount") / $(Get-JsonValue $Report "manualTuneObserverFrontendFilterOffPassbandPollCount") / $(Get-JsonValue $Report "manualTuneObserverFrontendOffsetMismatchPollCount")") | Out-Null
        $lines.Add("- Frontend tuning hint polls/shift/VFO/reason/distance: $(Get-JsonValue $Report "manualTuneObserverFrontendTuningHintPollCount") / $(Format-MarkdownCell (Get-JsonValue $Report "manualTuneObserverFrontendSuggestedDialShiftHz")) Hz / $(Format-MarkdownCell (Get-JsonValue $Report "manualTuneObserverFrontendSuggestedVfoMhz")) MHz / $(Format-MarkdownCell (Get-JsonValue $Report "manualTuneObserverFrontendSuggestedTuneReason")) / $(Format-MarkdownCell (Get-JsonValue $Report "manualTuneObserverFrontendSuggestedFilterDistanceHz")) Hz") | Out-Null
        $lines.Add("- Capture-qualified polls: $(Get-JsonValue $Report "manualTuneObserverCaptureQualifiedPollCount")") | Out-Null
        $lines.Add("- Referenced captures ready/total/problems: $(Get-JsonValue $Report "manualTuneObserverReferencedCaptureReadyCount") / $(Get-JsonValue $Report "manualTuneObserverReferencedCaptureCount") / $(Get-JsonValue $Report "manualTuneObserverReferencedCaptureProblemCount")") | Out-Null
        $lines.Add("- Referenced capture missing/non-portable/invalid/JSONL missing: $(Get-JsonValue $Report "manualTuneObserverReferencedCaptureMissingCount") / $(Get-JsonValue $Report "manualTuneObserverReferencedCaptureNonPortableCount") / $(Get-JsonValue $Report "manualTuneObserverReferencedCaptureInvalidCount") / $(Get-JsonValue $Report "manualTuneObserverReferencedJsonlMissingCount")") | Out-Null
        $lines.Add("- Weak/strong/near-strong samples: $(Get-JsonValue $Report "manualTuneObserverWeakInputSampleCount") / $(Get-JsonValue $Report "manualTuneObserverStrongInputSampleCount") / $(Get-JsonValue $Report "manualTuneObserverNearStrongInputSampleCount")") | Out-Null
        $lines.Add("- Speech-qualified weak/strong samples: $(Get-JsonValue $Report "manualTuneObserverSpeechQualifiedWeakInputSampleCount") / $(Get-JsonValue $Report "manualTuneObserverSpeechQualifiedStrongInputSampleCount")") | Out-Null
        $lines.Add("- Passband-qualified weak/strong samples: $(Get-JsonValue $Report "manualTuneObserverPassbandQualifiedWeakInputSampleCount") / $(Get-JsonValue $Report "manualTuneObserverPassbandQualifiedStrongInputSampleCount")") | Out-Null
        $lines.Add("- AGC pumping captures: $(Get-JsonValue $Report "manualTuneObserverAgcPumpingRiskCaptureCount")") | Out-Null
        $lines.Add("- Weak-only/ready weak-only/strong-only captures: $(Get-JsonValue $Report "manualTuneObserverWeakOnlyCaptureCount") / $(Get-JsonValue $Report "manualTuneObserverReadyWeakOnlyCaptureCount") / $(Get-JsonValue $Report "manualTuneObserverStrongOnlyCaptureCount")") | Out-Null
        $lines.Add("- Missing strong/weak/both/output-gap/gap-watch captures: $(Get-JsonValue $Report "manualTuneObserverMissingStrongInputCaptureCount") / $(Get-JsonValue $Report "manualTuneObserverMissingWeakInputCaptureCount") / $(Get-JsonValue $Report "manualTuneObserverMissingWeakAndStrongInputCaptureCount") / $(Get-JsonValue $Report "manualTuneObserverMissingOutputGapCaptureCount") / $(Get-JsonValue $Report "manualTuneObserverWeakStrongOutputGapWatchCaptureCount")") | Out-Null
        $lines.Add("- Near-strong promotion candidates: $(Get-JsonValue $Report "manualTuneObserverNearStrongPromotionCandidateCaptureCount")") | Out-Null
        $bestNearStrongReportPath = [string](Get-JsonValue $Report "manualTuneObserverBestNearStrongPromotionCandidateReportPath")
        if (-not [string]::IsNullOrWhiteSpace($bestNearStrongReportPath)) {
            $lines.Add("- Best near-strong candidate: $(Format-MarkdownCell (Get-JsonValue $Report "manualTuneObserverBestNearStrongPromotionCandidateVfoHz")) Hz / $(Format-MarkdownCell (Get-JsonValue $Report "manualTuneObserverBestNearStrongPromotionCandidateVfoMhz")) MHz distance $(Format-MarkdownCell (Get-JsonValue $Report "manualTuneObserverBestNearStrongPromotionCandidateDistanceToStrongThresholdDb")) dB near/speech/passband $(Get-JsonValue $Report "manualTuneObserverBestNearStrongPromotionCandidateNearStrongInputSampleCount")/$(Get-JsonValue $Report "manualTuneObserverBestNearStrongPromotionCandidateSpeechQualifiedNearStrongInputSampleCount")/$(Get-JsonValue $Report "manualTuneObserverBestNearStrongPromotionCandidatePassbandQualifiedNearStrongInputSampleCount")") | Out-Null
            $lines.Add("- Best near-strong candidate report: $(Format-MarkdownCell $bestNearStrongReportPath)") | Out-Null
        }
        $manualStatusCounts = @(Get-JsonArray $Report "manualTuneObserverMixedWeakStrongEvidenceStatusCounts")
        if ($manualStatusCounts.Count -gt 0) {
            $lines.Add("") | Out-Null
            $lines.Add("| Manual mixed weak/strong status | Count |") | Out-Null
            $lines.Add("|---|---:|") | Out-Null
            foreach ($status in $manualStatusCounts) {
                $statusName = [string](Get-JsonValue $status "status")
                if ([string]::IsNullOrWhiteSpace($statusName)) {
                    $statusName = [string](Get-JsonValue $status "name")
                }
                $lines.Add("| $(Format-MarkdownCell $statusName) | $(Get-JsonValue $status "count") |") | Out-Null
            }
        }
        $bestManualObserverReportPath = [string](Get-JsonValue $Report "manualTuneObserverBestReportPath")
        if (-not [string]::IsNullOrWhiteSpace($bestManualObserverReportPath)) {
            $lines.Add("- Best capture: $(Format-MarkdownCell (Get-JsonValue $Report "manualTuneObserverBestFrequencyHz")) Hz status $(Format-MarkdownCell (Get-JsonValue $Report "manualTuneObserverBestStatus"))") | Out-Null
            $lines.Add("- Best capture report: $(Format-MarkdownCell $bestManualObserverReportPath)") | Out-Null
            $lines.Add("- Best capture JSONL: $(Format-MarkdownCell (Get-JsonValue $Report "manualTuneObserverBestJsonlPath"))") | Out-Null
        }
        $lines.Add("") | Out-Null
    }

    if (Test-Truthy (Get-JsonValue $Report "g2RxPeakHuntReportPresent")) {
        $lines.Add("## G2 RX Peak-Hunt Evidence") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("- Report ready/status: $(Get-JsonValue $Report "g2RxPeakHuntReportReady") / $(Format-MarkdownCell (Get-JsonValue $Report "g2RxPeakHuntReportStatus"))") | Out-Null
        $lines.Add("- Report ok/scan error: $(Get-JsonValue $Report "g2RxPeakHuntOk") / $(Format-MarkdownCell (Get-JsonValue $Report "g2RxPeakHuntScanError"))") | Out-Null
        $lines.Add("- Base URL requested/resolved/auto-discovered: $(Format-MarkdownCell (Get-JsonValue $Report "g2RxPeakHuntRequestedBaseUrl")) / $(Format-MarkdownCell (Get-JsonValue $Report "g2RxPeakHuntBaseUrl")) / $(Get-JsonValue $Report "g2RxPeakHuntBaseUrlAutoDiscovered")") | Out-Null
        $lines.Add("- RX-only/VFO restored/radio LO restored/retune attempts: $(Get-JsonValue $Report "g2RxPeakHuntSafetyRxOnly") / $(Get-JsonValue $Report "g2RxPeakHuntSafetyOriginalVfoRestored") / $(Get-JsonValue $Report "g2RxPeakHuntSafetyOriginalRadioLoRestored") / $(Get-JsonValue $Report "g2RxPeakHuntRetuneAttemptCount")") | Out-Null
        $lines.Add("- Scan passes completed/planned/delay: $(Get-JsonValue $Report "g2RxPeakHuntCompletedPassCount") / $(Get-JsonValue $Report "g2RxPeakHuntPassCount") / $(Get-JsonValue $Report "g2RxPeakHuntPassDelaySec") sec") | Out-Null
        $lines.Add("- Operator candidate frequencies/count: $(Get-JsonValue $Report "g2RxPeakHuntCandidateFrequencyHzCount") / $(Get-JsonValue $Report "g2RxPeakHuntOperatorCandidateCount")") | Out-Null
        $lines.Add("- Auto phone cluster enabled/candidates/exact/neighbor/lookback/band: $(Get-JsonValue $Report "g2RxPeakHuntAutoPhoneCluster") / $(Get-JsonValue $Report "g2RxPeakHuntAutoPhoneClusterCandidateCount") / $(Get-JsonValue $Report "g2RxPeakHuntAutoPhoneClusterExactCandidateCount") / $(Get-JsonValue $Report "g2RxPeakHuntAutoPhoneClusterNeighborCandidateCount") / $(Get-JsonValue $Report "g2RxPeakHuntAutoPhoneClusterLookbackHours") h / $(Get-JsonValue $Report "g2RxPeakHuntAutoPhoneClusterBandLowHz")-$(Get-JsonValue $Report "g2RxPeakHuntAutoPhoneClusterBandHighHz") Hz") | Out-Null
        $lines.Add("- Runs/failed/planned: $(Get-JsonValue $Report "g2RxPeakHuntActualRunCount") / $(Get-JsonValue $Report "g2RxPeakHuntFailedRunCount") / $(Get-JsonValue $Report "g2RxPeakHuntPlannedRunCount")") | Out-Null
        $lines.Add("- Referenced windows ready/total/problems: $(Get-JsonValue $Report "g2RxPeakHuntReferencedWindowReadyCount") / $(Get-JsonValue $Report "g2RxPeakHuntReferencedWindowCount") / $(Get-JsonValue $Report "g2RxPeakHuntReferencedWindowProblemCount")") | Out-Null
        $lines.Add("- Referenced window missing/non-portable/invalid/JSONL missing: $(Get-JsonValue $Report "g2RxPeakHuntReferencedWindowMissingCount") / $(Get-JsonValue $Report "g2RxPeakHuntReferencedWindowNonPortableCount") / $(Get-JsonValue $Report "g2RxPeakHuntReferencedWindowInvalidCount") / $(Get-JsonValue $Report "g2RxPeakHuntReferencedJsonlMissingCount")") | Out-Null
        $lines.Add("- Mixed ready/runs: $(Get-JsonValue $Report "g2RxPeakHuntMixedWeakStrongReady") / $(Get-JsonValue $Report "g2RxPeakHuntMixedWeakStrongReadyRunCount")") | Out-Null
        $lines.Add("- Weak/strong/near-strong samples: $(Get-JsonValue $Report "g2RxPeakHuntWeakInputSampleCount") / $(Get-JsonValue $Report "g2RxPeakHuntStrongInputSampleCount") / $(Get-JsonValue $Report "g2RxPeakHuntNearStrongInputSampleCount")") | Out-Null
        $lines.Add("- Speech-qualified weak/strong/near-strong samples: $(Get-JsonValue $Report "g2RxPeakHuntSpeechQualifiedWeakInputSampleCount") / $(Get-JsonValue $Report "g2RxPeakHuntSpeechQualifiedStrongInputSampleCount") / $(Get-JsonValue $Report "g2RxPeakHuntSpeechQualifiedNearStrongInputSampleCount")") | Out-Null
        $lines.Add("- Passband-qualified weak/strong/near-strong samples: $(Get-JsonValue $Report "g2RxPeakHuntPassbandQualifiedWeakInputSampleCount") / $(Get-JsonValue $Report "g2RxPeakHuntPassbandQualifiedStrongInputSampleCount") / $(Get-JsonValue $Report "g2RxPeakHuntPassbandQualifiedNearStrongInputSampleCount")") | Out-Null
        $lines.Add("- Frontend near-passband samples: $(Get-JsonValue $Report "g2RxPeakHuntFrontendNearPassbandSampleCount")") | Out-Null
        $lines.Add("- Weak loss/hot makeup/hard blockers/AGC pumping runs: $(Get-JsonValue $Report "g2RxPeakHuntCandidateWeakLossSampleCount") / $(Get-JsonValue $Report "g2RxPeakHuntHotMakeupSampleCount") / $(Get-JsonValue $Report "g2RxPeakHuntHardBlockerSampleCount") / $(Get-JsonValue $Report "g2RxPeakHuntAgcPumpingRiskRunCount")") | Out-Null
        $lines.Add("- Live diagnostics NR mode/ready: $(Format-MarkdownCell (Get-JsonValue $Report "g2RxPeakHuntLiveDiagnosticsEffectiveNrMode")) / $(Get-JsonValue $Report "g2RxPeakHuntLiveDiagnosticsReadyForNr5Tuning")") | Out-Null
        $lines.Add("- Frontend status/top peaks: $(Format-MarkdownCell (Get-JsonValue $Report "g2RxPeakHuntFrontendSceneStatus")) / $(Get-JsonValue $Report "g2RxPeakHuntFrontendSceneTopPeakCount")") | Out-Null
        $bestPeakHuntReportPath = [string](Get-JsonValue $Report "g2RxPeakHuntBestReportPath")
        if (-not [string]::IsNullOrWhiteSpace($bestPeakHuntReportPath)) {
            $lines.Add("- Best run: $(Format-MarkdownCell (Get-JsonValue $Report "g2RxPeakHuntBestFrequencyHz")) Hz score $(Get-JsonValue $Report "g2RxPeakHuntBestScore") status $(Format-MarkdownCell (Get-JsonValue $Report "g2RxPeakHuntBestStatus"))") | Out-Null
            $lines.Add("- Best run report: $(Format-MarkdownCell $bestPeakHuntReportPath)") | Out-Null
            $lines.Add("- Best run JSONL: $(Format-MarkdownCell (Get-JsonValue $Report "g2RxPeakHuntBestJsonlPath"))") | Out-Null
        }
        $lines.Add("") | Out-Null
    }

    $liveMatrixArtifactControlReportCount = Get-IntegerValueOrDefault (Get-JsonValue $Report "liveMatrixArtifactControlReportCount")
    $liveMatrixArtifactControlStatus = [string](Get-JsonValue $Report "liveMatrixArtifactControlStatus")
    if ($liveMatrixArtifactControlReportCount -gt 0 -or -not [string]::IsNullOrWhiteSpace($liveMatrixArtifactControlStatus)) {
        $lines.Add("## Live Matrix Artifact-Control Advisory") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("- Status: $(Format-MarkdownCell $liveMatrixArtifactControlStatus)") | Out-Null
        $lines.Add("- Reports/schema-v3/review runs: $(Get-JsonValue $Report "liveMatrixArtifactControlReportCount") / $(Get-JsonValue $Report "liveMatrixArtifactControlSchemaV3ReportCount") / $(Get-JsonValue $Report "liveMatrixArtifactControlReviewRunCount")") | Out-Null
        $lines.Add("- Risk max/lifted samples/lifted pct max/audio alignment mismatch pct max: $(Get-JsonValue $Report "liveMatrixArtifactControlRiskScoreMax") / $(Get-JsonValue $Report "liveMatrixArtifactControlLowEvidenceLiftedSampleCount") / $(Get-JsonValue $Report "liveMatrixArtifactControlLowEvidenceLiftedPctMax") / $(Get-JsonValue $Report "liveMatrixArtifactControlAudioAlignmentMismatchPctMax")") | Out-Null
        $statusCounts = @(Get-JsonArray $Report "liveMatrixArtifactControlStatusCounts")
        if ($statusCounts.Count -gt 0) {
            $lines.Add("") | Out-Null
            $lines.Add("| Artifact-control status | Count |") | Out-Null
            $lines.Add("|---|---:|") | Out-Null
            foreach ($status in $statusCounts) {
                $lines.Add("| $(Format-MarkdownCell (Get-JsonValue $status "name")) | $(Get-JsonValue $status "count") |") | Out-Null
            }
        }
        $lines.Add("") | Out-Null
    }

    if (Test-Truthy (Get-JsonValue $Report "liveAcceptanceCycleSummaryPresent")) {
        $lines.Add("## Live Acceptance Cycle Summary") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("- Summary status: $(Format-MarkdownCell (Get-JsonValue $Report "liveAcceptanceCycleSummaryStatus"))") | Out-Null
        $lines.Add("- Summary valid: $(Get-JsonValue $Report "liveAcceptanceCycleSummaryValid")") | Out-Null
        $lines.Add("- Evidence ready/status: $(Get-JsonValue $Report "liveAcceptanceCycleEvidenceReady") / $(Format-MarkdownCell (Get-JsonValue $Report "liveAcceptanceCycleEvidenceStatus"))") | Out-Null
        $lines.Add("- Matrix ready/count/nonzero exits: $(Get-JsonValue $Report "liveAcceptanceCycleMatrixAcceptanceReady") / $(Get-JsonValue $Report "liveAcceptanceCycleMatrixAcceptanceReadyCount") / $(Get-JsonValue $Report "liveAcceptanceCycleMatrixNonZeroExitCount")") | Out-Null
        $lines.Add("- Zeus comparison ready/regressions/gate failures: $(Get-JsonValue $Report "liveAcceptanceCycleComparisonReadyForReview") / $(Get-JsonValue $Report "liveAcceptanceCycleComparisonRegressionCount") / $(Get-JsonValue $Report "liveAcceptanceCycleComparisonGateFailureCount")") | Out-Null
        $lines.Add("- Zeus comparison metric catalog alignment/status/definitions/missing/mismatches: $(Get-JsonValue $Report "liveAcceptanceCycleComparisonMetricCatalogAlignmentReady") / $(Format-MarkdownCell (Get-JsonValue $Report "liveAcceptanceCycleComparisonMetricCatalogAlignmentStatus")) / $(Get-JsonValue $Report "liveAcceptanceCycleComparisonMetricDefinitionCount") / $(Get-JsonValue $Report "liveAcceptanceCycleComparisonMetricCatalogMissingMetricCount") / $(Get-JsonValue $Report "liveAcceptanceCycleComparisonMetricCatalogMismatchCount")") | Out-Null
        $lines.Add("- Thetis comparison ready/regressions/gate failures: $(Get-JsonValue $Report "liveAcceptanceCycleThetisComparisonReadyForReview") / $(Get-JsonValue $Report "liveAcceptanceCycleThetisComparisonRegressionCount") / $(Get-JsonValue $Report "liveAcceptanceCycleThetisComparisonGateFailureCount")") | Out-Null
        $lines.Add("- Thetis comparison metric catalog alignment/status/definitions/missing/mismatches: $(Get-JsonValue $Report "liveAcceptanceCycleThetisComparisonMetricCatalogAlignmentReady") / $(Format-MarkdownCell (Get-JsonValue $Report "liveAcceptanceCycleThetisComparisonMetricCatalogAlignmentStatus")) / $(Get-JsonValue $Report "liveAcceptanceCycleThetisComparisonMetricDefinitionCount") / $(Get-JsonValue $Report "liveAcceptanceCycleThetisComparisonMetricCatalogMissingMetricCount") / $(Get-JsonValue $Report "liveAcceptanceCycleThetisComparisonMetricCatalogMismatchCount")") | Out-Null
        $lines.Add("- Validation exit/ok/errors/warnings: $(Get-JsonValue $Report "liveAcceptanceCycleValidationExitCode") / $(Get-JsonValue $Report "liveAcceptanceCycleValidationOk") / $(Get-JsonValue $Report "liveAcceptanceCycleValidationErrorCount") / $(Get-JsonValue $Report "liveAcceptanceCycleValidationWarningCount")") | Out-Null
        $lines.Add("- G2 hardware ready/status/target/capture target/diagnostics: $(Get-JsonValue $Report "liveAcceptanceCycleHardwareEvidenceReady") / $(Format-MarkdownCell (Get-JsonValue $Report "liveAcceptanceCycleHardwareEvidenceStatus")) / $(Format-MarkdownCell (Get-JsonValue $Report "liveAcceptanceCycleHardwareTarget")) / $(Format-MarkdownCell (Get-JsonValue $Report "liveAcceptanceCycleCaptureHardwareTarget")) / $(Get-JsonValue $Report "liveAcceptanceCycleHardwareDiagnosticsPresent")") | Out-Null
        $lines.Add("- Live history AGC stability ready/status/traces/missing/pumping: $(Get-JsonValue $Report "liveAcceptanceCycleLiveDiagnosticsHistoryAgcStabilityReady") / $(Format-MarkdownCell (Get-JsonValue $Report "liveAcceptanceCycleLiveDiagnosticsHistoryAgcStabilityStatus")) / $(Get-JsonValue $Report "liveAcceptanceCycleLiveDiagnosticsHistoryAgcStabilityTraceCount") / $(Get-JsonValue $Report "liveAcceptanceCycleLiveDiagnosticsHistoryAgcStabilityMissingTraceCount") / $(Get-JsonValue $Report "liveAcceptanceCycleLiveDiagnosticsHistoryAgcPumpingRiskTraceCount")") | Out-Null
        $lines.Add("- Live history artifact-control advisory signals: $(Get-JsonValue $Report "liveAcceptanceCycleLiveDiagnosticsHistoryArtifactControlSignalCount")") | Out-Null
        $lines.Add("- Live history mixed weak/strong ready/status/traces/ready/missing/gap-watch: $(Get-JsonValue $Report "liveAcceptanceCycleLiveDiagnosticsHistoryMixedWeakStrongEvidenceReady") / $(Format-MarkdownCell (Get-JsonValue $Report "liveAcceptanceCycleLiveDiagnosticsHistoryMixedWeakStrongEvidenceStatus")) / $(Get-JsonValue $Report "liveAcceptanceCycleLiveDiagnosticsHistoryMixedWeakStrongTraceCount") / $(Get-JsonValue $Report "liveAcceptanceCycleLiveDiagnosticsHistoryMixedWeakStrongReadyTraceCount") / $(Get-JsonValue $Report "liveAcceptanceCycleLiveDiagnosticsHistoryMixedWeakStrongMissingTraceCount") / $(Get-JsonValue $Report "liveAcceptanceCycleLiveDiagnosticsHistoryMixedWeakStrongGapWatchTraceCount")") | Out-Null
        $cycleMatrixBestRun = Get-JsonValue $Report "liveAcceptanceCycleLiveMatrixMixedWeakStrongBestRun"
        $cycleMatrixBestText = if ($null -eq $cycleMatrixBestRun) {
            "none"
        }
        else {
            "$(Get-JsonValue $cycleMatrixBestRun "scenarioId")/$(Get-JsonValue $cycleMatrixBestRun "comparisonId") score=$(Get-JsonValue $cycleMatrixBestRun "mixedWeakStrongHuntScore") status=$(Get-JsonValue $cycleMatrixBestRun "mixedWeakStrongEvidenceStatus")"
        }
        $lines.Add("- Live matrix mixed weak/strong hunt ready/status/reports/schema-v2/ready/best: $(Get-JsonValue $Report "liveAcceptanceCycleLiveMatrixMixedWeakStrongHuntReady") / $(Format-MarkdownCell (Get-JsonValue $Report "liveAcceptanceCycleLiveMatrixMixedWeakStrongStatus")) / $(Get-JsonValue $Report "liveAcceptanceCycleLiveMatrixMixedWeakStrongReportCount") / $(Get-JsonValue $Report "liveAcceptanceCycleLiveMatrixMixedWeakStrongSchemaV2ReportCount") / $(Get-JsonValue $Report "liveAcceptanceCycleLiveMatrixMixedWeakStrongReadyReportCount") / $(Format-MarkdownCell $cycleMatrixBestText)") | Out-Null
        $lines.Add("- Live matrix artifact-control advisory status/reports/schema-v3/review runs/risk max: $(Format-MarkdownCell (Get-JsonValue $Report "liveAcceptanceCycleLiveMatrixArtifactControlStatus")) / $(Get-JsonValue $Report "liveAcceptanceCycleLiveMatrixArtifactControlReportCount") / $(Get-JsonValue $Report "liveAcceptanceCycleLiveMatrixArtifactControlSchemaV3ReportCount") / $(Get-JsonValue $Report "liveAcceptanceCycleLiveMatrixArtifactControlReviewRunCount") / $(Get-JsonValue $Report "liveAcceptanceCycleLiveMatrixArtifactControlRiskScoreMax")") | Out-Null
        $lines.Add("- Triage exit/action count/live artifact problems: $(Get-JsonValue $Report "liveAcceptanceCycleTriageExitCode") / $(Get-JsonValue $Report "liveAcceptanceCycleTriageAcceptanceActionPlanCount") / $(Get-JsonValue $Report "liveAcceptanceCycleRequiredLiveAcceptanceArtifactProblemCount")") | Out-Null
        $lines.Add("- Triage primary action: $(Format-MarkdownCell (Get-JsonValue $Report "liveAcceptanceCycleTriagePrimaryAcceptanceActionId")) / $(Format-MarkdownCell (Get-JsonValue $Report "liveAcceptanceCycleTriagePrimaryAcceptanceActionCategory")) / steps $(Get-JsonValue $Report "liveAcceptanceCycleTriagePrimaryAcceptanceCommandStepCount") / expected artifacts $(Get-JsonValue $Report "liveAcceptanceCycleTriagePrimaryAcceptanceExpectedArtifactCount")") | Out-Null
        $cyclePrimaryCommandOrManual = [string](Get-JsonValue $Report "liveAcceptanceCycleTriagePrimaryAcceptanceCommandTemplate")
        if ([string]::IsNullOrWhiteSpace($cyclePrimaryCommandOrManual)) {
            $cyclePrimaryCommandOrManual = [string](Get-JsonValue $Report "liveAcceptanceCycleTriagePrimaryAcceptanceManualAction")
        }
        if (-not [string]::IsNullOrWhiteSpace($cyclePrimaryCommandOrManual)) {
            $lines.Add("- Triage primary command/manual action: $(Format-MarkdownCell $cyclePrimaryCommandOrManual)") | Out-Null
        }
        $cyclePrimaryFollowUp = [string](Get-JsonValue $Report "liveAcceptanceCycleTriagePrimaryAcceptanceFollowUp")
        if (-not [string]::IsNullOrWhiteSpace($cyclePrimaryFollowUp)) {
            $lines.Add("- Triage primary follow-up: $(Format-MarkdownCell $cyclePrimaryFollowUp)") | Out-Null
        }
        if (Test-Truthy (Get-JsonValue $Report "liveAcceptanceCycleTriageExternalEngineBakeoffActionPresent")) {
            $lines.Add("- Triage external DSP/ML bakeoff action: $(Format-MarkdownCell (Get-JsonValue $Report "liveAcceptanceCycleTriageExternalEngineBakeoffActionId")) / $(Format-MarkdownCell (Get-JsonValue $Report "liveAcceptanceCycleTriageExternalEngineBakeoffActionCategory")) / steps $(Get-JsonValue $Report "liveAcceptanceCycleTriageExternalEngineBakeoffCommandStepCount") / expected artifacts $(Get-JsonValue $Report "liveAcceptanceCycleTriageExternalEngineBakeoffExpectedArtifactCount")") | Out-Null
            $cycleExternalCommandOrManual = [string](Get-JsonValue $Report "liveAcceptanceCycleTriageExternalEngineBakeoffCommandTemplate")
            if ([string]::IsNullOrWhiteSpace($cycleExternalCommandOrManual)) {
                $cycleExternalCommandOrManual = [string](Get-JsonValue $Report "liveAcceptanceCycleTriageExternalEngineBakeoffManualAction")
            }
            if (-not [string]::IsNullOrWhiteSpace($cycleExternalCommandOrManual)) {
                $lines.Add("- Triage external DSP/ML bakeoff command/manual action: $(Format-MarkdownCell $cycleExternalCommandOrManual)") | Out-Null
            }
            $cycleExternalFollowUp = [string](Get-JsonValue $Report "liveAcceptanceCycleTriageExternalEngineBakeoffFollowUp")
            if (-not [string]::IsNullOrWhiteSpace($cycleExternalFollowUp)) {
                $lines.Add("- Triage external DSP/ML bakeoff follow-up: $(Format-MarkdownCell $cycleExternalFollowUp)") | Out-Null
            }
        }
        if (Test-Truthy (Get-JsonValue $Report "liveAcceptanceCycleTriagePureSignalSafeBypassActionPresent")) {
            $lines.Add("- Triage PureSignal safe-bypass action: $(Format-MarkdownCell (Get-JsonValue $Report "liveAcceptanceCycleTriagePureSignalSafeBypassActionId")) / $(Format-MarkdownCell (Get-JsonValue $Report "liveAcceptanceCycleTriagePureSignalSafeBypassActionCategory")) / steps $(Get-JsonValue $Report "liveAcceptanceCycleTriagePureSignalSafeBypassCommandStepCount") / expected artifacts $(Get-JsonValue $Report "liveAcceptanceCycleTriagePureSignalSafeBypassExpectedArtifactCount")") | Out-Null
            $cyclePureSignalCommandOrManual = [string](Get-JsonValue $Report "liveAcceptanceCycleTriagePureSignalSafeBypassCommandTemplate")
            if ([string]::IsNullOrWhiteSpace($cyclePureSignalCommandOrManual)) {
                $cyclePureSignalCommandOrManual = [string](Get-JsonValue $Report "liveAcceptanceCycleTriagePureSignalSafeBypassManualAction")
            }
            if (-not [string]::IsNullOrWhiteSpace($cyclePureSignalCommandOrManual)) {
                $lines.Add("- Triage PureSignal safe-bypass command/manual action: $(Format-MarkdownCell $cyclePureSignalCommandOrManual)") | Out-Null
            }
            $cyclePureSignalFollowUp = [string](Get-JsonValue $Report "liveAcceptanceCycleTriagePureSignalSafeBypassFollowUp")
            if (-not [string]::IsNullOrWhiteSpace($cyclePureSignalFollowUp)) {
                $lines.Add("- Triage PureSignal safe-bypass follow-up: $(Format-MarkdownCell $cyclePureSignalFollowUp)") | Out-Null
            }
        }
        $lines.Add("- Summary path: $(Format-MarkdownCell (Get-JsonValue $Report "liveAcceptanceCycleSummaryPath"))") | Out-Null
        $lines.Add("") | Out-Null

        $cycleBlockers = @(Get-JsonArray $Report "liveAcceptanceCycleEvidenceBlockers")
        if ($cycleBlockers.Count -gt 0) {
            $lines.Add("| Blocker | Message |") | Out-Null
            $lines.Add("|---|---|") | Out-Null
            foreach ($blocker in $cycleBlockers) {
                $lines.Add("| $(Format-MarkdownCell (Get-JsonValue $blocker "code")) | $(Format-MarkdownCell (Get-JsonValue $blocker "message")) |") | Out-Null
            }
            $lines.Add("") | Out-Null
        }
    }

    $acceptanceReadiness = @(Get-JsonArray $Report "acceptanceReadiness")
    if ($acceptanceReadiness.Count -gt 0) {
        $lines.Add("## Acceptance Readiness") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("- Opt-in DSP build-out prerequisites ready: $(Get-JsonValue $Report "optInDspBuildOutReady") / $(Format-MarkdownCell (Get-JsonValue $Report "optInDspBuildOutStatus"))") | Out-Null
        $lines.Add("- G2 first-pass ready: $(Get-JsonValue $Report "g2FirstPassAcceptanceReady")") | Out-Null
        $lines.Add("- Opt-in candidate comparison ready: $(Get-JsonValue $Report "candidateComparisonReady")") | Out-Null
        $lines.Add("- Default behavior change ready: $(Get-JsonValue $Report "defaultBehaviorChangeReady")") | Out-Null
        $lines.Add("- Cross-radio validation evidence: $(Get-JsonValue $Report "crossRadioValidationEvidenceStatus") / ready $(Get-JsonValue $Report "crossRadioValidationReady") / source-backed $(Get-JsonValue $Report "crossRadioValidationSourceBackedEvidenceReady") / non-G2 targets $(Format-MarkdownCell ((@(Get-JsonArray $Report "crossRadioValidationNonG2TargetIds") | ForEach-Object { [string]$_ }) -join ', ')) / source reports $(Get-JsonValue $Report "crossRadioValidationSourceReportCount") total, $(Get-JsonValue $Report "crossRadioValidationReadyNonG2SourceReportCount") ready non-G2") | Out-Null
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
        $lines.Add("| Priority | Action | Stage | Gate | Required | Category | Steps | Command / Manual Action | Expected Artifacts | Follow-up |") | Out-Null
        $lines.Add("|---:|---|---|---|---:|---|---:|---|---|---|") | Out-Null
        foreach ($action in $acceptanceActionPlan) {
            $commandOrManual = [string](Get-JsonValue $action "commandTemplate")
            if ([string]::IsNullOrWhiteSpace($commandOrManual)) {
                $commandOrManual = [string](Get-JsonValue $action "manualAction")
            }
            $expectedArtifacts = @(Get-JsonArray $action "expectedArtifacts") -join ", "
            $lines.Add("| $(Get-JsonValue $action "priority") | $(Format-MarkdownCell (Get-JsonValue $action "actionId")) | $(Format-MarkdownCell (Get-JsonValue $action "stageId")) | $(Format-MarkdownCell (Get-JsonValue $action "gateId")) | $(Get-JsonValue $action "requiredForAcceptance") | $(Format-MarkdownCell (Get-JsonValue $action "category")) | $(Get-JsonValue $action "commandStepCount") | $(Format-MarkdownCell $commandOrManual) | $(Format-MarkdownCell $expectedArtifacts) | $(Format-MarkdownCell (Get-JsonValue $action "followUp")) |") | Out-Null
        }
        $lines.Add("") | Out-Null

        $multiStepActions = @($acceptanceActionPlan | Where-Object { @(Get-JsonArray $_ "commandSteps").Count -gt 1 })
        if ($multiStepActions.Count -gt 0) {
            $lines.Add("## Acceptance Command Steps") | Out-Null
            $lines.Add("") | Out-Null
            foreach ($action in $multiStepActions) {
                $lines.Add("### $(Get-JsonValue $action "actionId")") | Out-Null
                $lines.Add("") | Out-Null
                $stepIndex = 1
                foreach ($step in @(Get-JsonArray $action "commandSteps")) {
                    $lines.Add("$stepIndex. ``$step``") | Out-Null
                    $stepIndex++
                }
                $lines.Add("") | Out-Null
            }
        }
    }

    $requiredLiveAcceptanceArtifactProblems = @(Get-JsonArray $Report "requiredLiveAcceptanceArtifactProblems")
    if ($requiredLiveAcceptanceArtifactProblems.Count -gt 0) {
        $lines.Add("## Required Live Acceptance Artifact Problems") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("- Problem count: $(Get-JsonValue $Report "requiredLiveAcceptanceArtifactProblemCount")") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("| Artifact | Kind | Path | Comparison IDs |") | Out-Null
        $lines.Add("|---|---|---|---|") | Out-Null
        foreach ($artifact in $requiredLiveAcceptanceArtifactProblems) {
            $comparisonIds = @(Get-JsonArray $artifact "comparisonIds") -join ", "
            $lines.Add("| $(Format-MarkdownCell (Get-JsonValue $artifact "id")) | $(Format-MarkdownCell (Get-JsonValue $artifact "kind")) | $(Format-MarkdownCell (Get-JsonValue $artifact "path")) | $(Format-MarkdownCell $comparisonIds) |") | Out-Null
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
        $lines.Add("| Artifact | Kind | Source | Path | Summary | Hash | Summary Hash | Trace Path | Metadata | Capture | Top Constraint | Top Hard Gate | Mismatches | Source Status | JSONL Hash |") | Out-Null
        $lines.Add("|---|---|---|---|---|---|---|---|---|---|---|---|---:|---|---|") | Out-Null
        foreach ($file in $failedReferencedFiles) {
            $topConstraint = [string](Get-JsonValue $file "topCaptureConstraintName")
            $topConstraintCount = Get-JsonValue $file "topCaptureConstraintCount"
            if (-not [string]::IsNullOrWhiteSpace($topConstraint) -and $null -ne $topConstraintCount) {
                $topConstraint = "$topConstraint ($topConstraintCount)"
            }
            $topHardGate = [string](Get-JsonValue $file "topCaptureHardConstraintName")
            $topHardGateCount = Get-JsonValue $file "topCaptureHardConstraintCount"
            if (-not [string]::IsNullOrWhiteSpace($topHardGate) -and $null -ne $topHardGateCount) {
                $topHardGate = "$topHardGate ($topHardGateCount)"
            }
            $lines.Add("| $(Format-MarkdownCell (Get-JsonValue $file "artifactId")) | $(Format-MarkdownCell (Get-JsonValue $file "artifactKind")) | $(Format-MarkdownCell (Get-JsonValue $file "sourceType")) | $(Format-MarkdownCell (Get-JsonValue $file "path")) | $(Format-MarkdownCell (Get-JsonValue $file "summaryPath")) | $(Format-MarkdownCell (Get-JsonValue $file "hashStatus")) | $(Format-MarkdownCell (Get-JsonValue $file "summaryHashStatus")) | $(Format-MarkdownCell (Get-JsonValue $file "summaryTracePathStatus")) | $(Format-MarkdownCell (Get-JsonValue $file "summaryMetadataStatus")) | $(Format-MarkdownCell (Get-JsonValue $file "captureReadinessStatus")) | $(Format-MarkdownCell $topConstraint) | $(Format-MarkdownCell $topHardGate) | $(Get-JsonValue $file "captureReadinessFieldMismatchCount") | $(Format-MarkdownCell (Get-JsonValue $file "sourceStatus")) | $(Format-MarkdownCell (Get-JsonValue $file "jsonlHashStatus")) |") | Out-Null
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
$liveAcceptanceArtifactProblems = @(Get-LiveAcceptanceArtifactProblemRecords -Validation $validation)
$liveAcceptanceArtifactProblemIds = @(Get-LiveAcceptanceArtifactProblemIds -Records $liveAcceptanceArtifactProblems)
$liveAcceptanceArtifactProblemText = Format-LiveAcceptanceArtifactProblemIds -Records $liveAcceptanceArtifactProblems

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
$advisoryEvidenceGateProblems = @($evidenceGateProblems | Where-Object { -not (Test-Truthy (Get-JsonValue $_ "requiredForAcceptance")) })
$evidenceGateProblemIds = @($evidenceGateProblems | ForEach-Object { [string](Get-JsonValue $_ "gateId") } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
$evidenceGateProblemNames = @($evidenceGateProblems | ForEach-Object { [string](Get-JsonValue $_ "name") } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
$requiredEvidenceGateProblemIds = @($requiredEvidenceGateProblems | ForEach-Object { [string](Get-JsonValue $_ "gateId") } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
$requiredEvidenceGateProblemNames = @($requiredEvidenceGateProblems | ForEach-Object { [string](Get-JsonValue $_ "name") } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
$advisoryEvidenceGateProblemIds = @($advisoryEvidenceGateProblems | ForEach-Object { [string](Get-JsonValue $_ "gateId") } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
$advisoryEvidenceGateProblemNames = @($advisoryEvidenceGateProblems | ForEach-Object { [string](Get-JsonValue $_ "name") } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
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
if ($liveAcceptanceArtifactProblems.Count -gt 0) {
    $recommendations.Add("Capture or regenerate required live acceptance artifacts before final G2 review: $liveAcceptanceArtifactProblemText.") | Out-Null
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
$nativeStageTimingPresent = Test-Truthy (Get-JsonValue $validation "nativeStageTimingReportPresent")
$nativeStageTimingReady = Test-Truthy (Get-JsonValue $validation "nativeStageTimingReportReady")
if (-not $nativeStageTimingReady) {
    $nativeStageTimingStatus = [string](Get-JsonValue $validation "nativeStageTimingReportStatus")
    if ([string]::IsNullOrWhiteSpace($nativeStageTimingStatus)) {
        $nativeStageTimingStatus = if ($nativeStageTimingPresent) { "not-ready" } else { "not-captured" }
    }
    $recommendations.Add("Run summarize-dsp-native-stage-timing.ps1 after WDSP-backed offline fixture evidence; native-stage-timing-report status is '$nativeStageTimingStatus'.") | Out-Null
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
    $recommendations.Add("Resolve required evidence gates before acceptance review: $($requiredEvidenceGateProblemNames -join ', ').") | Out-Null
}
if ($advisoryEvidenceGateProblems.Count -gt 0) {
    $recommendations.Add("Review advisory evidence gates before next-level DSP build-out or external/new-tech bakeoff: $($advisoryEvidenceGateProblemNames -join ', ').") | Out-Null
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
$optInBuildOutStage = Get-JsonArray ([ordered]@{ acceptanceReadiness = $acceptanceReadiness }) "acceptanceReadiness" | Where-Object {
    [string](Get-JsonValue $_ "stageId") -eq "opt-in-dsp-buildout-prerequisites"
} | Select-Object -First 1
$candidateComparisonStage = Get-JsonArray ([ordered]@{ acceptanceReadiness = $acceptanceReadiness }) "acceptanceReadiness" | Where-Object {
    [string](Get-JsonValue $_ "stageId") -eq "opt-in-candidate-comparison"
} | Select-Object -First 1
$acceptanceActionPlan = @(Get-AcceptanceActionPlanRecords `
        -EvidenceGates $evidenceGates `
        -AcceptanceReadiness $acceptanceReadiness `
        -Validation $validation)
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
$primaryAcceptanceExpectedArtifacts = @(Get-JsonArray $primaryAcceptanceAction "expectedArtifacts")

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
    liveDiagnosticsHistoryAgcStabilityStatus = [string](Get-JsonValue $validation "liveDiagnosticsHistoryAgcStabilityStatus")
    liveDiagnosticsHistoryAgcStabilityTraceCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveDiagnosticsHistoryAgcStabilityTraceCount")
    liveDiagnosticsHistoryAgcStabilityMissingTraceCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveDiagnosticsHistoryAgcStabilityMissingTraceCount")
    liveDiagnosticsHistoryAgcPumpingRiskTraceCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveDiagnosticsHistoryAgcPumpingRiskTraceCount")
    liveDiagnosticsHistoryAgcActivePumpingSignalCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveDiagnosticsHistoryAgcActivePumpingSignalCount")
    liveDiagnosticsHistoryAgcVoiceLikePumpingSignalCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveDiagnosticsHistoryAgcVoiceLikePumpingSignalCount")
    liveDiagnosticsHistoryArtifactControlSignalCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveDiagnosticsHistoryArtifactControlSignalCount")
    liveDiagnosticsHistoryMixedWeakStrongEvidenceReady = Test-Truthy (Get-JsonValue $validation "liveDiagnosticsHistoryMixedWeakStrongEvidenceReady")
    liveDiagnosticsHistoryMixedWeakStrongEvidenceStatus = [string](Get-JsonValue $validation "liveDiagnosticsHistoryMixedWeakStrongEvidenceStatus")
    liveDiagnosticsHistoryMixedWeakStrongTraceCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveDiagnosticsHistoryMixedWeakStrongTraceCount")
    liveDiagnosticsHistoryMixedWeakStrongReadyTraceCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveDiagnosticsHistoryMixedWeakStrongReadyTraceCount")
    liveDiagnosticsHistoryMixedWeakStrongMissingTraceCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveDiagnosticsHistoryMixedWeakStrongMissingTraceCount")
    liveDiagnosticsHistoryMixedWeakStrongGapWatchTraceCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveDiagnosticsHistoryMixedWeakStrongGapWatchTraceCount")
    liveTraceComparisonPresent = Test-Truthy (Get-JsonValue $validation "liveTraceComparisonPresent")
    liveTraceComparisonReady = Test-Truthy (Get-JsonValue $validation "liveTraceComparisonReady")
    liveTraceComparisonMetricDefinitionCount = [int](Get-JsonValue $validation "liveTraceComparisonMetricDefinitionCount")
    liveTraceComparisonMetricCatalogAlignedMetricCount = [int](Get-JsonValue $validation "liveTraceComparisonMetricCatalogAlignedMetricCount")
    liveTraceComparisonMetricCatalogMissingMetricCount = [int](Get-JsonValue $validation "liveTraceComparisonMetricCatalogMissingMetricCount")
    liveTraceComparisonMetricCatalogMissingMetricIds = @(Get-JsonArray $validation "liveTraceComparisonMetricCatalogMissingMetricIds")
    liveTraceComparisonMetricCatalogDirectionMismatchCount = [int](Get-JsonValue $validation "liveTraceComparisonMetricCatalogDirectionMismatchCount")
    liveTraceComparisonMetricCatalogDirectionMismatchMetricIds = @(Get-JsonArray $validation "liveTraceComparisonMetricCatalogDirectionMismatchMetricIds")
    liveTraceComparisonMetricCatalogThresholdMismatchCount = [int](Get-JsonValue $validation "liveTraceComparisonMetricCatalogThresholdMismatchCount")
    liveTraceComparisonMetricCatalogThresholdMismatchMetricIds = @(Get-JsonArray $validation "liveTraceComparisonMetricCatalogThresholdMismatchMetricIds")
    liveTraceComparisonMetricCatalogComparatorMismatchCount = [int](Get-JsonValue $validation "liveTraceComparisonMetricCatalogComparatorMismatchCount")
    liveTraceComparisonMetricCatalogComparatorMismatchMetricIds = @(Get-JsonArray $validation "liveTraceComparisonMetricCatalogComparatorMismatchMetricIds")
    liveTraceComparisonMetricCatalogSafetyClassMismatchCount = [int](Get-JsonValue $validation "liveTraceComparisonMetricCatalogSafetyClassMismatchCount")
    liveTraceComparisonMetricCatalogSafetyClassMismatchMetricIds = @(Get-JsonArray $validation "liveTraceComparisonMetricCatalogSafetyClassMismatchMetricIds")
    liveTraceComparisonMetricCatalogScopeMismatchCount = [int](Get-JsonValue $validation "liveTraceComparisonMetricCatalogScopeMismatchCount")
    liveTraceComparisonMetricCatalogScopeMismatchMetricIds = @(Get-JsonArray $validation "liveTraceComparisonMetricCatalogScopeMismatchMetricIds")
    liveTraceComparisonMetricCatalogAlignmentReady = Test-Truthy (Get-JsonValue $validation "liveTraceComparisonMetricCatalogAlignmentReady")
    liveTraceComparisonMetricCatalogAlignmentStatus = [string](Get-JsonValue $validation "liveTraceComparisonMetricCatalogAlignmentStatus")
    liveTraceComparisonCaptureReadinessScenarioComparisonCount = [int](Get-JsonValue $validation "liveTraceComparisonCaptureReadinessScenarioComparisonCount")
    liveTraceComparisonCaptureReadinessCandidateHardGatePassCount = [int](Get-JsonValue $validation "liveTraceComparisonCaptureReadinessCandidateHardGatePassCount")
    liveTraceComparisonCaptureReadinessCandidateHardGateFailCount = [int](Get-JsonValue $validation "liveTraceComparisonCaptureReadinessCandidateHardGateFailCount")
    liveTraceComparisonCaptureReadinessCandidateStrictPreflightPassCount = [int](Get-JsonValue $validation "liveTraceComparisonCaptureReadinessCandidateStrictPreflightPassCount")
    liveTraceComparisonCaptureReadinessCandidateStrictPreflightFailCount = [int](Get-JsonValue $validation "liveTraceComparisonCaptureReadinessCandidateStrictPreflightFailCount")
    liveTraceComparisonCaptureReadinessCandidateStatusCounts = @(Get-JsonArray $validation "liveTraceComparisonCaptureReadinessCandidateStatusCounts")
    liveTraceComparisonCaptureReadinessCandidateTopConstraintCounts = @(Get-JsonArray $validation "liveTraceComparisonCaptureReadinessCandidateTopConstraintCounts")
    liveTraceComparisonCaptureReadinessCandidateTopHardConstraintCounts = @(Get-JsonArray $validation "liveTraceComparisonCaptureReadinessCandidateTopHardConstraintCounts")
    liveTraceComparisonRxAudioLevelerConstrainedSampleDelta = [int](Get-JsonValue $validation "liveTraceComparisonRxAudioLevelerConstrainedSampleDelta")
    liveTraceComparisonRxAudioLevelerConstrainedPctDelta = Get-JsonValue $validation "liveTraceComparisonRxAudioLevelerConstrainedPctDelta"
    liveTraceComparisonRxAudioLevelerBoostSlewLimitedSampleDelta = [int](Get-JsonValue $validation "liveTraceComparisonRxAudioLevelerBoostSlewLimitedSampleDelta")
    liveTraceComparisonRxAudioLevelerPeakLimitedSampleDelta = [int](Get-JsonValue $validation "liveTraceComparisonRxAudioLevelerPeakLimitedSampleDelta")
    liveTraceComparisonRxAudioLevelerOutputLimitedSampleDelta = [int](Get-JsonValue $validation "liveTraceComparisonRxAudioLevelerOutputLimitedSampleDelta")
    liveTraceComparisonRxAudioLevelerOutputRmsMovementDbDelta = Get-JsonValue $validation "liveTraceComparisonRxAudioLevelerOutputRmsMovementDbDelta"
    liveTraceComparisonRxAudioLevelerAppliedGainMovementDbDelta = Get-JsonValue $validation "liveTraceComparisonRxAudioLevelerAppliedGainMovementDbDelta"
    liveTraceComparisonRxAudioLevelerConstrainedRegressionCount = [int](Get-JsonValue $validation "liveTraceComparisonRxAudioLevelerConstrainedRegressionCount")
    liveTraceComparisonRxAudioLevelerConstrainedPctRegressionCount = [int](Get-JsonValue $validation "liveTraceComparisonRxAudioLevelerConstrainedPctRegressionCount")
    liveTraceComparisonRxAudioLevelerBoostSlewRegressionCount = [int](Get-JsonValue $validation "liveTraceComparisonRxAudioLevelerBoostSlewRegressionCount")
    liveTraceComparisonRxAudioLevelerPeakLimitedRegressionCount = [int](Get-JsonValue $validation "liveTraceComparisonRxAudioLevelerPeakLimitedRegressionCount")
    liveTraceComparisonRxAudioLevelerOutputLimitedRegressionCount = [int](Get-JsonValue $validation "liveTraceComparisonRxAudioLevelerOutputLimitedRegressionCount")
    liveTraceThetisComparisonPresent = Test-Truthy (Get-JsonValue $validation "liveTraceThetisComparisonPresent")
    liveTraceThetisComparisonReady = Test-Truthy (Get-JsonValue $validation "liveTraceThetisComparisonReady")
    liveTraceThetisComparisonMetricDefinitionCount = [int](Get-JsonValue $validation "liveTraceThetisComparisonMetricDefinitionCount")
    liveTraceThetisComparisonMetricCatalogAlignedMetricCount = [int](Get-JsonValue $validation "liveTraceThetisComparisonMetricCatalogAlignedMetricCount")
    liveTraceThetisComparisonMetricCatalogMissingMetricCount = [int](Get-JsonValue $validation "liveTraceThetisComparisonMetricCatalogMissingMetricCount")
    liveTraceThetisComparisonMetricCatalogMissingMetricIds = @(Get-JsonArray $validation "liveTraceThetisComparisonMetricCatalogMissingMetricIds")
    liveTraceThetisComparisonMetricCatalogDirectionMismatchCount = [int](Get-JsonValue $validation "liveTraceThetisComparisonMetricCatalogDirectionMismatchCount")
    liveTraceThetisComparisonMetricCatalogDirectionMismatchMetricIds = @(Get-JsonArray $validation "liveTraceThetisComparisonMetricCatalogDirectionMismatchMetricIds")
    liveTraceThetisComparisonMetricCatalogThresholdMismatchCount = [int](Get-JsonValue $validation "liveTraceThetisComparisonMetricCatalogThresholdMismatchCount")
    liveTraceThetisComparisonMetricCatalogThresholdMismatchMetricIds = @(Get-JsonArray $validation "liveTraceThetisComparisonMetricCatalogThresholdMismatchMetricIds")
    liveTraceThetisComparisonMetricCatalogComparatorMismatchCount = [int](Get-JsonValue $validation "liveTraceThetisComparisonMetricCatalogComparatorMismatchCount")
    liveTraceThetisComparisonMetricCatalogComparatorMismatchMetricIds = @(Get-JsonArray $validation "liveTraceThetisComparisonMetricCatalogComparatorMismatchMetricIds")
    liveTraceThetisComparisonMetricCatalogSafetyClassMismatchCount = [int](Get-JsonValue $validation "liveTraceThetisComparisonMetricCatalogSafetyClassMismatchCount")
    liveTraceThetisComparisonMetricCatalogSafetyClassMismatchMetricIds = @(Get-JsonArray $validation "liveTraceThetisComparisonMetricCatalogSafetyClassMismatchMetricIds")
    liveTraceThetisComparisonMetricCatalogScopeMismatchCount = [int](Get-JsonValue $validation "liveTraceThetisComparisonMetricCatalogScopeMismatchCount")
    liveTraceThetisComparisonMetricCatalogScopeMismatchMetricIds = @(Get-JsonArray $validation "liveTraceThetisComparisonMetricCatalogScopeMismatchMetricIds")
    liveTraceThetisComparisonMetricCatalogAlignmentReady = Test-Truthy (Get-JsonValue $validation "liveTraceThetisComparisonMetricCatalogAlignmentReady")
    liveTraceThetisComparisonMetricCatalogAlignmentStatus = [string](Get-JsonValue $validation "liveTraceThetisComparisonMetricCatalogAlignmentStatus")
    liveTraceThetisComparisonCaptureReadinessScenarioComparisonCount = [int](Get-JsonValue $validation "liveTraceThetisComparisonCaptureReadinessScenarioComparisonCount")
    liveTraceThetisComparisonCaptureReadinessCandidateHardGatePassCount = [int](Get-JsonValue $validation "liveTraceThetisComparisonCaptureReadinessCandidateHardGatePassCount")
    liveTraceThetisComparisonCaptureReadinessCandidateHardGateFailCount = [int](Get-JsonValue $validation "liveTraceThetisComparisonCaptureReadinessCandidateHardGateFailCount")
    liveTraceThetisComparisonCaptureReadinessCandidateStrictPreflightPassCount = [int](Get-JsonValue $validation "liveTraceThetisComparisonCaptureReadinessCandidateStrictPreflightPassCount")
    liveTraceThetisComparisonCaptureReadinessCandidateStrictPreflightFailCount = [int](Get-JsonValue $validation "liveTraceThetisComparisonCaptureReadinessCandidateStrictPreflightFailCount")
    liveAcceptanceCycleSummaryPresent = Test-Truthy (Get-JsonValue $validation "liveAcceptanceCycleSummaryPresent")
    liveAcceptanceCycleSummaryValid = Test-Truthy (Get-JsonValue $validation "liveAcceptanceCycleSummaryValid")
    liveAcceptanceCycleSummaryStatus = [string](Get-JsonValue $validation "liveAcceptanceCycleSummaryStatus")
    liveAcceptanceCycleEvidenceReady = Test-Truthy (Get-JsonValue $validation "liveAcceptanceCycleEvidenceReady")
    liveAcceptanceCycleEvidenceStatus = [string](Get-JsonValue $validation "liveAcceptanceCycleEvidenceStatus")
    liveAcceptanceCycleEvidenceBlockerCount = [int](Get-JsonValue $validation "liveAcceptanceCycleEvidenceBlockerCount")
    liveAcceptanceCycleEvidenceBlockers = @(Get-JsonArray $validation "liveAcceptanceCycleEvidenceBlockers")
    liveAcceptanceCycleMatrixAcceptanceReady = Test-Truthy (Get-JsonValue $validation "liveAcceptanceCycleMatrixAcceptanceReady")
    liveAcceptanceCycleMatrixAcceptanceReadyCount = [int](Get-JsonValue $validation "liveAcceptanceCycleMatrixAcceptanceReadyCount")
    liveAcceptanceCycleMatrixNonZeroExitCount = [int](Get-JsonValue $validation "liveAcceptanceCycleMatrixNonZeroExitCount")
    liveAcceptanceCycleComparisonReadyForReview = Test-Truthy (Get-JsonValue $validation "liveAcceptanceCycleComparisonReadyForReview")
    liveAcceptanceCycleComparisonRegressionCount = [int](Get-JsonValue $validation "liveAcceptanceCycleComparisonRegressionCount")
    liveAcceptanceCycleComparisonGateFailureCount = [int](Get-JsonValue $validation "liveAcceptanceCycleComparisonGateFailureCount")
    liveAcceptanceCycleComparisonMetricCatalogAlignmentReady = Test-Truthy (Get-JsonValue $validation "liveAcceptanceCycleComparisonMetricCatalogAlignmentReady")
    liveAcceptanceCycleComparisonMetricCatalogAlignmentStatus = [string](Get-JsonValue $validation "liveAcceptanceCycleComparisonMetricCatalogAlignmentStatus")
    liveAcceptanceCycleComparisonMetricDefinitionCount = [int](Get-JsonValue $validation "liveAcceptanceCycleComparisonMetricDefinitionCount")
    liveAcceptanceCycleComparisonMetricCatalogMissingMetricCount = [int](Get-JsonValue $validation "liveAcceptanceCycleComparisonMetricCatalogMissingMetricCount")
    liveAcceptanceCycleComparisonMetricCatalogMismatchCount = [int](Get-JsonValue $validation "liveAcceptanceCycleComparisonMetricCatalogMismatchCount")
    liveAcceptanceCycleThetisComparisonReadyForReview = Test-Truthy (Get-JsonValue $validation "liveAcceptanceCycleThetisComparisonReadyForReview")
    liveAcceptanceCycleThetisComparisonRegressionCount = [int](Get-JsonValue $validation "liveAcceptanceCycleThetisComparisonRegressionCount")
    liveAcceptanceCycleThetisComparisonGateFailureCount = [int](Get-JsonValue $validation "liveAcceptanceCycleThetisComparisonGateFailureCount")
    liveAcceptanceCycleThetisComparisonMetricCatalogAlignmentReady = Test-Truthy (Get-JsonValue $validation "liveAcceptanceCycleThetisComparisonMetricCatalogAlignmentReady")
    liveAcceptanceCycleThetisComparisonMetricCatalogAlignmentStatus = [string](Get-JsonValue $validation "liveAcceptanceCycleThetisComparisonMetricCatalogAlignmentStatus")
    liveAcceptanceCycleThetisComparisonMetricDefinitionCount = [int](Get-JsonValue $validation "liveAcceptanceCycleThetisComparisonMetricDefinitionCount")
    liveAcceptanceCycleThetisComparisonMetricCatalogMissingMetricCount = [int](Get-JsonValue $validation "liveAcceptanceCycleThetisComparisonMetricCatalogMissingMetricCount")
    liveAcceptanceCycleThetisComparisonMetricCatalogMismatchCount = [int](Get-JsonValue $validation "liveAcceptanceCycleThetisComparisonMetricCatalogMismatchCount")
    liveAcceptanceCycleValidationExitCode = Get-JsonValue $validation "liveAcceptanceCycleValidationExitCode"
    liveAcceptanceCycleValidationOk = Test-Truthy (Get-JsonValue $validation "liveAcceptanceCycleValidationOk")
    liveAcceptanceCycleValidationErrorCount = [int](Get-JsonValue $validation "liveAcceptanceCycleValidationErrorCount")
    liveAcceptanceCycleValidationWarningCount = [int](Get-JsonValue $validation "liveAcceptanceCycleValidationWarningCount")
    liveAcceptanceCycleHardwareEvidenceReady = Test-Truthy (Get-JsonValue $validation "liveAcceptanceCycleHardwareEvidenceReady")
    liveAcceptanceCycleHardwareEvidenceStatus = [string](Get-JsonValue $validation "liveAcceptanceCycleHardwareEvidenceStatus")
    liveAcceptanceCycleHardwareTarget = [string](Get-JsonValue $validation "liveAcceptanceCycleHardwareTarget")
    liveAcceptanceCycleCaptureHardwareTarget = [string](Get-JsonValue $validation "liveAcceptanceCycleCaptureHardwareTarget")
    liveAcceptanceCycleHardwareDiagnosticsPresent = Test-Truthy (Get-JsonValue $validation "liveAcceptanceCycleHardwareDiagnosticsPresent")
    liveAcceptanceCycleLiveDiagnosticsHistoryAgcStabilityReady = Test-Truthy (Get-JsonValue $validation "liveAcceptanceCycleLiveDiagnosticsHistoryAgcStabilityReady")
    liveAcceptanceCycleLiveDiagnosticsHistoryAgcStabilityStatus = [string](Get-JsonValue $validation "liveAcceptanceCycleLiveDiagnosticsHistoryAgcStabilityStatus")
    liveAcceptanceCycleLiveDiagnosticsHistoryAgcStabilityTraceCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveAcceptanceCycleLiveDiagnosticsHistoryAgcStabilityTraceCount")
    liveAcceptanceCycleLiveDiagnosticsHistoryAgcStabilityMissingTraceCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveAcceptanceCycleLiveDiagnosticsHistoryAgcStabilityMissingTraceCount")
    liveAcceptanceCycleLiveDiagnosticsHistoryAgcPumpingRiskTraceCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveAcceptanceCycleLiveDiagnosticsHistoryAgcPumpingRiskTraceCount")
    liveAcceptanceCycleLiveDiagnosticsHistoryAgcActivePumpingSignalCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveAcceptanceCycleLiveDiagnosticsHistoryAgcActivePumpingSignalCount")
    liveAcceptanceCycleLiveDiagnosticsHistoryAgcVoiceLikePumpingSignalCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveAcceptanceCycleLiveDiagnosticsHistoryAgcVoiceLikePumpingSignalCount")
    liveAcceptanceCycleLiveDiagnosticsHistoryArtifactControlSignalCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveAcceptanceCycleLiveDiagnosticsHistoryArtifactControlSignalCount")
    liveAcceptanceCycleLiveDiagnosticsHistoryMixedWeakStrongEvidenceReady = Test-Truthy (Get-JsonValue $validation "liveAcceptanceCycleLiveDiagnosticsHistoryMixedWeakStrongEvidenceReady")
    liveAcceptanceCycleLiveDiagnosticsHistoryMixedWeakStrongEvidenceStatus = [string](Get-JsonValue $validation "liveAcceptanceCycleLiveDiagnosticsHistoryMixedWeakStrongEvidenceStatus")
    liveAcceptanceCycleLiveDiagnosticsHistoryMixedWeakStrongTraceCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveAcceptanceCycleLiveDiagnosticsHistoryMixedWeakStrongTraceCount")
    liveAcceptanceCycleLiveDiagnosticsHistoryMixedWeakStrongReadyTraceCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveAcceptanceCycleLiveDiagnosticsHistoryMixedWeakStrongReadyTraceCount")
    liveAcceptanceCycleLiveDiagnosticsHistoryMixedWeakStrongMissingTraceCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveAcceptanceCycleLiveDiagnosticsHistoryMixedWeakStrongMissingTraceCount")
    liveAcceptanceCycleLiveDiagnosticsHistoryMixedWeakStrongGapWatchTraceCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveAcceptanceCycleLiveDiagnosticsHistoryMixedWeakStrongGapWatchTraceCount")
    liveAcceptanceCycleLiveMatrixMixedWeakStrongHuntReady = Test-Truthy (Get-JsonValue $validation "liveAcceptanceCycleLiveMatrixMixedWeakStrongHuntReady")
    liveAcceptanceCycleLiveMatrixMixedWeakStrongStatus = [string](Get-JsonValue $validation "liveAcceptanceCycleLiveMatrixMixedWeakStrongStatus")
    liveAcceptanceCycleLiveMatrixMixedWeakStrongReportCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveAcceptanceCycleLiveMatrixMixedWeakStrongReportCount")
    liveAcceptanceCycleLiveMatrixMixedWeakStrongSchemaV2ReportCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveAcceptanceCycleLiveMatrixMixedWeakStrongSchemaV2ReportCount")
    liveAcceptanceCycleLiveMatrixMixedWeakStrongReadyReportCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveAcceptanceCycleLiveMatrixMixedWeakStrongReadyReportCount")
    liveAcceptanceCycleLiveMatrixMixedWeakStrongTraceCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveAcceptanceCycleLiveMatrixMixedWeakStrongTraceCount")
    liveAcceptanceCycleLiveMatrixMixedWeakStrongReadyTraceCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveAcceptanceCycleLiveMatrixMixedWeakStrongReadyTraceCount")
    liveAcceptanceCycleLiveMatrixMixedWeakStrongMissingRunCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveAcceptanceCycleLiveMatrixMixedWeakStrongMissingRunCount")
    liveAcceptanceCycleLiveMatrixMixedWeakStrongGapWatchRunCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveAcceptanceCycleLiveMatrixMixedWeakStrongGapWatchRunCount")
    liveAcceptanceCycleLiveMatrixMixedWeakStrongWeakInputSampleCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveAcceptanceCycleLiveMatrixMixedWeakStrongWeakInputSampleCount")
    liveAcceptanceCycleLiveMatrixMixedWeakStrongStrongInputSampleCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveAcceptanceCycleLiveMatrixMixedWeakStrongStrongInputSampleCount")
    liveAcceptanceCycleLiveMatrixMixedWeakStrongStatusCounts = @(Get-JsonArray $validation "liveAcceptanceCycleLiveMatrixMixedWeakStrongStatusCounts")
    liveAcceptanceCycleLiveMatrixMixedWeakStrongBestRun = Get-JsonValue $validation "liveAcceptanceCycleLiveMatrixMixedWeakStrongBestRun"
    liveAcceptanceCycleLiveMatrixArtifactControlStatus = [string](Get-JsonValue $validation "liveAcceptanceCycleLiveMatrixArtifactControlStatus")
    liveAcceptanceCycleLiveMatrixArtifactControlReportCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveAcceptanceCycleLiveMatrixArtifactControlReportCount")
    liveAcceptanceCycleLiveMatrixArtifactControlSchemaV3ReportCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveAcceptanceCycleLiveMatrixArtifactControlSchemaV3ReportCount")
    liveAcceptanceCycleLiveMatrixArtifactControlReviewRunCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveAcceptanceCycleLiveMatrixArtifactControlReviewRunCount")
    liveAcceptanceCycleLiveMatrixArtifactControlRiskScoreMax = Get-JsonValue $validation "liveAcceptanceCycleLiveMatrixArtifactControlRiskScoreMax"
    liveAcceptanceCycleLiveMatrixArtifactControlLowEvidenceLiftedSampleCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveAcceptanceCycleLiveMatrixArtifactControlLowEvidenceLiftedSampleCount")
    liveAcceptanceCycleLiveMatrixArtifactControlLowEvidenceLiftedPctMax = Get-JsonValue $validation "liveAcceptanceCycleLiveMatrixArtifactControlLowEvidenceLiftedPctMax"
    liveAcceptanceCycleLiveMatrixArtifactControlAudioAlignmentMismatchPctMax = Get-JsonValue $validation "liveAcceptanceCycleLiveMatrixArtifactControlAudioAlignmentMismatchPctMax"
    liveAcceptanceCycleLiveMatrixArtifactControlStatusCounts = @(Get-JsonArray $validation "liveAcceptanceCycleLiveMatrixArtifactControlStatusCounts")
    liveMatrixMixedWeakStrongHuntReady = Test-Truthy (Get-JsonValue $validation "liveMatrixMixedWeakStrongHuntReady")
    liveMatrixMixedWeakStrongStatus = [string](Get-JsonValue $validation "liveMatrixMixedWeakStrongStatus")
    liveMatrixMixedWeakStrongReportCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveMatrixMixedWeakStrongReportCount")
    liveMatrixMixedWeakStrongSchemaV2ReportCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveMatrixMixedWeakStrongSchemaV2ReportCount")
    liveMatrixMixedWeakStrongReadyReportCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveMatrixMixedWeakStrongReadyReportCount")
    liveMatrixMixedWeakStrongTraceCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveMatrixMixedWeakStrongTraceCount")
    liveMatrixMixedWeakStrongReadyTraceCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveMatrixMixedWeakStrongReadyTraceCount")
    liveMatrixMixedWeakStrongMissingRunCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveMatrixMixedWeakStrongMissingRunCount")
    liveMatrixMixedWeakStrongGapWatchRunCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveMatrixMixedWeakStrongGapWatchRunCount")
    liveMatrixMixedWeakStrongWeakInputSampleCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveMatrixMixedWeakStrongWeakInputSampleCount")
    liveMatrixMixedWeakStrongStrongInputSampleCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveMatrixMixedWeakStrongStrongInputSampleCount")
    liveMatrixMixedWeakStrongStatusCounts = @(Get-JsonArray $validation "liveMatrixMixedWeakStrongStatusCounts")
    liveMatrixMixedWeakStrongBestRun = Get-JsonValue $validation "liveMatrixMixedWeakStrongBestRun"
    manualTuneObserverReportPresent = Test-Truthy (Get-JsonValue $validation "manualTuneObserverReportPresent")
    manualTuneObserverReportReady = Test-Truthy (Get-JsonValue $validation "manualTuneObserverReportReady")
    manualTuneObserverReportValid = Test-Truthy (Get-JsonValue $validation "manualTuneObserverReportValid")
    manualTuneObserverReportStatus = [string](Get-JsonValue $validation "manualTuneObserverReportStatus")
    manualTuneObserverReportPath = [string](Get-JsonValue $validation "manualTuneObserverReportPath")
    manualTuneObserverReportSha256 = [string](Get-JsonValue $validation "manualTuneObserverReportSha256")
    manualTuneObserverSchemaVersion = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverSchemaVersion")
    manualTuneObserverTool = [string](Get-JsonValue $validation "manualTuneObserverTool")
    manualTuneObserverOk = Test-Truthy (Get-JsonValue $validation "manualTuneObserverOk")
    manualTuneObserverScanError = [string](Get-JsonValue $validation "manualTuneObserverScanError")
    manualTuneObserverBaseUrl = [string](Get-JsonValue $validation "manualTuneObserverBaseUrl")
    manualTuneObserverBundleRelativePaths = Test-Truthy (Get-JsonValue $validation "manualTuneObserverBundleRelativePaths")
    manualTuneObserverScenarioId = [string](Get-JsonValue $validation "manualTuneObserverScenarioId")
    manualTuneObserverComparisonId = [string](Get-JsonValue $validation "manualTuneObserverComparisonId")
    manualTuneObserverPollCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverPollCount")
    manualTuneObserverPollSampleCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverPollSampleCount")
    manualTuneObserverObservedVfoCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverObservedVfoCount")
    manualTuneObserverObservedVfos = @(Get-JsonArray $validation "manualTuneObserverObservedVfos")
    manualTuneObserverBestObservedVfo = Get-JsonValue $validation "manualTuneObserverBestObservedVfo"
    manualTuneObserverBestObservedVfoHz = Get-JsonValue $validation "manualTuneObserverBestObservedVfoHz"
    manualTuneObserverBestObservedVfoMhz = Get-JsonValue $validation "manualTuneObserverBestObservedVfoMhz"
    manualTuneObserverBestObservedVfoStatus = [string](Get-JsonValue $validation "manualTuneObserverBestObservedVfoStatus")
    manualTuneObserverBestObservedVfoScore = Get-JsonValue $validation "manualTuneObserverBestObservedVfoScore"
    manualTuneObserverBestObservedVfoSuggestedVfoHz = Get-JsonValue $validation "manualTuneObserverBestObservedVfoSuggestedVfoHz"
    manualTuneObserverBestObservedVfoSuggestedVfoMhz = Get-JsonValue $validation "manualTuneObserverBestObservedVfoSuggestedVfoMhz"
    manualTuneObserverBestObservedVfoSuggestedDialShiftHz = Get-JsonValue $validation "manualTuneObserverBestObservedVfoSuggestedDialShiftHz"
    manualTuneObserverBestObservedVfoSuggestedTuneReason = [string](Get-JsonValue $validation "manualTuneObserverBestObservedVfoSuggestedTuneReason")
    manualTuneObserverCaptureCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverCaptureCount")
    manualTuneObserverMaxCapturesPerVfo = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverMaxCapturesPerVfo")
    manualTuneObserverUniqueCapturedVfoCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverUniqueCapturedVfoCount")
    manualTuneObserverRecapturedVfoCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverRecapturedVfoCount")
    manualTuneObserverAllowStaleSceneCapture = Test-Truthy (Get-JsonValue $validation "manualTuneObserverAllowStaleSceneCapture")
    manualTuneObserverStaleScenePollCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverStaleScenePollCount")
    manualTuneObserverStaleSceneCaptureCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverStaleSceneCaptureCount")
    manualTuneObserverFrontendNearPassbandPollCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverFrontendNearPassbandPollCount")
    manualTuneObserverFrontendOffPassbandPollCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverFrontendOffPassbandPollCount")
    manualTuneObserverFrontendFilterPassbandPollCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverFrontendFilterPassbandPollCount")
    manualTuneObserverFrontendFilterOffPassbandPollCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverFrontendFilterOffPassbandPollCount")
    manualTuneObserverFrontendOffsetMismatchPollCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverFrontendOffsetMismatchPollCount")
    manualTuneObserverFrontendTuningHintPollCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverFrontendTuningHintPollCount")
    manualTuneObserverFrontendBestTuningHint = Get-JsonValue $validation "manualTuneObserverFrontendBestTuningHint"
    manualTuneObserverFrontendSuggestedDialShiftHz = Get-JsonValue $validation "manualTuneObserverFrontendSuggestedDialShiftHz"
    manualTuneObserverFrontendSuggestedVfoHz = Get-JsonValue $validation "manualTuneObserverFrontendSuggestedVfoHz"
    manualTuneObserverFrontendSuggestedVfoMhz = Get-JsonValue $validation "manualTuneObserverFrontendSuggestedVfoMhz"
    manualTuneObserverFrontendSuggestedPeakOffsetHz = Get-JsonValue $validation "manualTuneObserverFrontendSuggestedPeakOffsetHz"
    manualTuneObserverFrontendSuggestedPeakFrequencyHz = Get-JsonValue $validation "manualTuneObserverFrontendSuggestedPeakFrequencyHz"
    manualTuneObserverFrontendSuggestedFilterCenterOffsetHz = Get-JsonValue $validation "manualTuneObserverFrontendSuggestedFilterCenterOffsetHz"
    manualTuneObserverFrontendSuggestedFilterDistanceHz = Get-JsonValue $validation "manualTuneObserverFrontendSuggestedFilterDistanceHz"
    manualTuneObserverFrontendSuggestedTuneReason = [string](Get-JsonValue $validation "manualTuneObserverFrontendSuggestedTuneReason")
    manualTuneObserverCaptureQualifiedPollCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverCaptureQualifiedPollCount")
    manualTuneObserverReadyCaptureCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverReadyCaptureCount")
    manualTuneObserverMixedWeakStrongReady = Test-Truthy (Get-JsonValue $validation "manualTuneObserverMixedWeakStrongReady")
    manualTuneObserverMixedWeakStrongReadyCaptureCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverMixedWeakStrongReadyCaptureCount")
    manualTuneObserverWeakInputSampleCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverWeakInputSampleCount")
    manualTuneObserverStrongInputSampleCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverStrongInputSampleCount")
    manualTuneObserverNearStrongInputSampleCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverNearStrongInputSampleCount")
    manualTuneObserverSpeechQualifiedWeakInputSampleCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverSpeechQualifiedWeakInputSampleCount")
    manualTuneObserverSpeechQualifiedStrongInputSampleCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverSpeechQualifiedStrongInputSampleCount")
    manualTuneObserverPassbandQualifiedWeakInputSampleCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverPassbandQualifiedWeakInputSampleCount")
    manualTuneObserverPassbandQualifiedStrongInputSampleCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverPassbandQualifiedStrongInputSampleCount")
    manualTuneObserverAgcPumpingRiskCaptureCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverAgcPumpingRiskCaptureCount")
    manualTuneObserverMixedWeakStrongEvidenceStatusCounts = @(Get-JsonArray $validation "manualTuneObserverMixedWeakStrongEvidenceStatusCounts")
    manualTuneObserverMissingStrongInputCaptureCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverMissingStrongInputCaptureCount")
    manualTuneObserverMissingWeakInputCaptureCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverMissingWeakInputCaptureCount")
    manualTuneObserverMissingWeakAndStrongInputCaptureCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverMissingWeakAndStrongInputCaptureCount")
    manualTuneObserverMissingOutputGapCaptureCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverMissingOutputGapCaptureCount")
    manualTuneObserverWeakStrongOutputGapWatchCaptureCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverWeakStrongOutputGapWatchCaptureCount")
    manualTuneObserverWeakOnlyCaptureCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverWeakOnlyCaptureCount")
    manualTuneObserverReadyWeakOnlyCaptureCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverReadyWeakOnlyCaptureCount")
    manualTuneObserverStrongOnlyCaptureCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverStrongOnlyCaptureCount")
    manualTuneObserverBestWeakOnlyCapture = Get-JsonValue $validation "manualTuneObserverBestWeakOnlyCapture"
    manualTuneObserverBestWeakOnlyCaptureScore = Get-JsonValue $validation "manualTuneObserverBestWeakOnlyCaptureScore"
    manualTuneObserverBestWeakOnlyCaptureVfoHz = Get-JsonValue $validation "manualTuneObserverBestWeakOnlyCaptureVfoHz"
    manualTuneObserverBestWeakOnlyCaptureVfoMhz = Get-JsonValue $validation "manualTuneObserverBestWeakOnlyCaptureVfoMhz"
    manualTuneObserverBestWeakOnlyCaptureReportPath = [string](Get-JsonValue $validation "manualTuneObserverBestWeakOnlyCaptureReportPath")
    manualTuneObserverBestWeakOnlyCaptureWeakInputSampleCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverBestWeakOnlyCaptureWeakInputSampleCount")
    manualTuneObserverBestWeakOnlyCaptureNearStrongInputSampleCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverBestWeakOnlyCaptureNearStrongInputSampleCount")
    manualTuneObserverBestWeakOnlyCapturePassbandQualifiedWeakInputSampleCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverBestWeakOnlyCapturePassbandQualifiedWeakInputSampleCount")
    manualTuneObserverBestWeakOnlyCaptureAgcStabilityStatus = [string](Get-JsonValue $validation "manualTuneObserverBestWeakOnlyCaptureAgcStabilityStatus")
    manualTuneObserverNearStrongPromotionCandidateCaptureCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverNearStrongPromotionCandidateCaptureCount")
    manualTuneObserverBestNearStrongPromotionCandidateCapture = Get-JsonValue $validation "manualTuneObserverBestNearStrongPromotionCandidateCapture"
    manualTuneObserverBestNearStrongPromotionCandidateScore = Get-JsonValue $validation "manualTuneObserverBestNearStrongPromotionCandidateScore"
    manualTuneObserverBestNearStrongPromotionCandidateReportPath = [string](Get-JsonValue $validation "manualTuneObserverBestNearStrongPromotionCandidateReportPath")
    manualTuneObserverBestNearStrongPromotionCandidateVfoHz = Get-JsonValue $validation "manualTuneObserverBestNearStrongPromotionCandidateVfoHz"
    manualTuneObserverBestNearStrongPromotionCandidateVfoMhz = Get-JsonValue $validation "manualTuneObserverBestNearStrongPromotionCandidateVfoMhz"
    manualTuneObserverBestNearStrongPromotionCandidateDistanceToStrongThresholdDb = Get-JsonValue $validation "manualTuneObserverBestNearStrongPromotionCandidateDistanceToStrongThresholdDb"
    manualTuneObserverBestNearStrongPromotionCandidateNearStrongInputSampleCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverBestNearStrongPromotionCandidateNearStrongInputSampleCount")
    manualTuneObserverBestNearStrongPromotionCandidateSpeechQualifiedNearStrongInputSampleCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverBestNearStrongPromotionCandidateSpeechQualifiedNearStrongInputSampleCount")
    manualTuneObserverBestNearStrongPromotionCandidatePassbandQualifiedNearStrongInputSampleCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverBestNearStrongPromotionCandidatePassbandQualifiedNearStrongInputSampleCount")
    manualTuneObserverSafetyRxOnly = Test-Truthy (Get-JsonValue $validation "manualTuneObserverSafetyRxOnly")
    manualTuneObserverSafetyReadOnly = Test-Truthy (Get-JsonValue $validation "manualTuneObserverSafetyReadOnly")
    manualTuneObserverSafetyApiWrites = Test-Truthy (Get-JsonValue $validation "manualTuneObserverSafetyApiWrites")
    manualTuneObserverSafetyRetune = Test-Truthy (Get-JsonValue $validation "manualTuneObserverSafetyRetune")
    manualTuneObserverSafetyVfoWriteAttemptCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverSafetyVfoWriteAttemptCount")
    manualTuneObserverSafetyRadioLoWriteAttemptCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverSafetyRadioLoWriteAttemptCount")
    manualTuneObserverSafetyTxEndpointsTouched = Test-Truthy (Get-JsonValue $validation "manualTuneObserverSafetyTxEndpointsTouched")
    manualTuneObserverBestCapture = Get-JsonValue $validation "manualTuneObserverBestCapture"
    manualTuneObserverBestFrequencyHz = Get-JsonValue $validation "manualTuneObserverBestFrequencyHz"
    manualTuneObserverBestStatus = [string](Get-JsonValue $validation "manualTuneObserverBestStatus")
    manualTuneObserverBestReportPath = [string](Get-JsonValue $validation "manualTuneObserverBestReportPath")
    manualTuneObserverBestJsonlPath = [string](Get-JsonValue $validation "manualTuneObserverBestJsonlPath")
    manualTuneObserverReferencedCaptureCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverReferencedCaptureCount")
    manualTuneObserverReferencedCaptureReadyCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverReferencedCaptureReadyCount")
    manualTuneObserverReferencedCaptureProblemCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverReferencedCaptureProblemCount")
    manualTuneObserverReferencedCaptureMissingCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverReferencedCaptureMissingCount")
    manualTuneObserverReferencedCaptureNonPortableCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverReferencedCaptureNonPortableCount")
    manualTuneObserverReferencedCaptureInvalidCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverReferencedCaptureInvalidCount")
    manualTuneObserverReferencedJsonlMissingCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "manualTuneObserverReferencedJsonlMissingCount")
    g2RxPeakHuntReportPresent = Test-Truthy (Get-JsonValue $validation "g2RxPeakHuntReportPresent")
    g2RxPeakHuntReportReady = Test-Truthy (Get-JsonValue $validation "g2RxPeakHuntReportReady")
    g2RxPeakHuntReportValid = Test-Truthy (Get-JsonValue $validation "g2RxPeakHuntReportValid")
    g2RxPeakHuntReportStatus = [string](Get-JsonValue $validation "g2RxPeakHuntReportStatus")
    g2RxPeakHuntReportPath = [string](Get-JsonValue $validation "g2RxPeakHuntReportPath")
    g2RxPeakHuntReportSha256 = [string](Get-JsonValue $validation "g2RxPeakHuntReportSha256")
    g2RxPeakHuntComparisonId = [string](Get-JsonValue $validation "g2RxPeakHuntComparisonId")
    g2RxPeakHuntOk = Test-Truthy (Get-JsonValue $validation "g2RxPeakHuntOk")
    g2RxPeakHuntScanError = [string](Get-JsonValue $validation "g2RxPeakHuntScanError")
    g2RxPeakHuntRequestedBaseUrl = [string](Get-JsonValue $validation "g2RxPeakHuntRequestedBaseUrl")
    g2RxPeakHuntBaseUrl = [string](Get-JsonValue $validation "g2RxPeakHuntBaseUrl")
    g2RxPeakHuntBaseUrlAutoDiscoverRequested = Test-Truthy (Get-JsonValue $validation "g2RxPeakHuntBaseUrlAutoDiscoverRequested")
    g2RxPeakHuntBaseUrlAutoDiscovered = Test-Truthy (Get-JsonValue $validation "g2RxPeakHuntBaseUrlAutoDiscovered")
    g2RxPeakHuntBaseUrlAutoDiscoverError = [string](Get-JsonValue $validation "g2RxPeakHuntBaseUrlAutoDiscoverError")
    g2RxPeakHuntBaseUrlProbeResultCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "g2RxPeakHuntBaseUrlProbeResultCount")
    g2RxPeakHuntAllowRetune = Test-Truthy (Get-JsonValue $validation "g2RxPeakHuntAllowRetune")
    g2RxPeakHuntSkipCurrentVfo = Test-Truthy (Get-JsonValue $validation "g2RxPeakHuntSkipCurrentVfo")
    g2RxPeakHuntStopOnReady = Test-Truthy (Get-JsonValue $validation "g2RxPeakHuntStopOnReady")
    g2RxPeakHuntSamplesPerWindow = Get-IntegerValueOrDefault (Get-JsonValue $validation "g2RxPeakHuntSamplesPerWindow")
    g2RxPeakHuntIntervalMs = Get-IntegerValueOrDefault (Get-JsonValue $validation "g2RxPeakHuntIntervalMs")
    g2RxPeakHuntPassCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "g2RxPeakHuntPassCount")
    g2RxPeakHuntPassDelaySec = Get-IntegerValueOrDefault (Get-JsonValue $validation "g2RxPeakHuntPassDelaySec")
    g2RxPeakHuntCompletedPassCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "g2RxPeakHuntCompletedPassCount")
    g2RxPeakHuntScanPassCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "g2RxPeakHuntScanPassCount")
    g2RxPeakHuntCandidateFrequencyHzCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "g2RxPeakHuntCandidateFrequencyHzCount")
    g2RxPeakHuntAutoPhoneCluster = Test-Truthy (Get-JsonValue $validation "g2RxPeakHuntAutoPhoneCluster")
    g2RxPeakHuntAutoPhoneClusterCandidateFrequencyHzCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "g2RxPeakHuntAutoPhoneClusterCandidateFrequencyHzCount")
    g2RxPeakHuntAutoPhoneClusterCandidateCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "g2RxPeakHuntAutoPhoneClusterCandidateCount")
    g2RxPeakHuntAutoPhoneClusterExactCandidateCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "g2RxPeakHuntAutoPhoneClusterExactCandidateCount")
    g2RxPeakHuntAutoPhoneClusterNeighborCandidateCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "g2RxPeakHuntAutoPhoneClusterNeighborCandidateCount")
    g2RxPeakHuntAutoPhoneClusterMaxCandidates = Get-IntegerValueOrDefault (Get-JsonValue $validation "g2RxPeakHuntAutoPhoneClusterMaxCandidates")
    g2RxPeakHuntAutoPhoneClusterLookbackHours = Get-IntegerValueOrDefault (Get-JsonValue $validation "g2RxPeakHuntAutoPhoneClusterLookbackHours")
    g2RxPeakHuntAutoPhoneClusterMinSpeechSamples = Get-IntegerValueOrDefault (Get-JsonValue $validation "g2RxPeakHuntAutoPhoneClusterMinSpeechSamples")
    g2RxPeakHuntAutoPhoneClusterBandLowHz = Get-JsonValue $validation "g2RxPeakHuntAutoPhoneClusterBandLowHz"
    g2RxPeakHuntAutoPhoneClusterBandHighHz = Get-JsonValue $validation "g2RxPeakHuntAutoPhoneClusterBandHighHz"
    g2RxPeakHuntOperatorCandidateCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "g2RxPeakHuntOperatorCandidateCount")
    g2RxPeakHuntPlannedRunCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "g2RxPeakHuntPlannedRunCount")
    g2RxPeakHuntActualRunCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "g2RxPeakHuntActualRunCount")
    g2RxPeakHuntFailedRunCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "g2RxPeakHuntFailedRunCount")
    g2RxPeakHuntMixedWeakStrongReady = Test-Truthy (Get-JsonValue $validation "g2RxPeakHuntMixedWeakStrongReady")
    g2RxPeakHuntMixedWeakStrongReadyRunCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "g2RxPeakHuntMixedWeakStrongReadyRunCount")
    g2RxPeakHuntWeakInputSampleCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "g2RxPeakHuntWeakInputSampleCount")
    g2RxPeakHuntStrongInputSampleCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "g2RxPeakHuntStrongInputSampleCount")
    g2RxPeakHuntNearStrongInputSampleCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "g2RxPeakHuntNearStrongInputSampleCount")
    g2RxPeakHuntSpeechQualifiedWeakInputSampleCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "g2RxPeakHuntSpeechQualifiedWeakInputSampleCount")
    g2RxPeakHuntSpeechQualifiedStrongInputSampleCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "g2RxPeakHuntSpeechQualifiedStrongInputSampleCount")
    g2RxPeakHuntSpeechQualifiedNearStrongInputSampleCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "g2RxPeakHuntSpeechQualifiedNearStrongInputSampleCount")
    g2RxPeakHuntPassbandQualifiedWeakInputSampleCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "g2RxPeakHuntPassbandQualifiedWeakInputSampleCount")
    g2RxPeakHuntPassbandQualifiedStrongInputSampleCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "g2RxPeakHuntPassbandQualifiedStrongInputSampleCount")
    g2RxPeakHuntPassbandQualifiedNearStrongInputSampleCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "g2RxPeakHuntPassbandQualifiedNearStrongInputSampleCount")
    g2RxPeakHuntFrontendNearPassbandSampleCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "g2RxPeakHuntFrontendNearPassbandSampleCount")
    g2RxPeakHuntCandidateWeakLossSampleCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "g2RxPeakHuntCandidateWeakLossSampleCount")
    g2RxPeakHuntHotMakeupSampleCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "g2RxPeakHuntHotMakeupSampleCount")
    g2RxPeakHuntHardBlockerSampleCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "g2RxPeakHuntHardBlockerSampleCount")
    g2RxPeakHuntAgcPumpingRiskRunCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "g2RxPeakHuntAgcPumpingRiskRunCount")
    g2RxPeakHuntPeakCandidateCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "g2RxPeakHuntPeakCandidateCount")
    g2RxPeakHuntRetuneAttemptCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "g2RxPeakHuntRetuneAttemptCount")
    g2RxPeakHuntSafetyRxOnly = Test-Truthy (Get-JsonValue $validation "g2RxPeakHuntSafetyRxOnly")
    g2RxPeakHuntSafetyTxEndpointsTouched = Test-Truthy (Get-JsonValue $validation "g2RxPeakHuntSafetyTxEndpointsTouched")
    g2RxPeakHuntSafetyOriginalVfoRestoreAttempted = Test-Truthy (Get-JsonValue $validation "g2RxPeakHuntSafetyOriginalVfoRestoreAttempted")
    g2RxPeakHuntSafetyOriginalVfoRestored = Test-Truthy (Get-JsonValue $validation "g2RxPeakHuntSafetyOriginalVfoRestored")
    g2RxPeakHuntSafetyOriginalRadioLoRestoreAttempted = Test-Truthy (Get-JsonValue $validation "g2RxPeakHuntSafetyOriginalRadioLoRestoreAttempted")
    g2RxPeakHuntSafetyOriginalRadioLoRestored = Test-Truthy (Get-JsonValue $validation "g2RxPeakHuntSafetyOriginalRadioLoRestored")
    g2RxPeakHuntSafetyRestoreError = [string](Get-JsonValue $validation "g2RxPeakHuntSafetyRestoreError")
    g2RxPeakHuntHardwareConnectionStatus = [string](Get-JsonValue $validation "g2RxPeakHuntHardwareConnectionStatus")
    g2RxPeakHuntHardwareEndpoint = [string](Get-JsonValue $validation "g2RxPeakHuntHardwareEndpoint")
    g2RxPeakHuntHardwareEffectiveBoard = [string](Get-JsonValue $validation "g2RxPeakHuntHardwareEffectiveBoard")
    g2RxPeakHuntHardwareVariant = [string](Get-JsonValue $validation "g2RxPeakHuntHardwareVariant")
    g2RxPeakHuntHardwareOriginalVfoHz = Get-JsonValue $validation "g2RxPeakHuntHardwareOriginalVfoHz"
    g2RxPeakHuntHardwareRestoredVfoHz = Get-JsonValue $validation "g2RxPeakHuntHardwareRestoredVfoHz"
    g2RxPeakHuntHardwareOriginalRadioLoHz = Get-JsonValue $validation "g2RxPeakHuntHardwareOriginalRadioLoHz"
    g2RxPeakHuntHardwareRestoredRadioLoHz = Get-JsonValue $validation "g2RxPeakHuntHardwareRestoredRadioLoHz"
    g2RxPeakHuntHardwareSampleRate = Get-IntegerValueOrDefault (Get-JsonValue $validation "g2RxPeakHuntHardwareSampleRate")
    g2RxPeakHuntLiveDiagnosticsStatus = [string](Get-JsonValue $validation "g2RxPeakHuntLiveDiagnosticsStatus")
    g2RxPeakHuntLiveDiagnosticsReadyForLiveBenchmark = Test-Truthy (Get-JsonValue $validation "g2RxPeakHuntLiveDiagnosticsReadyForLiveBenchmark")
    g2RxPeakHuntLiveDiagnosticsWdspActive = Test-Truthy (Get-JsonValue $validation "g2RxPeakHuntLiveDiagnosticsWdspActive")
    g2RxPeakHuntLiveDiagnosticsWdspNativeLoadable = Test-Truthy (Get-JsonValue $validation "g2RxPeakHuntLiveDiagnosticsWdspNativeLoadable")
    g2RxPeakHuntLiveDiagnosticsRequestedNrMode = [string](Get-JsonValue $validation "g2RxPeakHuntLiveDiagnosticsRequestedNrMode")
    g2RxPeakHuntLiveDiagnosticsEffectiveNrMode = [string](Get-JsonValue $validation "g2RxPeakHuntLiveDiagnosticsEffectiveNrMode")
    g2RxPeakHuntLiveDiagnosticsReadyForNr5Tuning = Test-Truthy (Get-JsonValue $validation "g2RxPeakHuntLiveDiagnosticsReadyForNr5Tuning")
    g2RxPeakHuntLiveDiagnosticsFrontendSceneFresh = Test-Truthy (Get-JsonValue $validation "g2RxPeakHuntLiveDiagnosticsFrontendSceneFresh")
    g2RxPeakHuntFrontendSceneStatus = [string](Get-JsonValue $validation "g2RxPeakHuntFrontendSceneStatus")
    g2RxPeakHuntFrontendSceneFresh = Test-Truthy (Get-JsonValue $validation "g2RxPeakHuntFrontendSceneFresh")
    g2RxPeakHuntFrontendSceneSignalProfile = [string](Get-JsonValue $validation "g2RxPeakHuntFrontendSceneSignalProfile")
    g2RxPeakHuntFrontendSceneTopPeakCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "g2RxPeakHuntFrontendSceneTopPeakCount")
    g2RxPeakHuntBestRun = Get-JsonValue $validation "g2RxPeakHuntBestRun"
    g2RxPeakHuntBestFrequencyHz = Get-JsonValue $validation "g2RxPeakHuntBestFrequencyHz"
    g2RxPeakHuntBestScore = Get-JsonValue $validation "g2RxPeakHuntBestScore"
    g2RxPeakHuntBestStatus = [string](Get-JsonValue $validation "g2RxPeakHuntBestStatus")
    g2RxPeakHuntBestReportPath = [string](Get-JsonValue $validation "g2RxPeakHuntBestReportPath")
    g2RxPeakHuntBestJsonlPath = [string](Get-JsonValue $validation "g2RxPeakHuntBestJsonlPath")
    g2RxPeakHuntReferencedWindowCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "g2RxPeakHuntReferencedWindowCount")
    g2RxPeakHuntReferencedWindowReadyCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "g2RxPeakHuntReferencedWindowReadyCount")
    g2RxPeakHuntReferencedWindowProblemCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "g2RxPeakHuntReferencedWindowProblemCount")
    g2RxPeakHuntReferencedWindowMissingCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "g2RxPeakHuntReferencedWindowMissingCount")
    g2RxPeakHuntReferencedWindowNonPortableCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "g2RxPeakHuntReferencedWindowNonPortableCount")
    g2RxPeakHuntReferencedWindowInvalidCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "g2RxPeakHuntReferencedWindowInvalidCount")
    g2RxPeakHuntReferencedJsonlMissingCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "g2RxPeakHuntReferencedJsonlMissingCount")
    liveMatrixArtifactControlStatus = [string](Get-JsonValue $validation "liveMatrixArtifactControlStatus")
    liveMatrixArtifactControlReportCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveMatrixArtifactControlReportCount")
    liveMatrixArtifactControlSchemaV3ReportCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveMatrixArtifactControlSchemaV3ReportCount")
    liveMatrixArtifactControlReviewRunCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveMatrixArtifactControlReviewRunCount")
    liveMatrixArtifactControlRiskScoreMax = Get-JsonValue $validation "liveMatrixArtifactControlRiskScoreMax"
    liveMatrixArtifactControlLowEvidenceLiftedSampleCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveMatrixArtifactControlLowEvidenceLiftedSampleCount")
    liveMatrixArtifactControlLowEvidenceLiftedPctMax = Get-JsonValue $validation "liveMatrixArtifactControlLowEvidenceLiftedPctMax"
    liveMatrixArtifactControlAudioAlignmentMismatchPctMax = Get-JsonValue $validation "liveMatrixArtifactControlAudioAlignmentMismatchPctMax"
    liveMatrixArtifactControlStatusCounts = @(Get-JsonArray $validation "liveMatrixArtifactControlStatusCounts")
    liveAcceptanceCycleTriageExitCode = Get-JsonValue $validation "liveAcceptanceCycleTriageExitCode"
    liveAcceptanceCycleTriageAcceptanceActionPlanCount = [int](Get-JsonValue $validation "liveAcceptanceCycleTriageAcceptanceActionPlanCount")
    liveAcceptanceCycleTriageAcceptanceRequiredActionCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveAcceptanceCycleTriageAcceptanceRequiredActionCount")
    liveAcceptanceCycleTriageAcceptanceManualActionCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveAcceptanceCycleTriageAcceptanceManualActionCount")
    liveAcceptanceCycleTriageAcceptanceActionCategoryCounts = @(Get-JsonArray $validation "liveAcceptanceCycleTriageAcceptanceActionCategoryCounts")
    liveAcceptanceCycleTriagePrimaryAcceptanceActionId = [string](Get-JsonValue $validation "liveAcceptanceCycleTriagePrimaryAcceptanceActionId")
    liveAcceptanceCycleTriagePrimaryAcceptanceActionPriority = Get-JsonValue $validation "liveAcceptanceCycleTriagePrimaryAcceptanceActionPriority"
    liveAcceptanceCycleTriagePrimaryAcceptanceActionStageId = [string](Get-JsonValue $validation "liveAcceptanceCycleTriagePrimaryAcceptanceActionStageId")
    liveAcceptanceCycleTriagePrimaryAcceptanceActionGateId = [string](Get-JsonValue $validation "liveAcceptanceCycleTriagePrimaryAcceptanceActionGateId")
    liveAcceptanceCycleTriagePrimaryAcceptanceActionCategory = [string](Get-JsonValue $validation "liveAcceptanceCycleTriagePrimaryAcceptanceActionCategory")
    liveAcceptanceCycleTriagePrimaryAcceptanceActionRequired = Test-Truthy (Get-JsonValue $validation "liveAcceptanceCycleTriagePrimaryAcceptanceActionRequired")
    liveAcceptanceCycleTriagePrimaryAcceptanceActionManual = Test-Truthy (Get-JsonValue $validation "liveAcceptanceCycleTriagePrimaryAcceptanceActionManual")
    liveAcceptanceCycleTriagePrimaryAcceptanceCommandTemplate = [string](Get-JsonValue $validation "liveAcceptanceCycleTriagePrimaryAcceptanceCommandTemplate")
    liveAcceptanceCycleTriagePrimaryAcceptanceCommandStepCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveAcceptanceCycleTriagePrimaryAcceptanceCommandStepCount")
    liveAcceptanceCycleTriagePrimaryAcceptanceCommandSteps = @(Get-JsonArray $validation "liveAcceptanceCycleTriagePrimaryAcceptanceCommandSteps")
    liveAcceptanceCycleTriagePrimaryAcceptanceManualAction = [string](Get-JsonValue $validation "liveAcceptanceCycleTriagePrimaryAcceptanceManualAction")
    liveAcceptanceCycleTriagePrimaryAcceptanceExpectedArtifact = [string](Get-JsonValue $validation "liveAcceptanceCycleTriagePrimaryAcceptanceExpectedArtifact")
    liveAcceptanceCycleTriagePrimaryAcceptanceExpectedArtifactCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveAcceptanceCycleTriagePrimaryAcceptanceExpectedArtifactCount")
    liveAcceptanceCycleTriagePrimaryAcceptanceExpectedArtifacts = @(Get-JsonArray $validation "liveAcceptanceCycleTriagePrimaryAcceptanceExpectedArtifacts")
    liveAcceptanceCycleTriagePrimaryAcceptanceFollowUp = [string](Get-JsonValue $validation "liveAcceptanceCycleTriagePrimaryAcceptanceFollowUp")
    liveAcceptanceCycleTriageExternalEngineBakeoffActionPresent = Test-Truthy (Get-JsonValue $validation "liveAcceptanceCycleTriageExternalEngineBakeoffActionPresent")
    liveAcceptanceCycleTriageExternalEngineBakeoffActionId = [string](Get-JsonValue $validation "liveAcceptanceCycleTriageExternalEngineBakeoffActionId")
    liveAcceptanceCycleTriageExternalEngineBakeoffActionPriority = Get-JsonValue $validation "liveAcceptanceCycleTriageExternalEngineBakeoffActionPriority"
    liveAcceptanceCycleTriageExternalEngineBakeoffActionStageId = [string](Get-JsonValue $validation "liveAcceptanceCycleTriageExternalEngineBakeoffActionStageId")
    liveAcceptanceCycleTriageExternalEngineBakeoffActionGateId = [string](Get-JsonValue $validation "liveAcceptanceCycleTriageExternalEngineBakeoffActionGateId")
    liveAcceptanceCycleTriageExternalEngineBakeoffActionCategory = [string](Get-JsonValue $validation "liveAcceptanceCycleTriageExternalEngineBakeoffActionCategory")
    liveAcceptanceCycleTriageExternalEngineBakeoffActionRequired = Test-Truthy (Get-JsonValue $validation "liveAcceptanceCycleTriageExternalEngineBakeoffActionRequired")
    liveAcceptanceCycleTriageExternalEngineBakeoffActionManual = Test-Truthy (Get-JsonValue $validation "liveAcceptanceCycleTriageExternalEngineBakeoffActionManual")
    liveAcceptanceCycleTriageExternalEngineBakeoffCommandTemplate = [string](Get-JsonValue $validation "liveAcceptanceCycleTriageExternalEngineBakeoffCommandTemplate")
    liveAcceptanceCycleTriageExternalEngineBakeoffCommandStepCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveAcceptanceCycleTriageExternalEngineBakeoffCommandStepCount")
    liveAcceptanceCycleTriageExternalEngineBakeoffCommandSteps = @(Get-JsonArray $validation "liveAcceptanceCycleTriageExternalEngineBakeoffCommandSteps")
    liveAcceptanceCycleTriageExternalEngineBakeoffManualAction = [string](Get-JsonValue $validation "liveAcceptanceCycleTriageExternalEngineBakeoffManualAction")
    liveAcceptanceCycleTriageExternalEngineBakeoffExpectedArtifact = [string](Get-JsonValue $validation "liveAcceptanceCycleTriageExternalEngineBakeoffExpectedArtifact")
    liveAcceptanceCycleTriageExternalEngineBakeoffExpectedArtifactCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveAcceptanceCycleTriageExternalEngineBakeoffExpectedArtifactCount")
    liveAcceptanceCycleTriageExternalEngineBakeoffExpectedArtifacts = @(Get-JsonArray $validation "liveAcceptanceCycleTriageExternalEngineBakeoffExpectedArtifacts")
    liveAcceptanceCycleTriageExternalEngineBakeoffFollowUp = [string](Get-JsonValue $validation "liveAcceptanceCycleTriageExternalEngineBakeoffFollowUp")
    liveAcceptanceCycleTriagePureSignalSafeBypassActionPresent = Test-Truthy (Get-JsonValue $validation "liveAcceptanceCycleTriagePureSignalSafeBypassActionPresent")
    liveAcceptanceCycleTriagePureSignalSafeBypassActionId = [string](Get-JsonValue $validation "liveAcceptanceCycleTriagePureSignalSafeBypassActionId")
    liveAcceptanceCycleTriagePureSignalSafeBypassActionPriority = Get-JsonValue $validation "liveAcceptanceCycleTriagePureSignalSafeBypassActionPriority"
    liveAcceptanceCycleTriagePureSignalSafeBypassActionStageId = [string](Get-JsonValue $validation "liveAcceptanceCycleTriagePureSignalSafeBypassActionStageId")
    liveAcceptanceCycleTriagePureSignalSafeBypassActionGateId = [string](Get-JsonValue $validation "liveAcceptanceCycleTriagePureSignalSafeBypassActionGateId")
    liveAcceptanceCycleTriagePureSignalSafeBypassActionCategory = [string](Get-JsonValue $validation "liveAcceptanceCycleTriagePureSignalSafeBypassActionCategory")
    liveAcceptanceCycleTriagePureSignalSafeBypassActionRequired = Test-Truthy (Get-JsonValue $validation "liveAcceptanceCycleTriagePureSignalSafeBypassActionRequired")
    liveAcceptanceCycleTriagePureSignalSafeBypassActionManual = Test-Truthy (Get-JsonValue $validation "liveAcceptanceCycleTriagePureSignalSafeBypassActionManual")
    liveAcceptanceCycleTriagePureSignalSafeBypassCommandTemplate = [string](Get-JsonValue $validation "liveAcceptanceCycleTriagePureSignalSafeBypassCommandTemplate")
    liveAcceptanceCycleTriagePureSignalSafeBypassCommandStepCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveAcceptanceCycleTriagePureSignalSafeBypassCommandStepCount")
    liveAcceptanceCycleTriagePureSignalSafeBypassCommandSteps = @(Get-JsonArray $validation "liveAcceptanceCycleTriagePureSignalSafeBypassCommandSteps")
    liveAcceptanceCycleTriagePureSignalSafeBypassManualAction = [string](Get-JsonValue $validation "liveAcceptanceCycleTriagePureSignalSafeBypassManualAction")
    liveAcceptanceCycleTriagePureSignalSafeBypassExpectedArtifact = [string](Get-JsonValue $validation "liveAcceptanceCycleTriagePureSignalSafeBypassExpectedArtifact")
    liveAcceptanceCycleTriagePureSignalSafeBypassExpectedArtifactCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "liveAcceptanceCycleTriagePureSignalSafeBypassExpectedArtifactCount")
    liveAcceptanceCycleTriagePureSignalSafeBypassExpectedArtifacts = @(Get-JsonArray $validation "liveAcceptanceCycleTriagePureSignalSafeBypassExpectedArtifacts")
    liveAcceptanceCycleTriagePureSignalSafeBypassFollowUp = [string](Get-JsonValue $validation "liveAcceptanceCycleTriagePureSignalSafeBypassFollowUp")
    liveAcceptanceCycleRequiredLiveAcceptanceArtifactProblemCount = [int](Get-JsonValue $validation "liveAcceptanceCycleRequiredLiveAcceptanceArtifactProblemCount")
    liveAcceptanceCycleSummaryPath = [string](Get-JsonValue $validation "liveAcceptanceCycleSummaryPath")
    liveAcceptanceCycleSummarySha256 = [string](Get-JsonValue $validation "liveAcceptanceCycleSummarySha256")
    liveHistoryTraceSourceCount = $liveHistorySourceRecords.Count
    liveHistoryTraceSourceProblemCount = $failedLiveSources.Count
    sourceTypeCounts = @(Convert-CountsToRecords -Counts $sourceTypeCounts -NameField "sourceType")
    liveHistoryTraceSourceStatusCounts = @(Convert-CountsToRecords -Counts $liveSourceStatusCounts -NameField "sourceStatus")
    liveHistoryTraceSummaryHashStatusCounts = @(Convert-CountsToRecords -Counts $liveSummaryHashStatusCounts -NameField "summaryHashStatus")
    liveHistoryTraceJsonlHashStatusCounts = @(Convert-CountsToRecords -Counts $liveJsonlHashStatusCounts -NameField "jsonlHashStatus")
    evidenceGateCount = $evidenceGates.Count
    evidenceGateProblemCount = $evidenceGateProblems.Count
    evidenceGateProblemIds = @($evidenceGateProblemIds)
    evidenceGateProblemNames = @($evidenceGateProblemNames)
    requiredEvidenceGateProblemCount = $requiredEvidenceGateProblems.Count
    requiredEvidenceGateProblemIds = @($requiredEvidenceGateProblemIds)
    requiredEvidenceGateProblemNames = @($requiredEvidenceGateProblemNames)
    advisoryEvidenceGateProblemCount = $advisoryEvidenceGateProblems.Count
    advisoryEvidenceGateProblemIds = @($advisoryEvidenceGateProblemIds)
    advisoryEvidenceGateProblemNames = @($advisoryEvidenceGateProblemNames)
    evidenceGates = @($evidenceGates)
    benchmarkPlanStatus = [string](Get-JsonValue $validation "benchmarkPlanStatus")
    benchmarkPlanScenarioCount = [int](Get-JsonValue $validation "benchmarkPlanScenarioCount")
    benchmarkPlanScenarioIds = @(Get-JsonArray $validation "benchmarkPlanScenarioIds")
    benchmarkPlanRequiredAcceptanceScenarioFamilyCount = [int](Get-JsonValue $validation "benchmarkPlanRequiredAcceptanceScenarioFamilyCount")
    benchmarkPlanCoveredAcceptanceScenarioFamilyCount = [int](Get-JsonValue $validation "benchmarkPlanCoveredAcceptanceScenarioFamilyCount")
    benchmarkPlanMissingAcceptanceScenarioFamilyCount = [int](Get-JsonValue $validation "benchmarkPlanMissingAcceptanceScenarioFamilyCount")
    benchmarkPlanMissingAcceptanceScenarioFamilyIds = @(Get-JsonArray $validation "benchmarkPlanMissingAcceptanceScenarioFamilyIds")
    benchmarkPlanScenarioFamilyCoverage = @(Get-JsonArray $validation "benchmarkPlanScenarioFamilyCoverage")
    benchmarkPlanScenarioMissingRequiredComparisonCount = [int](Get-JsonValue $validation "benchmarkPlanScenarioMissingRequiredComparisonCount")
    benchmarkPlanScenarioMissingRequiredComparisonIds = @(Get-JsonArray $validation "benchmarkPlanScenarioMissingRequiredComparisonIds")
    benchmarkPlanScenarioMissingRequiredMetricCount = [int](Get-JsonValue $validation "benchmarkPlanScenarioMissingRequiredMetricCount")
    benchmarkPlanScenarioMissingRequiredMetricIds = @(Get-JsonArray $validation "benchmarkPlanScenarioMissingRequiredMetricIds")
    benchmarkPlanScenarioMissingAcceptanceGateCount = [int](Get-JsonValue $validation "benchmarkPlanScenarioMissingAcceptanceGateCount")
    benchmarkPlanScenarioMissingAcceptanceGateIds = @(Get-JsonArray $validation "benchmarkPlanScenarioMissingAcceptanceGateIds")
    metricCatalogPresent = Test-Truthy (Get-JsonValue $validation "metricCatalogPresent")
    metricCatalogStatus = [string](Get-JsonValue $validation "metricCatalogStatus")
    metricCatalogMetricCount = [int](Get-JsonValue $validation "metricCatalogMetricCount")
    metricCatalogRequiredMetricCount = [int](Get-JsonValue $validation "metricCatalogRequiredMetricCount")
    metricCatalogAcceptanceContractReady = Test-Truthy (Get-JsonValue $validation "metricCatalogAcceptanceContractReady")
    metricCatalogMissingRequiredMetricCount = [int](Get-JsonValue $validation "metricCatalogMissingRequiredMetricCount")
    metricCatalogMissingRequiredMetricIds = @(Get-JsonArray $validation "metricCatalogMissingRequiredMetricIds")
    metricCatalogInvalidDirectionCount = [int](Get-JsonValue $validation "metricCatalogInvalidDirectionCount")
    metricCatalogInvalidDirectionMetricIds = @(Get-JsonArray $validation "metricCatalogInvalidDirectionMetricIds")
    metricCatalogMissingThresholdCount = [int](Get-JsonValue $validation "metricCatalogMissingThresholdCount")
    metricCatalogMissingThresholdMetricIds = @(Get-JsonArray $validation "metricCatalogMissingThresholdMetricIds")
    metricCatalogMissingComparatorCount = [int](Get-JsonValue $validation "metricCatalogMissingComparatorCount")
    metricCatalogMissingComparatorMetricIds = @(Get-JsonArray $validation "metricCatalogMissingComparatorMetricIds")
    metricCatalogInvalidComparatorCount = [int](Get-JsonValue $validation "metricCatalogInvalidComparatorCount")
    metricCatalogInvalidComparatorMetricIds = @(Get-JsonArray $validation "metricCatalogInvalidComparatorMetricIds")
    metricCatalogMissingUnitCount = [int](Get-JsonValue $validation "metricCatalogMissingUnitCount")
    metricCatalogMissingUnitMetricIds = @(Get-JsonArray $validation "metricCatalogMissingUnitMetricIds")
    metricCatalogMissingSafetyClassCount = [int](Get-JsonValue $validation "metricCatalogMissingSafetyClassCount")
    metricCatalogMissingSafetyClassMetricIds = @(Get-JsonArray $validation "metricCatalogMissingSafetyClassMetricIds")
    metricCatalogMissingAcceptanceScopeCount = [int](Get-JsonValue $validation "metricCatalogMissingAcceptanceScopeCount")
    metricCatalogMissingAcceptanceScopeMetricIds = @(Get-JsonArray $validation "metricCatalogMissingAcceptanceScopeMetricIds")
    metricCatalogContractProblemMetricCount = [int](Get-JsonValue $validation "metricCatalogContractProblemMetricCount")
    metricCatalogContractProblemMetricIds = @(Get-JsonArray $validation "metricCatalogContractProblemMetricIds")
    externalEngineCandidateStatus = [string](Get-JsonValue $validation "externalEngineCandidateStatus")
    externalEngineCandidateCount = [int](Get-JsonValue $validation "externalEngineCandidateCount")
    externalEngineCandidateIds = @(Get-JsonArray $validation "externalEngineCandidateIds")
    externalEngineCandidateMissingCount = [int](Get-JsonValue $validation "externalEngineCandidateMissingCount")
    externalEngineCandidateMissingIds = @(Get-JsonArray $validation "externalEngineCandidateMissingIds")
    externalEngineCandidateUnsafeCount = [int](Get-JsonValue $validation "externalEngineCandidateUnsafeCount")
    externalEngineCandidateUnsafeIds = @(Get-JsonArray $validation "externalEngineCandidateUnsafeIds")
    externalEngineCandidateUnsafeDetails = @(Get-JsonArray $validation "externalEngineCandidateUnsafeDetails")
    externalEngineCandidateIssueCounts = @(Get-JsonArray $validation "externalEngineCandidateIssueCounts")
    externalEngineCandidateSnapshotMismatchCount = [int](Get-JsonValue $validation "externalEngineCandidateSnapshotMismatchCount")
    externalEngineCandidateSnapshotMissingIds = @(Get-JsonArray $validation "externalEngineCandidateSnapshotMissingIds")
    externalEngineCandidateSnapshotExtraIds = @(Get-JsonArray $validation "externalEngineCandidateSnapshotExtraIds")
    externalEngineBakeoffReportPresent = Test-Truthy (Get-JsonValue $validation "externalEngineBakeoffReportPresent")
    externalEngineBakeoffReady = Test-Truthy (Get-JsonValue $validation "externalEngineBakeoffReady")
    externalEngineBakeoffRequiredByScope = Test-Truthy (Get-JsonValue $validation "externalEngineBakeoffRequiredByScope")
    externalEngineBakeoffScopeTriggerCount = [int](Get-JsonValue $validation "externalEngineBakeoffScopeTriggerCount")
    externalEngineBakeoffScopeTriggers = @(Get-JsonArray $validation "externalEngineBakeoffScopeTriggers")
    externalEngineBakeoffCandidateCount = [int](Get-JsonValue $validation "externalEngineBakeoffCandidateCount")
    externalEngineBakeoffSafeForBakeoffCount = [int](Get-JsonValue $validation "externalEngineBakeoffSafeForBakeoffCount")
    externalEngineBakeoffBlockedCandidateCount = [int](Get-JsonValue $validation "externalEngineBakeoffBlockedCandidateCount")
    externalEngineBakeoffBlockedCandidateIds = @(Get-JsonArray $validation "externalEngineBakeoffBlockedCandidateIds")
    externalEngineBakeoffBlockedCandidateDetails = @(Get-JsonArray $validation "externalEngineBakeoffBlockedCandidateDetails")
    externalEngineBakeoffIntegrationReadyCandidateCount = [int](Get-JsonValue $validation "externalEngineBakeoffIntegrationReadyCandidateCount")
    externalEngineBakeoffIntegrationReadyCandidateIds = @(Get-JsonArray $validation "externalEngineBakeoffIntegrationReadyCandidateIds")
    externalEngineBakeoffMissingCandidateCount = [int](Get-JsonValue $validation "externalEngineBakeoffMissingCandidateCount")
    externalEngineBakeoffMissingCandidateIds = @(Get-JsonArray $validation "externalEngineBakeoffMissingCandidateIds")
    externalEngineBakeoffUnsafeCandidateCount = [int](Get-JsonValue $validation "externalEngineBakeoffUnsafeCandidateCount")
    externalEngineBakeoffUnsafeCandidateIds = @(Get-JsonArray $validation "externalEngineBakeoffUnsafeCandidateIds")
    externalEngineBakeoffUnsafeCandidateDetails = @(Get-JsonArray $validation "externalEngineBakeoffUnsafeCandidateDetails")
    externalEngineBakeoffSnapshotMismatchCount = [int](Get-JsonValue $validation "externalEngineBakeoffSnapshotMismatchCount")
    externalEngineBakeoffSnapshotMismatchCandidateIds = @(Get-JsonArray $validation "externalEngineBakeoffSnapshotMismatchCandidateIds")
    externalEngineBakeoffCandidateIssueCounts = @(Get-JsonArray $validation "externalEngineBakeoffCandidateIssueCounts")
    externalEngineBakeoffPlanScenarioIds = @(Get-JsonArray $validation "externalEngineBakeoffPlanScenarioIds")
    externalEngineBakeoffPlanFixtureComparisonCommandTemplate = [string](Get-JsonValue $validation "externalEngineBakeoffPlanFixtureComparisonCommandTemplate")
    externalEngineBakeoffPlanLiveMatrixCommandTemplate = [string](Get-JsonValue $validation "externalEngineBakeoffPlanLiveMatrixCommandTemplate")
    externalEngineBakeoffFirstSafeCandidateId = [string](Get-JsonValue $validation "externalEngineBakeoffFirstSafeCandidateId")
    externalEngineBakeoffFirstSafeScenarioIds = @(Get-JsonArray $validation "externalEngineBakeoffFirstSafeScenarioIds")
    externalEngineBakeoffEvaluationOrderCandidateIds = @(Get-JsonArray $validation "externalEngineBakeoffEvaluationOrderCandidateIds")
    externalEngineBakeoffEvaluationOrder = @(Get-JsonArray $validation "externalEngineBakeoffEvaluationOrder")
    externalEngineBakeoffCycleSummaryPresent = Test-Truthy (Get-JsonValue $validation "externalEngineBakeoffCycleSummaryPresent")
    externalEngineBakeoffCycleSummaryValid = Test-Truthy (Get-JsonValue $validation "externalEngineBakeoffCycleSummaryValid")
    externalEngineBakeoffCycleStatus = [string](Get-JsonValue $validation "externalEngineBakeoffCycleStatus")
    externalEngineBakeoffCycleMode = [string](Get-JsonValue $validation "externalEngineBakeoffCycleMode")
    externalEngineBakeoffCycleCandidateId = [string](Get-JsonValue $validation "externalEngineBakeoffCycleCandidateId")
    externalEngineBakeoffCycleComparisonId = [string](Get-JsonValue $validation "externalEngineBakeoffCycleComparisonId")
    externalEngineBakeoffCycleScenarioIds = @(Get-JsonArray $validation "externalEngineBakeoffCycleScenarioIds")
    externalEngineBakeoffCycleReadyToExecute = Test-Truthy (Get-JsonValue $validation "externalEngineBakeoffCycleReadyToExecute")
    externalEngineBakeoffCycleExecuted = Test-Truthy (Get-JsonValue $validation "externalEngineBakeoffCycleExecuted")
    externalEngineBakeoffCycleMissingPrerequisiteCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "externalEngineBakeoffCycleMissingPrerequisiteCount")
    externalEngineBakeoffCycleCommandStepCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "externalEngineBakeoffCycleCommandStepCount")
    externalEngineBakeoffCycleExpectedArtifactCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "externalEngineBakeoffCycleExpectedArtifactCount")
    externalEngineBakeoffCycleNonZeroExitCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "externalEngineBakeoffCycleNonZeroExitCount")
    externalEngineBakeoffCycleSourceExternalBakeoffReady = Test-Truthy (Get-JsonValue $validation "externalEngineBakeoffCycleSourceExternalBakeoffReady")
    externalEngineBakeoffCycleSourceExternalBakeoffActionPresent = Test-Truthy (Get-JsonValue $validation "externalEngineBakeoffCycleSourceExternalBakeoffActionPresent")
    externalEngineBakeoffCyclePath = [string](Get-JsonValue $validation "externalEngineBakeoffCyclePath")
    externalEngineBakeoffCycleSha256 = [string](Get-JsonValue $validation "externalEngineBakeoffCycleSha256")
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
    nativeStageTimingReportPresent = Test-Truthy (Get-JsonValue $validation "nativeStageTimingReportPresent")
    nativeStageTimingReportReady = Test-Truthy (Get-JsonValue $validation "nativeStageTimingReportReady")
    nativeStageTimingReportStatus = [string](Get-JsonValue $validation "nativeStageTimingReportStatus")
    nativeStageTimingReportPath = [string](Get-JsonValue $validation "nativeStageTimingReportPath")
    nativeStageTimingReportSha256 = [string](Get-JsonValue $validation "nativeStageTimingReportSha256")
    nativeStageTimingReportSchemaVersion = [int](Get-JsonValue $validation "nativeStageTimingReportSchemaVersion")
    nativeStageTimingReportTool = [string](Get-JsonValue $validation "nativeStageTimingReportTool")
    nativeStageTimingMetricsPath = [string](Get-JsonValue $validation "nativeStageTimingMetricsPath")
    nativeStageTimingMetricsSha256 = [string](Get-JsonValue $validation "nativeStageTimingMetricsSha256")
    nativeStageTimingMetricsHashStatus = [string](Get-JsonValue $validation "nativeStageTimingMetricsHashStatus")
    nativeStageTimingWdspBackedEvidence = Test-Truthy (Get-JsonValue $validation "nativeStageTimingWdspBackedEvidence")
    nativeStageTimingWdspRuntimeRid = [string](Get-JsonValue $validation "nativeStageTimingWdspRuntimeRid")
    nativeStageTimingWdspRuntimeSha256 = [string](Get-JsonValue $validation "nativeStageTimingWdspRuntimeSha256")
    nativeStageTimingWdspRuntimeHashStatus = [string](Get-JsonValue $validation "nativeStageTimingWdspRuntimeHashStatus")
    nativeStageTimingRunCount = [int](Get-JsonValue $validation "nativeStageTimingRunCount")
    nativeStageTimingStageRecordCount = [int](Get-JsonValue $validation "nativeStageTimingStageRecordCount")
    nativeStageTimingMissingStageTimingRunCount = [int](Get-JsonValue $validation "nativeStageTimingMissingStageTimingRunCount")
    nativeStageTimingMissingAllocationProbeRunCount = [int](Get-JsonValue $validation "nativeStageTimingMissingAllocationProbeRunCount")
    nativeStageTimingBudgetFailureCount = [int](Get-JsonValue $validation "nativeStageTimingBudgetFailureCount")
    nativeStageTimingMaxStageElapsedMs = [double](Get-JsonValue $validation "nativeStageTimingMaxStageElapsedMs")
    nativeStageTimingMaxRunElapsedMs = [double](Get-JsonValue $validation "nativeStageTimingMaxRunElapsedMs")
    nativeStageTimingMaxManagedAllocationBytes = [int64](Get-JsonValue $validation "nativeStageTimingMaxManagedAllocationBytes")
    nativeStageTimingNativeCStageInstrumentationReady = Test-Truthy (Get-JsonValue $validation "nativeStageTimingNativeCStageInstrumentationReady")
    nativeStageTimingNativeCStageInstrumentationStatus = [string](Get-JsonValue $validation "nativeStageTimingNativeCStageInstrumentationStatus")
    nativeStageTimingNativeAllocationProbeStatus = [string](Get-JsonValue $validation "nativeStageTimingNativeAllocationProbeStatus")
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
    metricComparisonCatalogRequiredMetricCount = [int](Get-JsonValue $validation "metricComparisonCatalogRequiredMetricCount")
    metricComparisonCatalogAcceptanceContractReady = Test-Truthy (Get-JsonValue $validation "metricComparisonCatalogAcceptanceContractReady")
    metricComparisonCatalogContractProblemMetricCount = [int](Get-JsonValue $validation "metricComparisonCatalogContractProblemMetricCount")
    metricComparisonCatalogContractProblemMetricIds = @(Get-JsonArray $validation "metricComparisonCatalogContractProblemMetricIds")
    nativeRuntimeArtifactAuditPresent = Test-Truthy (Get-JsonValue $validation "nativeRuntimeArtifactAuditPresent")
    nativeRuntimeArtifactAuditReadyForWinX64Package = Test-Truthy (Get-JsonValue $validation "nativeRuntimeArtifactAuditReadyForWinX64Package")
    nativeRuntimeArtifactAuditPendingRidCount = [int](Get-JsonValue $validation "nativeRuntimeArtifactAuditPendingRidCount")
    nativeRuntimeArtifactAuditArtifactCount = [int](Get-JsonValue $validation "nativeRuntimeArtifactAuditArtifactCount")
    nativeRuntimeArtifactAuditWinX64NativePath = [string](Get-JsonValue $validation "nativeRuntimeArtifactAuditWinX64NativePath")
    nativeRuntimeArtifactAuditWinX64NativeLength = [int64](Get-JsonValue $validation "nativeRuntimeArtifactAuditWinX64NativeLength")
    nativeRuntimeArtifactAuditWinX64NativeSha256 = [string](Get-JsonValue $validation "nativeRuntimeArtifactAuditWinX64NativeSha256")
    wdspSourceDriftReportPresent = Test-Truthy (Get-JsonValue $validation "wdspSourceDriftReportPresent")
    wdspSourceDriftReportReady = Test-Truthy (Get-JsonValue $validation "wdspSourceDriftReportReady")
    wdspSourceDriftReportStatus = [string](Get-JsonValue $validation "wdspSourceDriftReportStatus")
    wdspSourceDriftReportPath = [string](Get-JsonValue $validation "wdspSourceDriftReportPath")
    wdspSourceDriftReportSha256 = [string](Get-JsonValue $validation "wdspSourceDriftReportSha256")
    wdspSourceDriftReportNormalizedLineEndings = Test-Truthy (Get-JsonValue $validation "wdspSourceDriftReportNormalizedLineEndings")
    wdspSourceDriftReferenceFileCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "wdspSourceDriftReferenceFileCount")
    wdspSourceDriftCandidateFileCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "wdspSourceDriftCandidateFileCount")
    wdspSourceDriftFileCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "wdspSourceDriftFileCount")
    wdspSourceDriftDeltaCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "wdspSourceDriftDeltaCount")
    wdspSourceDriftLikelyDefectCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "wdspSourceDriftLikelyDefectCount")
    wdspSourceDriftStatusCounts = @(Get-JsonArray $validation "wdspSourceDriftStatusCounts")
    wdspSourceDriftCategoryCounts = @(Get-JsonArray $validation "wdspSourceDriftCategoryCounts")
    acceptanceReadinessStageCount = $acceptanceReadiness.Count
    acceptanceReadinessReadyStageCount = $acceptanceReadyStages.Count
    optInDspBuildOutReady = Test-Truthy (Get-JsonValue $optInBuildOutStage "ready")
    optInDspBuildOutStatus = [string](Get-JsonValue $optInBuildOutStage "status")
    optInDspBuildOutBlockingGateIds = @(Get-JsonArray $optInBuildOutStage "blockingGateIds")
    g2FirstPassAcceptanceReady = Test-Truthy (Get-JsonValue $g2FirstPassStage "ready")
    candidateComparisonReady = Test-Truthy (Get-JsonValue $candidateComparisonStage "ready")
    defaultBehaviorChangeReady = $false
    defaultGraduationReady = $false
    crossRadioValidationRequired = $true
    crossRadioValidationPresent = Test-Truthy (Get-JsonValue $validation "crossRadioValidationPresent")
    crossRadioValidationReady = Test-Truthy (Get-JsonValue $validation "crossRadioValidationReady")
    crossRadioValidationEvidenceStatus = if ([string]::IsNullOrWhiteSpace([string](Get-JsonValue $validation "crossRadioValidationEvidenceStatus"))) { "not-captured" } else { [string](Get-JsonValue $validation "crossRadioValidationEvidenceStatus") }
    crossRadioValidationNonG2TargetCount = [int](Get-JsonValue $validation "crossRadioValidationNonG2TargetCount")
    crossRadioValidationNonG2TargetIds = @(Get-JsonArray $validation "crossRadioValidationNonG2TargetIds")
    crossRadioValidationScenarioCount = [int](Get-JsonValue $validation "crossRadioValidationScenarioCount")
    crossRadioValidationScenarioIds = @(Get-JsonArray $validation "crossRadioValidationScenarioIds")
    crossRadioValidationComparisonCount = [int](Get-JsonValue $validation "crossRadioValidationComparisonCount")
    crossRadioValidationComparisonIds = @(Get-JsonArray $validation "crossRadioValidationComparisonIds")
    crossRadioValidationDefaultBehaviorChangeApproved = Test-Truthy (Get-JsonValue $validation "crossRadioValidationDefaultBehaviorChangeApproved")
    crossRadioValidationSourceReportCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "crossRadioValidationSourceReportCount")
    crossRadioValidationSourceProblemReportCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "crossRadioValidationSourceProblemReportCount")
    crossRadioValidationSourceWarningReportCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "crossRadioValidationSourceWarningReportCount")
    crossRadioValidationNonG2SourceReportCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "crossRadioValidationNonG2SourceReportCount")
    crossRadioValidationReadyNonG2SourceReportCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "crossRadioValidationReadyNonG2SourceReportCount")
    crossRadioValidationSourceMetricComparisonReadyCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "crossRadioValidationSourceMetricComparisonReadyCount")
    crossRadioValidationSourceLiveTraceComparisonReadyCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "crossRadioValidationSourceLiveTraceComparisonReadyCount")
    crossRadioValidationSourceThetisLiveTraceComparisonReadyCount = Get-IntegerValueOrDefault (Get-JsonValue $validation "crossRadioValidationSourceThetisLiveTraceComparisonReadyCount")
    crossRadioValidationSourceBackedEvidenceReady = Test-Truthy (Get-JsonValue $validation "crossRadioValidationSourceBackedEvidenceReady")
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
    primaryAcceptanceExpectedArtifactCount = $primaryAcceptanceExpectedArtifacts.Count
    primaryAcceptanceExpectedArtifacts = @($primaryAcceptanceExpectedArtifacts)
    primaryAcceptanceFollowUp = [string](Get-JsonValue $primaryAcceptanceAction "followUp")
    acceptanceActionPlan = @($acceptanceActionPlan)
    issueCodeCounts = @($issueCodeCounts)
    requiredLiveAcceptanceArtifactProblemCount = $liveAcceptanceArtifactProblems.Count
    requiredLiveAcceptanceArtifactProblemIds = @($liveAcceptanceArtifactProblemIds)
    requiredLiveAcceptanceArtifactProblems = @($liveAcceptanceArtifactProblems)
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
    Write-Host "Opt-in DSP build-out ready: $($report.optInDspBuildOutReady), status: $($report.optInDspBuildOutStatus)"
}

if ($FailOnIssues -and $report.status -ne "ready") {
    exit 1
}

if ($FailOnOptInDspBuildOutBlocked -and -not (Test-Truthy $report.optInDspBuildOutReady)) {
    $blockingGateIds = @($report.optInDspBuildOutBlockingGateIds) -join ", "
    if ([string]::IsNullOrWhiteSpace($blockingGateIds)) {
        $blockingGateIds = "none-recorded"
    }
    Write-Error "Opt-in DSP build-out prerequisites are blocked ($($report.optInDspBuildOutStatus)): $blockingGateIds"
    exit 1
}
