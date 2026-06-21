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
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. See ATTRIBUTIONS.md at the
// repository root for the full provenance statement.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

namespace Zeus.Server;

/// <summary>
/// Fixed-size history of (timestamp, RadioLoHz) pairs for the
/// delay-compensated panadapter CenterHz stamp (issue #597 Phase 2 — the
/// Thetis <c>pixel_ref</c> emulation). The display pixels broadcast at
/// time T were computed from IQ captured roughly D earlier (half the FFT
/// fill window + display-EMA lag + protocol transport), so stamping them
/// with the LO from <c>LookupAt(T − D)</c> makes every frame
/// self-describing: the client renders data at the frequency it actually
/// belongs to, regardless of arrival timing.
///
/// Concurrency: appends come from the radio state-handler thread (LO
/// changes only — at most input rate, ~60/s during a drag), lookups from
/// the RX/pipeline thread (30 Hz). A plain lock is held for nanoseconds;
/// no allocation after construction.
///
/// When the LO has been stable longer than the lookback delay, LookupAt
/// returns the current LO — i.e. the stamp is BYTE-IDENTICAL to the old
/// <c>state.RadioLoHz</c> behaviour. WWV frequency calibration (#325) and
/// every other consumer that runs at stable LO sees no change; the
/// regression test asserts this.
/// </summary>
public sealed class LoHistoryRing
{
    private readonly object _lock = new();
    private readonly long[] _timestamps;
    private readonly long[] _loHz;
    private int _count;
    private int _head; // index of the NEWEST entry when _count > 0

    public LoHistoryRing(int capacity = 64)
    {
        if (capacity < 2) capacity = 2;
        _timestamps = new long[capacity];
        _loHz = new long[capacity];
    }

    /// <summary>Record that the LO changed to <paramref name="loHz"/> at
    /// <paramref name="timestamp"/> (Stopwatch ticks). Out-of-order or
    /// duplicate timestamps are tolerated (last write wins on lookup ties).</summary>
    public void Append(long timestamp, long loHz)
    {
        lock (_lock)
        {
            _head = _count == 0 ? 0 : (_head + 1) % _timestamps.Length;
            _timestamps[_head] = timestamp;
            _loHz[_head] = loHz;
            if (_count < _timestamps.Length) _count++;
        }
    }

    /// <summary>
    /// The LO that was in effect at <paramref name="timestamp"/>: the newest
    /// entry whose timestamp is ≤ the query. Falls back to the oldest known
    /// entry when the query predates the whole ring (best effort — the ring
    /// only needs to span the lookback delay, ≤ ~400 ms), and to
    /// <paramref name="fallbackLoHz"/> when the ring is empty (pre-connect /
    /// first frames).
    /// </summary>
    public long LookupAt(long timestamp, long fallbackLoHz)
    {
        lock (_lock)
        {
            if (_count == 0) return fallbackLoHz;
            // Walk newest → oldest; the ring is tiny (≤64) and during a
            // gesture the answer is almost always within the first few
            // entries, so a linear scan beats anything fancier.
            int idx = _head;
            for (int i = 0; i < _count; i++)
            {
                if (_timestamps[idx] <= timestamp) return _loHz[idx];
                idx = (idx - 1 + _timestamps.Length) % _timestamps.Length;
            }
            // Query predates everything we remember — oldest known value.
            int oldest = (_head - (_count - 1) + _timestamps.Length) % _timestamps.Length;
            return _loHz[oldest];
        }
    }

    /// <summary>Forget all history (disconnect/reconnect).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _count = 0;
            _head = 0;
        }
    }
}
