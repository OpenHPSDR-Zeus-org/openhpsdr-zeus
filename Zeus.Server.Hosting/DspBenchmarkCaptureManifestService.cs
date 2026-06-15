// SPDX-License-Identifier: GPL-2.0-or-later
//
// Read-only capture manifest for WDSP modernization benchmark sessions.
// It composes diagnostics and benchmark-plan metadata into an evidence
// checklist; it does not run DSP, capture audio, or alter radio state.

using Zeus.Contracts;

namespace Zeus.Server;

public static class DspBenchmarkCaptureManifestService
{
    public static DspBenchmarkCaptureManifestDto Build(DspLiveDiagnosticsDto live, DspBenchmarkPlanDto plan)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(plan);

        var generatedUtc = DateTimeOffset.UtcNow;
        var scenarioIds = Unique(live.NextBenchmarkScenarios.Concat(["wdsp-channel-lifecycle"]));
        var constraints = live.Constraints.ToList();
        var actions = live.RecommendedActions.ToList();
        var preflight = new List<string>
        {
            "Confirm the active radio is the G2 before treating this capture as first-cycle hardware evidence.",
            "Save /api/dsp/live-diagnostics before and after each scenario.",
            "Save /api/dsp/benchmark-plan once with the capture bundle.",
            "Record operator mode, band, filter width, sample rate, AGC mode/top, attenuator state, squelch state, and NR mode.",
        };

        if (!live.WdspNativeLoadable)
            preflight.Add("Fix WDSP native loading before capture; benchmark data without native WDSP is not valid.");
        if (!live.WdspActive)
            preflight.Add("Connect the radio or start the WDSP engine before capture.");
        if (!live.FrontendSceneAvailable || !live.FrontendSceneFresh)
            preflight.Add("Open the frontend DSP scene publisher and wait for fresh scene evidence.");
        if (live.RuntimeAligned == false)
            preflight.Add("Wait for Smart NR requested/effective mode alignment or capture the apply-pending state explicitly.");
        if (live.HeldByRxChain == true || string.Equals(live.RxChainTone, "protect", StringComparison.OrdinalIgnoreCase))
            preflight.Add("Resolve RX-chain protect posture before increasing DSP aggressiveness.");

        if (!live.ReadyForLiveBenchmark)
        {
            constraints.Add("capture-preflight-not-ready");
            actions.Add("Resolve live diagnostics constraints before using this manifest as acceptance evidence.");
        }
        else
        {
            actions.Add("Run the listed scenarios on G2 and compare against off, Thetis-parity, current Zeus, and candidate-under-test baselines.");
        }

        var status = Status(live);
        var artifacts = Artifacts(scenarioIds);

