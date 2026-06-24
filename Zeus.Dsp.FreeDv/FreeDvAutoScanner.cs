// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Auto submode detection for FreeDV. A received FreeDV signal carries no
// in-band "which mode am I?" marker, so an operator on the wrong submode hears
// nothing and the decoder silently never syncs — the single most common
// "receiving but no decode" trap. This scanner removes the guesswork: while the
// modem is unsynced it dwells on each candidate submode in turn until one locks,
// then holds that mode. It is a PURE, deterministic state machine — it owns no
// clock and no modem handle; the caller supplies a monotonic timestamp and the
// live sync state on each Tick and applies the returned submode. That keeps it
// unit-testable without real time and keeps all native/threading concerns in the
// owning service.

using Zeus.Contracts;

namespace Zeus.Dsp.FreeDv;

public sealed class FreeDvAutoScanner
{
    // Scan order: the on-air-common OFDM voice modes first (700D is the global
    // FreeDV default), then the legacy interop modes. Matches the panel order.
    private static readonly FreeDvSubmode[] DefaultOrder =
    {
        FreeDvSubmode.Mode700D,
        FreeDvSubmode.Mode700E,
        FreeDvSubmode.Mode700C,
        FreeDvSubmode.Mode1600,
        FreeDvSubmode.Mode800XA,
    };

    private readonly FreeDvSubmode[] _order;
    private readonly long _dwellMs;        // time given to each candidate to acquire while scanning
    private readonly long _lockConfirmMs;  // continuous sync needed to declare LOCKED and stop scanning
    private readonly long _unlockMs;       // continuous loss-of-sync needed to give up a lock and resume

    // Two states: SCANNING (cycling modes by dwell) and LOCKED (camped on a mode).
    // Transitions are debounced by *continuous* sync/loss duration so a flickering
    // marginal signal can't make the scanner chatter between modes — that thrash
    // reopens the modem every hop and destroys decode. We only camp after sync has
    // held for _lockConfirmMs, and only leave after it has been lost for _unlockMs.
    private bool _locked;
    private bool _prevSynced;
    private long _stateChangeMs;  // when the sync flag last flipped (start of current sync/unsync run)
    private long _dwellStartMs;   // when the current candidate began its scan dwell
    private bool _haveTimebase;

    /// <param name="dwellMs">
    /// How long to sit on each candidate while scanning (no sustained sync) before
    /// advancing. 3 s tolerates a marginal signal's acquisition without making a
    /// full 4-mode cycle feel sluggish.
    /// </param>
    /// <param name="lockConfirmMs">
    /// Continuous sync required before declaring LOCKED and halting the scan. Long
    /// enough (1.5 s) that a brief false-sync flicker on the wrong mode never camps us.
    /// </param>
    /// <param name="unlockMs">
    /// Continuous loss-of-sync required to give up a lock and resume scanning. Long
    /// (8 s) so QSO overs and deep fades don't bump the operator off the right mode.
    /// </param>
    public FreeDvAutoScanner(
        long dwellMs = 3000,
        long lockConfirmMs = 1500,
        long unlockMs = 8000,
        FreeDvSubmode[]? order = null)
    {
        _order = order is { Length: > 0 } ? order : DefaultOrder;
        _dwellMs = dwellMs;
        _lockConfirmMs = lockConfirmMs;
        _unlockMs = unlockMs;
    }

    /// <summary>Submodes this scanner cycles through, in scan order.</summary>
    public IReadOnlyList<FreeDvSubmode> Order => _order;

    /// <summary>
    /// (Re)seed the scan timebase. Call when auto-detect is enabled, when the
    /// modem (re)activates, or when the operator manually jumps to a submode —
    /// the current mode then gets a fresh full dwell before the scan advances.
    /// </summary>
    public void Reset(long nowMs)
    {
        _locked = false;
        _prevSynced = false;
        _stateChangeMs = nowMs;
        _dwellStartMs = nowMs;
        _haveTimebase = true;
    }

    /// <summary>True once a mode has held sync long enough to be camped on.</summary>
    public bool Locked => _locked;

    /// <summary>
    /// Decide whether to switch submode. Returns the submode to switch to, or
    /// null to stay on <paramref name="current"/>.
    /// </summary>
    /// <param name="nowMs">A monotonic millisecond timestamp (e.g. Environment.TickCount64).</param>
    /// <param name="synced">The modem's live sync state.</param>
    /// <param name="current">The modem's current submode.</param>
    public FreeDvSubmode? Tick(long nowMs, bool synced, FreeDvSubmode current)
    {
        if (!_haveTimebase) Reset(nowMs);

        // Track how long we've been continuously in the current sync state.
        if (synced != _prevSynced)
        {
            _prevSynced = synced;
            _stateChangeMs = nowMs;
        }
        long inState = nowMs - _stateChangeMs;

        if (_locked)
        {
            // Camped on a mode. Only give it up after sync has been lost
            // *continuously* for the unlock window (rides out overs/fades).
            if (!synced && inState >= _unlockMs)
            {
                _locked = false;
                _dwellStartMs = nowMs; // give the resumed scan a fresh dwell here
            }
            return null; // never change mode while locked
        }

        // SCANNING.
        if (synced)
        {
            // Sustained sync → camp here and stop scanning. A brief flicker
            // (inState < _lockConfirmMs) does NOT lock and does NOT reset the
            // dwell, so it can't stall or chatter the scan.
            if (inState >= _lockConfirmMs) _locked = true;
            return null;
        }

        // Unsynced: stay on this candidate until its dwell elapses, then advance.
        if (nowMs - _dwellStartMs < _dwellMs)
            return null;

        int idx = Array.IndexOf(_order, current);
        int next = idx < 0 ? 0 : (idx + 1) % _order.Length;
        _dwellStartMs = nowMs;
        return _order[next];
    }
}
