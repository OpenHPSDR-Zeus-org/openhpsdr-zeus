// SPDX-License-Identifier: GPL-2.0-or-later
//
// Read-only benchmark and acceptance plan for WDSP modernization. This is a
// planning/diagnostic surface only; it does not run DSP or change defaults.

using Zeus.Contracts;

namespace Zeus.Server;

public static class DspBenchmarkPlanCatalog
{
    private const string RolloutGate = "no-default-change-without-offline-benchmark-g2-on-air-and-cross-radio-evidence";

    public static DspBenchmarkPlanDto Build() =>
        new(
            SchemaVersion: 1,
            GeneratedUtc: DateTimeOffset.UtcNow,
            Status: "ready-for-capture-planning",
            RolloutGate: RolloutGate,
            FirstHardwareTarget: "G2",
            RequiredHardwareBeforeGraduation:
            [
                "G2 receive and transmit validation",
                "At least one non-G2 OpenHPSDR-compatible radio validation",
                "On-air receive review across weak CW/carrier and SSB speech",
                "TX monitor and PureSignal-safe bypass review before TX profile graduation",
            ],
            RequiredComparisons:
            [
                "off-baseline",
                "thetis-parity",
                "current-zeus",
                "nr5-spnr",
                "candidate-external-engine-opt-in",
            ],
            GlobalAcceptanceGates:
            [
                "Beat current Zeus behavior on targeted metric without breaking Thetis parity.",
                "No audible AGC or NR pumping on level-step and fading fixtures.",
                "No weak-signal loss on coherent weak carrier/CW fixtures.",
                "No TX clipping, ALC instability, or PureSignal coupling regression.",
                "No native WDSP lifecycle instability or channel-state leakage.",
                "All experimental behavior remains opt-in until benchmark and hardware evidence is reviewed.",
            ],
            Scenarios: AllScenarios());

