// SPDX-License-Identifier: GPL-2.0-or-later
//
// Read-only evidence bundle for WDSP modernization work. This composes the
// existing diagnostics surfaces into one response for capture tools; it does
// not run DSP, mutate settings, or approve behavior changes.

using Zeus.Contracts;

namespace Zeus.Server;

public static class DspModernizationEvidenceSnapshotService
{
    public static DspModernizationEvidenceSnapshotDto Build(
        SmartNrConditionDto condition,
        DspLiveDiagnosticsDto live,
        DspBenchmarkPlanDto plan,
        DspBenchmarkCaptureManifestDto captureManifest,
        DspExternalEngineCandidateDto[] externalCandidates)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(captureManifest);
        ArgumentNullException.ThrowIfNull(externalCandidates);

        var generatedUtc = DateTimeOffset.UtcNow;
        var missing = MissingEvidence(live, captureManifest);
        var score = CompletenessScore(live, captureManifest, missing);

        return new DspModernizationEvidenceSnapshotDto(
            SchemaVersion: 1,
            GeneratedUtc: generatedUtc,
            SnapshotId: $"dsp-modernization-{generatedUtc:yyyyMMddTHHmmssfffZ}",
            Status: Status(live, captureManifest, score),
            EvidenceCompletenessScore: score,
            ReadyForLiveBenchmark: live.ReadyForLiveBenchmark,
            ReadyForCapture: captureManifest.ReadyForCapture,
            RolloutGate: plan.RolloutGate,
            HardwareTarget: plan.FirstHardwareTarget,
            IncludedEndpoints:
            [
                "/api/dsp/modernization-snapshot",
                "/api/dsp/nr-condition",
                "/api/dsp/live-diagnostics",
                "/api/dsp/benchmark-plan",
                "/api/dsp/benchmark-capture-manifest",
                "/api/dsp/external-engine-candidates",
                "/api/radio/diagnostics",
                "/api/radio/diagnostics/dsp-scene",
            ],
            IncludedArtifacts: captureManifest.RequiredArtifacts
                .Where(static artifact => artifact.Required)
                .Select(static artifact => artifact.Id)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            MissingEvidence: missing,
            NextActions: NextActions(live, captureManifest, missing),
            SmartNrCondition: condition,
            LiveDiagnostics: live,
            BenchmarkPlan: plan,
            CaptureManifest: captureManifest,
            ExternalEngineCandidates: externalCandidates);
    }

    private static string Status(DspLiveDiagnosticsDto live, DspBenchmarkCaptureManifestDto manifest, int score)
    {
        if (!live.WdspNativeLoadable) return "blocked-wdsp-native-unloadable";
        if (!live.WdspActive) return "blocked-dsp-engine-unavailable";
        if (!live.FrontendSceneAvailable) return "blocked-frontend-scene-missing";
        if (live.FrontendSceneStale || !live.FrontendSceneFresh) return "blocked-frontend-scene-not-fresh";
        if (live.RuntimeAligned == false) return "blocked-smart-nr-runtime-misaligned";
        if (!manifest.ReadyForCapture) return "capture-preflight-required";
        if (score >= 90) return "ready-for-g2-evidence-capture";
        if (score >= 75) return "evidence-needs-review";
        return "evidence-incomplete";
    }

    private static int CompletenessScore(
        DspLiveDiagnosticsDto live,
        DspBenchmarkCaptureManifestDto manifest,
        string[] missingEvidence)
    {
        var score = 100;

        if (!live.WdspNativeLoadable) score -= 30;
        if (!live.WdspActive) score -= 30;
        if (!live.FrontendSceneAvailable) score -= 20;
        else if (!live.FrontendSceneFresh || live.FrontendSceneStale) score -= 15;
        if (live.RuntimeAligned == false) score -= 15;
        if (!manifest.ReadyForCapture) score -= 10;
        score -= Math.Min(20, missingEvidence.Length * 3);

        return Math.Clamp(score, 0, 100);
    }

    private static string[] MissingEvidence(DspLiveDiagnosticsDto live, DspBenchmarkCaptureManifestDto manifest)
    {
        var missing = new List<string>();

        if (!live.WdspNativeLoadable) missing.Add("wdsp-native-loadable");
        if (!live.WdspActive) missing.Add("wdsp-active");
        if (!live.FrontendSceneAvailable) missing.Add("frontend-dsp-scene");
        if (live.FrontendSceneStale || !live.FrontendSceneFresh) missing.Add("fresh-frontend-dsp-scene");
        if (live.RuntimeAligned == false) missing.Add("smart-nr-runtime-alignment");
        if (!manifest.ReadyForCapture) missing.Add("capture-preflight");

        foreach (var constraint in live.Constraints)
            missing.Add($"live:{constraint}");
        foreach (var constraint in manifest.Constraints)
            missing.Add($"manifest:{constraint}");

        if (!manifest.RequiredArtifacts.Any(static artifact => artifact.Id == "offline-fixture-metrics"))
            missing.Add("offline-fixture-metrics");
        if (!manifest.RequiredArtifacts.Any(static artifact => artifact.Source == "/api/radio/diagnostics/dsp-scene"))
            missing.Add("frontend-scene-artifact");

        return missing
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] NextActions(
        DspLiveDiagnosticsDto live,
        DspBenchmarkCaptureManifestDto manifest,
        string[] missingEvidence)
    {
        var actions = new List<string>();

        if (missingEvidence.Length == 0 && manifest.ReadyForCapture)
            actions.Add("Save this modernization snapshot with the G2 capture bundle before running the listed scenarios.");

        actions.AddRange(live.RecommendedActions);
        actions.AddRange(manifest.RecommendedActions);

        if (manifest.ReadyForCapture)
            actions.Add("Run all manifest scenarios and attach offline metrics, audio renders, spectrum captures, and before/after diagnostics JSON.");
        else
            actions.Add("Resolve missing evidence before using this snapshot as acceptance evidence.");

        return actions
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}
