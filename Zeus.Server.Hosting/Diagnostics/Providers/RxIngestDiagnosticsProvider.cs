// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Zeus.Dsp.Wdsp;

namespace Zeus.Server.Diagnostics;

/// <summary>
/// Live per-DDC / per-receiver RX ingest health — the realtime overflow/underrun
/// signal for high-sample-rate (up to 1536 kHz) and full multi-DDC operation.
/// Surfaces, per WDSP RX channel: queue depth vs the rate-scaled capacity,
/// dropped-oldest frame count (the deliberate, bounded glitch the non-blocking
/// feed emits when the worker falls behind), worker frame time vs the per-frame
/// budget, and audio-ring overruns — plus per-DDC UDP packet rate (last ~1 s
/// window) so an operator can confirm exactly how many DDCs the radio is
/// actually streaming and whether any are overflowing/underrunning.
/// </summary>
public sealed class RxIngestDiagnosticsProvider : IDiagnosticsProvider
{
    private readonly DspPipelineService _dsp;

    public RxIngestDiagnosticsProvider(DspPipelineService dsp) =>
        _dsp = dsp ?? throw new ArgumentNullException(nameof(dsp));

    public string Id => "rx.ingest-health";
    public string RouteSegment => "rx-ingest-health";
    public string Category => "dsp";
    public int SchemaVersion => 1;
    public string Description =>
        "Per-DDC RX ingest health: WDSP queue depth/drops, worker timing vs per-frame budget, audio overruns, per-DDC packet rate.";

    public object Snapshot() => _dsp.SnapshotRxIngestHealth();

    public IReadOnlyList<DiagnosticsSelfCheck> SelfChecks => new[]
    {
        new DiagnosticsSelfCheck("rx-no-dropped-frames",
            "No RX IQ frames dropped and no audio-ring overruns in the last window.",
            DiagnosticsSeverity.Warn,
            _ => EvaluateDropsAndOverruns()),
        new DiagnosticsSelfCheck("rx-worker-within-budget",
            "Every RX channel's worst-case WDSP frame time fits the per-frame budget for its rate.",
            DiagnosticsSeverity.Warn,
            _ => EvaluateWorkerBudget()),
    };

    private SelfCheckResult EvaluateDropsAndOverruns()
    {
        var channels = SnapshotChannels();
        if (channels.Count == 0)
            return new SelfCheckResult(SelfCheckOutcome.Pass, "No active RX channels.", DateTimeOffset.UtcNow);

        long dropped = 0;
        long overrun = 0;
        foreach (var c in channels)
        {
            dropped += c.DroppedPerWindow;
            overrun += c.AudioOverrunPerWindow;
        }

        if (dropped > 0)
            return new SelfCheckResult(SelfCheckOutcome.Warn,
                $"{dropped} IQ frame(s) dropped (oldest-evicted) in the last window across {channels.Count} channel(s) — worker behind / CPU-bound.",
                DateTimeOffset.UtcNow);
        if (overrun > 0)
            return new SelfCheckResult(SelfCheckOutcome.Warn,
                $"{overrun} audio-ring overrun(s) in the last window — audio consumer behind.",
                DateTimeOffset.UtcNow);
        return new SelfCheckResult(SelfCheckOutcome.Pass,
            $"No drops or overruns across {channels.Count} active channel(s).",
            DateTimeOffset.UtcNow);
    }

    private SelfCheckResult EvaluateWorkerBudget()
    {
        var channels = SnapshotChannels();
        if (channels.Count == 0)
            return new SelfCheckResult(SelfCheckOutcome.Pass, "No active RX channels.", DateTimeOffset.UtcNow);

        foreach (var c in channels)
        {
            if (c.SampleRateHz <= 0) continue;
            double budgetMs = 1000.0 * 1024.0 / c.SampleRateHz;
            if (c.WorkerMaxMs > budgetMs)
                return new SelfCheckResult(SelfCheckOutcome.Warn,
                    $"ch{c.ChannelId} @ {c.SampleRateHz} Hz: worst frame {c.WorkerMaxMs:F2} ms exceeds the {budgetMs:F2} ms per-frame budget — CPU-bound at this rate.",
                    DateTimeOffset.UtcNow);
        }
        return new SelfCheckResult(SelfCheckOutcome.Pass,
            "All active channels are within their per-frame WDSP budget.",
            DateTimeOffset.UtcNow);
    }

    private IReadOnlyList<WdspDspEngine.RxChannelHealth> SnapshotChannels() =>
        (_dsp.CurrentEngine as WdspDspEngine)?.SnapshotRxChannels()
        ?? (IReadOnlyList<WdspDspEngine.RxChannelHealth>)Array.Empty<WdspDspEngine.RxChannelHealth>();
}
