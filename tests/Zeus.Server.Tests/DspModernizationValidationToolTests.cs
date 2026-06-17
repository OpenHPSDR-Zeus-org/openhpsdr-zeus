// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class DspModernizationValidationToolTests
{
    private static readonly JsonSerializerOptions CamelCaseJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    [SkippableFact]
    public async Task GlobalExternalEngineComparisonScopeRequiresBakeoffReport()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell validator smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-dsp-validation-global-external-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            WriteGlobalExternalScopeBundle(bundleDir);

            var validationReport = Path.Combine(bundleDir, "validation-report.json");
            var validation = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "validate-dsp-modernization-bundle.ps1"),
                "-BundleDir", bundleDir,
                "-ReportPath", validationReport,
                "-AllowPreflight",
                "-JsonOnly");

            Assert.NotEqual(0, validation.ExitCode);
            Assert.True(File.Exists(validationReport), validation.CombinedOutput);

            using var validationDoc = JsonDocument.Parse(await File.ReadAllTextAsync(validationReport));
            var validationRoot = validationDoc.RootElement;
            Assert.True(validationRoot.GetProperty("externalEngineBakeoffRequiredByScope").GetBoolean());
            Assert.False(validationRoot.GetProperty("externalEngineBakeoffReportPresent").GetBoolean());
            Assert.Equal(2, validationRoot.GetProperty("externalEngineBakeoffScopeTriggerCount").GetInt32());

            var triggers = validationRoot.GetProperty("externalEngineBakeoffScopeTriggers")
                .EnumerateArray()
                .Select(value => value.GetString())
                .ToArray();

            Assert.Contains("benchmark-plan.requiredComparisons", triggers);
            Assert.Contains("benchmark-capture-manifest.requiredComparisons", triggers);
            Assert.DoesNotContain(triggers, trigger => trigger?.Contains(".scenario:", StringComparison.Ordinal) == true);

            var warningCodes = validationRoot.GetProperty("warnings")
                .EnumerateArray()
                .Select(warning => warning.GetProperty("code").GetString())
                .ToArray();
            Assert.Contains("external-bakeoff-required-for-opt-in-comparison", warningCodes);

            var summaryReport = Path.Combine(bundleDir, "validation-summary.json");
            var summaryMarkdown = Path.Combine(bundleDir, "validation-summary.md");
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
            Assert.True(summaryRoot.GetProperty("externalEngineBakeoffRequiredByScope").GetBoolean());
            Assert.Equal(2, summaryRoot.GetProperty("externalEngineBakeoffScopeTriggerCount").GetInt32());

            var bakeoffGate = summaryRoot.GetProperty("evidenceGates")
                .EnumerateArray()
                .Single(gate => gate.GetProperty("gateId").GetString() == "external-engine-bakeoff");
            Assert.True(bakeoffGate.GetProperty("requiredForAcceptance").GetBoolean());
            Assert.False(bakeoffGate.GetProperty("ready").GetBoolean());
            var gateDetail = bakeoffGate.GetProperty("detail").GetString() ?? "";
            Assert.Contains("benchmark-plan.requiredComparisons", gateDetail, StringComparison.Ordinal);
            Assert.Contains("benchmark-capture-manifest.requiredComparisons", gateDetail, StringComparison.Ordinal);

            var markdown = await File.ReadAllTextAsync(summaryMarkdown);
            Assert.Contains("Bakeoff scope triggers", markdown, StringComparison.Ordinal);
            Assert.Contains("benchmark-plan.requiredComparisons", markdown, StringComparison.Ordinal);
            Assert.Contains("benchmark-capture-manifest.requiredComparisons", markdown, StringComparison.Ordinal);
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
    public async Task ArtifactManifestScaffoldRequiresExternalBakeoffReportForGlobalScope()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell artifact scaffold smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-dsp-artifact-global-external-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            WriteGlobalExternalScopeBundle(bundleDir);

            var manifestPath = Path.Combine(bundleDir, "artifact-manifest.json");
            var scaffold = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "new-dsp-artifact-manifest.ps1"),
                "-BundleDir", bundleDir,
                "-OutputPath", manifestPath,
                "-Force");

            Assert.Equal(0, scaffold.ExitCode);
            Assert.True(File.Exists(manifestPath), scaffold.CombinedOutput);

            using var manifestDoc = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
            var manifestRoot = manifestDoc.RootElement;
            Assert.True(manifestRoot.GetProperty("externalEngineBakeoffInScope").GetBoolean());
            var scopeTriggers = manifestRoot.GetProperty("externalEngineBakeoffScopeTriggers")
                .EnumerateArray()
                .Select(value => value.GetString())
                .ToArray();
            Assert.Contains("benchmark-plan.requiredComparisons", scopeTriggers);
            Assert.Contains("benchmark-capture-manifest.requiredComparisons", scopeTriggers);

            var bakeoffArtifact = manifestRoot.GetProperty("artifacts")
                .EnumerateArray()
                .Single(artifact => artifact.GetProperty("id").GetString() == "external-engine-bakeoff-report");

            Assert.True(bakeoffArtifact.GetProperty("required").GetBoolean());
            Assert.Equal("external-candidate-report-json", bakeoffArtifact.GetProperty("kind").GetString());
            Assert.Equal("artifacts/external-engine-bakeoff-report.json", bakeoffArtifact.GetProperty("path").GetString());

            var comparisonIds = bakeoffArtifact.GetProperty("comparisonIds")
                .EnumerateArray()
                .Select(value => value.GetString())
                .ToArray();
            Assert.Contains("candidate-external-engine-opt-in", comparisonIds);

            var notes = manifestRoot.GetProperty("notes")
                .EnumerateArray()
                .Select(value => value.GetString() ?? "")
                .ToArray();
            Assert.Contains(notes, note => note.Contains("candidate-external-engine-opt-in", StringComparison.Ordinal));
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
    public async Task ArtifactManifestScaffoldUsesBenchmarkPlanExternalScopeWhenManifestOmitsIt()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell artifact scaffold smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-dsp-artifact-plan-external-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            WriteGlobalExternalScopeBundle(bundleDir);
            RemoveJsonProperty(Path.Combine(bundleDir, "benchmark-capture-manifest.json"), "requiredComparisons");

            var manifestPath = Path.Combine(bundleDir, "artifact-manifest.json");
            var scaffold = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "new-dsp-artifact-manifest.ps1"),
                "-BundleDir", bundleDir,
                "-OutputPath", manifestPath,
                "-Force");

            Assert.Equal(0, scaffold.ExitCode);
            Assert.True(File.Exists(manifestPath), scaffold.CombinedOutput);

            using var manifestDoc = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
            var manifestRoot = manifestDoc.RootElement;
            Assert.True(manifestRoot.GetProperty("externalEngineBakeoffInScope").GetBoolean());
            var scopeTriggers = manifestRoot.GetProperty("externalEngineBakeoffScopeTriggers")
                .EnumerateArray()
                .Select(value => value.GetString())
                .ToArray();
            Assert.Contains("benchmark-plan.requiredComparisons", scopeTriggers);
            Assert.DoesNotContain("benchmark-capture-manifest.requiredComparisons", scopeTriggers);

            var bakeoffArtifact = manifestRoot.GetProperty("artifacts")
                .EnumerateArray()
                .Single(artifact => artifact.GetProperty("id").GetString() == "external-engine-bakeoff-report");
            Assert.True(bakeoffArtifact.GetProperty("required").GetBoolean());
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
    public async Task ArtifactManifestScaffoldKeepsSourceBenchmarkPlanOnNr5Scope()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell artifact scaffold smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-dsp-artifact-source-plan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            WriteSourcePlanScopeBundle(bundleDir);
            RemoveRequiredArtifactById(Path.Combine(bundleDir, "benchmark-capture-manifest.json"), "live-diagnostics-trace-index");

            var manifestPath = Path.Combine(bundleDir, "artifact-manifest.json");
            var scaffold = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "new-dsp-artifact-manifest.ps1"),
                "-BundleDir", bundleDir,
                "-OutputPath", manifestPath,
                "-Force");

            Assert.Equal(0, scaffold.ExitCode);
            Assert.True(File.Exists(manifestPath), scaffold.CombinedOutput);

            using var manifestDoc = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
            var manifestRoot = manifestDoc.RootElement;
            Assert.False(manifestRoot.GetProperty("externalEngineBakeoffInScope").GetBoolean());
            var scopeTriggers = manifestRoot.GetProperty("externalEngineBakeoffScopeTriggers")
                .EnumerateArray()
                .Select(value => value.GetString())
                .ToArray();
            Assert.Empty(scopeTriggers);

            Assert.DoesNotContain(
                manifestRoot.GetProperty("artifacts").EnumerateArray(),
                artifact => artifact.GetProperty("id").GetString() == "external-engine-bakeoff-report");

            var sourcePlan = DspBenchmarkPlanCatalog.Build();
            Assert.DoesNotContain("candidate-external-engine-opt-in", sourcePlan.RequiredComparisons);
            var weakCarrier = Assert.Single(sourcePlan.Scenarios, scenario => scenario.Id == "weak-cw-carrier");
            var txTwoTone = Assert.Single(sourcePlan.Scenarios, scenario => scenario.Id == "tx-two-tone");
            Assert.Contains("nr5-spnr", weakCarrier.RequiredComparisons);
            Assert.DoesNotContain("nr5-spnr", txTwoTone.RequiredComparisons);
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
    public async Task LiveDiagnosticsHistoryPlanRequiresParityAcceptanceComparisons()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell live history smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-dsp-live-history-plan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            var reportPath = Path.Combine(bundleDir, "artifacts", "live-diagnostics-history.json");
            var history = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "summarize-dsp-live-diagnostics-history.ps1"),
                "-BundleDir", bundleDir,
                "-ReportPath", reportPath,
                "-NoMarkdown",
                "-JsonOnly");

            Assert.Equal(0, history.ExitCode);
            Assert.True(File.Exists(reportPath), history.CombinedOutput);

            using var historyDoc = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
            var root = historyDoc.RootElement;
            Assert.Equal(15, root.GetProperty("schemaVersion").GetInt32());

            var plan = root.GetProperty("latestLiveExperimentPlan");
            Assert.Equal("g2-rx-acceptance-evidence", plan.GetProperty("planScope").GetString());
            Assert.Equal(
                new[] { "current-zeus", "nr5-spnr" },
                ReadStringArray(plan, "tuningComparisons"));
            Assert.Equal(
                new[] { "off-baseline", "thetis-parity", "current-zeus", "nr5-spnr" },
                ReadStringArray(plan, "acceptanceComparisons"));
            Assert.Equal(
                new[] { "off-baseline", "thetis-parity", "current-zeus", "nr5-spnr" },
                ReadStringArray(plan, "recommendedComparisons"));
            Assert.DoesNotContain("candidate-external-engine-opt-in", ReadStringArray(plan, "recommendedComparisons"));

            var templates = plan.GetProperty("matrixCommandTemplates");
            Assert.Contains("-ComparisonId off-baseline", templates.GetProperty("offBaseline").GetString(), StringComparison.Ordinal);
            Assert.Contains("-ComparisonId thetis-parity", templates.GetProperty("thetis").GetString(), StringComparison.Ordinal);
            Assert.Contains("-ComparisonId current-zeus", templates.GetProperty("baseline").GetString(), StringComparison.Ordinal);
            Assert.Contains("-ComparisonId nr5-spnr", templates.GetProperty("candidate").GetString(), StringComparison.Ordinal);
            Assert.Contains("-BaselineComparisonId current-zeus", templates.GetProperty("compare").GetString(), StringComparison.Ordinal);
            Assert.Contains("-CandidateComparisonId nr5-spnr", templates.GetProperty("compare").GetString(), StringComparison.Ordinal);

            foreach (var scenario in plan.GetProperty("scenarios").EnumerateArray())
            {
                Assert.Equal(
                    new[] { "off-baseline", "thetis-parity", "current-zeus", "nr5-spnr" },
                    ReadStringArray(scenario, "requiredComparisons"));
            }

            var coverage = root.GetProperty("latestLiveExperimentCoverage");
            Assert.Equal("not-started", coverage.GetProperty("status").GetString());
            Assert.Equal(
                plan.GetProperty("scenarioCount").GetInt32() * 4,
                coverage.GetProperty("requiredComparisonCount").GetInt32());
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
    public async Task ValidationFlagsIncompleteLiveDiagnosticsHistoryCoverage()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell live history validation smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-dsp-live-history-coverage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            WriteSourcePlanScopeBundle(bundleDir);

            var historyPath = Path.Combine(bundleDir, "artifacts", "live-diagnostics-history.json");
            var history = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "summarize-dsp-live-diagnostics-history.ps1"),
                "-BundleDir", bundleDir,
                "-ReportPath", historyPath,
                "-NoMarkdown",
                "-JsonOnly");

            Assert.Equal(0, history.ExitCode);
            Assert.True(File.Exists(historyPath), history.CombinedOutput);

            var artifactManifestPath = Path.Combine(bundleDir, "artifact-manifest.json");
            var artifactManifest = new
            {
                schemaVersion = 1,
                artifacts = new object[]
                {
                    new
                    {
                        id = "live-diagnostics-history",
                        kind = "diagnostics-history-json",
                        source = "tools/summarize-dsp-live-diagnostics-history.ps1",
                        path = "artifacts/live-diagnostics-history.json",
                        required = true
                    }
                }
            };
            File.WriteAllText(artifactManifestPath, JsonSerializer.Serialize(artifactManifest, CamelCaseJson));

            var validationReport = Path.Combine(bundleDir, "validation-report.json");
            var validation = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "validate-dsp-modernization-bundle.ps1"),
                "-BundleDir", bundleDir,
                "-ArtifactManifestPath", artifactManifestPath,
                "-ReportPath", validationReport,
                "-AllowPreflight",
                "-JsonOnly");

            Assert.NotEqual(0, validation.ExitCode);
            Assert.True(File.Exists(validationReport), validation.CombinedOutput);

            using var validationDoc = JsonDocument.Parse(await File.ReadAllTextAsync(validationReport));
            var validationRoot = validationDoc.RootElement;
            Assert.Equal("not-started", validationRoot.GetProperty("liveDiagnosticsHistoryLiveExperimentCoverageStatus").GetString());
            Assert.True(validationRoot.GetProperty("liveDiagnosticsHistoryLiveExperimentCoverageMissingComparisonCount").GetInt32() > 0);

            var missingCoverageIds = ReadStringArray(validationRoot, "liveDiagnosticsHistoryLiveExperimentCoverageMissingComparisonIds");
            Assert.Contains("weak-cw-carrier/off-baseline", missingCoverageIds);
            Assert.Contains("weak-cw-carrier/thetis-parity", missingCoverageIds);
            Assert.Contains("weak-cw-carrier/current-zeus", missingCoverageIds);
            Assert.Contains("weak-cw-carrier/nr5-spnr", missingCoverageIds);

            var errorCodes = validationRoot.GetProperty("errors")
                .EnumerateArray()
                .Select(error => error.GetProperty("code").GetString())
                .ToArray();
            Assert.Contains("live-history-live-experiment-coverage-incomplete", errorCodes);

            var summaryReport = Path.Combine(bundleDir, "validation-summary.json");
            var summaryMarkdown = Path.Combine(bundleDir, "validation-summary.md");
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
            var liveHistoryGate = summaryRoot.GetProperty("evidenceGates")
                .EnumerateArray()
                .Single(gate => gate.GetProperty("gateId").GetString() == "live-history-provenance");

            Assert.False(liveHistoryGate.GetProperty("ready").GetBoolean());
            Assert.Equal("coverage-not-started", liveHistoryGate.GetProperty("status").GetString());
            var gateDetail = liveHistoryGate.GetProperty("detail").GetString() ?? "";
            Assert.Contains("missingCoverage=", gateDetail, StringComparison.Ordinal);
            Assert.Contains("off-baseline", gateDetail, StringComparison.Ordinal);
            Assert.Contains("thetis-parity", gateDetail, StringComparison.Ordinal);

            var regenerateAction = summaryRoot.GetProperty("acceptanceActionPlan")
                .EnumerateArray()
                .Single(action => action.GetProperty("actionId").GetString() == "regenerate-live-diagnostics-history");
            var actionReason = regenerateAction.GetProperty("reason").GetString() ?? "";
            Assert.Contains("off-baseline", actionReason, StringComparison.Ordinal);
            Assert.Contains("thetis-parity", actionReason, StringComparison.Ordinal);
            Assert.Contains("current-zeus", actionReason, StringComparison.Ordinal);
            Assert.Contains("nr5-spnr", actionReason, StringComparison.Ordinal);

            var repairSteps = ReadStringArray(regenerateAction, "commandSteps");
            Assert.Equal(5, regenerateAction.GetProperty("commandStepCount").GetInt32());
            Assert.Contains(repairSteps, step => step.Contains("-ComparisonId off-baseline", StringComparison.Ordinal));
            Assert.Contains(repairSteps, step => step.Contains("-ComparisonId thetis-parity", StringComparison.Ordinal));
            Assert.Contains(repairSteps, step => step.Contains("-ComparisonId current-zeus", StringComparison.Ordinal));
            Assert.Contains(repairSteps, step => step.Contains("-ComparisonId nr5-spnr", StringComparison.Ordinal));
            Assert.Contains(repairSteps, step => step.Contains("summarize-dsp-live-diagnostics-history.ps1", StringComparison.Ordinal));
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
    public async Task ValidationTriageLiveHistoryCoverageGateTracksCompleteAndPartialStatus()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell validation triage smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-dsp-live-history-coverage-triage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            var completeValidationReport = Path.Combine(bundleDir, "validation-complete.json");
            WriteSyntheticLiveHistoryCoverageValidationReport(
                completeValidationReport,
                coverageStatus: "complete",
                missingComparisonCount: 0,
                missingComparisonIds: Array.Empty<string>());

            var completeSummaryReport = Path.Combine(bundleDir, "summary-complete.json");
            var completeSummary = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "summarize-dsp-modernization-validation-report.ps1"),
                "-ValidationReportPath", completeValidationReport,
                "-ReportPath", completeSummaryReport,
                "-NoMarkdown",
                "-JsonOnly");

            Assert.Equal(0, completeSummary.ExitCode);
            using (var completeSummaryDoc = JsonDocument.Parse(await File.ReadAllTextAsync(completeSummaryReport)))
            {
                var liveHistoryGate = completeSummaryDoc.RootElement.GetProperty("evidenceGates")
                    .EnumerateArray()
                    .Single(gate => gate.GetProperty("gateId").GetString() == "live-history-provenance");

                Assert.True(liveHistoryGate.GetProperty("ready").GetBoolean());
                Assert.Equal("hash-ready", liveHistoryGate.GetProperty("status").GetString());
                Assert.DoesNotContain(
                    completeSummaryDoc.RootElement.GetProperty("acceptanceActionPlan").EnumerateArray(),
                    action => action.GetProperty("gateId").GetString() == "live-history-provenance");
            }

            var partialValidationReport = Path.Combine(bundleDir, "validation-partial.json");
            WriteSyntheticLiveHistoryCoverageValidationReport(
                partialValidationReport,
                coverageStatus: "partial",
                missingComparisonCount: 2,
                missingComparisonIds: new[]
                {
                    "weak-cw-carrier/thetis-parity",
                    "weak-cw-carrier/nr5-spnr"
                });

            var partialSummaryReport = Path.Combine(bundleDir, "summary-partial.json");
            var partialSummary = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "summarize-dsp-modernization-validation-report.ps1"),
                "-ValidationReportPath", partialValidationReport,
                "-ReportPath", partialSummaryReport,
                "-NoMarkdown",
                "-JsonOnly");

            Assert.Equal(0, partialSummary.ExitCode);
            using var partialSummaryDoc = JsonDocument.Parse(await File.ReadAllTextAsync(partialSummaryReport));
            var partialRoot = partialSummaryDoc.RootElement;
            var partialGate = partialRoot.GetProperty("evidenceGates")
                .EnumerateArray()
                .Single(gate => gate.GetProperty("gateId").GetString() == "live-history-provenance");

            Assert.False(partialGate.GetProperty("ready").GetBoolean());
            Assert.Equal("coverage-partial", partialGate.GetProperty("status").GetString());
            var partialDetail = partialGate.GetProperty("detail").GetString() ?? "";
            Assert.Contains("missingCoverage=2", partialDetail, StringComparison.Ordinal);
            Assert.Contains("weak-cw-carrier/thetis-parity", partialDetail, StringComparison.Ordinal);
            Assert.Contains("weak-cw-carrier/nr5-spnr", partialDetail, StringComparison.Ordinal);

            Assert.Contains(
                partialRoot.GetProperty("acceptanceActionPlan").EnumerateArray(),
                action => action.GetProperty("gateId").GetString() == "live-history-provenance"
                    && action.GetProperty("actionId").GetString() == "regenerate-live-diagnostics-history");
            var partialAction = partialRoot.GetProperty("acceptanceActionPlan")
                .EnumerateArray()
                .Single(action => action.GetProperty("gateId").GetString() == "live-history-provenance"
                    && action.GetProperty("actionId").GetString() == "regenerate-live-diagnostics-history");
            var partialRepairSteps = ReadStringArray(partialAction, "commandSteps");
            Assert.Equal(5, partialAction.GetProperty("commandStepCount").GetInt32());
            Assert.Contains(partialRepairSteps, step => step.Contains("-ComparisonId off-baseline", StringComparison.Ordinal));
            Assert.Contains(partialRepairSteps, step => step.Contains("-ComparisonId thetis-parity", StringComparison.Ordinal));
            Assert.Contains(partialRepairSteps, step => step.Contains("-ComparisonId current-zeus", StringComparison.Ordinal));
            Assert.Contains(partialRepairSteps, step => step.Contains("-ComparisonId nr5-spnr", StringComparison.Ordinal));

            var missingCoverageValidationReport = Path.Combine(bundleDir, "validation-missing-coverage.json");
            WriteSyntheticLiveHistoryCoverageValidationReport(
                missingCoverageValidationReport,
                coverageStatus: "",
                missingComparisonCount: 0,
                missingComparisonIds: Array.Empty<string>());

            var missingCoverageSummaryReport = Path.Combine(bundleDir, "summary-missing-coverage.json");
            var missingCoverageSummary = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "summarize-dsp-modernization-validation-report.ps1"),
                "-ValidationReportPath", missingCoverageValidationReport,
                "-ReportPath", missingCoverageSummaryReport,
                "-NoMarkdown",
                "-JsonOnly");

            Assert.Equal(0, missingCoverageSummary.ExitCode);
            using var missingCoverageSummaryDoc = JsonDocument.Parse(await File.ReadAllTextAsync(missingCoverageSummaryReport));
            var missingCoverageGate = missingCoverageSummaryDoc.RootElement.GetProperty("evidenceGates")
                .EnumerateArray()
                .Single(gate => gate.GetProperty("gateId").GetString() == "live-history-provenance");

            Assert.False(missingCoverageGate.GetProperty("ready").GetBoolean());
            Assert.Equal("coverage-missing", missingCoverageGate.GetProperty("status").GetString());
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
    public async Task ValidationTriageLiveMatrixActionCapturesParityComparisons()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell validation triage smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-dsp-live-triage-plan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            WriteSourcePlanScopeBundle(bundleDir);

            var validationReport = Path.Combine(bundleDir, "validation-report.json");
            var validation = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "validate-dsp-modernization-bundle.ps1"),
                "-BundleDir", bundleDir,
                "-ReportPath", validationReport,
                "-AllowPreflight",
                "-JsonOnly");

            Assert.NotEqual(0, validation.ExitCode);
            Assert.True(File.Exists(validationReport), validation.CombinedOutput);

            var summaryReport = Path.Combine(bundleDir, "validation-summary.json");
            var summaryMarkdown = Path.Combine(bundleDir, "validation-summary.md");
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
            var evidenceGates = summaryDoc.RootElement.GetProperty("evidenceGates").EnumerateArray().ToArray();
            var zeusLiveGate = evidenceGates.Single(gate => gate.GetProperty("gateId").GetString() == "live-trace-comparison");
            Assert.Equal("Current-Zeus live trace comparison", zeusLiveGate.GetProperty("name").GetString());
            var thetisLiveGate = evidenceGates.Single(gate => gate.GetProperty("gateId").GetString() == "thetis-parity-live-comparison");
            Assert.Equal("Thetis-parity live trace comparison", thetisLiveGate.GetProperty("name").GetString());
            Assert.False(thetisLiveGate.GetProperty("ready").GetBoolean());
            Assert.True(thetisLiveGate.GetProperty("requiredForAcceptance").GetBoolean());
            Assert.Contains("metricCatalogAlignment", thetisLiveGate.GetProperty("detail").GetString() ?? "", StringComparison.Ordinal);

            var readinessStages = summaryDoc.RootElement.GetProperty("acceptanceReadiness").EnumerateArray().ToArray();
            Assert.False(summaryDoc.RootElement.GetProperty("optInDspBuildOutReady").GetBoolean());
            Assert.Equal(
                "blocked-buildout-prerequisites",
                summaryDoc.RootElement.GetProperty("optInDspBuildOutStatus").GetString());
            Assert.Contains("wdsp-native-symbol-audit", ReadStringArray(summaryDoc.RootElement, "optInDspBuildOutBlockingGateIds"));

            var buildOutStage = readinessStages.Single(stage => stage.GetProperty("stageId").GetString() == "opt-in-dsp-buildout-prerequisites");
            Assert.False(buildOutStage.GetProperty("ready").GetBoolean());
            Assert.False(buildOutStage.GetProperty("blocksDefaultBehaviorChange").GetBoolean());
            Assert.Contains("wdsp-native-symbol-audit", ReadStringArray(buildOutStage, "blockingGateIds"));

            var g2Stage = readinessStages.Single(stage => stage.GetProperty("stageId").GetString() == "g2-first-pass-evidence");
            Assert.Contains("thetisLiveTraceReady", g2Stage.GetProperty("detail").GetString() ?? "", StringComparison.Ordinal);
            Assert.Contains("thetis-parity-live-comparison", ReadStringArray(g2Stage, "blockingGateIds"));

            var action = summaryDoc.RootElement
                .GetProperty("acceptanceActionPlan")
                .EnumerateArray()
                .Single(item => item.GetProperty("actionId").GetString() == "capture-and-compare-live-matrix");

            var steps = ReadStringArray(action, "commandSteps");
            Assert.Equal(10, action.GetProperty("commandStepCount").GetInt32());
            Assert.Equal(10, steps.Length);
            Assert.Contains(steps, step => step.Contains("-ComparisonId off-baseline", StringComparison.Ordinal));
            Assert.Contains(steps, step => step.Contains("-ComparisonId thetis-parity", StringComparison.Ordinal));
            Assert.Contains(steps, step => step.Contains("-ComparisonId current-zeus", StringComparison.Ordinal));
            Assert.Contains(steps, step => step.Contains("-ComparisonId nr5-spnr", StringComparison.Ordinal));
            Assert.Contains(steps, step => step.Contains("summarize-dsp-live-diagnostics-history.ps1", StringComparison.Ordinal));
            Assert.Contains(steps, step => step.Contains("compare-dsp-live-diagnostics-matrix.ps1", StringComparison.Ordinal)
                && step.Contains("-BaselineComparisonId current-zeus", StringComparison.Ordinal));
            Assert.Contains(steps, step => step.Contains("compare-dsp-live-diagnostics-matrix.ps1", StringComparison.Ordinal)
                && step.Contains("-BaselineComparisonId thetis-parity", StringComparison.Ordinal)
                && step.Contains("live-diagnostics-trace-comparison.thetis-parity.json", StringComparison.Ordinal));
            Assert.Contains(steps, step => step.Contains("new-dsp-artifact-manifest.ps1", StringComparison.Ordinal)
                && step.Contains("-AcceptanceManifest", StringComparison.Ordinal)
                && step.Contains("-RequireLiveAcceptanceArtifacts", StringComparison.Ordinal));
            Assert.Contains(steps, step => step.Contains("validate-dsp-modernization-bundle.ps1", StringComparison.Ordinal)
                && step.Contains("-RequireArtifactFiles", StringComparison.Ordinal)
                && step.Contains("validation-report.json", StringComparison.Ordinal));
            Assert.Contains(steps, step => step.Contains("summarize-dsp-modernization-validation-report.ps1", StringComparison.Ordinal)
                && step.Contains("validation-triage-report.json", StringComparison.Ordinal)
                && step.Contains("-FailOnIssues", StringComparison.Ordinal));

            var commandTemplate = action.GetProperty("commandTemplate").GetString() ?? "";
            Assert.Contains("-ComparisonId off-baseline", commandTemplate, StringComparison.Ordinal);
            Assert.Contains("-ComparisonId thetis-parity", commandTemplate, StringComparison.Ordinal);
            Assert.Contains("-BaselineComparisonId current-zeus", commandTemplate, StringComparison.Ordinal);
            Assert.Contains("-CandidateComparisonId nr5-spnr", commandTemplate, StringComparison.Ordinal);
            Assert.Contains("-RequireLiveAcceptanceArtifacts", commandTemplate, StringComparison.Ordinal);
            Assert.Contains("-RequireArtifactFiles", commandTemplate, StringComparison.Ordinal);
            Assert.Contains("validation-triage-report.json", commandTemplate, StringComparison.Ordinal);

            var followUp = action.GetProperty("followUp").GetString() ?? "";
            Assert.Contains("validation-triage-report.json", followUp, StringComparison.Ordinal);
            Assert.Contains("strict validation passes", followUp, StringComparison.Ordinal);
            Assert.DoesNotContain("required=true", followUp, StringComparison.Ordinal);

            Assert.Equal("artifacts/live-diagnostics-trace-comparison.json", action.GetProperty("expectedArtifact").GetString());
            var expectedArtifacts = ReadStringArray(action, "expectedArtifacts");
            Assert.Contains("artifacts/live-diagnostics-trace-index.off-baseline.json", expectedArtifacts);
            Assert.Contains("artifacts/live-diagnostics-matrix-report.thetis-parity.json", expectedArtifacts);
            Assert.Contains("artifacts/live-diagnostics-history.json", expectedArtifacts);
            Assert.Contains("artifacts/live-diagnostics-trace-comparison.json", expectedArtifacts);
            Assert.Contains("artifacts/live-diagnostics-trace-comparison.thetis-parity.json", expectedArtifacts);
            Assert.Contains("artifact-manifest.json", expectedArtifacts);
            Assert.Contains("validation-report.json", expectedArtifacts);
            Assert.Contains("validation-triage-report.json", expectedArtifacts);
            Assert.Contains("validation-triage-report.md", expectedArtifacts);

            var markdown = await File.ReadAllTextAsync(summaryMarkdown);
            Assert.Contains("## Acceptance Command Steps", markdown, StringComparison.Ordinal);
            Assert.Contains("### capture-and-compare-live-matrix", markdown, StringComparison.Ordinal);
            Assert.Contains("5. `powershell -NoProfile -ExecutionPolicy Bypass -File tools\\summarize-dsp-live-diagnostics-history.ps1", markdown, StringComparison.Ordinal);
            Assert.Contains("6. `powershell -NoProfile -ExecutionPolicy Bypass -File tools\\compare-dsp-live-diagnostics-matrix.ps1", markdown, StringComparison.Ordinal);
            Assert.Contains("7. `powershell -NoProfile -ExecutionPolicy Bypass -File tools\\compare-dsp-live-diagnostics-matrix.ps1", markdown, StringComparison.Ordinal);
            Assert.Contains("8. `powershell -NoProfile -ExecutionPolicy Bypass -File tools\\new-dsp-artifact-manifest.ps1", markdown, StringComparison.Ordinal);
            Assert.Contains("9. `powershell -NoProfile -ExecutionPolicy Bypass -File tools\\validate-dsp-modernization-bundle.ps1", markdown, StringComparison.Ordinal);
            Assert.Contains("10. `powershell -NoProfile -ExecutionPolicy Bypass -File tools\\summarize-dsp-modernization-validation-report.ps1", markdown, StringComparison.Ordinal);
            Assert.Contains("-AcceptanceManifest -RequireLiveAcceptanceArtifacts", markdown, StringComparison.Ordinal);
            Assert.Contains("-RequireArtifactFiles", markdown, StringComparison.Ordinal);
            Assert.Contains("Expected Artifacts", markdown, StringComparison.Ordinal);
            Assert.Contains("artifact-manifest.json", markdown, StringComparison.Ordinal);
            Assert.Contains("validation-triage-report.md", markdown, StringComparison.Ordinal);

            var blockedBuildOutSummaryReport = Path.Combine(bundleDir, "validation-summary-buildout-blocked.json");
            var blockedBuildOutSummary = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "summarize-dsp-modernization-validation-report.ps1"),
                "-ValidationReportPath", validationReport,
                "-ReportPath", blockedBuildOutSummaryReport,
                "-NoMarkdown",
                "-JsonOnly",
                "-FailOnOptInDspBuildOutBlocked");

            Assert.NotEqual(0, blockedBuildOutSummary.ExitCode);
            Assert.True(File.Exists(blockedBuildOutSummaryReport), blockedBuildOutSummary.CombinedOutput);
            Assert.Contains("Opt-in DSP build-out prerequisites are blocked", blockedBuildOutSummary.StandardError, StringComparison.Ordinal);
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
    public async Task ValidationTriagePromotesBestMixedWeakStrongMatrixWindow()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell validation triage smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-dsp-mixed-weak-strong-action-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            var validationReport = Path.Combine(bundleDir, "validation-report.json");
            var validation = new
            {
                ok = false,
                errorCount = 1,
                warningCount = 0,
                errors = Array.Empty<object>(),
                warnings = Array.Empty<object>(),
                artifactReferencedFiles = Array.Empty<object>(),
                hardwareEvidenceStatus = "g2-hardware-evidence-ready",
                hardwareTarget = "G2",
                captureHardwareTarget = "G2",
                hardwareDiagnosticsPresent = true,
                liveMatrixMixedWeakStrongHuntReady = true,
                liveMatrixMixedWeakStrongStatus = "ready",
                liveMatrixMixedWeakStrongReportCount = 4,
                liveMatrixMixedWeakStrongSchemaV2ReportCount = 4,
                liveMatrixMixedWeakStrongReadyReportCount = 1,
                liveMatrixMixedWeakStrongTraceCount = 3,
                liveMatrixMixedWeakStrongReadyTraceCount = 1,
                liveMatrixMixedWeakStrongMissingRunCount = 1,
                liveMatrixMixedWeakStrongGapWatchRunCount = 1,
                liveMatrixMixedWeakStrongWeakInputSampleCount = 18,
                liveMatrixMixedWeakStrongStrongInputSampleCount = 7,
                liveMatrixMixedWeakStrongBestRun = new
                {
                    artifactId = "live-diagnostics-matrix-report-candidate",
                    artifactPath = "artifacts/live-diagnostics-matrix-report.candidate.json",
                    scenarioId = "mixed-ssb-speech",
                    comparisonId = "nr5-spnr",
                    reportPath = "artifacts/live-diagnostics-matrix-report.candidate.json",
                    readyForBenchmarkTrace = true,
                    weakInputSampleCount = 12,
                    strongInputSampleCount = 5,
                    weakStrongOutputGapDb = 3.25,
                    mixedWeakStrongEvidenceStatus = "ready",
                    mixedWeakStrongHuntScore = 58.5
                },
                liveDiagnosticsHistoryPresent = true,
                liveDiagnosticsHistoryReady = true,
                liveDiagnosticsHistoryTraceSourceStatus = "hash-ready",
                liveDiagnosticsHistoryTraceSourceCheckedCount = 1,
                liveDiagnosticsHistoryLiveExperimentCoverageStatus = "complete",
                liveDiagnosticsHistoryLiveExperimentCoverageMissingComparisonCount = 0,
                liveDiagnosticsHistoryLiveExperimentCoverageMissingComparisonIds = Array.Empty<string>(),
                liveDiagnosticsHistoryAgcStabilityStatus = "agc-stability-ready",
                liveDiagnosticsHistoryMixedWeakStrongEvidenceReady = false,
                liveDiagnosticsHistoryMixedWeakStrongEvidenceStatus = "missing-mixed-weak-strong",
                liveDiagnosticsHistoryMixedWeakStrongTraceCount = 0,
                liveDiagnosticsHistoryMixedWeakStrongReadyTraceCount = 0,
                liveDiagnosticsHistoryMixedWeakStrongMissingTraceCount = 1,
                liveDiagnosticsHistoryMixedWeakStrongGapWatchTraceCount = 0
            };
            await File.WriteAllTextAsync(validationReport, JsonSerializer.Serialize(validation, CamelCaseJson));

            var summaryReport = Path.Combine(bundleDir, "validation-summary.json");
            var summaryMarkdown = Path.Combine(bundleDir, "validation-summary.md");
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
            var action = summaryDoc.RootElement
                .GetProperty("acceptanceActionPlan")
                .EnumerateArray()
                .Single(item => item.GetProperty("actionId").GetString() == "promote-matrix-mixed-weak-strong-window");

            Assert.Equal("live-history-mixed-weak-strong", action.GetProperty("gateId").GetString());
            Assert.Equal("live-diagnostics", action.GetProperty("category").GetString());
            Assert.True(action.GetProperty("requiredForAcceptance").GetBoolean());
            Assert.True(action.GetProperty("blocksDefaultBehaviorChange").GetBoolean());

            var reason = action.GetProperty("reason").GetString() ?? "";
            Assert.Contains("mixed-ssb-speech", reason, StringComparison.Ordinal);
            Assert.Contains("nr5-spnr", reason, StringComparison.Ordinal);
            Assert.Contains("score=58.5", reason, StringComparison.Ordinal);
            Assert.Contains("weakStrongOutputGapDb=3.25", reason, StringComparison.Ordinal);

            var steps = ReadStringArray(action, "commandSteps");
            Assert.Equal(4, action.GetProperty("commandStepCount").GetInt32());
            Assert.Contains(steps, step => step.Contains("run-dsp-live-diagnostics-matrix.ps1", StringComparison.Ordinal)
                && step.Contains("-ScenarioIds mixed-ssb-speech", StringComparison.Ordinal)
                && step.Contains("-ComparisonId nr5-spnr", StringComparison.Ordinal)
                && step.Contains("live-diagnostics-matrix-report.mixed-weak-strong-followup.json", StringComparison.Ordinal));
            Assert.Contains(steps, step => step.Contains("summarize-dsp-live-diagnostics-history.ps1", StringComparison.Ordinal));
            Assert.Contains(steps, step => step.Contains("validate-dsp-modernization-bundle.ps1", StringComparison.Ordinal)
                && step.Contains("-RequireArtifactFiles", StringComparison.Ordinal));
            Assert.Contains(steps, step => step.Contains("summarize-dsp-modernization-validation-report.ps1", StringComparison.Ordinal)
                && step.Contains("-FailOnIssues", StringComparison.Ordinal));

            var followUp = action.GetProperty("followUp").GetString() ?? "";
            Assert.Contains("liveDiagnosticsHistoryMixedWeakStrongEvidenceReady=true", followUp, StringComparison.Ordinal);

            var expectedArtifacts = ReadStringArray(action, "expectedArtifacts");
            Assert.Contains("artifacts/live-diagnostics-trace-index.mixed-weak-strong-followup.json", expectedArtifacts);
            Assert.Contains("artifacts/live-diagnostics-matrix-report.mixed-weak-strong-followup.json", expectedArtifacts);
            Assert.Contains("artifacts/live-diagnostics-history.json", expectedArtifacts);

            var markdown = await File.ReadAllTextAsync(summaryMarkdown);
            Assert.Contains("### promote-matrix-mixed-weak-strong-window", markdown, StringComparison.Ordinal);
            Assert.Contains("mixed-ssb-speech", markdown, StringComparison.Ordinal);
            Assert.Contains("live-diagnostics-matrix-report.mixed-weak-strong-followup.json", markdown, StringComparison.Ordinal);
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
    public async Task ValidationTriageRecapturesArtifactReviewMatrixWindow()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell validation triage smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-dsp-matrix-artifact-action-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            var validationReport = Path.Combine(bundleDir, "validation-report.json");
            var validation = new
            {
                ok = false,
                errorCount = 0,
                warningCount = 1,
                errors = Array.Empty<object>(),
                warnings = Array.Empty<object>(),
                artifactReferencedFiles = Array.Empty<object>(),
                hardwareEvidenceStatus = "g2-hardware-evidence-ready",
                hardwareTarget = "G2",
                captureHardwareTarget = "G2",
                hardwareDiagnosticsPresent = true,
                liveMatrixMixedWeakStrongHuntReady = true,
                liveMatrixMixedWeakStrongStatus = "ready",
                liveMatrixMixedWeakStrongReportCount = 1,
                liveMatrixMixedWeakStrongSchemaV2ReportCount = 1,
                liveMatrixMixedWeakStrongReadyReportCount = 1,
                liveMatrixMixedWeakStrongTraceCount = 1,
                liveMatrixMixedWeakStrongReadyTraceCount = 1,
                liveMatrixMixedWeakStrongMissingRunCount = 0,
                liveMatrixMixedWeakStrongGapWatchRunCount = 0,
                liveMatrixMixedWeakStrongWeakInputSampleCount = 12,
                liveMatrixMixedWeakStrongStrongInputSampleCount = 5,
                liveMatrixMixedWeakStrongBestRun = new
                {
                    artifactId = "live-diagnostics-matrix-report-candidate",
                    artifactPath = "artifacts/live-diagnostics-matrix-report.candidate.json",
                    scenarioId = "mixed-ssb-speech",
                    comparisonId = "nr5-spnr",
                    reportPath = "artifacts/live-diagnostics-matrix-report.candidate.json",
                    readyForBenchmarkTrace = true,
                    weakInputSampleCount = 12,
                    strongInputSampleCount = 5,
                    weakStrongOutputGapDb = 3.25,
                    mixedWeakStrongEvidenceStatus = "ready",
                    mixedWeakStrongHuntScore = 58.5
                },
                liveMatrixArtifactControlStatus = "artifact-review",
                liveMatrixArtifactControlReportCount = 1,
                liveMatrixArtifactControlSchemaV3ReportCount = 1,
                liveMatrixArtifactControlReviewRunCount = 1,
                liveMatrixArtifactControlRiskScoreMax = 3.0,
                liveMatrixArtifactControlLowEvidenceLiftedSampleCount = 2,
                liveMatrixArtifactControlLowEvidenceLiftedPctMax = 66.7,
                liveMatrixArtifactControlAudioAlignmentMismatchPctMax = 12.5,
                liveMatrixArtifactControlStatusCounts = new object[] { new { name = "artifact-review", count = 1 } },
                liveDiagnosticsHistoryPresent = true,
                liveDiagnosticsHistoryReady = true,
                liveDiagnosticsHistoryTraceSourceStatus = "hash-ready",
                liveDiagnosticsHistoryTraceSourceCheckedCount = 1,
                liveDiagnosticsHistoryLiveExperimentCoverageStatus = "complete",
                liveDiagnosticsHistoryLiveExperimentCoverageMissingComparisonCount = 0,
                liveDiagnosticsHistoryLiveExperimentCoverageMissingComparisonIds = Array.Empty<string>(),
                liveDiagnosticsHistoryAgcStabilityStatus = "agc-stability-ready",
                liveDiagnosticsHistoryMixedWeakStrongEvidenceReady = true,
                liveDiagnosticsHistoryMixedWeakStrongEvidenceStatus = "ready",
                liveDiagnosticsHistoryMixedWeakStrongTraceCount = 1,
                liveDiagnosticsHistoryMixedWeakStrongReadyTraceCount = 1,
                liveDiagnosticsHistoryMixedWeakStrongMissingTraceCount = 0,
                liveDiagnosticsHistoryMixedWeakStrongGapWatchTraceCount = 0
            };
            await File.WriteAllTextAsync(validationReport, JsonSerializer.Serialize(validation, CamelCaseJson));

            var summaryReport = Path.Combine(bundleDir, "validation-summary.json");
            var summaryMarkdown = Path.Combine(bundleDir, "validation-summary.md");
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
            var action = summaryDoc.RootElement
                .GetProperty("acceptanceActionPlan")
                .EnumerateArray()
                .Single(item => item.GetProperty("actionId").GetString() == "recapture-matrix-artifact-control-window");

            Assert.Equal("live-matrix-artifact-control", action.GetProperty("gateId").GetString());
            Assert.Equal("live-diagnostics", action.GetProperty("category").GetString());
            Assert.False(action.GetProperty("requiredForAcceptance").GetBoolean());
            Assert.True(action.GetProperty("blocksDefaultBehaviorChange").GetBoolean());

            var reason = action.GetProperty("reason").GetString() ?? "";
            Assert.Contains("artifact-review", reason, StringComparison.Ordinal);
            Assert.Contains("reviewRuns=1", reason, StringComparison.Ordinal);
            Assert.Contains("riskScoreMax=3", reason, StringComparison.Ordinal);
            Assert.Contains("mixed-ssb-speech", reason, StringComparison.Ordinal);

            var steps = ReadStringArray(action, "commandSteps");
            Assert.Equal(3, action.GetProperty("commandStepCount").GetInt32());
            Assert.Contains(steps, step => step.Contains("run-dsp-live-diagnostics-matrix.ps1", StringComparison.Ordinal)
                && step.Contains("-ScenarioIds mixed-ssb-speech", StringComparison.Ordinal)
                && step.Contains("-ComparisonId nr5-spnr", StringComparison.Ordinal)
                && step.Contains("live-diagnostics-matrix-report.artifact-control-followup.json", StringComparison.Ordinal));
            Assert.Contains(steps, step => step.Contains("validate-dsp-modernization-bundle.ps1", StringComparison.Ordinal)
                && step.Contains("-RequireArtifactFiles", StringComparison.Ordinal));
            Assert.Contains(steps, step => step.Contains("summarize-dsp-modernization-validation-report.ps1", StringComparison.Ordinal)
                && step.Contains("-FailOnIssues", StringComparison.Ordinal));

            var followUp = action.GetProperty("followUp").GetString() ?? "";
            Assert.Contains("liveMatrixArtifactControlStatus is clear", followUp, StringComparison.Ordinal);

            var expectedArtifacts = ReadStringArray(action, "expectedArtifacts");
            Assert.Contains("artifacts/live-diagnostics-trace-index.artifact-control-followup.json", expectedArtifacts);
            Assert.Contains("artifacts/live-diagnostics-matrix-report.artifact-control-followup.json", expectedArtifacts);

            var artifactGate = summaryDoc.RootElement
                .GetProperty("evidenceGates")
                .EnumerateArray()
                .Single(gate => gate.GetProperty("gateId").GetString() == "live-matrix-artifact-control");
            Assert.False(artifactGate.GetProperty("ready").GetBoolean());
            Assert.False(artifactGate.GetProperty("requiredForAcceptance").GetBoolean());

            var markdown = await File.ReadAllTextAsync(summaryMarkdown);
            Assert.Contains("### recapture-matrix-artifact-control-window", markdown, StringComparison.Ordinal);
            Assert.Contains("artifact-control-followup", markdown, StringComparison.Ordinal);
            Assert.Contains("artifact-review", markdown, StringComparison.Ordinal);
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
    public async Task WatchLiveDiagnosticsClassifiesActiveAgcPumpingRisk()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell live diagnostics watcher smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-dsp-agc-stability-watch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            var jsonlPath = Path.Combine(bundleDir, "agc-active-pumping.jsonl");
            var samples = new object[]
            {
                AgcWatchSample(0, agcGainDb: 0.0, audioRmsDbfs: -33.0),
                AgcWatchSample(1, agcGainDb: 4.0, audioRmsDbfs: -31.0),
                AgcWatchSample(2, agcGainDb: 8.0, audioRmsDbfs: -32.0)
            };
            var jsonlOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            };
            await File.WriteAllTextAsync(
                jsonlPath,
                string.Join(Environment.NewLine, samples.Select(sample => JsonSerializer.Serialize(sample, jsonlOptions)))
                    + Environment.NewLine);

            var reportPath = Path.Combine(bundleDir, "agc-active-pumping.summary.json");
            var watch = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "watch-dsp-live-diagnostics.ps1"),
                "-InputPath", jsonlPath,
                "-ReportPath", reportPath,
                "-JsonOnly");

            Assert.Equal(0, watch.ExitCode);
            Assert.True(File.Exists(reportPath), watch.CombinedOutput);

            using var reportDoc = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
            var root = reportDoc.RootElement;
            Assert.Equal("agc-pumping-watch", root.GetProperty("trendStatus").GetString());

            var stability = root.GetProperty("agcStabilityWatch");
            Assert.Equal("active-pumping-risk", stability.GetProperty("status").GetString());
            Assert.True(stability.GetProperty("pumpingRisk").GetBoolean());
            Assert.Equal(8.0, stability.GetProperty("activeAgcGainDb").GetProperty("movement").GetDouble());
            Assert.Equal(3, stability.GetProperty("activeAudioSampleCount").GetInt32());

            Assert.Contains(
                root.GetProperty("recommendations").EnumerateArray(),
                recommendation => (recommendation.GetString() ?? "").Contains("AGC gain moved more than 6 dB", StringComparison.Ordinal));
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
    public async Task WatchLiveDiagnosticsTreatsNr5PostLevelerSpeechAsAligned()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell live diagnostics watcher smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-dsp-nr5-leveler-alignment-watch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            var jsonlPath = Path.Combine(bundleDir, "nr5-post-leveler-aligned.jsonl");
            await WriteAgcWatchJsonlAsync(
                jsonlPath,
                new[]
                {
                    Nr5LevelerAlignmentWatchSample(0, nr5InputDbfs: -31.6, nr5OutputDbfs: -22.6, levelerInputRmsDbfs: -45.5, levelerOutputRmsDbfs: -20.5),
                    Nr5LevelerAlignmentWatchSample(1, nr5InputDbfs: -33.0, nr5OutputDbfs: -20.9, levelerInputRmsDbfs: -44.0, levelerOutputRmsDbfs: -19.0),
                    Nr5LevelerAlignmentWatchSample(2, nr5InputDbfs: -34.4, nr5OutputDbfs: -22.9, levelerInputRmsDbfs: -45.9, levelerOutputRmsDbfs: -20.6)
                });

            var reportPath = Path.Combine(bundleDir, "nr5-post-leveler-aligned.summary.json");
            var watch = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "watch-dsp-live-diagnostics.ps1"),
                "-InputPath", jsonlPath,
                "-ReportPath", reportPath,
                "-JsonOnly");

            Assert.Equal(0, watch.ExitCode);
            Assert.True(File.Exists(reportPath), watch.CombinedOutput);

            using var reportDoc = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
            var root = reportDoc.RootElement;
            var alignment = root.GetProperty("nr5AudioAlignmentWatch");
            Assert.Equal(3, alignment.GetProperty("comparableSampleCount").GetInt32());
            Assert.Equal(0, alignment.GetProperty("mismatchSampleCount").GetInt32());
            Assert.Equal(3, alignment.GetProperty("alignedAfterLevelerSampleCount").GetInt32());
            Assert.True(alignment.GetProperty("nr5OutputToLevelerInputDeltaDb").GetProperty("min").GetDouble() < -20.0);
            Assert.True(Math.Abs(alignment.GetProperty("nr5OutputToLevelerOutputDeltaDb").GetProperty("max").GetDouble()) <= 3.0);
            var weakWatch = root.GetProperty("nr5WeakSignalWatch");
            Assert.Equal("missing-strong-input", weakWatch.GetProperty("mixedWeakStrongEvidenceStatus").GetString());
            Assert.False(weakWatch.GetProperty("mixedWeakStrongEvidenceReady").GetBoolean());
            Assert.False(weakWatch.GetProperty("weakStrongOutputParityReady").GetBoolean());
            Assert.Equal(0, weakWatch.GetProperty("strongInputSampleCount").GetInt32());
            Assert.Contains(
                root.GetProperty("recommendations").EnumerateArray(),
                recommendation => (recommendation.GetString() ?? "").Contains("no strong-input samples", StringComparison.Ordinal));

            Assert.Contains(
                root.GetProperty("recommendations").EnumerateArray(),
                recommendation => (recommendation.GetString() ?? "").Contains("intended RX leveler gain", StringComparison.Ordinal));
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
    public async Task CompareLiveDiagnosticsTraceFlagsAgcStabilityPumpingRegression()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell live diagnostics comparator smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-dsp-agc-stability-compare-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            var baselineJsonl = Path.Combine(bundleDir, "agc-stable-baseline.jsonl");
            await WriteAgcWatchJsonlAsync(
                baselineJsonl,
                new[]
                {
                    AgcWatchSample(0, agcGainDb: 0.0, audioRmsDbfs: -33.0),
                    AgcWatchSample(1, agcGainDb: 0.2, audioRmsDbfs: -32.5),
                    AgcWatchSample(2, agcGainDb: 0.4, audioRmsDbfs: -32.0)
                });

            var candidateJsonl = Path.Combine(bundleDir, "agc-pumping-candidate.jsonl");
            await WriteAgcWatchJsonlAsync(
                candidateJsonl,
                new[]
                {
                    AgcWatchSample(0, agcGainDb: 0.0, audioRmsDbfs: -33.0),
                    AgcWatchSample(1, agcGainDb: 4.0, audioRmsDbfs: -31.0),
                    AgcWatchSample(2, agcGainDb: 8.0, audioRmsDbfs: -32.0)
                });

            var baselineReport = Path.Combine(bundleDir, "agc-stable-baseline.summary.json");
            var baselineWatch = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "watch-dsp-live-diagnostics.ps1"),
                "-InputPath", baselineJsonl,
                "-ReportPath", baselineReport,
                "-JsonOnly");
            Assert.Equal(0, baselineWatch.ExitCode);

            var candidateReport = Path.Combine(bundleDir, "agc-pumping-candidate.summary.json");
            var candidateWatch = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "watch-dsp-live-diagnostics.ps1"),
                "-InputPath", candidateJsonl,
                "-ReportPath", candidateReport,
                "-JsonOnly");
            Assert.Equal(0, candidateWatch.ExitCode);

            var comparisonReport = Path.Combine(bundleDir, "agc-stability-comparison.json");
            var comparison = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "compare-dsp-live-diagnostics-traces.ps1"),
                "-BaselinePath", baselineReport,
                "-CandidatePath", candidateReport,
                "-ReportPath", comparisonReport,
                "-NoMarkdown",
                "-JsonOnly");

            Assert.True(comparison.ExitCode == 0, comparison.CombinedOutput);
            Assert.True(File.Exists(comparisonReport), comparison.CombinedOutput);

            using var comparisonDoc = JsonDocument.Parse(await File.ReadAllTextAsync(comparisonReport));
            var root = comparisonDoc.RootElement;
            Assert.False(root.GetProperty("readyForReview").GetBoolean());
            Assert.True(root.GetProperty("regressionCount").GetInt32() >= 2);

            var metrics = root.GetProperty("metricComparisons").EnumerateArray().ToArray();
            AssertMetricRegression(metrics, "agcActiveGainMovementDb", "pumping");
            AssertMetricRegression(metrics, "agcPumpingRisk", "pumping");
            AssertMetricRegression(metrics, "traceStatusSeverity", "hard-gate");
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
    public async Task CompareLiveDiagnosticsTraceFlagsNr5ArtifactControlRegression()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell live diagnostics comparator smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-dsp-artifact-control-compare-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            var baselineReport = Path.Combine(bundleDir, "artifact-clear-baseline.summary.json");
            await File.WriteAllTextAsync(
                baselineReport,
                JsonSerializer.Serialize(ArtifactControlSummary(0.72, 0.04, 0, 0.0, 0.0), CamelCaseJson));

            var candidateReport = Path.Combine(bundleDir, "artifact-review-candidate.summary.json");
            await File.WriteAllTextAsync(
                candidateReport,
                JsonSerializer.Serialize(ArtifactControlSummary(0.05, 0.82, 2, 66.7, 12.5), CamelCaseJson));

            var comparisonReport = Path.Combine(bundleDir, "artifact-control-comparison.json");
            var comparison = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "compare-dsp-live-diagnostics-traces.ps1"),
                "-BaselinePath", baselineReport,
                "-CandidatePath", candidateReport,
                "-ReportPath", comparisonReport,
                "-NoMarkdown",
                "-JsonOnly");

            Assert.True(comparison.ExitCode == 0, comparison.CombinedOutput);
            Assert.True(File.Exists(comparisonReport), comparison.CombinedOutput);

            using var comparisonDoc = JsonDocument.Parse(await File.ReadAllTextAsync(comparisonReport));
            var root = comparisonDoc.RootElement;
            Assert.False(root.GetProperty("readyForReview").GetBoolean());

            var metrics = root.GetProperty("metricComparisons").EnumerateArray().ToArray();
            AssertMetricRegression(metrics, "nr5LowEvidenceLiftedPct", "artifact-control");
            AssertMetricRegression(metrics, "nr5AudioAlignmentMismatchPct", "artifact-control");
            AssertMetricRegression(metrics, "nr5ArtifactRiskScore", "artifact-control");

            var weakSignal = root.GetProperty("nr5WeakSignalComparison");
            Assert.Equal("artifact-clear", weakSignal.GetProperty("baselineArtifactRiskStatus").GetString());
            Assert.Equal("artifact-review", weakSignal.GetProperty("candidateArtifactRiskStatus").GetString());
            Assert.Equal(2, weakSignal.GetProperty("candidateLowEvidenceLiftedSampleCount").GetInt32());
            Assert.Equal(66.7, weakSignal.GetProperty("candidateLowEvidenceLiftedPct").GetDouble(), precision: 3);
            Assert.Equal(12.5, weakSignal.GetProperty("candidateAudioAlignmentMismatchPct").GetDouble(), precision: 3);
            Assert.True(weakSignal.GetProperty("candidateArtifactRiskScore").GetDouble() >= 4.0);
            Assert.True(weakSignal.GetProperty("artifactRiskRegression").GetBoolean());
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
    public async Task CompareLiveDiagnosticsMatrixAggregatesNr5ArtifactControlRegression()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell matrix comparator smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-dsp-artifact-control-matrix-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            var baselineSummary = Path.Combine(bundleDir, "artifact-clear-baseline.summary.json");
            await File.WriteAllTextAsync(
                baselineSummary,
                JsonSerializer.Serialize(ArtifactControlSummary(0.72, 0.04, 0, 0.0, 0.0), CamelCaseJson));

            var candidateSummary = Path.Combine(bundleDir, "artifact-review-candidate.summary.json");
            await File.WriteAllTextAsync(
                candidateSummary,
                JsonSerializer.Serialize(ArtifactControlSummary(0.05, 0.82, 2, 66.7, 12.5), CamelCaseJson));

            var baselineIndex = Path.Combine(bundleDir, "baseline-index.json");
            await File.WriteAllTextAsync(
                baselineIndex,
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 3,
                    files = new object[]
                    {
                        new
                        {
                            path = "artifact-clear-baseline.summary.json",
                            summaryPath = "artifact-clear-baseline.summary.json",
                            scenarioId = "weak-cw-carrier",
                            comparisonId = "current-zeus",
                            captureReadinessStatus = "ready",
                            hardGatePass = true,
                            strictPreflightPass = true
                        }
                    }
                }, CamelCaseJson));

            var candidateIndex = Path.Combine(bundleDir, "candidate-index.json");
            await File.WriteAllTextAsync(
                candidateIndex,
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 3,
                    files = new object[]
                    {
                        new
                        {
                            path = "artifact-review-candidate.summary.json",
                            summaryPath = "artifact-review-candidate.summary.json",
                            scenarioId = "weak-cw-carrier",
                            comparisonId = "nr5-spnr",
                            captureReadinessStatus = "ready",
                            hardGatePass = true,
                            strictPreflightPass = true
                        }
                    }
                }, CamelCaseJson));

            var matrixReport = Path.Combine(bundleDir, "artifact-control-matrix-comparison.json");
            var matrixOutputDir = Path.Combine(bundleDir, "matrix-comparisons");
            var comparison = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "compare-dsp-live-diagnostics-matrix.ps1"),
                "-BaselineIndexPath", baselineIndex,
                "-CandidateIndexPath", candidateIndex,
                "-BaselineComparisonId", "current-zeus",
                "-CandidateComparisonId", "nr5-spnr",
                "-ReportPath", matrixReport,
                "-OutputDir", matrixOutputDir,
                "-NoMarkdown",
                "-JsonOnly");

            Assert.True(comparison.ExitCode == 0, comparison.CombinedOutput);
            Assert.True(File.Exists(matrixReport), comparison.CombinedOutput);

            using var matrixDoc = JsonDocument.Parse(await File.ReadAllTextAsync(matrixReport));
            var root = matrixDoc.RootElement;
            Assert.False(root.GetProperty("readyForReview").GetBoolean());

            var summary = root.GetProperty("nr5WeakSignalComparisonSummary");
            Assert.Equal(2, summary.GetProperty("candidateLowEvidenceLiftedSampleCount").GetInt32());
            Assert.Equal(66.7, summary.GetProperty("candidateLowEvidenceLiftedPctMax").GetDouble(), precision: 3);
            Assert.Equal(12.5, summary.GetProperty("candidateAudioAlignmentMismatchPctMax").GetDouble(), precision: 3);
            Assert.True(summary.GetProperty("candidateArtifactRiskScoreMax").GetDouble() >= 4.0);
            Assert.True(summary.GetProperty("artifactRiskRegression").GetBoolean());
            Assert.Equal(1, summary.GetProperty("artifactRiskRegressionCount").GetInt32());

            var regressions = root.GetProperty("metricRegressionDetails").EnumerateArray().ToArray();
            Assert.Contains(
                regressions,
                item => item.GetProperty("metricId").GetString() == "nr5ArtifactRiskScore"
                    && item.GetProperty("safetyClass").GetString() == "artifact-control");
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
    public async Task LiveDiagnosticsHistoryCarriesAgcStabilityPumpingSignals()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell live history smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-dsp-agc-stability-history-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            WriteSourcePlanScopeBundle(bundleDir);

            var baselineDir = Path.Combine(bundleDir, "20260616T000000000Z-agc-stable-baseline");
            Directory.CreateDirectory(baselineDir);
            var baselineJsonl = Path.Combine(baselineDir, "live-diagnostics-watch.jsonl");
            await WriteAgcWatchJsonlAsync(
                baselineJsonl,
                new[]
                {
                    AgcWatchSample(0, agcGainDb: 0.0, audioRmsDbfs: -32.0, includeNr5: true, nr5InputDbfs: -34.0),
                    AgcWatchSample(1, agcGainDb: 0.2, audioRmsDbfs: -29.0, includeNr5: true, nr5InputDbfs: -20.0),
                    AgcWatchSample(2, agcGainDb: 0.4, audioRmsDbfs: -32.5, includeNr5: true, nr5InputDbfs: -35.0)
                });

            var baselineReport = Path.Combine(baselineDir, "live-diagnostics-watch.json");
            var baselineWatch = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "watch-dsp-live-diagnostics.ps1"),
                "-InputPath", baselineJsonl,
                "-ScenarioId", "weak-cw-carrier",
                "-ComparisonId", "nr5-spnr",
                "-Label", "agc-stable-baseline",
                "-ReportPath", baselineReport,
                "-JsonOnly");
            Assert.Equal(0, baselineWatch.ExitCode);

            var candidateDir = Path.Combine(bundleDir, "20260616T000100000Z-agc-pumping-candidate");
            Directory.CreateDirectory(candidateDir);
            var candidateJsonl = Path.Combine(candidateDir, "live-diagnostics-watch.jsonl");
            await WriteAgcWatchJsonlAsync(
                candidateJsonl,
                new[]
                {
                    AgcWatchSample(0, agcGainDb: 0.0, audioRmsDbfs: -33.0, includeNr5: true, nr5InputDbfs: -34.0),
                    AgcWatchSample(1, agcGainDb: 4.0, audioRmsDbfs: -31.0, includeNr5: true, nr5InputDbfs: -20.0),
                    AgcWatchSample(2, agcGainDb: 8.0, audioRmsDbfs: -32.0, includeNr5: true, nr5InputDbfs: -35.0)
                });

            var candidateReport = Path.Combine(candidateDir, "live-diagnostics-watch.json");
            var candidateWatch = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "watch-dsp-live-diagnostics.ps1"),
                "-InputPath", candidateJsonl,
                "-ScenarioId", "weak-cw-carrier",
                "-ComparisonId", "nr5-spnr",
                "-Label", "agc-pumping-candidate",
                "-ReportPath", candidateReport,
                "-JsonOnly");
            Assert.Equal(0, candidateWatch.ExitCode);

            var artifactsDir = Path.Combine(bundleDir, "artifacts");
            Directory.CreateDirectory(artifactsDir);
            var historyReport = Path.Combine(artifactsDir, "live-diagnostics-history.json");
            var history = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "summarize-dsp-live-diagnostics-history.ps1"),
                "-BundleDir", bundleDir,
                "-ReportPath", historyReport,
                "-NoMarkdown",
                "-JsonOnly");

            Assert.Equal(0, history.ExitCode);
            Assert.True(File.Exists(historyReport), history.CombinedOutput);

            using var historyDoc = JsonDocument.Parse(await File.ReadAllTextAsync(historyReport));
            var root = historyDoc.RootElement;
            Assert.Equal(15, root.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(2, root.GetProperty("traceCount").GetInt32());
            Assert.Equal(0, root.GetProperty("artifactControlSignalCount").GetInt32());
            Assert.True(root.GetProperty("mixedWeakStrongEvidenceReady").GetBoolean());
            Assert.Equal("ready", root.GetProperty("mixedWeakStrongEvidenceStatus").GetString());
            Assert.Equal(2, root.GetProperty("mixedWeakStrongTraceCount").GetInt32());
            Assert.Equal(2, root.GetProperty("mixedWeakStrongReadyTraceCount").GetInt32());
            Assert.Equal(0, root.GetProperty("mixedWeakStrongMissingTraceCount").GetInt32());

            var latest = root.GetProperty("latestTrace");
            Assert.Contains("agc-pumping-candidate", latest.GetProperty("path").GetString(), StringComparison.Ordinal);
            Assert.Equal("active-pumping-risk", latest.GetProperty("agcStabilityStatus").GetString());
            Assert.True(latest.GetProperty("agcPumpingRisk").GetBoolean());
            Assert.Equal(8.0, latest.GetProperty("agcActiveGainMovementDb").GetDouble(), precision: 3);
            Assert.Equal(1, latest.GetProperty("strongInputSampleCount").GetInt32());
            Assert.True(latest.GetProperty("mixedWeakStrongEvidenceReady").GetBoolean());
            Assert.True(latest.GetProperty("weakStrongOutputParityReady").GetBoolean());
            Assert.Equal("ready", latest.GetProperty("mixedWeakStrongEvidenceStatus").GetString());
            Assert.Equal("weak-and-pumping-watch", latest.GetProperty("reviewStatus").GetString());

            var latestFullTrace = root.GetProperty("traces")
                .EnumerateArray()
                .Single(trace => (trace.GetProperty("path").GetString() ?? "").Contains("agc-pumping-candidate", StringComparison.Ordinal));
            AssertTraceHasSafetySignal(latestFullTrace, "agc-active-gain-movement-db", "pumping");
            AssertTraceHasSafetySignal(latestFullTrace, "agc-pumping-risk", "pumping");

            var lowestPumping = root.GetProperty("lowestPumpingTrace");
            Assert.Contains("agc-stable-baseline", lowestPumping.GetProperty("path").GetString(), StringComparison.Ordinal);

            var promotion = root.GetProperty("promotionDecision");
            Assert.Equal("blocked-weak-and-pumping", promotion.GetProperty("status").GetString());

            var delta = root.GetProperty("latestVsPreviousNr5Delta");
            Assert.Equal(1.0, delta.GetProperty("agcPumpingRisk").GetDouble(), precision: 3);
            Assert.Equal(7.6, delta.GetProperty("agcActiveGainMovementDb").GetDouble(), precision: 3);

            var artifactManifestPath = Path.Combine(bundleDir, "artifact-manifest.json");
            WriteLiveHistoryOnlyArtifactManifest(artifactManifestPath);

            var validationReport = Path.Combine(bundleDir, "validation-report.json");
            var validation = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "validate-dsp-modernization-bundle.ps1"),
                "-BundleDir", bundleDir,
                "-ArtifactManifestPath", artifactManifestPath,
                "-ReportPath", validationReport,
                "-AllowPreflight",
                "-JsonOnly");

            Assert.NotEqual(0, validation.ExitCode);
            Assert.True(File.Exists(validationReport), validation.CombinedOutput);

            using (var validationDoc = JsonDocument.Parse(await File.ReadAllTextAsync(validationReport)))
            {
                var validationRoot = validationDoc.RootElement;
                Assert.Equal("agc-stability-ready", validationRoot.GetProperty("liveDiagnosticsHistoryAgcStabilityStatus").GetString());
                Assert.Equal(2, validationRoot.GetProperty("liveDiagnosticsHistoryAgcStabilityTraceCount").GetInt32());
                Assert.Equal(0, validationRoot.GetProperty("liveDiagnosticsHistoryAgcStabilityMissingTraceCount").GetInt32());
                Assert.Equal(1, validationRoot.GetProperty("liveDiagnosticsHistoryAgcPumpingRiskTraceCount").GetInt32());
                Assert.True(validationRoot.GetProperty("liveDiagnosticsHistoryAgcActivePumpingSignalCount").GetInt32() >= 1);
                Assert.Equal(0, validationRoot.GetProperty("liveDiagnosticsHistoryArtifactControlSignalCount").GetInt32());
                Assert.True(validationRoot.GetProperty("liveDiagnosticsHistoryMixedWeakStrongEvidenceReady").GetBoolean());
                Assert.Equal("ready", validationRoot.GetProperty("liveDiagnosticsHistoryMixedWeakStrongEvidenceStatus").GetString());
                Assert.Equal(2, validationRoot.GetProperty("liveDiagnosticsHistoryMixedWeakStrongTraceCount").GetInt32());
                Assert.Equal(2, validationRoot.GetProperty("liveDiagnosticsHistoryMixedWeakStrongReadyTraceCount").GetInt32());
                Assert.Equal(0, validationRoot.GetProperty("liveDiagnosticsHistoryMixedWeakStrongMissingTraceCount").GetInt32());
            }

            var staleNode = JsonNode.Parse(await File.ReadAllTextAsync(historyReport))?.AsObject()
                ?? throw new InvalidOperationException("Could not parse generated live diagnostics history.");
            var firstTrace = staleNode["traces"]?.AsArray().FirstOrDefault()?.AsObject()
                ?? throw new InvalidOperationException("Generated live diagnostics history did not include trace records.");
            firstTrace.Remove("agcStabilityStatus");
            firstTrace.Remove("agcPumpingRisk");
            await File.WriteAllTextAsync(historyReport, staleNode.ToJsonString(CamelCaseJson));

            var staleValidationReport = Path.Combine(bundleDir, "validation-report-stale-agc.json");
            var staleValidation = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "validate-dsp-modernization-bundle.ps1"),
                "-BundleDir", bundleDir,
                "-ArtifactManifestPath", artifactManifestPath,
                "-ReportPath", staleValidationReport,
                "-AllowPreflight",
                "-JsonOnly");

            Assert.NotEqual(0, staleValidation.ExitCode);
            using (var staleValidationDoc = JsonDocument.Parse(await File.ReadAllTextAsync(staleValidationReport)))
            {
                var staleRoot = staleValidationDoc.RootElement;
                Assert.Equal("agc-stability-missing", staleRoot.GetProperty("liveDiagnosticsHistoryAgcStabilityStatus").GetString());
                Assert.Equal(1, staleRoot.GetProperty("liveDiagnosticsHistoryAgcStabilityMissingTraceCount").GetInt32());

                var errorCodes = staleRoot.GetProperty("errors")
                    .EnumerateArray()
                    .Select(error => error.GetProperty("code").GetString())
                    .ToArray();
                Assert.Contains("live-history-agc-stability-missing", errorCodes);
            }
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
    public async Task LiveDiagnosticsHistoryReportsArtifactControlAdvisorySignal()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell live history smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-dsp-artifact-control-history-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            WriteSourcePlanScopeBundle(bundleDir);

            var traceDir = Path.Combine(bundleDir, "20260616T000200000Z-nr5-low-evidence-lift");
            Directory.CreateDirectory(traceDir);
            var jsonlPath = Path.Combine(traceDir, "live-diagnostics-watch.jsonl");
            await WriteAgcWatchJsonlAsync(
                jsonlPath,
                Enumerable.Range(0, 6).Select(index => AgcWatchSample(
                    index,
                    agcGainDb: 0.0,
                    audioRmsDbfs: -18.5,
                    includeNr5: true,
                    nr5InputDbfs: -36.0,
                    signalConfidence: 0.12,
                    agcGate: 0.10,
                    signalProbability: 0.05,
                    textureFill: 0.82,
                    nr5OutputDbfs: -18.5)));

            var watchReport = Path.Combine(traceDir, "live-diagnostics-watch.json");
            var watch = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "watch-dsp-live-diagnostics.ps1"),
                "-InputPath", jsonlPath,
                "-ScenarioId", "ssb-like-speech",
                "-ComparisonId", "nr5-spnr",
                "-Label", "nr5-low-evidence-lift",
                "-ReportPath", watchReport,
                "-JsonOnly");
            Assert.Equal(0, watch.ExitCode);

            var artifactsDir = Path.Combine(bundleDir, "artifacts");
            Directory.CreateDirectory(artifactsDir);
            var historyReport = Path.Combine(artifactsDir, "live-diagnostics-history.json");
            var history = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "summarize-dsp-live-diagnostics-history.ps1"),
                "-BundleDir", bundleDir,
                "-ReportPath", historyReport,
                "-NoMarkdown",
                "-JsonOnly");

            Assert.Equal(0, history.ExitCode);
            using var historyDoc = JsonDocument.Parse(await File.ReadAllTextAsync(historyReport));
            var root = historyDoc.RootElement;
            Assert.Equal(15, root.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(1, root.GetProperty("artifactControlSignalCount").GetInt32());

            var latest = root.GetProperty("latestTrace");
            Assert.Equal("artifact-review", latest.GetProperty("nr5ArtifactRiskStatus").GetString());
            Assert.Equal(6, latest.GetProperty("nr5LowEvidenceLiftedSampleCount").GetInt32());
            Assert.True(latest.GetProperty("nr5ArtifactRiskScore").GetDouble() >= 2.0);
            var latestFullTrace = root.GetProperty("traces")
                .EnumerateArray()
                .Single(trace => (trace.GetProperty("path").GetString() ?? "").Contains("nr5-low-evidence-lift", StringComparison.Ordinal));
            AssertTraceHasSafetySignal(latestFullTrace, "nr5-speech-artifact-risk-score", "artifact-control");

            var promotion = root.GetProperty("promotionDecision");
            Assert.DoesNotContain("artifact-control", ReadStringArray(promotion, "blockerClasses"));

            var artifactManifestPath = Path.Combine(bundleDir, "artifact-manifest.json");
            WriteLiveHistoryOnlyArtifactManifest(artifactManifestPath);

            var validationReport = Path.Combine(bundleDir, "validation-report.json");
            var validation = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "validate-dsp-modernization-bundle.ps1"),
                "-BundleDir", bundleDir,
                "-ArtifactManifestPath", artifactManifestPath,
                "-ReportPath", validationReport,
                "-AllowPreflight",
                "-JsonOnly");

            Assert.NotEqual(0, validation.ExitCode);
            using var validationDoc = JsonDocument.Parse(await File.ReadAllTextAsync(validationReport));
            var validationRoot = validationDoc.RootElement;
            Assert.Equal(1, validationRoot.GetProperty("liveDiagnosticsHistoryArtifactControlSignalCount").GetInt32());
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
    public async Task ValidationTriageOptInBuildOutFailGateCanPassBeforeAcceptanceGates()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell validation triage smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-dsp-buildout-readiness-triage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            var validationReport = Path.Combine(bundleDir, "validation-buildout-ready.json");
            WriteSyntheticOptInBuildOutReadyValidationReport(validationReport);

            var summaryReport = Path.Combine(bundleDir, "summary-buildout-ready.json");
            var summary = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "summarize-dsp-modernization-validation-report.ps1"),
                "-ValidationReportPath", validationReport,
                "-ReportPath", summaryReport,
                "-NoMarkdown",
                "-JsonOnly",
                "-FailOnOptInDspBuildOutBlocked");

            Assert.Equal(0, summary.ExitCode);
            Assert.True(File.Exists(summaryReport), summary.CombinedOutput);

            using (var summaryDoc = JsonDocument.Parse(await File.ReadAllTextAsync(summaryReport)))
            {
                var root = summaryDoc.RootElement;
                Assert.NotEqual("ready", root.GetProperty("status").GetString());
                Assert.True(root.GetProperty("optInDspBuildOutReady").GetBoolean());
                Assert.Equal("ready-for-opt-in-buildout", root.GetProperty("optInDspBuildOutStatus").GetString());
                Assert.Empty(ReadStringArray(root, "optInDspBuildOutBlockingGateIds"));
                Assert.False(root.GetProperty("g2FirstPassAcceptanceReady").GetBoolean());
                Assert.False(root.GetProperty("candidateComparisonReady").GetBoolean());
                Assert.False(root.GetProperty("defaultBehaviorChangeReady").GetBoolean());
            }

            var strictSummaryReport = Path.Combine(bundleDir, "summary-strict.json");
            var strictSummary = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "summarize-dsp-modernization-validation-report.ps1"),
                "-ValidationReportPath", validationReport,
                "-ReportPath", strictSummaryReport,
                "-NoMarkdown",
                "-JsonOnly",
                "-FailOnIssues");

            Assert.NotEqual(0, strictSummary.ExitCode);
            Assert.True(File.Exists(strictSummaryReport), strictSummary.CombinedOutput);
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
    public async Task LiveDiagnosticsMatrixPlanOnlyIncludesAcceptanceCycle()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell plan-only smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var plan = await RunPowerShellAsync(
            powerShell,
            repoRoot,
            Path.Combine(repoRoot, "tools", "run-dsp-live-diagnostics-matrix.ps1"),
            "-PlanOnly");

        Assert.Equal(0, plan.ExitCode);

        using var planDoc = JsonDocument.Parse(plan.StandardOutput);
        var root = planDoc.RootElement;
        Assert.Equal(3, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("run-dsp-live-diagnostics-matrix", root.GetProperty("tool").GetString());
        Assert.Equal("plan-only", root.GetProperty("mode").GetString());
        Assert.Equal(10, root.GetProperty("acceptanceCommandStepCount").GetInt32());

        var steps = ReadStringArray(root, "acceptanceCommandSteps");
        Assert.Equal(10, steps.Length);
        Assert.Contains(steps, step => step.Contains("-ComparisonId off-baseline", StringComparison.Ordinal));
        Assert.Contains(steps, step => step.Contains("-ComparisonId thetis-parity", StringComparison.Ordinal));
        Assert.Contains(steps, step => step.Contains("-ComparisonId current-zeus", StringComparison.Ordinal));
        Assert.Contains(steps, step => step.Contains("-ComparisonId nr5-spnr", StringComparison.Ordinal));
        Assert.Contains(steps, step => step.Contains("compare-dsp-live-diagnostics-matrix.ps1", StringComparison.Ordinal)
            && step.Contains("-BaselineComparisonId current-zeus", StringComparison.Ordinal)
            && step.Contains("-CandidateComparisonId nr5-spnr", StringComparison.Ordinal));
        Assert.Contains(steps, step => step.Contains("compare-dsp-live-diagnostics-matrix.ps1", StringComparison.Ordinal)
            && step.Contains("-BaselineComparisonId thetis-parity", StringComparison.Ordinal)
            && step.Contains("-CandidateComparisonId nr5-spnr", StringComparison.Ordinal)
            && step.Contains("live-diagnostics-trace-comparison.thetis-parity.json", StringComparison.Ordinal));
        Assert.Contains(steps, step => step.Contains("new-dsp-artifact-manifest.ps1", StringComparison.Ordinal)
            && step.Contains("-AcceptanceManifest", StringComparison.Ordinal)
            && step.Contains("-RequireLiveAcceptanceArtifacts", StringComparison.Ordinal));
        Assert.Contains(steps, step => step.Contains("validate-dsp-modernization-bundle.ps1", StringComparison.Ordinal)
            && step.Contains("-RequireArtifactFiles", StringComparison.Ordinal));
        Assert.Contains(steps, step => step.Contains("summarize-dsp-modernization-validation-report.ps1", StringComparison.Ordinal)
            && step.Contains("validation-triage-report.md", StringComparison.Ordinal)
            && step.Contains("-FailOnIssues", StringComparison.Ordinal));

        var expectedArtifacts = ReadStringArray(root, "acceptanceExpectedArtifacts");
        Assert.Contains("artifacts/live-diagnostics-trace-index.off-baseline.json", expectedArtifacts);
        Assert.Contains("artifacts/live-diagnostics-matrix-report.thetis-parity.json", expectedArtifacts);
        Assert.Contains("artifacts/live-diagnostics-history.json", expectedArtifacts);
        Assert.Contains("artifacts/live-diagnostics-trace-comparison.json", expectedArtifacts);
        Assert.Contains("artifacts/live-diagnostics-trace-comparison.thetis-parity.json", expectedArtifacts);
        Assert.Contains("artifact-manifest.json", expectedArtifacts);
        Assert.Contains("validation-report.json", expectedArtifacts);
        Assert.Contains("validation-triage-report.md", expectedArtifacts);

        var outputs = ReadStringArray(root, "outputs");
        Assert.Contains(outputs, output => output.Contains("mixed weak/strong hunt scoring", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(outputs, output => output.Contains("speech-artifact advisory", StringComparison.OrdinalIgnoreCase));
    }

    [SkippableFact]
    public async Task LiveDiagnosticsMatrixScoresMixedWeakStrongHuntWindows()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell matrix smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-dsp-mixed-weak-strong-matrix-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            var fakeWatchPath = Path.Combine(bundleDir, "fake-watch.ps1");
            await File.WriteAllTextAsync(
                fakeWatchPath,
                """
                param(
                    [string]$BaseUrl,
                    [int]$Samples,
                    [int]$IntervalMs,
                    [int]$TimeoutSec,
                    [string]$Label,
                    [string]$ScenarioId,
                    [string]$ComparisonId,
                    [string]$ReportPath,
                    [string]$JsonlPath,
                    [switch]$JsonOnly,
                    [switch]$SkipCertificateCheck
                )
                $ErrorActionPreference = "Stop"
                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $ReportPath) | Out-Null
                Set-Content -LiteralPath $JsonlPath -Value "{}" -Encoding UTF8
                $isMixed = [string]::Equals($ScenarioId, "mixed-ssb-speech", [StringComparison]::OrdinalIgnoreCase)
                $strong = if ($isMixed) { 2 } else { 0 }
                $gap = if ($isMixed) { 3.0 } else { $null }
                $status = if ($isMixed) { "ready" } else { "missing-strong-input" }
                $artifactReview = $isMixed
                $signalProbabilityAverage = if ($artifactReview) { 0.05 } else { 0.68 }
                $textureFillAverage = if ($artifactReview) { 0.82 } else { 0.04 }
                $audioAlignmentMismatchPct = if ($artifactReview) { 12.5 } else { 0.0 }
                $lowEvidenceLiftedSampleCount = if ($artifactReview) { 2 } else { 0 }
                $lowEvidenceLiftedPct = if ($artifactReview) { 66.7 } else { 0.0 }
                $lowEvidenceAlignmentMismatchPct = if ($artifactReview) { 33.3 } else { 0.0 }
                $report = [ordered]@{
                    schemaVersion = 1
                    tool = "fake-watch"
                    readyForBenchmarkTrace = $true
                    trendStatus = "ready"
                    okSampleCount = $Samples
                    failedSampleCount = 0
                    hardBlockerSampleCount = 0
                    captureReadinessWatch = [ordered]@{
                        status = "ready"
                        hardGatePass = $true
                        strictPreflightPass = $true
                        topConstraint = $null
                        topHardConstraint = $null
                        topStatus = [ordered]@{ name = "ready"; count = $Samples }
                    }
                    nr5WeakSignalWatch = [ordered]@{
                        weakInputSampleCount = 3
                        weakRecoveredSampleCount = 3
                        weakDropoutSampleCount = 0
                        hotMakeupSampleCount = 0
                        strongInputSampleCount = $strong
                        weakStrongOutputGapDb = $gap
                        mixedWeakStrongEvidenceReady = $isMixed
                        weakStrongOutputParityReady = $isMixed
                        mixedWeakStrongEvidenceStatus = $status
                    }
                    nr5SignalProbability = [ordered]@{
                        average = $signalProbabilityAverage
                    }
                    nr5TextureFill = [ordered]@{
                        average = $textureFillAverage
                    }
                    nr5AudioAlignmentWatch = [ordered]@{
                        mismatchPct = $audioAlignmentMismatchPct
                    }
                    nr5LowEvidenceLiftWatch = [ordered]@{
                        liftedSampleCount = $lowEvidenceLiftedSampleCount
                        liftedPct = $lowEvidenceLiftedPct
                        alignmentMismatchPct = $lowEvidenceAlignmentMismatchPct
                    }
                    comparisonStateReadiness = [ordered]@{
                        strict = $true
                        ready = $true
                        status = "ready"
                        nextAction = ""
                    }
                    nr5SampleCount = $Samples
                    nr5AlignedSampleCount = $Samples
                    nr5AgcDiagnosticSampleCount = $Samples
                    nr5ProbabilityDiagnosticSampleCount = $Samples
                    nr5PeakDiagnosticSampleCount = $Samples
                    nr5RequestedSampleCount = $Samples
                    nr5EffectiveSampleCount = $Samples
                    nrOffRequestedSampleCount = 0
                    nrOffEffectiveSampleCount = 0
                    nrModeMismatchSampleCount = 0
                }
                $json = $report | ConvertTo-Json -Depth 32
                Set-Content -LiteralPath $ReportPath -Value $json -Encoding UTF8
                if ($JsonOnly) { $json }
                """);

            var matrixReportPath = Path.Combine(bundleDir, "matrix-report.json");
            var matrixIndexPath = Path.Combine(bundleDir, "matrix-index.json");
            var matrix = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "run-dsp-live-diagnostics-matrix.ps1"),
                "-WatchScriptPath", fakeWatchPath,
                "-ScenarioIds", "weak-ssb-speech,mixed-ssb-speech",
                "-ComparisonId", "nr5-spnr",
                "-Samples", "3",
                "-IntervalMs", "0",
                "-OutputRoot", bundleDir,
                "-ReportPath", matrixReportPath,
                "-IndexPath", matrixIndexPath,
                "-JsonOnly");

            Assert.True(matrix.ExitCode == 0, matrix.CombinedOutput);
            Assert.True(File.Exists(matrixReportPath), matrix.CombinedOutput);
            Assert.True(File.Exists(matrixIndexPath), matrix.CombinedOutput);

            using var matrixDoc = JsonDocument.Parse(await File.ReadAllTextAsync(matrixReportPath));
            var root = matrixDoc.RootElement;
            Assert.Equal(3, root.GetProperty("schemaVersion").GetInt32());
            Assert.True(root.GetProperty("nr5MixedWeakStrongHuntReady").GetBoolean());
            Assert.Equal(6, root.GetProperty("nr5WeakInputSampleCount").GetInt32());
            Assert.Equal(2, root.GetProperty("nr5StrongInputSampleCount").GetInt32());
            Assert.Equal(1, root.GetProperty("nr5MixedWeakStrongTraceCount").GetInt32());
            Assert.Equal(1, root.GetProperty("nr5MixedWeakStrongReadyTraceCount").GetInt32());
            Assert.Equal(1, root.GetProperty("nr5MixedWeakStrongMissingRunCount").GetInt32());
            Assert.Equal(1, root.GetProperty("nr5ArtifactReviewRunCount").GetInt32());
            Assert.True(root.GetProperty("nr5ArtifactRiskScoreMax").GetDouble() >= 3.0);
            Assert.Equal(2, root.GetProperty("nr5LowEvidenceLiftedSampleCount").GetInt32());
            Assert.Equal(66.7, root.GetProperty("nr5LowEvidenceLiftedPctMax").GetDouble(), precision: 3);
            Assert.Equal(12.5, root.GetProperty("nr5AudioAlignmentMismatchPctMax").GetDouble(), precision: 3);

            var best = root.GetProperty("bestMixedWeakStrongRun");
            Assert.Equal("mixed-ssb-speech", best.GetProperty("scenarioId").GetString());
            Assert.Equal("nr5-spnr", best.GetProperty("comparisonId").GetString());
            Assert.Equal("ready", best.GetProperty("nr5MixedWeakStrongEvidenceStatus").GetString());
            Assert.Equal(3.0, best.GetProperty("nr5WeakStrongOutputGapDb").GetDouble(), precision: 3);
            Assert.True(best.GetProperty("nr5MixedWeakStrongHuntScore").GetDouble() > 70.0);
            Assert.Equal("artifact-review", best.GetProperty("nr5ArtifactRiskStatus").GetString());
            Assert.True(best.GetProperty("nr5ArtifactRiskScore").GetDouble() >= 3.0);

            var runs = root.GetProperty("runs").EnumerateArray().ToArray();
            var weakRun = runs.Single(run => run.GetProperty("scenarioId").GetString() == "weak-ssb-speech");
            Assert.Equal("missing-strong-input", weakRun.GetProperty("nr5MixedWeakStrongEvidenceStatus").GetString());
            Assert.False(weakRun.GetProperty("nr5MixedWeakStrongEvidenceReady").GetBoolean());

            var readyStatusCount = root.GetProperty("nr5MixedWeakStrongStatusCounts")
                .EnumerateArray()
                .Single(entry => entry.GetProperty("name").GetString() == "ready");
            Assert.Equal(1, readyStatusCount.GetProperty("count").GetInt32());

            Assert.Contains(
                root.GetProperty("recommendations").EnumerateArray(),
                recommendation => (recommendation.GetString() ?? "").Contains("Best mixed weak+strong matrix run", StringComparison.Ordinal));
            Assert.Contains(
                root.GetProperty("recommendations").EnumerateArray(),
                recommendation => (recommendation.GetString() ?? "").Contains("artifact-control advisories", StringComparison.Ordinal));

            using var indexDoc = JsonDocument.Parse(await File.ReadAllTextAsync(matrixIndexPath));
            var indexRoot = indexDoc.RootElement;
            Assert.Equal(3, indexRoot.GetProperty("schemaVersion").GetInt32());
            Assert.True(indexRoot.GetProperty("nr5MixedWeakStrongHuntReady").GetBoolean());
            Assert.Equal("mixed-ssb-speech", indexRoot.GetProperty("bestMixedWeakStrongRun").GetProperty("scenarioId").GetString());
            Assert.Equal(1, indexRoot.GetProperty("nr5ArtifactReviewRunCount").GetInt32());
            Assert.True(indexRoot.GetProperty("nr5ArtifactRiskScoreMax").GetDouble() >= 3.0);
            Assert.Contains(
                indexRoot.GetProperty("files").EnumerateArray(),
                file => file.GetProperty("nr5MixedWeakStrongHuntScore").GetDouble() > 70.0);
            Assert.Contains(
                indexRoot.GetProperty("files").EnumerateArray(),
                file => file.GetProperty("nr5ArtifactRiskStatus").GetString() == "artifact-review");

            WriteSourcePlanScopeBundle(bundleDir);
            var manifestPath = Path.Combine(bundleDir, "artifact-manifest.json");
            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(
                    new
                    {
                        schemaVersion = 1,
                        artifacts = new object[]
                        {
                            new
                            {
                                id = "live-diagnostics-matrix-report-candidate",
                                kind = "diagnostics-matrix-json",
                                source = "tools/run-dsp-live-diagnostics-matrix.ps1",
                                path = Path.GetFileName(matrixReportPath),
                                required = false,
                                comparisonIds = new[] { "nr5-spnr" }
                            }
                        }
                    },
                    CamelCaseJson));

            var validationReport = Path.Combine(bundleDir, "validation-report.json");
            var validation = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "validate-dsp-modernization-bundle.ps1"),
                "-BundleDir", bundleDir,
                "-ArtifactManifestPath", manifestPath,
                "-ReportPath", validationReport,
                "-AllowPreflight",
                "-JsonOnly");

            Assert.True(File.Exists(validationReport), validation.CombinedOutput);
            using var validationDoc = JsonDocument.Parse(await File.ReadAllTextAsync(validationReport));
            var validationRoot = validationDoc.RootElement;
            Assert.True(validationRoot.GetProperty("liveMatrixMixedWeakStrongHuntReady").GetBoolean());
            Assert.Equal("ready", validationRoot.GetProperty("liveMatrixMixedWeakStrongStatus").GetString());
            Assert.Equal(1, validationRoot.GetProperty("liveMatrixMixedWeakStrongReportCount").GetInt32());
            Assert.Equal(1, validationRoot.GetProperty("liveMatrixMixedWeakStrongSchemaV2ReportCount").GetInt32());
            Assert.Equal(1, validationRoot.GetProperty("liveMatrixMixedWeakStrongReadyReportCount").GetInt32());
            Assert.Equal(1, validationRoot.GetProperty("liveMatrixMixedWeakStrongTraceCount").GetInt32());
            Assert.Equal(1, validationRoot.GetProperty("liveMatrixMixedWeakStrongReadyTraceCount").GetInt32());
            Assert.Equal(1, validationRoot.GetProperty("liveMatrixMixedWeakStrongMissingRunCount").GetInt32());
            Assert.Equal(6, validationRoot.GetProperty("liveMatrixMixedWeakStrongWeakInputSampleCount").GetInt32());
            Assert.Equal(2, validationRoot.GetProperty("liveMatrixMixedWeakStrongStrongInputSampleCount").GetInt32());
            Assert.Equal("mixed-ssb-speech", validationRoot.GetProperty("liveMatrixMixedWeakStrongBestRun").GetProperty("scenarioId").GetString());
            Assert.Equal("artifact-review", validationRoot.GetProperty("liveMatrixArtifactControlStatus").GetString());
            Assert.Equal(1, validationRoot.GetProperty("liveMatrixArtifactControlReportCount").GetInt32());
            Assert.Equal(1, validationRoot.GetProperty("liveMatrixArtifactControlSchemaV3ReportCount").GetInt32());
            Assert.Equal(1, validationRoot.GetProperty("liveMatrixArtifactControlReviewRunCount").GetInt32());
            Assert.True(validationRoot.GetProperty("liveMatrixArtifactControlRiskScoreMax").GetDouble() >= 3.0);
            Assert.Equal(2, validationRoot.GetProperty("liveMatrixArtifactControlLowEvidenceLiftedSampleCount").GetInt32());

            var summaryReport = Path.Combine(bundleDir, "validation-summary.json");
            var summaryMarkdown = Path.Combine(bundleDir, "validation-summary.md");
            var summary = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "summarize-dsp-modernization-validation-report.ps1"),
                "-ValidationReportPath", validationReport,
                "-ReportPath", summaryReport,
                "-MarkdownPath", summaryMarkdown,
                "-JsonOnly");

            Assert.Equal(0, summary.ExitCode);
            using var summaryDoc = JsonDocument.Parse(await File.ReadAllTextAsync(summaryReport));
            var summaryRoot = summaryDoc.RootElement;
            Assert.True(summaryRoot.GetProperty("liveMatrixMixedWeakStrongHuntReady").GetBoolean());
            Assert.Equal("ready", summaryRoot.GetProperty("liveMatrixMixedWeakStrongStatus").GetString());
            Assert.Equal("artifact-review", summaryRoot.GetProperty("liveMatrixArtifactControlStatus").GetString());
            Assert.Equal(1, summaryRoot.GetProperty("liveMatrixArtifactControlReviewRunCount").GetInt32());
            var huntGate = summaryRoot.GetProperty("evidenceGates")
                .EnumerateArray()
                .Single(gate => gate.GetProperty("gateId").GetString() == "live-matrix-mixed-weak-strong-hunt");
            Assert.True(huntGate.GetProperty("ready").GetBoolean());
            Assert.False(huntGate.GetProperty("requiredForAcceptance").GetBoolean());
            Assert.Contains("best=mixed-ssb-speech/nr5-spnr", huntGate.GetProperty("detail").GetString(), StringComparison.Ordinal);
            var artifactGate = summaryRoot.GetProperty("evidenceGates")
                .EnumerateArray()
                .Single(gate => gate.GetProperty("gateId").GetString() == "live-matrix-artifact-control");
            Assert.False(artifactGate.GetProperty("ready").GetBoolean());
            Assert.False(artifactGate.GetProperty("requiredForAcceptance").GetBoolean());
            Assert.Contains("reviewRuns=1", artifactGate.GetProperty("detail").GetString(), StringComparison.Ordinal);

            var markdown = await File.ReadAllTextAsync(summaryMarkdown);
            Assert.Contains("## Live Matrix Mixed Weak/Strong Hunt", markdown, StringComparison.Ordinal);
            Assert.Contains("## Live Matrix Artifact-Control Advisory", markdown, StringComparison.Ordinal);
            Assert.Contains("artifact-review", markdown, StringComparison.Ordinal);
            Assert.Contains("mixed-ssb-speech", markdown, StringComparison.Ordinal);
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
    public async Task LiveAcceptanceCyclePlanOnlyIncludesExecutableAcceptanceRecipe()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell plan-only smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var plan = await RunPowerShellAsync(
            powerShell,
            repoRoot,
            Path.Combine(repoRoot, "tools", "run-dsp-live-acceptance-cycle.ps1"),
            "-PlanOnly",
            "-BundleDir", "captures\\dsp-modernization\\g2-live",
            "-ScenarioIds", "weak-cw-carrier",
            "-Samples", "12",
            "-IntervalMs", "250");

        Assert.Equal(0, plan.ExitCode);

        using var planDoc = JsonDocument.Parse(plan.StandardOutput);
        var root = planDoc.RootElement;
        Assert.Equal(7, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("run-dsp-live-acceptance-cycle", root.GetProperty("tool").GetString());
        Assert.Equal("plan-only", root.GetProperty("mode").GetString());
        Assert.Equal("captures\\dsp-modernization\\g2-live", root.GetProperty("bundleDir").GetString());
        Assert.Equal(new[] { "weak-cw-carrier" }, ReadStringArray(root, "scenarioIds"));
        Assert.Equal(10, root.GetProperty("acceptanceCommandStepCount").GetInt32());

        var steps = ReadStringArray(root, "acceptanceCommandSteps");
        Assert.Equal(10, steps.Length);
        Assert.Contains(steps, step => step.Contains("-ScenarioIds weak-cw-carrier", StringComparison.Ordinal));
        Assert.Contains(steps, step => step.Contains("-ComparisonId off-baseline", StringComparison.Ordinal));
        Assert.Contains(steps, step => step.Contains("-ComparisonId thetis-parity", StringComparison.Ordinal));
        Assert.Contains(steps, step => step.Contains("-ComparisonId current-zeus", StringComparison.Ordinal));
        Assert.Contains(steps, step => step.Contains("-ComparisonId nr5-spnr", StringComparison.Ordinal));
        Assert.Contains(steps, step => step.Contains("compare-dsp-live-diagnostics-matrix.ps1", StringComparison.Ordinal)
            && step.Contains("-BaselineComparisonId current-zeus", StringComparison.Ordinal)
            && step.Contains("-CandidateComparisonId nr5-spnr", StringComparison.Ordinal));
        Assert.Contains(steps, step => step.Contains("compare-dsp-live-diagnostics-matrix.ps1", StringComparison.Ordinal)
            && step.Contains("-BaselineComparisonId thetis-parity", StringComparison.Ordinal)
            && step.Contains("-CandidateComparisonId nr5-spnr", StringComparison.Ordinal)
            && step.Contains("live-diagnostics-trace-comparison.thetis-parity.json", StringComparison.Ordinal));
        Assert.Contains(steps, step => step.Contains("new-dsp-artifact-manifest.ps1", StringComparison.Ordinal)
            && step.Contains("-RequireLiveAcceptanceArtifacts", StringComparison.Ordinal));
        Assert.Contains(steps, step => step.Contains("validate-dsp-modernization-bundle.ps1", StringComparison.Ordinal)
            && step.Contains("-RequireArtifactFiles", StringComparison.Ordinal));
        Assert.Contains(steps, step => step.Contains("summarize-dsp-modernization-validation-report.ps1", StringComparison.Ordinal)
            && step.Contains("validation-triage-report.md", StringComparison.Ordinal));

        var expectedArtifacts = ReadStringArray(root, "acceptanceExpectedArtifacts");
        Assert.Contains("artifacts/live-diagnostics-trace-index.off-baseline.json", expectedArtifacts);
        Assert.Contains("artifacts/live-diagnostics-matrix-report.candidate.json", expectedArtifacts);
        Assert.Contains("artifacts/live-diagnostics-history.json", expectedArtifacts);
        Assert.Contains("artifacts/live-diagnostics-trace-comparison.json", expectedArtifacts);
        Assert.Contains("artifacts/live-diagnostics-trace-comparison.thetis-parity.json", expectedArtifacts);
        Assert.Contains("artifact-manifest.json", expectedArtifacts);
        Assert.Contains("validation-report.json", expectedArtifacts);
        Assert.Contains("validation-triage-report.md", expectedArtifacts);
        Assert.Contains("artifacts/live-acceptance-cycle-summary.json", expectedArtifacts);

        var requiredGates = ReadStringArray(root, "requiredEvidenceGates");
        Assert.Contains("g2-hardware", requiredGates);
        Assert.Contains("live-matrix-captures", requiredGates);
        Assert.Contains("thetis-parity-live-comparison", requiredGates);
        Assert.Contains("live-trace-metric-catalog-alignment", requiredGates);
        Assert.Contains("live-history-agc-stability", requiredGates);
        Assert.Contains("live-history-mixed-weak-strong", requiredGates);
        Assert.Contains("strict-bundle-validation", requiredGates);

        var advisorySignals = ReadStringArray(root, "advisoryEvidenceSignals");
        Assert.Contains("live-matrix-mixed-weak-strong-hunt", advisorySignals);
        Assert.Contains("live-matrix-artifact-control", advisorySignals);
        Assert.Contains("live-history-artifact-control", advisorySignals);

        var notes = ReadStringArray(root, "notes");
        Assert.Contains(notes, note => note.Contains("No DSP runtime behavior", StringComparison.Ordinal));
        Assert.Contains(notes, note => note.Contains("G2 hardware evidence", StringComparison.Ordinal));
        Assert.Contains(notes, note => note.Contains("after the wrapper writes its summary", StringComparison.Ordinal));
        Assert.Contains(notes, note => note.Contains("Matrix child runs use -ContinueOnError", StringComparison.Ordinal));
        Assert.Contains(notes, note => note.Contains("mixed weak/strong matrix hunt", StringComparison.Ordinal));
        Assert.Contains(notes, note => note.Contains("primaryAcceptance", StringComparison.Ordinal));
        Assert.Contains(notes, note => note.Contains("ArtifactControlSignalCount", StringComparison.Ordinal));
        Assert.Contains(notes, note => note.Contains("liveMatrixArtifactControl", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task LiveAcceptanceCycleSummaryArtifactRejectsNonPortablePaths()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell validator smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-dsp-live-acceptance-summary-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            WriteSourcePlanScopeBundle(bundleDir);

            var manifestPath = Path.Combine(bundleDir, "artifact-manifest.json");
            var scaffold = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "new-dsp-artifact-manifest.ps1"),
                "-BundleDir", bundleDir,
                "-OutputPath", manifestPath,
                "-IncludeOptionalArtifacts",
                "-Force");

            Assert.Equal(0, scaffold.ExitCode);

            var artifactsDir = Path.Combine(bundleDir, "artifacts");
            Directory.CreateDirectory(artifactsDir);
            var absoluteComparisonPath = Path.Combine(bundleDir, "artifacts", "live-diagnostics-trace-comparison.json");
            var summaryPath = Path.Combine(artifactsDir, "live-acceptance-cycle-summary.json");
            var summary = new
            {
                schemaVersion = 7,
                tool = "run-dsp-live-acceptance-cycle",
                acceptanceCommandStepCount = 10,
                acceptanceExpectedArtifacts = new[]
                {
                    "artifacts/live-diagnostics-trace-index.off-baseline.json",
                    "artifacts/live-diagnostics-matrix-report.thetis-parity.json",
                    "artifacts/live-diagnostics-history.json",
                    "artifacts/live-diagnostics-trace-comparison.json",
                    "artifacts/live-diagnostics-trace-comparison.thetis-parity.json",
                    "artifact-manifest.json",
                    "validation-report.json",
                    "validation-triage-report.md",
                    "artifacts/live-acceptance-cycle-summary.json"
                },
                matrixReportPaths = new[]
                {
                    "artifacts/live-diagnostics-matrix-report.off-baseline.json",
                    "artifacts/live-diagnostics-matrix-report.thetis-parity.json",
                    "artifacts/live-diagnostics-matrix-report.baseline.json",
                    "artifacts/live-diagnostics-matrix-report.candidate.json"
                },
                historyReportPath = "artifacts/live-diagnostics-history.json",
                comparisonReportPath = absoluteComparisonPath,
                thetisComparisonReportPath = "artifacts/live-diagnostics-trace-comparison.thetis-parity.json",
                artifactManifestPath = "artifact-manifest.json",
                validationReportPath = "validation-report.json",
                triageReportPath = "validation-triage-report.json",
                triageMarkdownPath = "validation-triage-report.md",
                matrixAcceptanceReady = false,
                comparisonReadyForReview = false,
                comparisonMetricCatalogAlignmentReady = false,
                comparisonMetricCatalogAlignmentStatus = "not-evaluated",
                comparisonMetricDefinitionCount = 0,
                comparisonMetricCatalogMissingMetricCount = 0,
                comparisonMetricCatalogMismatchCount = 0,
                thetisComparisonReadyForReview = false,
                thetisComparisonRegressionCount = 0,
                thetisComparisonGateFailureCount = 0,
                thetisComparisonMetricCatalogAlignmentReady = false,
                thetisComparisonMetricCatalogAlignmentStatus = "not-evaluated",
                thetisComparisonMetricDefinitionCount = 0,
                thetisComparisonMetricCatalogMissingMetricCount = 0,
                thetisComparisonMetricCatalogMismatchCount = 0,
                validationOk = false,
                hardwareEvidenceReady = false,
                hardwareEvidenceStatus = "diagnostics-missing",
                hardwareTarget = "G2",
                captureHardwareTarget = "G2",
                hardwareDiagnosticsPresent = false,
                liveDiagnosticsHistoryAgcStabilityReady = false,
                liveDiagnosticsHistoryAgcStabilityStatus = "no-traces",
                liveDiagnosticsHistoryAgcStabilityTraceCount = 0,
                liveDiagnosticsHistoryAgcStabilityMissingTraceCount = 0,
                liveDiagnosticsHistoryAgcPumpingRiskTraceCount = 0,
                liveDiagnosticsHistoryAgcActivePumpingSignalCount = 0,
                liveDiagnosticsHistoryAgcVoiceLikePumpingSignalCount = 0,
                liveDiagnosticsHistoryArtifactControlSignalCount = 0,
                liveDiagnosticsHistoryMixedWeakStrongEvidenceReady = false,
                liveDiagnosticsHistoryMixedWeakStrongEvidenceStatus = "no-nr5-history",
                liveDiagnosticsHistoryMixedWeakStrongTraceCount = 0,
                liveDiagnosticsHistoryMixedWeakStrongReadyTraceCount = 0,
                liveDiagnosticsHistoryMixedWeakStrongMissingTraceCount = 0,
                liveDiagnosticsHistoryMixedWeakStrongGapWatchTraceCount = 0,
                liveMatrixMixedWeakStrongHuntReady = false,
                liveMatrixMixedWeakStrongStatus = "missing",
                liveMatrixMixedWeakStrongReportCount = 0,
                liveMatrixMixedWeakStrongSchemaV2ReportCount = 0,
                liveMatrixMixedWeakStrongReadyReportCount = 0,
                liveMatrixMixedWeakStrongTraceCount = 0,
                liveMatrixMixedWeakStrongReadyTraceCount = 0,
                liveMatrixMixedWeakStrongMissingRunCount = 0,
                liveMatrixMixedWeakStrongGapWatchRunCount = 0,
                liveMatrixMixedWeakStrongWeakInputSampleCount = 0,
                liveMatrixMixedWeakStrongStrongInputSampleCount = 0,
                liveMatrixMixedWeakStrongStatusCounts = Array.Empty<object>(),
                liveMatrixMixedWeakStrongBestRun = (object?)null,
                liveMatrixArtifactControlStatus = "not-present",
                liveMatrixArtifactControlReportCount = 0,
                liveMatrixArtifactControlSchemaV3ReportCount = 0,
                liveMatrixArtifactControlReviewRunCount = 0,
                liveMatrixArtifactControlRiskScoreMax = 0.0,
                liveMatrixArtifactControlLowEvidenceLiftedSampleCount = 0,
                liveMatrixArtifactControlLowEvidenceLiftedPctMax = (double?)null,
                liveMatrixArtifactControlAudioAlignmentMismatchPctMax = (double?)null,
                liveMatrixArtifactControlStatusCounts = Array.Empty<object>(),
                triageAcceptanceActionPlanCount = 1,
                triageAcceptanceRequiredActionCount = 1,
                triageAcceptanceManualActionCount = 0,
                triageAcceptanceActionCategoryCounts = new object[] { new { category = "live-diagnostics", count = 1 } },
                triagePrimaryAcceptanceActionId = "promote-matrix-mixed-weak-strong-window",
                triagePrimaryAcceptanceActionPriority = 78,
                triagePrimaryAcceptanceActionStageId = "opt-in-candidate-comparison",
                triagePrimaryAcceptanceActionGateId = "live-history-mixed-weak-strong",
                triagePrimaryAcceptanceActionCategory = "live-diagnostics",
                triagePrimaryAcceptanceActionRequired = true,
                triagePrimaryAcceptanceActionManual = false,
                triagePrimaryAcceptanceCommandTemplate = "powershell -NoProfile -ExecutionPolicy Bypass -File tools\\run-dsp-live-diagnostics-matrix.ps1 -BundleDir \"$bundleDir\" -ComparisonId nr5-spnr",
                triagePrimaryAcceptanceCommandStepCount = 1,
                triagePrimaryAcceptanceCommandSteps = new[]
                {
                    "powershell -NoProfile -ExecutionPolicy Bypass -File tools\\run-dsp-live-diagnostics-matrix.ps1 -BundleDir \"$bundleDir\" -ComparisonId nr5-spnr"
                },
                triagePrimaryAcceptanceManualAction = "",
                triagePrimaryAcceptanceExpectedArtifact = "artifacts/live-diagnostics-history.json",
                triagePrimaryAcceptanceExpectedArtifactCount = 1,
                triagePrimaryAcceptanceExpectedArtifacts = new[] { "artifacts/live-diagnostics-history.json" },
                triagePrimaryAcceptanceFollowUp = "Rerun strict validation after promotion.",
                requiredLiveAcceptanceArtifactProblemCount = 1,
                liveAcceptanceEvidenceReady = false
            };
            await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(summary, CamelCaseJson));

            var validationReport = Path.Combine(bundleDir, "validation-report.json");
            var validation = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "validate-dsp-modernization-bundle.ps1"),
                "-BundleDir", bundleDir,
                "-ArtifactManifestPath", manifestPath,
                "-ReportPath", validationReport,
                "-AllowPreflight",
                "-JsonOnly");

            Assert.NotEqual(0, validation.ExitCode);
            Assert.True(File.Exists(validationReport), validation.CombinedOutput);

            using var validationDoc = JsonDocument.Parse(await File.ReadAllTextAsync(validationReport));
            var validationRoot = validationDoc.RootElement;
            Assert.True(validationRoot.GetProperty("liveAcceptanceCycleSummaryPresent").GetBoolean());
            Assert.False(validationRoot.GetProperty("liveAcceptanceCycleSummaryValid").GetBoolean());
            Assert.Equal("invalid", validationRoot.GetProperty("liveAcceptanceCycleSummaryStatus").GetString());
            Assert.False(validationRoot.GetProperty("liveAcceptanceCycleEvidenceReady").GetBoolean());
            Assert.False(validationRoot.GetProperty("liveAcceptanceCycleHardwareEvidenceReady").GetBoolean());
            Assert.Equal("diagnostics-missing", validationRoot.GetProperty("liveAcceptanceCycleHardwareEvidenceStatus").GetString());
            Assert.Equal("G2", validationRoot.GetProperty("liveAcceptanceCycleHardwareTarget").GetString());
            Assert.Equal("G2", validationRoot.GetProperty("liveAcceptanceCycleCaptureHardwareTarget").GetString());
            Assert.False(validationRoot.GetProperty("liveAcceptanceCycleHardwareDiagnosticsPresent").GetBoolean());
            Assert.False(validationRoot.GetProperty("liveAcceptanceCycleLiveDiagnosticsHistoryAgcStabilityReady").GetBoolean());
            Assert.Equal("no-traces", validationRoot.GetProperty("liveAcceptanceCycleLiveDiagnosticsHistoryAgcStabilityStatus").GetString());
            Assert.Equal(0, validationRoot.GetProperty("liveAcceptanceCycleLiveDiagnosticsHistoryAgcStabilityMissingTraceCount").GetInt32());
            Assert.Equal(0, validationRoot.GetProperty("liveAcceptanceCycleLiveDiagnosticsHistoryArtifactControlSignalCount").GetInt32());
            Assert.False(validationRoot.GetProperty("liveAcceptanceCycleLiveDiagnosticsHistoryMixedWeakStrongEvidenceReady").GetBoolean());
            Assert.Equal("no-nr5-history", validationRoot.GetProperty("liveAcceptanceCycleLiveDiagnosticsHistoryMixedWeakStrongEvidenceStatus").GetString());
            Assert.False(validationRoot.GetProperty("liveAcceptanceCycleLiveMatrixMixedWeakStrongHuntReady").GetBoolean());
            Assert.Equal("missing", validationRoot.GetProperty("liveAcceptanceCycleLiveMatrixMixedWeakStrongStatus").GetString());
            Assert.Equal(0, validationRoot.GetProperty("liveAcceptanceCycleLiveMatrixMixedWeakStrongReportCount").GetInt32());
            Assert.Equal("not-present", validationRoot.GetProperty("liveAcceptanceCycleLiveMatrixArtifactControlStatus").GetString());
            Assert.Equal(0, validationRoot.GetProperty("liveAcceptanceCycleLiveMatrixArtifactControlReportCount").GetInt32());
            Assert.Equal(0, validationRoot.GetProperty("liveAcceptanceCycleLiveMatrixArtifactControlReviewRunCount").GetInt32());
            Assert.Equal(0.0, validationRoot.GetProperty("liveAcceptanceCycleLiveMatrixArtifactControlRiskScoreMax").GetDouble(), precision: 3);
            Assert.Equal("promote-matrix-mixed-weak-strong-window", validationRoot.GetProperty("liveAcceptanceCycleTriagePrimaryAcceptanceActionId").GetString());
            Assert.Equal("live-diagnostics", validationRoot.GetProperty("liveAcceptanceCycleTriagePrimaryAcceptanceActionCategory").GetString());
            Assert.Equal(1, validationRoot.GetProperty("liveAcceptanceCycleRequiredLiveAcceptanceArtifactProblemCount").GetInt32());
            Assert.Equal("artifacts/live-acceptance-cycle-summary.json", validationRoot.GetProperty("liveAcceptanceCycleSummaryPath").GetString());

            var warningCodes = validationDoc.RootElement
                .GetProperty("warnings")
                .EnumerateArray()
                .Select(warning => warning.GetProperty("code").GetString())
                .ToArray();

            Assert.Contains("live-acceptance-cycle-summary-path-absolute", warningCodes);
            var summaryArtifact = validationDoc.RootElement
                .GetProperty("artifactFiles")
                .EnumerateArray()
                .Single(artifact => artifact.GetProperty("id").GetString() == "live-acceptance-cycle-summary");
            Assert.False(summaryArtifact.GetProperty("required").GetBoolean());
            Assert.False(summaryArtifact.GetProperty("ok").GetBoolean());

            var summaryReport = Path.Combine(bundleDir, "validation-summary.json");
            var summaryMarkdown = Path.Combine(bundleDir, "validation-summary.md");
            var triage = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "summarize-dsp-modernization-validation-report.ps1"),
                "-ValidationReportPath", validationReport,
                "-ReportPath", summaryReport,
                "-MarkdownPath", summaryMarkdown,
                "-JsonOnly");

            Assert.Equal(0, triage.ExitCode);
            Assert.True(File.Exists(summaryReport), triage.CombinedOutput);
            Assert.True(File.Exists(summaryMarkdown), triage.CombinedOutput);

            using var summaryDoc = JsonDocument.Parse(await File.ReadAllTextAsync(summaryReport));
            var summaryRoot = summaryDoc.RootElement;
            Assert.True(summaryRoot.GetProperty("liveAcceptanceCycleSummaryPresent").GetBoolean());
            Assert.False(summaryRoot.GetProperty("liveAcceptanceCycleSummaryValid").GetBoolean());
            Assert.Equal("invalid", summaryRoot.GetProperty("liveAcceptanceCycleSummaryStatus").GetString());
            Assert.False(summaryRoot.GetProperty("liveAcceptanceCycleEvidenceReady").GetBoolean());
            Assert.False(summaryRoot.GetProperty("liveAcceptanceCycleHardwareEvidenceReady").GetBoolean());
            Assert.Equal("diagnostics-missing", summaryRoot.GetProperty("liveAcceptanceCycleHardwareEvidenceStatus").GetString());
            Assert.False(summaryRoot.GetProperty("liveAcceptanceCycleLiveDiagnosticsHistoryAgcStabilityReady").GetBoolean());
            Assert.Equal("no-traces", summaryRoot.GetProperty("liveAcceptanceCycleLiveDiagnosticsHistoryAgcStabilityStatus").GetString());
            Assert.Equal(0, summaryRoot.GetProperty("liveAcceptanceCycleLiveDiagnosticsHistoryArtifactControlSignalCount").GetInt32());
            Assert.False(summaryRoot.GetProperty("liveAcceptanceCycleLiveDiagnosticsHistoryMixedWeakStrongEvidenceReady").GetBoolean());
            Assert.Equal("no-nr5-history", summaryRoot.GetProperty("liveAcceptanceCycleLiveDiagnosticsHistoryMixedWeakStrongEvidenceStatus").GetString());
            Assert.False(summaryRoot.GetProperty("liveAcceptanceCycleLiveMatrixMixedWeakStrongHuntReady").GetBoolean());
            Assert.Equal("missing", summaryRoot.GetProperty("liveAcceptanceCycleLiveMatrixMixedWeakStrongStatus").GetString());
            Assert.Equal("not-present", summaryRoot.GetProperty("liveAcceptanceCycleLiveMatrixArtifactControlStatus").GetString());
            Assert.Equal(0, summaryRoot.GetProperty("liveAcceptanceCycleLiveMatrixArtifactControlReviewRunCount").GetInt32());
            Assert.Equal("promote-matrix-mixed-weak-strong-window", summaryRoot.GetProperty("liveAcceptanceCycleTriagePrimaryAcceptanceActionId").GetString());

            var markdown = await File.ReadAllTextAsync(summaryMarkdown);
            Assert.Contains("## Live Acceptance Cycle Summary", markdown, StringComparison.Ordinal);
            Assert.Contains("Summary status: invalid", markdown, StringComparison.Ordinal);
            Assert.Contains("G2 hardware ready/status/target/capture target/diagnostics", markdown, StringComparison.Ordinal);
            Assert.Contains("Live history AGC stability ready/status/traces/missing/pumping", markdown, StringComparison.Ordinal);
            Assert.Contains("Live history artifact-control advisory signals", markdown, StringComparison.Ordinal);
            Assert.Contains("Live history mixed weak/strong ready/status/traces/ready/missing/gap-watch", markdown, StringComparison.Ordinal);
            Assert.Contains("Live matrix mixed weak/strong hunt ready/status", markdown, StringComparison.Ordinal);
            Assert.Contains("Live matrix artifact-control advisory status", markdown, StringComparison.Ordinal);
            Assert.Contains("Triage primary action", markdown, StringComparison.Ordinal);
            Assert.Contains("promote-matrix-mixed-weak-strong-window", markdown, StringComparison.Ordinal);
            Assert.Contains("Triage primary command/manual action", markdown, StringComparison.Ordinal);
            Assert.Contains("-ComparisonId nr5-spnr", markdown, StringComparison.Ordinal);
            Assert.Contains("Triage primary follow-up", markdown, StringComparison.Ordinal);
            Assert.Contains("diagnostics-missing", markdown, StringComparison.Ordinal);
            Assert.Contains("artifacts/live-acceptance-cycle-summary.json", markdown, StringComparison.Ordinal);
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
    public async Task LiveAcceptanceCycleSummaryRejectsG2HardwareReadinessMismatch()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell validator smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-dsp-live-acceptance-hardware-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            WriteSourcePlanScopeBundle(bundleDir);

            var manifestPath = Path.Combine(bundleDir, "artifact-manifest.json");
            var scaffold = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "new-dsp-artifact-manifest.ps1"),
                "-BundleDir", bundleDir,
                "-OutputPath", manifestPath,
                "-IncludeOptionalArtifacts",
                "-Force");

            Assert.Equal(0, scaffold.ExitCode);

            var artifactsDir = Path.Combine(bundleDir, "artifacts");
            Directory.CreateDirectory(artifactsDir);
            foreach (var matrixReportName in new[]
            {
                "live-diagnostics-matrix-report.off-baseline.json",
                "live-diagnostics-matrix-report.thetis-parity.json",
                "live-diagnostics-matrix-report.baseline.json",
                "live-diagnostics-matrix-report.candidate.json"
            })
            {
                await File.WriteAllTextAsync(Path.Combine(artifactsDir, matrixReportName), "{}");
            }

            await File.WriteAllTextAsync(Path.Combine(artifactsDir, "live-diagnostics-history.json"), "{}");
            await File.WriteAllTextAsync(
                Path.Combine(artifactsDir, "live-diagnostics-trace-comparison.json"),
                JsonSerializer.Serialize(new
                {
                    readyForReview = true,
                    regressionCount = 0,
                    gateFailureCount = 0
                }, CamelCaseJson));
            await File.WriteAllTextAsync(
                Path.Combine(artifactsDir, "live-diagnostics-trace-comparison.thetis-parity.json"),
                JsonSerializer.Serialize(new
                {
                    readyForReview = true,
                    regressionCount = 0,
                    gateFailureCount = 0
                }, CamelCaseJson));
            await File.WriteAllTextAsync(Path.Combine(bundleDir, "validation-report.json"), "{}");
            await File.WriteAllTextAsync(
                Path.Combine(bundleDir, "validation-triage-report.json"),
                JsonSerializer.Serialize(new
                {
                    requiredLiveAcceptanceArtifactProblemCount = 0,
                    acceptanceActionPlanCount = 1,
                    acceptanceRequiredActionCount = 1,
                    acceptanceManualActionCount = 0,
                    acceptanceActionCategoryCounts = new object[] { new { category = "live-diagnostics", count = 1 } },
                    primaryAcceptanceActionId = "promote-matrix-mixed-weak-strong-window",
                    primaryAcceptanceActionPriority = 78,
                    primaryAcceptanceActionStageId = "opt-in-candidate-comparison",
                    primaryAcceptanceActionGateId = "live-history-mixed-weak-strong",
                    primaryAcceptanceActionCategory = "live-diagnostics",
                    primaryAcceptanceActionRequired = true,
                    primaryAcceptanceActionManual = false,
                    primaryAcceptanceCommandTemplate = "powershell -NoProfile -ExecutionPolicy Bypass -File tools\\run-dsp-live-diagnostics-matrix.ps1 -BundleDir \"$bundleDir\" -ComparisonId nr5-spnr",
                    primaryAcceptanceCommandStepCount = 1,
                    primaryAcceptanceCommandSteps = new[]
                    {
                        "powershell -NoProfile -ExecutionPolicy Bypass -File tools\\run-dsp-live-diagnostics-matrix.ps1 -BundleDir \"$bundleDir\" -ComparisonId nr5-spnr"
                    },
                    primaryAcceptanceManualAction = "",
                    primaryAcceptanceExpectedArtifact = "artifacts/live-diagnostics-history.json",
                    primaryAcceptanceExpectedArtifactCount = 1,
                    primaryAcceptanceExpectedArtifacts = new[] { "artifacts/live-diagnostics-history.json" },
                    primaryAcceptanceFollowUp = "Rerun strict validation after promotion."
                }, CamelCaseJson));
            await File.WriteAllTextAsync(Path.Combine(bundleDir, "validation-triage-report.md"), "# Triage");

            var summaryPath = Path.Combine(artifactsDir, "live-acceptance-cycle-summary.json");
            var summary = new
            {
                schemaVersion = 7,
                tool = "run-dsp-live-acceptance-cycle",
                acceptanceCommandStepCount = 10,
                acceptanceExpectedArtifacts = new[]
                {
                    "artifacts/live-diagnostics-trace-index.off-baseline.json",
                    "artifacts/live-diagnostics-matrix-report.thetis-parity.json",
                    "artifacts/live-diagnostics-history.json",
                    "artifacts/live-diagnostics-trace-comparison.json",
                    "artifacts/live-diagnostics-trace-comparison.thetis-parity.json",
                    "artifact-manifest.json",
                    "validation-report.json",
                    "validation-triage-report.md",
                    "artifacts/live-acceptance-cycle-summary.json"
                },
                matrixReportPaths = new[]
                {
                    "artifacts/live-diagnostics-matrix-report.off-baseline.json",
                    "artifacts/live-diagnostics-matrix-report.thetis-parity.json",
                    "artifacts/live-diagnostics-matrix-report.baseline.json",
                    "artifacts/live-diagnostics-matrix-report.candidate.json"
                },
                historyReportPath = "artifacts/live-diagnostics-history.json",
                comparisonReportPath = "artifacts/live-diagnostics-trace-comparison.json",
                thetisComparisonReportPath = "artifacts/live-diagnostics-trace-comparison.thetis-parity.json",
                artifactManifestPath = "artifact-manifest.json",
                validationReportPath = "validation-report.json",
                triageReportPath = "validation-triage-report.json",
                triageMarkdownPath = "validation-triage-report.md",
                matrixAcceptanceReady = true,
                matrixAcceptanceReadyCount = 4,
                matrixNonZeroExitCount = 0,
                comparisonReadyForReview = true,
                comparisonRegressionCount = 0,
                comparisonGateFailureCount = 0,
                comparisonMetricCatalogAlignmentReady = false,
                comparisonMetricCatalogAlignmentStatus = "metric-definitions-missing",
                comparisonMetricDefinitionCount = 0,
                comparisonMetricCatalogMissingMetricCount = 0,
                comparisonMetricCatalogMismatchCount = 0,
                thetisComparisonReadyForReview = true,
                thetisComparisonRegressionCount = 0,
                thetisComparisonGateFailureCount = 0,
                thetisComparisonMetricCatalogAlignmentReady = false,
                thetisComparisonMetricCatalogAlignmentStatus = "metric-definitions-missing",
                thetisComparisonMetricDefinitionCount = 0,
                thetisComparisonMetricCatalogMissingMetricCount = 0,
                thetisComparisonMetricCatalogMismatchCount = 0,
                validationOk = true,
                validationErrorCount = 0,
                validationWarningCount = 0,
                hardwareEvidenceReady = true,
                hardwareEvidenceStatus = "g2-hardware-evidence-ready",
                hardwareTarget = "G2",
                captureHardwareTarget = "G2",
                hardwareDiagnosticsPresent = true,
                liveDiagnosticsHistoryAgcStabilityReady = true,
                liveDiagnosticsHistoryAgcStabilityStatus = "agc-stability-ready",
                liveDiagnosticsHistoryAgcStabilityTraceCount = 1,
                liveDiagnosticsHistoryAgcStabilityMissingTraceCount = 0,
                liveDiagnosticsHistoryAgcPumpingRiskTraceCount = 0,
                liveDiagnosticsHistoryAgcActivePumpingSignalCount = 0,
                liveDiagnosticsHistoryAgcVoiceLikePumpingSignalCount = 0,
                liveDiagnosticsHistoryArtifactControlSignalCount = 0,
                liveDiagnosticsHistoryMixedWeakStrongEvidenceReady = true,
                liveDiagnosticsHistoryMixedWeakStrongEvidenceStatus = "ready",
                liveDiagnosticsHistoryMixedWeakStrongTraceCount = 1,
                liveDiagnosticsHistoryMixedWeakStrongReadyTraceCount = 1,
                liveDiagnosticsHistoryMixedWeakStrongMissingTraceCount = 0,
                liveDiagnosticsHistoryMixedWeakStrongGapWatchTraceCount = 0,
                liveMatrixMixedWeakStrongHuntReady = false,
                liveMatrixMixedWeakStrongStatus = "schema-v2-missing",
                liveMatrixMixedWeakStrongReportCount = 4,
                liveMatrixMixedWeakStrongSchemaV2ReportCount = 0,
                liveMatrixMixedWeakStrongReadyReportCount = 0,
                liveMatrixMixedWeakStrongTraceCount = 0,
                liveMatrixMixedWeakStrongReadyTraceCount = 0,
                liveMatrixMixedWeakStrongMissingRunCount = 0,
                liveMatrixMixedWeakStrongGapWatchRunCount = 0,
                liveMatrixMixedWeakStrongWeakInputSampleCount = 0,
                liveMatrixMixedWeakStrongStrongInputSampleCount = 0,
                liveMatrixMixedWeakStrongStatusCounts = Array.Empty<object>(),
                liveMatrixMixedWeakStrongBestRun = (object?)null,
                liveMatrixArtifactControlStatus = "schema-v3-missing",
                liveMatrixArtifactControlReportCount = 4,
                liveMatrixArtifactControlSchemaV3ReportCount = 0,
                liveMatrixArtifactControlReviewRunCount = 0,
                liveMatrixArtifactControlRiskScoreMax = 0.0,
                liveMatrixArtifactControlLowEvidenceLiftedSampleCount = 0,
                liveMatrixArtifactControlLowEvidenceLiftedPctMax = (double?)null,
                liveMatrixArtifactControlAudioAlignmentMismatchPctMax = (double?)null,
                liveMatrixArtifactControlStatusCounts = Array.Empty<object>(),
                triageAcceptanceActionPlanCount = 1,
                triageAcceptanceRequiredActionCount = 1,
                triageAcceptanceManualActionCount = 0,
                triageAcceptanceActionCategoryCounts = new object[] { new { category = "live-diagnostics", count = 1 } },
                triagePrimaryAcceptanceActionId = "promote-matrix-mixed-weak-strong-window",
                triagePrimaryAcceptanceActionPriority = 78,
                triagePrimaryAcceptanceActionStageId = "opt-in-candidate-comparison",
                triagePrimaryAcceptanceActionGateId = "live-history-mixed-weak-strong",
                triagePrimaryAcceptanceActionCategory = "live-diagnostics",
                triagePrimaryAcceptanceActionRequired = true,
                triagePrimaryAcceptanceActionManual = false,
                triagePrimaryAcceptanceCommandTemplate = "powershell -NoProfile -ExecutionPolicy Bypass -File tools\\run-dsp-live-diagnostics-matrix.ps1 -BundleDir \"$bundleDir\" -ComparisonId nr5-spnr",
                triagePrimaryAcceptanceCommandStepCount = 1,
                triagePrimaryAcceptanceCommandSteps = new[]
                {
                    "powershell -NoProfile -ExecutionPolicy Bypass -File tools\\run-dsp-live-diagnostics-matrix.ps1 -BundleDir \"$bundleDir\" -ComparisonId nr5-spnr"
                },
                triagePrimaryAcceptanceManualAction = "",
                triagePrimaryAcceptanceExpectedArtifact = "artifacts/live-diagnostics-history.json",
                triagePrimaryAcceptanceExpectedArtifactCount = 1,
                triagePrimaryAcceptanceExpectedArtifacts = new[] { "artifacts/live-diagnostics-history.json" },
                triagePrimaryAcceptanceFollowUp = "Rerun strict validation after promotion.",
                requiredLiveAcceptanceArtifactProblemCount = 0,
                liveAcceptanceEvidenceReady = true
            };
            await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(summary, CamelCaseJson));

            var validationReport = Path.Combine(bundleDir, "validation-output.json");
            var validation = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "validate-dsp-modernization-bundle.ps1"),
                "-BundleDir", bundleDir,
                "-ArtifactManifestPath", manifestPath,
                "-ReportPath", validationReport,
                "-AllowPreflight",
                "-JsonOnly");

            Assert.NotEqual(0, validation.ExitCode);
            Assert.True(File.Exists(validationReport), validation.CombinedOutput);

            using var validationDoc = JsonDocument.Parse(await File.ReadAllTextAsync(validationReport));
            var validationRoot = validationDoc.RootElement;
            Assert.True(validationRoot.GetProperty("liveAcceptanceCycleSummaryPresent").GetBoolean());
            Assert.False(validationRoot.GetProperty("liveAcceptanceCycleSummaryValid").GetBoolean());
            Assert.Equal("invalid", validationRoot.GetProperty("liveAcceptanceCycleSummaryStatus").GetString());
            Assert.True(validationRoot.GetProperty("liveAcceptanceCycleHardwareEvidenceReady").GetBoolean());
            Assert.Equal("g2-hardware-evidence-ready", validationRoot.GetProperty("liveAcceptanceCycleHardwareEvidenceStatus").GetString());
            Assert.False(validationRoot.GetProperty("liveAcceptanceCycleComparisonMetricCatalogAlignmentReady").GetBoolean());
            Assert.Equal("metric-definitions-missing", validationRoot.GetProperty("liveAcceptanceCycleComparisonMetricCatalogAlignmentStatus").GetString());
            Assert.True(validationRoot.GetProperty("liveAcceptanceCycleLiveDiagnosticsHistoryAgcStabilityReady").GetBoolean());
            Assert.Equal("agc-stability-ready", validationRoot.GetProperty("liveAcceptanceCycleLiveDiagnosticsHistoryAgcStabilityStatus").GetString());
            Assert.Equal(0, validationRoot.GetProperty("liveAcceptanceCycleLiveDiagnosticsHistoryArtifactControlSignalCount").GetInt32());
            Assert.True(validationRoot.GetProperty("liveAcceptanceCycleLiveDiagnosticsHistoryMixedWeakStrongEvidenceReady").GetBoolean());
            Assert.Equal("ready", validationRoot.GetProperty("liveAcceptanceCycleLiveDiagnosticsHistoryMixedWeakStrongEvidenceStatus").GetString());
            Assert.False(validationRoot.GetProperty("liveAcceptanceCycleLiveMatrixMixedWeakStrongHuntReady").GetBoolean());
            Assert.Equal("schema-v2-missing", validationRoot.GetProperty("liveAcceptanceCycleLiveMatrixMixedWeakStrongStatus").GetString());
            Assert.Equal(4, validationRoot.GetProperty("liveAcceptanceCycleLiveMatrixMixedWeakStrongReportCount").GetInt32());
            Assert.Equal("schema-v3-missing", validationRoot.GetProperty("liveAcceptanceCycleLiveMatrixArtifactControlStatus").GetString());
            Assert.Equal(4, validationRoot.GetProperty("liveAcceptanceCycleLiveMatrixArtifactControlReportCount").GetInt32());
            Assert.Equal(0, validationRoot.GetProperty("liveAcceptanceCycleLiveMatrixArtifactControlSchemaV3ReportCount").GetInt32());
            Assert.Equal(1, validationRoot.GetProperty("liveAcceptanceCycleTriageAcceptanceActionPlanCount").GetInt32());
            Assert.Equal("promote-matrix-mixed-weak-strong-window", validationRoot.GetProperty("liveAcceptanceCycleTriagePrimaryAcceptanceActionId").GetString());
            Assert.Equal("live-diagnostics", validationRoot.GetProperty("liveAcceptanceCycleTriagePrimaryAcceptanceActionCategory").GetString());
            Assert.True(validationRoot.GetProperty("liveAcceptanceCycleTriagePrimaryAcceptanceActionRequired").GetBoolean());
            Assert.Equal(1, validationRoot.GetProperty("liveAcceptanceCycleTriagePrimaryAcceptanceCommandStepCount").GetInt32());
            Assert.Equal("artifacts/live-diagnostics-history.json", validationRoot.GetProperty("liveAcceptanceCycleTriagePrimaryAcceptanceExpectedArtifact").GetString());
            Assert.Contains(
                validationRoot.GetProperty("liveAcceptanceCycleTriagePrimaryAcceptanceCommandSteps").EnumerateArray(),
                step => (step.GetString() ?? "").Contains("-ComparisonId nr5-spnr", StringComparison.Ordinal));

            var warningCodes = validationRoot
                .GetProperty("warnings")
                .EnumerateArray()
                .Select(warning => warning.GetProperty("code").GetString())
                .ToArray();

            Assert.Contains("live-acceptance-cycle-summary-hardware-ready-mismatch", warningCodes);
            Assert.Contains("live-acceptance-cycle-summary-hardware-status-mismatch", warningCodes);
            Assert.Contains("live-acceptance-cycle-summary-hardware-diagnostics-present-mismatch", warningCodes);
            Assert.Contains("live-acceptance-cycle-summary-agc-stability-ready-mismatch", warningCodes);
            Assert.Contains("live-acceptance-cycle-summary-agc-stability-status-mismatch", warningCodes);
            Assert.Contains("live-acceptance-cycle-summary-agc-stability-count-mismatch", warningCodes);
            Assert.Contains("live-acceptance-cycle-summary-mixed-weak-strong-ready-mismatch", warningCodes);
            Assert.Contains("live-acceptance-cycle-summary-mixed-weak-strong-status-mismatch", warningCodes);
            Assert.Contains("live-acceptance-cycle-summary-mixed-weak-strong-count-mismatch", warningCodes);
            Assert.Contains("live-acceptance-cycle-summary-matrix-artifact-control-status-mismatch", warningCodes);
            Assert.Contains("live-acceptance-cycle-summary-matrix-artifact-control-count-mismatch", warningCodes);
            Assert.Contains("live-acceptance-cycle-summary-readiness-mismatch", warningCodes);
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
    public async Task RequiredLiveTraceRejectsMetricCatalogContractMismatch()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell validator smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-dsp-live-metric-catalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            WriteSourcePlanScopeBundle(bundleDir);

            await File.WriteAllTextAsync(
                Path.Combine(bundleDir, "bundle-index.json"),
                """
                {
                  "schemaVersion": 1,
                  "endpoints": [
                    { "id": "benchmark-plan", "file": "benchmark-plan.json", "required": true, "ok": true },
                    { "id": "benchmark-capture-manifest", "file": "benchmark-capture-manifest.json", "required": true, "ok": true },
                    { "id": "benchmark-metric-catalog", "file": "benchmark-metric-catalog.json", "required": true, "ok": true }
                  ],
                  "requiredFailures": []
                }
                """);

            var catalog = JsonSerializer.SerializeToNode(DspBenchmarkPlanCatalog.BuildMetricCatalog(), CamelCaseJson)!.AsObject();
            var failedSamplesMetric = catalog["metrics"]!.AsArray()
                .Select(node => node!.AsObject())
                .Single(metric => metric["id"]!.GetValue<string>() == "failedsamplecount");
            failedSamplesMetric["direction"] = "higher";
            failedSamplesMetric["acceptanceThreshold"] = "2.0";
            failedSamplesMetric["safetyClass"] = "pumping";
            await File.WriteAllTextAsync(
                Path.Combine(bundleDir, "benchmark-metric-catalog.json"),
                catalog.ToJsonString(CamelCaseJson));

            var artifactsDir = Path.Combine(bundleDir, "artifacts");
            Directory.CreateDirectory(artifactsDir);
            var baselinePath = Path.Combine(artifactsDir, "live-baseline.jsonl");
            var candidatePath = Path.Combine(artifactsDir, "live-candidate.jsonl");
            await File.WriteAllTextAsync(baselinePath, "{}" + Environment.NewLine);
            await File.WriteAllTextAsync(candidatePath, "{}" + Environment.NewLine);

            var manifestPath = Path.Combine(bundleDir, "artifact-manifest.json");
            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    artifacts = new object[]
                    {
                        new
                        {
                            id = "live-diagnostics-trace-comparison",
                            kind = "diagnostics-comparison-json",
                            source = "tools/compare-dsp-live-diagnostics-traces.ps1",
                            path = "artifacts/live-diagnostics-trace-comparison.json",
                            required = true,
                            scenarioIds = new[] { "weak-cw-carrier" },
                            comparisonIds = new[] { "current-zeus", "nr5-spnr" }
                        }
                    }
                }, CamelCaseJson));

            await File.WriteAllTextAsync(
                Path.Combine(artifactsDir, "live-diagnostics-trace-comparison.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    tool = "compare-dsp-live-diagnostics-traces",
                    bundleRelativePaths = true,
                    baselinePath = "artifacts/live-baseline.jsonl",
                    baselineInputSha256 = ComputeSha256(baselinePath),
                    candidatePath = "artifacts/live-candidate.jsonl",
                    candidateInputSha256 = ComputeSha256(candidatePath),
                    readyForReview = true,
                    candidateComparisonCount = 1,
                    failedComparisonCount = 0,
                    missingBaselineCount = 0,
                    missingCandidateCount = 0,
                    regressionCount = 0,
                    hardConstraintRegressionCount = 0,
                    gateFailureCount = 0,
                    missingMetricValueCount = 0,
                    metricDefinitionSource = "compare-dsp-live-diagnostics-traces",
                    metricDefinitionCount = 1,
                    metricDefinitions = new object[]
                    {
                        new
                        {
                            id = "failedSampleCount",
                            label = "Failed samples",
                            direction = "lower",
                            threshold = 0.0,
                            safetyClass = "hard-gate",
                            acceptanceScope = "live-diagnostics-trace-comparison",
                            rationale = "Endpoint failures make trace evidence incomplete."
                        }
                    },
                    metricComparisonCount = 1,
                    metricComparisons = new object[]
                    {
                        new
                        {
                            metricId = "failedSampleCount",
                            label = "Failed samples",
                            direction = "lower",
                            definitionThreshold = 0.0,
                            threshold = 0.0,
                            safetyClass = "hard-gate",
                            acceptanceScope = "live-diagnostics-trace-comparison",
                            baselineValue = 0.0,
                            candidateValue = 0.0,
                            improvementValue = 0.0,
                            verdict = "tie",
                            rationale = "Endpoint failures make trace evidence incomplete."
                        }
                    },
                    captureReadinessComparison = new
                    {
                        candidateStatus = "ready",
                        candidateHardGatePass = true,
                        candidateStrictPreflightPass = true,
                        candidateTopConstraintName = "",
                        candidateTopConstraintCount = 0,
                        candidateTopHardConstraintName = "",
                        candidateTopHardConstraintCount = 0
                    },
                    rxAudioLevelerComparison = new { }
                }, CamelCaseJson));

            var validationReport = Path.Combine(bundleDir, "validation-report.json");
            var validation = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "validate-dsp-modernization-bundle.ps1"),
                "-BundleDir", bundleDir,
                "-ArtifactManifestPath", manifestPath,
                "-ReportPath", validationReport,
                "-AllowPreflight",
                "-JsonOnly");

            Assert.NotEqual(0, validation.ExitCode);
            Assert.True(File.Exists(validationReport), validation.CombinedOutput);

            using var validationDoc = JsonDocument.Parse(await File.ReadAllTextAsync(validationReport));
            var validationRoot = validationDoc.RootElement;
            Assert.False(validationRoot.GetProperty("liveTraceComparisonMetricCatalogAlignmentReady").GetBoolean());
            Assert.Equal("metric-catalog-mismatch", validationRoot.GetProperty("liveTraceComparisonMetricCatalogAlignmentStatus").GetString());
            Assert.Equal(1, validationRoot.GetProperty("liveTraceComparisonMetricDefinitionCount").GetInt32());
            Assert.Equal(1, validationRoot.GetProperty("liveTraceComparisonMetricCatalogDirectionMismatchCount").GetInt32());
            Assert.Equal(1, validationRoot.GetProperty("liveTraceComparisonMetricCatalogThresholdMismatchCount").GetInt32());
            Assert.Equal(1, validationRoot.GetProperty("liveTraceComparisonMetricCatalogSafetyClassMismatchCount").GetInt32());

            var errorCodes = validationRoot.GetProperty("errors")
                .EnumerateArray()
                .Select(error => error.GetProperty("code").GetString())
                .ToArray();

            Assert.Contains("live-trace-comparison-metric-catalog-direction-mismatch", errorCodes);
            Assert.Contains("live-trace-comparison-metric-catalog-threshold-mismatch", errorCodes);
            Assert.Contains("live-trace-comparison-metric-catalog-safety-class-mismatch", errorCodes);
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
    public async Task RequiredLiveTraceRejectsMislabeledNr5ComparisonState()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell validator smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-dsp-live-state-readiness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            WriteSourcePlanScopeBundle(bundleDir);

            var manifestPath = Path.Combine(bundleDir, "artifact-manifest.json");
            var scaffold = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "new-dsp-artifact-manifest.ps1"),
                "-BundleDir", bundleDir,
                "-OutputPath", manifestPath,
                "-AcceptanceManifest",
                "-RequireLiveAcceptanceArtifacts",
                "-Force");

            Assert.Equal(0, scaffold.ExitCode);

            var artifactsDir = Path.Combine(bundleDir, "artifacts");
            var tracesDir = Path.Combine(artifactsDir, "live-traces");
            Directory.CreateDirectory(tracesDir);

            var jsonlPath = Path.Combine(tracesDir, "weak-cw-carrier.nr5-spnr.jsonl");
            var jsonlRecord = new
            {
                sampleIndex = 1,
                sampledUtc = DateTimeOffset.UtcNow,
                ok = true,
                diagnostics = new
                {
                    schemaVersion = 1,
                    status = "ready",
                    requestedNrMode = "Off",
                    effectiveNrMode = "Off",
                    readyForLiveBenchmark = true,
                    runtimeEvidence = new
                    {
                        status = "ready",
                        audioStatus = "ready",
                        rxMetersFresh = true,
                        audioFresh = true
                    }
                }
            };
            await File.WriteAllTextAsync(jsonlPath, JsonSerializer.Serialize(jsonlRecord, CamelCaseJson) + Environment.NewLine);

            var summaryPath = Path.Combine(tracesDir, "weak-cw-carrier.nr5-spnr.summary.json");
            var jsonlRelative = "artifacts/live-traces/weak-cw-carrier.nr5-spnr.jsonl";
            var summaryRelative = "artifacts/live-traces/weak-cw-carrier.nr5-spnr.summary.json";
            var watchSummary = new
            {
                schemaVersion = 1,
                tool = "watch-dsp-live-diagnostics",
                scenarioId = "weak-cw-carrier",
                comparisonId = "nr5-spnr",
                jsonlPath = jsonlRelative,
                sampleCount = 1,
                okSampleCount = 1,
                failedSampleCount = 0,
                readyForBenchmarkTrace = true,
                trendStatus = "ready-trace",
                nr5SampleCount = 0,
                nr5AlignedSampleCount = 0,
                nr5AgcDiagnosticSampleCount = 0,
                nr5ProbabilityDiagnosticSampleCount = 0,
                nr5PeakDiagnosticSampleCount = 0,
                nr5RequestedSampleCount = 0,
                nr5EffectiveSampleCount = 0,
                nrOffRequestedSampleCount = 1,
                nrOffEffectiveSampleCount = 1,
                nrModeMismatchSampleCount = 0,
                nr5WeakSignalWatch = new
                {
                    weakInputSampleCount = 0,
                    weakRecoveredSampleCount = 0,
                    weakDropoutSampleCount = 0,
                    hotMakeupSampleCount = 0
                },
                comparisonStateReadiness = new
                {
                    comparisonId = "nr5-spnr",
                    strict = true,
                    ready = false,
                    status = "nr5-effective-missing",
                    nextAction = "Reassert NR5/SPNR before recapturing.",
                    okSampleCount = 1,
                    nr5SampleCount = 0,
                    nr5AlignedSampleCount = 0,
                    nr5AgcDiagnosticSampleCount = 0,
                    nr5ProbabilityDiagnosticSampleCount = 0,
                    nr5PeakDiagnosticSampleCount = 0
                }
            };
            await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(watchSummary, CamelCaseJson));

            var indexPath = Path.Combine(artifactsDir, "live-diagnostics-trace-index.candidate.json");
            var traceIndex = new
            {
                schemaVersion = 1,
                tool = "run-dsp-live-diagnostics-matrix",
                artifactId = "live-diagnostics-trace-index",
                comparisonId = "nr5-spnr",
                comparisonIds = new[] { "nr5-spnr" },
                scenarioIds = new[] { "weak-cw-carrier" },
                files = new[]
                {
                    new
                    {
                        path = jsonlRelative,
                        kind = "diagnostics-jsonl",
                        scenarioId = "weak-cw-carrier",
                        comparisonId = "nr5-spnr",
                        comparisonIds = new[] { "nr5-spnr" },
                        sampleCount = 1,
                        summaryPath = summaryRelative,
                        sha256 = ComputeSha256(jsonlPath),
                        summarySha256 = ComputeSha256(summaryPath),
                        comparisonStateStrict = true,
                        comparisonStateReady = false,
                        comparisonStateStatus = "nr5-effective-missing",
                        nr5SampleCount = 0,
                        nr5AlignedSampleCount = 0,
                        nr5AgcDiagnosticSampleCount = 0,
                        nr5ProbabilityDiagnosticSampleCount = 0,
                        nr5PeakDiagnosticSampleCount = 0
                    }
                }
            };
            await File.WriteAllTextAsync(indexPath, JsonSerializer.Serialize(traceIndex, CamelCaseJson));

            var matrixReportPath = Path.Combine(artifactsDir, "live-diagnostics-matrix-report.candidate.json");
            var matrixReport = new
            {
                schemaVersion = 1,
                tool = "run-dsp-live-diagnostics-matrix",
                comparisonId = "nr5-spnr",
                comparisonIds = new[] { "nr5-spnr" },
                samples = 1,
                scenarioCount = 1,
                failedRunCount = 0,
                notReadyTraceCount = 0,
                hardBlockerRunCount = 0,
                hardGatePassRunCount = 1,
                strictPreflightPassRunCount = 1,
                comparisonStateStatusCounts = new[] { new { name = "nr5-effective-missing", count = 1 } },
                comparisonStateStrictRunCount = 1,
                comparisonStateReadyRunCount = 0,
                comparisonStateStrictFailureCount = 1,
                collectionReady = true,
                acceptanceReady = true,
                indexPath = "artifacts/live-diagnostics-trace-index.candidate.json",
                indexSha256 = ComputeSha256(indexPath),
                runs = new[]
                {
                    new
                    {
                        scenarioId = "weak-cw-carrier",
                        comparisonId = "nr5-spnr",
                        ok = true,
                        readyForBenchmarkTrace = true,
                        okSampleCount = 1,
                        failedSampleCount = 0,
                        hardBlockerSampleCount = 0,
                        hardGatePass = true,
                        strictPreflightPass = true,
                        comparisonStateStrict = true,
                        comparisonStateReady = false,
                        comparisonStateStatus = "nr5-effective-missing"
                    }
                }
            };
            await File.WriteAllTextAsync(matrixReportPath, JsonSerializer.Serialize(matrixReport, CamelCaseJson));

            var validationReport = Path.Combine(bundleDir, "validation-report.json");
            var validation = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "validate-dsp-modernization-bundle.ps1"),
                "-BundleDir", bundleDir,
                "-ArtifactManifestPath", manifestPath,
                "-ReportPath", validationReport,
                "-AllowPreflight",
                "-JsonOnly");

            Assert.NotEqual(0, validation.ExitCode);
            Assert.True(File.Exists(validationReport), validation.CombinedOutput);

            using var validationDoc = JsonDocument.Parse(await File.ReadAllTextAsync(validationReport));
            var errorCodes = validationDoc.RootElement
                .GetProperty("errors")
                .EnumerateArray()
                .Select(error => error.GetProperty("code").GetString())
                .ToArray();

            Assert.Contains("live-trace-index-comparison-state-not-ready", errorCodes);
            Assert.Contains("live-matrix-report-acceptance-ready-mismatch", errorCodes);
            Assert.Contains("live-matrix-report-comparison-state-not-ready", errorCodes);
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
    public async Task ArtifactManifestScaffoldCanRequireLiveAcceptanceArtifactsWithoutExternalBakeoff()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell artifact scaffold smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-dsp-live-acceptance-artifacts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            WriteSourcePlanScopeBundle(bundleDir);

            var manifestPath = Path.Combine(bundleDir, "artifact-manifest.json");
            var scaffold = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "new-dsp-artifact-manifest.ps1"),
                "-BundleDir", bundleDir,
                "-OutputPath", manifestPath,
                "-AcceptanceManifest",
                "-RequireLiveAcceptanceArtifacts",
                "-Force");

            Assert.Equal(0, scaffold.ExitCode);
            Assert.True(File.Exists(manifestPath), scaffold.CombinedOutput);

            using var manifestDoc = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
            var manifestRoot = manifestDoc.RootElement;
            Assert.True(manifestRoot.GetProperty("acceptanceManifest").GetBoolean());
            Assert.True(manifestRoot.GetProperty("requireLiveAcceptanceArtifacts").GetBoolean());
            Assert.False(manifestRoot.GetProperty("externalEngineBakeoffInScope").GetBoolean());
            var notes = manifestRoot.GetProperty("notes")
                .EnumerateArray()
                .Select(note => note.GetString() ?? "")
                .ToArray();
            Assert.Contains(notes, note => note.Contains("-RequireLiveAcceptanceArtifacts", StringComparison.Ordinal));
            Assert.DoesNotContain(notes, note => note.Contains("set the live-diagnostics-trace-comparison artifact required=true", StringComparison.Ordinal));

            var artifacts = manifestRoot.GetProperty("artifacts").EnumerateArray().ToArray();
            AssertLiveArtifact(artifacts, "artifacts/live-diagnostics-trace-index.off-baseline.json", "live-diagnostics-trace-index", "off-baseline", required: true);
            AssertLiveArtifact(artifacts, "artifacts/live-diagnostics-matrix-report.off-baseline.json", "live-diagnostics-matrix-report-off-baseline", "off-baseline", required: true);
            AssertLiveArtifact(artifacts, "artifacts/live-diagnostics-trace-index.thetis-parity.json", "live-diagnostics-trace-index", "thetis-parity", required: true);
            AssertLiveArtifact(artifacts, "artifacts/live-diagnostics-matrix-report.thetis-parity.json", "live-diagnostics-matrix-report-thetis-parity", "thetis-parity", required: true);
            AssertLiveArtifact(artifacts, "artifacts/live-diagnostics-trace-index.baseline.json", "live-diagnostics-trace-index", "current-zeus", required: true);
            AssertLiveArtifact(artifacts, "artifacts/live-diagnostics-matrix-report.baseline.json", "live-diagnostics-matrix-report-baseline", "current-zeus", required: true);
            AssertLiveArtifact(artifacts, "artifacts/live-diagnostics-trace-index.candidate.json", "live-diagnostics-trace-index", "nr5-spnr", required: true);
            AssertLiveArtifact(artifacts, "artifacts/live-diagnostics-matrix-report.candidate.json", "live-diagnostics-matrix-report-candidate", "nr5-spnr", required: true);

            var comparison = artifacts.Single(artifact => artifact.GetProperty("id").GetString() == "live-diagnostics-trace-comparison");
            Assert.True(comparison.GetProperty("required").GetBoolean());
            Assert.Equal("artifacts/live-diagnostics-trace-comparison.json", comparison.GetProperty("path").GetString());

            var thetisComparison = artifacts.Single(artifact => artifact.GetProperty("id").GetString() == "live-diagnostics-trace-comparison-thetis-parity");
            Assert.True(thetisComparison.GetProperty("required").GetBoolean());
            Assert.Equal("artifacts/live-diagnostics-trace-comparison.thetis-parity.json", thetisComparison.GetProperty("path").GetString());
            Assert.Equal(new[] { "thetis-parity", "nr5-spnr" }, ReadStringArray(thetisComparison, "comparisonIds"));

            var history = artifacts.Single(artifact => artifact.GetProperty("id").GetString() == "live-diagnostics-history");
            Assert.True(history.GetProperty("required").GetBoolean());
            Assert.Equal("artifacts/live-diagnostics-history.json", history.GetProperty("path").GetString());

            var liveAcceptanceCycleSummary = artifacts.Single(artifact => artifact.GetProperty("id").GetString() == "live-acceptance-cycle-summary");
            Assert.False(liveAcceptanceCycleSummary.GetProperty("required").GetBoolean());
            Assert.Equal("live-acceptance-cycle-summary-json", liveAcceptanceCycleSummary.GetProperty("kind").GetString());
            Assert.Equal("artifacts/live-acceptance-cycle-summary.json", liveAcceptanceCycleSummary.GetProperty("path").GetString());

            Assert.DoesNotContain(
                artifacts,
                artifact => artifact.GetProperty("id").GetString() == "external-engine-bakeoff-report"
                    && artifact.GetProperty("required").GetBoolean());

            var validationReport = Path.Combine(bundleDir, "validation-report.json");
            var validation = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "validate-dsp-modernization-bundle.ps1"),
                "-BundleDir", bundleDir,
                "-ArtifactManifestPath", manifestPath,
                "-ReportPath", validationReport,
                "-AllowPreflight",
                "-JsonOnly");

            Assert.NotEqual(0, validation.ExitCode);
            Assert.True(File.Exists(validationReport), validation.CombinedOutput);

            using var validationDoc = JsonDocument.Parse(await File.ReadAllTextAsync(validationReport));
            var unexpectedArtifactWarnings = validationDoc.RootElement
                .GetProperty("warnings")
                .EnumerateArray()
                .Where(issue => issue.GetProperty("code").GetString() == "artifact-not-in-capture-manifest")
                .Select(issue => issue.GetProperty("message").GetString() ?? "")
                .ToArray();

            Assert.DoesNotContain(unexpectedArtifactWarnings, message => message.Contains("live-diagnostics-trace-comparison", StringComparison.Ordinal));
            Assert.DoesNotContain(unexpectedArtifactWarnings, message => message.Contains("live-diagnostics-trace-index", StringComparison.Ordinal));
            Assert.DoesNotContain(unexpectedArtifactWarnings, message => message.Contains("live-diagnostics-matrix-report-off-baseline", StringComparison.Ordinal));
            Assert.DoesNotContain(unexpectedArtifactWarnings, message => message.Contains("live-diagnostics-history", StringComparison.Ordinal));
            Assert.DoesNotContain(unexpectedArtifactWarnings, message => message.Contains("live-acceptance-cycle-summary", StringComparison.Ordinal));

            var summaryReport = Path.Combine(bundleDir, "validation-summary.json");
            var summaryMarkdown = Path.Combine(bundleDir, "validation-summary.md");
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
            Assert.True(summaryRoot.GetProperty("requiredLiveAcceptanceArtifactProblemCount").GetInt32() > 0);

            var problemIds = ReadStringArray(summaryRoot, "requiredLiveAcceptanceArtifactProblemIds");
            Assert.Contains("live-diagnostics-trace-comparison", problemIds);
            Assert.Contains("live-diagnostics-trace-comparison-thetis-parity", problemIds);
            Assert.Contains("live-diagnostics-trace-index", problemIds);
            Assert.Contains("live-diagnostics-history", problemIds);
            Assert.Contains("live-diagnostics-matrix-report-off-baseline", problemIds);
            Assert.DoesNotContain("live-acceptance-cycle-summary", problemIds);

            var problemRecords = summaryRoot.GetProperty("requiredLiveAcceptanceArtifactProblems")
                .EnumerateArray()
                .ToArray();
            var baselineTraceIndexProblem = problemRecords.Single(record =>
                record.GetProperty("id").GetString() == "live-diagnostics-trace-index"
                && record.GetProperty("path").GetString() == "artifacts/live-diagnostics-trace-index.baseline.json");
            Assert.Equal(new[] { "current-zeus" }, ReadStringArray(baselineTraceIndexProblem, "comparisonIds"));

            var validationGate = summaryRoot.GetProperty("evidenceGates")
                .EnumerateArray()
                .Single(gate => gate.GetProperty("gateId").GetString() == "validation-report");
            var validationGateDetail = validationGate.GetProperty("detail").GetString() ?? "";
            Assert.Contains("liveAcceptanceArtifactProblems=", validationGateDetail, StringComparison.Ordinal);
            Assert.Contains("live-diagnostics-trace-comparison", validationGateDetail, StringComparison.Ordinal);

            Assert.Contains(
                ReadStringArray(summaryRoot, "recommendations"),
                recommendation => recommendation.Contains("required live acceptance artifacts", StringComparison.Ordinal));

            var validationAction = summaryRoot.GetProperty("acceptanceActionPlan")
                .EnumerateArray()
                .Single(action => action.GetProperty("gateId").GetString() == "validation-report");
            Assert.Contains(
                "Missing required live acceptance artifacts",
                validationAction.GetProperty("reason").GetString() ?? "",
                StringComparison.Ordinal);
            Assert.Contains(
                "live-diagnostics-trace-comparison",
                validationAction.GetProperty("reason").GetString() ?? "",
                StringComparison.Ordinal);

            var liveReadyValidationPath = Path.Combine(bundleDir, "validation-report-live-ready-sidecars-missing.json");
            var liveReadyValidationNode = JsonNode.Parse(await File.ReadAllTextAsync(validationReport))!.AsObject();
            liveReadyValidationNode["liveTraceComparisonPresent"] = true;
            liveReadyValidationNode["liveTraceComparisonReady"] = true;
            liveReadyValidationNode["liveTraceComparisonRegressionCount"] = 0;
            liveReadyValidationNode["liveTraceComparisonGateFailureCount"] = 0;
            liveReadyValidationNode["liveTraceComparisonMissingMetricDetailCount"] = 0;
            liveReadyValidationNode["liveTraceComparisonCaptureReadinessCandidateHardGateFailCount"] = 0;
            liveReadyValidationNode["liveTraceComparisonCaptureReadinessCandidateStrictPreflightFailCount"] = 0;
            liveReadyValidationNode["liveTraceThetisComparisonPresent"] = true;
            liveReadyValidationNode["liveTraceThetisComparisonReady"] = true;
            liveReadyValidationNode["liveTraceThetisComparisonRegressionCount"] = 0;
            liveReadyValidationNode["liveTraceThetisComparisonGateFailureCount"] = 0;
            liveReadyValidationNode["liveTraceThetisComparisonCaptureReadinessCandidateHardGateFailCount"] = 0;
            liveReadyValidationNode["liveTraceThetisComparisonCaptureReadinessCandidateStrictPreflightFailCount"] = 0;
            await File.WriteAllTextAsync(liveReadyValidationPath, liveReadyValidationNode.ToJsonString(CamelCaseJson));

            var liveReadySummaryReport = Path.Combine(bundleDir, "validation-summary-live-ready-sidecars-missing.json");
            var liveReadySummary = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "summarize-dsp-modernization-validation-report.ps1"),
                "-ValidationReportPath", liveReadyValidationPath,
                "-ReportPath", liveReadySummaryReport,
                "-NoMarkdown",
                "-JsonOnly");

            Assert.Equal(0, liveReadySummary.ExitCode);
            Assert.True(File.Exists(liveReadySummaryReport), liveReadySummary.CombinedOutput);

            using var liveReadySummaryDoc = JsonDocument.Parse(await File.ReadAllTextAsync(liveReadySummaryReport));
            var liveReadyAction = liveReadySummaryDoc.RootElement
                .GetProperty("acceptanceActionPlan")
                .EnumerateArray()
                .Single(action => action.GetProperty("actionId").GetString() == "capture-and-compare-live-matrix");
            Assert.Equal("validation-report", liveReadyAction.GetProperty("gateId").GetString());
            Assert.Equal(10, liveReadyAction.GetProperty("commandStepCount").GetInt32());
            Assert.Contains(
                "Required live acceptance artifacts are missing",
                liveReadyAction.GetProperty("reason").GetString() ?? "",
                StringComparison.Ordinal);
            Assert.Contains(
                "artifacts/live-diagnostics-history.json",
                ReadStringArray(liveReadyAction, "expectedArtifacts"));
            Assert.Contains(
                "validation-triage-report.md",
                ReadStringArray(liveReadyAction, "expectedArtifacts"));

            var markdown = await File.ReadAllTextAsync(summaryMarkdown);
            Assert.Contains("## Required Live Acceptance Artifact Problems", markdown, StringComparison.Ordinal);
            Assert.Contains("artifacts/live-diagnostics-trace-index.baseline.json", markdown, StringComparison.Ordinal);
            Assert.Contains("current-zeus", markdown, StringComparison.Ordinal);
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
    public async Task ArtifactManifestScaffoldOptionalLiveMatrixArtifactsCoverParityComparisons()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell artifact scaffold smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-dsp-live-artifact-plan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            WriteSourcePlanScopeBundle(bundleDir);

            var manifestPath = Path.Combine(bundleDir, "artifact-manifest.json");
            var scaffold = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "new-dsp-artifact-manifest.ps1"),
                "-BundleDir", bundleDir,
                "-OutputPath", manifestPath,
                "-IncludeOptionalArtifacts",
                "-Force");

            Assert.Equal(0, scaffold.ExitCode);
            Assert.True(File.Exists(manifestPath), scaffold.CombinedOutput);

            using var manifestDoc = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
            var manifestRoot = manifestDoc.RootElement;
            Assert.False(manifestRoot.GetProperty("externalEngineBakeoffInScope").GetBoolean());

            var artifacts = manifestRoot.GetProperty("artifacts").EnumerateArray().ToArray();
            AssertLiveArtifact(artifacts, "artifacts/live-diagnostics-trace-index.off-baseline.json", "live-diagnostics-trace-index", "off-baseline");
            AssertLiveArtifact(artifacts, "artifacts/live-diagnostics-matrix-report.off-baseline.json", "live-diagnostics-matrix-report-off-baseline", "off-baseline");
            AssertLiveArtifact(artifacts, "artifacts/live-diagnostics-trace-index.thetis-parity.json", "live-diagnostics-trace-index", "thetis-parity");
            AssertLiveArtifact(artifacts, "artifacts/live-diagnostics-matrix-report.thetis-parity.json", "live-diagnostics-matrix-report-thetis-parity", "thetis-parity");
            AssertLiveArtifact(artifacts, "artifacts/live-diagnostics-trace-index.baseline.json", "live-diagnostics-trace-index", "current-zeus");
            AssertLiveArtifact(artifacts, "artifacts/live-diagnostics-matrix-report.baseline.json", "live-diagnostics-matrix-report-baseline", "current-zeus");
            AssertLiveArtifact(artifacts, "artifacts/live-diagnostics-trace-index.candidate.json", "live-diagnostics-trace-index", "nr5-spnr");
            AssertLiveArtifact(artifacts, "artifacts/live-diagnostics-matrix-report.candidate.json", "live-diagnostics-matrix-report-candidate", "nr5-spnr");

            var liveAcceptanceCycleSummary = artifacts.Single(artifact => artifact.GetProperty("id").GetString() == "live-acceptance-cycle-summary");
            Assert.False(liveAcceptanceCycleSummary.GetProperty("required").GetBoolean());
            Assert.Equal("artifacts/live-acceptance-cycle-summary.json", liveAcceptanceCycleSummary.GetProperty("path").GetString());

            Assert.DoesNotContain(
                artifacts,
                artifact => artifact.GetProperty("id").GetString() == "external-engine-bakeoff-report"
                    && artifact.GetProperty("required").GetBoolean());

            var validationReport = Path.Combine(bundleDir, "validation-report.json");
            var validation = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "validate-dsp-modernization-bundle.ps1"),
                "-BundleDir", bundleDir,
                "-ArtifactManifestPath", manifestPath,
                "-ReportPath", validationReport,
                "-AllowPreflight",
                "-JsonOnly");

            Assert.NotEqual(0, validation.ExitCode);
            Assert.True(File.Exists(validationReport), validation.CombinedOutput);

            using var validationDoc = JsonDocument.Parse(await File.ReadAllTextAsync(validationReport));
            var unexpectedArtifactWarnings = validationDoc.RootElement
                .GetProperty("warnings")
                .EnumerateArray()
                .Where(issue => issue.GetProperty("code").GetString() == "artifact-not-in-capture-manifest")
                .Select(issue => issue.GetProperty("message").GetString() ?? "")
                .ToArray();

            Assert.DoesNotContain(unexpectedArtifactWarnings, message => message.Contains("live-diagnostics-matrix-report-off-baseline", StringComparison.Ordinal));
            Assert.DoesNotContain(unexpectedArtifactWarnings, message => message.Contains("live-diagnostics-matrix-report-thetis-parity", StringComparison.Ordinal));
            Assert.DoesNotContain(unexpectedArtifactWarnings, message => message.Contains("live-acceptance-cycle-summary", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(bundleDir))
            {
                Directory.Delete(bundleDir, recursive: true);
            }
        }
    }

    private static void WriteGlobalExternalScopeBundle(string bundleDir)
    {
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

        File.WriteAllText(
            Path.Combine(bundleDir, "benchmark-plan.json"),
            """
            {
              "schemaVersion": 1,
              "firstHardwareTarget": "G2",
              "requiredComparisons": [
                "off-baseline",
                "thetis-parity",
                "current-zeus",
                "nr5-spnr",
                "candidate-external-engine-opt-in"
              ],
              "globalAcceptanceGates": [
                "No weak-signal loss",
                "No TX clipping",
                "G2 first-pass validation",
                "non-G2 cross-radio validation"
              ],
              "scenarios": [
                {
                  "id": "weak-cw-carrier",
                  "name": "Weak CW carrier",
                  "fixtureStatus": "offline-fixture-ready",
                  "signalPath": "RXA",
                  "requiredComparisons": [
                    "current-zeus",
                    "thetis-parity",
                    "candidate-under-test"
                  ],
                  "requiredMetrics": [
                    "outputRms",
                    "clippingCount"
                  ],
                  "acceptanceGates": [
                    "weak-signal-preserved"
                  ]
                }
              ]
            }
            """);

        File.WriteAllText(
            Path.Combine(bundleDir, "benchmark-capture-manifest.json"),
            """
            {
              "schemaVersion": 1,
              "hardwareTarget": "G2",
              "scenarioIds": [
                "weak-cw-carrier"
              ],
              "requiredComparisons": [
                "off-baseline",
                "thetis-parity",
                "current-zeus",
                "nr5-spnr",
                "candidate-external-engine-opt-in"
              ],
              "requiredArtifacts": [
                { "id": "live-diagnostics-json", "kind": "endpoint-json", "required": false },
                { "id": "benchmark-plan-json", "kind": "endpoint-json", "required": false },
                { "id": "wdsp-native-symbol-audit", "kind": "symbol-audit-json", "required": false },
                { "id": "wdsp-runtime-artifact-audit", "kind": "runtime-audit-json", "required": false },
                { "id": "offline-fixture-metrics", "kind": "metrics-json", "required": false }
              ]
            }
            """);
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

    private static void WriteSyntheticLiveHistoryCoverageValidationReport(
        string path,
        string coverageStatus,
        int missingComparisonCount,
        string[] missingComparisonIds)
    {
        var report = new
        {
            ok = true,
            errorCount = 0,
            warningCount = 0,
            errors = Array.Empty<object>(),
            warnings = Array.Empty<object>(),
            artifactReferencedFiles = Array.Empty<object>(),
            hardwareEvidenceStatus = "g2-hardware-evidence-ready",
            liveDiagnosticsHistoryPresent = true,
            liveDiagnosticsHistoryReady = true,
            liveDiagnosticsHistoryTraceSourceStatus = "hash-ready",
            liveDiagnosticsHistoryTraceSourceCheckedCount = 2,
            liveDiagnosticsHistoryLiveExperimentCoverageStatus = coverageStatus,
            liveDiagnosticsHistoryLiveExperimentCoverageMissingComparisonCount = missingComparisonCount,
            liveDiagnosticsHistoryLiveExperimentCoverageMissingComparisonIds = missingComparisonIds
        };

        File.WriteAllText(path, JsonSerializer.Serialize(report, CamelCaseJson));
    }

    private static void WriteLiveHistoryOnlyArtifactManifest(string path)
    {
        var manifest = new
        {
            schemaVersion = 1,
            artifacts = new object[]
            {
                new
                {
                    id = "live-diagnostics-history",
                    kind = "diagnostics-history-json",
                    source = "tools/summarize-dsp-live-diagnostics-history.ps1",
                    path = "artifacts/live-diagnostics-history.json",
                    required = true
                }
            }
        };

        File.WriteAllText(path, JsonSerializer.Serialize(manifest, CamelCaseJson));
    }

    private static void WriteSyntheticOptInBuildOutReadyValidationReport(string path)
    {
        var report = new
        {
            ok = true,
            errorCount = 0,
            warningCount = 0,
            errors = Array.Empty<object>(),
            warnings = Array.Empty<object>(),
            artifactReferencedFiles = Array.Empty<object>(),
            hardwareEvidenceStatus = "diagnostics-missing",
            hardwareTarget = "G2",
            captureHardwareTarget = "G2",
            hardwareDiagnosticsPresent = false,
            nativeSymbolAuditReady = true,
            nativeSymbolAuditPresent = true,
            nativeSymbolAuditImportedSymbolCount = 64,
            nativeSymbolAuditSourceMissingRequiredCount = 0,
            nativeSymbolAuditSignatureMismatchCount = 0,
            nativeSymbolAuditBinaryMissingRequiredCount = 0,
            nativeRuntimeArtifactAuditPresent = true,
            nativeRuntimeArtifactAuditReadyForWinX64Package = true,
            nativeRuntimeArtifactAuditArtifactCount = 1,
            nativeRuntimeArtifactAuditPendingRidCount = 0,
            nativeRuntimeArtifactAuditWinX64NativeSha256 = "synthetic-ready-runtime",
            benchmarkPlanStatus = "ready",
            benchmarkPlanScenarioCount = 12,
            benchmarkPlanRequiredAcceptanceScenarioFamilyCount = 11,
            benchmarkPlanCoveredAcceptanceScenarioFamilyCount = 11,
            benchmarkPlanMissingAcceptanceScenarioFamilyCount = 0,
            benchmarkPlanMissingAcceptanceScenarioFamilyIds = Array.Empty<string>(),
            benchmarkPlanScenarioMissingRequiredComparisonCount = 0,
            benchmarkPlanScenarioMissingRequiredMetricCount = 0,
            benchmarkPlanScenarioMissingAcceptanceGateCount = 0,
            metricCatalogStatus = "ready",
            metricCatalogMetricCount = 27,
            metricCatalogRequiredMetricCount = 12,
            metricCatalogMissingRequiredMetricCount = 0,
            metricCatalogAcceptanceContractReady = true,
            metricCatalogMissingThresholdCount = 0,
            metricCatalogMissingComparatorCount = 0,
            metricCatalogInvalidComparatorCount = 0,
            metricCatalogMissingUnitCount = 0,
            metricCatalogMissingSafetyClassCount = 0,
            metricCatalogMissingAcceptanceScopeCount = 0,
            metricCatalogContractProblemMetricIds = Array.Empty<string>(),
            externalEngineCandidateStatus = "opt-in-gated",
            externalEngineCandidateCount = 4,
            externalEngineCandidateMissingCount = 0,
            externalEngineCandidateMissingIds = Array.Empty<string>(),
            externalEngineCandidateUnsafeCount = 0,
            externalEngineCandidateUnsafeIds = Array.Empty<string>(),
            externalEngineCandidateIssueCounts = Array.Empty<object>(),
            externalEngineCandidateSnapshotMismatchCount = 0,
            externalEngineBakeoffRequiredByScope = false,
            externalEngineBakeoffReportPresent = false,
            externalEngineBakeoffReady = false,
            externalEngineBakeoffMissingCandidateIds = Array.Empty<string>(),
            externalEngineBakeoffUnsafeCandidateIds = Array.Empty<string>(),
            externalEngineBakeoffBlockedCandidateIds = Array.Empty<string>(),
            externalEngineBakeoffCandidateIssueCounts = Array.Empty<object>(),
            externalEngineBakeoffScopeTriggers = Array.Empty<string>(),
            metricComparisonReady = false,
            liveTraceComparisonReady = false,
            liveTraceThetisComparisonReady = false,
            liveDiagnosticsHistoryPresent = false,
            liveDiagnosticsHistoryReady = false
        };

        File.WriteAllText(path, JsonSerializer.Serialize(report, CamelCaseJson));
    }

    private static object ArtifactControlSummary(
        double signalProbabilityAverage,
        double textureFillAverage,
        int lowEvidenceLiftedSampleCount,
        double lowEvidenceLiftedPct,
        double audioAlignmentMismatchPct)
    {
        return new
        {
            schemaVersion = 1,
            tool = "synthetic-watch-summary",
            readyForBenchmarkTrace = true,
            trendStatus = "ready",
            okSampleCount = 3,
            readySampleCount = 3,
            failedSampleCount = 0,
            hardBlockerSampleCount = 0,
            runtimeEvidenceSampleCount = 3,
            audioFreshSampleCount = 3,
            rxMetersFreshSampleCount = 3,
            nr5SampleCount = 3,
            squelchClosedPct = 0.0,
            readinessScore = new { average = 92.0 },
            agcGainDb = new { movement = 0.2 },
            agcStabilityWatch = new
            {
                pumpingRisk = false,
                activeAgcGainDb = new { movement = 0.2 },
                voiceLikeAgcGainDb = new { movement = 0.2 }
            },
            nr5WeakSignalWatch = new
            {
                weakInputSampleCount = 3,
                weakRecoveredSampleCount = 3,
                weakDropoutSampleCount = 0,
                weakDropoutCandidateLossSampleCount = 0,
                weakDropoutFinalAudibleSampleCount = 0,
                weakDropoutFinalAudiblePct = 100.0,
                hotMakeupSampleCount = 0
            },
            nr5LowEvidenceLiftWatch = new
            {
                liftedSampleCount = lowEvidenceLiftedSampleCount,
                liftedPct = lowEvidenceLiftedPct,
                alignmentMismatchPct = 0.0
            },
            nr5AudioAlignmentWatch = new
            {
                mismatchPct = audioAlignmentMismatchPct
            },
            nr5SignalProbability = new { average = signalProbabilityAverage },
            nr5TextureFill = new { average = textureFillAverage },
            nr5OutputDbfs = new { movement = 0.1 },
            nr5MakeupGainDb = new { movement = 0.1, max = 1.0 },
            nr5RecoveryDrive = new { movement = 0.01 },
            nr5PeakReductionDb = new { max = 0.0 },
            nr5OutputPeakDbfs = new { max = -10.0 },
            audioRmsDbfs = new { movement = 0.2 },
            audioPeakDbfs = new { max = -9.0 },
            rxAudioLevelerOutputRmsDbfs = new { movement = 0.2 },
            rxAudioLevelerAppliedGainDb = new { movement = 0.0 },
            rxAudioLevelerWatch = new
            {
                diagnosticSampleCount = 3,
                boostSlewLimitedSampleCount = 0,
                peakLimitedSampleCount = 0,
                outputLimitedSampleCount = 0
            },
            rxAudioLevelerOutputLimitReductionDb = new { max = 0.0 },
            rxAudioLevelerOutputLimitSampleCount = new { max = 0.0 },
            adcHeadroomDb = new { min = 22.0 },
            monitorBacklogSamples = new { max = 0.0 },
            latencyMs = new { average = 1.0 }
        };
    }

    private static async Task WriteAgcWatchJsonlAsync(string path, IEnumerable<object> samples)
    {
        var jsonlOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
        await File.WriteAllTextAsync(
            path,
            string.Join(Environment.NewLine, samples.Select(sample => JsonSerializer.Serialize(sample, jsonlOptions)))
                + Environment.NewLine);
    }

    private static object AgcWatchSample(
        int sampleIndex,
        double agcGainDb,
        double audioRmsDbfs,
        bool includeNr5 = false,
        double nr5InputDbfs = -24.0,
        double signalConfidence = 0.72,
        double agcGate = 0.74,
        double signalProbability = 0.68,
        double textureFill = 0.04,
        double? nr5OutputDbfs = null)
    {
        return new
        {
            sampleIndex,
            sampledUtc = DateTimeOffset.UtcNow.AddMilliseconds(sampleIndex * 250),
            ok = true,
            diagnostics = new
            {
                schemaVersion = 1,
                status = "ready-for-live-benchmark",
                qualityTone = "ready",
                readinessScore = 92,
                readyForLiveBenchmark = true,
                readyForNr5Tuning = includeNr5,
                nr5TuningStatus = includeNr5 ? "ready-for-nr5-live-tuning" : "nr5-diagnostics-missing",
                nr5TuningConstraints = Array.Empty<string>(),
                requestedNrMode = includeNr5 ? "Nr5" : "Off",
                effectiveNrMode = includeNr5 ? "Nr5" : "Off",
                constraints = Array.Empty<string>(),
                recommendedActions = Array.Empty<string>(),
                runtimeEvidence = new
                {
                    status = "ready",
                    audioStatus = "ready",
                    rxMetersFresh = true,
                    audioFresh = true,
                    agcGainDb,
                    audioRmsDbfs,
                    audioPeakDbfs = -9.0,
                    adcHeadroomDb = 22.0,
                    monitorBacklogSamples = 0,
                    txMonitorRequested = false,
                    squelchEnabled = false,
                    squelchOpen = true,
                    squelchTailActive = false,
                    rxAudioLevelerInputRmsDbfs = audioRmsDbfs,
                    rxAudioLevelerOutputRmsDbfs = audioRmsDbfs,
                    rxAudioLevelerAppliedGainDb = 0.0
                },
                nr5SpnrDiagnostics = includeNr5
                    ? new
                    {
                        run = true,
                        agcRun = true,
                        learnedFrames = 30,
                        inputDbfs = nr5InputDbfs,
                        outputDbfs = nr5OutputDbfs ?? audioRmsDbfs,
                        meanGain = 0.98,
                        floorReductionDb = 8.0,
                        dynamicRangeDb = 12.0,
                        signalConfidence,
                        agcGate,
                        signalProbability,
                        textureFill,
                        maskSmoothing = 0.36,
                        levelDrive = 0.25,
                        recoveryDrive = 0.20,
                        weakSignalMemory = 0.55,
                        makeupGainDb = 1.0,
                        outputPeakDbfs = -10.0,
                        peakEvidence = 0.80,
                        peakLimitDbfs = -3.0,
                        peakReductionDb = 0.0
                    }
                    : null
            }
        };
    }

    private static object Nr5LevelerAlignmentWatchSample(
        int sampleIndex,
        double nr5InputDbfs,
        double nr5OutputDbfs,
        double levelerInputRmsDbfs,
        double levelerOutputRmsDbfs)
    {
        return new
        {
            sampleIndex,
            sampledUtc = DateTimeOffset.UtcNow.AddMilliseconds(sampleIndex * 250),
            ok = true,
            diagnostics = new
            {
                schemaVersion = 1,
                status = "ready-for-live-benchmark",
                qualityTone = "ready",
                readinessScore = 92,
                readyForLiveBenchmark = true,
                readyForNr5Tuning = true,
                nr5TuningStatus = "ready-for-nr5-live-tuning",
                nr5TuningConstraints = Array.Empty<string>(),
                requestedNrMode = "Nr5",
                effectiveNrMode = "Nr5",
                constraints = Array.Empty<string>(),
                recommendedActions = Array.Empty<string>(),
                runtimeEvidence = new
                {
                    status = "ready",
                    audioStatus = "ready",
                    rxMetersFresh = true,
                    audioFresh = true,
                    agcGainDb = -42.5,
                    audioRmsDbfs = levelerOutputRmsDbfs,
                    audioPeakDbfs = levelerOutputRmsDbfs + 4.0,
                    adcHeadroomDb = 48.0,
                    monitorBacklogSamples = 0,
                    txMonitorRequested = false,
                    squelchEnabled = false,
                    squelchOpen = true,
                    squelchTailActive = false,
                    rxAudioLevelerInputRmsDbfs = levelerInputRmsDbfs,
                    rxAudioLevelerOutputRmsDbfs = levelerOutputRmsDbfs,
                    rxAudioLevelerAppliedGainDb = Math.Round(levelerOutputRmsDbfs - levelerInputRmsDbfs, 1),
                    rxAudioLevelerBoostSlewLimited = false,
                    rxAudioLevelerPeakLimited = false,
                    rxAudioLevelerOutputLimited = false
                },
                nr5SpnrDiagnostics = new
                {
                    run = true,
                    agcRun = true,
                    learnedFrames = 30,
                    inputDbfs = nr5InputDbfs,
                    outputDbfs = nr5OutputDbfs,
                    meanGain = 0.16,
                    floorReductionDb = 5.2,
                    dynamicRangeDb = 54.2,
                    signalConfidence = 0.59,
                    agcGate = 0.96,
                    signalProbability = 0.76,
                    textureFill = 0.0,
                    maskSmoothing = 0.0,
                    levelDrive = 1.0,
                    recoveryDrive = 1.0,
                    weakSignalMemory = 0.50,
                    makeupGainDb = 0.2,
                    outputPeakDbfs = nr5OutputDbfs + 4.0,
                    peakEvidence = 1.0,
                    peakLimitDbfs = -2.6,
                    peakReductionDb = 0.0
                }
            }
        };
    }

    private static void AssertTraceHasSafetySignal(JsonElement trace, string name, string safetyClass)
    {
        Assert.Contains(
            trace.GetProperty("safetySignals").EnumerateArray(),
            signal => signal.GetProperty("name").GetString() == name
                && signal.GetProperty("safetyClass").GetString() == safetyClass);
    }

    private static void AssertMetricRegression(JsonElement[] metrics, string metricId, string safetyClass)
    {
        var metric = metrics.Single(item => item.GetProperty("metricId").GetString() == metricId);
        Assert.Equal("regression", metric.GetProperty("verdict").GetString());
        Assert.Equal(safetyClass, metric.GetProperty("safetyClass").GetString());
    }

    private static void RemoveJsonProperty(string path, string propertyName)
    {
        var node = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw new InvalidOperationException($"Could not parse JSON object at {path}.");
        node.Remove(propertyName);
        File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void RemoveRequiredArtifactById(string path, string artifactId)
    {
        var node = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw new InvalidOperationException($"Could not parse JSON object at {path}.");
        var artifacts = node["requiredArtifacts"]?.AsArray()
            ?? throw new InvalidOperationException($"Could not find requiredArtifacts array at {path}.");

        for (var index = artifacts.Count - 1; index >= 0; index--)
        {
            var artifact = artifacts[index]?.AsObject();
            var id = artifact?["id"]?.GetValue<string>();
            if (string.Equals(id, artifactId, StringComparison.Ordinal))
            {
                artifacts.RemoveAt(index);
            }
        }

        File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha256 = SHA256.Create();
        return Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
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

    private static void AssertLiveArtifact(JsonElement[] artifacts, string path, string id, string comparisonId, bool required = false)
    {
        var artifact = artifacts.Single(item => item.GetProperty("path").GetString() == path);
        Assert.Equal(id, artifact.GetProperty("id").GetString());
        Assert.Equal(required, artifact.GetProperty("required").GetBoolean());
        Assert.Equal(new[] { comparisonId }, ReadStringArray(artifact, "comparisonIds"));
    }

    private static string[] ReadStringArray(JsonElement element, string propertyName)
    {
        return element.GetProperty(propertyName)
            .EnumerateArray()
            .Select(value => value.GetString())
            .Where(value => value is not null)
            .Cast<string>()
            .ToArray();
    }

    private sealed record ToolResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public string CombinedOutput => $"{StandardOutput}{Environment.NewLine}{StandardError}";
    }
}
