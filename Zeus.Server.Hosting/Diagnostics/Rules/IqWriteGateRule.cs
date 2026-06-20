// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections.Generic;

namespace Zeus.Server.Diagnostics;

/// <summary>
/// No (or near-zero) transmit power on a Hermes-class board even though a drive
/// byte is going out: the IQ-write gate isn't open, so the radio is keyed with a
/// non-zero drive level but no modulation samples reach the wire. The fingerprint
/// is a <c>p1.tx.rate</c> log line with a non-zero <c>drv=</c> while <c>peak=0</c>
/// (drive present, IQ peak zero). PS can't calibrate against a flat carrier
/// either, so this also explains a PS-won't-work report.
/// </summary>
public sealed class IqWriteGateRule : IKnownIssueRule
{
    public string Id => "iq-write-gate";

    public IReadOnlyCollection<string> Symptoms { get; } = ["no-tx-power", "ps-not-working"];

    public DiagnosticFinding? Evaluate(
        DiagnosticContext ctx, IReadOnlyList<DiagnosticSection> sections)
    {
        if (!RuleInspection.IsHermesClass(sections))
            return null;

        if (!RuleInspection.HasTxRateDriveButZeroPeak(ctx.RecentLog))
            return null;

        return new DiagnosticFinding(
            Title: "Transmit IQ never reaches the radio (IQ-write gate)",
            Detail:
                "The radio is being keyed with a drive level set, but the transmit " +
                "log shows the outgoing IQ peak is zero (drv= is non-zero while " +
                "peak=0). That means the modulation samples aren't being written to " +
                "the wire — the radio transmits an unmodulated, near-zero-power " +
                "signal. On Hermes-class boards this is the IQ-write-gate pattern: " +
                "audio is flowing into the TX stage but the IQ-write gate to the " +
                "protocol client isn't open. PureSignal also can't calibrate against " +
                "a flat carrier, so this can show up as 'PS won't work' too.",
            Severity: DiagnosticSeverity.Likely,
            DocRef: "docs/rca/2026-05-25-anan10e-wire-0x06-reclassify.md");
    }
}
