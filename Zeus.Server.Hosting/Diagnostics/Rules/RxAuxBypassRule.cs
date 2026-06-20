// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections.Generic;

namespace Zeus.Server.Diagnostics;

/// <summary>
/// A common receive foot-gun: a band's RX-aux setting is left on "Bypass", which
/// routes that band's receive path to an empty/unused antenna jack instead of the
/// real antenna. The classic signature is exactly one dead band with an unusually
/// low noise floor while other bands are fine. This is an operator setting, not a
/// Zeus bug — the fix is to set that band's RX-aux back to "None". Surfaced as a
/// warning on both no-audio and crackle/distortion receive reports so the operator
/// rules it out before chasing the DSP path.
/// </summary>
public sealed class RxAuxBypassRule : IKnownIssueRule
{
    public string Id => "rx-aux-bypass";

    public IReadOnlyCollection<string> Symptoms { get; } = ["rx-no-audio", "rx-audio-quality"];

    public DiagnosticFinding? Evaluate(
        DiagnosticContext ctx, IReadOnlyList<DiagnosticSection> sections)
    {
        // No reliable per-band RX-aux signal is collected, so this rule always
        // surfaces as advisory context for the receive symptoms — it's a cheap,
        // high-frequency mistake worth ruling out first. It never claims to be
        // the confirmed cause (Warning, not Likely).
        return new DiagnosticFinding(
            Title: "Check the band's RX-aux setting (Bypass routes to an empty jack)",
            Detail:
                "If only one band is dead — or much quieter than the others, with an " +
                "unusually low noise floor — check that band's RX-aux setting. When " +
                "RX-aux is set to \"Bypass\", receive is routed to an unused / empty " +
                "antenna jack instead of your real antenna, so you hear little or " +
                "nothing. This is a setting, not a fault. Set the affected band's " +
                "RX-aux back to \"None\" and the antenna comes back. If every band is " +
                "dead, this isn't it — look at the audio path instead.",
            Severity: DiagnosticSeverity.Warning,
            DocRef: null);
    }
}
