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
    private readonly long _dwellMs;      // time given to each candidate to acquire
    private readonly long _reacquireMs;  // grace to re-lock a known-good mode before resuming the scan

    private long _dwellStartMs;
    private long _lastSyncMs;
    private bool _everSynced;
    private bool _haveTimebase;

    /// <param name="dwellMs">
    /// How long to sit on each candidate submode before advancing while unsynced.
    /// OFDM acquisition on a good signal is sub-second; 2.5 s tolerates marginal
    /// signals without making a full 5-mode cycle feel sluggish (~12.5 s worst case).
    /// </param>
    /// <param name="reacquireMs">
    /// After a mode has locked and then lost sync (a QSO gap or a fade), hold it
    /// this long before resuming the scan — overs and deep fades shouldn't bump
    /// the operator off a mode that was demonstrably correct.
    /// </param>
    public FreeDvAutoScanner(long dwellMs = 2500, long reacquireMs = 4000, FreeDvSubmode[]? order = null)
    {
        _order = order is { Length: > 0 } ? order : DefaultOrder;
        _dwellMs = dwellMs;
        _reacquireMs = reacquireMs;
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
        _dwellStartMs = nowMs;
        _lastSyncMs = nowMs;
        _everSynced = false;
        _haveTimebase = true;
    }

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

        if (synced)
        {
            // Locked — hold here indefinitely and keep the dwell window fresh so
            // a momentary sync drop doesn't advance us on the very next tick.
            _everSynced = true;
            _lastSyncMs = nowMs;
            _dwellStartMs = nowMs;
            return null;
        }

        // Unsynced. If this mode locked recently, give it the re-acquire grace
        // before considering a move (covers transmit overs and fades).
        if (_everSynced && nowMs - _lastSyncMs < _reacquireMs)
            return null;

        // Scanning: stay on the current candidate until its dwell elapses.
        if (nowMs - _dwellStartMs < _dwellMs)
            return null;

        // Advance to the next candidate (wrapping). Unknown current → start of order.
        int idx = Array.IndexOf(_order, current);
        int next = idx < 0 ? 0 : (idx + 1) % _order.Length;
        _dwellStartMs = nowMs;
        return _order[next];
    }
}
