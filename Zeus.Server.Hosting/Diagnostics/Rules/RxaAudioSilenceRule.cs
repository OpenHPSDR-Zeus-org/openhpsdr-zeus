// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections.Generic;

namespace Zeus.Server.Diagnostics;

/// <summary>
/// No receive audio while the panadapter still animates — the WDSP RXA channel
/// opened but the DSP/meter worker (xrxa / xmeter) never ran, so the audio ring
/// stays empty. The tell-tale is the RX S-meter sitting at the -400 sentinel:
/// that value means the meter thread didn't run, which is the same condition that
/// starves the audio path. Root cause is WDSP channel-state init ordering (open at
/// state 0, flip to 1 only after the worker is live). Surfaced as advisory context
/// for the no-RX-audio symptom.
/// </summary>
public sealed class RxaAudioSilenceRule : IKnownIssueRule
{
    public string Id => "rxa-audio-silence";

    public IReadOnlyCollection<string> Symptoms { get; } = ["rx-no-audio"];

    public DiagnosticFinding? Evaluate(
        DiagnosticContext ctx, IReadOnlyList<DiagnosticSection> sections)
    {
        // Look for the -400 meter sentinel either in a probe's reported RX dBm
        // (forward-compatible key, not collected today) or in the recent log. The
        // panadapter animating while audio is silent is the classic misleading
        // clue, so we don't require a "pan ok" signal.
        var rxDbm =
            RuleInspection.Value(sections, "dsp-audio", "rx.dbm") ??
            RuleInspection.Value(sections, "dsp-audio", "RxDbm");
        bool meterSentinel =
            (rxDbm is not null && rxDbm.Contains("-400")) ||
            RuleInspection.LogContains(ctx.RecentLog, "-400");

        if (!meterSentinel)
            return null;

        return new DiagnosticFinding(
            Title: "Receive DSP started but produced no audio (WDSP init ordering)",
            Detail:
                "The receive meter is reading the -400 sentinel, which means the DSP " +
                "engine's receive worker never actually ran — even though the " +
                "panadapter may keep animating (that runs on a separate path and is a " +
                "misleading clue). When this happens there's no audio because the " +
                "receive channel was brought up out of order. The known cause is a " +
                "WDSP channel-state init-ordering issue (the channel must be opened " +
                "idle and only switched on after its worker thread is live). If you " +
                "see this consistently, disconnect and reconnect; if it persists, " +
                "report it with the log below.",
            Severity: DiagnosticSeverity.Likely,
            DocRef: "docs/rca/2026-04-17-rxa-audio-silence.md");
    }
}
