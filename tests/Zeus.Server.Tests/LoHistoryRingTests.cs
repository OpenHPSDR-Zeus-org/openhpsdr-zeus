// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Tests for the delay-compensated CenterHz stamp's LO history
// (issue #597 Phase 2). The stable-LO regression is load-bearing: WWV
// frequency calibration (#325) and every other consumer that reads frame
// CenterHz at a quiet dial must see EXACTLY the live RadioLoHz — this
// radio has a prior spurious-calibration incident, so that equivalence is
// pinned by test, not by inspection.

using Xunit;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class LoHistoryRingTests
{
    [Fact]
    public void EmptyRing_ReturnsFallback()
    {
        var ring = new LoHistoryRing();
        Assert.Equal(14_200_000L, ring.LookupAt(1_000, fallbackLoHz: 14_200_000L));
    }

    [Fact]
    public void StableLo_StampEqualsLiveRadioLo_Regression325()
    {
        // The LO tuned once, long ago. ANY query after that change — i.e.
        // every tick at a stable dial, which is where WWV cal runs — must
        // return the live LO, byte-identical to the pre-Phase-2 stamp.
        var ring = new LoHistoryRing();
        ring.Append(timestamp: 1_000, loHz: 14_200_000L);
        for (long t = 1_000; t < 1_000_000; t += 33_333)
        {
            Assert.Equal(14_200_000L, ring.LookupAt(t, fallbackLoHz: -1L));
        }
    }

    [Fact]
    public void MidGesture_LookbackReturnsTheLoAtCaptureTime()
    {
        var ring = new LoHistoryRing();
        ring.Append(1_000, 14_200_000L);
        ring.Append(2_000, 14_200_500L);
        ring.Append(3_000, 14_201_000L);
        // A tick at t=3_100 whose pixels were captured at t=2_500 must be
        // stamped with the LO in effect at 2_500 — the middle entry.
        Assert.Equal(14_200_500L, ring.LookupAt(2_500, fallbackLoHz: -1L));
        // Exactly on a boundary: the entry at that timestamp wins.
        Assert.Equal(14_200_500L, ring.LookupAt(2_000, fallbackLoHz: -1L));
        // Just before the first entry: best-effort oldest.
        Assert.Equal(14_200_000L, ring.LookupAt(500, fallbackLoHz: -1L));
    }

    [Fact]
    public void Wraparound_KeepsTheNewestCapacityEntries()
    {
        var ring = new LoHistoryRing(capacity: 4);
        for (int i = 0; i < 10; i++)
        {
            ring.Append(timestamp: i * 100, loHz: 7_000_000L + i);
        }
        // Newest entry (t=900) serves late queries…
        Assert.Equal(7_000_009L, ring.LookupAt(10_000, fallbackLoHz: -1L));
        // …entries 6..9 survive (capacity 4); a query inside the window
        // resolves correctly…
        Assert.Equal(7_000_007L, ring.LookupAt(750, fallbackLoHz: -1L));
        // …and a query older than the whole ring degrades to the oldest
        // surviving value (bounded error, never a wild stamp).
        Assert.Equal(7_000_006L, ring.LookupAt(0, fallbackLoHz: -1L));
    }

    [Fact]
    public void Clear_ForgetsHistory()
    {
        var ring = new LoHistoryRing();
        ring.Append(1_000, 14_200_000L);
        ring.Clear();
        Assert.Equal(-1L, ring.LookupAt(2_000, fallbackLoHz: -1L));
    }

    [Fact]
    public void WheelGestureProfile_StampsAreMonotonicAndLagBounded()
    {
        // Simulate a 10-notch wheel burst at 50 ms spacing (500 Hz steps),
        // then verify a 30 Hz tick sequence with an 85 ms lookback never
        // produces a stamp that moves BACKWARD — the reversal-counter
        // invariant from the #597 verification plan, applied server-side.
        var ring = new LoHistoryRing();
        const long tickHz = 10_000; // fake stopwatch resolution: 10 kHz
        for (int i = 0; i < 10; i++)
        {
            ring.Append(timestamp: i * 50 * tickHz / 1000, loHz: 14_200_000L + i * 500);
        }
        long lookback = 85 * tickHz / 1000;
        long prev = long.MinValue;
        for (long t = 0; t < 1_000 * tickHz / 1000; t += 33 * tickHz / 1000)
        {
            long stamp = ring.LookupAt(t - lookback, fallbackLoHz: 14_200_000L);
            Assert.True(stamp >= prev, $"stamp went backward at t={t}: {stamp} < {prev}");
            prev = stamp;
        }
        // And the final settled stamp is the final LO.
        Assert.Equal(14_204_500L, ring.LookupAt(long.MaxValue, fallbackLoHz: -1L));
    }
}
