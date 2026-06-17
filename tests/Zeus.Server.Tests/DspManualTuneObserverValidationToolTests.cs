// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class DspManualTuneObserverValidationToolTests
{
    private static readonly JsonSerializerOptions CamelCaseJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    [SkippableFact]
    public async Task ManualTuneObserverTuningHintsValidateAndSummarize()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell manual-tune observer validator smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-manual-tune-observer-hint-validation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            WriteSourcePlanScopeBundle(bundleDir);
            WriteManualTuneObserverArtifactManifest(bundleDir);
            WriteManualTuneObserverReportWithTuningHints(bundleDir);

            var validationReport = Path.Combine(bundleDir, "validation-manual-tune-observer-hint.json");
            var validation = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "validate-dsp-modernization-bundle.ps1"),
                "-BundleDir", bundleDir,
                "-ArtifactManifestPath", Path.Combine(bundleDir, "artifact-manifest.json"),
                "-ReportPath", validationReport,
                "-AllowPreflight",
                "-JsonOnly");

            Assert.NotEqual(0, validation.ExitCode);
            Assert.True(File.Exists(validationReport), validation.CombinedOutput);

            using var validationDoc = JsonDocument.Parse(await File.ReadAllTextAsync(validationReport));
            var validationRoot = validationDoc.RootElement;
            Assert.True(validationRoot.GetProperty("manualTuneObserverReportPresent").GetBoolean());
            Assert.True(validationRoot.GetProperty("manualTuneObserverReportReady").GetBoolean());
            Assert.True(validationRoot.GetProperty("manualTuneObserverReportValid").GetBoolean());
            Assert.Equal(1, validationRoot.GetProperty("manualTuneObserverObservedVfoCount").GetInt32());
            Assert.Equal(14_331_500L, validationRoot.GetProperty("manualTuneObserverBestObservedVfoHz").GetInt64());
            Assert.Equal(14.3315, validationRoot.GetProperty("manualTuneObserverBestObservedVfoMhz").GetDouble(), precision: 6);
            Assert.Equal("capture-qualified", validationRoot.GetProperty("manualTuneObserverBestObservedVfoStatus").GetString());
            Assert.True(validationRoot.GetProperty("manualTuneObserverBestObservedVfoScore").GetDouble() > 0.0);
            Assert.Equal(14_365_124L, validationRoot.GetProperty("manualTuneObserverBestObservedVfoSuggestedVfoHz").GetInt64());
            Assert.Equal(14.365124, validationRoot.GetProperty("manualTuneObserverBestObservedVfoSuggestedVfoMhz").GetDouble(), precision: 6);
            Assert.Equal(33_624.0, validationRoot.GetProperty("manualTuneObserverBestObservedVfoSuggestedDialShiftHz").GetDouble(), precision: 3);
            Assert.Equal("above-filter", validationRoot.GetProperty("manualTuneObserverBestObservedVfoSuggestedTuneReason").GetString());
            Assert.Equal(3, validationRoot.GetProperty("manualTuneObserverFrontendTuningHintPollCount").GetInt32());
            Assert.Equal(33_624.0, validationRoot.GetProperty("manualTuneObserverFrontendSuggestedDialShiftHz").GetDouble(), precision: 3);
            Assert.Equal(14_365_124L, validationRoot.GetProperty("manualTuneObserverFrontendSuggestedVfoHz").GetInt64());
            Assert.Equal(14.365124, validationRoot.GetProperty("manualTuneObserverFrontendSuggestedVfoMhz").GetDouble(), precision: 6);
            Assert.Equal(35_250.0, validationRoot.GetProperty("manualTuneObserverFrontendSuggestedPeakOffsetHz").GetDouble(), precision: 3);
            Assert.Equal(14_366_750L, validationRoot.GetProperty("manualTuneObserverFrontendSuggestedPeakFrequencyHz").GetInt64());
            Assert.Equal(1_626.0, validationRoot.GetProperty("manualTuneObserverFrontendSuggestedFilterCenterOffsetHz").GetDouble(), precision: 3);
            Assert.Equal(32_098.0, validationRoot.GetProperty("manualTuneObserverFrontendSuggestedFilterDistanceHz").GetDouble(), precision: 3);
            Assert.Equal("above-filter", validationRoot.GetProperty("manualTuneObserverFrontendSuggestedTuneReason").GetString());

            var validationBestHint = validationRoot.GetProperty("manualTuneObserverFrontendBestTuningHint");
            Assert.Equal("above-filter", validationBestHint.GetProperty("reason").GetString());
            Assert.Equal(14_365_124L, validationBestHint.GetProperty("suggestedVfoHz").GetInt64());

            var observerIssueCodes = validationRoot.GetProperty("warnings")
                .EnumerateArray()
                .Concat(validationRoot.GetProperty("errors").EnumerateArray())
                .Select(issue => issue.GetProperty("code").GetString() ?? "")
                .Where(code =>
                    code.StartsWith("manual-tune-observer-tuning-hint", StringComparison.Ordinal) ||
                    code.StartsWith("manual-tune-observer-observed-vfo", StringComparison.Ordinal) ||
                    code.StartsWith("manual-tune-observer-best-observed-vfo", StringComparison.Ordinal))
                .ToArray();
            Assert.Empty(observerIssueCodes);

            var summaryReport = Path.Combine(bundleDir, "summary-manual-tune-observer-hint.json");
            var summaryMarkdown = Path.Combine(bundleDir, "summary-manual-tune-observer-hint.md");
            var summary = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "summarize-dsp-modernization-validation-report.ps1"),
                "-ValidationReportPath", validationReport,
                "-ReportPath", summaryReport,
                "-MarkdownPath", summaryMarkdown,
                "-JsonOnly");

            Assert.Equal(0, summary.ExitCode);
            Assert.True(File.Exists(summaryReport), summary.CombinedOutput);
            Assert.True(File.Exists(summaryMarkdown), summary.CombinedOutput);

            using var summaryDoc = JsonDocument.Parse(await File.ReadAllTextAsync(summaryReport));
            var summaryRoot = summaryDoc.RootElement;
            Assert.Equal(1, summaryRoot.GetProperty("manualTuneObserverObservedVfoCount").GetInt32());
            Assert.Equal(14_331_500L, summaryRoot.GetProperty("manualTuneObserverBestObservedVfoHz").GetInt64());
            Assert.Equal("capture-qualified", summaryRoot.GetProperty("manualTuneObserverBestObservedVfoStatus").GetString());
            Assert.Equal(14_365_124L, summaryRoot.GetProperty("manualTuneObserverBestObservedVfoSuggestedVfoHz").GetInt64());
            Assert.Equal(3, summaryRoot.GetProperty("manualTuneObserverFrontendTuningHintPollCount").GetInt32());
            Assert.Equal(33_624.0, summaryRoot.GetProperty("manualTuneObserverFrontendSuggestedDialShiftHz").GetDouble(), precision: 3);
            Assert.Equal(14_365_124L, summaryRoot.GetProperty("manualTuneObserverFrontendSuggestedVfoHz").GetInt64());
            Assert.Equal(14.365124, summaryRoot.GetProperty("manualTuneObserverFrontendSuggestedVfoMhz").GetDouble(), precision: 6);
            Assert.Equal("above-filter", summaryRoot.GetProperty("manualTuneObserverFrontendSuggestedTuneReason").GetString());

            var summaryBestHint = summaryRoot.GetProperty("manualTuneObserverFrontendBestTuningHint");
            Assert.Equal(32_098.0, summaryBestHint.GetProperty("filterDistanceHz").GetDouble(), precision: 3);

            var observerGate = summaryRoot.GetProperty("evidenceGates")
                .EnumerateArray()
                .Single(gate => gate.GetProperty("gateId").GetString() == "manual-tune-observer");
            var gateDetail = observerGate.GetProperty("detail").GetString() ?? "";
            Assert.Contains("observedVfos=1", gateDetail, StringComparison.Ordinal);
            Assert.Contains("bestObserved=14331500/capture-qualified", gateDetail, StringComparison.Ordinal);
            Assert.Contains("tuningHint=polls=3", gateDetail, StringComparison.Ordinal);
            Assert.Contains("shiftHz=33624", gateDetail, StringComparison.Ordinal);
            Assert.Contains("vfoMhz=14.365124", gateDetail, StringComparison.Ordinal);
            Assert.Contains("reason=above-filter", gateDetail, StringComparison.Ordinal);

            var markdown = await File.ReadAllTextAsync(summaryMarkdown);
            Assert.Contains("Observed VFOs/best/status/score: 1 / 14331500 Hz / capture-qualified", markdown, StringComparison.Ordinal);
            Assert.Contains("Frontend tuning hint polls/shift/VFO/reason/distance: 3 / 33624 Hz / 14.365124 MHz / above-filter / 32098 Hz", markdown, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(bundleDir))
            {
                Directory.Delete(bundleDir, recursive: true);
            }
        }
    }

    [SkippableFact]
    public async Task ManualTuneObserverLegacyReportsDoNotRequireTuningHints()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell manual-tune observer validator smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-manual-tune-observer-legacy-validation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            WriteSourcePlanScopeBundle(bundleDir);
            WriteManualTuneObserverArtifactManifest(bundleDir);
            WriteManualTuneObserverReportWithTuningHints(bundleDir, includeTuningHints: false, includeObservedVfos: false);

            var validationReport = Path.Combine(bundleDir, "validation-manual-tune-observer-legacy.json");
            var validation = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "validate-dsp-modernization-bundle.ps1"),
                "-BundleDir", bundleDir,
                "-ArtifactManifestPath", Path.Combine(bundleDir, "artifact-manifest.json"),
                "-ReportPath", validationReport,
                "-AllowPreflight",
                "-JsonOnly");

            Assert.NotEqual(0, validation.ExitCode);
            Assert.True(File.Exists(validationReport), validation.CombinedOutput);

            using var validationDoc = JsonDocument.Parse(await File.ReadAllTextAsync(validationReport));
            var validationRoot = validationDoc.RootElement;
            Assert.True(validationRoot.GetProperty("manualTuneObserverReportValid").GetBoolean());
            Assert.Equal(0, validationRoot.GetProperty("manualTuneObserverObservedVfoCount").GetInt32());
            Assert.Equal(JsonValueKind.Null, validationRoot.GetProperty("manualTuneObserverBestObservedVfo").ValueKind);
            Assert.Equal(0, validationRoot.GetProperty("manualTuneObserverFrontendTuningHintPollCount").GetInt32());
            Assert.Equal(JsonValueKind.Null, validationRoot.GetProperty("manualTuneObserverFrontendBestTuningHint").ValueKind);
            Assert.Equal("", validationRoot.GetProperty("manualTuneObserverFrontendSuggestedTuneReason").GetString());

            var validationIssueCodes = validationRoot.GetProperty("warnings")
                .EnumerateArray()
                .Concat(validationRoot.GetProperty("errors").EnumerateArray())
                .Select(issue => issue.GetProperty("code").GetString() ?? "")
                .Where(code =>
                    code.StartsWith("manual-tune-observer-tuning-hint", StringComparison.Ordinal) ||
                    code.StartsWith("manual-tune-observer-observed-vfo", StringComparison.Ordinal) ||
                    code.StartsWith("manual-tune-observer-best-observed-vfo", StringComparison.Ordinal))
                .ToArray();
            Assert.Empty(validationIssueCodes);

            var summaryReport = Path.Combine(bundleDir, "summary-manual-tune-observer-legacy.json");
            var summary = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "summarize-dsp-modernization-validation-report.ps1"),
                "-ValidationReportPath", validationReport,
                "-ReportPath", summaryReport,
                "-NoMarkdown",
                "-JsonOnly");

            Assert.Equal(0, summary.ExitCode);
            Assert.True(File.Exists(summaryReport), summary.CombinedOutput);

            using var summaryDoc = JsonDocument.Parse(await File.ReadAllTextAsync(summaryReport));
            var summaryRoot = summaryDoc.RootElement;
            Assert.Equal(0, summaryRoot.GetProperty("manualTuneObserverObservedVfoCount").GetInt32());
            Assert.Equal(JsonValueKind.Null, summaryRoot.GetProperty("manualTuneObserverBestObservedVfo").ValueKind);
            Assert.Equal(0, summaryRoot.GetProperty("manualTuneObserverFrontendTuningHintPollCount").GetInt32());
            Assert.Equal(JsonValueKind.Null, summaryRoot.GetProperty("manualTuneObserverFrontendBestTuningHint").ValueKind);

            var observerGate = summaryRoot.GetProperty("evidenceGates")
                .EnumerateArray()
                .Single(gate => gate.GetProperty("gateId").GetString() == "manual-tune-observer");
            Assert.Contains("tuningHint=none", observerGate.GetProperty("detail").GetString() ?? "", StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(bundleDir))
            {
                Directory.Delete(bundleDir, recursive: true);
            }
        }
    }

    [SkippableFact]
    public async Task ManualTuneObserverValidationDerivesBestObservedVfoFromObject()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell manual-tune observer validator smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-manual-tune-observer-derived-best-vfo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            WriteSourcePlanScopeBundle(bundleDir);
            WriteManualTuneObserverArtifactManifest(bundleDir);
            WriteManualTuneObserverReportWithTuningHints(bundleDir, staleFlattenedBestObservedVfo: true);

            var validationReport = Path.Combine(bundleDir, "validation-manual-tune-observer-derived-best-vfo.json");
            var validation = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "validate-dsp-modernization-bundle.ps1"),
                "-BundleDir", bundleDir,
                "-ArtifactManifestPath", Path.Combine(bundleDir, "artifact-manifest.json"),
                "-ReportPath", validationReport,
                "-AllowPreflight",
                "-JsonOnly");

            Assert.NotEqual(0, validation.ExitCode);
            Assert.True(File.Exists(validationReport), validation.CombinedOutput);

            using var validationDoc = JsonDocument.Parse(await File.ReadAllTextAsync(validationReport));
            var validationRoot = validationDoc.RootElement;
            Assert.True(validationRoot.GetProperty("manualTuneObserverReportValid").GetBoolean());
            Assert.Equal(14_331_500L, validationRoot.GetProperty("manualTuneObserverBestObservedVfoHz").GetInt64());
            Assert.Equal("capture-qualified", validationRoot.GetProperty("manualTuneObserverBestObservedVfoStatus").GetString());
            Assert.Equal(14_365_124L, validationRoot.GetProperty("manualTuneObserverBestObservedVfoSuggestedVfoHz").GetInt64());
            Assert.NotEqual(14_000_000L, validationRoot.GetProperty("manualTuneObserverBestObservedVfoHz").GetInt64());
            Assert.NotEqual("stale-flattened", validationRoot.GetProperty("manualTuneObserverBestObservedVfoStatus").GetString());
        }
        finally
        {
            if (Directory.Exists(bundleDir))
            {
                Directory.Delete(bundleDir, recursive: true);
            }
        }
    }

    private static void WriteManualTuneObserverArtifactManifest(string bundleDir)
    {
        Directory.CreateDirectory(Path.Combine(bundleDir, "artifacts"));

        var manifest = new
        {
            schemaVersion = 1,
            artifacts = new object[]
            {
                new
                {
                    id = "manual-tune-observer-report",
                    kind = "manual-tune-observer-report-json",
                    source = "tools/watch-dsp-manual-tune-observer.ps1",
                    path = "artifacts/manual-tune-observer-report.json",
                    required = false,
                    comparisonIds = new[] { "nr5-spnr" }
                }
            }
        };

        File.WriteAllText(
            Path.Combine(bundleDir, "artifact-manifest.json"),
            JsonSerializer.Serialize(manifest, CamelCaseJson));
    }

    private static void WriteSourcePlanScopeBundle(string bundleDir)
    {
        var plan = DspBenchmarkPlanCatalog.Build();
        File.WriteAllText(
            Path.Combine(bundleDir, "benchmark-plan.json"),
            JsonSerializer.Serialize(plan, CamelCaseJson));

        File.WriteAllText(
            Path.Combine(bundleDir, "bundle-index.json"),
            """
            {
              "schemaVersion": 1,
              "endpoints": [
                { "id": "benchmark-plan", "file": "benchmark-plan.json", "required": true, "ok": true },
                { "id": "benchmark-capture-manifest", "file": "benchmark-capture-manifest.json", "required": true, "ok": true }
              ],
              "requiredFailures": []
            }
            """);

        var captureManifest = new
        {
            schemaVersion = 1,
            hardwareTarget = plan.FirstHardwareTarget,
            scenarioIds = new[] { "weak-cw-carrier" },
            requiredComparisons = plan.RequiredComparisons,
            requiredArtifacts = new object[]
            {
                new { id = "live-diagnostics-json", kind = "endpoint-json", required = false },
                new { id = "live-diagnostics-trace-index", kind = "trace", source = "tools/run-dsp-live-diagnostics-matrix.ps1", required = false },
                new { id = "benchmark-plan-json", kind = "endpoint-json", required = false },
                new { id = "wdsp-native-symbol-audit", kind = "symbol-audit-json", required = false },
                new { id = "wdsp-runtime-artifact-audit", kind = "runtime-audit-json", required = false },
                new { id = "offline-fixture-metrics", kind = "metrics-json", required = false }
            }
        };

        File.WriteAllText(
            Path.Combine(bundleDir, "benchmark-capture-manifest.json"),
            JsonSerializer.Serialize(captureManifest, CamelCaseJson));
    }

    private static void WriteManualTuneObserverReportWithTuningHints(
        string bundleDir,
        bool includeTuningHints = true,
        bool includeObservedVfos = true,
        bool staleFlattenedBestObservedVfo = false)
    {
        const string captureReportPath = "artifacts/manual-tune-observer/14365124/live-diagnostics-watch.json";
        const string captureJsonlPath = "artifacts/manual-tune-observer/14365124/live-diagnostics-watch.jsonl";

        var hint = new
        {
            reason = "above-filter",
            peakFrequencyHz = 14_366_750L,
            peakOffsetHz = 35_250.0,
            peakSnrDb = 16.8,
            peakDbfs = -84.6,
            peakConfidence = 0.9,
            filterLowHz = 100,
            filterHighHz = 3_152,
            filterCenterOffsetHz = 1_626.0,
            filterDistanceHz = 32_098.0,
            currentVfoHz = 14_331_500L,
            suggestedDialShiftHz = 33_624.0,
            suggestedVfoHz = 14_365_124L,
            suggestedVfoMhz = 14.365124
        };

        var report = new
        {
            schemaVersion = 1,
            tool = "watch-dsp-manual-tune-observer",
            generatedUtc = "2026-06-17T12:00:00.0000000Z",
            startedUtc = "2026-06-17T11:59:30.0000000Z",
            completedUtc = "2026-06-17T12:00:00.0000000Z",
            durationMs = 30000,
            ok = true,
            scanError = "",
            baseUrl = "http://127.0.0.1:6060",
            bundleRelativePaths = true,
            outputRoot = "artifacts/manual-tune-observer",
            label = "synthetic-hint",
            scenarioId = "rx-ssb-voice-like-manual",
            comparisonId = "nr5-spnr",
            pollCount = 3,
            pollIntervalSec = 1,
            stablePolls = 1,
            minCoherentSnrDb = 6.0,
            sceneProfilePattern = "voice|speech|active",
            maxCaptures = 1,
            maxCapturesPerVfo = 1,
            allowStaleSceneCapture = false,
            captureSamples = 24,
            captureIntervalMs = 250,
            safety = new
            {
                rxOnly = true,
                readOnly = true,
                apiWrites = false,
                retune = false,
                vfoWriteAttemptCount = 0,
                radioLoWriteAttemptCount = 0,
                txEndpointsTouched = false,
                delegatedCapture = "watch-dsp-live-diagnostics.ps1"
            },
            pollSampleCount = 3,
            observedVfoCount = includeObservedVfos ? 1 : 0,
            observedVfos = includeObservedVfos ? new object[] { ManualObservedVfo(hint, includeTuningHints) } : Array.Empty<object>(),
            bestObservedVfo = includeObservedVfos ? ManualObservedVfo(hint, includeTuningHints) : null,
            bestObservedVfoHz = includeObservedVfos ? (staleFlattenedBestObservedVfo ? 14_000_000L : 14_331_500L) : 0L,
            bestObservedVfoMhz = includeObservedVfos ? 14.3315 : 0.0,
            bestObservedVfoStatus = includeObservedVfos ? (staleFlattenedBestObservedVfo ? "stale-flattened" : "capture-qualified") : "",
            bestObservedVfoScore = includeObservedVfos ? 256.8 : 0.0,
            bestObservedVfoSuggestedVfoHz = includeObservedVfos && includeTuningHints ? (staleFlattenedBestObservedVfo ? 14_000_100L : 14_365_124L) : 0L,
            bestObservedVfoSuggestedVfoMhz = includeObservedVfos && includeTuningHints ? 14.365124 : 0.0,
            bestObservedVfoSuggestedDialShiftHz = includeObservedVfos && includeTuningHints ? 33_624.0 : 0.0,
            bestObservedVfoSuggestedTuneReason = includeObservedVfos && includeTuningHints ? "above-filter" : "",
            captureCount = 1,
            uniqueCapturedVfoCount = 1,
            recapturedVfoCount = 0,
            staleScenePollCount = 0,
            staleSceneCaptureCount = 0,
            frontendNearPassbandPollCount = 0,
            frontendOffPassbandPollCount = 3,
            frontendFilterPassbandPollCount = 0,
            frontendFilterOffPassbandPollCount = 3,
            frontendOffsetMismatchPollCount = 0,
            frontendTuningHintPollCount = includeTuningHints ? 3 : 0,
            frontendBestTuningHint = includeTuningHints ? hint : null,
            captureQualifiedPollCount = 1,
            readyCaptureCount = 1,
            mixedWeakStrongReady = true,
            mixedWeakStrongReadyCaptureCount = 1,
            weakInputSampleCount = 8,
            strongInputSampleCount = 9,
            nearStrongInputSampleCount = 2,
            speechQualifiedWeakInputSampleCount = 6,
            speechQualifiedStrongInputSampleCount = 7,
            passbandQualifiedWeakInputSampleCount = 5,
            passbandQualifiedStrongInputSampleCount = 5,
            agcPumpingRiskCaptureCount = 0,
            captures = new object[]
            {
                new
                {
                    ok = true,
                    exitCode = 0,
                    error = "",
                    vfoHz = 14_365_124L,
                    vfoCaptureIndex = 1,
                    maxCapturesPerVfo = 1,
                    recaptureReason = "first-vfo-capture",
                    radioLoHz = 14_331_500L,
                    mode = "USB",
                    sceneFresh = true,
                    staleSceneCapture = false,
                    signalProfile = "speech-with-adjacent-strong",
                    coherentMaxSnrDb = 18.0,
                    reportPath = captureReportPath,
                    jsonlPath = captureJsonlPath,
                    readyForBenchmarkTrace = true,
                    trendStatus = "ready",
                    weakInputSampleCount = 8,
                    strongInputSampleCount = 9,
                    nearStrongInputSampleCount = 2,
                    mixedWeakStrongEvidenceStatus = "ready",
                    mixedWeakStrongEvidenceReady = true,
                    weakStrongOutputGapDb = 1.5,
                    speechQualifiedWeakInputSampleCount = 6,
                    speechQualifiedStrongInputSampleCount = 7,
                    passbandQualifiedWeakInputSampleCount = 5,
                    passbandQualifiedStrongInputSampleCount = 5,
                    agcStabilityStatus = "stable",
                    agcPumpingRisk = false
                }
            },
            polls = new object[]
            {
                ManualTunePoll(1, hint, includeTuningHints),
                ManualTunePoll(2, hint, includeTuningHints),
                ManualTunePoll(3, hint, includeTuningHints)
            },
            recommendations = new[]
            {
                "Read-only manual tuning hint: strongest frontend peak is above the RX filter; manually tune near 14.365124 MHz before capture."
            }
        };

        var reportJson = JsonSerializer.SerializeToNode(report, CamelCaseJson)!.AsObject();
        if (!includeTuningHints)
        {
            reportJson.Remove("frontendTuningHintPollCount");
            reportJson.Remove("frontendBestTuningHint");
        }
        if (!includeObservedVfos)
        {
            reportJson.Remove("observedVfoCount");
            reportJson.Remove("observedVfos");
            reportJson.Remove("bestObservedVfo");
            reportJson.Remove("bestObservedVfoHz");
            reportJson.Remove("bestObservedVfoMhz");
            reportJson.Remove("bestObservedVfoStatus");
            reportJson.Remove("bestObservedVfoScore");
            reportJson.Remove("bestObservedVfoSuggestedVfoHz");
            reportJson.Remove("bestObservedVfoSuggestedVfoMhz");
            reportJson.Remove("bestObservedVfoSuggestedDialShiftHz");
            reportJson.Remove("bestObservedVfoSuggestedTuneReason");
        }

        File.WriteAllText(
            Path.Combine(bundleDir, "artifacts", "manual-tune-observer-report.json"),
            reportJson.ToJsonString(CamelCaseJson));

        WriteSyntheticWatcherFiles(bundleDir, captureReportPath, captureJsonlPath);
    }

    private static object ManualObservedVfo(object hint, bool includeTuningHints) => new
    {
        vfoHz = 14_331_500L,
        vfoMhz = 14.3315,
        firstPoll = 1,
        lastPoll = 3,
        pollCount = 3,
        maxStablePollCount = 3,
        sceneFreshPollCount = 3,
        staleScenePollCount = 0,
        topPeakPollCount = 3,
        frontendNearPassbandPollCount = 0,
        frontendFilterPassbandPollCount = 0,
        frontendFilterOffPassbandPollCount = 3,
        frontendOffsetMismatchPollCount = 0,
        frontendTuningHintPollCount = includeTuningHints ? 3 : 0,
        captureQualifiedPollCount = 1,
        maxCoherentSnrDb = 18.0,
        maxAudioRmsDbfs = -34.0,
        maxNr5OutputDbfs = -31.0,
        bestTuningHint = includeTuningHints ? hint : null,
        frontendSuggestedDialShiftHz = includeTuningHints ? 33_624.0 : 0.0,
        frontendSuggestedVfoHz = includeTuningHints ? 14_365_124L : 0L,
        frontendSuggestedVfoMhz = includeTuningHints ? 14.365124 : 0.0,
        frontendSuggestedTuneReason = includeTuningHints ? "above-filter" : "",
        frontendSuggestedFilterDistanceHz = includeTuningHints ? 32_098.0 : 0.0,
        status = "capture-qualified",
        score = 256.8
    };

    private static JsonObject ManualTunePoll(int poll, object hint, bool includeTuningHints)
    {
        var pollJson = JsonSerializer.SerializeToNode(new
        {
            poll,
            vfoHz = 14_331_500L,
            radioLoHz = 14_331_500L,
            mode = "USB",
            stablePollCount = poll,
            sceneFresh = true,
            signalProfile = "speech-with-adjacent-strong",
            captureQualified = poll == 3,
            frontendFilterPassbandKnown = true,
            frontendFilterPassband = false,
            frontendFilterOffPassband = true,
            frontendTuningHint = includeTuningHints ? hint : null,
            frontendSuggestedDialShiftHz = includeTuningHints ? 33_624.0 : 0.0,
            frontendSuggestedVfoHz = includeTuningHints ? 14_365_124L : 0L,
            frontendSuggestedVfoMhz = includeTuningHints ? 14.365124 : 0.0,
            frontendSuggestedTuneReason = includeTuningHints ? "above-filter" : ""
        }, CamelCaseJson)!.AsObject();

        if (!includeTuningHints)
        {
            pollJson.Remove("frontendTuningHint");
            pollJson.Remove("frontendSuggestedDialShiftHz");
            pollJson.Remove("frontendSuggestedVfoHz");
            pollJson.Remove("frontendSuggestedVfoMhz");
            pollJson.Remove("frontendSuggestedTuneReason");
        }

        return pollJson;
    }

    private static void WriteSyntheticWatcherFiles(string bundleDir, string reportPath, string jsonlPath)
    {
        var resolvedReportPath = Path.Combine(bundleDir, reportPath.Replace('/', Path.DirectorySeparatorChar));
        var resolvedJsonlPath = Path.Combine(bundleDir, jsonlPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(resolvedReportPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(resolvedJsonlPath)!);

        var summary = new
        {
            schemaVersion = 1,
            tool = "watch-dsp-live-diagnostics",
            ok = true,
            readyForBenchmarkTrace = true,
            sampleCount = 24,
            jsonlPath,
            nr5WeakSignalWatch = new
            {
                weakInputSampleCount = 8,
                strongInputSampleCount = 9,
                mixedWeakStrongEvidenceReady = true,
                mixedWeakStrongEvidenceStatus = "ready",
                speechQualifiedWeakInputSampleCount = 6,
                speechQualifiedStrongInputSampleCount = 7,
                passbandQualifiedWeakInputSampleCount = 5,
                passbandQualifiedStrongInputSampleCount = 5
            },
            frontendTopPeakWatch = new
            {
                sampleCount = 17,
                nearPassbandSampleCount = 0,
                nearPassbandThresholdHz = 3000
            }
        };

        File.WriteAllText(resolvedReportPath, JsonSerializer.Serialize(summary, CamelCaseJson));
        File.WriteAllText(resolvedJsonlPath, JsonSerializer.Serialize(new { ok = true }, CamelCaseJson) + Environment.NewLine);
    }

    private static async Task<ToolResult> RunPowerShellAsync(
        string powerShell,
        string workingDirectory,
        string scriptPath,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(powerShell)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {powerShell}.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        var output = await outputTask;
        var error = await errorTask;
        return new ToolResult(process.ExitCode, output, error);
    }

    private static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var validator = Path.Combine(directory.FullName, "tools", "validate-dsp-modernization-bundle.ps1");
            if (File.Exists(validator))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not locate repository root from test output directory.");
    }

    private static string? FindPowerShell()
    {
        var systemPowerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        if (File.Exists(systemPowerShell))
        {
            return systemPowerShell;
        }

        return FindOnPath("powershell.exe")
            ?? FindOnPath("powershell")
            ?? FindOnPath("pwsh.exe")
            ?? FindOnPath("pwsh");
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            var candidate = Path.Combine(directory.Trim(), fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private sealed record ToolResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public string CombinedOutput => string.Join(
            Environment.NewLine,
            new[] { StandardOutput, StandardError }.Where(text => !string.IsNullOrWhiteSpace(text)));
    }
}
