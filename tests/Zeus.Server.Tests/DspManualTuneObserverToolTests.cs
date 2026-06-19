// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Zeus.Server.Tests;

[Trait("Category", "DspModernization")]
public sealed class DspManualTuneObserverToolTests
{
    private static readonly JsonSerializerOptions CamelCaseJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    [SkippableFact]
    public async Task ManualTuneObserverReportsSuggestedVfoForOffFilterPeak()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell manual-tune observer smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-manual-tune-observer-hint-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            using var server = JsonRouteServer.Start(new Dictionary<string, string>
            {
                ["/api/state"] = Json(new
                {
                    status = "Connected",
                    vfoHz = 14_331_500,
                    radioLoHz = 14_208_500,
                    mode = "USB",
                    filterLowHz = 100,
                    filterHighHz = 3_152
                }),
                ["/api/radio/diagnostics/dsp-scene"] = Json(new
                {
                    status = "fresh",
                    fresh = true,
                    signalProfile = "voice-like",
                    coherentMaxSnrDb = 20.0,
                    maxSnrDb = 20.0,
                    topPeaks = new object[]
                    {
                        FrontendTopPeak(14_366_750, 35_250, 16.8, -84.6),
                        FrontendTopPeak(14_076_688, -254_812, 26.2, -71.2)
                    }
                }),
                ["/api/dsp/live-diagnostics"] = Json(ManualTuneObserverLiveDiagnostics())
            });

