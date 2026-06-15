// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Distributed under the GNU General Public License v2 or later. See the
// LICENSE file at the root of this repository for full text.

using System.Buffers.Binary;
using Zeus.Server.Tci;

namespace Zeus.Server.Tests.Tci;

/// <summary>
/// Wire-compatibility tests for the 64-byte TCI binary stream header.
/// Layout matches SunSDR / ExpertSDR3 TCI v1.x spec §7.1 — offset 28..63
/// is reserved zero; channel count is implicit by stream type.
/// </summary>
public class TciStreamPayloadTests
{
    [Fact]
    public void Build_HeaderSize_IsSixtyFourBytes()
    {
        var frame = TciStreamPayload.Build(
            receiver: 0, sampleRate: 48000, sampleType: TciSampleType.Float32,
            length: 0, streamType: TciStreamType.IqStream,
            samplePayload: ReadOnlySpan<byte>.Empty);

        Assert.Equal(TciStreamPayload.HeaderSize, frame.Length);
        Assert.Equal(64, frame.Length);
    }

    [Fact]
    public void Build_HeaderFields_LittleEndianAtCorrectOffsets()
    {
        var frame = TciStreamPayload.Build(
            receiver: 1, sampleRate: 192_000, sampleType: TciSampleType.Float32,
            length: 1024, streamType: TciStreamType.IqStream,
            samplePayload: ReadOnlySpan<byte>.Empty);

        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(0)));
        Assert.Equal(192_000u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(4)));
        Assert.Equal((uint)TciSampleType.Float32, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(8)));
        Assert.Equal(1024u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(20)));
        Assert.Equal((uint)TciStreamType.IqStream, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(24)));
    }

    [Fact]
    public void Build_ReservedFields_AreZero()
    {
        var frame = TciStreamPayload.Build(
            receiver: 0, sampleRate: 48000, sampleType: TciSampleType.Float32,
            length: 0, streamType: TciStreamType.IqStream,
            samplePayload: ReadOnlySpan<byte>.Empty);

        // Codec at [12], crc at [16], and reserved[9] at [28..63] are all zero
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(12)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(16)));
        for (int offset = 28; offset < 64; offset += 4)
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(offset)));
    }

    [Fact]
    public void Build_AppendsPayloadAfterHeader()
    {
        ReadOnlySpan<byte> payload = stackalloc byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var frame = TciStreamPayload.Build(
            receiver: 0, sampleRate: 48000, sampleType: TciSampleType.Float32,
            length: 4, streamType: TciStreamType.IqStream,
            samplePayload: payload);

        Assert.Equal(64 + 4, frame.Length);
        Assert.Equal(0xDE, frame[64]);
        Assert.Equal(0xAD, frame[65]);
        Assert.Equal(0xBE, frame[66]);
        Assert.Equal(0xEF, frame[67]);
    }

    [Fact]
    public void BuildIqFromDoubles_HeaderTypedAsFloat32Iq()
    {
        ReadOnlySpan<double> samples = stackalloc double[] { 1.0, -1.0, 0.5, -0.5 };
        var frame = TciStreamPayload.BuildIqFromDoubles(0, 48000, samples);

        Assert.Equal((uint)TciSampleType.Float32, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(8)));
        Assert.Equal((uint)TciStreamType.IqStream, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(24)));
        Assert.Equal((uint)samples.Length, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(20)));
        // Offset 28 is reserved — must be zero per spec
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(28)));
    }

    [Fact]
    public void BuildIqFromDoubles_DowncastsSamplesToFloat32LittleEndian()
    {
        ReadOnlySpan<double> samples = stackalloc double[] { 1.0, -1.0, 0.5, -0.5 };
        var frame = TciStreamPayload.BuildIqFromDoubles(0, 48000, samples);

        Assert.Equal(64 + samples.Length * 4, frame.Length);
        for (int i = 0; i < samples.Length; i++)
        {
            float expected = (float)samples[i];
            float actual = BitConverter.ToSingle(frame, 64 + i * 4);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void BuildIqFromDoubles_ZerosNonFiniteOrFloatOverflowSamples()
    {
        ReadOnlySpan<double> samples = stackalloc double[]
        {
            1.0,
            double.NaN,
            double.PositiveInfinity,
            double.NegativeInfinity,
            (double)float.MaxValue * 2.0,
            -0.25,
        };
        var frame = TciStreamPayload.BuildIqFromDoubles(0, 48000, samples);

        Assert.Equal(1.0f, PayloadFloat(frame, 0));
        Assert.Equal(0.0f, PayloadFloat(frame, 1));
        Assert.Equal(0.0f, PayloadFloat(frame, 2));
        Assert.Equal(0.0f, PayloadFloat(frame, 3));
        Assert.Equal(0.0f, PayloadFloat(frame, 4));
        Assert.Equal(-0.25f, PayloadFloat(frame, 5));
    }

    [Fact]
    public void BuildAudioFromFloats_HeaderTypedAsFloat32RxAudio()
    {
        ReadOnlySpan<float> samples = stackalloc float[] { 0.1f, -0.2f, 0.3f, -0.4f };
        var frame = TciStreamPayload.BuildAudioFromFloats(0, 48000, samples);

        Assert.Equal((uint)TciSampleType.Float32, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(8)));
        Assert.Equal((uint)TciStreamType.RxAudioStream, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(24)));
        // length is the *total float count* on the wire — stereo doubles it
        Assert.Equal((uint)(samples.Length * 2), BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(20)));
        // Offset 28 is reserved — must be zero per spec
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(28)));
    }

    [Fact]
    public void BuildAudioFromFloats_DuplicatesMonoToStereoLR()
    {
        ReadOnlySpan<float> mono = stackalloc float[] { 0.1f, -0.2f, 0.3f };
        var frame = TciStreamPayload.BuildAudioFromFloats(0, 48000, mono);

        // Payload is mono.Length * 2 floats, interleaved L,R,L,R,…
        // Each L and R for a given source sample equals that source sample.
        Assert.Equal(64 + mono.Length * 2 * 4, frame.Length);
        for (int i = 0; i < mono.Length; i++)
        {
            float left = BitConverter.ToSingle(frame, 64 + (i * 2) * 4);
            float right = BitConverter.ToSingle(frame, 64 + (i * 2 + 1) * 4);
            Assert.Equal(mono[i], left);
            Assert.Equal(mono[i], right);
        }
    }

    [Fact]
    public void BuildAudioFromFloats_ClipsAndZerosStereoPayload()
    {
        ReadOnlySpan<float> mono = stackalloc float[]
        {
            1.25f,
            -1.5f,
            float.NaN,
            float.PositiveInfinity,
            0.5f,
        };
        var frame = TciStreamPayload.BuildAudioFromFloats(0, 48000, mono);
        ReadOnlySpan<float> expected = stackalloc float[] { 1.0f, -1.0f, 0.0f, 0.0f, 0.5f };

        Assert.Equal(64 + mono.Length * 2 * 4, frame.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], PayloadFloat(frame, i * 2));
            Assert.Equal(expected[i], PayloadFloat(frame, i * 2 + 1));
        }
    }

    [Fact]
    public void BuildAudioFromFloats_PreservesMonoWhenOneChannelRequested()
    {
        ReadOnlySpan<float> mono = stackalloc float[] { 0.1f, -0.2f, 0.3f };
        var frame = TciStreamPayload.BuildAudioFromFloats(0, 24000, mono, channels: 1);

        Assert.Equal(64 + mono.Length * 4, frame.Length);
        Assert.Equal((uint)mono.Length, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(20)));
        Assert.Equal(24_000u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(4)));
        Assert.Equal((uint)TciStreamType.RxAudioStream, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(24)));

        for (int i = 0; i < mono.Length; i++)
            Assert.Equal(mono[i], BitConverter.ToSingle(frame, 64 + i * 4));
    }

    [Fact]
    public void BuildAudioFromFloats_ClipsAndZerosMonoPayload()
    {
        ReadOnlySpan<float> mono = stackalloc float[]
        {
            1.25f,
            -1.5f,
            float.NaN,
            float.NegativeInfinity,
            0.25f,
        };
        var frame = TciStreamPayload.BuildAudioFromFloats(0, 24000, mono, channels: 1);
        ReadOnlySpan<float> expected = stackalloc float[] { 1.0f, -1.0f, 0.0f, 0.0f, 0.25f };

        Assert.Equal(64 + mono.Length * 4, frame.Length);
        Assert.Equal((uint)mono.Length, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(20)));
        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], PayloadFloat(frame, i));
    }

    [Fact]
    public void BuildAudioFromFloats_RejectsUnsupportedChannelCount()
    {
        var mono = new[] { 0.1f };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TciStreamPayload.BuildAudioFromFloats(0, 48000, mono, channels: 3));
    }

    [Fact]
    public void BuildAudioFromFloats_SampleRateWrittenToHeader()
    {
        ReadOnlySpan<float> samples = stackalloc float[] { 0.0f };
        var frame = TciStreamPayload.BuildAudioFromFloats(0, 48000, samples);

        Assert.Equal(48000u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(4)));
    }

    [Fact]
    public void BuildTxChrono_HeaderTypedAsTxChrono()
    {
        var frame = TciStreamPayload.BuildTxChrono(receiver: 0, sampleRate: 48000);

        Assert.Equal(64, frame.Length); // header only — no payload
        Assert.Equal((uint)TciStreamType.TxChrono, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(24)));
        Assert.Equal(4096u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(20))); // 2048 samples × 2 channels
    }

    [Fact]
    public void TryParseHeader_RoundTripsBuildHeader()
    {
        var built = TciStreamPayload.Build(
            receiver: 7, sampleRate: 192_000, sampleType: TciSampleType.Float32,
            length: 1024, streamType: TciStreamType.TxAudioStream,
            samplePayload: ReadOnlySpan<byte>.Empty);

        Assert.True(TciStreamPayload.TryParseHeader(built, out var hdr));
        Assert.Equal(7u, hdr.Receiver);
        Assert.Equal(192_000u, hdr.SampleRate);
        Assert.Equal(TciSampleType.Float32, hdr.SampleType);
        Assert.Equal(1024u, hdr.Length);
        Assert.Equal(TciStreamType.TxAudioStream, hdr.StreamType);
    }

    [Fact]
    public void TryParseHeader_RejectsShortFrames()
    {
        Assert.False(TciStreamPayload.TryParseHeader(new byte[0], out _));
        Assert.False(TciStreamPayload.TryParseHeader(new byte[63], out _));
    }

    [Fact]
    public void TryParseHeader_RejectsOutOfRangeStreamType()
    {
        var bad = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(24), 99); // type=99 (invalid)
        Assert.False(TciStreamPayload.TryParseHeader(bad, out _));
    }

    [Fact]
    public void TryParseHeader_AcceptsTxChronoFrame()
    {
        var chrono = TciStreamPayload.BuildTxChrono(receiver: 0, sampleRate: 48000);

        Assert.True(TciStreamPayload.TryParseHeader(chrono, out var hdr));
        Assert.Equal(TciStreamType.TxChrono, hdr.StreamType);
        Assert.Equal(4096u, hdr.Length);
    }

    private static float PayloadFloat(byte[] frame, int sampleIndex) =>
        BitConverter.ToSingle(frame, 64 + sampleIndex * 4);
}