        return new DspBenchmarkCaptureManifestDto(
            SchemaVersion: 1,
            GeneratedUtc: generatedUtc,
            ManifestId: $"dsp-capture-{generatedUtc:yyyyMMddTHHmmssfffZ}",
            Status: status,
            ReadyForCapture: live.ReadyForLiveBenchmark,
            CaptureGate: plan.RolloutGate,
            HardwareTarget: plan.FirstHardwareTarget,
            LiveDiagnosticsStatus: live.Status,
            LiveReadinessScore: live.ReadinessScore,
            LiveDiagnosticsEndpoint: "/api/dsp/live-diagnostics",
            BenchmarkPlanEndpoint: "/api/dsp/benchmark-plan",
            ExternalEngineCandidatesEndpoint: "/api/dsp/external-engine-candidates",
            ScenarioIds: scenarioIds,
            RequiredComparisons: plan.RequiredComparisons,
            GlobalAcceptanceGates: plan.GlobalAcceptanceGates,
            PreflightChecks: Unique(preflight),
            StopConditions:
            [
                "Stop capture if ADC overload, RX-chain protect, or unexpected clipping appears.",
                "Stop TX capture if ALC instability, compressor runaway, or PureSignal feedback coupling appears.",
                "Stop external-engine evaluation if weak CW/carrier content is muted or speech artifacts fool Smart NR/metering.",
                "Stop tuning if any scenario shows audible pumping, weak-signal loss, or native lifecycle instability.",
            ],
            Constraints: Unique(constraints),
            RecommendedActions: Unique(actions),
            RequiredArtifacts: artifacts,
            OperatorNotes:
            [
                "This manifest is an evidence checklist only; it does not approve changing DSP defaults.",
                "Attach audio renders, spectrum before/after captures, diagnostics JSON, and operator listening notes to the same capture bundle.",
                "External engines remain post-demod and opt-in until their own catalog blockers are cleared.",
                "Cross-radio validation is required before calling a DSP enhancement complete.",
            ]);
    }

    private static string Status(DspLiveDiagnosticsDto live)
    {
        if (!live.WdspNativeLoadable) return "blocked-wdsp-native-unloadable";
        if (!live.WdspActive) return "blocked-dsp-engine-unavailable";
        if (!live.FrontendSceneAvailable) return "blocked-frontend-scene-missing";
        if (live.FrontendSceneStale || !live.FrontendSceneFresh) return "blocked-frontend-scene-not-fresh";
        if (live.RuntimeAligned == false) return "blocked-smart-nr-runtime-misaligned";
        return live.ReadyForLiveBenchmark ? "ready-for-g2-capture" : "capture-preflight-required";
    }

    private static DspBenchmarkCaptureArtifactDto[] Artifacts(string[] scenarioIds)
    {
        var all = scenarioIds;
        var pureSignal = scenarioIds.Contains("tx-puresignal-safe-bypass", StringComparer.Ordinal)
            ? new[] { "tx-puresignal-safe-bypass" }
            : Array.Empty<string>();

        return
        [
            Artifact(
                "live-diagnostics-json",
                "endpoint-json",
                "/api/dsp/live-diagnostics",
                "Capture live readiness, constraints, next scenarios, NR runtime alignment, and candidate gates.",
                "before-and-after-each-scenario",
                true,
                all),
            Artifact(
                "benchmark-plan-json",
                "endpoint-json",
                "/api/dsp/benchmark-plan",
                "Freeze the scenario catalog, required metrics, comparisons, and global acceptance gates used for the run.",
                "once-per-capture-bundle",
                true,
                all),
            Artifact(
                "nr-condition-json",
                "endpoint-json",
                "/api/dsp/nr-condition",
                "Capture frontend Smart NR condition and backend RX-chain posture.",
                "before-and-after-each-scenario",
                true,
                all),
            Artifact(
                "frontend-dsp-scene-json",
                "endpoint-json",
                "/api/radio/diagnostics/dsp-scene",
                "Preserve scene freshness, signal profile, coherent peak evidence, and Smart NR recommendation source data.",
                "before-and-after-each-scenario",
                true,
                all),
            Artifact(
                "hardware-diagnostics-json",
                "endpoint-json",
                "/api/radio/diagnostics",
                "Capture feature surfaces, radio telemetry paths, and diagnostic discoverability for the session.",
                "before-and-after-capture",
                true,
                all),
            Artifact(
                "external-engine-candidates-json",
                "endpoint-json",
                "/api/dsp/external-engine-candidates",
                "Freeze opt-in external candidate blockers, license/package risk, and required benchmarks.",
                "once-per-capture-bundle",
                true,
                all),
            Artifact(
                "offline-fixture-metrics",
                "metrics-json",
                "offline-dsp-benchmark-harness",
                "Record deterministic fixture metrics for each scenario before comparing live G2 captures.",
                "once-per-scenario-and-candidate",
                true,
                all),
            Artifact(
                "audio-render-before-after",
                "audio",
                "offline-render-and-g2-capture",
                "Store before/after audio for listening review and artifact scoring.",
                "once-per-scenario-and-candidate",
                true,
                all),
            Artifact(
                "spectrum-before-after",
                "spectrum",
                "offline-render-and-g2-capture",
                "Store spectral preservation evidence for weak, adjacent, and noise-only scenarios.",
                "once-per-rx-scenario-and-candidate",
                true,
                all),
            Artifact(
                "puresignal-feedback-trace",
                "trace",
                "g2-tx-feedback",
                "Prove TX monitor and external DSP paths do not couple into PureSignal feedback correction.",
                "before-and-after-tx-scenario",
                pureSignal.Length > 0,
                pureSignal),
        ];
    }

    private static DspBenchmarkCaptureArtifactDto Artifact(
        string id,
        string kind,
        string source,
        string purpose,
        string cadence,
        bool required,
        string[] scenarioIds) =>
        new(
            SchemaVersion: 1,
            Id: id,
            Kind: kind,
            Source: source,
            Purpose: purpose,
            Cadence: cadence,
            Required: required,
            ScenarioIds: scenarioIds);

    private static string[] Unique(IEnumerable<string> values) =>
        values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
}
