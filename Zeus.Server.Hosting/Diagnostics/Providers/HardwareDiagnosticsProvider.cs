// SPDX-License-Identifier: GPL-2.0-or-later
//
// v2 provider wrapping the live hardware diagnostic snapshot. The underlying
// Snapshot() returns an anonymous object (rich discovery/state/telemetry tree)
// that must serialise verbatim for wire compatibility, so this provider returns
// it unchanged — it falls through to the reflection JSON resolver (not the
// source-gen context). Strictly read-only; touches no PureSignal arm state.

namespace Zeus.Server.Diagnostics;

public sealed class HardwareDiagnosticsProvider : IDiagnosticsProvider
{
    private readonly HardwareDiagnosticsService _diag;

    public HardwareDiagnosticsProvider(HardwareDiagnosticsService diag)
    {
        _diag = diag ?? throw new ArgumentNullException(nameof(diag));
    }

    public string Id => "hardware.diagnostics";
    public string RouteSegment => "hardware";
    public string Category => "hardware";
    public int SchemaVersion => 1;
    public string Description => "Live hardware discovery, capabilities, and decoded P1/P2 wire telemetry.";

    public object Snapshot() => _diag.Snapshot();

    public IReadOnlyList<DiagnosticsSelfCheck> SelfChecks => new[]
    {
        new DiagnosticsSelfCheck("snapshot-available",
            "Hardware diagnostics snapshot builds without error.", DiagnosticsSeverity.Error,
            _ => _diag.Snapshot() is not null
                ? new SelfCheckResult(SelfCheckOutcome.Pass, "Hardware snapshot is available.", DateTimeOffset.UtcNow)
                : new SelfCheckResult(SelfCheckOutcome.Fail, "Hardware snapshot returned null.", DateTimeOffset.UtcNow)),
    };
}
