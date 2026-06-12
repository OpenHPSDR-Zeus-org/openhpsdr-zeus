// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Tests for the Voyeur Mode energy segmenter (zeus-la5 Phase 1): detects
// transmission "overs" by energy over the noise floor, with hang time so
// speech pauses don't split one over, and a minimum length so blips are
// ignored. Phase 2 swaps this detector for Silero VAD behind the same
// transition interface.

using System;
using Xunit;
using Zeus.Server.Voyeur;

namespace Zeus.Server.Tests;

public class VoyeurSegmenterTests
{
    private const int Rate = 48_000;

    private static float[] Block(int samples, float amplitude, int seed)
    {
        // Pseudo-random noise at a given amplitude (deterministic per seed).
        var rng = new Random(seed);
        var b = new float[samples];
        for (int i = 0; i < samples; i++)
            b[i] = (float)((rng.NextDouble() * 2 - 1) * amplitude);
        return b;
    }

    private static void Warm(VoyeurSegmenter seg, float floorAmp)
    {
        // Feed quiet blocks so the noise-floor follower settles.
        for (int i = 0; i < 30; i++)
            seg.Process(Block(2048, floorAmp, i));
    }

    [Fact]
    public void DetectsAnOver_StartThenEnd()
    {
        var seg = new VoyeurSegmenter(Rate, hangSeconds: 0.2, minSegmentSeconds: 0.1);
        Warm(seg, 0.001f);

        // Loud speech-like energy for ~0.5 s (well above the 8 dB open margin).
        bool sawStart = false;
        for (int i = 0; i < 12; i++) // 12 * 2048 ≈ 0.5 s
        {
            var r = seg.Process(Block(2048, 0.3f, 100 + i));
            if (r.Transition == VoyeurSegmenter.Transition.Started) sawStart = true;
        }
        Assert.True(sawStart, "segmenter did not open an over on loud audio");
        Assert.True(seg.IsActive);

        // Go quiet; after the hang time the over must close.
        VoyeurSegmenter.Result ended = default;
        for (int i = 0; i < 30; i++)
        {
            var r = seg.Process(Block(2048, 0.001f, 500 + i));
            if (r.Transition == VoyeurSegmenter.Transition.Ended) { ended = r; break; }
        }
        Assert.Equal(VoyeurSegmenter.Transition.Ended, ended.Transition);
        Assert.False(seg.IsActive);
        Assert.True(ended.DurationMs > 0, "ended over reported zero duration");
        Assert.True(ended.PeakDbfs < 0 && ended.PeakDbfs > -120, $"unexpected peak {ended.PeakDbfs}");
    }

    [Fact]
    public void HangTime_DoesNotSplitOnShortPause()
    {
        var seg = new VoyeurSegmenter(Rate, hangSeconds: 0.5, minSegmentSeconds: 0.1);
        Warm(seg, 0.001f);

        int ends = 0;
        // speech ... brief 0.1 s pause ... speech — one over, not two.
        void Speak(int n, int seed) { for (int i = 0; i < n; i++) { var r = seg.Process(Block(2048, 0.3f, seed + i)); if (r.Transition == VoyeurSegmenter.Transition.Ended) ends++; } }
        void Pause(int n, int seed) { for (int i = 0; i < n; i++) { var r = seg.Process(Block(2048, 0.001f, seed + i)); if (r.Transition == VoyeurSegmenter.Transition.Ended) ends++; } }

        Speak(10, 0);
        Pause(2, 1000);   // ~0.085 s < 0.5 s hang -> must NOT end
        Speak(10, 2000);
        Assert.Equal(0, ends);
        Assert.True(seg.IsActive);
    }

    [Fact]
    public void IgnoresSubMinimumBlip()
    {
        var seg = new VoyeurSegmenter(Rate, hangSeconds: 0.2, minSegmentSeconds: 0.5);
        Warm(seg, 0.001f);

        // A single loud block (~43 ms) — far under the 0.5 s minimum.
        seg.Process(Block(2048, 0.3f, 1));
        VoyeurSegmenter.Result result = default;
        for (int i = 0; i < 30; i++)
        {
            var r = seg.Process(Block(2048, 0.001f, 200 + i));
            if (r.Transition == VoyeurSegmenter.Transition.Ended) { result = r; break; }
        }
        // Blip is dropped: it closes as Idle, never reported as a real over.
        Assert.NotEqual(VoyeurSegmenter.Transition.Ended, result.Transition);
        Assert.False(seg.IsActive);
    }

    [Fact]
    public void Flush_ClosesInFlightOver()
    {
        var seg = new VoyeurSegmenter(Rate, hangSeconds: 2.0, minSegmentSeconds: 0.1);
        Warm(seg, 0.001f);
        for (int i = 0; i < 12; i++) seg.Process(Block(2048, 0.3f, i)); // open + sustain
        Assert.True(seg.IsActive);

        var r = seg.Flush();
        Assert.Equal(VoyeurSegmenter.Transition.Ended, r.Transition);
        Assert.False(seg.IsActive);
        Assert.True(r.DurationMs > 0);
    }

    [Fact]
    public void SteadyQuiet_NeverOpens()
    {
        var seg = new VoyeurSegmenter(Rate);
        for (int i = 0; i < 100; i++)
        {
            var r = seg.Process(Block(2048, 0.001f, i));
            Assert.Equal(VoyeurSegmenter.Transition.Idle, r.Transition);
        }
        Assert.False(seg.IsActive);
    }
}
