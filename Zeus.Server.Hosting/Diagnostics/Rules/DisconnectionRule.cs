// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections.Generic;

namespace Zeus.Server.Diagnostics;

/// <summary>
/// The radio won't connect or keeps dropping. The most common backend signature
/// is a run of RX socket timeouts ("radio gone"), which points at the radio or
/// the network rather than at Zeus — wired Ethernet vs WiFi is the usual culprit
/// at Protocol 1 packet rates. Surfaced for the won't-connect symptom with a
/// pointer to the troubleshooting guide.
/// </summary>
public sealed class DisconnectionRule : IKnownIssueRule
{
    public string Id => "disconnection";

    public IReadOnlyCollection<string> Symptoms { get; } = ["wont-connect"];

    public DiagnosticFinding? Evaluate(
        DiagnosticContext ctx, IReadOnlyList<DiagnosticSection> sections)
    {
        bool rxTimeout =
            RuleInspection.LogContains(ctx.RecentLog, "consecutive socket timeouts") ||
            RuleInspection.LogContains(ctx.RecentLog, "radio gone");

        if (rxTimeout)
        {
            return new DiagnosticFinding(
                Title: "Radio stopped sending data (RX timeout)",
                Detail:
                    "The log shows the radio stopped sending data long enough for " +
                    "Zeus to declare it gone (a run of receive timeouts). That points " +
                    "at the radio or the network, not at Zeus. First, ping the radio's " +
                    "IP: if it doesn't answer, the radio lost power or its firmware " +
                    "restarted. If it does answer, you're losing UDP packets — switch " +
                    "from WiFi to wired Gigabit Ethernet (Protocol 1 sends ~1200 " +
                    "packets/second and WiFi drops them under contention) and reduce " +
                    "other network load.",
                Severity: DiagnosticSeverity.Likely,
                DocRef: "docs/lessons/disconnection-troubleshooting.md");
        }

        // No timeout fingerprint: still offer the guide as general advice, but
        // only as a Warning so it doesn't outrank a more specific finding.
        return new DiagnosticFinding(
            Title: "Connection / dropout troubleshooting",
            Detail:
                "Connect-and-drop problems are usually network rather than Zeus. " +
                "Prefer wired Gigabit Ethernet over WiFi for Protocol 1 radios, make " +
                "sure your computer and the radio are on the same subnet, and after " +
                "switching networks click Disconnect then Discover (Zeus does not " +
                "auto-reconnect after a network change). If you run the dev server, " +
                "brief disconnects every time you save a file are expected Vite " +
                "reloads, not a fault.",
            Severity: DiagnosticSeverity.Warning,
            DocRef: "docs/lessons/disconnection-troubleshooting.md");
    }
}
