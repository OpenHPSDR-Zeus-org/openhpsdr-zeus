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
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using Zeus.Contracts;

namespace Zeus.Server;

public sealed class TxService
{
    private readonly RadioService _radio;
    private readonly DspPipelineService _pipeline;
    private readonly StreamingHub _hub;
    private readonly IBandPlanService _bandPlan;
    private readonly ILogger<TxService> _log;
    private readonly object _sync = new();
    private bool _moxOn;
    private bool _tunOn;
    private DateTime? _moxStartedAt;
    private DateTime? _tunStartedAt;
    // Who currently owns MOX, set on the rising edge and cleared on the
    // falling one. See <see cref="MoxSource"/> for the release rule: only
    // the owning source can drop MOX, except UI (master override) and
    // <see cref="TryTripForAlert"/> (always wins). Null when MOX is off.
    private MoxSource? _moxOwner;

    public TxService(RadioService radio, DspPipelineService pipeline, StreamingHub hub, IBandPlanService bandPlan, ILogger<TxService> log)
    {
        _radio = radio;
        _pipeline = pipeline;
        _hub = hub;
        _bandPlan = bandPlan;
        _log = log;
    }

    public bool IsMoxOn { get { lock (_sync) return _moxOn; } }
    public bool IsTunOn { get { lock (_sync) return _tunOn; } }
    /// <summary>Source that currently holds MOX, or null when MOX is off.
    /// Subscribers (e.g. <c>CwEngine</c>) read this on the
    /// <see cref="TxActiveChanged"/> falling edge to tell apart "I dropped
    /// MOX myself" from "the operator overrode me from the UI".</summary>
    public MoxSource? MoxOwner { get { lock (_sync) return _moxOwner; } }

    /// <summary>
    /// Fires on every change to combined TX-active state
    /// (<c>IsMoxOn || IsTunOn</c>). Argument is the new combined value.
    /// Fired OFF the <c>_sync</c> lock so subscribers can call back into
    /// the service without deadlocking.
    ///
    /// <para>Primary subscriber: <see cref="NativeAudioSink"/> uses this
    /// to drain the RX audio ring on the rising edge so the operator
    /// hears instant silence on TX rather than the accumulated
    /// radio-clock-vs-soundcard-clock backlog. See issue #403 for the
    /// symptom this addresses on Windows.</para>
    /// </summary>
    public event Action<bool>? TxActiveChanged;

    // Last TX-active value observed by the firing path. Read+written
    // under _sync inside the helpers below. The "fire off the lock"
    // contract above means we capture the new value under the lock,
    // release it, then raise — so two rapid edges from different
    // threads can never reorder a stale notification past a fresh one.
    private bool _lastTxActiveFired;

    /// <summary>
    /// Recompute combined TX-active state under the lock; on change,
    /// capture the new value for off-lock notification. Returns the
    /// captured value or null if unchanged. Caller must invoke
    /// <see cref="RaiseTxActiveChanged"/> with the result outside the
    /// lock.
    /// </summary>
    private bool? CaptureTxActiveChangeUnderLock()
    {
        bool now = _moxOn || _tunOn;
        if (now == _lastTxActiveFired) return null;
        _lastTxActiveFired = now;
        return now;
    }

