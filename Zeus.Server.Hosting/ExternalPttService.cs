// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Microsoft.Extensions.Hosting;
using Zeus.Contracts;
using Zeus.Protocol1;

namespace Zeus.Server;

/// <summary>
/// Promotes the radio's hardware-PTT echo (C0[0] of every inbound EP6 frame)
/// into a host-side MOX request. The HL2 gateware generates a shaped CW
/// envelope on its own whenever the rear KEY tip is grounded — protocol doc
/// line 200 — so the radio transmits without any host involvement. Without
/// this service the host stays unkeyed: the MOX UI indicator never lights,
/// TX meters stay at idle cadence, PsAutoAttenuate doesn't engage, and the
/// FR-6 120 s TX-timeout never arms.
///
/// State machine (per-connection):
///   • inbound rising AND host MOX/TUN/TwoTone off → claim MOX via
///     <c>TxService.TrySetMox(true)</c>, mark <c>_owned</c>.
///   • inbound falling → arm a hang timer (<see cref="HangTime"/>). A rising
///     edge inside the window cancels it (inter-character CW spaces).
///   • hang elapsed AND inbound still low AND <c>_owned</c> → release MOX.
///   • UI/TCI/trip drops MOX externally → <c>_owned</c> clears so the next
///     hang timer doesn't re-release what someone else changed.
///
/// Inbound echo of host-initiated MOX (operator clicks the UI button) lands
/// here too, but the <c>IsMoxOn || IsTunOn || IsTwoToneOn</c> gate ignores
/// it because the host is already the source of truth.
/// </summary>
public sealed class ExternalPttService : IHostedService, IDisposable
{
    // CW operators expect TX to bridge inter-character spaces. 250 ms matches
    // Thetis's default CW hang — long enough for a ~25 wpm character gap
    // (~80 ms) plus margin, short enough that releasing a straight key feels
    // immediate. Not configurable yet; if operators ask for a knob it lives
    // on the per-mode DSP settings panel alongside the CW pitch.
    private static readonly TimeSpan HangTime = TimeSpan.FromMilliseconds(250);

    /// <summary>Read-only hang time surfaced to the UI ("Hang: 250 ms") and the
    /// REST status endpoint. The knob itself is out of scope for this pass.</summary>
    public static int HangMs => (int)HangTime.TotalMilliseconds;

    /// <summary>Live PTT-IN level (lamp state) for the REST status snapshot.
    /// Fed by both protocol edge sources; false when disconnected.</summary>
    public bool IsKeyed => _rawHigh;

    private readonly RadioService _radio;
    private readonly TxService _tx;
    private readonly StreamingHub _hub;
    private readonly PttSettingsStore _settings;
    private readonly CwSidetoneSource? _sidetone;
    private readonly ILogger<ExternalPttService> _log;

    private readonly object _sync = new();
    private IProtocol1Client? _client;
    private Zeus.Protocol2.Protocol2Client? _p2Client;
    // True iff the most recent MOX-on we caused has not been released yet.
    // Cleared by the hang-release path, by TxActiveChanged(false) from any
    // other source, and by disconnect.
    private bool _owned;
    // Latest raw hardware-PTT level, fed by BOTH protocol edge sources. Used by
    // the hang-release race-guard (protocol-agnostic replacement for the P1-only
    // IProtocol1Client.HardwarePtt latched property) and surfaced as the lamp
    // state. Volatile: written on the RX thread, read on the timer ThreadPool.
    private volatile bool _rawHigh;
    // Previous P2 PttIn level, for edge detection on the level-sampled P2
    // telemetry stream. Owned by the P2 RX thread only.
    private bool _p2PrevPtt;
    // Single-shot timer used to debounce falling edges. Created lazily and
    // re-armed via Change(); the underlying System.Threading.Timer is thread
    // safe so cancellation from the RX thread and fire-and-handle on the
    // ThreadPool coexist cleanly.
    private Timer? _hangTimer;

    public ExternalPttService(
        RadioService radio,
        TxService tx,
        StreamingHub hub,
        PttSettingsStore settings,
        ILogger<ExternalPttService> log,
        CwSidetoneSource? sidetone = null)
    {
        _radio = radio;
        _tx = tx;
        _hub = hub;
        _settings = settings;
        _sidetone = sidetone;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _radio.Connected += OnConnected;
        _radio.Disconnected += OnDisconnected;
        _radio.P2Connected += OnP2Connected;
        _radio.P2Disconnected += OnP2Disconnected;
        _tx.TxActiveChanged += OnTxActiveChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _radio.Connected -= OnConnected;
        _radio.Disconnected -= OnDisconnected;
        _radio.P2Connected -= OnP2Connected;
        _radio.P2Disconnected -= OnP2Disconnected;
        _tx.TxActiveChanged -= OnTxActiveChanged;
        IProtocol1Client? client;
        Zeus.Protocol2.Protocol2Client? p2;
        lock (_sync) { client = _client; _client = null; p2 = _p2Client; _p2Client = null; _owned = false; }
        if (client is not null)
        {
            client.HardwarePttChanged -= OnHardwarePttChanged;
            client.CwKeyDownChanged -= OnCwKeyDownChanged;
        }
        if (p2 is not null) p2.TelemetryReceived -= OnP2Telemetry;
        _hangTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _hangTimer?.Dispose();
    }

