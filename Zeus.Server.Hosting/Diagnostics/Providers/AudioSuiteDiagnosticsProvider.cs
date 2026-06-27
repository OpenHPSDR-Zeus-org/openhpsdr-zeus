// SPDX-License-Identifier: GPL-2.0-or-later
//
// Diagnostics v2 provider for the Audio Suite (VST / native insert chain).
// Surfaces the real-time metrics that define "best in class" hosting —
// insert latency, per-block DSP load (realtime headroom), and fidelity
// counters — over GET /api/diagnostics/v2/audio-suite, so latency, fidelity,
// and performance can be measured live while transmitting/receiving.

namespace Zeus.Server.Diagnostics;

public sealed class AudioSuiteDiagnosticsProvider : IDiagnosticsProvider
{
    private readonly AudioPluginBridge _bridge;

    public AudioSuiteDiagnosticsProvider(AudioPluginBridge bridge)
        => _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));

    public string Id => "audio.suite";
    public string RouteSegment => "audio-suite";
    public string Category => "audio";
    public int SchemaVersion => 1;
    public string Description =>
        "Audio Suite (VST/native insert chain) real-time metrics: insert latency, per-block DSP load, and fidelity counters.";

    public object Snapshot() => _bridge.GetAudioSuiteDiagnostics();

    public IReadOnlyList<DiagnosticsSelfCheck> SelfChecks => new[]
    {
        new DiagnosticsSelfCheck("tx-realtime-headroom",
            "TX Audio Suite peak per-block DSP load stays within realtime headroom (<70% of the block period).",
            DiagnosticsSeverity.Warn,
            _ =>
            {
                var tx = _bridge.GetAudioSuiteDiagnostics().Tx;
                if (tx.BlocksProcessed == 0)
                    return Pass("TX chain idle (no blocks processed yet).");
                return tx.PeakDspLoadPercent <= 70.0
                    ? Pass($"TX peak DSP load {tx.PeakDspLoadPercent:F1}% of the block period - comfortable margin.")
                    : Warn($"TX peak DSP load {tx.PeakDspLoadPercent:F1}% - approaching the realtime budget.");
            }),

        new DiagnosticsSelfCheck("rx-realtime-headroom",
            "RX Audio Suite peak per-block DSP load stays within realtime headroom (<70% of the block period).",
            DiagnosticsSeverity.Warn,
            _ =>
            {
                var rx = _bridge.GetAudioSuiteDiagnostics().Rx;
                if (rx.BlocksProcessed == 0)
                    return Pass("RX chain idle (no blocks processed yet).");
                return rx.PeakDspLoadPercent <= 70.0
                    ? Pass($"RX peak DSP load {rx.PeakDspLoadPercent:F1}% of the block period - comfortable margin.")
                    : Warn($"RX peak DSP load {rx.PeakDspLoadPercent:F1}% - approaching the realtime budget.");
            }),

        new DiagnosticsSelfCheck("no-non-finite-samples",
            "No plugin in either chain has emitted non-finite (NaN/Inf) samples.",
            DiagnosticsSeverity.Warn,
            _ =>
            {
                var s = _bridge.GetAudioSuiteDiagnostics();
                long total = s.Tx.NonFiniteRepairs + s.Rx.NonFiniteRepairs;
                return total == 0
                    ? Pass("No non-finite samples repaired; clean signal path.")
                    : Warn($"{total} non-finite (NaN/Inf) samples repaired; a plugin is emitting bad audio (TX {s.Tx.NonFiniteRepairs}, RX {s.Rx.NonFiniteRepairs}).");
            }),

        new DiagnosticsSelfCheck("oop-engine-not-degraded",
            "When the out-of-process VST engine is active, it is meeting its realtime budget.",
            DiagnosticsSeverity.Warn,
            _ =>
            {
                var s = _bridge.GetAudioSuiteDiagnostics();
                if (!s.OutOfProcessEngineActive)
                    return Pass("Out-of-process engine not active (in-process is the live route).");
                return s.OutOfProcessDegradedBlocks == 0
                    ? Pass("Out-of-process engine has missed no block budgets.")
                    : Warn($"Out-of-process engine degraded {s.OutOfProcessDegradedBlocks} block(s) to passthrough.");
            }),
    };

    private static SelfCheckResult Pass(string detail) => new(SelfCheckOutcome.Pass, detail, DateTimeOffset.UtcNow);
    private static SelfCheckResult Warn(string detail) => new(SelfCheckOutcome.Warn, detail, DateTimeOffset.UtcNow);
}
