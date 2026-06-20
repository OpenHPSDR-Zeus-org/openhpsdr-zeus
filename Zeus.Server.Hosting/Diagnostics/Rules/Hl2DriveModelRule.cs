// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections.Generic;

namespace Zeus.Server.Diagnostics;

/// <summary>
/// Low transmit power on a Hermes Lite 2. The HL2 does NOT use the dB-based
/// drive model the other HPSDR boards use — it uses a percentage-based model, so
/// the "PA Gain (dB)" field is actually a per-band output percentage (0..100),
/// shown in the UI as "PA Output (%)". Hand-calibrated dB values carried over
/// from another radio (e.g. 40.5 or 26) get silently reinterpreted as a low
/// percentage and produce ~20-40% of rated power. The fix is almost always
/// "press Reset to Hermes Lite 2 defaults" (100% on HF). Informational guidance
/// whenever the connected board is an HL2 and the symptom is low TX power.
/// </summary>
public sealed class Hl2DriveModelRule : IKnownIssueRule
{
    public string Id => "hl2-drive-model";

    public IReadOnlyCollection<string> Symptoms { get; } = ["no-tx-power"];

    public DiagnosticFinding? Evaluate(
        DiagnosticContext ctx, IReadOnlyList<DiagnosticSection> sections)
    {
        if (!RuleInspection.IsHermesLite2(sections))
            return null;

        return new DiagnosticFinding(
            Title: "Hermes Lite 2 uses a percentage drive model, not dB",
            Detail:
                "On the Hermes Lite 2 the \"PA Gain\" value is a per-band output " +
                "PERCENTAGE (0-100, shown as \"PA Output (%)\"), not decibels like " +
                "the other radios. If you carried over a hand-calibrated dB number " +
                "from another rig (for example 40.5 or 26), the HL2 now reads it as " +
                "that percentage of full output, which is why power tops out at " +
                "roughly 20-40%. Open the PA Settings panel and press \"Reset to " +
                "Hermes Lite 2 defaults\" to seed 100% on HF (and the lower 6 m " +
                "soft-cap). Then key TUN and confirm full power.",
            Severity: DiagnosticSeverity.Warning,
            DocRef: "docs/lessons/hl2-drive-model.md");
    }
}
