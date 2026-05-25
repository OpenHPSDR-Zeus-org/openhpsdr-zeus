// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
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

    private readonly RadioService _radio;
    private readonly TxService _tx;
    private readonly CwSidetoneSource? _sidetone;
    private readonly ILogger<ExternalPttService> _log;

    private readonly object _sync = new();
    private IProtocol1Client? _client;
    // True iff the most recent MOX-on we caused has not been released yet.
    // Cleared by the hang-release path, by TxActiveChanged(false) from any
    // other source, and by disconnect.
    private bool _owned;
    // Single-shot timer used to debounce falling edges. Created lazily and
    // re-armed via Change(); the underlying System.Threading.Timer is thread
    // safe so cancellation from the RX thread and fire-and-handle on the
    // ThreadPool coexist cleanly.
    private Timer? _hangTimer;

    public ExternalPttService(RadioService radio, TxService tx, ILogger<ExternalPttService> log, CwSidetoneSource? sidetone = null)
    {
        _radio = radio;
        _tx = tx;
        _sidetone = sidetone;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _radio.Connected += OnConnected;
        _radio.Disconnected += OnDisconnected;
        _tx.TxActiveChanged += OnTxActiveChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _radio.Connected -= OnConnected;
        _radio.Disconnected -= OnDisconnected;
        _tx.TxActiveChanged -= OnTxActiveChanged;
        IProtocol1Client? client;
        lock (_sync) { client = _client; _client = null; _owned = false; }
        if (client is not null)
        {
            client.HardwarePttChanged -= OnHardwarePttChanged;
            client.CwKeyDownChanged -= OnCwKeyDownChanged;
        }
        _hangTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _hangTimer?.Dispose();
    }

    private void OnConnected(IProtocol1Client client)
    {
        lock (_sync) { _client = client; _owned = false; }
        client.HardwarePttChanged += OnHardwarePttChanged;
        client.CwKeyDownChanged += OnCwKeyDownChanged;
    }

    private void OnDisconnected()
    {
        IProtocol1Client? client;
        lock (_sync) { client = _client; _client = null; _owned = false; }
        if (client is not null)
        {
            client.HardwarePttChanged -= OnHardwarePttChanged;
            client.CwKeyDownChanged -= OnCwKeyDownChanged;
        }
        _hangTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
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

    private void OnHardwarePttChanged(bool on)
    {
        if (on) HandleRising();
        else HandleFalling();
    }

    private static bool IsCwMode(Zeus.Contracts.RxMode mode) =>
        mode is Zeus.Contracts.RxMode.CWU or Zeus.Contracts.RxMode.CWL;

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
            if (_tx.TrySetMox(true, out var err))
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
        // cancel arrives. Re-check the latest level before acting.
        var client = _client;
        if (client is not null && client.HardwarePtt) return;

        bool releaseNow;
        lock (_sync) { releaseNow = _owned; _owned = false; }
        if (!releaseNow) return;

        if (_tx.TrySetMox(false, out var err))
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
}