    public static string[] NextScenarioIds(SmartNrConditionDto condition)
    {
        ArgumentNullException.ThrowIfNull(condition);

        var scenarios = new List<string>();

        if (!condition.Available || !condition.Fresh)
        {
            scenarios.Add("frontend-scene-freshness");
            scenarios.Add("noise-only-gating");
        }

        if (ModeEquals(condition.ExpectedNrMode, "Nr5") || ModeEquals(condition.EffectiveNrMode, "Nr5"))
        {
            scenarios.Add("weak-cw-carrier");
            scenarios.Add("fading-carrier");
            scenarios.Add("strong-adjacent");
            scenarios.Add("agc-level-step");
        }
        else if (ModeEquals(condition.ExpectedNrMode, "Emnr") || ModeEquals(condition.EffectiveNrMode, "Emnr")
            || ModeEquals(condition.ExpectedNrMode, "Sbnr") || ModeEquals(condition.EffectiveNrMode, "Sbnr"))
        {
            scenarios.Add("ssb-like-speech");
            scenarios.Add("weak-cw-carrier");
            scenarios.Add("noise-only-gating");
        }

        if (condition.HeldByRxChain == true || string.Equals(condition.RxChainTone, "protect", StringComparison.OrdinalIgnoreCase))
        {
            scenarios.Add("agc-level-step");
            scenarios.Add("squelch-transition");
        }

        if (condition.Nr5SpnrDiagnostics is { SignalConfidence: < 0.20 }
            || condition.CoherentSubthresholdSignal == true)
            scenarios.Add("weak-cw-carrier");

        if (scenarios.Count == 0)
        {
            scenarios.Add("weak-cw-carrier");
            scenarios.Add("ssb-like-speech");
            scenarios.Add("tx-puresignal-safe-bypass");
        }

        return scenarios.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static DspBenchmarkScenarioDto[] AllScenarios() =>
    [
        Scenario(
            id: "frontend-scene-freshness",
            name: "Frontend scene freshness and clock alignment",
            phase: "live-diagnostics",
            signalPath: "frontend-spectrum-scene",
            fixtureStatus: "live-capture-required",
            appliesTo: ["Smart NR", "live diagnostics", "G2"],
            metrics: ["scene age", "source clock skew", "scene freshness", "runtime alignment"],
            gates:
            [
                "Scene age must be fresh enough for live decisions.",
                "Source clock skew must stay inside the live diagnostics limit.",
                "Requested/effective NR mode must be aligned or explicitly apply-pending.",
            ],
            artifacts: ["live diagnostics JSON", "frontend DSP scene snapshot"]),
        Scenario(
            id: "weak-cw-carrier",
            name: "Weak CW/carrier preservation",
            phase: "offline-and-g2-live",
            signalPath: "RX IQ",
            fixtureStatus: "offline-fixture-ready",
            appliesTo: ["NR2", "NR4", "NR5/SPNR", "external speech bypass"],
            metrics: ["coherent tone power", "wanted SNR", "spectral preservation", "output RMS", "latency"],
            gates:
            [
                "Wanted coherent tone must not disappear below off/current Zeus baseline.",
                "NR gain must stay bounded with no audible breathing around the signal.",
                "External post-demod engines must bypass or prove neutral behavior.",
            ],
            artifacts: ["fixture metrics JSON", "audio render", "spectrum before/after"]),
        Scenario(
            id: "ssb-like-speech",
            name: "SSB-like speech readability",
            phase: "offline-and-g2-live",
            signalPath: "RX audio",
            fixtureStatus: "offline-fixture-ready",
            appliesTo: ["NR2", "NR4", "NR5/SPNR", "RNNoise", "DeepFilterNet", "SpeexDSP", "WebRTC APM"],
            metrics: ["speech-band preservation", "noise reduction", "artifact score", "RMS movement", "CPU", "latency"],
            gates:
            [
                "Speech readability must improve over current Zeus without metallic artifacts.",
                "No double-AGC pumping or level collapse.",
                "Candidate engines must remain post-demod and opt-in.",
            ],
            artifacts: ["fixture metrics JSON", "speech audio render", "artifact notes"]),
        Scenario(
            id: "fading-carrier",
            name: "Fading carrier stability",
            phase: "offline-and-g2-live",
            signalPath: "RX IQ",
            fixtureStatus: "offline-fixture-ready",
            appliesTo: ["AGC", "NR2", "NR5/SPNR"],
            metrics: ["windowed RMS movement", "coherent tone continuity", "AGC gain movement", "latency"],
            gates:
            [
                "Fading signal must remain audible without NR gate chatter.",
                "AGC must not chase the noise floor into pumping.",
            ],
            artifacts: ["fixture metrics JSON", "AGC movement trace"]),
        Scenario(
            id: "impulse-noise",
            name: "Impulse noise and blanker interaction",
            phase: "offline-and-g2-live",
            signalPath: "RX IQ",
            fixtureStatus: "offline-fixture-ready",
            appliesTo: ["NB1", "NB2", "SNB", "NR5/SPNR"],
            metrics: ["impulse suppression", "wanted SNR", "post-blanker ringing", "artifact score"],
            gates:
            [
                "Impulse suppression must not smear or mute wanted signal content.",
                "NB/NR combinations must not crash or leave WDSP state inconsistent.",
            ],
            artifacts: ["fixture metrics JSON", "before/after waveform"]),
        Scenario(
            id: "strong-adjacent",
            name: "Strong adjacent signal rejection",
            phase: "offline-and-g2-live",
            signalPath: "RX IQ",
            fixtureStatus: "offline-fixture-ready",
            appliesTo: ["filters", "AGC", "NR2", "NR4", "NR5/SPNR"],
            metrics: ["wanted/adjacent ratio", "filter leakage", "AGC movement", "spectral preservation"],
            gates:
            [
                "Adjacent energy must not drive AGC/NR into wanted signal loss.",
                "Filter parity with Thetis must remain intact.",
            ],
            artifacts: ["fixture metrics JSON", "spectrum before/after"]),
        Scenario(
            id: "noise-only-gating",
            name: "Noise-only gating and false-signal avoidance",
            phase: "offline-and-g2-live",
            signalPath: "RX IQ/RX audio",
            fixtureStatus: "offline-fixture-ready",
            appliesTo: ["squelch", "Smart NR", "external speech engines"],
            metrics: ["false-open rate", "noise floor movement", "artifact score", "CPU"],
            gates:
            [
                "No false coherent-signal detection on noise-only scenes.",
                "External engines must not create speech-like artifacts that fool meters or Smart NR.",
            ],
            artifacts: ["fixture metrics JSON", "meter trace"]),
        Scenario(
            id: "agc-level-step",
            name: "AGC level step and pumping",
            phase: "offline-and-g2-live",
            signalPath: "RX IQ/RX audio",
            fixtureStatus: "offline-fixture-ready",
            appliesTo: ["WDSP AGC", "Auto AGC", "NR5/SPNR AGC", "external post-demod engines"],
            metrics: ["AGC gain movement", "windowed RMS movement", "settling time", "overshoot", "artifact score"],
            gates:
            [
                "No audible pumping, clipping, or noise-floor chase.",
                "Bounded gain movement must preserve weak-signal audibility.",
            ],
            artifacts: ["fixture metrics JSON", "AGC trace", "audio render"]),
        Scenario(
            id: "squelch-transition",
            name: "Squelch open/close transition",
            phase: "offline-and-g2-live",
            signalPath: "RX audio",
            fixtureStatus: "offline-fixture-ready",
            appliesTo: ["SSQL", "AMSQ", "FMSQ", "adaptive squelch", "external post-demod engines"],
            metrics: ["open latency", "close latency", "false-open rate", "audio discontinuity"],
            gates:
            [
                "Squelch must not chatter or open on external-engine artifacts.",
                "Open/close transitions must not leave stale audio or meters.",
            ],
            artifacts: ["fixture metrics JSON", "squelch trace"]),
        Scenario(
            id: "tx-two-tone",
            name: "TX two-tone linearity and limiter safety",
            phase: "offline-and-g2-bench",
            signalPath: "TX audio/TXA",
            fixtureStatus: "offline-fixture-ready",
            appliesTo: ["TX leveler", "CFC", "compressor", "ALC"],
            metrics: ["peak", "crest factor", "clipping count", "intermodulation proxy", "latency"],
            gates:
            [
                "No TX clipping or uncontrolled ALC action.",
                "Thetis-compatible TXA behavior must remain the reference unless explicitly approved.",
            ],
            artifacts: ["fixture metrics JSON", "TX audio render"]),
        Scenario(
            id: "tx-voice-like",
            name: "TX voice-like audio shaping",
            phase: "offline-and-g2-bench",
            signalPath: "TX audio/TXA",
            fixtureStatus: "offline-fixture-ready",
            appliesTo: ["TX leveler", "CFC", "compressor", "ALC", "future external speech tools"],
            metrics: ["RMS", "peak", "crest factor", "spectral balance", "latency"],
            gates:
            [
                "Voice-like TX audio must not clip, pump, or overdrive downstream TX stages.",
                "External enhancement candidates must not touch TX by default.",
            ],
            artifacts: ["fixture metrics JSON", "TX audio render"]),
        Scenario(
            id: "tx-puresignal-safe-bypass",
            name: "PureSignal-safe bypass and TX monitor isolation",
            phase: "g2-bench-required",
            signalPath: "TX feedback/PureSignal",
            fixtureStatus: "hardware-capture-required",
            appliesTo: ["PureSignal", "CFIR", "TX monitor", "external speech tools"],
            metrics: ["bypass state", "feedback stability", "TX monitor coupling", "clipping count"],
            gates:
            [
                "No modernization path may change PureSignal default or bypass state.",
                "TX monitor/external candidates must not couple into feedback correction.",
                "PureSignal disabled and enabled paths must both be captured before TX graduation.",
            ],
            artifacts: ["G2 bench capture", "TX feedback trace", "diagnostics JSON"]),
        Scenario(
            id: "wdsp-channel-lifecycle",
            name: "WDSP channel lifecycle and state transitions",
            phase: "automated-and-g2-live",
            signalPath: "RXA/TXA lifecycle",
            fixtureStatus: "lifecycle-test-required",
            appliesTo: ["OpenChannel", "SetChannelState", "MOX", "RXA", "TXA"],
            metrics: ["state transition success", "meter escape", "audio drain", "native exception count"],
            gates:
            [
                "Open/stop/configure/start transitions must stay stable under RX/TX changes.",
                "No stale meters or audio buffers may leak across channel state changes.",
            ],
            artifacts: ["lifecycle test log", "diagnostics JSON"])];

    private static DspBenchmarkScenarioDto Scenario(
        string id,
        string name,
        string phase,
        string signalPath,
        string fixtureStatus,
        string[] appliesTo,
        string[] metrics,
        string[] gates,
        string[] artifacts) =>
        new(
            SchemaVersion: 1,
            Id: id,
            Name: name,
            Phase: phase,
            SignalPath: signalPath,
            FixtureStatus: fixtureStatus,
            AppliesTo: appliesTo,
            RequiredComparisons:
            [
                "off-baseline",
                "thetis-parity",
                "current-zeus",
                "candidate-under-test",
            ],
            RequiredMetrics: metrics,
            AcceptanceGates: gates,
            RequiredArtifacts: artifacts,
            FailureModes:
            [
                "weak-signal-loss",
                "audible-pumping",
                "artifact-regression",
                "meter-drift",
                "native-lifecycle-instability",
            ],
            RelatedTools:
            [
                "offline-dsp-benchmark-harness",
                "g2-live-capture",
                "dsp-live-diagnostics",
            ]);

    private static bool ModeEquals(string? left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