    public ExternalPttStatusDto Snapshot()
    {
        IProtocol1Client? client;
        Zeus.Protocol2.Protocol2Client? p2;
        bool owned;
        lock (_sync)
        {
            client = _client;
            p2 = _p2Client;
            owned = _owned;
        }

        // PTT-IN is wired on BOTH protocols (P1 HardwarePttChanged, P2 hi-pri
        // PttIn telemetry), so availability/level must be protocol-agnostic.
        bool available = client is not null || p2 is not null;
        // _rawHigh is the protocol-agnostic raw footswitch level, fed by both
        // edge sources — use it instead of the P1-only IProtocol1Client.HardwarePtt.
        bool? hardwarePtt = available ? _rawHigh : (bool?)null;
        bool? cwKeyDown = client?.CwKeyDown;
        var mode = _radio.Snapshot().Mode;
        bool moxOn = _tx.IsMoxOn;
        bool tunOn = _tx.IsTunOn;
        bool twoToneOn = _tx.IsTwoToneOn;
        string? owner = _tx.MoxOwner?.ToString();
        bool enabled = _settings.Get();
        string recommendation = !available
            ? "No hardware PTT client is attached; external PTT takeover is idle."
            : owned
                ? "External PTT owns MOX through the hardware source path; falling edges release only hardware-owned transmissions after hang time."
                : !enabled
                    ? "Hardware PTT-IN is read-only: the lamp tracks the footswitch, but PTT-IN will not key MOX until enabled in Radio Settings."
                    : hardwarePtt == true && !moxOn
                        ? "Hardware PTT is asserted but MOX is not active; check connection state, band guard, and TX interlocks."
                        : "External PTT takeover is armed and read-only diagnostics are live.";

        return new(
            SchemaVersion: 1,
            Available: available,
            Protocol: client is not null ? "P1" : p2 is not null ? "P2" : "none",
            HardwarePtt: hardwarePtt,
            CwKeyDown: cwKeyDown,
            OwnedMox: owned,
            HangTimeMs: (int)HangTime.TotalMilliseconds,
            MoxOn: moxOn,
            TunOn: tunOn,
            TwoToneOn: twoToneOn,
            MoxOwner: owner,
            CwMode: IsCwMode(mode),
            SidetoneAvailable: _sidetone is not null,
            DiagnosticRecommendation: recommendation,
            GeneratedUtc: DateTimeOffset.UtcNow,
            Enabled: enabled);
    }

    private void OnConnected(IProtocol1Client client)
    {
        lock (_sync) { _client = client; _owned = false; _rawHigh = false; }
        client.HardwarePttChanged += OnHardwarePttChanged;
        client.CwKeyDownChanged += OnCwKeyDownChanged;
    }

    private void OnDisconnected()
    {
        IProtocol1Client? client;
        lock (_sync) { client = _client; _client = null; _owned = false; _rawHigh = false; }
        if (client is not null)
        {
            client.HardwarePttChanged -= OnHardwarePttChanged;
            client.CwKeyDownChanged -= OnCwKeyDownChanged;
        }
        _hangTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        BroadcastLamp(false);
    }

    // Sidetone follows the gateware's shaped keyer output (C0[2] /
    // cw_key_status), which toggles per dit/dah — NOT the held PTT (C0[0] /
    // ptt_resp) that drives MOX below. Reading PTT here would leave the tone
    // on for the whole keyed period including inter-element gaps + hang time.
    // Gated to CW modes so an SSB mic-PTT press doesn't inject a 600 Hz tone.
    // (zeus-cl2)
    private void OnCwKeyDownChanged(bool down)
    {
        if (_sidetone is null) return;
        if (!IsCwMode(_radio.Snapshot().Mode)) return;
        if (down) _sidetone.Down();
        else _sidetone.Up();
    }

    private void OnHardwarePttChanged(bool on) => HandleRawPtt(on);

    // ---- Protocol 2 wiring (P2 PTT-in → MOX, §4) -------------------------

    private void OnP2Connected(Zeus.Protocol2.Protocol2Client client)
    {
        lock (_sync) { _p2Client = client; _owned = false; _rawHigh = false; _p2PrevPtt = false; }
        client.TelemetryReceived += OnP2Telemetry;
    }

    private void OnP2Disconnected()
    {
        Zeus.Protocol2.Protocol2Client? client;
        lock (_sync) { client = _p2Client; _p2Client = null; _owned = false; _rawHigh = false; _p2PrevPtt = false; }
        if (client is not null) client.TelemetryReceived -= OnP2Telemetry;
        _hangTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        BroadcastLamp(false);
    }

    // UDP-1025 hi-priority status is LEVEL-sampled at the P2 status cadence, not
    // an edge event — derive rising/falling by comparing against the previous
    // PttIn level. Runs on the Protocol2Client RX thread.
    private void OnP2Telemetry(Zeus.Protocol2.P2TelemetryReading reading)
    {
        bool ptt = reading.PttIn;
        if (ptt == _p2PrevPtt) return; // no edge
        _p2PrevPtt = ptt;
        HandleRawPtt(ptt);
    }