            var reportPath = Path.Combine(bundleDir, "manual-observer-hint.json");
            var run = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "watch-dsp-manual-tune-observer.ps1"),
                "-BaseUrl", server.BaseUrl,
                "-ReportPath", reportPath,
                "-OutputRoot", Path.Combine(bundleDir, "captures"),
                "-PollCount", "3",
                "-PollIntervalSec", "0",
                "-StablePolls", "1",
                "-MinCoherentSnrDb", "6",
                "-SceneProfilePattern", "voice",
                "-MaxCaptures", "0",
                "-RequireFrontendNearPassband",
                "-JsonOnly");

            Assert.True(run.ExitCode == 0, run.CombinedOutput);
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
            var root = doc.RootElement;
            Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(3, root.GetProperty("pollSampleCount").GetInt32());
            Assert.Equal(0, root.GetProperty("maxCaptures").GetInt32());
            Assert.Equal(1, root.GetProperty("observedVfoCount").GetInt32());
            Assert.Equal(14_331_500, root.GetProperty("bestObservedVfoHz").GetInt64());
            Assert.Equal("tuning-hint", root.GetProperty("bestObservedVfoStatus").GetString());
            Assert.True(root.GetProperty("bestObservedVfoScore").GetDouble() > 0.0);
            Assert.Equal(14_365_000, root.GetProperty("bestObservedVfoSuggestedVfoHz").GetInt64());
            Assert.Equal(14.365, root.GetProperty("bestObservedVfoSuggestedVfoMhz").GetDouble(), precision: 6);
            Assert.Equal(33_500.0, root.GetProperty("bestObservedVfoSuggestedDialShiftHz").GetDouble(), precision: 3);
            Assert.Equal(1_000, root.GetProperty("bestObservedVfoSuggestedVfoStepHz").GetInt64());
            Assert.Equal(14_365_124, root.GetProperty("bestObservedVfoExactSuggestedVfoHz").GetInt64());
            Assert.Equal(14.365124, root.GetProperty("bestObservedVfoExactSuggestedVfoMhz").GetDouble(), precision: 6);
            Assert.Equal(33_624.0, root.GetProperty("bestObservedVfoExactSuggestedDialShiftHz").GetDouble(), precision: 3);
            Assert.Equal("above-filter", root.GetProperty("bestObservedVfoSuggestedTuneReason").GetString());
            Assert.Equal(3, root.GetProperty("frontendTuningHintPollCount").GetInt32());
            Assert.Equal(0, root.GetProperty("captureQualifiedPollCount").GetInt32());
            Assert.Equal(0, root.GetProperty("frontendFilterPassbandPollCount").GetInt32());
            Assert.Equal(3, root.GetProperty("frontendFilterOffPassbandPollCount").GetInt32());

            var hint = root.GetProperty("frontendBestTuningHint");
            Assert.Equal("above-filter", hint.GetProperty("reason").GetString());
            Assert.Equal(14_366_750, hint.GetProperty("peakFrequencyHz").GetInt64());
            Assert.Equal(35_250.0, hint.GetProperty("peakOffsetHz").GetDouble(), precision: 3);
            Assert.Equal(1_626.0, hint.GetProperty("filterCenterOffsetHz").GetDouble(), precision: 3);
            Assert.Equal(32_098.0, hint.GetProperty("filterDistanceHz").GetDouble(), precision: 3);
            Assert.Equal(33_500.0, hint.GetProperty("suggestedDialShiftHz").GetDouble(), precision: 3);
            Assert.Equal(14_365_000, hint.GetProperty("suggestedVfoHz").GetInt64());
            Assert.Equal(14.365, hint.GetProperty("suggestedVfoMhz").GetDouble(), precision: 6);
            Assert.Equal(1_000, hint.GetProperty("suggestedVfoStepHz").GetInt64());
            Assert.Equal(33_624.0, hint.GetProperty("exactSuggestedDialShiftHz").GetDouble(), precision: 3);
            Assert.Equal(14_365_124, hint.GetProperty("exactSuggestedVfoHz").GetInt64());
            Assert.Equal(14.365124, hint.GetProperty("exactSuggestedVfoMhz").GetDouble(), precision: 6);

            var poll = root.GetProperty("polls").EnumerateArray().First();
            Assert.Equal(33_500.0, poll.GetProperty("frontendSuggestedDialShiftHz").GetDouble(), precision: 3);
            Assert.Equal(14_365_000, poll.GetProperty("frontendSuggestedVfoHz").GetInt64());
            Assert.Equal(14_365_124, poll.GetProperty("frontendExactSuggestedVfoHz").GetInt64());
            Assert.Equal("above-filter", poll.GetProperty("frontendSuggestedTuneReason").GetString());

            var observedVfo = root.GetProperty("observedVfos").EnumerateArray().Single();
            Assert.Equal(3, observedVfo.GetProperty("pollCount").GetInt32());
            Assert.Equal(3, observedVfo.GetProperty("frontendTuningHintPollCount").GetInt32());
            Assert.Equal(0, observedVfo.GetProperty("captureQualifiedPollCount").GetInt32());
            Assert.Equal("tuning-hint", observedVfo.GetProperty("status").GetString());

            Assert.Equal("manual-tune-to-frontend-suggestion", root.GetProperty("primaryManualTuneActionId").GetString());
            Assert.Equal("tuning-hint", root.GetProperty("primaryManualTuneActionStatus").GetString());
            Assert.Contains("14.365000 MHz", root.GetProperty("primaryManualTuneActionManualAction").GetString() ?? "", StringComparison.Ordinal);
            Assert.DoesNotContain("-RequireCandidateCaptureReady", root.GetProperty("primaryManualTuneActionCommandTemplate").GetString() ?? "", StringComparison.Ordinal);
            Assert.Contains("-SuggestedVfoStepHz 1000", root.GetProperty("primaryManualTuneActionCommandTemplate").GetString() ?? "", StringComparison.Ordinal);
            var primaryAction = root.GetProperty("primaryManualTuneAction");
            Assert.Equal(14_365_000, primaryAction.GetProperty("suggestedVfoHz").GetInt64());
            Assert.Equal(33_500.0, primaryAction.GetProperty("suggestedDialShiftHz").GetDouble(), precision: 3);
            Assert.Equal(14_365_124, primaryAction.GetProperty("exactSuggestedVfoHz").GetInt64());
            Assert.Equal(33_624.0, primaryAction.GetProperty("exactSuggestedDialShiftHz").GetDouble(), precision: 3);
            Assert.Equal("above-filter", primaryAction.GetProperty("suggestedTuneReason").GetString());

            var recommendations = root.GetProperty("recommendations")
                .EnumerateArray()
                .Select(item => item.GetString() ?? "")
                .ToArray();
            Assert.Contains(recommendations, value => value.Contains("Read-only manual tuning hint", StringComparison.Ordinal));
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
    public async Task ManualTuneObserverReportsCaptureDisabledWhenMaxCapturesZero()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell manual-tune observer smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-manual-tune-observer-no-capture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            using var server = JsonRouteServer.Start(new Dictionary<string, string>
            {
                ["/api/state"] = Json(new
                {
                    status = "Connected",
                    vfoHz = 14_213_000,
                    radioLoHz = 14_208_500,
                    mode = "USB",
                    filterLowHz = 100,
                    filterHighHz = 3_152
                }),
                ["/api/radio/diagnostics/dsp-scene"] = Json(new
                {
                    status = "fresh",
                    fresh = true,
                    signalProfile = "voice-like",
                    coherentMaxSnrDb = 28.0,
                    maxSnrDb = 28.0,
                    topPeaks = new object[]
                    {
                        FrontendTopPeak(14_214_441, 1_441, 28.8, -75.6)
                    }
                }),
                ["/api/dsp/live-diagnostics"] = Json(ManualTuneObserverLiveDiagnostics())
            });

            var reportPath = Path.Combine(bundleDir, "manual-observer-no-capture.json");
            var run = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "watch-dsp-manual-tune-observer.ps1"),
                "-BaseUrl", server.BaseUrl,
                "-ReportPath", reportPath,
                "-OutputRoot", Path.Combine(bundleDir, "captures"),
                "-PollCount", "3",
                "-PollIntervalSec", "0",
                "-StablePolls", "1",
                "-MinCoherentSnrDb", "6",
                "-SceneProfilePattern", "voice",
                "-MaxCaptures", "0",
                "-RequireFrontendNearPassband",
                "-JsonOnly");

            Assert.True(run.ExitCode == 0, run.CombinedOutput);
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
            var root = doc.RootElement;
            Assert.Equal(3, root.GetProperty("pollSampleCount").GetInt32());
            Assert.Equal(1, root.GetProperty("observedVfoCount").GetInt32());
            Assert.Equal(14_213_000, root.GetProperty("bestObservedVfoHz").GetInt64());
            Assert.Equal("capture-qualified", root.GetProperty("bestObservedVfoStatus").GetString());
            Assert.Equal(0, root.GetProperty("captureCount").GetInt32());
            Assert.Equal(3, root.GetProperty("captureQualifiedPollCount").GetInt32());
            Assert.Equal("enable-manual-observer-capture", root.GetProperty("primaryManualTuneActionId").GetString());
            Assert.Equal("capture-disabled", root.GetProperty("primaryManualTuneActionStatus").GetString());
            Assert.Contains("capture-qualified", root.GetProperty("primaryManualTuneActionSummary").GetString() ?? "", StringComparison.Ordinal);
            Assert.Equal(3, root.GetProperty("frontendFilterPassbandPollCount").GetInt32());

            var recommendations = root.GetProperty("recommendations")
                .EnumerateArray()
                .Select(item => item.GetString() ?? "")
                .ToArray();
            Assert.Contains(recommendations, value => value.Contains("Capture is disabled by -MaxCaptures 0", StringComparison.Ordinal));
            Assert.DoesNotContain(recommendations, value => value.Contains("No stable voice-like manual-tune VFO met the capture threshold", StringComparison.Ordinal));
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
    public async Task ManualTuneObserverDefaultProfilePatternCapturesDxSignals()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell manual-tune observer smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-manual-tune-observer-dx-default-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            using var server = JsonRouteServer.Start(new Dictionary<string, string>
            {
                ["/api/state"] = Json(new
                {
                    status = "Connected",
                    vfoHz = 14_249_000,
                    radioLoHz = 14_249_000,
                    mode = "USB",
                    filterLowHz = 100,
                    filterHighHz = 3_100
                }),
                ["/api/radio/diagnostics/dsp-scene"] = Json(new
                {
                    status = "fresh",
                    fresh = true,
                    signalProfile = "dx",
                    coherentMaxSnrDb = 26.0,
                    maxSnrDb = 26.0,
                    topPeaks = new object[]
                    {
                        FrontendTopPeak(14_249_938, 938, 15.5, -88.3)
                    }
                }),
                ["/api/dsp/live-diagnostics"] = Json(ManualTuneObserverLiveDiagnostics())
            });

            var reportPath = Path.Combine(bundleDir, "manual-observer-dx-default.json");
            var run = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "watch-dsp-manual-tune-observer.ps1"),
                "-BaseUrl", server.BaseUrl,
                "-ReportPath", reportPath,
                "-OutputRoot", Path.Combine(bundleDir, "captures"),
                "-PollCount", "3",
                "-PollIntervalSec", "0",
                "-StablePolls", "1",
                "-MinCoherentSnrDb", "6",
                "-MaxCaptures", "0",
                "-RequireFrontendNearPassband",
                "-JsonOnly");

            Assert.True(run.ExitCode == 0, run.CombinedOutput);
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
            var root = doc.RootElement;
            Assert.Equal("voice|speech|phone|dx", root.GetProperty("sceneProfilePattern").GetString());
            Assert.Equal(3, root.GetProperty("captureQualifiedPollCount").GetInt32());
            Assert.Equal("capture-qualified", root.GetProperty("bestObservedVfoStatus").GetString());
            Assert.Equal(3, root.GetProperty("frontendFilterPassbandPollCount").GetInt32());
            Assert.DoesNotContain(
                root.GetProperty("recommendations").EnumerateArray().Select(item => item.GetString() ?? ""),
                value => value.Contains("No stable voice-like manual-tune VFO met the capture threshold", StringComparison.Ordinal));
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
    public async Task ManualTuneObserverPromotesNearStrongWeakOnlyCapture()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell manual-tune observer smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-manual-tune-observer-near-strong-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);
        var liveDiagnosticCall = -1;

        try
        {
            using var server = JsonRouteServer.Start(new Dictionary<string, Func<string>>
            {
                ["/api/state"] = () => Json(new
                {
                    status = "Connected",
                    vfoHz = 14_249_000,
                    radioLoHz = 14_249_000,
                    mode = "USB",
                    filterLowHz = 100,
                    filterHighHz = 3_100
                }),
                ["/api/radio/diagnostics/dsp-scene"] = () => Json(new
                {
                    status = "fresh",
                    fresh = true,
                    signalProfile = "voice-like",
                    coherentMaxSnrDb = 24.0,
                    maxSnrDb = 24.0,
                    topPeaks = new object[]
                    {
                        FrontendTopPeak(14_249_938, 938, 18.5, -84.3)
                    }
                }),
                ["/api/dsp/live-diagnostics"] = () =>
                {
                    var call = Interlocked.Increment(ref liveDiagnosticCall);
                    return Json(call is 2 or 3
                        ? ManualTuneObserverLiveDiagnostics(inputDbfs: -24.5, outputDbfs: -27.0, audioRmsDbfs: -28.5)
                        : ManualTuneObserverLiveDiagnostics(inputDbfs: -34.0, outputDbfs: -31.0, audioRmsDbfs: -32.5));
                }
            });

            var reportPath = Path.Combine(bundleDir, "manual-observer-near-strong.json");
            var run = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "watch-dsp-manual-tune-observer.ps1"),
                "-BaseUrl", server.BaseUrl,
                "-ReportPath", reportPath,
                "-OutputRoot", Path.Combine(bundleDir, "captures"),
                "-PollCount", "1",
                "-PollIntervalSec", "0",
                "-StablePolls", "1",
                "-MinCoherentSnrDb", "6",
                "-SceneProfilePattern", "voice",
                "-MaxCaptures", "1",
                "-CaptureSamples", "4",
                "-CaptureIntervalMs", "0",
                "-RequireFrontendNearPassband",
                "-JsonOnly");

            Assert.True(run.ExitCode == 0, run.CombinedOutput);
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
            var root = doc.RootElement;
            Assert.Equal(1, root.GetProperty("captureCount").GetInt32());
            Assert.Equal(1, root.GetProperty("missingStrongInputCaptureCount").GetInt32());
            Assert.Equal(1, root.GetProperty("weakOnlyCaptureCount").GetInt32());
            Assert.Equal(1, root.GetProperty("nearStrongPromotionCandidateCaptureCount").GetInt32());
            Assert.Equal(14_249_000, root.GetProperty("bestNearStrongPromotionCandidateVfoHz").GetInt64());
            Assert.Equal(14.249, root.GetProperty("bestNearStrongPromotionCandidateVfoMhz").GetDouble(), precision: 6);
            Assert.Equal(2.5, root.GetProperty("bestNearStrongPromotionCandidateDistanceToStrongThresholdDb").GetDouble(), precision: 3);
            Assert.True(root.GetProperty("bestNearStrongPromotionCandidateNearStrongInputSampleCount").GetInt32() >= 1);
            Assert.Equal("recapture-manual-observer-near-strong-vfo", root.GetProperty("primaryManualTuneActionId").GetString());
            Assert.Equal("near-strong-weak-only", root.GetProperty("primaryManualTuneActionStatus").GetString());
            Assert.Contains("14.249000 MHz", root.GetProperty("primaryManualTuneActionManualAction").GetString() ?? "", StringComparison.Ordinal);

            var statusCount = root.GetProperty("mixedWeakStrongEvidenceStatusCounts").EnumerateArray().Single();
            Assert.Equal("missing-strong-input", statusCount.GetProperty("status").GetString());
            Assert.Equal(1, statusCount.GetProperty("count").GetInt32());

            var capture = root.GetProperty("captures").EnumerateArray().Single();
            Assert.Equal(-22.0, capture.GetProperty("strongInputThresholdDbfs").GetDouble(), precision: 3);
            Assert.Equal(-26.0, capture.GetProperty("nearStrongInputThresholdDbfs").GetDouble(), precision: 3);
            Assert.True(capture.GetProperty("weakInputSampleCount").GetInt32() > 0);
            Assert.Equal(0, capture.GetProperty("strongInputSampleCount").GetInt32());
            Assert.True(capture.GetProperty("nearStrongInputSampleCount").GetInt32() > 0);
            Assert.NotEmpty(capture.GetProperty("topNearStrongInputs").EnumerateArray());
            Assert.Equal(2.5, capture.GetProperty("topNearStrongInputs").EnumerateArray().First().GetProperty("distanceToStrongThresholdDb").GetDouble(), precision: 3);
            Assert.Equal("candidate-preflight-ready", capture.GetProperty("candidateTuningTraceStatus").GetString());
            Assert.Equal("ready", capture.GetProperty("captureReadinessStatus").GetString());
            Assert.True(capture.GetProperty("captureReadinessHardGatePass").GetBoolean());
            Assert.True(capture.GetProperty("comparisonStateReady").GetBoolean());
            Assert.Equal("not-required", capture.GetProperty("comparisonStateStatus").GetString());
            Assert.Equal("missing-strong-input", capture.GetProperty("mixedWeakStrongTuningStatus").GetString());
            Assert.Equal("inspect-frontend-strong-passband-subthreshold-inputs-before-changing-dsp", capture.GetProperty("mixedWeakStrongTuningAction").GetString());

            var bestCandidate = root.GetProperty("bestNearStrongPromotionCandidateCapture");
            Assert.NotEmpty(bestCandidate.GetProperty("topNearStrongInputs").EnumerateArray());
            Assert.Equal(capture.GetProperty("reportPath").GetString(), root.GetProperty("bestNearStrongPromotionCandidateReportPath").GetString());

            var recommendations = root.GetProperty("recommendations")
                .EnumerateArray()
                .Select(item => item.GetString() ?? "")
                .ToArray();
            Assert.Contains(recommendations, value => value.Contains("Best near-strong promotion candidate", StringComparison.Ordinal));
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
    public async Task ManualTuneObserverLegacyRequireCandidateCaptureReadyDoesNotBlockQualifiedWindow()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell manual-tune observer smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-manual-tune-observer-candidate-gate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);

        try
        {
            using var server = JsonRouteServer.Start(new Dictionary<string, string>
            {
                ["/api/state"] = Json(new
                {
                    status = "Connected",
                    vfoHz = 14_249_000,
                    radioLoHz = 14_249_000,
                    mode = "USB",
                    filterLowHz = 100,
                    filterHighHz = 3_100
                }),
                ["/api/radio/diagnostics/dsp-scene"] = Json(new
                {
                    status = "fresh",
                    fresh = true,
                    signalProfile = "voice-like",
                    coherentMaxSnrDb = 24.0,
                    maxSnrDb = 24.0,
                    topPeaks = new object[]
                    {
                        FrontendTopPeak(14_249_938, 938, 18.5, -84.3)
                    }
                }),
                ["/api/dsp/live-diagnostics"] = Json(ManualTuneObserverLiveDiagnostics(
                    requestedNrMode: "Off",
                    effectiveNrMode: "Off",
                    readyForCandidateTuning: true))
            });

            var reportPath = Path.Combine(bundleDir, "manual-observer-candidate-gate.json");
            var run = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "watch-dsp-manual-tune-observer.ps1"),
                "-BaseUrl", server.BaseUrl,
                "-ReportPath", reportPath,
                "-OutputRoot", Path.Combine(bundleDir, "captures"),
                "-PollCount", "3",
                "-PollIntervalSec", "0",
                "-StablePolls", "1",
                "-MinCoherentSnrDb", "6",
                "-SceneProfilePattern", "voice",
                "-MaxCaptures", "0",
                "-RequireFrontendNearPassband",
                "-RequireCandidateCaptureReady",
                "-JsonOnly");

            Assert.True(run.ExitCode == 0, run.CombinedOutput);
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
            var root = doc.RootElement;
            Assert.True(root.GetProperty("requireCandidateCaptureReady").GetBoolean());
            Assert.True(root.GetProperty("legacyRequireCandidateCaptureReadyRequested").GetBoolean());
            Assert.Equal(3, root.GetProperty("baseCaptureQualifiedPollCount").GetInt32());
            Assert.Equal(3, root.GetProperty("captureQualifiedPollCount").GetInt32());
            Assert.Equal(0, root.GetProperty("captureCount").GetInt32());
            Assert.Equal(3, root.GetProperty("candidateCaptureReadyPollCount").GetInt32());
            Assert.Equal(0, root.GetProperty("candidateCaptureBlockedPollCount").GetInt32());

            var poll = root.GetProperty("polls").EnumerateArray().First();
            Assert.True(poll.GetProperty("candidateCaptureReady").GetBoolean());
            Assert.Equal("ready", poll.GetProperty("candidateCaptureReadinessStatus").GetString());
            var constraints = poll.GetProperty("candidateCaptureReadinessConstraints")
                .EnumerateArray()
                .Select(item => item.GetString() ?? "")
                .ToArray();
            Assert.Empty(constraints);

            var recommendations = root.GetProperty("recommendations")
                .EnumerateArray()
                .Select(item => item.GetString() ?? "")
                .ToArray();
            Assert.DoesNotContain(recommendations, value => value.Contains("Candidate capture readiness was blocked", StringComparison.Ordinal));
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
    public async Task ManualTuneObserverCarriesWatcherPumpingRisk()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "PowerShell manual-tune observer smoke runs on Windows.");

        var powerShell = FindPowerShell();
        Skip.If(powerShell is null, "PowerShell executable was not found.");

        var repoRoot = FindRepoRoot();
        var bundleDir = Path.Combine(Path.GetTempPath(), $"zeus-manual-tune-observer-pumping-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bundleDir);
        var liveDiagnosticCall = -1;

        try
        {
            using var server = JsonRouteServer.Start(new Dictionary<string, Func<string>>
            {
                ["/api/state"] = () => Json(new
                {
                    status = "Connected",
                    vfoHz = 14_249_000,
                    radioLoHz = 14_249_000,
                    mode = "USB",
                    filterLowHz = 100,
                    filterHighHz = 3_100
                }),
                ["/api/radio/diagnostics/dsp-scene"] = () => Json(new
                {
                    status = "fresh",
                    fresh = true,
                    signalProfile = "voice-like",
                    coherentMaxSnrDb = 24.0,
                    maxSnrDb = 24.0,
                    topPeaks = new object[]
                    {
                        FrontendTopPeak(14_249_938, 938, 18.5, -84.3)
                    }
                }),
                ["/api/dsp/live-diagnostics"] = () =>
                {
                    var call = Interlocked.Increment(ref liveDiagnosticCall);
                    var agcGainDb = call % 2 == 0 ? -42.5 : -34.5;
                    return Json(ManualTuneObserverLiveDiagnostics(
                        inputDbfs: -20.0,
                        outputDbfs: -20.0,
                        audioRmsDbfs: -20.0,
                        agcGainDb: agcGainDb));
                }
            });

            var reportPath = Path.Combine(bundleDir, "manual-observer-pumping.json");
            var run = await RunPowerShellAsync(
                powerShell,
                repoRoot,
                Path.Combine(repoRoot, "tools", "watch-dsp-manual-tune-observer.ps1"),
                "-BaseUrl", server.BaseUrl,
                "-ReportPath", reportPath,
                "-OutputRoot", Path.Combine(bundleDir, "captures"),
                "-PollCount", "1",
                "-PollIntervalSec", "0",
                "-StablePolls", "1",
                "-MinCoherentSnrDb", "6",
                "-SceneProfilePattern", "voice",
                "-MaxCaptures", "1",
                "-CaptureSamples", "4",
                "-CaptureIntervalMs", "0",
                "-RequireFrontendNearPassband",
                "-JsonOnly");

            Assert.True(run.ExitCode == 0, run.CombinedOutput);
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
            var root = doc.RootElement;
            Assert.Equal(1, root.GetProperty("captureCount").GetInt32());
            Assert.Equal(1, root.GetProperty("agcPumpingRiskCaptureCount").GetInt32());

            var capture = root.GetProperty("captures").EnumerateArray().Single();
            Assert.True(capture.GetProperty("agcPumpingRisk").GetBoolean());
            Assert.Equal("active-pumping-risk", capture.GetProperty("agcStabilityStatus").GetString());
        }
        finally
        {
            if (Directory.Exists(bundleDir))
            {
                Directory.Delete(bundleDir, recursive: true);
            }
        }
    }

    private static object FrontendTopPeak(long frequencyHz, int offsetHz, double snrDb, double dbfs) => new
    {
        frequencyHz,
        offsetHz,
        snrDb,
        dbfs,
        confidence = 0.9
    };

    private static object ManualTuneObserverLiveDiagnostics(
        double inputDbfs = -34.0,
        double outputDbfs = -31.0,
        double audioRmsDbfs = -34.0,
        double outputPeakDbfs = -9.0,
        double agcGainDb = -42.5,
        string requestedNrMode = "Off",
        string effectiveNrMode = "Off",
        bool readyForCandidateTuning = true) => new
    {
        status = "ready-for-live-benchmark",
        qualityTone = "ready",
        readinessScore = 92,
        readyForLiveBenchmark = true,
        requestedNrMode,
        effectiveNrMode,
        readyForCandidateTuning,
        candidateTuningStatus = readyForCandidateTuning ? "candidate-preflight-ready" : "candidate-tuning-preflight-required",
        candidateTuningConstraints = readyForCandidateTuning ? Array.Empty<string>() : new[] { "candidate-preflight-blocked" },
        wdspActive = true,
        frontendSceneAvailable = true,
        frontendSceneStatus = "fresh",
        frontendSceneFresh = true,
        frontendSceneAgeMs = 100,
        frontendTopPeaks = new object[]
        {
            FrontendTopPeak(14_249_938, 938, 18.5, -84.3)
        },
        frontendAdjacentNoiseUsable = true,
        frontendAdjacentNoiseBins = 96,
        frontendAdjacentNoiseFloorDb = -104.0,
        rxChainFilterLowHz = 100,
        rxChainFilterHighHz = 3_100,
        rxChainFilterWidthHz = 3_000,
        rxChainFilterPresetName = "3.0k",
        runtimeEvidence = new
        {
            status = "ready",
            rxMetersFresh = true,
            rxMetersStale = false,
            rxMetersAgeMs = 10,
            agcGainDb,
            adcHeadroomDb = 58.0,
            audioFresh = true,
            audioStale = false,
            audioAgeMs = 10,
            audioStatus = "ready",
            audioRmsDbfs,
            audioPeakDbfs = -12.0,
            rxAudioLevelerInputRmsDbfs = audioRmsDbfs,
            rxAudioLevelerOutputRmsDbfs = audioRmsDbfs,
            rxAudioLevelerInputPeakDbfs = -12.0,
            rxAudioLevelerOutputPeakDbfs = -12.0,
            rxAudioLevelerDesiredGainDb = 0.0,
            rxAudioLevelerAppliedGainDb = 0.0,
            rxAudioLevelerGainDeltaDb = 0.0,
            rxAudioLevelerPeakHeadroomDb = 58.0,
            rxAudioLevelerPreLimitPeakDbfs = -12.0,
            rxAudioLevelerOutputLimitReductionDb = 0.0,
            rxAudioLevelerOutputLimitSampleCount = 0,
            rxAudioLevelerPauseHoldBlocks = 0,
            rxAudioLevelerCandidateSpeechHoldBlocks = 0,
            rxAudioLevelerBoostSlewLimited = false,
            rxAudioLevelerPeakLimited = false,
            rxAudioLevelerOutputLimited = false
        },
        candidateDspDiagnostics = new
        {
            schemaVersion = 9,
            run = true,
            learnedFrames = 100,
            signalConfidence = 0.7,
            signalProbability = 0.6,
            textureFill = 0.02,
            agcGate = 0.6,
            levelDrive = 0.6,
            recoveryDrive = 0.2,
            weakSignalMemory = 0.5,
            makeupGainDb = 0.0,
            maskSmoothing = 0.3,
            inputDbfs,
            outputDbfs,
            outputPeakDbfs,
            meanGain = 0.5,
            floorReductionDb = 6.0,
            peakEvidence = 0.1,
            peakLimitDbfs = -4.3,
            peakReductionDb = 0.0,
            adjacentNoiseUsable = true,
            adjacentNoiseBins = 96,
            adjacentNoiseFloorDb = -104.0,
            adjacentNoiseTrust = 0.5,
            adjacentNoiseDrive = 0.2,
            adjacentNoiseRejectedPct = 20.0,
            adjacentNoiseSideBalance = 0.5,
            adjacentNoiseAsymmetryDb = 0.5
        }
    };

    private static string Json(object value) => JsonSerializer.Serialize(value, CamelCaseJson);

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
            var script = Path.Combine(directory.FullName, "tools", "watch-dsp-manual-tune-observer.ps1");
            if (File.Exists(script))
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

    private sealed class JsonRouteServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly IReadOnlyDictionary<string, Func<string>> _routes;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        private JsonRouteServer(TcpListener listener, IReadOnlyDictionary<string, Func<string>> routes)
        {
            _listener = listener;
            _routes = routes;
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}";
            _loop = Task.Run(AcceptLoopAsync);
        }

        public string BaseUrl { get; }

        public static JsonRouteServer Start(IReadOnlyDictionary<string, string> routes)
            => Start(routes.ToDictionary(
                pair => pair.Key,
                pair =>
                {
                    var body = pair.Value;
                    return (Func<string>)(() => body);
                }));

        public static JsonRouteServer Start(IReadOnlyDictionary<string, Func<string>> routes)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new JsonRouteServer(listener, routes);
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }

            _cts.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch
                {
                    if (_cts.IsCancellationRequested)
                    {
                        break;
                    }

                    continue;
                }

                _ = Task.Run(() => HandleClientAsync(client), _cts.Token);
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using var clientRef = client;
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync();
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync()))
            {
            }

            var path = "/";
            var parts = (requestLine ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && Uri.TryCreate(parts[1], UriKind.RelativeOrAbsolute, out var uri))
            {
                path = uri.IsAbsoluteUri ? uri.AbsolutePath : uri.OriginalString.Split('?', 2)[0];
            }

            var found = _routes.TryGetValue(path, out var jsonFactory);
            var body = found ? jsonFactory!() : "{\"error\":\"not found\"}";
            var status = found ? "200 OK" : "404 Not Found";
            var bytes = Encoding.UTF8.GetBytes(body);
            var header = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header);
            await stream.WriteAsync(bytes);
        }
    }

    private sealed record ToolResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public string CombinedOutput => $"{StandardOutput}{Environment.NewLine}{StandardError}";
    }
}
