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

    [SkippableFact]
    public async Task ManualTuneObserverObservedVfoTriagePlansManualFollowUp()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell manual-tune observer validator smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-manual-tune-observer-observed-action-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            WriteSourcePlanScopeBundle(bundleDir);
            WriteManualTuneObserverArtifactManifest(bundleDir);
            WriteManualTuneObserverReportWithTuningHints(bundleDir, includeCapture: false);

            var validationReport = Path.Combine(bundleDir, "validation-manual-tune-observer-observed-action.json");
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

            var summaryReport = Path.Combine(bundleDir, "summary-manual-tune-observer-observed-action.json");
            var summaryMarkdown = Path.Combine(bundleDir, "summary-manual-tune-observer-observed-action.md");
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
            using var validationDoc = JsonDocument.Parse(await File.ReadAllTextAsync(validationReport));
            var validationRoot = validationDoc.RootElement;
            var actions = summaryRoot.GetProperty("acceptanceActionPlan")
                .EnumerateArray()
                .ToArray();
            var actionIds = string.Join(
                ", ",
                actions.Select(item => item.GetProperty("actionId").GetString() ?? "(missing)"));
            var actionMatches = actions
                .Where(item => item.GetProperty("actionId").GetString() == "capture-manual-observer-best-observed-vfo")
                .ToArray();

            Assert.True(
                actionMatches.Length == 1,
                $"Expected capture-manual-observer-best-observed-vfo once. Actions: {actionIds}. " +
                $"manualTuneObserverReportValid={GetBooleanDebug(validationRoot, "manualTuneObserverReportValid")}, " +
                $"manualTuneObserverObservedVfoCount={GetLongDebug(validationRoot, "manualTuneObserverObservedVfoCount")}, " +
                $"manualTuneObserverBestObservedVfoHz={GetLongDebug(validationRoot, "manualTuneObserverBestObservedVfoHz")}, " +
                $"manualTuneObserverSafetyReadOnly={GetBooleanDebug(validationRoot, "manualTuneObserverSafetyReadOnly")}.");
            var action = actionMatches[0];

            Assert.Equal("live-history-mixed-weak-strong", action.GetProperty("gateId").GetString());
            Assert.Equal("live-diagnostics", action.GetProperty("category").GetString());
            Assert.True(action.GetProperty("requiredForAcceptance").GetBoolean());
            Assert.True(action.GetProperty("blocksDefaultBehaviorChange").GetBoolean());
            Assert.Contains("bestObservedVfoHz=14331500", action.GetProperty("reason").GetString() ?? "", StringComparison.Ordinal);
            Assert.Contains("suggestedVfoMhz=14.365124", action.GetProperty("reason").GetString() ?? "", StringComparison.Ordinal);
            Assert.Contains("Manually tune G2 near 14.365124 MHz", action.GetProperty("manualAction").GetString() ?? "", StringComparison.Ordinal);
            Assert.Contains("Do not use retune/VFO-writing tools", action.GetProperty("manualAction").GetString() ?? "", StringComparison.Ordinal);

            var commandSteps = action.GetProperty("commandSteps")
                .EnumerateArray()
                .Select(item => item.GetString() ?? "")
                .ToArray();
            Assert.Contains(commandSteps, step => step.Contains("watch-dsp-manual-tune-observer.ps1", StringComparison.Ordinal));
            Assert.Contains(commandSteps, step => step.Contains("-RequireFrontendNearPassband", StringComparison.Ordinal));
            Assert.Contains(commandSteps, step => step.Contains("summarize-dsp-live-diagnostics-history.ps1", StringComparison.Ordinal));

            var expectedArtifacts = action.GetProperty("expectedArtifacts")
                .EnumerateArray()
                .Select(item => item.GetString() ?? "")
                .ToArray();
            Assert.Contains("artifacts/manual-tune-observer-report.json", expectedArtifacts);
            Assert.Contains("artifacts/live-diagnostics-history.json", expectedArtifacts);

            var markdown = await File.ReadAllTextAsync(summaryMarkdown);
            Assert.Contains("capture-manual-observer-best-observed-vfo", markdown, StringComparison.Ordinal);
            Assert.Contains("watch-dsp-manual-tune-observer.ps1", markdown, StringComparison.Ordinal);
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
    public async Task ManualTuneObserverNearStrongTriagePlansTargetedRecapture()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell manual-tune observer validator smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-manual-tune-observer-near-strong-action-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            WriteSourcePlanScopeBundle(bundleDir);
            WriteManualTuneObserverArtifactManifest(bundleDir);
            WriteManualTuneObserverReportWithTuningHints(bundleDir, nearStrongCandidate: true);

            var validationReport = Path.Combine(bundleDir, "validation-manual-tune-observer-near-strong-action.json");
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
            Assert.Equal("near-strong-weak-only", validationRoot.GetProperty("manualTuneObserverReportStatus").GetString());
            Assert.False(validationRoot.GetProperty("manualTuneObserverMixedWeakStrongReady").GetBoolean());
            Assert.Equal(1, validationRoot.GetProperty("manualTuneObserverNearStrongPromotionCandidateCaptureCount").GetInt32());
            Assert.Equal(14_365_124L, validationRoot.GetProperty("manualTuneObserverBestNearStrongPromotionCandidateVfoHz").GetInt64());
            Assert.Equal(2.5, validationRoot.GetProperty("manualTuneObserverBestNearStrongPromotionCandidateDistanceToStrongThresholdDb").GetDouble(), precision: 3);
            Assert.Equal(3, validationRoot.GetProperty("manualTuneObserverBestNearStrongPromotionCandidateNearStrongInputSampleCount").GetInt32());
            Assert.Equal("artifacts/manual-tune-observer/14365124/live-diagnostics-watch.json", validationRoot.GetProperty("manualTuneObserverBestNearStrongPromotionCandidateReportPath").GetString());

            var summaryReport = Path.Combine(bundleDir, "summary-manual-tune-observer-near-strong-action.json");
            var summaryMarkdown = Path.Combine(bundleDir, "summary-manual-tune-observer-near-strong-action.md");
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
            Assert.Equal("near-strong-weak-only", summaryRoot.GetProperty("manualTuneObserverReportStatus").GetString());
            Assert.Equal(1, summaryRoot.GetProperty("manualTuneObserverNearStrongPromotionCandidateCaptureCount").GetInt32());

            var actions = summaryRoot.GetProperty("acceptanceActionPlan")
                .EnumerateArray()
                .ToArray();
            var actionIds = actions
                .Select(item => item.GetProperty("actionId").GetString() ?? "")
                .ToArray();
            Assert.DoesNotContain("capture-manual-observer-best-observed-vfo", actionIds);

            var action = actions.Single(item => item.GetProperty("actionId").GetString() == "recapture-manual-observer-near-strong-window");
            Assert.Equal("live-history-mixed-weak-strong", action.GetProperty("gateId").GetString());
            Assert.Equal("live-diagnostics", action.GetProperty("category").GetString());
            Assert.True(action.GetProperty("requiredForAcceptance").GetBoolean());
            Assert.True(action.GetProperty("blocksDefaultBehaviorChange").GetBoolean());
            Assert.Contains("bestCandidateVfoHz=14365124", action.GetProperty("reason").GetString() ?? "", StringComparison.Ordinal);
            Assert.Contains("distanceToStrongThresholdDb=2.5", action.GetProperty("reason").GetString() ?? "", StringComparison.Ordinal);
            Assert.Contains("nearStrongSamples=3", action.GetProperty("reason").GetString() ?? "", StringComparison.Ordinal);
            Assert.Contains("Stay manually tuned near 14.365124 MHz", action.GetProperty("manualAction").GetString() ?? "", StringComparison.Ordinal);
            Assert.Contains("no retune/VFO-writing tools", action.GetProperty("manualAction").GetString() ?? "", StringComparison.Ordinal);

            var commandSteps = action.GetProperty("commandSteps")
                .EnumerateArray()
                .Select(item => item.GetString() ?? "")
                .ToArray();
            Assert.Contains(commandSteps, step => step.Contains("watch-dsp-manual-tune-observer.ps1", StringComparison.Ordinal));
            Assert.Contains(commandSteps, step => step.Contains("-CaptureSamples 32", StringComparison.Ordinal));
            Assert.Contains(commandSteps, step => step.Contains("-MaxCapturesPerVfo 2", StringComparison.Ordinal));

            var expectedArtifacts = action.GetProperty("expectedArtifacts")
                .EnumerateArray()
                .Select(item => item.GetString() ?? "")
                .ToArray();
            Assert.Contains("artifacts/manual-tune-observer/14365124/live-diagnostics-watch.json", expectedArtifacts);

            var markdown = await File.ReadAllTextAsync(summaryMarkdown);
            Assert.Contains("recapture-manual-observer-near-strong-window", markdown, StringComparison.Ordinal);
            Assert.Contains("Best near-strong candidate: 14365124 Hz / 14.365124 MHz", markdown, StringComparison.Ordinal);
            Assert.Contains("artifacts/manual-tune-observer/14365124/live-diagnostics-watch.json", markdown, StringComparison.Ordinal);
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
    public async Task ManualTuneObserverNearStrongValidationRejectsMalformedCandidate()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell manual-tune observer validator smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-manual-tune-observer-near-strong-invalid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            WriteSourcePlanScopeBundle(bundleDir);
            WriteManualTuneObserverArtifactManifest(bundleDir);
            WriteManualTuneObserverReportWithTuningHints(bundleDir, nearStrongCandidate: true, invalidNearStrongCandidate: true);

            var validationReport = Path.Combine(bundleDir, "validation-manual-tune-observer-near-strong-invalid.json");
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
            Assert.False(validationRoot.GetProperty("manualTuneObserverReportValid").GetBoolean());
            Assert.Equal("invalid", validationRoot.GetProperty("manualTuneObserverReportStatus").GetString());

            var issueCodes = validationRoot.GetProperty("warnings")
                .EnumerateArray()
                .Concat(validationRoot.GetProperty("errors").EnumerateArray())
                .Select(issue => issue.GetProperty("code").GetString() ?? "")
                .ToArray();
            Assert.Contains("manual-tune-observer-near-strong-candidate-strong-present", issueCodes);
            Assert.Contains("manual-tune-observer-near-strong-candidate-samples-missing", issueCodes);
            Assert.Contains("manual-tune-observer-near-strong-candidate-report-path-mismatch", issueCodes);

            var summaryReport = Path.Combine(bundleDir, "summary-manual-tune-observer-near-strong-invalid.json");
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
            var actionIds = summaryDoc.RootElement.GetProperty("acceptanceActionPlan")
                .EnumerateArray()
                .Select(item => item.GetProperty("actionId").GetString() ?? "")
                .ToArray();
            Assert.DoesNotContain("recapture-manual-observer-near-strong-window", actionIds);
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
    public async Task ManualTuneObserverPartialScanErrorWithReadyCaptureStaysUsable()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell manual-tune observer validator smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-manual-tune-observer-partial-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            WriteSourcePlanScopeBundle(bundleDir);
            WriteManualTuneObserverArtifactManifest(bundleDir);
            WriteManualTuneObserverReportWithTuningHints(bundleDir, weakOnlyCapture: true, partialScanError: true);

            var validationReport = Path.Combine(bundleDir, "validation-manual-tune-observer-partial-scan.json");
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
            Assert.True(validationRoot.GetProperty("manualTuneObserverReportValid").GetBoolean());
            Assert.True(validationRoot.GetProperty("manualTuneObserverReportReady").GetBoolean());
            Assert.False(validationRoot.GetProperty("manualTuneObserverOk").GetBoolean());
            Assert.Equal("Unable to connect to the remote server", validationRoot.GetProperty("manualTuneObserverScanError").GetString());
            Assert.Equal("weak-only", validationRoot.GetProperty("manualTuneObserverReportStatus").GetString());
            Assert.Equal(1, validationRoot.GetProperty("manualTuneObserverReferencedCaptureReadyCount").GetInt32());
            Assert.Equal(0, validationRoot.GetProperty("manualTuneObserverReferencedCaptureProblemCount").GetInt32());

            var issueCodes = validationRoot.GetProperty("warnings")
                .EnumerateArray()
                .Concat(validationRoot.GetProperty("errors").EnumerateArray())
                .Select(issue => issue.GetProperty("code").GetString() ?? "")
                .ToArray();
            Assert.Contains("manual-tune-observer-partial-scan-error", issueCodes);
            Assert.DoesNotContain("manual-tune-observer-scan-error", issueCodes);

            var summaryReport = Path.Combine(bundleDir, "summary-manual-tune-observer-partial-scan.json");
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
            var actionIds = summaryDoc.RootElement.GetProperty("acceptanceActionPlan")
                .EnumerateArray()
                .Select(item => item.GetProperty("actionId").GetString() ?? "")
                .ToArray();
            Assert.Contains("capture-manual-observer-best-observed-vfo", actionIds);
            Assert.DoesNotContain("recapture-manual-observer-near-strong-window", actionIds);
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
    public async Task ManualTuneObserverPartialScanErrorWithUnreadyReferencedCaptureStaysBlocked()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell manual-tune observer validator smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-manual-tune-observer-partial-scan-unready-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            WriteSourcePlanScopeBundle(bundleDir);
            WriteManualTuneObserverArtifactManifest(bundleDir);
            WriteManualTuneObserverReportWithTuningHints(
                bundleDir,
                weakOnlyCapture: true,
                partialScanError: true,
                watcherReportNotReady: true);

            var validationReport = Path.Combine(bundleDir, "validation-manual-tune-observer-partial-scan-unready.json");
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
            Assert.False(validationRoot.GetProperty("manualTuneObserverReportValid").GetBoolean());
            Assert.False(validationRoot.GetProperty("manualTuneObserverReportReady").GetBoolean());
            Assert.Equal(0, validationRoot.GetProperty("manualTuneObserverReferencedCaptureReadyCount").GetInt32());
            Assert.Equal(1, validationRoot.GetProperty("manualTuneObserverReferencedCaptureProblemCount").GetInt32());

            var issueCodes = validationRoot.GetProperty("warnings")
                .EnumerateArray()
                .Concat(validationRoot.GetProperty("errors").EnumerateArray())
                .Select(issue => issue.GetProperty("code").GetString() ?? "")
                .ToArray();
            Assert.Contains("manual-tune-observer-capture-report-not-ok", issueCodes);
            Assert.Contains("manual-tune-observer-capture-report-not-ready", issueCodes);
            Assert.Contains("manual-tune-observer-scan-error", issueCodes);
            Assert.DoesNotContain("manual-tune-observer-partial-scan-error", issueCodes);

            var summaryReport = Path.Combine(bundleDir, "summary-manual-tune-observer-partial-scan-unready.json");
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
            var actionIds = summaryDoc.RootElement.GetProperty("acceptanceActionPlan")
                .EnumerateArray()
                .Select(item => item.GetProperty("actionId").GetString() ?? "")
                .ToArray();
            Assert.DoesNotContain("capture-manual-observer-best-observed-vfo", actionIds);
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
    public async Task ManualTuneObserverObservedVfoTriageFallsBackToObservedVfoWithoutHint()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell manual-tune observer validator smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-manual-tune-observer-observed-no-hint-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            WriteSourcePlanScopeBundle(bundleDir);
            WriteManualTuneObserverArtifactManifest(bundleDir);
            WriteManualTuneObserverReportWithTuningHints(bundleDir, includeTuningHints: false, includeCapture: false);

            var validationReport = Path.Combine(bundleDir, "validation-manual-tune-observer-observed-no-hint.json");
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

            var summaryReport = Path.Combine(bundleDir, "summary-manual-tune-observer-observed-no-hint.json");
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
            var action = summaryDoc.RootElement.GetProperty("acceptanceActionPlan")
                .EnumerateArray()
                .Single(item => item.GetProperty("actionId").GetString() == "capture-manual-observer-best-observed-vfo");
            var reason = action.GetProperty("reason").GetString() ?? "";
            var manualAction = action.GetProperty("manualAction").GetString() ?? "";

            Assert.Contains("observedVfoHz=14331500", reason, StringComparison.Ordinal);
            Assert.DoesNotContain("suggestedVfoMhz=0", reason);
            Assert.Contains("Manually keep G2 near the best observed VFO 14331500 Hz", manualAction, StringComparison.Ordinal);
            Assert.DoesNotContain("0 MHz", manualAction);
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
    public async Task ManualTuneObserverObservedVfoTriageRejectsZeroObservedVfo()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell manual-tune observer validator smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-manual-tune-observer-observed-zero-vfo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            WriteSourcePlanScopeBundle(bundleDir);
            WriteManualTuneObserverArtifactManifest(bundleDir);
            WriteManualTuneObserverReportWithTuningHints(
                bundleDir,
                includeTuningHints: false,
                includeCapture: false,
                observedVfoHz: 0L);

            var validationReport = Path.Combine(bundleDir, "validation-manual-tune-observer-observed-zero-vfo.json");
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

            var summaryReport = Path.Combine(bundleDir, "summary-manual-tune-observer-observed-zero-vfo.json");
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
            var actionIds = summaryDoc.RootElement.GetProperty("acceptanceActionPlan")
                .EnumerateArray()
                .Select(item => item.GetProperty("actionId").GetString() ?? "")
                .ToArray();

            Assert.DoesNotContain("capture-manual-observer-best-observed-vfo", actionIds);
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
        bool staleFlattenedBestObservedVfo = false,
        bool includeCapture = true,
        bool nearStrongCandidate = false,
        bool invalidNearStrongCandidate = false,
        bool weakOnlyCapture = false,
        bool partialScanError = false,
        bool watcherReportNotReady = false,
        long observedVfoHz = 14_331_500L)
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
            currentVfoHz = observedVfoHz,
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
            ok = !partialScanError,
            scanError = partialScanError ? "Unable to connect to the remote server" : "",
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
            observedVfos = includeObservedVfos ? new object[] { ManualObservedVfo(hint, includeTuningHints, includeCapture, observedVfoHz) } : Array.Empty<object>(),
            bestObservedVfo = includeObservedVfos ? ManualObservedVfo(hint, includeTuningHints, includeCapture, observedVfoHz) : null,
            bestObservedVfoHz = includeObservedVfos ? (staleFlattenedBestObservedVfo ? 14_000_000L : observedVfoHz) : 0L,
            bestObservedVfoMhz = includeObservedVfos ? observedVfoHz / 1_000_000.0 : 0.0,
            bestObservedVfoStatus = includeObservedVfos ? (staleFlattenedBestObservedVfo ? "stale-flattened" : (includeCapture ? "capture-qualified" : "tuning-hint")) : "",
            bestObservedVfoScore = includeObservedVfos ? (includeCapture ? 256.8 : 85.6) : 0.0,
            bestObservedVfoSuggestedVfoHz = includeObservedVfos && includeTuningHints ? (staleFlattenedBestObservedVfo ? 14_000_100L : 14_365_124L) : 0L,
            bestObservedVfoSuggestedVfoMhz = includeObservedVfos && includeTuningHints ? 14.365124 : 0.0,
            bestObservedVfoSuggestedDialShiftHz = includeObservedVfos && includeTuningHints ? 33_624.0 : 0.0,
            bestObservedVfoSuggestedTuneReason = includeObservedVfos && includeTuningHints ? "above-filter" : "",
            captureCount = includeCapture ? 1 : 0,
            uniqueCapturedVfoCount = includeCapture ? 1 : 0,
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
            captureQualifiedPollCount = includeCapture ? 1 : 0,
            readyCaptureCount = includeCapture ? 1 : 0,
            mixedWeakStrongReady = includeCapture,
            mixedWeakStrongReadyCaptureCount = includeCapture ? 1 : 0,
            weakInputSampleCount = includeCapture ? 8 : 0,
            strongInputSampleCount = includeCapture ? 9 : 0,
            nearStrongInputSampleCount = includeCapture ? 2 : 0,
            speechQualifiedWeakInputSampleCount = includeCapture ? 6 : 0,
            speechQualifiedStrongInputSampleCount = includeCapture ? 7 : 0,
            passbandQualifiedWeakInputSampleCount = includeCapture ? 5 : 0,
            passbandQualifiedStrongInputSampleCount = includeCapture ? 5 : 0,
            agcPumpingRiskCaptureCount = 0,
            captures = includeCapture ? new object[]
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
            } : Array.Empty<object>(),
            polls = new object[]
            {
                ManualTunePoll(1, hint, includeTuningHints, includeCapture, observedVfoHz),
                ManualTunePoll(2, hint, includeTuningHints, includeCapture, observedVfoHz),
                ManualTunePoll(3, hint, includeTuningHints, includeCapture, observedVfoHz)
            },
            recommendations = new[]
            {
                "Read-only manual tuning hint: strongest frontend peak is above the RX filter; manually tune near 14.365124 MHz before capture."
            }
        };

        var reportJson = JsonSerializer.SerializeToNode(report, CamelCaseJson)!.AsObject();
        if (weakOnlyCapture && includeCapture && !nearStrongCandidate)
        {
            var capture = reportJson["captures"]!.AsArray()[0]!.AsObject();
            capture["strongInputThresholdDbfs"] = -22.0;
            capture["nearStrongInputThresholdDbfs"] = -26.0;
            capture["strongInputSampleCount"] = 0;
            capture["nearStrongInputSampleCount"] = 0;
            capture["mixedWeakStrongEvidenceStatus"] = "missing-strong-input";
            capture["mixedWeakStrongEvidenceReady"] = false;
            capture["weakStrongOutputGapDb"] = null;
            capture["speechQualifiedStrongInputSampleCount"] = 0;
            capture["passbandQualifiedStrongInputSampleCount"] = 0;

            reportJson["mixedWeakStrongReady"] = false;
            reportJson["mixedWeakStrongReadyCaptureCount"] = 0;
            reportJson["strongInputSampleCount"] = 0;
            reportJson["nearStrongInputSampleCount"] = 0;
            reportJson["speechQualifiedStrongInputSampleCount"] = 0;
            reportJson["passbandQualifiedStrongInputSampleCount"] = 0;
            reportJson["mixedWeakStrongEvidenceStatusCounts"] = JsonSerializer.SerializeToNode(new object[]
            {
                new { status = "missing-strong-input", count = 1 }
            }, CamelCaseJson);
            reportJson["missingStrongInputCaptureCount"] = 1;
            reportJson["missingWeakInputCaptureCount"] = 0;
            reportJson["missingWeakAndStrongInputCaptureCount"] = 0;
            reportJson["missingOutputGapCaptureCount"] = 0;
            reportJson["weakStrongOutputGapWatchCaptureCount"] = 0;
            reportJson["weakOnlyCaptureCount"] = 1;
            reportJson["readyWeakOnlyCaptureCount"] = 1;
            reportJson["strongOnlyCaptureCount"] = 0;
            reportJson["bestWeakOnlyCapture"] = capture.DeepClone();
            reportJson["bestWeakOnlyCaptureScore"] = 177.3;
            reportJson["bestWeakOnlyCaptureVfoHz"] = 14_365_124L;
            reportJson["bestWeakOnlyCaptureVfoMhz"] = 14.365124;
            reportJson["bestWeakOnlyCaptureReportPath"] = captureReportPath;
            reportJson["bestWeakOnlyCaptureWeakInputSampleCount"] = 8;
            reportJson["bestWeakOnlyCaptureNearStrongInputSampleCount"] = 0;
            reportJson["bestWeakOnlyCapturePassbandQualifiedWeakInputSampleCount"] = 5;
            reportJson["bestWeakOnlyCaptureAgcStabilityStatus"] = "stable";
        }
        if (nearStrongCandidate && includeCapture)
        {
            var capture = reportJson["captures"]!.AsArray()[0]!.AsObject();
            capture["strongInputThresholdDbfs"] = -22.0;
            capture["nearStrongInputThresholdDbfs"] = -26.0;
            capture["strongInputSampleCount"] = 0;
            capture["nearStrongInputSampleCount"] = 3;
            capture["mixedWeakStrongEvidenceStatus"] = "missing-strong-input";
            capture["mixedWeakStrongEvidenceReady"] = false;
            capture["weakStrongOutputGapDb"] = null;
            capture["speechQualifiedStrongInputSampleCount"] = 0;
            capture["speechQualifiedNearStrongInputSampleCount"] = 2;
            capture["passbandQualifiedStrongInputSampleCount"] = 0;
            capture["passbandQualifiedNearStrongInputSampleCount"] = 1;
            capture["topNearStrongInputs"] = JsonSerializer.SerializeToNode(new object[]
            {
                new
                {
                    sampleIndex = 2,
                    inputDbfs = -24.5,
                    distanceToStrongThresholdDb = 2.5,
                    speechQualified = true,
                    passbandQualified = true
                }
            }, CamelCaseJson);

            reportJson["mixedWeakStrongReady"] = false;
            reportJson["mixedWeakStrongReadyCaptureCount"] = 0;
            reportJson["strongInputSampleCount"] = 0;
            reportJson["nearStrongInputSampleCount"] = 3;
            reportJson["speechQualifiedStrongInputSampleCount"] = 0;
            reportJson["passbandQualifiedStrongInputSampleCount"] = 0;
            reportJson["mixedWeakStrongEvidenceStatusCounts"] = JsonSerializer.SerializeToNode(new object[]
            {
                new { status = "missing-strong-input", count = 1 }
            }, CamelCaseJson);
            reportJson["missingStrongInputCaptureCount"] = 1;
            reportJson["missingWeakInputCaptureCount"] = 0;
            reportJson["missingWeakAndStrongInputCaptureCount"] = 0;
            reportJson["missingOutputGapCaptureCount"] = 0;
            reportJson["weakStrongOutputGapWatchCaptureCount"] = 0;
            reportJson["weakOnlyCaptureCount"] = 1;
            reportJson["readyWeakOnlyCaptureCount"] = 1;
            reportJson["strongOnlyCaptureCount"] = 0;
            reportJson["bestWeakOnlyCapture"] = capture.DeepClone();
            reportJson["bestWeakOnlyCaptureScore"] = 211.4;
            reportJson["bestWeakOnlyCaptureVfoHz"] = 14_365_124L;
            reportJson["bestWeakOnlyCaptureVfoMhz"] = 14.365124;
            reportJson["bestWeakOnlyCaptureReportPath"] = captureReportPath;
            reportJson["bestWeakOnlyCaptureWeakInputSampleCount"] = 8;
            reportJson["bestWeakOnlyCaptureNearStrongInputSampleCount"] = 3;
            reportJson["bestWeakOnlyCapturePassbandQualifiedWeakInputSampleCount"] = 5;
            reportJson["bestWeakOnlyCaptureAgcStabilityStatus"] = "stable";
            reportJson["nearStrongPromotionCandidateCaptureCount"] = 1;
            reportJson["bestNearStrongPromotionCandidateCapture"] = capture.DeepClone();
            reportJson["bestNearStrongPromotionCandidateScore"] = 198.2;
            reportJson["bestNearStrongPromotionCandidateReportPath"] = captureReportPath;
            reportJson["bestNearStrongPromotionCandidateVfoHz"] = 14_365_124L;
            reportJson["bestNearStrongPromotionCandidateVfoMhz"] = 14.365124;
            reportJson["bestNearStrongPromotionCandidateDistanceToStrongThresholdDb"] = 2.5;
            reportJson["bestNearStrongPromotionCandidateNearStrongInputSampleCount"] = 3;
            reportJson["bestNearStrongPromotionCandidateSpeechQualifiedNearStrongInputSampleCount"] = 2;
            reportJson["bestNearStrongPromotionCandidatePassbandQualifiedNearStrongInputSampleCount"] = 1;

            if (invalidNearStrongCandidate)
            {
                var invalidCandidate = reportJson["bestNearStrongPromotionCandidateCapture"]!.AsObject();
                invalidCandidate["strongInputSampleCount"] = 4;
                invalidCandidate["nearStrongInputSampleCount"] = 0;
                invalidCandidate["topNearStrongInputs"] = new JsonArray();
                invalidCandidate["reportPath"] = "artifacts/manual-tune-observer/14365124/not-the-candidate.json";
            }
        }
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

        WriteSyntheticWatcherFiles(
            bundleDir,
            captureReportPath,
            captureJsonlPath,
            nearStrongCandidate && includeCapture,
            weakOnlyCapture && includeCapture,
            watcherReportNotReady && includeCapture);
    }

    private static string GetBooleanDebug(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return "(missing)";
        }

        return property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean().ToString()
            : property.ToString();
    }

    private static string GetLongDebug(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return "(missing)";
        }

        return property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var value)
            ? value.ToString()
            : property.ToString();
    }

    private static object ManualObservedVfo(object hint, bool includeTuningHints, bool includeCapture, long observedVfoHz) => new
    {
        vfoHz = observedVfoHz,
        vfoMhz = observedVfoHz / 1_000_000.0,
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
        captureQualifiedPollCount = includeCapture ? 1 : 0,
        maxCoherentSnrDb = 18.0,
        maxAudioRmsDbfs = -34.0,
        maxNr5OutputDbfs = -31.0,
        bestTuningHint = includeTuningHints ? hint : null,
        frontendSuggestedDialShiftHz = includeTuningHints ? 33_624.0 : 0.0,
        frontendSuggestedVfoHz = includeTuningHints ? 14_365_124L : 0L,
        frontendSuggestedVfoMhz = includeTuningHints ? 14.365124 : 0.0,
        frontendSuggestedTuneReason = includeTuningHints ? "above-filter" : "",
        frontendSuggestedFilterDistanceHz = includeTuningHints ? 32_098.0 : 0.0,
        status = includeCapture ? "capture-qualified" : "tuning-hint",
        score = includeCapture ? 256.8 : 85.6
    };

    private static JsonObject ManualTunePoll(int poll, object hint, bool includeTuningHints, bool includeCapture, long observedVfoHz)
    {
        var pollJson = JsonSerializer.SerializeToNode(new
        {
            poll,
            vfoHz = observedVfoHz,
            radioLoHz = observedVfoHz,
            mode = "USB",
            stablePollCount = poll,
            sceneFresh = true,
            signalProfile = "speech-with-adjacent-strong",
            captureQualified = includeCapture && poll == 3,
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

    private static void WriteSyntheticWatcherFiles(
        string bundleDir,
        string reportPath,
        string jsonlPath,
        bool nearStrongCandidate = false,
        bool weakOnlyCapture = false,
        bool watcherReportNotReady = false)
    {
        var resolvedReportPath = Path.Combine(bundleDir, reportPath.Replace('/', Path.DirectorySeparatorChar));
        var resolvedJsonlPath = Path.Combine(bundleDir, jsonlPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(resolvedReportPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(resolvedJsonlPath)!);

        var summary = new
        {
            schemaVersion = 1,
            tool = "watch-dsp-live-diagnostics",
            ok = !watcherReportNotReady,
            readyForBenchmarkTrace = !watcherReportNotReady,
            sampleCount = 24,
            jsonlPath,
            nr5WeakSignalWatch = new
            {
                weakInputSampleCount = 8,
                strongInputSampleCount = nearStrongCandidate || weakOnlyCapture ? 0 : 9,
                nearStrongInputSampleCount = nearStrongCandidate ? 3 : (weakOnlyCapture ? 0 : 2),
                mixedWeakStrongEvidenceReady = !nearStrongCandidate && !weakOnlyCapture,
                mixedWeakStrongEvidenceStatus = nearStrongCandidate || weakOnlyCapture ? "missing-strong-input" : "ready",
                speechQualifiedWeakInputSampleCount = 6,
                speechQualifiedStrongInputSampleCount = nearStrongCandidate || weakOnlyCapture ? 0 : 7,
                speechQualifiedNearStrongInputSampleCount = nearStrongCandidate ? 2 : 0,
                passbandQualifiedWeakInputSampleCount = 5,
                passbandQualifiedStrongInputSampleCount = nearStrongCandidate || weakOnlyCapture ? 0 : 5,
                passbandQualifiedNearStrongInputSampleCount = nearStrongCandidate ? 1 : 0
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