    // ---- Shared debounce / hang / ownership engine -----------------------

    private static bool IsCwMode(RxMode mode) =>
        mode is RxMode.CWU or RxMode.CWL;

    // Single entry point for a raw hardware-PTT edge from EITHER protocol. The
    // lamp always tracks the physical input; MOX promotion is gated by the
    // operator Enable toggle. Edge-triggered: MOX is only ever promoted here,
    // never on connect/reconnect — so a persisted-ON gate arms without keying.
    private void HandleRawPtt(bool on)
    {
        _rawHigh = on;
        BroadcastLamp(on);
        // Enable gate: when off, the lamp still tracks the footswitch but we
        // never promote it to MOX (UI-only keying). Checked on each edge so a
        // toggle takes effect immediately without restart.
        if (!_settings.Get()) return;
        if (on) HandleRising();
        else HandleFalling();
    }

    private void BroadcastLamp(bool keyed) => _hub.Broadcast(new PttStatusFrame(keyed));

    private void HandleRising()
    {
        // Cancel any pending hang-release — operator re-keyed before the
        // window expired (inter-character CW gap).
        _hangTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        // Host is already the source of truth. Ignore the echo.
        if (_tx.IsMoxOn || _tx.IsTunOn || _tx.IsTwoToneOn) return;

        lock (_sync)
        {
            if (_owned) return;
            _owned = true;
        }

        // TxService.TrySetMox locks, broadcasts via the hub, and pokes WDSP
        // through DspPipelineService — work we don't want on the RX thread.
        // The fire-and-forget Task.Run is OK because OnHardwarePttChanged is
        // already edge-triggered (single rise per external press) and a
        // subsequent fall is handled by HandleFalling regardless of ordering.
        _ = Task.Run(() =>
        {
            if (_tx.TrySetMox(true, MoxSource.Hardware, out var err))
            {
                _log.LogInformation("externalPtt.takeover.applied");
            }
            else
            {
                _log.LogWarning("externalPtt.takeover.rejected reason={Reason}", err);
                lock (_sync) _owned = false;
            }
        });
    }

    private void HandleFalling()
    {
        // Arm or re-arm the single-shot hang timer. If we don't own the MOX
        // (UI is driving) the eventual fire will short-circuit via _owned=false
        // — but we still arm so a subsequent UI-release after the external key
        // returns to the steady "external is low, _owned is false" state.
        var timer = _hangTimer;
        if (timer is null)
        {
            timer = new Timer(OnHangElapsed, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _hangTimer = timer;
        }
        timer.Change(HangTime, Timeout.InfiniteTimeSpan);
    }

    private void OnHangElapsed(object? _)
    {
        // Race guard: a rising edge inside the hang window cancels the timer,
        // but the timer can already be in-flight on the ThreadPool when the
        // cancel arrives. Re-check the latest raw level before acting. _rawHigh
        // is fed by both protocols, so this is protocol-agnostic; on P1 it
        // tracks the same C0[0] level the old IProtocol1Client.HardwarePtt did.
        if (_rawHigh) return;

        bool releaseNow;
        lock (_sync) { releaseNow = _owned; _owned = false; }
        if (!releaseNow) return;

        if (_tx.TrySetMox(false, MoxSource.Hardware, out var err))
        {
            _log.LogInformation("externalPtt.release.applied");
        }
        else
        {
            _log.LogWarning("externalPtt.release.rejected reason={Reason}", err);
        }
    }

    private void OnTxActiveChanged(bool active)
    {
        // Any drop of TX-active state from outside our control (UI release,
        // SWR trip, TCI ZZTX0, …) invalidates our ownership claim. The next
        // external rise will re-acquire; the next external fall will no-op.
        if (active) return;
        lock (_sync) _owned = false;
    }

    // ---- Test seams ------------------------------------------------------

    /// <summary>Test seam: drive a raw P1-style hardware-PTT edge through the
    /// full shared engine (lamp + gated MOX promotion). Mirrors what
    /// <see cref="OnHardwarePttChanged"/> does for a live P1 client.</summary>
    internal void TestRawPttP1(bool on) => OnHardwarePttChanged(on);

    /// <summary>Test seam: simulate a P2 client connect, exactly as the
    /// RadioService.P2Connected event would (so Snapshot reports the P2
    /// client + protocol). Mirrors <see cref="OnP2Connected"/>.</summary>
    internal void TestP2Connect(Zeus.Protocol2.Protocol2Client client) => OnP2Connected(client);

    /// <summary>Test seam: feed a P2 hi-priority telemetry sample through the
    /// level→edge derivation and the shared engine, exactly as a live
    /// Protocol2Client TelemetryReceived would.</summary>
    internal void TestP2Telemetry(Zeus.Protocol2.P2TelemetryReading reading) => OnP2Telemetry(reading);

    /// <summary>Test seam: current latched raw PTT-IN (lamp) level.</summary>
    internal bool TestRawHigh => _rawHigh;
}
