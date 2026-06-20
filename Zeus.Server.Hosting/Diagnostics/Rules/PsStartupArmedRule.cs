// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections.Generic;

namespace Zeus.Server.Diagnostics;

/// <summary>
/// Defensive safety-invariant check, evaluated for every symptom (no symptom
/// filter). PureSignal MUST always initialise to OFF on server startup — the
/// operator arms it manually each session. If the tx-ps probe reports PS armed
/// immediately at startup (before any operator action), the no-auto-arm
/// invariant has been violated and PS could be live on a feedback chain the
/// operator hasn't checked, which can saturate the feedback ADC. This should
/// essentially never fire; when it does, it is Critical.
/// </summary>
public sealed class PsStartupArmedRule : IKnownIssueRule
{
    public string Id => "ps-startup-armed";

    // Empty = evaluate for every symptom.
    public IReadOnlyCollection<string> Symptoms { get; } = [];

    public DiagnosticFinding? Evaluate(
        DiagnosticContext ctx, IReadOnlyList<DiagnosticSection> sections)
    {
        // The tx-ps probe is expected to flag this explicitly if it ever detects PS
        // armed at startup with no operator action. No such key exists today (the
        // probe only reports the current "ps.enabled" / "ps.armedAtStartup"), so in
        // normal operation this rule stays silent — it is purely a tripwire for a
        // regression of the PsEnabled-must-init-false invariant. We also honour a
        // log marker if one is ever emitted.
        var armedAtStartup =
            RuleInspection.Value(sections, "tx-ps", "ps.armedAtStartup") ??
            RuleInspection.Value(sections, "tx-ps", "ps.autoArmedAtStartup") ??
            RuleInspection.Value(sections, "tx-ps", "PsArmedAtStartup") ??
            RuleInspection.Value(sections, "tx-ps", "PsAutoArmedAtStartup");

        bool flagged = RuleInspection.IsTrue(armedAtStartup) ||
                       RuleInspection.LogContains(ctx.RecentLog, "ps.startup.autoArmed");

        if (!flagged)
            return null;

        return new DiagnosticFinding(
            Title: "PureSignal appears to have armed itself at startup",
            Detail:
                "PureSignal reports as armed at startup without any operator action. " +
                "This should never happen: Zeus always starts with PureSignal OFF as " +
                "a safety rule, and the operator arms it manually each session. An " +
                "auto-armed PureSignal on an unchecked feedback chain can saturate " +
                "the feedback ADC before you make any transmit decision. Do not " +
                "transmit until you have disarmed PureSignal and confirmed it is off. " +
                "Please report this with the log below — it indicates a real bug in " +
                "the startup state.",
            Severity: DiagnosticSeverity.Critical,
            DocRef: "CLAUDE.md");
    }
}
