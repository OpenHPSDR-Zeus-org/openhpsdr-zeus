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

namespace Zeus.Protocol1;

/// <summary>
/// SPSC-ish ring buffer of (I, Q) s16 pairs linking the WDSP TXA producer
/// (TxAudioIngest, flushes 1024-sample blocks every ~21 ms) to the P1 EP2
/// packer consumer (ControlFrame, pulls 63 pairs per USB frame every 1.5 ms).
///
/// Rate shape:
///   producer:  1024 × (1 / 21.3 ms)  ≈ 48 000 pairs / s
///   consumer:  126  × (1 /    3 ms)  ≈ 42 000 pairs / s
/// The consumer is slower than the producer by ~12 % (because the TX tick
/// interval is 3 ms, not 2.625 ms). Over the length of a transmission the
/// ring fills; a small drop-oldest policy is intentional — staleness on the
/// order of one WDSP block (~21 ms) is below audible threshold for the
/// round-trip, and drop-oldest keeps the loop latency bounded.
///
/// Reading past the producer (ring empty) returns (0, 0) — silent IQ, which
/// is the right thing to transmit while the ingest side is still filling its
/// first block or the user has un-keyed.
///
/// Implemented with a plain lock rather than a lock-free pair of volatiles.
/// The enqueue path runs once per ~21 ms and the dequeue once per ~1.5 ms,
/// so contention is negligible and a lock is far simpler to reason about
/// than the spin cases you'd need in a lock-free version.
/// </summary>
public sealed class TxIqRing : ITxIqSource
{
    // 16384 pairs ≈ 340 ms of audio at 48 kHz. Bumped from 4096 after observing
    // ~24 % drop-oldest at /api/tx/diag under real mic: WDSP writes bursts of
    // 1024 pairs, EP2 drains 63 pairs every 3 ms — a browser GC pause or a
    // back-to-back ingest flush easily stacks 4+ burst writes before the
    // consumer catches up. 340 ms keeps the drop count at zero in the normal
    // case without introducing audible stale-tail at key-down (MOX-off still
    // drains on the next rising edge).
    public const int DefaultCapacityPairs = 16384;

    private readonly short[] _iBuf;
    private readonly short[] _qBuf;
    private readonly int _capacity;
    private readonly object _gate = new();
    private int _head;   // write index
    private int _count;  // number of valid pairs
    private long _totalWritten;
    private long _totalRead;
    private long _dropped;
    // Rolling energy of the last-served IQ: |i| + |q|, decayed. Exposed via
    // /api/tx/diag as a "are we serving silence or real samples?" probe. A
    // keyed transmission producing real RF should show values in the 1000s
    // (s16 range is ±32767); values near 0 mean WDSP TXA isn't producing or
    // the ring is draining faster than it fills.
    private double _recentMag;

    public TxIqRing(int capacityPairs = DefaultCapacityPairs)
    {
        if (capacityPairs <= 0) throw new ArgumentOutOfRangeException(nameof(capacityPairs));
        _capacity = capacityPairs;
        _iBuf = new short[capacityPairs];
        _qBuf = new short[capacityPairs];
    }

    public int Capacity => _capacity;
    public int Count { get { lock (_gate) return _count; } }
    public long TotalWritten { get { lock (_gate) return _totalWritten; } }
    public long TotalRead { get { lock (_gate) return _totalRead; } }
    public long Dropped { get { lock (_gate) return _dropped; } }
    public double RecentMag { get { lock (_gate) return _recentMag; } }

    /// <summary>
    /// Push one block of interleaved float IQ into the ring, converting
    /// −1..+1 floats to s16. Samples above ±1 are saturated rather than
    /// wrapped. <paramref name="iqInterleaved"/> length must be even
    /// (pairs of I, Q).
    /// </summary>
    public void Write(ReadOnlySpan<float> iqInterleaved)
    {
        if ((iqInterleaved.Length & 1) != 0)
            throw new ArgumentException("length must be even (I,Q pairs)", nameof(iqInterleaved));

        lock (_gate)
        {
            for (int k = 0; k < iqInterleaved.Length; k += 2)
            {
                short i = ToS16(iqInterleaved[k]);
                short q = ToS16(iqInterleaved[k + 1]);
                _iBuf[_head] = i;
                _qBuf[_head] = q;
                _head = (_head + 1) % _capacity;
                if (_count < _capacity) _count++;
                else _dropped++;   // overwrote the oldest
            }
            _totalWritten += iqInterleaved.Length / 2;
        }
    }

    /// <summary>
    /// Clear the ring. Called on MOX-off so a fresh key-down starts from a
    /// drained buffer — otherwise you'd hear the tail of the previous
    /// transmission the moment PTT closes.
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            _count = 0;
            _head = 0;
        }
    }

    public (short i, short q) Next(double amplitude)
    {
        lock (_gate)
        {
            if (_count == 0) return (0, 0);
            int tail = (_head - _count + _capacity) % _capacity;
            short i = _iBuf[tail];
            short q = _qBuf[tail];
            _count--;
            _totalRead++;
            // Single-pole exponential: 99 % decay keeps the value smooth over
            // the ~63-sample EP2 cadence while still dropping to near-zero
            // within ~200 ms of unkey.
            _recentMag = 0.99 * _recentMag + 0.01 * (Math.Abs((int)i) + Math.Abs((int)q));
            if (amplitude >= 0.999) return (i, q);
            double a = Math.Clamp(amplitude, 0.0, 1.0);
            return ((short)(i * a), (short)(q * a));
        }
    }

    private static short ToS16(float v)
    {
        // WDSP TXA hands back normalised floats (≈−1..+1). Saturate rather
        // than letting int-cast wrap — a spike above 1.0 is rare in practice
        // and wrapping would introduce loud click artefacts on the air.
        if (!float.IsFinite(v)) return 0;
        float clamped = v;
        if (clamped > 1.0f) clamped = 1.0f;
        else if (clamped < -1.0f) clamped = -1.0f;
        return (short)Math.Round(clamped * short.MaxValue);
    }
}
