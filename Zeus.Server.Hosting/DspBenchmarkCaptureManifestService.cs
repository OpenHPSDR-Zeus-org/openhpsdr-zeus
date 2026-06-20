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
        var scenarioIds = Unique(live.NextBenchmarkScenarios
            .Concat(RequiredSafetyScenarioIds(plan))
            .Concat(["wdsp-channel-lifecycle"]));
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
            actions.Add("Run the listed scenarios on G2 and compare against off, Thetis-parity, current Zeus, and comparison-under-test baselines.");
        }

        var status = Status(live);
        var artifacts = Artifacts(scenarioIds, plan.RequiredComparisons);

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

    private static IEnumerable<string> RequiredSafetyScenarioIds(DspBenchmarkPlanDto plan)
    {
        if (plan.Scenarios.Any(scenario => string.Equals(scenario.Id, "tx-two-tone", StringComparison.Ordinal)))
            yield return "tx-two-tone";
        if (plan.Scenarios.Any(scenario => string.Equals(scenario.Id, "tx-voice-like", StringComparison.Ordinal)))
            yield return "tx-voice-like";
        if (plan.Scenarios.Any(scenario => string.Equals(scenario.Id, "tx-puresignal-safe-bypass", StringComparison.Ordinal)))
            yield return "tx-puresignal-safe-bypass";
    }

    private static DspBenchmarkCaptureArtifactDto[] Artifacts(string[] scenarioIds, string[] requiredComparisons)
    {
        var all = scenarioIds;
        var pureSignal = scenarioIds.Contains("tx-puresignal-safe-bypass", StringComparer.Ordinal)
            ? new[] { "tx-puresignal-safe-bypass" }
            : Array.Empty<string>();
        var wdspLifecycle = scenarioIds.Contains("wdsp-channel-lifecycle", StringComparer.Ordinal)
            ? new[] { "wdsp-channel-lifecycle" }
            : Array.Empty<string>();
        var txFixtureSafety = scenarioIds
            .Where(static scenarioId => string.Equals(scenarioId, "tx-two-tone", StringComparison.Ordinal) ||
                                        string.Equals(scenarioId, "tx-voice-like", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var txOutputHeadroom = txFixtureSafety
            .Concat(pureSignal)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var externalBakeoff = requiredComparisons.Contains("candidate-external-engine-opt-in", StringComparer.Ordinal)
            ? all
            : Array.Empty<string>();
        string[] rxAudioLevelerFixtureScenarioIds =
        [
            "ssb-syllable-step",
            "near-target-speech",
            "sustained-weak-speech",
            "strong-after-weak",
        ];

        List<DspBenchmarkCaptureArtifactDto> artifacts =
        [
            Artifact(
                "live-diagnostics-json",
                "endpoint-json",
                "/api/dsp/live-diagnostics",
                "Capture live readiness, constraints, next scenarios, NR runtime alignment, and comparison preflight gates.",
                "before-and-after-each-scenario",
                true,
                all),
            Artifact(
                "live-diagnostics-trace",
                "diagnostics-jsonl",
                "tools/watch-dsp-live-diagnostics.ps1",
                "Sample live diagnostics over a scenario window to preserve runtime evidence movement, blockers, AGC gain, audio level, ADC headroom, squelch, and monitor backlog trends.",
                "once-per-live-scenario-window",
                false,
                all),
            Artifact(
                "live-diagnostics-trace-comparison",
                "diagnostics-comparison-json",
                "tools/compare-dsp-live-diagnostics-traces.ps1 or tools/compare-dsp-live-diagnostics-matrix.ps1",
                "Compare baseline and candidate live diagnostics traces or trace indexes to reject regressions in blockers, readiness, AGC movement, audio stability, ADC headroom, squelch, monitor backlog, and diagnostics latency.",
                "once-per-candidate-live-trace-or-matrix",
                false,
                all),
            Artifact(
                "live-diagnostics-trace-index",
                "trace",
                "tools/run-dsp-live-diagnostics-matrix.ps1",
                "Collect repeatable baseline or candidate live diagnostics windows across benchmark scenarios and write a bundle-compatible trace index.",
                "once-per-baseline-or-candidate-scenario-matrix",
                false,
                all),
            Artifact(
                "live-diagnostics-history",
                "diagnostics-history-json",
                "tools/summarize-dsp-live-diagnostics-history.ps1",
                "Summarize captured under-test and baseline live diagnostics attempts, rank best weak-signal and lowest-pumping traces, and preserve safety-class rollups before selecting the next comparison.",
                "once-after-several-live-diagnostics-attempts",
                false,
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
        ];

        if (externalBakeoff.Length > 0)
        {
            artifacts.Add(Artifact(
                "external-engine-bakeoff-report",
                "external-candidate-report-json",
                "tools/summarize-dsp-external-engine-candidates.ps1",
                "Summarize opt-in external DSP/ML candidate readiness, blockers, required benchmark coverage, and snapshot sync before any post-demod bakeoff review.",
                "once-per-capture-bundle-when-external-opt-in-comparison-is-in-scope",
                true,
                externalBakeoff));
        }

        artifacts.AddRange(
        [
            Artifact(
                "wdsp-native-symbol-audit",
                "native-audit-json",
                "tools/audit-wdsp-native-symbols.ps1",
                "Audit Zeus NativeMethods bindings against vendored WDSP source and native exports before accepting DSP changes.",
                "once-per-native-build-and-candidate",
                true,
                all),
            Artifact(
                "wdsp-runtime-artifact-audit",
                "runtime-audit-json",
                "tools/audit-wdsp-runtime-artifacts.ps1",
                "Audit packaged WDSP runtime artifacts by RID for NR4 symbol presence and side-by-side native dependencies.",
                "once-per-native-build-and-candidate",
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
                "rx-audio-leveler-fixture-benchmark",
                "metrics-json",
                "tools/run-dsp-rx-leveler-fixture-benchmark.ps1",
                "Export targeted current-vs-stable-speech-candidate RX audio leveler fixture metrics for syllable pumping, near-target stability, sustained weak-speech recovery, and strong-after-weak safety.",
                "once-per-rx-audio-leveler-candidate",
                false,
                rxAudioLevelerFixtureScenarioIds),
            Artifact(
                "native-stage-timing-report",
                "native-stage-timing-report-json",
                "tools/summarize-dsp-native-stage-timing.ps1",
                "Summarize WDSP fixture stage timing, managed allocation deltas, runtime hash provenance, and the remaining native C instrumentation gap before accepting DSP build-out evidence.",
                "once-after-wdsp-offline-fixture-matrix",
                true,
                all),
            Artifact(
                "tx-fixture-safety-report",
                "tx-fixture-safety-report-json",
                "tools/summarize-dsp-tx-fixture-safety.ps1",
                "Summarize WDSP TX two-tone and voice-like fixture safety: TX stage meter presence, clipping, output headroom, ALC/CFC/leveler gain reduction, runtime hash, and no default behavior changes before TX bench review.",
                "once-after-wdsp-offline-fixture-matrix-tx-scenarios",
                txFixtureSafety.Length > 0,
                txFixtureSafety),
            Artifact(
                "tx-output-headroom-ab-trace",
                "diagnostics-ab-summary-json",
                "tools/capture-tx-output-headroom-ab.ps1",
                "Capture guarded G2 current-vs-headroom-trim-candidate TX output headroom live diagnostics windows without keying TX from the script, including PureSignal bypass proof when armed.",
                "once-per-tx-output-headroom-candidate-live-window",
                false,
                txOutputHeadroom),
            Artifact(
                "wdsp-channel-lifecycle-report",
                "wdsp-channel-lifecycle-json",
                "tools/run-dsp-wdsp-channel-lifecycle.ps1",
                "Exercise Zeus WdspDspEngine OpenChannel, public wrapper configuration, SetMox state transitions, audio drain recovery, meter leakage, and CloseChannel/reopen behavior without radio hardware.",
                "once-per-native-build-and-lifecycle-candidate",
                wdspLifecycle.Length > 0,
                wdspLifecycle),
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
                "tools/capture-dsp-puresignal-bench-trace.ps1",
                "Capture read-only G2 TX/PureSignal disabled and enabled bench traces, then prove TX monitor and external DSP paths do not couple into PureSignal feedback correction.",
                "before-and-after-tx-scenario",
                pureSignal.Length > 0,
                pureSignal),
            Artifact(
                "puresignal-safe-bypass-report",
                "puresignal-safe-bypass-report-json",
                "tools/summarize-dsp-puresignal-bench.ps1",
                "Summarize G2 PureSignal disabled and enabled bench captures into explicit bypass-state, feedback-stability, TX monitor coupling, and clipping safety gates.",
                "once-after-puresignal-disabled-and-enabled-bench-captures",
                pureSignal.Length > 0,
                pureSignal),
        ]);

        return artifacts.ToArray();
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
