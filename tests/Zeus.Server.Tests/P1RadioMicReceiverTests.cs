// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Distributed under the GNU General Public License v2 or later. See the
// LICENSE file at the root of this repository for full text.

using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server;

namespace Zeus.Server.Tests;

// Covers the Protocol-1 codec mic / line-in re-blocker (issue #992 —
// ANAN-10E line-in selected but silent on the air). EP6 frames carry 126
// codec samples each; at 48 kHz IQ rate they map 1:1 to the 48 kHz codec, at
// higher rates the gateware duplicates each sample N = iqRateHz/48000 times
// (Hermes USB protocol V1.58 NOTE 2) and the receiver decimates.
public class P1RadioMicReceiverTests
{
    private static short[] BuildRamp(int n, short start = 0, short step = 100)
    {
        var s = new short[n];
        for (int i = 0; i < n; i++) s[i] = (short)(start + i * step);
        return s;
    }

    [Fact]
    public void ReBlocks_OneTwentySixSamplePackets_At48k_IntoNineSixtySampleBlocks()
    {
        var blocks = new List<byte[]>();
        var rx = new P1RadioMicReceiver(b => blocks.Add(b.ToArray()), NullLogger.Instance);

        // 8 packets × 126 = 1008 → one 960-block emitted, 48-sample remainder.
        for (int i = 0; i < 8; i++) rx.Accept(BuildRamp(126, start: 8192, step: 0), 48_000);

        Assert.Single(blocks);
        Assert.Equal(P1RadioMicReceiver.OutputBlockSamples * 4, blocks[0].Length);

        // int16 8192 → f32 0.25, little-endian on the wire out.
        float first = BinaryPrimitives.ReadSingleLittleEndian(blocks[0].AsSpan(0, 4));
        Assert.Equal(0.25f, first, 5);
    }

    [Fact]
    public void DecimatesByFour_At192kIqRate()
    {
        var blocks = new List<byte[]>();
        var rx = new P1RadioMicReceiver(b => blocks.Add(b.ToArray()), NullLogger.Instance);

        // At 192k IQ rate every 4 consecutive samples are duplicates of one
        // 48 kHz sample. The decimation phase is CONTINUOUS across packets
        // (Thetis parity), so the output rate is exactly raw/4 — NOT
        // ⌈126/4⌉ = 32 per packet, which would inflate the rate. 32 packets ×
        // 126 = 4032 raw → 4032/4 = 1008 unique 48 kHz samples → one 960-block.
        for (int i = 0; i < 32; i++)
            rx.Accept(BuildRamp(126, start: 16384, step: 0), 192_000);

        Assert.Single(blocks);
        Assert.Equal(P1RadioMicReceiver.OutputBlockSamples * 4, blocks[0].Length);

        // Exactly raw/4 accepted — no per-packet rounding inflation.
        Assert.Equal(4032 / 4, rx.TotalSamplesAccepted); // 1008
    }

    [Fact]
    public void DecimationPhase_CarriesAcrossPacketBoundaries()
    {
        // 126 is not a multiple of 4. A per-packet phase reset would emit
        // ⌈126/4⌉ = 32 per packet → 64 for two packets (an inflated 48762 Hz).
        // The Thetis-correct continuous counter emits ⌊252/4⌋ = 63.
        var rx = new P1RadioMicReceiver(_ => { }, NullLogger.Instance);

        rx.Accept(BuildRamp(126, start: 16384, step: 0), 192_000);
        rx.Accept(BuildRamp(126, start: 16384, step: 0), 192_000);

        Assert.Equal(63, rx.TotalSamplesAccepted);
    }

    [Fact]
    public void DecimationPhase_ResetsOnRateChange()
    {
        // Thetis zeroes mic_decimation_count on a sample-rate change. Feed a
        // partial 192k run (leaves the phase mid-group), then switch to 48k
        // where decim=1 keeps all 4 samples — proving the phase didn't leak
        // across the rate change.
        var rx = new P1RadioMicReceiver(_ => { }, NullLogger.Instance);

        rx.Accept(BuildRamp(6, start: 16384, step: 0), 192_000); // 6/4 → keeps 1
        long after192 = rx.TotalSamplesAccepted;
        rx.Accept(BuildRamp(4, start: 16384, step: 0), 48_000);  // decim=1: all 4 kept

        Assert.Equal(after192 + 4, rx.TotalSamplesAccepted);
    }

    [Fact]
    public void Reset_DropsRemainder_NoStitchAcrossSwitch()
    {
        var blocks = new List<byte[]>();
        var rx = new P1RadioMicReceiver(b => blocks.Add(b.ToArray()), NullLogger.Instance);

        // 5 packets at 48k = 630 samples, no block yet.
        for (int i = 0; i < 5; i++) rx.Accept(BuildRamp(126, start: 16384, step: 0), 48_000);
        Assert.Empty(blocks);

        rx.Reset();

        // 8 fresh packets (1008 samples) — after Reset the first block arrives
        // only when 960 new samples have landed, not stitched onto the 630
        // pre-switch remainder.
        for (int i = 0; i < 8; i++) rx.Accept(BuildRamp(126, start: 16384, step: 0), 48_000);
        Assert.Single(blocks);
    }

    [Fact]
    public void EmptyInput_NoOp()
    {
        var blocks = new List<byte[]>();
        var rx = new P1RadioMicReceiver(b => blocks.Add(b.ToArray()), NullLogger.Instance);

        rx.Accept(ReadOnlySpan<short>.Empty, 48_000);

        Assert.Empty(blocks);
        Assert.Equal(0, rx.TotalSamplesAccepted);
    }

    [Fact]
    public void ZeroOrNegativeRateHz_FallsBackToOneToOne()
    {
        var blocks = new List<byte[]>();
        var rx = new P1RadioMicReceiver(b => blocks.Add(b.ToArray()), NullLogger.Instance);

        // Caller misbehaviour: rateHz=0. Receiver must treat as no decimation
        // (decim=1) rather than divide by zero.
        for (int i = 0; i < 8; i++) rx.Accept(BuildRamp(126, start: 8192, step: 0), 0);

        Assert.Single(blocks);
    }
}
