param(
    [string]$OutputPath = "",

    [int]$Cycles = 3,

    [switch]$Force,

    [switch]$FailOnGate,

    [switch]$JsonOnly,

    [switch]$PlanOnly
)

$ErrorActionPreference = "Stop"

$projectPath = Join-Path $PSScriptRoot "wdsp-channel-lifecycle-benchmark\wdsp-channel-lifecycle-benchmark.csproj"
if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "WDSP channel lifecycle benchmark project not found: $projectPath"
}

if ($Cycles -le 0 -or $Cycles -gt 25) {
    throw "Cycles must be between 1 and 25."
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $OutputPath = Join-Path $repoRoot "tmp\wdsp-v2-fixtures\wdsp-channel-lifecycle-report.json"
}

if ($PlanOnly) {
    [ordered]@{
        schemaVersion = 1
        tool = "run-dsp-wdsp-channel-lifecycle"
        mode = "plan-only"
        scenarioId = "wdsp-channel-lifecycle"
        outputPath = $OutputPath
        cycles = $Cycles
        projectPath = $projectPath
        evidenceKind = "wdsp-channel-lifecycle-json"
        safety = @(
            "offline-local-wdsp-wrapper-only",
            "does not connect to radio hardware",
            "does not tune VFO or LO",
            "does not key hardware MOX or TUN",
            "does not change operator defaults"
        )
        actions = @(
            "open RXA through WdspDspEngine.OpenChannel",
            "configure RXA through public Zeus wrapper setters",
            "open TXA through WdspDspEngine.OpenTxChannel",
            "feed deterministic local IQ and drain RX audio",
            "toggle WdspDspEngine.SetMox true and false locally",
            "verify RX audio and meters recover after lifecycle transitions",
            "close RXA through WdspDspEngine.CloseChannel and reopen RXA to check stale audio and meter leakage"
        )
    } | ConvertTo-Json -Depth 16
    exit 0
}

$toolArgs = New-Object System.Collections.Generic.List[string]
$toolArgs.Add("--output-path") | Out-Null
$toolArgs.Add($OutputPath) | Out-Null
$toolArgs.Add("--cycles") | Out-Null
$toolArgs.Add([string]$Cycles) | Out-Null

if ($Force) {
    $toolArgs.Add("--force") | Out-Null
}

if ($FailOnGate) {
    $toolArgs.Add("--fail-on-gate") | Out-Null
}

if ($JsonOnly) {
    $toolArgs.Add("--json-only") | Out-Null
}

& dotnet run --project $projectPath -- @($toolArgs.ToArray())
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
