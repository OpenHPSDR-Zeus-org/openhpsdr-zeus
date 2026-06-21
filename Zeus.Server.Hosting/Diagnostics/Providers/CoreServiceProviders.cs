// SPDX-License-Identifier: GPL-2.0-or-later
//
// v2 providers that wrap existing read-only service snapshots verbatim — zero
// behaviour change. Each surfaces an already-public Snapshot()/diagnostics
// method on the unified diagnostics surface so an operator can read live state
// for that subsystem (and the conformance harness auto-tests it).

using Zeus.Dsp.Wdsp;
using Zeus.Protocol2;

namespace Zeus.Server.Diagnostics;

/// <summary>Websocket hub health: client/subscriber counts + frame-drop counters.</summary>
public sealed class StreamingHubProvider : IDiagnosticsProvider
{
    private readonly StreamingHub _hub;
    public StreamingHubProvider(StreamingHub hub) => _hub = hub ?? throw new ArgumentNullException(nameof(hub));

    public string Id => "streaming.hub";
    public string RouteSegment => "streaming-hub";
    public string Category => "streaming";
    public int SchemaVersion => 1;
    public string Description => "Websocket streaming hub: connected clients, subscribers, and frame-drop counters.";

    public object Snapshot() => _hub.DiagnosticsSnapshot();

    public IReadOnlyList<DiagnosticsSelfCheck> SelfChecks => new[]
    {
        new DiagnosticsSelfCheck("hub-snapshot-available",
            "Streaming hub snapshot builds.", DiagnosticsSeverity.Info,
            _ => DiagnosticsProbe.NonNull(_hub.DiagnosticsSnapshot(), "streaming hub")),
    };
}

/// <summary>Current radio state (the /api/state payload).</summary>
public sealed class RadioStateProvider : IDiagnosticsProvider
{
    private readonly RadioService _radio;
    public RadioStateProvider(RadioService radio) => _radio = radio ?? throw new ArgumentNullException(nameof(radio));

    public string Id => "radio.state";
    public string RouteSegment => "radio-state";
    public string Category => "radio";
    public int SchemaVersion => 1;
    public string Description => "Live radio state: VFO, mode, filter, sample rate, TX/RX posture.";

    public object Snapshot() => _radio.Snapshot();

    public IReadOnlyList<DiagnosticsSelfCheck> SelfChecks => new[]
    {
        new DiagnosticsSelfCheck("state-snapshot-available",
            "Radio state snapshot builds.", DiagnosticsSeverity.Info,
            _ => DiagnosticsProbe.NonNull(_radio.Snapshot(), "radio state")),
    };
}

/// <summary>Board/feature capability fingerprint (the /api/capabilities payload).</summary>
public sealed class RadioCapabilitiesProvider : IDiagnosticsProvider
{
    private readonly CapabilitiesService _caps;
    public RadioCapabilitiesProvider(CapabilitiesService caps) => _caps = caps ?? throw new ArgumentNullException(nameof(caps));

    public string Id => "radio.capabilities";
    public string RouteSegment => "radio-capabilities";
    public string Category => "radio";
    public int SchemaVersion => 1;
    public string Description => "Board type, radio type, feature flags, and host mode.";

    public object Snapshot() => _caps.Snapshot();

    public IReadOnlyList<DiagnosticsSelfCheck> SelfChecks => new[]
    {
        new DiagnosticsSelfCheck("capabilities-snapshot-available",
            "Capabilities snapshot builds.", DiagnosticsSeverity.Info,
            _ => DiagnosticsProbe.NonNull(_caps.Snapshot(), "capabilities")),
    };
}

/// <summary>HamClock sidecar integration status.</summary>
public sealed class HamClockProvider : IDiagnosticsProvider
{
    private readonly HamClockService _hamClock;
    public HamClockProvider(HamClockService hamClock) => _hamClock = hamClock ?? throw new ArgumentNullException(nameof(hamClock));

    public string Id => "integration.hamclock";
    public string RouteSegment => "integration-hamclock";
    public string Category => "integration";
    public int SchemaVersion => 1;
    public string Description => "HamClock sidecar install/process status and Node availability.";

    public object Snapshot() => _hamClock.Snapshot();

    public IReadOnlyList<DiagnosticsSelfCheck> SelfChecks => new[]
    {
        new DiagnosticsSelfCheck("hamclock-snapshot-available",
            "HamClock status snapshot builds.", DiagnosticsSeverity.Info,
            _ => DiagnosticsProbe.NonNull(_hamClock.Snapshot(), "hamclock")),
    };
}

