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

        // At 192k IQ rate, every 4 consecutive samples in the packet are
        // duplicates of one 48 kHz sample. Feeding 126 raw samples therefore
        // contributes ⌈126/4⌉ = 32 unique 48 kHz samples per packet. 30 packets
        // → 30 × 32 = 960 unique samples → one block.
        for (int i = 0; i < 30; i++)
            rx.Accept(BuildRamp(126, start: 16384, step: 0), 192_000);

        Assert.Single(blocks);
        Assert.Equal(P1RadioMicReceiver.OutputBlockSamples * 4, blocks[0].Length);

        // Total samples ACCEPTED = 30 packets × 32 (decimated) per-packet = 960.
        Assert.Equal(960, rx.TotalSamplesAccepted);
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