    private void RaiseTxActiveChanged(bool? captured)
    {
        if (captured is null) return;
        try { TxActiveChanged?.Invoke(captured.Value); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "tx.txActiveChanged subscriber threw");
        }
    }

    // TwoTone latch — independent of MOX/TUN. Set by RadioService.SetTwoTone
    // on every state mutation. TxTuneDriver polls it so the WDSP TXA pump
    // runs even when no mic uplink is feeding fexchange2 (PostGen mode=1
    // injects the two-tone excitation regardless of mic input).
    public bool IsTwoToneOn { get; private set; }
    internal void SetTwoToneOn(bool on) { IsTwoToneOn = on; }

    public DateTime? MoxStartedAt { get { lock (_sync) return _moxStartedAt; } }
    public DateTime? TunStartedAt { get { lock (_sync) return _tunStartedAt; } }

    // Test seams: drive the keyed-at timestamps directly from a unit test
    // without routing through TrySetMox/TrySetTun (which require an active
    // Protocol1 client). Only the TxMetersService timeout path reads them.
    internal void SetMoxStartedAtForTest(DateTime? t) { lock (_sync) _moxStartedAt = t; }
    internal void SetTunStartedAtForTest(DateTime? t) { lock (_sync) _tunStartedAt = t; }

    // Refuse to key TX when the current VFO / mode is out of band, unless the
    // operator has set the TxGuardIgnore override. Shared by MOX, TUN, and
    // TwoTone — all three are RF-emitting paths and must honour the same
    // regulatory check.
    private bool CheckBandGuard(out string? error)
    {
        if (_bandPlan.TxGuardIgnore) { error = null; return true; }
        var state = _radio.Snapshot();
        if (_bandPlan.InBand(state.VfoHz, state.Mode)) { error = null; return true; }
        var seg = _bandPlan.GetSegment(state.VfoHz);
        var segLabel = seg is not null
            ? $"{seg.Label} ({seg.ModeRestriction})"
            : "no amateur allocation";
        error = $"TX blocked: {state.VfoHz / 1_000_000.0:F4} MHz is out of band for mode {state.Mode} in region {_bandPlan.CurrentRegion.DisplayName} ({segLabel})";
        _log.LogWarning("tx.guard.blocked vfo={Vfo}Hz mode={Mode} region={Region}", state.VfoHz, state.Mode, _bandPlan.CurrentRegion.Id);
        // Surface the block in the UI via the same AlertFrame path that SWR
        // trips and TX timeouts use (AlertKind.OutOfBand reserved for this).
        // The frontend AlertBanner consumes this automatically.
        _hub.Broadcast(new AlertFrame(AlertKind.OutOfBand, error));
        return false;
    }

    /// <summary>Back-compat shim: callers that don't tag a source get
    /// <see cref="MoxSource.UI"/>, the master override. New callers should
    /// pass an explicit source so the release path can reject foreign drops.</summary>
    public bool TrySetMox(bool on, out string? error)
        => TrySetMox(on, MoxSource.UI, out error);

    /// <summary>
    /// Source-aware MOX setter. The <paramref name="source"/> tag determines
    /// whether the call is allowed when MOX is already held by another
    /// source — see <see cref="MoxSource"/> for the rule. UI always wins;
    /// any other source can only drop MOX it itself raised.
    /// </summary>
    public bool TrySetMox(bool on, MoxSource source, out string? error)
    {
        // FR-1 interlock: no TX unless connected.
        if (on && !_radio.IsConnected) { error = "not connected"; return false; }

        if (on && !CheckBandGuard(out error)) return false;

        bool wasTunOn;
        bool? txActiveCaptured;
        lock (_sync)
        {
            if (_moxOn == on)
            {
                // No-op edge. Don't reseat ownership on a redundant key-on
                // (first-claim semantics — whoever raised the rising edge owns
                // it for the life of the transmission). Drops are rejected
                // here too so a foreign source can't even silently match the
                // current state, since that would let a hardware-PTT release
                // race a UI-driven send.
                error = null;
                return true;
            }
            if (!on && source != MoxSource.UI && _moxOwner is not null && _moxOwner != source)
            {
                // Foreign source trying to release someone else's MOX. Refuse.
                error = $"MOX held by {_moxOwner}; only UI can override";
                return false;
            }
            wasTunOn = _tunOn;
            if (on)
            {
                _tunOn = false;  // MOX-on preempts TUN (PRD FR-7 mutual-exclusion)
                _tunStartedAt = null;
                _moxStartedAt = DateTime.UtcNow;
                _moxOwner = source;
            }
            else
            {
                _moxStartedAt = null;
                _moxOwner = null;
            }
            _moxOn = on;
            txActiveCaptured = CaptureTxActiveChangeUnderLock();
        }
        RaiseTxActiveChanged(txActiveCaptured);

        if (wasTunOn && on)
        {
            // TUN was up and MOX came on — drop the tune carrier before keying
            // the mic chain so we don't briefly sum both.
            _pipeline.SetTxTune(false);
        }

        // Order: mute RX before keying TX on MOX-on; reverse on MOX-off.
        // Engine handles the RXA/TXA pair atomically under its own lock.
        _pipeline.SetMox(on);
        // PERF_PASS_3_DEBUG: t1 — server received MOX edge. Uncommitted.
        _log.LogInformation("tx.mox.{Edge}.recv ts={Ts}",
            on ? "on" : "off", System.Diagnostics.Stopwatch.GetTimestamp());
        _radio.SetMox(on);
        // MOX-edge unconditionally deactivates TUN on the drive-source side —
        // MOX-on preempts TUN above, MOX-off should also leave the recompute
        // pointing at _drivePct for the next half-key.
        _radio.NotifyTunActive(false);
        _log.LogInformation("tx.mox on={On}", on);
        _hub.Broadcast(new MoxStateFrame(MoxOn: on, TunOn: false));
        error = null;
        return true;
    }

    /// <summary>
    /// Arm or disarm the TwoTone test generator AND key MOX. Mirrors the Thetis
    /// chkTestIMD_CheckedChanged path (setup.cs:11162-11165, 11189-11216):
    /// TwoTone owns the MOX state while armed and unconditionally drops it on
    /// disarm. This matches the operator expectation "press 2-Tone → radio is
    /// transmitting two tones" without a separate MOX press.
    ///
    /// Order on arm: configure PostGen via RadioService.SetTwoTone (which arms
    /// xgen mode=1 with the sideband-correct signed freqs from Group A), THEN
    /// flip MOX on so TXA is alive when the generator starts running. On disarm
    /// the order is reversed — MOX off first so the radio stops emitting RF
    /// before the engine drops the generator run flag.
    /// </summary>
    public bool TrySetTwoTone(TwoToneSetRequest req, out string? error)
    {
        ArgumentNullException.ThrowIfNull(req);
        // Connect interlock — same as TrySetMox / TrySetTun. No TX of any kind
        // before the radio is up.
        if (req.Enabled && !_radio.IsConnected) { error = "not connected"; return false; }

        if (req.Enabled && !CheckBandGuard(out error)) return false;

        bool wasMoxOn, wasTunOn;
        bool? txActiveCaptured;
        lock (_sync)
        {
            wasMoxOn = _moxOn;
            wasTunOn = _tunOn;
            if (req.Enabled)
            {
                // TwoTone-on preempts TUN and OWNS MOX while armed (PRD FR-7
                // mutual-exclusion + Thetis setup.cs:11162-11165). _moxOn
                // tracks "TX is keyed", whether by mic-MOX or TwoTone — the
                // operator's MOX button reflects the same flag, so a TwoTone
                // arm reads as "transmitting" in the UI.
                _tunOn = false;
                _tunStartedAt = null;
                _moxOn = true;
                _moxStartedAt = DateTime.UtcNow;
                // TwoTone is a UI-driven feature; tag the owner so the source
                // gate in TrySetMox correctly rejects a foreign drop while
                // the two-tone test is armed.
                _moxOwner = MoxSource.UI;
            }
            else
            {
                _moxOn = false;
                _moxStartedAt = null;
                _moxOwner = null;
            }
            txActiveCaptured = CaptureTxActiveChangeUnderLock();
        }
        RaiseTxActiveChanged(txActiveCaptured);

        if (req.Enabled)
        {
            if (wasTunOn) _pipeline.SetTxTune(false);
            // Arm PostGen + cache state (signed freqs for USB family, mag,
            // run=1) BEFORE flipping MOX so TXA pump sees a configured
            // generator on the very first sample window.
            _radio.SetTwoTone(req);
            IsTwoToneOn = true;
            _pipeline.SetMox(true);
            _radio.SetMox(true);
            // Drive recompute for the next half-key — TwoTone is mic-path-free
            // so any TUN drive % left over should be reset.
            _radio.NotifyTunActive(false);
            _log.LogInformation(
                "tx.twoTone on=true f1={F1} f2={F2} mag={Mag}",
                req.Freq1, req.Freq2, req.Mag);
            _hub.Broadcast(new MoxStateFrame(MoxOn: true, TunOn: false));
        }
        else
        {
            // Disarm: flip MOX off first so RF stops, then drop the generator
            // run flag in the engine. Order matches Thetis (setup.cs:11189-11216
            // — MOX off, then TXPostGenRun=0).
            IsTwoToneOn = false;
            _pipeline.SetMox(false);
            _radio.SetMox(false);
            _radio.SetTwoTone(req);
            _log.LogInformation("tx.twoTone on=false");
            _hub.Broadcast(new MoxStateFrame(MoxOn: false, TunOn: false));
        }
        error = null;
        return true;
    }

    public bool TrySetTun(bool on, out string? error)
    {
        // Same connect-interlock as MOX: no TX of any kind without a connected
        // backend. IsConnected covers both P1 (Protocol1Client) and P2
        // (Protocol2Client owned by DspPipelineService); ActiveClient alone
        // would reject TUN on any G2 MkII.
        if (on && !_radio.IsConnected) { error = "not connected"; return false; }

        if (on && !CheckBandGuard(out error)) return false;

        bool wasMoxOn;
        bool? txActiveCaptured;
        lock (_sync)
        {
            if (_tunOn == on) { error = null; return true; }
            wasMoxOn = _moxOn;
            if (on)
            {
                _moxOn = false;  // TUN-on preempts MOX (PRD FR-7)
                _moxStartedAt = null;
                // Whoever owned MOX just lost the channel — TUN took it.
                _moxOwner = null;
                _tunStartedAt = DateTime.UtcNow;
            }
            else
            {
                _tunStartedAt = null;
            }
            _tunOn = on;
            txActiveCaptured = CaptureTxActiveChangeUnderLock();
        }
        RaiseTxActiveChanged(txActiveCaptured);

        if (wasMoxOn && on)
        {
            // MOX was engaged and TUN is taking over — stop mic-driven TX first.
            _pipeline.SetMox(false);
            _radio.SetMox(false);
        }

        // TUN is a WDSP TXA post-gen tone that needs TXA running. Engage the
        // engine MOX (which flips RXA→TXA state) whenever TUN is on, without
        // flipping TxService._moxOn — we tracked that separately above.
        _pipeline.SetMox(on);
        _radio.SetMox(on);
        _pipeline.SetTxTune(on);
        // Swap the drive source AFTER the engine flip so the DriveFilter byte
        // on the first TUN frame carries the tune % (not the stale drive %).
        _radio.NotifyTunActive(on);
        _log.LogInformation("tx.tun on={On}", on);
        _hub.Broadcast(new MoxStateFrame(MoxOn: false, TunOn: on));
        error = null;
        return true;
    }

    /// <summary>
    /// Trip both MOX and TUN for a protection alert (SWR, timeout, etc.).
    /// Emits an <see cref="AlertFrame"/> over WS so the UI can inform the operator.
    /// Operator must manually re-key. PRD FR-6.
    /// </summary>
    public void TryTripForAlert(AlertKind kind, string reason)
    {
        bool wasMoxOn, wasTunOn;
        bool? txActiveCaptured;
        lock (_sync)
        {
            wasMoxOn = _moxOn;
            wasTunOn = _tunOn;
            _moxOn = false;
            _tunOn = false;
            // Clear the keyed-at timestamps too — otherwise EvaluateTimeoutTrip
            // would keep re-firing against the stale start time after the trip.
            _moxStartedAt = null;
            _tunStartedAt = null;
            // Trip always wins regardless of owner. Drop ownership so the
            // next rising edge starts from a clean source.
            _moxOwner = null;
            txActiveCaptured = CaptureTxActiveChangeUnderLock();
        }

        if (wasMoxOn || wasTunOn)
        {
            _pipeline.SetMox(false);
            _radio.SetMox(false);
            if (wasTunOn) _pipeline.SetTxTune(false);
            _radio.NotifyTunActive(false);
            _log.LogWarning("tx.trip kind={Kind} reason={Reason}", kind, reason);
            _hub.Broadcast(new AlertFrame(kind, reason));
            _hub.Broadcast(new MoxStateFrame(MoxOn: false, TunOn: false));
        }
        RaiseTxActiveChanged(txActiveCaptured);
    }
}