/// <summary>External PTT / serial keying line state.</summary>
public sealed class ExternalPttProvider : IDiagnosticsProvider
{
    private readonly ExternalPttService _externalPtt;
    public ExternalPttProvider(ExternalPttService externalPtt) => _externalPtt = externalPtt ?? throw new ArgumentNullException(nameof(externalPtt));

    public string Id => "external-ptt.serial";
    public string RouteSegment => "external-ptt";
    public string Category => "hardware";
    public int SchemaVersion => 1;
    public string Description => "External PTT serial line: port state, CTS/RTS levels, last edge.";

    public object Snapshot() => _externalPtt.Snapshot();

    public IReadOnlyList<DiagnosticsSelfCheck> SelfChecks => new[]
    {
        new DiagnosticsSelfCheck("external-ptt-snapshot-available",
            "External PTT snapshot builds.", DiagnosticsSeverity.Info,
            _ => DiagnosticsProbe.NonNull(_externalPtt.Snapshot(), "external ptt")),
    };
}

/// <summary>Protocol-2 TX IQ path health (when a P2 radio is connected).</summary>
public sealed class Protocol2TxIqProvider : IDiagnosticsProvider
{
    private readonly DspPipelineService _dsp;
    public Protocol2TxIqProvider(DspPipelineService dsp) => _dsp = dsp ?? throw new ArgumentNullException(nameof(dsp));

    public string Id => "protocol2.tx-iq";
    public string RouteSegment => "protocol2-tx-iq";
    public string Category => "protocol";
    public int SchemaVersion => 1;
    public string Description => "Protocol-2 TX IQ queue health: queued packets, send failures, drain resets.";

    public object Snapshot()
    {
        Protocol2Client? p2 = _dsp.ActiveP2Client;
        object? diag = p2?.TxIqDiagnosticsSnapshot();
        return diag is not null
            ? new { schemaVersion = 1, available = true, txIq = diag }
            : new { schemaVersion = 1, available = false, reason = "no-active-protocol2-client" };
    }

    public IReadOnlyList<DiagnosticsSelfCheck> SelfChecks => new[]
    {
        new DiagnosticsSelfCheck("protocol2-active",
            "A Protocol-2 client is connected.", DiagnosticsSeverity.Info,
            _ => _dsp.ActiveP2Client is not null
                ? new SelfCheckResult(SelfCheckOutcome.Pass, "Protocol-2 client active.", DateTimeOffset.UtcNow)
                : new SelfCheckResult(SelfCheckOutcome.Warn, "No active Protocol-2 client (idle or P1).", DateTimeOffset.UtcNow)),
    };
}

/// <summary>DSP pipeline runtime: WDSP channel lifecycle, wisdom/FFT plan state, engine rates.</summary>
public sealed class DspPipelineProvider : IDiagnosticsProvider
{
    private readonly DspPipelineService _dsp;
    private readonly WdspWisdomInitializer _wisdom;

    public DspPipelineProvider(DspPipelineService dsp, WdspWisdomInitializer wisdom)
    {
        _dsp = dsp ?? throw new ArgumentNullException(nameof(dsp));
        _wisdom = wisdom ?? throw new ArgumentNullException(nameof(wisdom));
    }

    public string Id => "dsp.pipeline";
    public string RouteSegment => "dsp-pipeline";
    public string Category => "dsp";
    public int SchemaVersion => 1;
    public string Description => "DSP pipeline runtime: WDSP channels, wisdom/FFT plan state, engine status.";

    public object Snapshot() => _dsp.SnapshotDiagnostics(_wisdom);

    public IReadOnlyList<DiagnosticsSelfCheck> SelfChecks => new[]
    {
        new DiagnosticsSelfCheck("dsp-snapshot-available",
            "DSP pipeline diagnostics snapshot builds.", DiagnosticsSeverity.Info,
            _ => DiagnosticsProbe.NonNull(_dsp.SnapshotDiagnostics(_wisdom), "dsp pipeline")),
    };
}

/// <summary>Small shared helpers for provider self-checks.</summary>
internal static class DiagnosticsProbe
{
    public static SelfCheckResult NonNull(object? value, string what) =>
        value is not null
            ? new SelfCheckResult(SelfCheckOutcome.Pass, $"{what} snapshot available.", DateTimeOffset.UtcNow)
            : new SelfCheckResult(SelfCheckOutcome.Fail, $"{what} snapshot returned null.", DateTimeOffset.UtcNow);
}
