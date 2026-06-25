// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Collections.Generic;
using Zeus.Contracts;
using Zeus.Server.Hosting.Remote;
using Xunit;

namespace Zeus.Server.Tests;

/// <summary>
/// Block-level coverage for the SOTA RX-audio Opus encoder pipeline: it must
/// chunk an RX <see cref="AudioFrame"/> into 20 ms (960-sample) Opus packets,
/// emit a real (non-empty) packet per block, and downmix stereo to mono. The
/// over-the-wire WebRTC behaviour is bench-verified separately (no peer in unit
/// tests); this pins the framing + encode contract.
/// </summary>
public class RemoteRxAudioPipelineTests
{
    private static AudioFrame Frame(int sampleCount, byte channels)
    {
        var samples = new float[sampleCount * channels];
        // A low-amplitude tone so the encoder has real signal (not pure silence).
        for (int i = 0; i < samples.Length; i++)
            samples[i] = 0.1f * MathF.Sin(i * 0.05f);
        return new AudioFrame(0, 0, 0, channels, 48000, (ushort)sampleCount, samples);
    }

    [Fact]
    public void Encode_MonoFrame_EmitsOnePacketPer20msBlock()
    {
        var packets = new List<byte[]>();
        var pipe = new RemoteRxAudioPipeline(p => packets.Add(p.ToArray()));

        // 1920 mono samples = 40 ms = exactly two 20 ms Opus frames.
        pipe.Encode(Frame(1920, 1));

        Assert.Equal(2, packets.Count);
        Assert.All(packets, p => Assert.True(p.Length > 0));
    }

    [Fact]
    public void Encode_PartialBlock_HoldsUntilFull()
    {
        var packets = new List<byte[]>();
        var pipe = new RemoteRxAudioPipeline(p => packets.Add(p.ToArray()));

        // 480 samples = half a block — nothing emitted yet.
        pipe.Encode(Frame(480, 1));
        Assert.Empty(packets);

        // Another 480 completes the 960-sample block — one packet now.
        pipe.Encode(Frame(480, 1));
        Assert.Single(packets);
    }

    [Fact]
    public void Encode_StereoFrame_DownmixesToMono()
    {
        var packets = new List<byte[]>();
        var pipe = new RemoteRxAudioPipeline(p => packets.Add(p.ToArray()));

        // 960 stereo frames = 1920 interleaved samples → 960 mono samples → 1 block.
        pipe.Encode(Frame(960, 2));

        Assert.Single(packets);
        Assert.True(packets[0].Length > 0);
    }
}
