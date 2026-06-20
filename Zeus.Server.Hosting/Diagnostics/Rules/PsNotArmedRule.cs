// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections.Generic;

namespace Zeus.Server.Diagnostics;

/// <summary>
/// PureSignal produced no correction because it was never armed this session.
/// Zeus never auto-arms PS — that is a hard safety rule (PsEnabled always
/// initialises to false on startup; the operator arms it manually every
/// session). The fingerprint is either the auto-attenuate gate logging
/// <c>skip=PsEnabled-off</c>, or the tx-ps probe reporting PsEnabled=false.
/// </summary>
public sealed class PsNotArmedRule : IKnownIssueRule
{
    public string Id => "ps-not-armed";

    public IReadOnlyCollection<string> Symptoms { get; } = ["ps-not-working"];

    public DiagnosticFinding? Evaluate(
        DiagnosticContext ctx, IReadOnlyList<DiagnosticSection> sections)
    {
        // The psAutoAttn gate logs this exact marker when PS is not armed.
        bool gateSkipped = RuleInspection.LogContains(ctx.RecentLog, "skip=PsEnabled-off");

        // Or the tx-ps probe directly reports the arm flag is off (key "ps.enabled";
        // "PsEnabled" accepted as a forward-compatible alias).
        var psEnabled =
            RuleInspection.Value(sections, "tx-ps", "ps.enabled") ??
            RuleInspection.Value(sections, "tx-ps", "PsEnabled");
        bool reportedOff = RuleInspection.IsFalse(psEnabled);

        if (!gateSkipped && !reportedOff)
            return null;

        return new DiagnosticFinding(
            Title: "PureSignal was not armed this session",
            Detail:
                "PureSignal never ran because it was not armed. Zeus never turns " +
                "PureSignal on by itself — that is a safety rule, so PS always starts " +
                "OFF every time the server starts, and you arm it manually each " +
                "session. Open the PURESIGNAL panel and arm PS, then key up to let it " +
                "calibrate. If it still won't converge, check the HW Peak setting next.",
            Severity: DiagnosticSeverity.Likely,
            DocRef: "CLAUDE.md");
    }
}
