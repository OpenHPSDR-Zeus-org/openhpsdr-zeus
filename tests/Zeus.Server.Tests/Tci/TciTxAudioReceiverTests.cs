// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Distributed under the GNU General Public License v2 or later. See the
// LICENSE file at the root of this repository for full text.

using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server.Tci;

namespace Zeus.Server.Tests.Tci;

/// <summary>
/// Unit tests for <see cref="TciTxAudioReceiver"/> — the inbound TX-audio
/// decoder that mixes stereo to mono, chunks into 960-sample 48 kHz blocks,
/// and forwards f32le bytes downstream to TxAudioIngest.OnMicPcmBytes.
///
/// The downstream forward delegate is captured into a list of byte[] so the
/// tests can assert on block count, contents, and ordering without standing
/// up the full WDSP pipeline.
/// </summary>
public class TciTxAudioReceiverTests
{
    private static byte[] EncodeStereoFloats(float[] mono, float pan = 0.5f)
    {
        // Encodes mono samples as stereo Float32 LE (L=R=sample). Used to
        // emulate the v2.5.1 client's TX upload, which copies mic to L=R.
        var bytes = new byte[mono.Length * 2 * 4];
        for (int i = 0; i < mono.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan((i * 2 + 0) * 4, 4), mono[i]);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan((i * 2 + 1) * 4, 4), mono[i]);
        }
        return bytes;
    }

    private static byte[] EncodeStereoLR(float[] left, float[] right)
    {
        Assert.Equal(left.Length, right.Length);
        var bytes = new byte[left.Length * 2 * 4];
        for (int i = 0; i < left.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan((i * 2 + 0) * 4, 4), left[i]);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan((i * 2 + 1) * 4, 4), right[i]);
        }
        return bytes;
    }

    private static byte[] EncodeMonoFloats(float[] samples)
    {
        var bytes = new byte[samples.Length * 4];
        for (int i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4, 4), samples[i]);
        return bytes;
    }

    private static double DecodedRms(byte[] f32leBlock)
    {
        double sum = 0;
        int count = f32leBlock.Length / 4;
        for (int i = 0; i < count; i++)
        {
            float sample = BinaryPrimitives.ReadSingleLittleEndian(f32leBlock.AsSpan(i * 4, 4));
            sum += sample * sample;
        }
        return Math.Sqrt(sum / count);
    }

    [Fact]
    public void Stereo48k_FullBlock_ForwardsOne960SampleMicBlock()
    {
        var forwarded = new List<byte[]>();
        Action<ReadOnlyMemory<byte>> capture = m => forwarded.Add(m.ToArray());
        var receiver = new TciTxAudioReceiver(capture, NullLogger.Instance);

        // 960 mono samples → encoded as 1920 stereo floats (L=R)
        var mono = Enumerable.Range(0, 960).Select(i => i / 960f).ToArray();
        var payload = EncodeStereoFloats(mono);

        receiver.AcceptTxAudio(
            payload, TciSampleType.Float32,
            declaredFloatCount: 1920, channels: 2, sampleRate: 48000);

        Assert.Single(forwarded);
        Assert.Equal(960 * 4, forwarded[0].Length);

        // Round-trip the f32le bytes back to floats and verify mono mixdown
        // (L=R, so 0.5*(L+R) == sample).
        for (int i = 0; i < mono.Length; i++)
        {
            float decoded = BinaryPrimitives.ReadSingleLittleEndian(forwarded[0].AsSpan(i * 4, 4));
            Assert.Equal(mono[i], decoded, precision: 5);
        }
    }

    [Fact]
    public void Stereo48k_MixesLandRByAveraging()
    {
        var forwarded = new List<byte[]>();
        Action<ReadOnlyMemory<byte>> capture = m => forwarded.Add(m.ToArray());
        var receiver = new TciTxAudioReceiver(capture, NullLogger.Instance);

        // L=0.4, R=0.8 across 960 frames → mixed should be 0.6
        var left = Enumerable.Repeat(0.4f, 960).ToArray();
        var right = Enumerable.Repeat(0.8f, 960).ToArray();
        var payload = EncodeStereoLR(left, right);

        receiver.AcceptTxAudio(payload, TciSampleType.Float32, 1920, channels: 2, sampleRate: 48000);

        Assert.Single(forwarded);
        for (int i = 0; i < 960; i++)
        {
            float decoded = BinaryPrimitives.ReadSingleLittleEndian(forwarded[0].AsSpan(i * 4, 4));
            Assert.Equal(0.6f, decoded, precision: 5);
        }
    }

    [Fact]
    public void NonAligned2048StereoFrame_BuffersRemainderAcrossCalls()
    {
        var forwarded = new List<byte[]>();
        Action<ReadOnlyMemory<byte>> capture = m => forwarded.Add(m.ToArray());
        var receiver = new TciTxAudioReceiver(capture, NullLogger.Instance);

        // 2048 frames per call (default v2.5.1 client size) doesn't divide
        // by 960 — first call should produce 2 mic blocks, leftover 128.
        // Second call appends another 2048 → cumulative 2176 → 2 more blocks
        // (2*960=1920) with 256 leftover. Total 4 blocks across 2 calls.
        var first = EncodeStereoFloats(new float[2048]);
        var second = EncodeStereoFloats(new float[2048]);

        receiver.AcceptTxAudio(first, TciSampleType.Float32, 4096, channels: 2, sampleRate: 48000);
        Assert.Equal(2, forwarded.Count);

        receiver.AcceptTxAudio(second, TciSampleType.Float32, 4096, channels: 2, sampleRate: 48000);
        Assert.Equal(4, forwarded.Count);

        // Each forwarded block is exactly one mic-block (960*4=3840) bytes.
        Assert.All(forwarded, b => Assert.Equal(960 * 4, b.Length));
    }

    [Fact]
    public void Mono48k_PassesThroughWithoutMixdown()
    {
        var forwarded = new List<byte[]>();
        Action<ReadOnlyMemory<byte>> capture = m => forwarded.Add(m.ToArray());
        var receiver = new TciTxAudioReceiver(capture, NullLogger.Instance);

        var mono = Enumerable.Range(0, 960).Select(i => 0.1f * (i % 10)).ToArray();
        receiver.AcceptTxAudio(EncodeMonoFloats(mono), TciSampleType.Float32, 960, channels: 1, sampleRate: 48000);

        Assert.Single(forwarded);
        for (int i = 0; i < 960; i++)
        {
            float decoded = BinaryPrimitives.ReadSingleLittleEndian(forwarded[0].AsSpan(i * 4, 4));
            Assert.Equal(mono[i], decoded, precision: 5);
        }
    }

    [Fact]
    public void Stereo24k_ResamplesWithSharedTxConverter()
    {
        var forwarded = new List<byte[]>();
        Action<ReadOnlyMemory<byte>> capture = m => forwarded.Add(m.ToArray());
        var receiver = new TciTxAudioReceiver(capture, NullLogger.Instance);

        var first = EncodeStereoFloats(Enumerable.Range(0, 480).Select(i => i / 480f).ToArray());
        var second = EncodeStereoFloats(Enumerable.Range(480, 480).Select(i => i / 960f).ToArray());

        receiver.AcceptTxAudio(first, TciSampleType.Float32, 960, channels: 2, sampleRate: 24000);
        Assert.Empty(forwarded);

        receiver.AcceptTxAudio(second, TciSampleType.Float32, 960, channels: 2, sampleRate: 24000);

        Assert.Single(forwarded);
        Assert.Equal(960 * 4, forwarded[0].Length);
        Assert.Equal(0, receiver.TotalFramesDropped);
        Assert.Equal(2, receiver.TotalFramesAccepted);
        Assert.Equal(960, receiver.TotalSamplesForwarded);
        Assert.True(DecodedRms(forwarded[0]) > 0.05);
    }

    [Fact]
    public void Stereo44k1_ResamplesFractionalRateAcrossCalls()
    {
        var forwarded = new List<byte[]>();
        Action<ReadOnlyMemory<byte>> capture = m => forwarded.Add(m.ToArray());
        var receiver = new TciTxAudioReceiver(capture, NullLogger.Instance);

        var first = EncodeStereoFloats(Enumerable.Range(0, 441).Select(i => i / 441f).ToArray());
        var second = EncodeStereoFloats(Enumerable.Range(0, 441).Select(i => (441 + i) / 882f).ToArray());
        var third = EncodeStereoFloats(Enumerable.Range(0, 441).Select(i => (882 + i) / 1323f).ToArray());

        receiver.AcceptTxAudio(first, TciSampleType.Float32, 882, channels: 2, sampleRate: 44100);
        Assert.Empty(forwarded);

        receiver.AcceptTxAudio(second, TciSampleType.Float32, 882, channels: 2, sampleRate: 44100);
        Assert.Empty(forwarded);

        receiver.AcceptTxAudio(third, TciSampleType.Float32, 882, channels: 2, sampleRate: 44100);
        Assert.Single(forwarded);
        Assert.Equal(960, receiver.TotalSamplesForwarded);
        Assert.Equal(3, receiver.TotalFramesAccepted);
    }

    [Fact]
    public void Stereo96k_DownsamplesTo48kMicBlock()
    {
        var forwarded = new List<byte[]>();
        Action<ReadOnlyMemory<byte>> capture = m => forwarded.Add(m.ToArray());
        var receiver = new TciTxAudioReceiver(capture, NullLogger.Instance);

        var payload = EncodeStereoFloats(Enumerable.Range(0, 3840)
            .Select(i => (float)Math.Sin(2.0 * Math.PI * 1_000.0 * i / 96_000.0))
            .ToArray());
        receiver.AcceptTxAudio(payload, TciSampleType.Float32, 7680, channels: 2, sampleRate: 96000);

        Assert.Single(forwarded);
        Assert.Equal(0, receiver.TotalFramesDropped);
        Assert.Equal(960, receiver.TotalSamplesForwarded);
        Assert.True(DecodedRms(forwarded[0]) > 0.2);
    }

    [Fact]
    public void NonFloat32SampleType_DropsFrame()
    {
        var forwarded = new List<byte[]>();
        Action<ReadOnlyMemory<byte>> capture = m => forwarded.Add(m.ToArray());
        var receiver = new TciTxAudioReceiver(capture, NullLogger.Instance);

        // 16-bit int payload would be (numFrames * 2) bytes; size is irrelevant
        // because the receiver should reject before decoding.
        receiver.AcceptTxAudio(new byte[3840], TciSampleType.Int16, 1920, channels: 2, sampleRate: 48000);

        Assert.Empty(forwarded);
        Assert.Equal(1, receiver.TotalFramesDropped);
    }

    [Fact]
    public void InvalidChannelCount_DropsFrame()
    {
        var forwarded = new List<byte[]>();
        Action<ReadOnlyMemory<byte>> capture = m => forwarded.Add(m.ToArray());
        var receiver = new TciTxAudioReceiver(capture, NullLogger.Instance);

        receiver.AcceptTxAudio(new byte[3840], TciSampleType.Float32, 960, channels: 6, sampleRate: 48000);

        Assert.Empty(forwarded);
    }

    [Fact]
    public void Reset_ClearsAccumulatorRemainder()
    {
        var forwarded = new List<byte[]>();
        Action<ReadOnlyMemory<byte>> capture = m => forwarded.Add(m.ToArray());
        var receiver = new TciTxAudioReceiver(capture, NullLogger.Instance);

        // Send 100 stereo frames — below the 960 mic-block threshold, so
        // nothing forwards yet. The 100 mono samples sit in the accumulator.
        var first = EncodeStereoFloats(Enumerable.Repeat(0.7f, 100).ToArray());
        receiver.AcceptTxAudio(first, TciSampleType.Float32, 200, channels: 2, sampleRate: 48000);
        Assert.Empty(forwarded);

        receiver.Reset();

        // Now send 960 fresh frames at value 0.0 — all 960 mic samples should
        // be 0.0. If reset hadn't cleared, the first 100 would be 0.7.
        var fresh = EncodeStereoFloats(new float[960]);
        receiver.AcceptTxAudio(fresh, TciSampleType.Float32, 1920, channels: 2, sampleRate: 48000);

        Assert.Single(forwarded);
        for (int i = 0; i < 960; i++)
        {
            float decoded = BinaryPrimitives.ReadSingleLittleEndian(forwarded[0].AsSpan(i * 4, 4));
            Assert.Equal(0.0f, decoded);
        }
    }

    [Fact]
    public void TruncatedDeclaredLength_TrustsActualPayload()
    {
        var forwarded = new List<byte[]>();
        Action<ReadOnlyMemory<byte>> capture = m => forwarded.Add(m.ToArray());
        var receiver = new TciTxAudioReceiver(capture, NullLogger.Instance);

        // Payload contains 960 stereo frames (3840 floats) but declared
        // length lies and says 100000. Receiver should still process only
        // the bytes actually present and not over-read.
        var payload = EncodeStereoFloats(Enumerable.Repeat(0.25f, 960).ToArray());
        receiver.AcceptTxAudio(payload, TciSampleType.Float32, 100000, channels: 2, sampleRate: 48000);

        Assert.Single(forwarded);
    }
}
