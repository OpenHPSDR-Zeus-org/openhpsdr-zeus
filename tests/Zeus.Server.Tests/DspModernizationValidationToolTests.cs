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

            var artifacts = manifestRoot.GetProperty("artifacts").EnumerateArray().ToArray();
            var bakeoffArtifact = artifacts
                .Single(artifact => artifact.GetProperty("id").GetString() == "external-engine-bakeoff-report");

            Assert.True(bakeoffArtifact.GetProperty("required").GetBoolean());
            Assert.Equal("external-candidate-report-json", bakeoffArtifact.GetProperty("kind").GetString());
            Assert.Equal("artifacts/external-engine-bakeoff-report.json", bakeoffArtifact.GetProperty("path").GetString());

            var comparisonIds = bakeoffArtifact.GetProperty("comparisonIds")
                .EnumerateArray()
                .Select(value => value.GetString())
                .ToArray();
            Assert.Contains("candidate-external-engine-opt-in", comparisonIds);
            AssertExternalBakeoffCycleSummaryArtifact(artifacts);

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
    public async Task ExternalEngineBakeoffReportRanksOptInCandidatesBySafety()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell external-engine smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-dsp-external-bakeoff-order-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            WriteExternalBakeoffEndpointBundle(bundleDir);
            WriteExternalBakeoffArtifactManifest(bundleDir);

            var reportPath = Path.Combine(bundleDir, "artifacts", "external-engine-bakeoff-report.json");
            var generated = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "summarize-dsp-external-engine-candidates.ps1"),
                "-BundleDir", bundleDir,
                "-ReportPath", reportPath,
                "-NoMarkdown",
                "-JsonOnly");

            Assert.True(generated.ExitCode == 0, generated.CombinedOutput);
            Assert.True(File.Exists(reportPath), generated.CombinedOutput);

            using var reportDoc = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
            var reportRoot = reportDoc.RootElement;
            Assert.Equal(4, reportRoot.GetProperty("schemaVersion").GetInt32());
            Assert.True(reportRoot.GetProperty("readyForReview").GetBoolean());
            Assert.Equal("speexdsp", reportRoot.GetProperty("firstSafeBakeoffCandidateId").GetString());

            var orderIds = ReadStringArray(reportRoot, "externalBakeoffEvaluationOrderCandidateIds");
            Assert.Equal(new[] { "speexdsp", "rnnoise", "webrtc-apm", "deepfilternet" }, orderIds);

            var orderRecords = reportRoot.GetProperty("externalBakeoffEvaluationOrder").EnumerateArray().ToArray();
            Assert.Equal(orderIds.Length, orderRecords.Length);
            Assert.Equal(1, orderRecords[0].GetProperty("priority").GetInt32());
            Assert.Equal("speexdsp", orderRecords[0].GetProperty("candidateId").GetString());
            Assert.True(orderRecords[0].GetProperty("readyForBakeoff").GetBoolean());

            var plan = reportRoot.GetProperty("externalBakeoffPlan");
            Assert.False(plan.GetProperty("defaultBehaviorChangeReady").GetBoolean());
            Assert.False(plan.GetProperty("rawWdspIqReplacementAllowed").GetBoolean());
            Assert.False(plan.GetProperty("txPathAllowed").GetBoolean());
            Assert.Equal("speexdsp", plan.GetProperty("firstSafeBakeoffCandidateId").GetString());
            Assert.Equal(orderIds, ReadStringArray(plan, "evaluationOrderCandidateIds"));

            var validationReport = Path.Combine(bundleDir, "validation-report.json");
            var validation = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "validate-dsp-modernization-bundle.ps1"),
                "-BundleDir", bundleDir,
                "-ReportPath", validationReport,
                "-AllowPreflight",
                "-JsonOnly");

            Assert.True(File.Exists(validationReport), validation.CombinedOutput);

            using var validationDoc = JsonDocument.Parse(await File.ReadAllTextAsync(validationReport));
            var validationRoot = validationDoc.RootElement;
            Assert.True(validationRoot.GetProperty("externalEngineBakeoffReportPresent").GetBoolean());
            Assert.True(validationRoot.GetProperty("externalEngineBakeoffReady").GetBoolean());
            Assert.Equal("speexdsp", validationRoot.GetProperty("externalEngineBakeoffFirstSafeCandidateId").GetString());
            Assert.Equal(orderIds, ReadStringArray(validationRoot, "externalEngineBakeoffEvaluationOrderCandidateIds"));

            var errorCodes = validationRoot
                .GetProperty("errors")
                .EnumerateArray()
                .Select(error => error.GetProperty("code").GetString())
                .ToArray();
            Assert.DoesNotContain("external-bakeoff-evaluation-order-mismatch", errorCodes);
            Assert.DoesNotContain("external-bakeoff-evaluation-order-record-mismatch", errorCodes);

            var triageReport = Path.Combine(bundleDir, "validation-triage-report.json");
            var triageMarkdown = Path.Combine(bundleDir, "validation-triage-report.md");
            var triage = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "summarize-dsp-modernization-validation-report.ps1"),
                "-ValidationReportPath", validationReport,
                "-ReportPath", triageReport,
                "-MarkdownPath", triageMarkdown,
                "-JsonOnly");

            Assert.Equal(0, triage.ExitCode);
            Assert.True(File.Exists(triageReport), triage.CombinedOutput);
            Assert.True(File.Exists(triageMarkdown), triage.CombinedOutput);

            using var triageDoc = JsonDocument.Parse(await File.ReadAllTextAsync(triageReport));
            var triageRoot = triageDoc.RootElement;
            Assert.Equal("speexdsp", triageRoot.GetProperty("externalEngineBakeoffFirstSafeCandidateId").GetString());
            Assert.Contains("ssb-like-speech-post-demod", ReadStringArray(triageRoot, "externalEngineBakeoffFirstSafeScenarioIds"));

            var firstSafeAction = triageRoot.GetProperty("acceptanceActionPlan")
                .EnumerateArray()
                .Single(action => action.GetProperty("actionId").GetString() == "run-first-safe-external-engine-bakeoff");
            Assert.False(firstSafeAction.GetProperty("requiredForAcceptance").GetBoolean());
            Assert.False(firstSafeAction.GetProperty("blocksDefaultBehaviorChange").GetBoolean());
            Assert.Equal("external-dsp-ml", firstSafeAction.GetProperty("category").GetString());
            Assert.Equal("external-engine-bakeoff-cycle", firstSafeAction.GetProperty("gateId").GetString());
            var firstSafeSteps = ReadStringArray(firstSafeAction, "commandSteps");
            Assert.Single(firstSafeSteps);
            Assert.Contains("run-dsp-external-engine-bakeoff.ps1", firstSafeSteps[0], StringComparison.Ordinal);
            Assert.Contains("-CandidateId speexdsp", firstSafeSteps[0], StringComparison.Ordinal);
            Assert.Contains("-ScenarioIds ssb-like-speech-post-demod agc-disabled-no-pumping noise-only-gating", firstSafeSteps[0], StringComparison.Ordinal);
            Assert.Contains("-PlanOnly", firstSafeSteps[0], StringComparison.Ordinal);
            Assert.Equal("artifacts/external-engine-bakeoff-cycle-summary.json", firstSafeAction.GetProperty("expectedArtifact").GetString());
            Assert.Equal(new[]
            {
                "artifacts/external-engine-bakeoff-cycle-summary.json",
                "artifacts/external-engine-bakeoff-cycle-summary.md"
            }, ReadStringArray(firstSafeAction, "expectedArtifacts"));
            Assert.Contains("Start with -PlanOnly", firstSafeAction.GetProperty("manualAction").GetString(), StringComparison.Ordinal);

            var cycleGate = triageRoot.GetProperty("evidenceGates")
                .EnumerateArray()
                .Single(gate => gate.GetProperty("gateId").GetString() == "external-engine-bakeoff-cycle");
            Assert.False(cycleGate.GetProperty("ready").GetBoolean());
            Assert.False(cycleGate.GetProperty("requiredForAcceptance").GetBoolean());
            Assert.Equal("not-captured", cycleGate.GetProperty("status").GetString());
            Assert.Contains("present=False", cycleGate.GetProperty("detail").GetString(), StringComparison.Ordinal);
            Assert.Contains("run-dsp-external-engine-bakeoff.ps1", cycleGate.GetProperty("remediation").GetString(), StringComparison.Ordinal);
            Assert.Contains("external-engine-bakeoff-cycle", ReadStringArray(triageRoot, "evidenceGateProblemIds"));
            Assert.DoesNotContain("external-engine-bakeoff-cycle", ReadStringArray(triageRoot, "requiredEvidenceGateProblemIds"));
            Assert.Contains("external-engine-bakeoff-cycle", ReadStringArray(triageRoot, "advisoryEvidenceGateProblemIds"));
            Assert.True(triageRoot.GetProperty("advisoryEvidenceGateProblemCount").GetInt32() > 0);
            Assert.Contains(
                triageRoot.GetProperty("recommendations").EnumerateArray(),
                recommendation => (recommendation.GetString() ?? "").Contains("advisory evidence gates", StringComparison.Ordinal));

            var markdown = await File.ReadAllTextAsync(triageMarkdown);
            Assert.Contains("run-first-safe-external-engine-bakeoff", markdown, StringComparison.Ordinal);
            Assert.Contains("Bakeoff first safe scenarios", markdown, StringComparison.Ordinal);
            Assert.Contains("Advisory problem gates", markdown, StringComparison.Ordinal);
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
    public async Task ExternalEngineBakeoffValidationRejectsHandEditedEvaluationOrder()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell external-engine smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-dsp-external-bakeoff-order-mutated-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            WriteExternalBakeoffEndpointBundle(bundleDir);
            WriteExternalBakeoffArtifactManifest(bundleDir);

            var reportPath = Path.Combine(bundleDir, "artifacts", "external-engine-bakeoff-report.json");
            var generated = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "summarize-dsp-external-engine-candidates.ps1"),
                "-BundleDir", bundleDir,
                "-ReportPath", reportPath,
                "-NoMarkdown",
                "-JsonOnly");

            Assert.True(generated.ExitCode == 0, generated.CombinedOutput);

            var reportNode = JsonNode.Parse(await File.ReadAllTextAsync(reportPath))?.AsObject()
                ?? throw new InvalidOperationException("Could not parse external bakeoff report.");
            var orderArray = reportNode["externalBakeoffEvaluationOrderCandidateIds"]?.AsArray()
                ?? throw new InvalidOperationException("Could not read external bakeoff order IDs.");
            var orderIds = orderArray
                .Select(node => node?.GetValue<string>() ?? "")
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Reverse()
                .ToArray();

            var mutatedOrder = new JsonArray();
            foreach (var candidateId in orderIds)
            {
                mutatedOrder.Add(candidateId);
            }
            reportNode["externalBakeoffEvaluationOrderCandidateIds"] = mutatedOrder;
            await File.WriteAllTextAsync(reportPath, reportNode.ToJsonString(CamelCaseJson));

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
            var errorCodes = validationDoc.RootElement
                .GetProperty("errors")
                .EnumerateArray()
                .Select(error => error.GetProperty("code").GetString())
                .ToArray();
            Assert.Contains("external-bakeoff-evaluation-order-mismatch", errorCodes);
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
    public async Task ExternalEngineBakeoffRunnerPlanOnlyBuildsSafeFirstCandidatePlan()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell external-engine smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-dsp-external-bakeoff-runner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            var artifactsDir = Path.Combine(bundleDir, "artifacts");
            Directory.CreateDirectory(artifactsDir);

            await File.WriteAllTextAsync(
                Path.Combine(artifactsDir, "current-zeus-fixtures.json"),
                "{}");
            await File.WriteAllTextAsync(
                Path.Combine(artifactsDir, "external-engine.speexdsp.fixtures.json"),
                "{}");

            var scenarioIds = new[] { "ssb-like-speech-post-demod", "agc-disabled-no-pumping", "noise-only-gating" };
            await File.WriteAllTextAsync(
                Path.Combine(bundleDir, "validation-report.json"),
                JsonSerializer.Serialize(new
                {
                    externalEngineBakeoffReady = true,
                    externalEngineBakeoffFirstSafeCandidateId = "speexdsp",
                    externalEngineBakeoffFirstSafeScenarioIds = scenarioIds,
                    externalEngineBakeoffPlanScenarioIds = scenarioIds,
                    externalEngineBakeoffPlanDefaultBehaviorChangeReady = false,
                    externalEngineBakeoffPlanRawWdspIqReplacementAllowed = false
                }, CamelCaseJson));

            var commandSteps = new[]
            {
                "powershell -NoProfile -ExecutionPolicy Bypass -File tools\\compare-dsp-fixture-metrics.ps1 -BaselinePath \"$bundleDir\\artifacts\\current-zeus-fixtures.json\" -CandidatePath \"$bundleDir\\artifacts\\external-engine.speexdsp.fixtures.json\" -CandidateComparisonId candidate-external-engine-opt-in -FailOnRegression",
                "powershell -NoProfile -ExecutionPolicy Bypass -File tools\\run-dsp-live-diagnostics-matrix.ps1 -BundleDir \"$bundleDir\" -ComparisonId candidate-external-engine-opt-in -ScenarioIds ssb-like-speech-post-demod,agc-disabled-no-pumping,noise-only-gating -IndexPath \"$bundleDir\\artifacts\\live-diagnostics-trace-index.external-engine.speexdsp.json\" -ReportPath \"$bundleDir\\artifacts\\live-diagnostics-matrix-report.external-engine.speexdsp.json\" -Samples 60 -IntervalMs 1000 -ContinueOnError"
            };
            await File.WriteAllTextAsync(
                Path.Combine(bundleDir, "validation-triage-report.json"),
                JsonSerializer.Serialize(new
                {
                    acceptanceActionPlan = new object[]
                    {
                        new
                        {
                            actionId = "run-first-safe-external-engine-bakeoff",
                            priority = 72,
                            stageId = "external-dsp-ml-bakeoff",
                            gateId = "external-engine-bakeoff",
                            category = "external-dsp-ml",
                            requiredForAcceptance = false,
                            blocksDefaultChange = false,
                            commandTemplate = commandSteps[0],
                            commandStepCount = commandSteps.Length,
                            commandSteps,
                            manualAction = "Produce or enable only the post-demod, operator-opt-in 'speexdsp' candidate path before running these comparisons. Do not route raw WDSP IQ, TX audio, TX monitor, or PureSignal feedback through the external engine.",
                            expectedArtifact = "artifacts/dsp-fixture-metric-comparison.json",
                            expectedArtifactCount = 3,
                            expectedArtifacts = new[]
                            {
                                "artifacts/dsp-fixture-metric-comparison.json",
                                "artifacts/live-diagnostics-trace-index.external-engine.speexdsp.json",
                                "artifacts/live-diagnostics-matrix-report.external-engine.speexdsp.json"
                            },
                            followUp = "Treat this as exploratory opt-in evidence only."
                        }
                    }
                }, CamelCaseJson));

            var reportPath = Path.Combine(artifactsDir, "external-engine-bakeoff-cycle-summary.json");
            var markdownPath = Path.Combine(artifactsDir, "external-engine-bakeoff-cycle-summary.md");
            var plan = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "run-dsp-external-engine-bakeoff.ps1"),
                "-BundleDir", bundleDir,
                "-PlanOnly",
                "-JsonOnly");

            Assert.Equal(0, plan.ExitCode);
            Assert.True(File.Exists(reportPath), plan.CombinedOutput);
            Assert.True(File.Exists(markdownPath), plan.CombinedOutput);

            using var planDoc = JsonDocument.Parse(plan.StandardOutput);
            var root = planDoc.RootElement;
            Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("run-dsp-external-engine-bakeoff", root.GetProperty("tool").GetString());
            Assert.Equal("plan-only", root.GetProperty("mode").GetString());
            Assert.Equal("ready", root.GetProperty("status").GetString());
            Assert.Equal("speexdsp", root.GetProperty("candidateId").GetString());
            Assert.Equal("candidate-external-engine-opt-in", root.GetProperty("comparisonId").GetString());
            Assert.Equal(scenarioIds, ReadStringArray(root, "scenarioIds"));
            Assert.True(root.GetProperty("readyToExecute").GetBoolean());
            Assert.False(root.GetProperty("executed").GetBoolean());
            Assert.Equal(0, root.GetProperty("missingPrerequisiteCount").GetInt32());

            var safety = root.GetProperty("safetyPolicy");
            Assert.True(safety.GetProperty("optInOnly").GetBoolean());
            Assert.True(safety.GetProperty("postDemodOnly").GetBoolean());
            Assert.True(safety.GetProperty("rxOnly").GetBoolean());
            Assert.False(safety.GetProperty("rawWdspIqAllowed").GetBoolean());
            Assert.False(safety.GetProperty("txPathAllowed").GetBoolean());
            Assert.False(safety.GetProperty("pureSignalAllowed").GetBoolean());

            var steps = ReadStringArray(root, "commandSteps");
            Assert.Equal(4, steps.Length);
            Assert.Contains(steps, step => step.Contains("compare-dsp-fixture-metrics.ps1", StringComparison.Ordinal)
                && step.Contains("external-engine.speexdsp.fixtures.json", StringComparison.Ordinal));
            Assert.Contains(steps, step => step.Contains("run-dsp-live-diagnostics-matrix.ps1", StringComparison.Ordinal)
                && step.Contains("-ComparisonId candidate-external-engine-opt-in", StringComparison.Ordinal)
                && step.Contains("-ScenarioIds ssb-like-speech-post-demod agc-disabled-no-pumping noise-only-gating", StringComparison.Ordinal));
            Assert.Contains(steps, step => step.Contains("validate-dsp-modernization-bundle.ps1", StringComparison.Ordinal));
            Assert.Contains(steps, step => step.Contains("summarize-dsp-modernization-validation-report.ps1", StringComparison.Ordinal));

            var artifacts = ReadStringArray(root, "expectedArtifacts");
            Assert.Contains("artifacts/dsp-fixture-metric-comparison.json", artifacts);
            Assert.Contains("artifacts/live-diagnostics-trace-index.external-engine.speexdsp.json", artifacts);
            Assert.Contains("artifacts/live-diagnostics-matrix-report.external-engine.speexdsp.json", artifacts);
            Assert.Contains("artifacts/external-engine-bakeoff-cycle-summary.json", artifacts);

            var markdown = await File.ReadAllTextAsync(markdownPath);
            Assert.Contains("External DSP/ML Bakeoff Cycle", markdown, StringComparison.Ordinal);
            Assert.Contains("post-demod", markdown, StringComparison.Ordinal);
            Assert.Contains("run-dsp-live-diagnostics-matrix.ps1", markdown, StringComparison.Ordinal);
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
    public async Task ExternalEngineBakeoffCycleSummaryArtifactIsAcceptedAndCopiedByValidationSummary()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell external-engine smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-dsp-external-bakeoff-cycle-validated-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            WriteExternalBakeoffEndpointBundle(bundleDir);
            WriteExternalBakeoffArtifactManifest(bundleDir, includeCycleSummary: true);

            var artifactsDir = Path.Combine(bundleDir, "artifacts");
            Directory.CreateDirectory(artifactsDir);
            await File.WriteAllTextAsync(Path.Combine(artifactsDir, "current-zeus-fixtures.json"), "{}");
            await File.WriteAllTextAsync(Path.Combine(artifactsDir, "external-engine.speexdsp.fixtures.json"), "{}");

            var bakeoffReportPath = Path.Combine(artifactsDir, "external-engine-bakeoff-report.json");
            var generated = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "summarize-dsp-external-engine-candidates.ps1"),
                "-BundleDir", bundleDir,
                "-ReportPath", bakeoffReportPath,
                "-NoMarkdown",
                "-JsonOnly");

            Assert.Equal(0, generated.ExitCode);
            Assert.True(File.Exists(bakeoffReportPath), generated.CombinedOutput);

            var validationReport = Path.Combine(bundleDir, "validation-report.json");
            var validation = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "validate-dsp-modernization-bundle.ps1"),
                "-BundleDir", bundleDir,
                "-ReportPath", validationReport,
                "-AllowPreflight",
                "-JsonOnly");

            Assert.True(File.Exists(validationReport), validation.CombinedOutput);

            var triageReport = Path.Combine(bundleDir, "validation-triage-report.json");
            var triageMarkdown = Path.Combine(bundleDir, "validation-triage-report.md");
            var triage = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "summarize-dsp-modernization-validation-report.ps1"),
                "-ValidationReportPath", validationReport,
                "-ReportPath", triageReport,
                "-MarkdownPath", triageMarkdown,
                "-JsonOnly");

            Assert.Equal(0, triage.ExitCode);
            Assert.True(File.Exists(triageReport), triage.CombinedOutput);

            var runner = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "run-dsp-external-engine-bakeoff.ps1"),
                "-BundleDir", bundleDir,
                "-PlanOnly",
                "-JsonOnly");

            Assert.Equal(0, runner.ExitCode);
            Assert.True(File.Exists(Path.Combine(artifactsDir, "external-engine-bakeoff-cycle-summary.json")), runner.CombinedOutput);

            validation = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "validate-dsp-modernization-bundle.ps1"),
                "-BundleDir", bundleDir,
                "-ReportPath", validationReport,
                "-AllowPreflight",
                "-JsonOnly");

            Assert.True(File.Exists(validationReport), validation.CombinedOutput);

            using var validationDoc = JsonDocument.Parse(await File.ReadAllTextAsync(validationReport));
            var validationRoot = validationDoc.RootElement;
            Assert.True(validationRoot.GetProperty("externalEngineBakeoffCycleSummaryPresent").GetBoolean());
            Assert.True(validationRoot.GetProperty("externalEngineBakeoffCycleSummaryValid").GetBoolean());
            Assert.Equal("ready", validationRoot.GetProperty("externalEngineBakeoffCycleStatus").GetString());
            Assert.Equal("plan-only", validationRoot.GetProperty("externalEngineBakeoffCycleMode").GetString());
            Assert.Equal("speexdsp", validationRoot.GetProperty("externalEngineBakeoffCycleCandidateId").GetString());
            Assert.Equal("candidate-external-engine-opt-in", validationRoot.GetProperty("externalEngineBakeoffCycleComparisonId").GetString());
            Assert.Equal(new[] { "ssb-like-speech-post-demod", "agc-disabled-no-pumping", "noise-only-gating" }, ReadStringArray(validationRoot, "externalEngineBakeoffCycleScenarioIds"));
            Assert.True(validationRoot.GetProperty("externalEngineBakeoffCycleReadyToExecute").GetBoolean());
            Assert.False(validationRoot.GetProperty("externalEngineBakeoffCycleExecuted").GetBoolean());
            Assert.Equal(0, validationRoot.GetProperty("externalEngineBakeoffCycleMissingPrerequisiteCount").GetInt32());
            Assert.Equal(4, validationRoot.GetProperty("externalEngineBakeoffCycleCommandStepCount").GetInt32());
            Assert.Equal(8, validationRoot.GetProperty("externalEngineBakeoffCycleExpectedArtifactCount").GetInt32());
            Assert.Equal(0, validationRoot.GetProperty("externalEngineBakeoffCycleNonZeroExitCount").GetInt32());
            Assert.True(validationRoot.GetProperty("externalEngineBakeoffCycleSourceExternalBakeoffReady").GetBoolean());
            Assert.True(validationRoot.GetProperty("externalEngineBakeoffCycleSourceExternalBakeoffActionPresent").GetBoolean());
            Assert.Equal("artifacts/external-engine-bakeoff-cycle-summary.json", validationRoot.GetProperty("externalEngineBakeoffCyclePath").GetString());

            var cycleArtifact = validationRoot
                .GetProperty("artifactFiles")
                .EnumerateArray()
                .Single(artifact => artifact.GetProperty("id").GetString() == "external-engine-bakeoff-cycle-summary");
            Assert.True(cycleArtifact.GetProperty("ok").GetBoolean());
            Assert.False(cycleArtifact.GetProperty("required").GetBoolean());
            Assert.Equal("external-engine-bakeoff-cycle-summary-json", cycleArtifact.GetProperty("kind").GetString());
            Assert.Equal("artifacts/external-engine-bakeoff-cycle-summary.json", cycleArtifact.GetProperty("path").GetString());
            Assert.Equal(new[] { "candidate-external-engine-opt-in" }, ReadStringArray(cycleArtifact, "comparisonIds"));

            var unexpectedArtifactWarnings = validationRoot
                .GetProperty("warnings")
                .EnumerateArray()
                .Where(issue => issue.GetProperty("code").GetString() == "artifact-not-in-capture-manifest")
                .Select(issue => issue.GetProperty("message").GetString() ?? "")
                .ToArray();
            Assert.DoesNotContain(unexpectedArtifactWarnings, message => message.Contains("external-engine-bakeoff-cycle-summary", StringComparison.Ordinal));

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
            Assert.True(summaryRoot.GetProperty("externalEngineBakeoffCycleSummaryPresent").GetBoolean());
            Assert.True(summaryRoot.GetProperty("externalEngineBakeoffCycleSummaryValid").GetBoolean());
            Assert.Equal("ready", summaryRoot.GetProperty("externalEngineBakeoffCycleStatus").GetString());
            Assert.Equal("plan-only", summaryRoot.GetProperty("externalEngineBakeoffCycleMode").GetString());
            Assert.Equal("speexdsp", summaryRoot.GetProperty("externalEngineBakeoffCycleCandidateId").GetString());
            Assert.Equal("candidate-external-engine-opt-in", summaryRoot.GetProperty("externalEngineBakeoffCycleComparisonId").GetString());
            Assert.Equal(new[] { "ssb-like-speech-post-demod", "agc-disabled-no-pumping", "noise-only-gating" }, ReadStringArray(summaryRoot, "externalEngineBakeoffCycleScenarioIds"));
            Assert.True(summaryRoot.GetProperty("externalEngineBakeoffCycleReadyToExecute").GetBoolean());
            Assert.False(summaryRoot.GetProperty("externalEngineBakeoffCycleExecuted").GetBoolean());
            Assert.Equal(0, summaryRoot.GetProperty("externalEngineBakeoffCycleMissingPrerequisiteCount").GetInt32());
            Assert.Equal(4, summaryRoot.GetProperty("externalEngineBakeoffCycleCommandStepCount").GetInt32());
            Assert.Equal(8, summaryRoot.GetProperty("externalEngineBakeoffCycleExpectedArtifactCount").GetInt32());
            Assert.Equal(0, summaryRoot.GetProperty("externalEngineBakeoffCycleNonZeroExitCount").GetInt32());
            Assert.True(summaryRoot.GetProperty("externalEngineBakeoffCycleSourceExternalBakeoffReady").GetBoolean());
            Assert.True(summaryRoot.GetProperty("externalEngineBakeoffCycleSourceExternalBakeoffActionPresent").GetBoolean());
            Assert.Equal("artifacts/external-engine-bakeoff-cycle-summary.json", summaryRoot.GetProperty("externalEngineBakeoffCyclePath").GetString());

            var cycleGate = summaryRoot.GetProperty("evidenceGates")
                .EnumerateArray()
                .Single(gate => gate.GetProperty("gateId").GetString() == "external-engine-bakeoff-cycle");
            Assert.True(cycleGate.GetProperty("ready").GetBoolean());
            Assert.False(cycleGate.GetProperty("requiredForAcceptance").GetBoolean());
            Assert.Equal("ready", cycleGate.GetProperty("status").GetString());
            Assert.Contains("present=True", cycleGate.GetProperty("detail").GetString(), StringComparison.Ordinal);
            Assert.Contains("candidate=speexdsp", cycleGate.GetProperty("detail").GetString(), StringComparison.Ordinal);
            Assert.DoesNotContain("external-engine-bakeoff-cycle", ReadStringArray(summaryRoot, "evidenceGateProblemIds"));
            Assert.DoesNotContain("external-engine-bakeoff-cycle", ReadStringArray(summaryRoot, "advisoryEvidenceGateProblemIds"));

            var markdown = await File.ReadAllTextAsync(summaryMarkdown);
            Assert.Contains("Bakeoff cycle summary: status ready", markdown, StringComparison.Ordinal);
            Assert.Contains("candidate speexdsp", markdown, StringComparison.Ordinal);
            Assert.Contains("Advisory problem gates", markdown, StringComparison.Ordinal);
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

            var artifacts = manifestRoot.GetProperty("artifacts").EnumerateArray().ToArray();
            var bakeoffArtifact = artifacts
                .Single(artifact => artifact.GetProperty("id").GetString() == "external-engine-bakeoff-report");
            Assert.True(bakeoffArtifact.GetProperty("required").GetBoolean());
            AssertExternalBakeoffCycleSummaryArtifact(artifacts);
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
    public async Task SourceBenchmarkPlanRecognizesCanonicalPureSignalSafeBypassScenario()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell validator smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-dsp-plan-puresignal-family-{Guid.NewGuid():N}");
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

            Assert.True(File.Exists(validationReport), validation.CombinedOutput);

            using var validationDoc = JsonDocument.Parse(await File.ReadAllTextAsync(validationReport));
            var validationRoot = validationDoc.RootElement;
            Assert.Contains("tx-puresignal-safe-bypass", ReadStringArray(validationRoot, "benchmarkPlanScenarioIds"));
            Assert.DoesNotContain("puresignal-safe-bypass", ReadStringArray(validationRoot, "benchmarkPlanMissingAcceptanceScenarioFamilyIds"));
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
    public async Task ArtifactManifestScaffoldIncludesPureSignalSafeBypassReportForTxScope()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell artifact scaffold smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-dsp-artifact-puresignal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            WritePureSignalScopeBundle(bundleDir);

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
            var artifacts = manifestDoc.RootElement.GetProperty("artifacts").EnumerateArray().ToArray();
            var pureSignalArtifact = artifacts.Single(artifact => artifact.GetProperty("id").GetString() == "puresignal-safe-bypass-report");
            Assert.True(pureSignalArtifact.GetProperty("required").GetBoolean());
            Assert.Equal("puresignal-safe-bypass-report-json", pureSignalArtifact.GetProperty("kind").GetString());
            Assert.Equal("tools/summarize-dsp-puresignal-bench.ps1", pureSignalArtifact.GetProperty("source").GetString());
            Assert.Equal("artifacts/puresignal-safe-bypass-report.json", pureSignalArtifact.GetProperty("path").GetString());
            Assert.Equal(new[] { "tx-puresignal-safe-bypass" }, ReadStringArray(pureSignalArtifact, "scenarioIds"));
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
    public async Task PureSignalBenchSummaryAcceptsDisabledAndEnabledSafeBypassReport()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell PureSignal bench smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-dsp-puresignal-ready-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            WritePureSignalScopeBundle(bundleDir);
            WritePureSignalArtifactManifest(bundleDir);
            WritePureSignalTrace(bundleDir, "artifacts/puresignal-disabled.json", enabled: false, feedbackStability: 0.991, txMonitorCoupling: 0.0, clippingCount: 0);
            WritePureSignalTrace(bundleDir, "artifacts/puresignal-enabled.json", enabled: true, feedbackStability: 0.986, txMonitorCoupling: 0.01, clippingCount: 0);

            var reportPath = Path.Combine(bundleDir, "artifacts", "puresignal-safe-bypass-report.json");
            var summary = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "summarize-dsp-puresignal-bench.ps1"),
                "-BundleDir", bundleDir,
                "-DisabledTracePath", "artifacts/puresignal-disabled.json",
                "-EnabledTracePath", "artifacts/puresignal-enabled.json",
                "-ReportPath", "artifacts/puresignal-safe-bypass-report.json",
                "-NoMarkdown",
                "-JsonOnly",
                "-Force");

            Assert.Equal(0, summary.ExitCode);
            Assert.True(File.Exists(reportPath), summary.CombinedOutput);

            using (var reportDoc = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath)))
            {
                var reportRoot = reportDoc.RootElement;
                Assert.True(reportRoot.GetProperty("readyForReview").GetBoolean());
                Assert.Equal("ready", reportRoot.GetProperty("status").GetString());
                Assert.True(reportRoot.GetProperty("disabledPathReady").GetBoolean());
                Assert.True(reportRoot.GetProperty("enabledPathReady").GetBoolean());
                Assert.Equal(2, reportRoot.GetProperty("capturedModeCount").GetInt32());
                Assert.Equal(0, reportRoot.GetProperty("missingModeCount").GetInt32());
                Assert.Equal(0, reportRoot.GetProperty("gateFailureCount").GetInt32());
                Assert.False(reportRoot.GetProperty("defaultBehaviorChangeApproved").GetBoolean());
            }

            var validationReport = Path.Combine(bundleDir, "validation-puresignal-ready.json");
            var validation = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "validate-dsp-modernization-bundle.ps1"),
                "-BundleDir", bundleDir,
                "-ArtifactManifestPath", Path.Combine(bundleDir, "artifact-manifest.json"),
                "-ReportPath", validationReport,
                "-AllowPreflight",
                "-JsonOnly");

            Assert.True(File.Exists(validationReport), validation.CombinedOutput);

            using var validationDoc = JsonDocument.Parse(await File.ReadAllTextAsync(validationReport));
            var validationRoot = validationDoc.RootElement;
            Assert.True(validationRoot.GetProperty("pureSignalSafeBypassReportPresent").GetBoolean());
            Assert.True(validationRoot.GetProperty("pureSignalSafeBypassReportReady").GetBoolean());
            Assert.Equal("ready", validationRoot.GetProperty("pureSignalSafeBypassReportStatus").GetString());
            Assert.True(validationRoot.GetProperty("pureSignalSafeBypassDisabledPathReady").GetBoolean());
            Assert.True(validationRoot.GetProperty("pureSignalSafeBypassEnabledPathReady").GetBoolean());
            Assert.Equal(2, validationRoot.GetProperty("pureSignalSafeBypassCapturedModeCount").GetInt32());
            Assert.Equal(0, validationRoot.GetProperty("pureSignalSafeBypassGateFailureCount").GetInt32());

            var pureSignalIssueCodes = validationRoot.GetProperty("errors")
                .EnumerateArray()
                .Select(issue => issue.GetProperty("code").GetString())
                .Where(code => code?.StartsWith("puresignal-safe-bypass-", StringComparison.Ordinal) == true)
                .ToArray();
            Assert.Empty(pureSignalIssueCodes);

            var summaryReport = Path.Combine(bundleDir, "summary-puresignal-ready.json");
            var triage = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "summarize-dsp-modernization-validation-report.ps1"),
                "-ValidationReportPath", validationReport,
                "-ReportPath", summaryReport,
                "-NoMarkdown",
                "-JsonOnly");

            Assert.Equal(0, triage.ExitCode);
            Assert.True(File.Exists(summaryReport), triage.CombinedOutput);

            using var summaryDoc = JsonDocument.Parse(await File.ReadAllTextAsync(summaryReport));
            var pureSignalGate = summaryDoc.RootElement.GetProperty("evidenceGates")
                .EnumerateArray()
                .Single(gate => gate.GetProperty("gateId").GetString() == "puresignal-safe-bypass");
            Assert.True(pureSignalGate.GetProperty("ready").GetBoolean());
            Assert.False(pureSignalGate.GetProperty("requiredForAcceptance").GetBoolean());
            Assert.DoesNotContain("puresignal-safe-bypass", ReadStringArray(summaryDoc.RootElement, "advisoryEvidenceGateProblemIds"));
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
    public async Task PureSignalBenchSummaryRejectsIncompleteSafeBypassReport()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell PureSignal bench smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-dsp-puresignal-incomplete-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            WritePureSignalScopeBundle(bundleDir);
            WritePureSignalArtifactManifest(bundleDir);
            WritePureSignalTrace(bundleDir, "artifacts/puresignal-disabled.json", enabled: false, feedbackStability: 0.99, txMonitorCoupling: 0.0, clippingCount: 0);

            var reportPath = Path.Combine(bundleDir, "artifacts", "puresignal-safe-bypass-report.json");
            var summary = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "summarize-dsp-puresignal-bench.ps1"),
                "-BundleDir", bundleDir,
                "-DisabledTracePath", "artifacts/puresignal-disabled.json",
                "-ReportPath", "artifacts/puresignal-safe-bypass-report.json",
                "-NoMarkdown",
                "-JsonOnly",
                "-Force");

            Assert.NotEqual(0, summary.ExitCode);
            Assert.True(File.Exists(reportPath), summary.CombinedOutput);

            using (var reportDoc = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath)))
            {
                var reportRoot = reportDoc.RootElement;
                Assert.False(reportRoot.GetProperty("readyForReview").GetBoolean());
                Assert.Equal("not-ready", reportRoot.GetProperty("status").GetString());
                Assert.True(reportRoot.GetProperty("disabledPathReady").GetBoolean());
                Assert.False(reportRoot.GetProperty("enabledPathReady").GetBoolean());
                Assert.Equal(1, reportRoot.GetProperty("capturedModeCount").GetInt32());
                Assert.Equal(1, reportRoot.GetProperty("missingModeCount").GetInt32());
                Assert.Contains("enabled", ReadStringArray(reportRoot, "missingModes"));
            }

            var validationReport = Path.Combine(bundleDir, "validation-puresignal-incomplete.json");
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
            Assert.True(validationRoot.GetProperty("pureSignalSafeBypassReportPresent").GetBoolean());
            Assert.False(validationRoot.GetProperty("pureSignalSafeBypassReportReady").GetBoolean());
            Assert.Equal("not-ready", validationRoot.GetProperty("pureSignalSafeBypassReportStatus").GetString());
            Assert.Equal(1, validationRoot.GetProperty("pureSignalSafeBypassMissingModeCount").GetInt32());
            Assert.Contains("enabled", ReadStringArray(validationRoot, "pureSignalSafeBypassMissingModes"));

            var errorCodes = validationRoot.GetProperty("errors")
                .EnumerateArray()
                .Select(issue => issue.GetProperty("code").GetString())
                .ToArray();
            Assert.Contains("puresignal-safe-bypass-not-ready", errorCodes);
            Assert.Contains("puresignal-safe-bypass-mode-coverage-missing", errorCodes);

            var summaryReport = Path.Combine(bundleDir, "summary-puresignal-incomplete.json");
            var triage = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "summarize-dsp-modernization-validation-report.ps1"),
                "-ValidationReportPath", validationReport,
                "-ReportPath", summaryReport,
                "-NoMarkdown",
                "-JsonOnly");

            Assert.Equal(0, triage.ExitCode);
            Assert.True(File.Exists(summaryReport), triage.CombinedOutput);

            using var summaryDoc = JsonDocument.Parse(await File.ReadAllTextAsync(summaryReport));
            var summaryRoot = summaryDoc.RootElement;
            Assert.Contains("puresignal-safe-bypass", ReadStringArray(summaryRoot, "advisoryEvidenceGateProblemIds"));
            var pureSignalAction = summaryRoot.GetProperty("acceptanceActionPlan")
                .EnumerateArray()
                .Single(action => action.GetProperty("actionId").GetString() == "capture-puresignal-safe-bypass-bench");
            Assert.Equal("puresignal-safe-bypass", pureSignalAction.GetProperty("gateId").GetString());
            Assert.False(pureSignalAction.GetProperty("requiredForAcceptance").GetBoolean());
            Assert.True(pureSignalAction.GetProperty("blocksDefaultBehaviorChange").GetBoolean());
            Assert.Equal("artifacts/puresignal-safe-bypass-report.json", pureSignalAction.GetProperty("expectedArtifact").GetString());
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
    public async Task OfflineFixtureComparisonCarriesSinadAndProcessingTimingMetrics()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell fixture comparator smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-dsp-fixture-sinad-timing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(bundleDir, "benchmark-plan.json"),
                JsonSerializer.Serialize(DspBenchmarkPlanCatalog.Build(), CamelCaseJson));
            await File.WriteAllTextAsync(
                Path.Combine(bundleDir, "benchmark-metric-catalog.json"),
                JsonSerializer.Serialize(DspBenchmarkPlanCatalog.BuildMetricCatalog(), CamelCaseJson));

            var evidence = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "run-dsp-offline-fixture-evidence.ps1"),
                "-BundleDir", bundleDir,
                "-ScenarioIds", "weak-cw-carrier,tx-two-tone",
                "-ComparisonIds", "current-zeus,thetis-parity,candidate-under-test",
                "-Force",
                "-NoMarkdown");

            Assert.True(evidence.ExitCode == 0, evidence.CombinedOutput);

            var comparison = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "compare-dsp-fixture-metrics.ps1"),
                "-BundleDir", bundleDir,
                "-NoMarkdown");

            Assert.True(comparison.ExitCode == 0 || comparison.ExitCode == 2, comparison.CombinedOutput);

            var metricsPath = Path.Combine(bundleDir, "artifacts", "offline-fixture-metrics.json");
            var reportPath = Path.Combine(bundleDir, "artifacts", "dsp-fixture-metric-comparison.json");
            Assert.True(File.Exists(metricsPath), evidence.CombinedOutput);
            Assert.True(File.Exists(reportPath), comparison.CombinedOutput);

            using var metricsDoc = JsonDocument.Parse(await File.ReadAllTextAsync(metricsPath));
            var weakCarrier = metricsDoc.RootElement
                .GetProperty("scenarios")
                .EnumerateArray()
                .Single(scenario => scenario.GetProperty("scenarioId").GetString() == "weak-cw-carrier");
            var weakCurrent = weakCarrier
                .GetProperty("comparisons")
                .EnumerateArray()
                .Single(item => item.GetProperty("comparisonId").GetString() == "current-zeus")
                .GetProperty("metrics");
            Assert.True(weakCurrent.TryGetProperty("signal SINAD", out _));
            Assert.True(weakCurrent.TryGetProperty("processing elapsed ms", out _));
            Assert.True(weakCurrent.TryGetProperty("throughput ratio", out _));

            using var reportDoc = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
            var metricIds = reportDoc.RootElement
                .GetProperty("comparisons")
                .EnumerateArray()
                .SelectMany(comparisonElement => comparisonElement.GetProperty("metricComparisons").EnumerateArray())
                .Select(metric => metric.GetProperty("metricId").GetString())
                .ToHashSet(StringComparer.Ordinal);

            Assert.Contains("signalsinad", metricIds);
            Assert.Contains("processingelapsedms", metricIds);
            Assert.Contains("throughputratio", metricIds);
            Assert.Contains("txoutputpeak", metricIds);
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
            Assert.Contains("wdsp-source-drift-report", ReadStringArray(summaryDoc.RootElement, "optInDspBuildOutBlockingGateIds"));

            var buildOutStage = readinessStages.Single(stage => stage.GetProperty("stageId").GetString() == "opt-in-dsp-buildout-prerequisites");
            Assert.False(buildOutStage.GetProperty("ready").GetBoolean());
            Assert.False(buildOutStage.GetProperty("blocksDefaultBehaviorChange").GetBoolean());
            Assert.Contains("wdsp-native-symbol-audit", ReadStringArray(buildOutStage, "blockingGateIds"));
            Assert.Contains("wdsp-source-drift-report", ReadStringArray(buildOutStage, "blockingGateIds"));

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
    public async Task ValidationTriageExplainsWeakOnlyG2PeakHuntBeforeMixedWeakStrongRecapture()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell validation triage smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-dsp-weak-only-peak-hunt-action-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            var validationReport = Path.Combine(bundleDir, "validation-report.json");
            var validation = new
            {
                ok = true,
                errorCount = 0,
                warningCount = 0,
                errors = Array.Empty<object>(),
                warnings = Array.Empty<object>(),
                artifactReferencedFiles = Array.Empty<object>(),
                hardwareEvidenceStatus = "g2-hardware-evidence-ready",
                hardwareTarget = "G2",
                captureHardwareTarget = "G2",
                hardwareDiagnosticsPresent = true,
                liveMatrixMixedWeakStrongHuntReady = false,
                liveMatrixMixedWeakStrongStatus = "missing-mixed-weak-strong",
                liveMatrixMixedWeakStrongReportCount = 0,
                liveMatrixMixedWeakStrongSchemaV2ReportCount = 0,
                liveMatrixMixedWeakStrongReadyReportCount = 0,
                liveMatrixMixedWeakStrongTraceCount = 0,
                liveMatrixMixedWeakStrongReadyTraceCount = 0,
                liveMatrixMixedWeakStrongMissingRunCount = 0,
                liveMatrixMixedWeakStrongGapWatchRunCount = 0,
                liveMatrixMixedWeakStrongWeakInputSampleCount = 0,
                liveMatrixMixedWeakStrongStrongInputSampleCount = 0,
                liveMatrixMixedWeakStrongBestRun = (object?)null,
                g2RxPeakHuntReportPresent = true,
                g2RxPeakHuntReportReady = true,
                g2RxPeakHuntReportValid = true,
                g2RxPeakHuntReportStatus = "weak-only",
                g2RxPeakHuntAllowRetune = true,
                g2RxPeakHuntActualRunCount = 7,
                g2RxPeakHuntFailedRunCount = 0,
                g2RxPeakHuntReferencedWindowCount = 7,
                g2RxPeakHuntReferencedWindowReadyCount = 7,
                g2RxPeakHuntReferencedWindowProblemCount = 0,
                g2RxPeakHuntMixedWeakStrongReady = false,
                g2RxPeakHuntWeakInputSampleCount = 147,
                g2RxPeakHuntStrongInputSampleCount = 0,
                g2RxPeakHuntCandidateWeakLossSampleCount = 0,
                g2RxPeakHuntHotMakeupSampleCount = 0,
                g2RxPeakHuntHardBlockerSampleCount = 0,
                g2RxPeakHuntAgcPumpingRiskRunCount = 0,
                g2RxPeakHuntSafetyOriginalVfoRestored = true,
                g2RxPeakHuntBestFrequencyHz = 14127164,
                g2RxPeakHuntBestScore = 35.0,
                g2RxPeakHuntBestStatus = "missing-strong-input",
                g2RxPeakHuntBestReportPath = "artifacts/g2-rx-peak-hunt/frontend-top-peak-14127164/window-01/live-diagnostics-watch.json",
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

            using var summaryDoc = JsonDocument.Parse(await File.ReadAllTextAsync(summaryReport));
            var action = summaryDoc.RootElement
                .GetProperty("acceptanceActionPlan")
                .EnumerateArray()
                .Single(item => item.GetProperty("actionId").GetString() == "capture-mixed-weak-strong-live-history");

            var reason = action.GetProperty("reason").GetString() ?? "";
            Assert.Contains("weak-only", reason, StringComparison.Ordinal);
            Assert.Contains("weakSamples=147", reason, StringComparison.Ordinal);
            Assert.Contains("strongSamples=0", reason, StringComparison.Ordinal);
            Assert.Contains("bestFrequencyHz=14127164", reason, StringComparison.Ordinal);
            Assert.Contains("bestStatus='missing-strong-input'", reason, StringComparison.Ordinal);
            Assert.Contains("cannot satisfy mixed weak+strong acceptance", reason, StringComparison.Ordinal);

            var manualAction = action.GetProperty("manualAction").GetString() ?? "";
            Assert.Contains("weak-only/missing strong input", manualAction, StringComparison.Ordinal);
            Assert.Contains("both weak and strong speech", manualAction, StringComparison.Ordinal);

            var steps = ReadStringArray(action, "commandSteps");
            Assert.Contains(steps, step => step.Contains("run-dsp-g2-rx-peak-hunt.ps1", StringComparison.Ordinal));
            Assert.Contains("artifacts/g2-rx-peak-hunt-report.json", ReadStringArray(action, "expectedArtifacts"));
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
    public async Task WatchLiveDiagnosticsSummarizesFrontendTopPeaks()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell live diagnostics watcher smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-dsp-frontend-top-peaks-watch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            var jsonlPath = Path.Combine(bundleDir, "frontend-top-peaks.jsonl");
            await WriteAgcWatchJsonlAsync(
                jsonlPath,
                new[]
                {
                    AgcWatchSample(0, agcGainDb: 0.0, audioRmsDbfs: -32.0, frontendTopPeaks:
                    [
                        FrontendTopPeak(14_268_700, 1_700, 24.2, -81.2),
                        FrontendTopPeak(14_280_000, 13_000, 18.0, -88.0)
                    ]),
                    AgcWatchSample(1, agcGainDb: 0.2, audioRmsDbfs: -31.0, frontendTopPeaks:
                    [
                        FrontendTopPeak(14_266_100, -900, 22.1, -82.5)
                    ]),
                    AgcWatchSample(2, agcGainDb: 0.4, audioRmsDbfs: -30.0, frontendTopPeaks:
                    [
                        FrontendTopPeak(14_256_000, -11_000, 19.8, -86.0)
                    ])
                });

            var reportPath = Path.Combine(bundleDir, "frontend-top-peaks.summary.json");
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
            var topPeakWatch = root.GetProperty("frontendTopPeakWatch");

            Assert.Equal(3, topPeakWatch.GetProperty("sampleCount").GetInt32());
            Assert.Equal(2, topPeakWatch.GetProperty("nearPassbandSampleCount").GetInt32());
            Assert.Equal(3000, topPeakWatch.GetProperty("nearPassbandThresholdHz").GetInt32());
            Assert.Equal(3, topPeakWatch.GetProperty("topPeakCount").GetProperty("count").GetInt32());
            Assert.Equal(24.2, topPeakWatch.GetProperty("strongestSnrDb").GetProperty("max").GetDouble());
            Assert.Equal(11_000, topPeakWatch.GetProperty("nearestAbsOffsetHz").GetProperty("max").GetDouble());

            var nearSamples = topPeakWatch.GetProperty("topNearPassbandSamples").EnumerateArray().ToArray();
            Assert.NotEmpty(nearSamples);
            Assert.True(Math.Abs(nearSamples[0].GetProperty("nearest").GetProperty("offsetHz").GetInt32()) <= 3000);

            var passbandAudioWatch = root.GetProperty("passbandAudioWatch");
            Assert.Equal("passband-active-audio", passbandAudioWatch.GetProperty("status").GetString());
            Assert.Equal(3, passbandAudioWatch.GetProperty("frontendTopPeakSampleCount").GetInt32());
            Assert.Equal(2, passbandAudioWatch.GetProperty("passbandPeakSampleCount").GetInt32());
            Assert.Equal(1, passbandAudioWatch.GetProperty("offPassbandPeakSampleCount").GetInt32());
            Assert.Equal(2, passbandAudioWatch.GetProperty("passbandActiveAudioSampleCount").GetInt32());
            Assert.Equal(100.0, passbandAudioWatch.GetProperty("passbandActiveAudioPct").GetDouble(), precision: 3);
            Assert.Equal(-31.5, passbandAudioWatch.GetProperty("passbandAudioRmsDbfs").GetProperty("average").GetDouble(), precision: 3);
            Assert.Equal(-30.0, passbandAudioWatch.GetProperty("offPassbandAudioRmsDbfs").GetProperty("average").GetDouble(), precision: 3);
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
    public async Task WatchLiveDiagnosticsTreatsNr5TargetLevelerNormalizationAsResolved()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell live diagnostics watcher smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-dsp-nr5-leveler-normalized-watch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            var jsonlPath = Path.Combine(bundleDir, "nr5-leveler-normalized.jsonl");
            await WriteAgcWatchJsonlAsync(
                jsonlPath,
                new[]
                {
                    Nr5LevelerAlignmentWatchSample(13, nr5InputDbfs: -47.2, nr5OutputDbfs: -36.0, levelerInputRmsDbfs: -54.2, levelerOutputRmsDbfs: -19.8),
                    Nr5LevelerAlignmentWatchSample(14, nr5InputDbfs: -48.4, nr5OutputDbfs: -36.8, levelerInputRmsDbfs: -57.0, levelerOutputRmsDbfs: -19.6)
                });

            var reportPath = Path.Combine(bundleDir, "nr5-leveler-normalized.summary.json");
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
            Assert.Equal(2, alignment.GetProperty("comparableSampleCount").GetInt32());
            Assert.Equal(0, alignment.GetProperty("mismatchSampleCount").GetInt32());
            Assert.Equal(0, alignment.GetProperty("alignedAfterLevelerSampleCount").GetInt32());
            Assert.Equal(2, alignment.GetProperty("levelerNormalizedSampleCount").GetInt32());
            Assert.Equal(100.0, alignment.GetProperty("levelerNormalizedPct").GetDouble(), precision: 3);

            var normalizedSamples = alignment.GetProperty("topLevelerNormalizedSamples").EnumerateArray().ToArray();
            Assert.Equal(2, normalizedSamples.Length);
            Assert.Equal(14, normalizedSamples[0].GetProperty("sampleIndex").GetInt32());
            Assert.Equal(-19.6, normalizedSamples[0].GetProperty("rxAudioLevelerOutputRmsDbfs").GetDouble(), precision: 3);

            Assert.Contains(
                root.GetProperty("recommendations").EnumerateArray(),
                recommendation => (recommendation.GetString() ?? "").Contains("normalized to target loudness", StringComparison.Ordinal));
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
    public async Task WatchLiveDiagnosticsUsesFinalAudioParityForWeakStrongNr5Comparison()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell live diagnostics watcher smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-dsp-nr5-final-audio-parity-watch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            var jsonlPath = Path.Combine(bundleDir, "nr5-final-audio-parity.jsonl");
            await WriteAgcWatchJsonlAsync(
                jsonlPath,
                new[]
                {
                    Nr5LevelerAlignmentWatchSample(21, nr5InputDbfs: -54.0, nr5OutputDbfs: -35.0, levelerInputRmsDbfs: -52.0, levelerOutputRmsDbfs: -18.8),
                    Nr5LevelerAlignmentWatchSample(22, nr5InputDbfs: -20.0, nr5OutputDbfs: -23.0, levelerInputRmsDbfs: -50.0, levelerOutputRmsDbfs: -18.0)
                });

            var reportPath = Path.Combine(bundleDir, "nr5-final-audio-parity.summary.json");
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
            var weakWatch = root.GetProperty("nr5WeakSignalWatch");
            Assert.Equal("ready-final-audio", weakWatch.GetProperty("mixedWeakStrongEvidenceStatus").GetString());
            Assert.True(weakWatch.GetProperty("mixedWeakStrongEvidenceReady").GetBoolean());
            Assert.True(weakWatch.GetProperty("weakStrongFinalAudioParityReady").GetBoolean());
            Assert.Equal(0.8, weakWatch.GetProperty("weakStrongFinalAudioGapDb").GetDouble(), precision: 3);
            Assert.Equal(12.0, weakWatch.GetProperty("weakStrongOutputGapDb").GetDouble(), precision: 3);
            Assert.Equal(-18.8, weakWatch.GetProperty("weakFinalAudioDbfs").GetProperty("average").GetDouble(), precision: 3);
            Assert.Equal(-18.0, weakWatch.GetProperty("strongFinalAudioDbfs").GetProperty("average").GetDouble(), precision: 3);

            Assert.Contains(
                root.GetProperty("recommendations").EnumerateArray(),
                recommendation => (recommendation.GetString() ?? "").Contains("post-leveler speech audio is within parity", StringComparison.Ordinal));
            Assert.DoesNotContain(
                root.GetProperty("recommendations").EnumerateArray(),
                recommendation => (recommendation.GetString() ?? "").Contains("tune normalization before judging", StringComparison.Ordinal));
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
    public async Task WatchLiveDiagnosticsSeparatesSpeechQualifiedParityFromFloorSuppression()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell live diagnostics watcher smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-dsp-nr5-speech-qualified-parity-watch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            var jsonlPath = Path.Combine(bundleDir, "nr5-speech-qualified-parity.jsonl");
            await WriteAgcWatchJsonlAsync(
                jsonlPath,
                new[]
                {
                    Nr5LevelerAlignmentWatchSample(31, nr5InputDbfs: -54.0, nr5OutputDbfs: -55.0, levelerInputRmsDbfs: -85.0, levelerOutputRmsDbfs: -85.0),
                    Nr5LevelerAlignmentWatchSample(32, nr5InputDbfs: -54.0, nr5OutputDbfs: -35.0, levelerInputRmsDbfs: -52.0, levelerOutputRmsDbfs: -18.8),
                    Nr5LevelerAlignmentWatchSample(33, nr5InputDbfs: -21.0, nr5OutputDbfs: -24.0, levelerInputRmsDbfs: -60.0, levelerOutputRmsDbfs: -48.4),
                    Nr5LevelerAlignmentWatchSample(34, nr5InputDbfs: -20.0, nr5OutputDbfs: -23.0, levelerInputRmsDbfs: -50.0, levelerOutputRmsDbfs: -18.0)
                });

            var reportPath = Path.Combine(bundleDir, "nr5-speech-qualified-parity.summary.json");
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
            var weakWatch = root.GetProperty("nr5WeakSignalWatch");
            Assert.Equal("weak-strong-output-gap-watch", weakWatch.GetProperty("mixedWeakStrongEvidenceStatus").GetString());
            Assert.False(weakWatch.GetProperty("mixedWeakStrongEvidenceReady").GetBoolean());
            Assert.Equal("ready-final-audio", weakWatch.GetProperty("speechQualifiedMixedWeakStrongEvidenceStatus").GetString());
            Assert.True(weakWatch.GetProperty("speechQualifiedMixedWeakStrongEvidenceReady").GetBoolean());
            Assert.True(weakWatch.GetProperty("speechQualifiedWeakStrongFinalAudioParityReady").GetBoolean());
            Assert.Equal(1, weakWatch.GetProperty("speechQualifiedWeakInputSampleCount").GetInt32());
            Assert.Equal(1, weakWatch.GetProperty("speechQualifiedStrongInputSampleCount").GetInt32());
            Assert.Equal(0.8, weakWatch.GetProperty("speechQualifiedWeakStrongFinalAudioGapDb").GetDouble(), precision: 3);

            Assert.True(weakWatch.GetProperty("speechQualifiedWeakFinalAudioDbfs").GetProperty("count").GetInt32() > 0);
            Assert.True(weakWatch.GetProperty("speechQualifiedStrongFinalAudioDbfs").GetProperty("count").GetInt32() > 0);
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
    public async Task WatchLiveDiagnosticsSeparatesPassbandQualifiedParityFromAdjacentSuppression()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell live diagnostics watcher smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-dsp-nr5-passband-qualified-parity-watch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            var jsonlPath = Path.Combine(bundleDir, "nr5-passband-qualified-parity.jsonl");
            await WriteAgcWatchJsonlAsync(
                jsonlPath,
                new[]
                {
                    Nr5LevelerAlignmentWatchSample(
                        41,
                        nr5InputDbfs: -52.0,
                        nr5OutputDbfs: -31.0,
                        levelerInputRmsDbfs: -50.0,
                        levelerOutputRmsDbfs: -42.0,
                        frontendTopPeaks: new[] { FrontendTopPeak(14_255_264, 15_264, 27.4, -72.0, confidence: 0.94) }),
                    Nr5LevelerAlignmentWatchSample(
                        42,
                        nr5InputDbfs: -54.0,
                        nr5OutputDbfs: -35.0,
                        levelerInputRmsDbfs: -52.0,
                        levelerOutputRmsDbfs: -18.8,
                        frontendTopPeaks: new[] { FrontendTopPeak(14_240_264, 264, 27.3, -73.7, confidence: 0.94) }),
                    Nr5LevelerAlignmentWatchSample(
                        43,
                        nr5InputDbfs: -20.0,
                        nr5OutputDbfs: -23.0,
                        levelerInputRmsDbfs: -50.0,
                        levelerOutputRmsDbfs: -18.0,
                        frontendTopPeaks: new[] { FrontendTopPeak(14_240_264, 264, 29.0, -66.8, confidence: 0.94) })
                });

            var reportPath = Path.Combine(bundleDir, "nr5-passband-qualified-parity.summary.json");
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
            var weakWatch = reportDoc.RootElement.GetProperty("nr5WeakSignalWatch");
            Assert.Equal("weak-strong-speech-gap-watch", weakWatch.GetProperty("speechQualifiedMixedWeakStrongEvidenceStatus").GetString());
            Assert.False(weakWatch.GetProperty("speechQualifiedMixedWeakStrongEvidenceReady").GetBoolean());
            Assert.Equal(2, weakWatch.GetProperty("speechQualifiedWeakInputSampleCount").GetInt32());
            Assert.Equal(1, weakWatch.GetProperty("speechQualifiedStrongInputSampleCount").GetInt32());

            Assert.Equal("ready-final-audio", weakWatch.GetProperty("passbandQualifiedMixedWeakStrongEvidenceStatus").GetString());
            Assert.True(weakWatch.GetProperty("passbandQualifiedMixedWeakStrongEvidenceReady").GetBoolean());
            Assert.True(weakWatch.GetProperty("passbandQualifiedWeakStrongFinalAudioParityReady").GetBoolean());
            Assert.Equal(1, weakWatch.GetProperty("passbandQualifiedWeakInputSampleCount").GetInt32());
            Assert.Equal(1, weakWatch.GetProperty("passbandQualifiedStrongInputSampleCount").GetInt32());
            Assert.Equal(0.8, weakWatch.GetProperty("passbandQualifiedWeakStrongFinalAudioGapDb").GetDouble(), precision: 3);
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
    public async Task CompareLiveDiagnosticsTraceScoresPassbandAudioWatch()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell live diagnostics comparator smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-dsp-passband-audio-compare-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            var baselineJsonl = Path.Combine(bundleDir, "passband-healthy-baseline.jsonl");
            await WriteAgcWatchJsonlAsync(
                baselineJsonl,
                new[]
                {
                    AgcWatchSample(0, agcGainDb: 0.0, audioRmsDbfs: -32.0, frontendTopPeaks:
                    [
                        FrontendTopPeak(14_240_900, 900, 24.0, -80.0)
                    ]),
                    AgcWatchSample(1, agcGainDb: 0.0, audioRmsDbfs: -33.0, frontendTopPeaks:
                    [
                        FrontendTopPeak(14_241_500, 1_500, 22.0, -82.0)
                    ]),
                    AgcWatchSample(2, agcGainDb: 0.0, audioRmsDbfs: -72.0, frontendTopPeaks:
                    [
                        FrontendTopPeak(14_248_000, 8_000, 18.0, -88.0)
                    ])
                });

            var candidateJsonl = Path.Combine(bundleDir, "passband-overgated-candidate.jsonl");
            await WriteAgcWatchJsonlAsync(
                candidateJsonl,
                new[]
                {
                    AgcWatchSample(0, agcGainDb: 0.0, audioRmsDbfs: -50.0, frontendTopPeaks:
                    [
                        FrontendTopPeak(14_240_900, 900, 24.0, -80.0)
                    ]),
                    AgcWatchSample(1, agcGainDb: 0.0, audioRmsDbfs: -75.0, frontendTopPeaks:
                    [
                        FrontendTopPeak(14_241_500, 1_500, 22.0, -82.0)
                    ]),
                    AgcWatchSample(2, agcGainDb: 0.0, audioRmsDbfs: -110.0, frontendTopPeaks:
                    [
                        FrontendTopPeak(14_248_000, 8_000, 18.0, -88.0)
                    ])
                });

            var baselineReport = Path.Combine(bundleDir, "passband-healthy-baseline.summary.json");
            var baselineWatch = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "watch-dsp-live-diagnostics.ps1"),
                "-InputPath", baselineJsonl,
                "-ReportPath", baselineReport,
                "-JsonOnly");
            Assert.Equal(0, baselineWatch.ExitCode);

            var candidateReport = Path.Combine(bundleDir, "passband-overgated-candidate.summary.json");
            var candidateWatch = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "watch-dsp-live-diagnostics.ps1"),
                "-InputPath", candidateJsonl,
                "-ReportPath", candidateReport,
                "-JsonOnly");
            Assert.Equal(0, candidateWatch.ExitCode);

            var comparisonReport = Path.Combine(bundleDir, "passband-audio-comparison.json");
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
            var metrics = comparisonDoc.RootElement.GetProperty("metricComparisons").EnumerateArray().ToArray();

            AssertMetricVerdict(metrics, "passbandPeakSampleCount", "informational", "weak-signal");
            AssertMetricRegression(metrics, "passbandActiveAudioPct", "weak-signal");
            AssertMetricRegression(metrics, "passbandFloorAudioPct", "weak-signal");
            AssertMetricRegression(metrics, "passbandAudioAverageDbfs", "weak-signal");
            AssertMetricRegression(metrics, "passbandAudioMovementDb", "pumping");
            AssertMetricImprovement(metrics, "offPassbandAudioAverageDbfs", "noise-gate");
            AssertMetricImprovement(metrics, "passbandNoiseSeparationDb", "noise-gate");
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
            Assert.Equal("ready-final-audio", latest.GetProperty("mixedWeakStrongEvidenceStatus").GetString());
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
    public async Task ValidationReportAcceptsNonG2CrossRadioEvidenceArtifact()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell validator smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-dsp-cross-radio-ready-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            WriteSourcePlanScopeBundle(bundleDir);
            WriteCrossRadioArtifactManifest(bundleDir);
            await WriteCrossRadioSourceValidationReportAsync(
                bundleDir,
                "artifacts/non-g2-validation-report.json",
                "ANAN-7000DLE");

            var crossRadio = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "summarize-dsp-cross-radio-validation.ps1"),
                "-BundleDir", bundleDir,
                "-ValidationReportPath", "artifacts/non-g2-validation-report.json",
                "-ReportPath", "artifacts/cross-radio-validation-report.json",
                "-NoMarkdown",
                "-JsonOnly");

            Assert.Equal(0, crossRadio.ExitCode);
            using (var crossRadioDoc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(bundleDir, "artifacts", "cross-radio-validation-report.json"))))
            {
                var crossRadioRoot = crossRadioDoc.RootElement;
                Assert.Equal(2, crossRadioRoot.GetProperty("schemaVersion").GetInt32());
                Assert.True(crossRadioRoot.GetProperty("sourceBackedEvidenceReady").GetBoolean());
                Assert.Equal(1, crossRadioRoot.GetProperty("sourceReportCount").GetInt32());
                Assert.Equal(1, crossRadioRoot.GetProperty("nonG2SourceReportCount").GetInt32());
                Assert.Equal(1, crossRadioRoot.GetProperty("readyNonG2SourceReportCount").GetInt32());
            }

            var validationReport = Path.Combine(bundleDir, "validation-cross-radio-ready.json");
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

            using (var validationDoc = JsonDocument.Parse(await File.ReadAllTextAsync(validationReport)))
            {
                var root = validationDoc.RootElement;
                Assert.True(root.GetProperty("crossRadioValidationPresent").GetBoolean());
                Assert.True(root.GetProperty("crossRadioValidationReady").GetBoolean());
                Assert.Equal("cross-radio-evidence-ready", root.GetProperty("crossRadioValidationEvidenceStatus").GetString());
                Assert.Equal("summarize-dsp-cross-radio-validation", root.GetProperty("crossRadioValidationEvidence").GetProperty("tool").GetString());
                Assert.Equal(1, root.GetProperty("crossRadioValidationNonG2TargetCount").GetInt32());
                Assert.Contains("ANAN-7000DLE", ReadStringArray(root, "crossRadioValidationNonG2TargetIds"));
                Assert.Equal(1, root.GetProperty("crossRadioValidationScenarioCount").GetInt32());
                Assert.Contains("weak-cw-carrier", ReadStringArray(root, "crossRadioValidationScenarioIds"));
                Assert.Equal(2, root.GetProperty("crossRadioValidationComparisonCount").GetInt32());
                Assert.Contains("current-zeus", ReadStringArray(root, "crossRadioValidationComparisonIds"));
                Assert.False(root.GetProperty("crossRadioValidationDefaultBehaviorChangeApproved").GetBoolean());
                Assert.Equal(1, root.GetProperty("crossRadioValidationSourceReportCount").GetInt32());
                Assert.Equal(1, root.GetProperty("crossRadioValidationNonG2SourceReportCount").GetInt32());
                Assert.Equal(1, root.GetProperty("crossRadioValidationReadyNonG2SourceReportCount").GetInt32());
                Assert.True(root.GetProperty("crossRadioValidationSourceBackedEvidenceReady").GetBoolean());
            }

            var summaryReport = Path.Combine(bundleDir, "summary-cross-radio-ready.json");
            var summary = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "summarize-dsp-modernization-validation-report.ps1"),
                "-ValidationReportPath", validationReport,
                "-ReportPath", summaryReport,
                "-NoMarkdown",
                "-JsonOnly");

            Assert.Equal(0, summary.ExitCode);
            using var summaryDoc = JsonDocument.Parse(await File.ReadAllTextAsync(summaryReport));
            var summaryRoot = summaryDoc.RootElement;
            Assert.True(summaryRoot.GetProperty("crossRadioValidationReady").GetBoolean());
            Assert.Equal("cross-radio-evidence-ready", summaryRoot.GetProperty("crossRadioValidationEvidenceStatus").GetString());
            Assert.Contains("ANAN-7000DLE", ReadStringArray(summaryRoot, "crossRadioValidationNonG2TargetIds"));
            Assert.Equal(1, summaryRoot.GetProperty("crossRadioValidationReadyNonG2SourceReportCount").GetInt32());
            Assert.True(summaryRoot.GetProperty("crossRadioValidationSourceBackedEvidenceReady").GetBoolean());

            var crossRadioGate = summaryRoot.GetProperty("evidenceGates")
                .EnumerateArray()
                .Single(gate => gate.GetProperty("gateId").GetString() == "cross-radio-validation");
            Assert.True(crossRadioGate.GetProperty("ready").GetBoolean());
            Assert.False(crossRadioGate.GetProperty("requiredForAcceptance").GetBoolean());
            Assert.Contains("readyNonG2Sources=1", crossRadioGate.GetProperty("detail").GetString(), StringComparison.Ordinal);
            Assert.False(summaryRoot.GetProperty("defaultBehaviorChangeReady").GetBoolean());
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
    public async Task ValidationReportRejectsDeclaredOnlyCrossRadioEvidenceArtifact()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell validator smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-dsp-cross-radio-declared-only-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            WriteSourcePlanScopeBundle(bundleDir);
            WriteCrossRadioArtifactManifest(bundleDir);
            var crossRadio = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "summarize-dsp-cross-radio-validation.ps1"),
                "-BundleDir", bundleDir,
                "-ReportPath", "artifacts/cross-radio-validation-report.json",
                "-HardwareTarget", "ANAN-7000DLE",
                "-ScenarioId", "weak-cw-carrier",
                "-ComparisonId", "current-zeus,nr5-spnr",
                "-NoMarkdown",
                "-JsonOnly");

            Assert.Equal(0, crossRadio.ExitCode);
            using (var crossRadioDoc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(bundleDir, "artifacts", "cross-radio-validation-report.json"))))
            {
                var crossRadioRoot = crossRadioDoc.RootElement;
                Assert.False(crossRadioRoot.GetProperty("readyForReview").GetBoolean());
                Assert.False(crossRadioRoot.GetProperty("sourceBackedEvidenceReady").GetBoolean());
                Assert.Equal(0, crossRadioRoot.GetProperty("sourceReportCount").GetInt32());
                var blockerCodes = crossRadioRoot.GetProperty("blockers")
                    .EnumerateArray()
                    .Select(blocker => blocker.GetProperty("code").GetString())
                    .ToArray();
                Assert.Contains("source-validation-report-missing", blockerCodes);
            }

            var validationReport = Path.Combine(bundleDir, "validation-cross-radio-declared-only.json");
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
            using var validationDoc = JsonDocument.Parse(await File.ReadAllTextAsync(validationReport));
            var root = validationDoc.RootElement;
            Assert.True(root.GetProperty("crossRadioValidationPresent").GetBoolean());
            Assert.False(root.GetProperty("crossRadioValidationReady").GetBoolean());
            Assert.False(root.GetProperty("crossRadioValidationSourceBackedEvidenceReady").GetBoolean());

            var errorCodes = root.GetProperty("errors")
                .EnumerateArray()
                .Select(issue => issue.GetProperty("code").GetString())
                .ToArray();
            Assert.Contains("cross-radio-validation-source-reports-missing", errorCodes);
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
    public async Task ValidationReportRejectsCrossRadioSourceMissingThetisLiveComparison()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell validator smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-dsp-cross-radio-thetis-missing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            WriteSourcePlanScopeBundle(bundleDir);
            WriteCrossRadioArtifactManifest(bundleDir);
            await WriteCrossRadioSourceValidationReportAsync(
                bundleDir,
                "artifacts/non-g2-validation-report.json",
                "ANAN-7000DLE",
                liveTraceThetisComparisonReady: false);

            var crossRadio = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "summarize-dsp-cross-radio-validation.ps1"),
                "-BundleDir", bundleDir,
                "-ValidationReportPath", "artifacts/non-g2-validation-report.json",
                "-ReportPath", "artifacts/cross-radio-validation-report.json",
                "-NoMarkdown",
                "-JsonOnly",
                "-FailOnNotReady");

            Assert.NotEqual(0, crossRadio.ExitCode);
            Assert.True(File.Exists(Path.Combine(bundleDir, "artifacts", "cross-radio-validation-report.json")), crossRadio.CombinedOutput);

            var validationReport = Path.Combine(bundleDir, "validation-cross-radio-thetis-missing.json");
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
            using var validationDoc = JsonDocument.Parse(await File.ReadAllTextAsync(validationReport));
            var root = validationDoc.RootElement;
            Assert.True(root.GetProperty("crossRadioValidationPresent").GetBoolean());
            Assert.False(root.GetProperty("crossRadioValidationReady").GetBoolean());
            Assert.Equal(1, root.GetProperty("crossRadioValidationNonG2SourceReportCount").GetInt32());
            Assert.Equal(0, root.GetProperty("crossRadioValidationReadyNonG2SourceReportCount").GetInt32());
            Assert.False(root.GetProperty("crossRadioValidationSourceBackedEvidenceReady").GetBoolean());

            var errorCodes = root.GetProperty("errors")
                .EnumerateArray()
                .Select(issue => issue.GetProperty("code").GetString())
                .ToArray();
            Assert.Contains("cross-radio-validation-source-thetis-live-trace-comparison-not-ready", errorCodes);
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
    public async Task ValidationReportRejectsG2OnlyCrossRadioEvidenceArtifact()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell validator smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-dsp-cross-radio-g2-only-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            WriteSourcePlanScopeBundle(bundleDir);
            WriteCrossRadioArtifactManifest(bundleDir);
            var crossRadio = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "summarize-dsp-cross-radio-validation.ps1"),
                "-BundleDir", bundleDir,
                "-ReportPath", "artifacts/cross-radio-validation-report.json",
                "-HardwareTarget", "G2",
                "-ScenarioId", "weak-cw-carrier",
                "-ComparisonId", "current-zeus,nr5-spnr",
                "-NoMarkdown",
                "-JsonOnly",
                "-FailOnNotReady");

            Assert.NotEqual(0, crossRadio.ExitCode);
            Assert.True(File.Exists(Path.Combine(bundleDir, "artifacts", "cross-radio-validation-report.json")), crossRadio.CombinedOutput);

            var validationReport = Path.Combine(bundleDir, "validation-cross-radio-g2-only.json");
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
            var root = validationDoc.RootElement;
            Assert.True(root.GetProperty("crossRadioValidationPresent").GetBoolean());
            Assert.False(root.GetProperty("crossRadioValidationReady").GetBoolean());
            Assert.Equal("not-ready", root.GetProperty("crossRadioValidationEvidenceStatus").GetString());
            Assert.Equal(0, root.GetProperty("crossRadioValidationNonG2TargetCount").GetInt32());

            var errorCodes = root.GetProperty("errors")
                .EnumerateArray()
                .Select(issue => issue.GetProperty("code").GetString())
                .ToArray();
            Assert.Contains("cross-radio-validation-target-g2-only", errorCodes);
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
    public async Task WdspSourceDriftReportIgnoresLineEndingsAndFlagsLikelyDefects()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell WDSP source drift smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-wdsp-source-drift-{Guid.NewGuid():N}");
        var referenceDir = Path.Combine(bundleDir, "thetis-wdsp");
        var candidateDir = Path.Combine(bundleDir, "zeus-wdsp");
        Directory.CreateDirectory(referenceDir);
        Directory.CreateDirectory(candidateDir);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(referenceDir, "same.c"), "int wdsp_same(void) {\r\n    return 1;\r\n}\r\n");
            await File.WriteAllTextAsync(Path.Combine(candidateDir, "same.c"), "int wdsp_same(void) {\n    return 1;\n}\n");
            await File.WriteAllTextAsync(Path.Combine(referenceDir, "anr.c"), "int anr_mode(void) {\n    return 1;\n}\n");
            await File.WriteAllTextAsync(Path.Combine(candidateDir, "anr.c"), "int anr_mode(void) {\n    return 2;\n}\n");
            await File.WriteAllTextAsync(Path.Combine(candidateDir, "linux_port.c"), "int zeus_port_support(void) {\n    return 1;\n}\n");

            var reportPath = Path.Combine(bundleDir, "wdsp-source-drift-report.json");
            var drift = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "compare-wdsp-source-drift.ps1"),
                "-ReferenceDir", referenceDir,
                "-CandidateDir", candidateDir,
                "-ReportPath", reportPath,
                "-NoMarkdown",
                "-JsonOnly");

            Assert.True(drift.ExitCode == 0, drift.CombinedOutput);
            Assert.True(File.Exists(reportPath), drift.CombinedOutput);

            using var driftDoc = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
            var root = driftDoc.RootElement;
            Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("compare-wdsp-source-drift", root.GetProperty("tool").GetString());
            Assert.False(root.GetProperty("readyForReview").GetBoolean());
            Assert.Equal(2, root.GetProperty("deltaCount").GetInt32());
            Assert.Equal(1, root.GetProperty("likelyDefectCount").GetInt32());

            var records = root.GetProperty("fileDrift").EnumerateArray().ToArray();
            var same = records.Single(record => record.GetProperty("relativePath").GetString() == "same.c");
            Assert.Equal("line-ending-only", same.GetProperty("status").GetString());
            Assert.Equal("thetis-parity", same.GetProperty("category").GetString());
            Assert.False(same.GetProperty("delta").GetBoolean());

            var driftRecord = records.Single(record => record.GetProperty("relativePath").GetString() == "anr.c");
            Assert.Equal("content-drift", driftRecord.GetProperty("status").GetString());
            Assert.Equal("likely-defect", driftRecord.GetProperty("category").GetString());

            var portRecord = records.Single(record => record.GetProperty("relativePath").GetString() == "linux_port.c");
            Assert.Equal("candidate-only", portRecord.GetProperty("status").GetString());
            Assert.Equal("port-build-support", portRecord.GetProperty("category").GetString());

            var strictReportPath = Path.Combine(bundleDir, "wdsp-source-drift-strict.json");
            var strict = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "compare-wdsp-source-drift.ps1"),
                "-ReferenceDir", referenceDir,
                "-CandidateDir", candidateDir,
                "-ReportPath", strictReportPath,
                "-NoMarkdown",
                "-JsonOnly",
                "-FailOnLikelyDefect");

            Assert.NotEqual(0, strict.ExitCode);
            Assert.True(File.Exists(strictReportPath), strict.CombinedOutput);
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
    public async Task ValidationReportAcceptsReadyWdspSourceDriftArtifact()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell WDSP source drift validator smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-wdsp-source-drift-ready-{Guid.NewGuid():N}");
        var referenceDir = Path.Combine(bundleDir, "thetis-wdsp");
        var candidateDir = Path.Combine(bundleDir, "zeus-wdsp");
        Directory.CreateDirectory(referenceDir);
        Directory.CreateDirectory(candidateDir);

        try
        {
            WriteSourcePlanScopeBundle(bundleDir);
            WriteWdspSourceDriftArtifactManifest(bundleDir);
            await File.WriteAllTextAsync(Path.Combine(referenceDir, "same.c"), "int wdsp_same(void) {\r\n    return 1;\r\n}\r\n");
            await File.WriteAllTextAsync(Path.Combine(candidateDir, "same.c"), "int wdsp_same(void) {\n    return 1;\n}\n");

            var drift = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "compare-wdsp-source-drift.ps1"),
                "-ReferenceDir", referenceDir,
                "-CandidateDir", candidateDir,
                "-ReportPath", Path.Combine(bundleDir, "artifacts", "wdsp-source-drift-report.json"),
                "-NoMarkdown",
                "-JsonOnly",
                "-FailOnLikelyDefect");

            Assert.Equal(0, drift.ExitCode);

            var validationReport = Path.Combine(bundleDir, "validation-source-drift-ready.json");
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
            Assert.True(validationRoot.GetProperty("wdspSourceDriftReportPresent").GetBoolean());
            Assert.True(validationRoot.GetProperty("wdspSourceDriftReportReady").GetBoolean());
            Assert.Equal("ready", validationRoot.GetProperty("wdspSourceDriftReportStatus").GetString());
            Assert.True(validationRoot.GetProperty("wdspSourceDriftReportNormalizedLineEndings").GetBoolean());
            Assert.Equal(1, validationRoot.GetProperty("wdspSourceDriftReferenceFileCount").GetInt32());
            Assert.Equal(1, validationRoot.GetProperty("wdspSourceDriftCandidateFileCount").GetInt32());
            Assert.Equal(1, validationRoot.GetProperty("wdspSourceDriftFileCount").GetInt32());
            Assert.Equal(0, validationRoot.GetProperty("wdspSourceDriftLikelyDefectCount").GetInt32());

            var summaryReport = Path.Combine(bundleDir, "summary-source-drift-ready.json");
            var summary = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "summarize-dsp-modernization-validation-report.ps1"),
                "-ValidationReportPath", validationReport,
                "-ReportPath", summaryReport,
                "-NoMarkdown",
                "-JsonOnly");

            Assert.Equal(0, summary.ExitCode);
            using var summaryDoc = JsonDocument.Parse(await File.ReadAllTextAsync(summaryReport));
            var sourceGate = summaryDoc.RootElement.GetProperty("evidenceGates")
                .EnumerateArray()
                .Single(gate => gate.GetProperty("gateId").GetString() == "wdsp-source-drift-report");
            Assert.True(sourceGate.GetProperty("ready").GetBoolean());
            Assert.False(sourceGate.GetProperty("requiredForAcceptance").GetBoolean());
            Assert.DoesNotContain("wdsp-source-drift-report", ReadStringArray(summaryDoc.RootElement, "optInDspBuildOutBlockingGateIds"));
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
    public async Task ValidationReportRejectsLikelyDefectWdspSourceDriftArtifact()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell WDSP source drift validator smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-wdsp-source-drift-defect-{Guid.NewGuid():N}");
        var referenceDir = Path.Combine(bundleDir, "thetis-wdsp");
        var candidateDir = Path.Combine(bundleDir, "zeus-wdsp");
        Directory.CreateDirectory(referenceDir);
        Directory.CreateDirectory(candidateDir);

        try
        {
            WriteSourcePlanScopeBundle(bundleDir);
            WriteWdspSourceDriftArtifactManifest(bundleDir);
            await File.WriteAllTextAsync(Path.Combine(referenceDir, "anr.c"), "int anr_mode(void) {\n    return 1;\n}\n");
            await File.WriteAllTextAsync(Path.Combine(candidateDir, "anr.c"), "int anr_mode(void) {\n    return 2;\n}\n");

            var drift = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "compare-wdsp-source-drift.ps1"),
                "-ReferenceDir", referenceDir,
                "-CandidateDir", candidateDir,
                "-ReportPath", Path.Combine(bundleDir, "artifacts", "wdsp-source-drift-report.json"),
                "-NoMarkdown",
                "-JsonOnly");

            Assert.Equal(0, drift.ExitCode);

            var validationReport = Path.Combine(bundleDir, "validation-source-drift-defect.json");
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
            Assert.True(validationRoot.GetProperty("wdspSourceDriftReportPresent").GetBoolean());
            Assert.False(validationRoot.GetProperty("wdspSourceDriftReportReady").GetBoolean());
            Assert.Equal("not-ready", validationRoot.GetProperty("wdspSourceDriftReportStatus").GetString());
            Assert.Equal(1, validationRoot.GetProperty("wdspSourceDriftLikelyDefectCount").GetInt32());

            var errorCodes = validationRoot.GetProperty("errors")
                .EnumerateArray()
                .Select(issue => issue.GetProperty("code").GetString())
                .ToArray();
            Assert.Contains("wdsp-source-drift-likely-defect", errorCodes);
            Assert.Contains("wdsp-source-drift-not-ready", errorCodes);
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
    public async Task NativeStageTimingReportValidatesWdspFixtureTelemetry()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell native stage timing validator smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-native-stage-timing-ready-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            WriteSourcePlanScopeBundle(bundleDir);
            WriteNativeStageTimingArtifactManifest(bundleDir);
            await WriteNativeStageTimingFixtureMetricsAsync(bundleDir);

            var reportPath = Path.Combine(bundleDir, "artifacts", "native-stage-timing-report.json");
            var timing = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "summarize-dsp-native-stage-timing.ps1"),
                "-BundleDir", bundleDir,
                "-ReportPath", reportPath,
                "-Force",
                "-FailOnBudget",
                "-JsonOnly");

            Assert.Equal(0, timing.ExitCode);
            Assert.True(File.Exists(reportPath), timing.CombinedOutput);

            using (var timingDoc = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath)))
            {
                var root = timingDoc.RootElement;
                Assert.Equal("summarize-dsp-native-stage-timing", root.GetProperty("tool").GetString());
                Assert.True(root.GetProperty("readyForReview").GetBoolean());
                Assert.Equal("managed-wrapper-ready-native-c-pending", root.GetProperty("status").GetString());
                Assert.False(root.GetProperty("nativeCStageInstrumentationReady").GetBoolean());
                Assert.Equal("managed-thread-delta-only", root.GetProperty("nativeAllocationProbeStatus").GetString());
                Assert.Equal(2, root.GetProperty("runCount").GetInt32());
                Assert.Equal(4, root.GetProperty("stageRecordCount").GetInt32());
            }

            var validationReport = Path.Combine(bundleDir, "validation-native-stage-ready.json");
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
            Assert.True(validationRoot.GetProperty("nativeStageTimingReportPresent").GetBoolean());
            Assert.True(validationRoot.GetProperty("nativeStageTimingReportReady").GetBoolean());
            Assert.Equal("managed-wrapper-ready-native-c-pending", validationRoot.GetProperty("nativeStageTimingReportStatus").GetString());
            Assert.Equal("match", validationRoot.GetProperty("nativeStageTimingMetricsHashStatus").GetString());
            Assert.Equal("abc123", validationRoot.GetProperty("nativeStageTimingWdspRuntimeSha256").GetString());

            var summaryReport = Path.Combine(bundleDir, "summary-native-stage-ready.json");
            var summary = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "summarize-dsp-modernization-validation-report.ps1"),
                "-ValidationReportPath", validationReport,
                "-ReportPath", summaryReport,
                "-NoMarkdown",
                "-JsonOnly");

            Assert.Equal(0, summary.ExitCode);
            using var summaryDoc = JsonDocument.Parse(await File.ReadAllTextAsync(summaryReport));
            var timingGate = summaryDoc.RootElement.GetProperty("evidenceGates")
                .EnumerateArray()
                .Single(gate => gate.GetProperty("gateId").GetString() == "native-stage-timing-report");
            Assert.True(timingGate.GetProperty("ready").GetBoolean());
            Assert.True(timingGate.GetProperty("requiredForAcceptance").GetBoolean());
            Assert.DoesNotContain("native-stage-timing-report", ReadStringArray(summaryDoc.RootElement, "requiredEvidenceGateProblemIds"));
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
    public async Task ValidationReportRejectsNativeStageTimingBudgetFailures()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell native stage timing validator smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-native-stage-timing-budget-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            WriteSourcePlanScopeBundle(bundleDir);
            WriteNativeStageTimingArtifactManifest(bundleDir);
            await WriteNativeStageTimingFixtureMetricsAsync(bundleDir);

            var reportPath = Path.Combine(bundleDir, "artifacts", "native-stage-timing-report.json");
            var timing = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "summarize-dsp-native-stage-timing.ps1"),
                "-BundleDir", bundleDir,
                "-ReportPath", reportPath,
                "-MaxStageElapsedMs", "0.001",
                "-Force",
                "-JsonOnly");

            Assert.Equal(0, timing.ExitCode);

            var validationReport = Path.Combine(bundleDir, "validation-native-stage-budget.json");
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
            using var validationDoc = JsonDocument.Parse(await File.ReadAllTextAsync(validationReport));
            var validationRoot = validationDoc.RootElement;
            Assert.True(validationRoot.GetProperty("nativeStageTimingReportPresent").GetBoolean());
            Assert.False(validationRoot.GetProperty("nativeStageTimingReportReady").GetBoolean());
            Assert.Equal("budget-failures", validationRoot.GetProperty("nativeStageTimingReportStatus").GetString());
            Assert.True(validationRoot.GetProperty("nativeStageTimingBudgetFailureCount").GetInt32() > 0);

            var errorCodes = validationRoot.GetProperty("errors")
                .EnumerateArray()
                .Select(issue => issue.GetProperty("code").GetString())
                .ToArray();
            Assert.Contains("native-stage-timing-not-ready", errorCodes);
            Assert.Contains("native-stage-timing-budget-failure", errorCodes);
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
        Assert.Equal(8, root.GetProperty("schemaVersion").GetInt32());
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
        Assert.Contains("external-engine-first-safe-bakeoff", advisorySignals);
        Assert.Contains("puresignal-safe-bypass-bench", advisorySignals);

        var notes = ReadStringArray(root, "notes");
        Assert.Contains(notes, note => note.Contains("No DSP runtime behavior", StringComparison.Ordinal));
        Assert.Contains(notes, note => note.Contains("G2 hardware evidence", StringComparison.Ordinal));
        Assert.Contains(notes, note => note.Contains("after the wrapper writes its summary", StringComparison.Ordinal));
        Assert.Contains(notes, note => note.Contains("Matrix child runs use -ContinueOnError", StringComparison.Ordinal));
        Assert.Contains(notes, note => note.Contains("mixed weak/strong matrix hunt", StringComparison.Ordinal));
        Assert.Contains(notes, note => note.Contains("primaryAcceptance", StringComparison.Ordinal));
        Assert.Contains(notes, note => note.Contains("ArtifactControlSignalCount", StringComparison.Ordinal));
        Assert.Contains(notes, note => note.Contains("liveMatrixArtifactControl", StringComparison.Ordinal));
        Assert.Contains(notes, note => note.Contains("external DSP/ML first-safe bakeoff", StringComparison.Ordinal));
        Assert.Contains(notes, note => note.Contains("PureSignal safe-bypass", StringComparison.Ordinal));
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
                schemaVersion = 8,
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
                triageExternalEngineBakeoffActionPresent = false,
                triageExternalEngineBakeoffActionId = "",
                triageExternalEngineBakeoffActionPriority = (int?)null,
                triageExternalEngineBakeoffActionStageId = "",
                triageExternalEngineBakeoffActionGateId = "",
                triageExternalEngineBakeoffActionCategory = "",
                triageExternalEngineBakeoffActionRequired = false,
                triageExternalEngineBakeoffActionManual = false,
                triageExternalEngineBakeoffCommandTemplate = "",
                triageExternalEngineBakeoffCommandStepCount = 0,
                triageExternalEngineBakeoffCommandSteps = Array.Empty<string>(),
                triageExternalEngineBakeoffManualAction = "",
                triageExternalEngineBakeoffExpectedArtifact = "",
                triageExternalEngineBakeoffExpectedArtifactCount = 0,
                triageExternalEngineBakeoffExpectedArtifacts = Array.Empty<string>(),
                triageExternalEngineBakeoffFollowUp = "",
                triagePureSignalSafeBypassActionPresent = false,
                triagePureSignalSafeBypassActionId = "",
                triagePureSignalSafeBypassActionPriority = (int?)null,
                triagePureSignalSafeBypassActionStageId = "",
                triagePureSignalSafeBypassActionGateId = "",
                triagePureSignalSafeBypassActionCategory = "",
                triagePureSignalSafeBypassActionRequired = false,
                triagePureSignalSafeBypassActionManual = false,
                triagePureSignalSafeBypassCommandTemplate = "",
                triagePureSignalSafeBypassCommandStepCount = 0,
                triagePureSignalSafeBypassCommandSteps = Array.Empty<string>(),
                triagePureSignalSafeBypassManualAction = "",
                triagePureSignalSafeBypassExpectedArtifact = "",
                triagePureSignalSafeBypassExpectedArtifactCount = 0,
                triagePureSignalSafeBypassExpectedArtifacts = Array.Empty<string>(),
                triagePureSignalSafeBypassFollowUp = "",
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
            Assert.False(validationRoot.GetProperty("liveAcceptanceCycleTriageExternalEngineBakeoffActionPresent").GetBoolean());
            Assert.Equal("", validationRoot.GetProperty("liveAcceptanceCycleTriageExternalEngineBakeoffActionId").GetString());
            Assert.False(validationRoot.GetProperty("liveAcceptanceCycleTriagePureSignalSafeBypassActionPresent").GetBoolean());
            Assert.Equal("", validationRoot.GetProperty("liveAcceptanceCycleTriagePureSignalSafeBypassActionId").GetString());
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
            Assert.False(summaryRoot.GetProperty("liveAcceptanceCycleTriageExternalEngineBakeoffActionPresent").GetBoolean());
            Assert.False(summaryRoot.GetProperty("liveAcceptanceCycleTriagePureSignalSafeBypassActionPresent").GetBoolean());

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
            var externalBakeoffCommandSteps = new[]
            {
                "powershell -NoProfile -ExecutionPolicy Bypass -File tools\\compare-dsp-fixture-metrics.ps1 -BaselinePath \"$bundleDir\\artifacts\\current-zeus-fixtures.json\" -CandidatePath \"$bundleDir\\artifacts\\external-engine.speexdsp.fixtures.json\" -CandidateComparisonId candidate-external-engine-opt-in -FailOnRegression",
                "powershell -NoProfile -ExecutionPolicy Bypass -File tools\\run-dsp-live-diagnostics-matrix.ps1 -BundleDir \"$bundleDir\" -ComparisonId candidate-external-engine-opt-in -ScenarioIds ssb-like-speech-post-demod,agc-disabled-no-pumping,noise-only-gating -IndexPath \"$bundleDir\\artifacts\\live-diagnostics-trace-index.external-engine.speexdsp.json\" -ReportPath \"$bundleDir\\artifacts\\live-diagnostics-matrix-report.external-engine.speexdsp.json\" -Samples 60 -IntervalMs 1000 -ContinueOnError",
                "powershell -NoProfile -ExecutionPolicy Bypass -File tools\\validate-dsp-modernization-bundle.ps1 -BundleDir \"$bundleDir\" -RequireArtifactFiles -ReportPath \"$bundleDir\\validation-report.json\"",
                "powershell -NoProfile -ExecutionPolicy Bypass -File tools\\summarize-dsp-modernization-validation-report.ps1 -BundleDir \"$bundleDir\" -ReportPath \"$bundleDir\\validation-triage-report.json\" -MarkdownPath \"$bundleDir\\validation-triage-report.md\" -FailOnIssues"
            };
            var externalBakeoffExpectedArtifacts = new[]
            {
                "artifacts/dsp-fixture-metric-comparison.json",
                "artifacts/live-diagnostics-trace-index.external-engine.speexdsp.json",
                "artifacts/live-diagnostics-matrix-report.external-engine.speexdsp.json",
                "validation-report.json",
                "validation-triage-report.json",
                "validation-triage-report.md"
            };
            var pureSignalCommandSteps = new[]
            {
                "powershell -NoProfile -ExecutionPolicy Bypass -File tools\\summarize-dsp-puresignal-bench.ps1 -BundleDir \"$bundleDir\" -DisabledTracePath \"$bundleDir\\artifacts\\puresignal-disabled.json\" -EnabledTracePath \"$bundleDir\\artifacts\\puresignal-enabled.json\" -ReportPath \"$bundleDir\\artifacts\\puresignal-safe-bypass-report.json\" -Force"
            };
            var pureSignalExpectedArtifacts = new[]
            {
                "artifacts/puresignal-safe-bypass-report.json"
            };
            await File.WriteAllTextAsync(
                Path.Combine(bundleDir, "validation-triage-report.json"),
                JsonSerializer.Serialize(new
                {
                    requiredLiveAcceptanceArtifactProblemCount = 0,
                    acceptanceActionPlanCount = 3,
                    acceptanceRequiredActionCount = 1,
                    acceptanceManualActionCount = 2,
                    acceptanceActionCategoryCounts = new object[]
                    {
                        new { category = "live-diagnostics", count = 1 },
                        new { category = "external-dsp-ml", count = 1 },
                        new { category = "tx-puresignal", count = 1 }
                    },
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
                    primaryAcceptanceFollowUp = "Rerun strict validation after promotion.",
                    acceptanceActionPlan = new object[]
                    {
                        new
                        {
                            actionId = "promote-matrix-mixed-weak-strong-window",
                            priority = 78,
                            stageId = "opt-in-candidate-comparison",
                            gateId = "live-history-mixed-weak-strong",
                            category = "live-diagnostics",
                            requiredForAcceptance = true,
                            blocksDefaultChange = true,
                            commandTemplate = "powershell -NoProfile -ExecutionPolicy Bypass -File tools\\run-dsp-live-diagnostics-matrix.ps1 -BundleDir \"$bundleDir\" -ComparisonId nr5-spnr",
                            commandStepCount = 1,
                            commandSteps = new[]
                            {
                                "powershell -NoProfile -ExecutionPolicy Bypass -File tools\\run-dsp-live-diagnostics-matrix.ps1 -BundleDir \"$bundleDir\" -ComparisonId nr5-spnr"
                            },
                            manualAction = "",
                            expectedArtifact = "artifacts/live-diagnostics-history.json",
                            expectedArtifactCount = 1,
                            expectedArtifacts = new[] { "artifacts/live-diagnostics-history.json" },
                            followUp = "Rerun strict validation after promotion."
                        },
                        new
                        {
                            actionId = "run-first-safe-external-engine-bakeoff",
                            priority = 72,
                            stageId = "external-dsp-ml-bakeoff",
                            gateId = "external-engine-bakeoff",
                            category = "external-dsp-ml",
                            requiredForAcceptance = false,
                            blocksDefaultChange = false,
                            commandTemplate = externalBakeoffCommandSteps[0],
                            commandStepCount = externalBakeoffCommandSteps.Length,
                            commandSteps = externalBakeoffCommandSteps,
                            manualAction = "Produce or enable only the post-demod, operator-opt-in 'speexdsp' candidate path before running these comparisons. Do not route raw WDSP IQ, TX audio, TX monitor, or PureSignal feedback through the external engine.",
                            expectedArtifact = "artifacts/dsp-fixture-metric-comparison.json",
                            expectedArtifactCount = externalBakeoffExpectedArtifacts.Length,
                            expectedArtifacts = externalBakeoffExpectedArtifacts,
                            followUp = "Treat this as exploratory opt-in evidence only. External DSP/ML remains post-demod and off by default until fixture metrics, G2 live evidence, operator notes, package/license review, and cross-radio validation all pass."
                        },
                        new
                        {
                            actionId = "capture-puresignal-safe-bypass-bench",
                            priority = 68,
                            stageId = "g2-first-pass-evidence",
                            gateId = "puresignal-safe-bypass",
                            category = "tx-puresignal",
                            requiredForAcceptance = false,
                            blocksDefaultChange = true,
                            commandTemplate = pureSignalCommandSteps[0],
                            commandStepCount = pureSignalCommandSteps.Length,
                            commandSteps = pureSignalCommandSteps,
                            manualAction = "On G2, capture TX/PureSignal bench traces with PureSignal disabled/bypassed and enabled before running the summary command. Do not route external DSP/ML, TX monitor audio, or default profile changes into the PureSignal feedback path.",
                            expectedArtifact = "artifacts/puresignal-safe-bypass-report.json",
                            expectedArtifactCount = pureSignalExpectedArtifacts.Length,
                            expectedArtifacts = pureSignalExpectedArtifacts,
                            followUp = "Rerun strict validation and validation triage; TX profile graduation remains blocked until the report is ready and defaultBehaviorChangeApproved remains false."
                        }
                    }
                }, CamelCaseJson));
            await File.WriteAllTextAsync(Path.Combine(bundleDir, "validation-triage-report.md"), "# Triage");

            var summaryPath = Path.Combine(artifactsDir, "live-acceptance-cycle-summary.json");
            var summary = new
            {
                schemaVersion = 8,
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
                triageAcceptanceActionPlanCount = 3,
                triageAcceptanceRequiredActionCount = 1,
                triageAcceptanceManualActionCount = 2,
                triageAcceptanceActionCategoryCounts = new object[]
                {
                    new { category = "live-diagnostics", count = 1 },
                    new { category = "external-dsp-ml", count = 1 },
                    new { category = "tx-puresignal", count = 1 }
                },
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
                triageExternalEngineBakeoffActionPresent = true,
                triageExternalEngineBakeoffActionId = "run-first-safe-external-engine-bakeoff",
                triageExternalEngineBakeoffActionPriority = 72,
                triageExternalEngineBakeoffActionStageId = "external-dsp-ml-bakeoff",
                triageExternalEngineBakeoffActionGateId = "external-engine-bakeoff",
                triageExternalEngineBakeoffActionCategory = "external-dsp-ml",
                triageExternalEngineBakeoffActionRequired = false,
                triageExternalEngineBakeoffActionManual = true,
                triageExternalEngineBakeoffCommandTemplate = externalBakeoffCommandSteps[0],
                triageExternalEngineBakeoffCommandStepCount = externalBakeoffCommandSteps.Length,
                triageExternalEngineBakeoffCommandSteps = externalBakeoffCommandSteps,
                triageExternalEngineBakeoffManualAction = "Produce or enable only the post-demod, operator-opt-in 'speexdsp' candidate path before running these comparisons. Do not route raw WDSP IQ, TX audio, TX monitor, or PureSignal feedback through the external engine.",
                triageExternalEngineBakeoffExpectedArtifact = "artifacts/dsp-fixture-metric-comparison.json",
                triageExternalEngineBakeoffExpectedArtifactCount = externalBakeoffExpectedArtifacts.Length,
                triageExternalEngineBakeoffExpectedArtifacts = externalBakeoffExpectedArtifacts,
                triageExternalEngineBakeoffFollowUp = "Treat this as exploratory opt-in evidence only. External DSP/ML remains post-demod and off by default until fixture metrics, G2 live evidence, operator notes, package/license review, and cross-radio validation all pass.",
                triagePureSignalSafeBypassActionPresent = true,
                triagePureSignalSafeBypassActionId = "capture-puresignal-safe-bypass-bench",
                triagePureSignalSafeBypassActionPriority = 68,
                triagePureSignalSafeBypassActionStageId = "g2-first-pass-evidence",
                triagePureSignalSafeBypassActionGateId = "puresignal-safe-bypass",
                triagePureSignalSafeBypassActionCategory = "tx-puresignal",
                triagePureSignalSafeBypassActionRequired = false,
                triagePureSignalSafeBypassActionManual = true,
                triagePureSignalSafeBypassCommandTemplate = pureSignalCommandSteps[0],
                triagePureSignalSafeBypassCommandStepCount = pureSignalCommandSteps.Length,
                triagePureSignalSafeBypassCommandSteps = pureSignalCommandSteps,
                triagePureSignalSafeBypassManualAction = "On G2, capture TX/PureSignal bench traces with PureSignal disabled/bypassed and enabled before running the summary command. Do not route external DSP/ML, TX monitor audio, or default profile changes into the PureSignal feedback path.",
                triagePureSignalSafeBypassExpectedArtifact = "artifacts/puresignal-safe-bypass-report.json",
                triagePureSignalSafeBypassExpectedArtifactCount = pureSignalExpectedArtifacts.Length,
                triagePureSignalSafeBypassExpectedArtifacts = pureSignalExpectedArtifacts,
                triagePureSignalSafeBypassFollowUp = "Rerun strict validation and validation triage; TX profile graduation remains blocked until the report is ready and defaultBehaviorChangeApproved remains false.",
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
            Assert.Equal(3, validationRoot.GetProperty("liveAcceptanceCycleTriageAcceptanceActionPlanCount").GetInt32());
            Assert.Equal(2, validationRoot.GetProperty("liveAcceptanceCycleTriageAcceptanceManualActionCount").GetInt32());
            Assert.Equal("promote-matrix-mixed-weak-strong-window", validationRoot.GetProperty("liveAcceptanceCycleTriagePrimaryAcceptanceActionId").GetString());
            Assert.Equal("live-diagnostics", validationRoot.GetProperty("liveAcceptanceCycleTriagePrimaryAcceptanceActionCategory").GetString());
            Assert.True(validationRoot.GetProperty("liveAcceptanceCycleTriagePrimaryAcceptanceActionRequired").GetBoolean());
            Assert.Equal(1, validationRoot.GetProperty("liveAcceptanceCycleTriagePrimaryAcceptanceCommandStepCount").GetInt32());
            Assert.Equal("artifacts/live-diagnostics-history.json", validationRoot.GetProperty("liveAcceptanceCycleTriagePrimaryAcceptanceExpectedArtifact").GetString());
            Assert.Contains(
                validationRoot.GetProperty("liveAcceptanceCycleTriagePrimaryAcceptanceCommandSteps").EnumerateArray(),
                step => (step.GetString() ?? "").Contains("-ComparisonId nr5-spnr", StringComparison.Ordinal));
            Assert.True(validationRoot.GetProperty("liveAcceptanceCycleTriageExternalEngineBakeoffActionPresent").GetBoolean());
            Assert.Equal("run-first-safe-external-engine-bakeoff", validationRoot.GetProperty("liveAcceptanceCycleTriageExternalEngineBakeoffActionId").GetString());
            Assert.Equal("external-dsp-ml", validationRoot.GetProperty("liveAcceptanceCycleTriageExternalEngineBakeoffActionCategory").GetString());
            Assert.False(validationRoot.GetProperty("liveAcceptanceCycleTriageExternalEngineBakeoffActionRequired").GetBoolean());
            Assert.True(validationRoot.GetProperty("liveAcceptanceCycleTriageExternalEngineBakeoffActionManual").GetBoolean());
            Assert.Equal(4, validationRoot.GetProperty("liveAcceptanceCycleTriageExternalEngineBakeoffCommandStepCount").GetInt32());
            Assert.Equal(6, validationRoot.GetProperty("liveAcceptanceCycleTriageExternalEngineBakeoffExpectedArtifactCount").GetInt32());
            Assert.Contains(
                validationRoot.GetProperty("liveAcceptanceCycleTriageExternalEngineBakeoffCommandSteps").EnumerateArray(),
                step => (step.GetString() ?? "").Contains("external-engine.speexdsp.fixtures.json", StringComparison.Ordinal));
            Assert.Contains("post-demod", validationRoot.GetProperty("liveAcceptanceCycleTriageExternalEngineBakeoffManualAction").GetString(), StringComparison.Ordinal);
            Assert.Contains("cross-radio validation", validationRoot.GetProperty("liveAcceptanceCycleTriageExternalEngineBakeoffFollowUp").GetString(), StringComparison.Ordinal);
            Assert.True(validationRoot.GetProperty("liveAcceptanceCycleTriagePureSignalSafeBypassActionPresent").GetBoolean());
            Assert.Equal("capture-puresignal-safe-bypass-bench", validationRoot.GetProperty("liveAcceptanceCycleTriagePureSignalSafeBypassActionId").GetString());
            Assert.Equal("tx-puresignal", validationRoot.GetProperty("liveAcceptanceCycleTriagePureSignalSafeBypassActionCategory").GetString());
            Assert.False(validationRoot.GetProperty("liveAcceptanceCycleTriagePureSignalSafeBypassActionRequired").GetBoolean());
            Assert.True(validationRoot.GetProperty("liveAcceptanceCycleTriagePureSignalSafeBypassActionManual").GetBoolean());
            Assert.Equal(1, validationRoot.GetProperty("liveAcceptanceCycleTriagePureSignalSafeBypassCommandStepCount").GetInt32());
            Assert.Equal(1, validationRoot.GetProperty("liveAcceptanceCycleTriagePureSignalSafeBypassExpectedArtifactCount").GetInt32());
            Assert.Contains(
                validationRoot.GetProperty("liveAcceptanceCycleTriagePureSignalSafeBypassCommandSteps").EnumerateArray(),
                step => (step.GetString() ?? "").Contains("summarize-dsp-puresignal-bench.ps1", StringComparison.Ordinal));
            Assert.Contains("PureSignal disabled", validationRoot.GetProperty("liveAcceptanceCycleTriagePureSignalSafeBypassManualAction").GetString(), StringComparison.Ordinal);
            Assert.Contains("defaultBehaviorChangeApproved remains false", validationRoot.GetProperty("liveAcceptanceCycleTriagePureSignalSafeBypassFollowUp").GetString(), StringComparison.Ordinal);

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
            Assert.True(summaryRoot.GetProperty("liveAcceptanceCycleTriageExternalEngineBakeoffActionPresent").GetBoolean());
            Assert.Equal("run-first-safe-external-engine-bakeoff", summaryRoot.GetProperty("liveAcceptanceCycleTriageExternalEngineBakeoffActionId").GetString());
            Assert.Equal("external-dsp-ml", summaryRoot.GetProperty("liveAcceptanceCycleTriageExternalEngineBakeoffActionCategory").GetString());
            Assert.Equal(4, summaryRoot.GetProperty("liveAcceptanceCycleTriageExternalEngineBakeoffCommandStepCount").GetInt32());
            Assert.True(summaryRoot.GetProperty("liveAcceptanceCycleTriagePureSignalSafeBypassActionPresent").GetBoolean());
            Assert.Equal("capture-puresignal-safe-bypass-bench", summaryRoot.GetProperty("liveAcceptanceCycleTriagePureSignalSafeBypassActionId").GetString());
            Assert.Equal("tx-puresignal", summaryRoot.GetProperty("liveAcceptanceCycleTriagePureSignalSafeBypassActionCategory").GetString());
            Assert.Equal(1, summaryRoot.GetProperty("liveAcceptanceCycleTriagePureSignalSafeBypassCommandStepCount").GetInt32());

            var markdown = await File.ReadAllTextAsync(summaryMarkdown);
            Assert.Contains("Triage external DSP/ML bakeoff action", markdown, StringComparison.Ordinal);
            Assert.Contains("run-first-safe-external-engine-bakeoff", markdown, StringComparison.Ordinal);
            Assert.Contains("external-engine.speexdsp.fixtures.json", markdown, StringComparison.Ordinal);
            Assert.Contains("post-demod", markdown, StringComparison.Ordinal);
            Assert.Contains("Triage PureSignal safe-bypass action", markdown, StringComparison.Ordinal);
            Assert.Contains("capture-puresignal-safe-bypass-bench", markdown, StringComparison.Ordinal);
            Assert.Contains("summarize-dsp-puresignal-bench.ps1", markdown, StringComparison.Ordinal);

            var staleSummary = JsonNode.Parse(await File.ReadAllTextAsync(summaryPath))?.AsObject()
                ?? throw new InvalidOperationException("Could not parse live acceptance summary.");
            staleSummary["triageExternalEngineBakeoffActionCategory"] = "stale-external-category";
            staleSummary["triagePureSignalSafeBypassActionCategory"] = "stale-puresignal-category";
            await File.WriteAllTextAsync(summaryPath, staleSummary.ToJsonString(CamelCaseJson));

            var staleValidationReport = Path.Combine(bundleDir, "validation-output-stale-external.json");
            var staleValidation = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "validate-dsp-modernization-bundle.ps1"),
                "-BundleDir", bundleDir,
                "-ArtifactManifestPath", manifestPath,
                "-ReportPath", staleValidationReport,
                "-AllowPreflight",
                "-JsonOnly");

            Assert.NotEqual(0, staleValidation.ExitCode);
            Assert.True(File.Exists(staleValidationReport), staleValidation.CombinedOutput);

            using var staleValidationDoc = JsonDocument.Parse(await File.ReadAllTextAsync(staleValidationReport));
            var staleWarningCodes = staleValidationDoc.RootElement
                .GetProperty("warnings")
                .EnumerateArray()
                .Select(warning => warning.GetProperty("code").GetString())
                .ToArray();
            Assert.Contains("live-acceptance-cycle-summary-triage-external-bakeoff-mismatch", staleWarningCodes);
            Assert.Contains("live-acceptance-cycle-summary-triage-puresignal-mismatch", staleWarningCodes);
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

            var crossRadioReport = artifacts.Single(artifact => artifact.GetProperty("id").GetString() == "cross-radio-validation-report");
            Assert.False(crossRadioReport.GetProperty("required").GetBoolean());
            Assert.Equal("cross-radio-validation-report-json", crossRadioReport.GetProperty("kind").GetString());
            Assert.Equal("tools/summarize-dsp-cross-radio-validation.ps1", crossRadioReport.GetProperty("source").GetString());
            Assert.Equal("artifacts/cross-radio-validation-report.json", crossRadioReport.GetProperty("path").GetString());

            var sourceDriftReport = artifacts.Single(artifact => artifact.GetProperty("id").GetString() == "wdsp-source-drift-report");
            Assert.False(sourceDriftReport.GetProperty("required").GetBoolean());
            Assert.Equal("wdsp-source-drift-report-json", sourceDriftReport.GetProperty("kind").GetString());
            Assert.Equal("tools/compare-wdsp-source-drift.ps1", sourceDriftReport.GetProperty("source").GetString());
            Assert.Equal("artifacts/wdsp-source-drift-report.json", sourceDriftReport.GetProperty("path").GetString());

            var nativeStageTimingReport = artifacts.Single(artifact => artifact.GetProperty("id").GetString() == "native-stage-timing-report");
            Assert.True(nativeStageTimingReport.GetProperty("required").GetBoolean());
            Assert.Equal("native-stage-timing-report-json", nativeStageTimingReport.GetProperty("kind").GetString());
            Assert.Equal("tools/summarize-dsp-native-stage-timing.ps1", nativeStageTimingReport.GetProperty("source").GetString());
            Assert.Equal("artifacts/native-stage-timing-report.json", nativeStageTimingReport.GetProperty("path").GetString());

            var g2RxPeakHuntReport = artifacts.Single(artifact => artifact.GetProperty("id").GetString() == "g2-rx-peak-hunt-report");
            Assert.False(g2RxPeakHuntReport.GetProperty("required").GetBoolean());
            Assert.Equal("g2-rx-peak-hunt-report-json", g2RxPeakHuntReport.GetProperty("kind").GetString());
            Assert.Equal("tools/run-dsp-g2-rx-peak-hunt.ps1", g2RxPeakHuntReport.GetProperty("source").GetString());
            Assert.Equal("artifacts/g2-rx-peak-hunt-report.json", g2RxPeakHuntReport.GetProperty("path").GetString());
            Assert.Equal(new[] { "nr5-spnr" }, ReadStringArray(g2RxPeakHuntReport, "comparisonIds"));

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
            Assert.DoesNotContain(unexpectedArtifactWarnings, message => message.Contains("g2-rx-peak-hunt-report", StringComparison.Ordinal));
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
    public async Task G2RxPeakHuntReportValidatesAndSummarizesRxOnlyEvidence()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell G2 peak-hunt validator smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-g2-rx-peak-hunt-ready-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            WriteSourcePlanScopeBundle(bundleDir);
            WriteG2RxPeakHuntArtifactManifest(bundleDir);
            WriteG2RxPeakHuntReport(bundleDir);

            var validationReport = Path.Combine(bundleDir, "validation-g2-rx-peak-hunt.json");
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
            Assert.True(validationRoot.GetProperty("g2RxPeakHuntReportPresent").GetBoolean());
            Assert.True(validationRoot.GetProperty("g2RxPeakHuntReportReady").GetBoolean());
            Assert.True(validationRoot.GetProperty("g2RxPeakHuntReportValid").GetBoolean());
            Assert.Equal("mixed-ready", validationRoot.GetProperty("g2RxPeakHuntReportStatus").GetString());
            Assert.True(validationRoot.GetProperty("g2RxPeakHuntOk").GetBoolean());
            Assert.Equal("", validationRoot.GetProperty("g2RxPeakHuntScanError").GetString());
            Assert.Equal("auto", validationRoot.GetProperty("g2RxPeakHuntRequestedBaseUrl").GetString());
            Assert.Equal("http://127.0.0.1:6060", validationRoot.GetProperty("g2RxPeakHuntBaseUrl").GetString());
            Assert.True(validationRoot.GetProperty("g2RxPeakHuntBaseUrlAutoDiscoverRequested").GetBoolean());
            Assert.True(validationRoot.GetProperty("g2RxPeakHuntBaseUrlAutoDiscovered").GetBoolean());
            Assert.Equal(1, validationRoot.GetProperty("g2RxPeakHuntBaseUrlProbeResultCount").GetInt32());
            Assert.True(validationRoot.GetProperty("g2RxPeakHuntSafetyRxOnly").GetBoolean());
            Assert.False(validationRoot.GetProperty("g2RxPeakHuntSafetyTxEndpointsTouched").GetBoolean());
            Assert.True(validationRoot.GetProperty("g2RxPeakHuntSafetyOriginalVfoRestored").GetBoolean());
            Assert.True(validationRoot.GetProperty("g2RxPeakHuntSafetyOriginalRadioLoRestored").GetBoolean());
            Assert.Equal(14255000L, validationRoot.GetProperty("g2RxPeakHuntHardwareOriginalRadioLoHz").GetInt64());
            Assert.Equal(14255000L, validationRoot.GetProperty("g2RxPeakHuntHardwareRestoredRadioLoHz").GetInt64());
            Assert.True(validationRoot.GetProperty("g2RxPeakHuntMixedWeakStrongReady").GetBoolean());
            Assert.Equal(2, validationRoot.GetProperty("g2RxPeakHuntPassCount").GetInt32());
            Assert.Equal(5, validationRoot.GetProperty("g2RxPeakHuntPassDelaySec").GetInt32());
            Assert.Equal(2, validationRoot.GetProperty("g2RxPeakHuntCompletedPassCount").GetInt32());
            Assert.Equal(2, validationRoot.GetProperty("g2RxPeakHuntScanPassCount").GetInt32());
            Assert.Equal(2, validationRoot.GetProperty("g2RxPeakHuntCandidateFrequencyHzCount").GetInt32());
            Assert.True(validationRoot.GetProperty("g2RxPeakHuntAutoPhoneCluster").GetBoolean());
            Assert.Equal(4, validationRoot.GetProperty("g2RxPeakHuntAutoPhoneClusterCandidateFrequencyHzCount").GetInt32());
            Assert.Equal(4, validationRoot.GetProperty("g2RxPeakHuntAutoPhoneClusterCandidateCount").GetInt32());
            Assert.Equal(3, validationRoot.GetProperty("g2RxPeakHuntAutoPhoneClusterExactCandidateCount").GetInt32());
            Assert.Equal(1, validationRoot.GetProperty("g2RxPeakHuntAutoPhoneClusterNeighborCandidateCount").GetInt32());
            Assert.Equal(12, validationRoot.GetProperty("g2RxPeakHuntAutoPhoneClusterMaxCandidates").GetInt32());
            Assert.Equal(12, validationRoot.GetProperty("g2RxPeakHuntAutoPhoneClusterLookbackHours").GetInt32());
            Assert.Equal(1, validationRoot.GetProperty("g2RxPeakHuntAutoPhoneClusterMinSpeechSamples").GetInt32());
            Assert.Equal(14150000L, validationRoot.GetProperty("g2RxPeakHuntAutoPhoneClusterBandLowHz").GetInt64());
            Assert.Equal(14350000L, validationRoot.GetProperty("g2RxPeakHuntAutoPhoneClusterBandHighHz").GetInt64());
            Assert.Equal(2, validationRoot.GetProperty("g2RxPeakHuntOperatorCandidateCount").GetInt32());
            Assert.Equal(2, validationRoot.GetProperty("g2RxPeakHuntActualRunCount").GetInt32());
            Assert.Equal(18, validationRoot.GetProperty("g2RxPeakHuntWeakInputSampleCount").GetInt32());
            Assert.Equal(14, validationRoot.GetProperty("g2RxPeakHuntStrongInputSampleCount").GetInt32());
            Assert.Equal(13, validationRoot.GetProperty("g2RxPeakHuntSpeechQualifiedWeakInputSampleCount").GetInt32());
            Assert.Equal(11, validationRoot.GetProperty("g2RxPeakHuntSpeechQualifiedStrongInputSampleCount").GetInt32());
            Assert.Equal(9, validationRoot.GetProperty("g2RxPeakHuntPassbandQualifiedWeakInputSampleCount").GetInt32());
            Assert.Equal(8, validationRoot.GetProperty("g2RxPeakHuntPassbandQualifiedStrongInputSampleCount").GetInt32());
            Assert.Equal(20, validationRoot.GetProperty("g2RxPeakHuntFrontendNearPassbandSampleCount").GetInt32());
            Assert.Equal(1, validationRoot.GetProperty("g2RxPeakHuntRetuneAttemptCount").GetInt32());
            Assert.Equal(14250000L, validationRoot.GetProperty("g2RxPeakHuntBestFrequencyHz").GetInt64());
            Assert.Equal(2, validationRoot.GetProperty("g2RxPeakHuntReferencedWindowCount").GetInt32());
            Assert.Equal(2, validationRoot.GetProperty("g2RxPeakHuntReferencedWindowReadyCount").GetInt32());
            Assert.Equal(0, validationRoot.GetProperty("g2RxPeakHuntReferencedWindowProblemCount").GetInt32());
            Assert.Equal(0, validationRoot.GetProperty("g2RxPeakHuntReferencedWindowNonPortableCount").GetInt32());
            Assert.Equal("artifacts/g2-rx-peak-hunt/frontend-top-peak-14250000/window-01/live-diagnostics-watch.jsonl", validationRoot.GetProperty("g2RxPeakHuntBestJsonlPath").GetString());

            var referencedFiles = validationRoot.GetProperty("artifactReferencedFiles")
                .EnumerateArray()
                .Where(file => file.GetProperty("sourceType").GetString() == "g2-rx-peak-hunt-window")
                .ToArray();
            Assert.Equal(2, referencedFiles.Length);
            Assert.All(referencedFiles, file =>
            {
                Assert.True(file.GetProperty("ok").GetBoolean());
                Assert.Equal("matched", file.GetProperty("sourceStatus").GetString());
                Assert.Equal("matched", file.GetProperty("jsonlStatus").GetString());
                Assert.StartsWith("artifacts/g2-rx-peak-hunt/", file.GetProperty("path").GetString(), StringComparison.Ordinal);
            });

            var peakHuntIssueCodes = validationRoot.GetProperty("warnings")
                .EnumerateArray()
                .Concat(validationRoot.GetProperty("errors").EnumerateArray())
                .Select(issue => issue.GetProperty("code").GetString() ?? "")
                .Where(code => code.StartsWith("g2-rx-peak-hunt-", StringComparison.Ordinal))
                .ToArray();
            Assert.Empty(peakHuntIssueCodes);

            var summaryReport = Path.Combine(bundleDir, "summary-g2-rx-peak-hunt.json");
            var summaryMarkdown = Path.Combine(bundleDir, "summary-g2-rx-peak-hunt.md");
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
            Assert.True(summaryRoot.GetProperty("g2RxPeakHuntReportPresent").GetBoolean());
            Assert.Equal("mixed-ready", summaryRoot.GetProperty("g2RxPeakHuntReportStatus").GetString());
            Assert.True(summaryRoot.GetProperty("g2RxPeakHuntOk").GetBoolean());
            Assert.Equal("auto", summaryRoot.GetProperty("g2RxPeakHuntRequestedBaseUrl").GetString());
            Assert.Equal("http://127.0.0.1:6060", summaryRoot.GetProperty("g2RxPeakHuntBaseUrl").GetString());
            Assert.True(summaryRoot.GetProperty("g2RxPeakHuntBaseUrlAutoDiscovered").GetBoolean());
            Assert.Equal(2, summaryRoot.GetProperty("g2RxPeakHuntCompletedPassCount").GetInt32());
            Assert.Equal(2, summaryRoot.GetProperty("g2RxPeakHuntOperatorCandidateCount").GetInt32());
            Assert.True(summaryRoot.GetProperty("g2RxPeakHuntSafetyOriginalRadioLoRestored").GetBoolean());
            Assert.Equal(14255000L, summaryRoot.GetProperty("g2RxPeakHuntHardwareOriginalRadioLoHz").GetInt64());
            Assert.Equal(14255000L, summaryRoot.GetProperty("g2RxPeakHuntHardwareRestoredRadioLoHz").GetInt64());
            Assert.True(summaryRoot.GetProperty("g2RxPeakHuntAutoPhoneCluster").GetBoolean());
            Assert.Equal(4, summaryRoot.GetProperty("g2RxPeakHuntAutoPhoneClusterCandidateCount").GetInt32());
            Assert.Equal(3, summaryRoot.GetProperty("g2RxPeakHuntAutoPhoneClusterExactCandidateCount").GetInt32());
            Assert.Equal(1, summaryRoot.GetProperty("g2RxPeakHuntAutoPhoneClusterNeighborCandidateCount").GetInt32());
            Assert.Equal(14150000L, summaryRoot.GetProperty("g2RxPeakHuntAutoPhoneClusterBandLowHz").GetInt64());
            Assert.Equal(14350000L, summaryRoot.GetProperty("g2RxPeakHuntAutoPhoneClusterBandHighHz").GetInt64());
            Assert.Equal(13, summaryRoot.GetProperty("g2RxPeakHuntSpeechQualifiedWeakInputSampleCount").GetInt32());
            Assert.Equal(11, summaryRoot.GetProperty("g2RxPeakHuntSpeechQualifiedStrongInputSampleCount").GetInt32());
            Assert.Equal(9, summaryRoot.GetProperty("g2RxPeakHuntPassbandQualifiedWeakInputSampleCount").GetInt32());
            Assert.Equal(8, summaryRoot.GetProperty("g2RxPeakHuntPassbandQualifiedStrongInputSampleCount").GetInt32());
            Assert.Equal(20, summaryRoot.GetProperty("g2RxPeakHuntFrontendNearPassbandSampleCount").GetInt32());
            Assert.Equal(14250000L, summaryRoot.GetProperty("g2RxPeakHuntBestFrequencyHz").GetInt64());
            Assert.Equal(2, summaryRoot.GetProperty("g2RxPeakHuntReferencedWindowReadyCount").GetInt32());

            var peakHuntGate = summaryRoot.GetProperty("evidenceGates")
                .EnumerateArray()
                .Single(gate => gate.GetProperty("gateId").GetString() == "g2-rx-peak-hunt");
            Assert.True(peakHuntGate.GetProperty("ready").GetBoolean());
            Assert.False(peakHuntGate.GetProperty("requiredForAcceptance").GetBoolean());
            Assert.Equal("mixed-ready", peakHuntGate.GetProperty("status").GetString());

            var markdown = await File.ReadAllTextAsync(summaryMarkdown);
            Assert.Contains("G2 RX Peak-Hunt Evidence", markdown, StringComparison.Ordinal);
            Assert.Contains("Base URL requested/resolved/auto-discovered", markdown, StringComparison.Ordinal);
            Assert.Contains("Scan passes completed/planned/delay", markdown, StringComparison.Ordinal);
            Assert.Contains("Operator candidate frequencies/count", markdown, StringComparison.Ordinal);
            Assert.Contains("Auto phone cluster enabled/candidates/exact/neighbor/lookback/band", markdown, StringComparison.Ordinal);
            Assert.Contains("radio LO restored", markdown, StringComparison.Ordinal);
            Assert.Contains("14150000-14350000", markdown, StringComparison.Ordinal);
            Assert.Contains("Speech-qualified weak/strong samples", markdown, StringComparison.Ordinal);
            Assert.Contains("Passband-qualified weak/strong samples", markdown, StringComparison.Ordinal);
            Assert.Contains("Frontend near-passband samples", markdown, StringComparison.Ordinal);
            Assert.Contains("14250000", markdown, StringComparison.Ordinal);
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
    public async Task G2RxPeakHuntReportRejectsNonPortableWindowPaths()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell G2 peak-hunt validator smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-g2-rx-peak-hunt-nonportable-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            WriteSourcePlanScopeBundle(bundleDir);
            WriteG2RxPeakHuntArtifactManifest(bundleDir);
            WriteG2RxPeakHuntReport(bundleDir, nonPortableWindowPaths: true);

            var validationReport = Path.Combine(bundleDir, "validation-g2-rx-peak-hunt-nonportable.json");
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
            Assert.True(validationRoot.GetProperty("g2RxPeakHuntReportPresent").GetBoolean());
            Assert.False(validationRoot.GetProperty("g2RxPeakHuntReportReady").GetBoolean());
            Assert.False(validationRoot.GetProperty("g2RxPeakHuntReportValid").GetBoolean());
            Assert.Equal("invalid", validationRoot.GetProperty("g2RxPeakHuntReportStatus").GetString());
            Assert.Equal(2, validationRoot.GetProperty("g2RxPeakHuntReferencedWindowCount").GetInt32());
            Assert.Equal(0, validationRoot.GetProperty("g2RxPeakHuntReferencedWindowReadyCount").GetInt32());
            Assert.Equal(2, validationRoot.GetProperty("g2RxPeakHuntReferencedWindowProblemCount").GetInt32());
            Assert.True(validationRoot.GetProperty("g2RxPeakHuntReferencedWindowNonPortableCount").GetInt32() >= 2);

            var issueCodes = validationRoot.GetProperty("warnings")
                .EnumerateArray()
                .Concat(validationRoot.GetProperty("errors").EnumerateArray())
                .Select(issue => issue.GetProperty("code").GetString() ?? "")
                .ToArray();
            Assert.Contains("g2-rx-peak-hunt-window-report-path-not-portable", issueCodes);
            Assert.Contains("g2-rx-peak-hunt-window-jsonl-path-not-portable", issueCodes);
        }
        finally
        {
            if (Directory.Exists(bundleDir))
            {
                Directory.Delete(bundleDir, recursive: true);
            }
        }
    }

    private static void WriteG2RxPeakHuntArtifactManifest(string bundleDir)
    {
        Directory.CreateDirectory(Path.Combine(bundleDir, "artifacts"));

        var manifest = new
        {
            schemaVersion = 1,
            artifacts = new object[]
            {
                new
                {
                    id = "g2-rx-peak-hunt-report",
                    kind = "g2-rx-peak-hunt-report-json",
                    source = "tools/run-dsp-g2-rx-peak-hunt.ps1",
                    path = "artifacts/g2-rx-peak-hunt-report.json",
                    required = false,
                    comparisonIds = new[] { "nr5-spnr" }
                }
            }
        };

        File.WriteAllText(
            Path.Combine(bundleDir, "artifact-manifest.json"),
            JsonSerializer.Serialize(manifest, CamelCaseJson));
    }

    private static void WriteG2RxPeakHuntReport(string bundleDir, bool nonPortableWindowPaths = false)
    {
        var currentRunReportPath = nonPortableWindowPaths
            ? Path.Combine(Path.GetTempPath(), $"zeus-g2-rx-peak-hunt-outside-{Guid.NewGuid():N}", "current-vfo", "live-diagnostics-watch.json")
            : "artifacts/g2-rx-peak-hunt/current-vfo-14290000/window-01/live-diagnostics-watch.json";
        var currentRunJsonlPath = nonPortableWindowPaths
            ? Path.ChangeExtension(currentRunReportPath, ".jsonl")
            : "artifacts/g2-rx-peak-hunt/current-vfo-14290000/window-01/live-diagnostics-watch.jsonl";
        var bestRunReportPath = nonPortableWindowPaths
            ? Path.Combine(Path.GetTempPath(), $"zeus-g2-rx-peak-hunt-outside-{Guid.NewGuid():N}", "frontend-top-peak", "live-diagnostics-watch.json")
            : "artifacts/g2-rx-peak-hunt/frontend-top-peak-14250000/window-01/live-diagnostics-watch.json";
        var bestRunJsonlPath = nonPortableWindowPaths
            ? Path.ChangeExtension(bestRunReportPath, ".jsonl")
            : "artifacts/g2-rx-peak-hunt/frontend-top-peak-14250000/window-01/live-diagnostics-watch.jsonl";

        var report = new
        {
            schemaVersion = 1,
            tool = "run-dsp-g2-rx-peak-hunt",
            generatedUtc = "2026-06-17T12:00:00.0000000Z",
            startedUtc = "2026-06-17T11:59:30.0000000Z",
            completedUtc = "2026-06-17T12:00:00.0000000Z",
            durationMs = 30000,
            ok = true,
            scanError = (string?)null,
            requestedBaseUrl = "auto",
            baseUrl = "http://127.0.0.1:6060",
            baseUrlAutoDiscoverRequested = true,
            baseUrlAutoDiscovered = true,
            baseUrlAutoDiscoverError = "",
            baseUrlProbeResults = new object[]
            {
                new
                {
                    baseUrl = "http://127.0.0.1:6060",
                    ok = true,
                    connectionStatus = "Connected",
                    vfoHz = 14290000,
                    error = ""
                }
            },
            outputDir = "artifacts/g2-rx-peak-hunt",
            label = "synthetic-ready",
            comparisonId = "nr5-spnr",
            allowRetune = true,
            skipCurrentVfo = false,
            stopOnReady = true,
            samplesPerWindow = 24,
            intervalMs = 250,
            windowsPerPeak = 1,
            passCount = 2,
            passDelaySec = 5,
            completedPassCount = 2,
            scanPassCount = 2,
            candidateFrequencyHz = new[] { 14260000L, 14243000L },
            autoPhoneCluster = true,
            autoPhoneClusterMaxCandidates = 12,
            autoPhoneClusterLookbackHours = 12,
            autoPhoneClusterMinSpeechSamples = 1,
            autoPhoneClusterBandLowHz = 14150000L,
            autoPhoneClusterBandHighHz = 14350000L,
            autoPhoneClusterCandidateFrequencyHz = new[] { 14240000L, 14270000L, 14277000L, 14280000L },
            autoPhoneClusterCandidateCount = 4,
            autoPhoneClusterExactCandidateCount = 3,
            autoPhoneClusterNeighborCandidateCount = 1,
            operatorCandidateCount = 2,
            maxPeaks = 6,
            peakMergeHz = 1000,
            minPeakSnrDb = 8.0,
            settleMs = 3000,
            safety = new
            {
                rxOnly = true,
                txEndpointsTouched = false,
                vfoRetuneRequiresAllowRetune = true,
                originalVfoRestoreAttempted = true,
                originalVfoRestored = true,
                originalRadioLoRestoreAttempted = true,
                originalRadioLoRestored = true,
                restoreError = (string?)null
            },
            hardware = new
            {
                connectionStatus = "Connected",
                endpoint = "192.168.1.25:1024",
                effectiveBoard = "OrionMkII",
                orionMkIIVariant = "G2",
                originalVfoHz = 14290000,
                restoredVfoHz = 14290000,
                originalRadioLoHz = 14255000,
                restoredRadioLoHz = 14255000,
                mode = "USB",
                sampleRate = 384000
            },
            liveDiagnostics = new
            {
                status = "ready-for-live-benchmark",
                readyForLiveBenchmark = true,
                wdspActive = true,
                wdspNativeLoadable = true,
                requestedNrMode = "NR5",
                effectiveNrMode = "NR5",
                readyForNr5Tuning = true,
                frontendSceneFresh = true
            },
            frontendScene = new
            {
                status = "fresh",
                fresh = true,
                signalProfile = "speech-with-adjacent-strong",
                maxSnrDb = 22.5,
                coherentMaxSnrDb = 19.0,
                topPeakCount = 2
            },
            peakCandidates = new object[]
            {
                new
                {
                    pass = 1,
                    rank = 1,
                    frequencyHz = 14250000,
                    offsetHz = -40000,
                    snrDb = 22.5,
                    source = "frontend-top-peak"
                }
            },
            plannedRunCount = 2,
            actualRunCount = 2,
            failedRunCount = 0,
            mixedWeakStrongReady = true,
            mixedWeakStrongReadyRunCount = 1,
            weakInputSampleCount = 18,
            strongInputSampleCount = 14,
            speechQualifiedWeakInputSampleCount = 13,
            speechQualifiedStrongInputSampleCount = 11,
            passbandQualifiedWeakInputSampleCount = 9,
            passbandQualifiedStrongInputSampleCount = 8,
            frontendNearPassbandSampleCount = 20,
            candidateWeakLossSampleCount = 0,
            hotMakeupSampleCount = 0,
            hardBlockerSampleCount = 0,
            agcPumpingRiskRunCount = 0,
            bestRun = new object[]
            {
                new
                {
                    ok = true,
                    pass = 1,
                    frequencyHz = 14250000,
                    candidateSource = "frontend-top-peak",
                    candidateRank = 1,
                    candidateSnrDb = 22.5,
                    candidateOffsetHz = -40000,
                    window = 1,
                    reportPath = bestRunReportPath,
                    jsonlPath = bestRunJsonlPath,
                    trendStatus = "ready",
                    readyForBenchmarkTrace = true,
                    okSampleCount = 24,
                    failedSampleCount = 0,
                    readySampleCount = 24,
                    hardBlockerSampleCount = 0,
                    nr5TuningTraceStatus = "ready",
                    nr5TuningReadySampleCount = 24,
                    agcStabilityStatus = "stable",
                    agcPumpingRisk = false,
                    weakInputSampleCount = 10,
                    strongInputSampleCount = 9,
                    weakRecoveredSampleCount = 7,
                    weakDropoutSampleCount = 1,
                    weakDropoutCandidateLossSampleCount = 0,
                    hotMakeupSampleCount = 0,
                    weakStrongOutputGapDb = 1.5,
                    weakStrongFinalAudioGapDb = 1.2,
                    speechQualifiedWeakInputSampleCount = 7,
                    speechQualifiedStrongInputSampleCount = 6,
                    speechQualifiedWeakStrongOutputGapDb = 1.4,
                    speechQualifiedWeakStrongFinalAudioGapDb = 1.1,
                    speechQualifiedMixedWeakStrongEvidenceReady = true,
                    speechQualifiedWeakStrongOutputParityReady = true,
                    speechQualifiedWeakStrongFinalAudioParityReady = true,
                    speechQualifiedMixedWeakStrongEvidenceStatus = "ready-final-audio",
                    passbandQualifiedWeakInputSampleCount = 5,
                    passbandQualifiedStrongInputSampleCount = 4,
                    passbandQualifiedWeakStrongOutputGapDb = 1.3,
                    passbandQualifiedWeakStrongFinalAudioGapDb = 1.0,
                    passbandQualifiedMixedWeakStrongEvidenceReady = true,
                    passbandQualifiedWeakStrongOutputParityReady = true,
                    passbandQualifiedWeakStrongFinalAudioParityReady = true,
                    passbandQualifiedMixedWeakStrongEvidenceStatus = "ready-final-audio",
                    mixedWeakStrongEvidenceReady = true,
                    weakStrongOutputParityReady = true,
                    weakStrongFinalAudioParityReady = true,
                    mixedWeakStrongEvidenceStatus = "ready",
                    frontendTopPeakSampleCount = 12,
                    frontendNearPassbandSampleCount = 12,
                    frontendNearPassbandThresholdHz = 3000,
                    score = 52.0
                }
            },
            retuneAttempts = new object[]
            {
                new
                {
                    pass = 1,
                    frequencyHz = 14250000,
                    source = "frontend-top-peak",
                    startedUtc = "2026-06-17T11:59:40.0000000Z",
                    ok = true,
                    error = (string?)null,
                    completedUtc = "2026-06-17T11:59:43.0000000Z"
                }
            },
            operatorCandidates = new object[]
            {
                new
                {
                    rank = 1,
                    source = "operator-frequency",
                    frequencyHz = 14260000,
                    offsetHz = -30000
                },
                new
                {
                    rank = 2,
                    source = "operator-frequency",
                    frequencyHz = 14243000,
                    offsetHz = -47000
                }
            },
            autoPhoneClusterCandidates = new object[]
            {
                new
                {
                    rank = 1,
                    source = "recent-phone-cluster",
                    frequencyHz = 14240000,
                    offsetHz = -50000,
                    evidenceScore = 134.0,
                    evidenceSpeechWeak = 6,
                    evidenceSpeechStrong = 0,
                    evidencePassbandWeak = 3,
                    evidencePassbandStrong = 0,
                    evidenceNearPassband = 3,
                    evidenceCandidateSource = "operator-frequency",
                    evidenceStatus = "missing-strong-input",
                    evidenceReportPath = "artifacts/g2-rx-peak-hunt-report.previous.json"
                },
                new
                {
                    rank = 2,
                    source = "recent-phone-cluster",
                    frequencyHz = 14270000,
                    offsetHz = -20000,
                    evidenceScore = 124.0,
                    evidenceSpeechWeak = 3,
                    evidenceSpeechStrong = 0,
                    evidencePassbandWeak = 2,
                    evidencePassbandStrong = 0,
                    evidenceNearPassband = 4,
                    evidenceCandidateSource = "operator-frequency",
                    evidenceStatus = "missing-strong-input",
                    evidenceReportPath = "artifacts/g2-rx-peak-hunt-report.previous.json"
                },
                new
                {
                    rank = 3,
                    source = "recent-phone-cluster",
                    frequencyHz = 14277000,
                    offsetHz = -13000,
                    evidenceScore = 119.0,
                    evidenceSpeechWeak = 4,
                    evidenceSpeechStrong = 0,
                    evidencePassbandWeak = 2,
                    evidencePassbandStrong = 0,
                    evidenceNearPassband = 2,
                    evidenceCandidateSource = "operator-frequency",
                    evidenceStatus = "missing-strong-input",
                    evidenceReportPath = "artifacts/g2-rx-peak-hunt-report.previous.json"
                },
                new
                {
                    rank = 4,
                    source = "recent-phone-cluster-neighbor",
                    frequencyHz = 14280000,
                    offsetHz = -10000,
                    evidenceScore = 70.0,
                    evidenceSpeechWeak = 4,
                    evidenceSpeechStrong = 0,
                    evidencePassbandWeak = 2,
                    evidencePassbandStrong = 0,
                    evidenceNearPassband = 2,
                    evidenceCandidateSource = "operator-frequency",
                    evidenceStatus = "missing-strong-input",
                    evidenceReportPath = "artifacts/g2-rx-peak-hunt-report.previous.json",
                    evidenceNeighborOfFrequencyHz = 14277000,
                    evidenceNeighborOffsetHz = 3000
                }
            },
            scanPasses = new object[]
            {
                new
                {
                    pass = 1,
                    startedUtc = "2026-06-17T11:59:30.0000000Z",
                    completedUtc = "2026-06-17T11:59:45.0000000Z",
                    operatorCandidateCount = 2,
                    peakCandidateCount = 1,
                    candidateCount = 2,
                    plannedRunCount = 2,
                    stoppedEarly = false
                },
                new
                {
                    pass = 2,
                    startedUtc = "2026-06-17T11:59:50.0000000Z",
                    completedUtc = "2026-06-17T12:00:00.0000000Z",
                    operatorCandidateCount = 2,
                    peakCandidateCount = 1,
                    candidateCount = 0,
                    plannedRunCount = 0,
                    stoppedEarly = true
                }
            },
            stoppedEarly = true,
            runs = new object[]
            {
                new
                {
                    ok = true,
                    pass = 1,
                    frequencyHz = 14290000,
                    candidateSource = "current-vfo",
                    window = 1,
                    reportPath = currentRunReportPath,
                    jsonlPath = currentRunJsonlPath,
                    weakInputSampleCount = 8,
                    strongInputSampleCount = 5,
                    speechQualifiedWeakInputSampleCount = 6,
                    speechQualifiedStrongInputSampleCount = 5,
                    passbandQualifiedWeakInputSampleCount = 4,
                    passbandQualifiedStrongInputSampleCount = 4,
                    weakDropoutCandidateLossSampleCount = 0,
                    hotMakeupSampleCount = 0,
                    hardBlockerSampleCount = 0,
                    agcPumpingRisk = false,
                    mixedWeakStrongEvidenceReady = false,
                    mixedWeakStrongEvidenceStatus = "mixed-not-ready",
                    frontendTopPeakSampleCount = 8,
                    frontendNearPassbandSampleCount = 8,
                    frontendNearPassbandThresholdHz = 3000,
                    score = 18.0
                },
                new
                {
                    ok = true,
                    pass = 1,
                    frequencyHz = 14250000,
                    candidateSource = "frontend-top-peak",
                    window = 1,
                    reportPath = bestRunReportPath,
                    jsonlPath = bestRunJsonlPath,
                    weakInputSampleCount = 10,
                    strongInputSampleCount = 9,
                    speechQualifiedWeakInputSampleCount = 7,
                    speechQualifiedStrongInputSampleCount = 6,
                    passbandQualifiedWeakInputSampleCount = 5,
                    passbandQualifiedStrongInputSampleCount = 4,
                    weakDropoutCandidateLossSampleCount = 0,
                    hotMakeupSampleCount = 0,
                    hardBlockerSampleCount = 0,
                    agcPumpingRisk = false,
                    mixedWeakStrongEvidenceReady = true,
                    mixedWeakStrongEvidenceStatus = "ready",
                    frontendTopPeakSampleCount = 12,
                    frontendNearPassbandSampleCount = 12,
                    frontendNearPassbandThresholdHz = 3000,
                    score = 52.0
                }
            },
            recommendations = new[]
            {
                "A mixed weak+strong NR5/SPNR run was found; promote the best run into live history before tuning defaults."
            }
        };

        File.WriteAllText(
            Path.Combine(bundleDir, "artifacts", "g2-rx-peak-hunt-report.json"),
            JsonSerializer.Serialize(report, CamelCaseJson));

        if (!nonPortableWindowPaths)
        {
            WriteSyntheticG2PeakHuntWatcherFiles(bundleDir, currentRunReportPath, currentRunJsonlPath, weakInputSampleCount: 8, strongInputSampleCount: 5, mixedReady: false);
            WriteSyntheticG2PeakHuntWatcherFiles(bundleDir, bestRunReportPath, bestRunJsonlPath, weakInputSampleCount: 10, strongInputSampleCount: 9, mixedReady: true);
        }
    }

    private static void WriteSyntheticG2PeakHuntWatcherFiles(
        string bundleDir,
        string reportPath,
        string jsonlPath,
        int weakInputSampleCount,
        int strongInputSampleCount,
        bool mixedReady)
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
                weakInputSampleCount,
                strongInputSampleCount,
                mixedWeakStrongEvidenceReady = mixedReady,
                mixedWeakStrongEvidenceStatus = mixedReady ? "ready" : "mixed-not-ready",
                speechQualifiedWeakInputSampleCount = Math.Max(0, weakInputSampleCount - 2),
                speechQualifiedStrongInputSampleCount = Math.Max(0, strongInputSampleCount - 2),
                passbandQualifiedWeakInputSampleCount = Math.Max(0, weakInputSampleCount - 4),
                passbandQualifiedStrongInputSampleCount = Math.Max(0, strongInputSampleCount - 4)
            },
            frontendTopPeakWatch = new
            {
                sampleCount = weakInputSampleCount + strongInputSampleCount,
                nearPassbandSampleCount = weakInputSampleCount + strongInputSampleCount,
                nearPassbandThresholdHz = 3000
            }
        };

        File.WriteAllText(resolvedReportPath, JsonSerializer.Serialize(summary, CamelCaseJson));
        File.WriteAllText(resolvedJsonlPath, JsonSerializer.Serialize(new { ok = true }, CamelCaseJson) + Environment.NewLine);
    }

    private static void WriteExternalBakeoffEndpointBundle(string bundleDir)
    {
        WriteGlobalExternalScopeBundle(bundleDir);

        var candidates = DspExternalEngineCandidateCatalog.All();
        var candidatePayload = new { externalEngineCandidates = candidates };
        File.WriteAllText(
            Path.Combine(bundleDir, "external-engine-candidates.json"),
            JsonSerializer.Serialize(candidatePayload, CamelCaseJson));
        File.WriteAllText(
            Path.Combine(bundleDir, "modernization-snapshot.json"),
            JsonSerializer.Serialize(candidatePayload, CamelCaseJson));

        File.WriteAllText(
            Path.Combine(bundleDir, "bundle-index.json"),
            """
            {
              "schemaVersion": 1,
              "endpoints": [
                { "id": "benchmark-plan", "file": "benchmark-plan.json", "required": true, "ok": true },
                { "id": "benchmark-capture-manifest", "file": "benchmark-capture-manifest.json", "required": true, "ok": true },
                { "id": "external-engine-candidates", "file": "external-engine-candidates.json", "required": true, "ok": true },
                { "id": "modernization-snapshot", "file": "modernization-snapshot.json", "required": true, "ok": true }
              ],
              "requiredFailures": []
            }
            """);
    }

    private static void WriteExternalBakeoffArtifactManifest(string bundleDir, bool includeCycleSummary = false)
    {
        Directory.CreateDirectory(Path.Combine(bundleDir, "artifacts"));
        var artifacts = new List<object>
        {
            new
            {
                id = "external-engine-bakeoff-report",
                kind = "external-candidate-report-json",
                source = "tools/summarize-dsp-external-engine-candidates.ps1",
                path = "artifacts/external-engine-bakeoff-report.json",
                required = true,
                comparisonIds = new[] { "candidate-external-engine-opt-in" }
            }
        };

        if (includeCycleSummary)
        {
            artifacts.Add(new
            {
                id = "external-engine-bakeoff-cycle-summary",
                kind = "external-engine-bakeoff-cycle-summary-json",
                source = "tools/run-dsp-external-engine-bakeoff.ps1",
                path = "artifacts/external-engine-bakeoff-cycle-summary.json",
                required = false,
                comparisonIds = new[] { "candidate-external-engine-opt-in" }
            });
        }

        File.WriteAllText(
            Path.Combine(bundleDir, "artifact-manifest.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                artifacts
            }, CamelCaseJson));
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

    private static void WritePureSignalScopeBundle(string bundleDir)
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
            hardwareTarget = "G2",
            scenarioIds = new[] { "tx-puresignal-safe-bypass" },
            requiredComparisons = plan.RequiredComparisons,
            requiredArtifacts = new object[]
            {
                new { id = "live-diagnostics-json", kind = "endpoint-json", source = "/api/dsp/live-diagnostics", required = false, scenarioIds = new[] { "tx-puresignal-safe-bypass" } },
                new { id = "benchmark-plan-json", kind = "endpoint-json", source = "/api/dsp/benchmark-plan", required = false, scenarioIds = new[] { "tx-puresignal-safe-bypass" } },
                new { id = "wdsp-native-symbol-audit", kind = "native-audit-json", source = "tools/audit-wdsp-native-symbols.ps1", required = false, scenarioIds = new[] { "tx-puresignal-safe-bypass" } },
                new { id = "wdsp-runtime-artifact-audit", kind = "runtime-audit-json", source = "tools/audit-wdsp-runtime-artifacts.ps1", required = false, scenarioIds = new[] { "tx-puresignal-safe-bypass" } },
                new { id = "offline-fixture-metrics", kind = "metrics-json", source = "offline-dsp-benchmark-harness", required = false, scenarioIds = new[] { "tx-puresignal-safe-bypass" } },
                new { id = "puresignal-feedback-trace", kind = "trace", source = "g2-tx-feedback", required = true, scenarioIds = new[] { "tx-puresignal-safe-bypass" } },
                new
                {
                    id = "puresignal-safe-bypass-report",
                    kind = "puresignal-safe-bypass-report-json",
                    source = "tools/summarize-dsp-puresignal-bench.ps1",
                    purpose = "Summarize G2 PureSignal disabled and enabled bench captures into explicit safety gates.",
                    cadence = "once-after-puresignal-disabled-and-enabled-bench-captures",
                    required = true,
                    scenarioIds = new[] { "tx-puresignal-safe-bypass" }
                }
            }
        };

        File.WriteAllText(
            Path.Combine(bundleDir, "benchmark-capture-manifest.json"),
            JsonSerializer.Serialize(captureManifest, CamelCaseJson));
    }

    private static void WritePureSignalArtifactManifest(string bundleDir)
    {
        Directory.CreateDirectory(Path.Combine(bundleDir, "artifacts"));

        var manifest = new
        {
            schemaVersion = 1,
            artifacts = new object[]
            {
                new
                {
                    id = "puresignal-safe-bypass-report",
                    kind = "puresignal-safe-bypass-report-json",
                    source = "tools/summarize-dsp-puresignal-bench.ps1",
                    path = "artifacts/puresignal-safe-bypass-report.json",
                    required = true,
                    scenarioIds = new[] { "tx-puresignal-safe-bypass" }
                }
            }
        };

        File.WriteAllText(
            Path.Combine(bundleDir, "artifact-manifest.json"),
            JsonSerializer.Serialize(manifest, CamelCaseJson));
    }

    private static void WritePureSignalTrace(
        string bundleDir,
        string relativePath,
        bool enabled,
        double feedbackStability,
        double txMonitorCoupling,
        int clippingCount)
    {
        var path = Path.Combine(bundleDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var trace = new
        {
            mode = enabled ? "enabled" : "disabled",
            pureSignal = new
            {
                pureSignalEnabled = enabled,
                bypassState = enabled ? "enabled-feedback-correction" : "disabled-bypass",
                feedbackStability,
                txMonitorCoupling,
                clippingCount,
                txOutputPeakDbfs = -6.0
            }
        };

        File.WriteAllText(path, JsonSerializer.Serialize(trace, CamelCaseJson));
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

    private static void WriteCrossRadioArtifactManifest(string bundleDir)
    {
        Directory.CreateDirectory(Path.Combine(bundleDir, "artifacts"));

        var manifest = new
        {
            schemaVersion = 1,
            artifacts = new object[]
            {
                new
                {
                    id = "cross-radio-validation-report",
                    kind = "cross-radio-validation-report-json",
                    source = "manual-cross-radio-validation",
                    path = "artifacts/cross-radio-validation-report.json",
                    required = true
                }
            }
        };

        File.WriteAllText(
            Path.Combine(bundleDir, "artifact-manifest.json"),
            JsonSerializer.Serialize(manifest, CamelCaseJson));
    }

    private static void WriteWdspSourceDriftArtifactManifest(string bundleDir)
    {
        Directory.CreateDirectory(Path.Combine(bundleDir, "artifacts"));

        var manifest = new
        {
            schemaVersion = 1,
            artifacts = new object[]
            {
                new
                {
                    id = "wdsp-source-drift-report",
                    kind = "wdsp-source-drift-report-json",
                    source = "tools/compare-wdsp-source-drift.ps1",
                    path = "artifacts/wdsp-source-drift-report.json",
                    required = true
                }
            }
        };

        File.WriteAllText(
            Path.Combine(bundleDir, "artifact-manifest.json"),
            JsonSerializer.Serialize(manifest, CamelCaseJson));
    }

    private static void WriteNativeStageTimingArtifactManifest(string bundleDir)
    {
        Directory.CreateDirectory(Path.Combine(bundleDir, "artifacts"));

        var manifest = new
        {
            schemaVersion = 1,
            artifacts = new object[]
            {
                new
                {
                    id = "native-stage-timing-report",
                    kind = "native-stage-timing-report-json",
                    source = "tools/summarize-dsp-native-stage-timing.ps1",
                    path = "artifacts/native-stage-timing-report.json",
                    required = true
                }
            }
        };

        File.WriteAllText(
            Path.Combine(bundleDir, "artifact-manifest.json"),
            JsonSerializer.Serialize(manifest, CamelCaseJson));
    }

    private static async Task WriteNativeStageTimingFixtureMetricsAsync(string bundleDir)
    {
        var metricsPath = Path.Combine(bundleDir, "artifacts", "offline-fixture-metrics.json");
        Directory.CreateDirectory(Path.GetDirectoryName(metricsPath)!);

        var metrics = new
        {
            schemaVersion = 1,
            tool = "dsp-fixture-evidence",
            evidenceEngine = "wdsp",
            wdspRuntimeRid = "win-x64",
            wdspRuntimeSha256 = "abc123",
            wdspRuntimeStatus = "found",
            scenarioCount = 1,
            comparisonIds = new[] { "current-zeus", "nr5-spnr" },
            scenarios = new[]
            {
                new
                {
                    scenarioId = "weak-cw-carrier",
                    scenarioName = "Weak CW carrier",
                    fixtureStatus = "offline-fixture-ready",
                    signalPath = "RXA",
                    comparisons = new object[]
                    {
                        NativeStageTimingComparison("current-zeus", 5.25, 4096),
                        NativeStageTimingComparison("nr5-spnr", 6.50, 8192)
                    }
                }
            }
        };

        await File.WriteAllTextAsync(metricsPath, JsonSerializer.Serialize(metrics, CamelCaseJson));
    }

    private static object NativeStageTimingComparison(string comparisonId, double processElapsedMs, long allocationBytes) =>
        new
        {
            comparisonId,
            source = "wdsp-fixture-runner",
            profile = comparisonId,
            metrics = new Dictionary<string, double>
            {
                ["processing elapsed ms"] = processElapsedMs,
                ["throughput ratio"] = 3.0,
                ["outputRms"] = 0.02,
                ["clippingCount"] = 0
            },
            nativeStageTiming = new
            {
                schemaVersion = 1,
                probeKind = "managed-wrapper-stage-timing",
                timingSource = "Stopwatch.GetTimestamp",
                allocationProbeKind = "GC.GetAllocatedBytesForCurrentThread",
                nativeCStageInstrumentationStatus = "not-instrumented",
                nativeAllocationProbeStatus = "managed-thread-delta-only",
                stageCount = 2,
                totalStageElapsedMs = processElapsedMs,
                processingStageElapsedMs = processElapsedMs - 0.25,
                maxStageElapsedMs = processElapsedMs - 1.0,
                totalManagedAllocatedBytes = allocationBytes,
                maxStageManagedAllocatedBytes = allocationBytes - 512,
                stages = new object[]
                {
                    new
                    {
                        stageId = "rx-feed-iq",
                        label = "RXA FeedIq fixture block",
                        elapsedMs = processElapsedMs - 1.0,
                        managedAllocatedBytes = allocationBytes - 512
                    },
                    new
                    {
                        stageId = "rx-drain-audio",
                        label = "RXA ReadAudio drain",
                        elapsedMs = 1.0,
                        managedAllocatedBytes = 512
                    }
                }
            },
            managedAllocation = new
            {
                schemaVersion = 1,
                probeKind = "managed-thread-delta",
                allocationSource = "GC.GetAllocatedBytesForCurrentThread",
                totalManagedAllocatedBytes = allocationBytes,
                maxStageManagedAllocatedBytes = allocationBytes - 512,
                nativeAllocationProbeStatus = "not-instrumented"
            }
        };

    private static async Task WriteCrossRadioSourceValidationReportAsync(
        string bundleDir,
        string relativePath,
        string hardwareTarget,
        bool metricComparisonReady = true,
        bool liveTraceComparisonReady = true,
        bool liveTraceThetisComparisonReady = true)
    {
        var path = Path.Combine(bundleDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var report = new
        {
            ok = true,
            errorCount = 0,
            warningCount = 0,
            errors = Array.Empty<object>(),
            warnings = Array.Empty<object>(),
            hardwareTarget,
            hardwareEvidenceStatus = "cross-radio-hardware-evidence-ready",
            metricComparisonReady,
            liveTraceComparisonReady,
            liveTraceThetisComparisonReady,
            scenarioIds = new[] { "weak-cw-carrier" },
            comparisonIds = new[] { "current-zeus", "nr5-spnr" }
        };

        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, CamelCaseJson));
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
            wdspSourceDriftReportPresent = true,
            wdspSourceDriftReportReady = true,
            wdspSourceDriftReportStatus = "ready",
            wdspSourceDriftReportNormalizedLineEndings = true,
            wdspSourceDriftReferenceFileCount = 2,
            wdspSourceDriftCandidateFileCount = 2,
            wdspSourceDriftFileCount = 2,
            wdspSourceDriftDeltaCount = 0,
            wdspSourceDriftLikelyDefectCount = 0,
            benchmarkPlanStatus = "ready",
            benchmarkPlanScenarioCount = 13,
            benchmarkPlanRequiredAcceptanceScenarioFamilyCount = 12,
            benchmarkPlanCoveredAcceptanceScenarioFamilyCount = 12,
            benchmarkPlanMissingAcceptanceScenarioFamilyCount = 0,
            benchmarkPlanMissingAcceptanceScenarioFamilyIds = Array.Empty<string>(),
            benchmarkPlanScenarioMissingRequiredComparisonCount = 0,
            benchmarkPlanScenarioMissingRequiredMetricCount = 0,
            benchmarkPlanScenarioMissingAcceptanceGateCount = 0,
            metricCatalogStatus = "ready",
            metricCatalogMetricCount = 88,
            metricCatalogRequiredMetricCount = 51,
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
            tool = "watch-dsp-live-diagnostics",
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
        double? nr5OutputDbfs = null,
        object[]? frontendTopPeaks = null)
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
                frontendTopPeaks = frontendTopPeaks ?? Array.Empty<object>(),
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

    private static object FrontendTopPeak(
        long frequencyHz,
        int offsetHz,
        double snrDb,
        double dbfs,
        double confidence = 0.84,
        bool coherent = true) =>
        new
        {
            frequencyHz,
            offsetHz,
            snrDb,
            dbfs,
            confidence,
            coherent
        };

    private static object Nr5LevelerAlignmentWatchSample(
        int sampleIndex,
        double nr5InputDbfs,
        double nr5OutputDbfs,
        double levelerInputRmsDbfs,
        double levelerOutputRmsDbfs,
        object[]? frontendTopPeaks = null)
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
                frontendTopPeaks = frontendTopPeaks ?? Array.Empty<object>(),
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
        AssertMetricVerdict(metrics, metricId, "regression", safetyClass);
    }

    private static void AssertMetricImprovement(JsonElement[] metrics, string metricId, string safetyClass)
    {
        AssertMetricVerdict(metrics, metricId, "improvement", safetyClass);
    }

    private static void AssertMetricVerdict(JsonElement[] metrics, string metricId, string verdict, string safetyClass)
    {
        var metric = metrics.Single(item => item.GetProperty("metricId").GetString() == metricId);
        Assert.Equal(verdict, metric.GetProperty("verdict").GetString());
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

    private static void AssertExternalBakeoffCycleSummaryArtifact(JsonElement[] artifacts)
    {
        var artifact = artifacts.Single(item => item.GetProperty("id").GetString() == "external-engine-bakeoff-cycle-summary");
        Assert.False(artifact.GetProperty("required").GetBoolean());
        Assert.Equal("external-engine-bakeoff-cycle-summary-json", artifact.GetProperty("kind").GetString());
        Assert.Equal("tools/run-dsp-external-engine-bakeoff.ps1", artifact.GetProperty("source").GetString());
        Assert.Equal("artifacts/external-engine-bakeoff-cycle-summary.json", artifact.GetProperty("path").GetString());
        Assert.Equal(new[] { "candidate-external-engine-opt-in" }, ReadStringArray(artifact, "comparisonIds"));
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
