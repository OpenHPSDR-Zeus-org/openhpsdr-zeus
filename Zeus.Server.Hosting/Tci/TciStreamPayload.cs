// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Distributed under the GNU General Public License v2 or later. See the
// LICENSE file at the root of this repository for full text.

using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Zeus.Server.Tci;

/// <summary>
/// TCI binary stream type tag — matches Thetis TCIServer.TCIStreamType
/// (Project Files/Source/Console/TCIServer.cs:341) so on-the-wire bytes
/// are identical for ExpertSDR3-compatible clients.
/// </summary>
internal enum TciStreamType : uint
{
    IqStream = 0,
    RxAudioStream = 1,
    TxAudioStream = 2,
    TxChrono = 3,
    LineOutStream = 4,
}

/// <summary>
/// TCI sample-encoding tag — matches Thetis TCIServer.TCISampleType.
/// </summary>
internal enum TciSampleType : uint
{
    Int16 = 0,
    Int24 = 1,
    Int32 = 2,
    Float32 = 3,
}

/// <summary>
/// Parsed view of a 64-byte TCI binary stream header. <see cref="StreamType"/>
/// at offset 24 is the dispatch key — per markdown spec §8 quirk #7, a server
/// keys binary frames off offset 24, not offset 8 (some clients write a
/// non-canonical value at offset 8).
/// </summary>
internal readonly record struct TciStreamHeader(
    uint Receiver,
    uint SampleRate,
    TciSampleType SampleType,
    uint Length,
    TciStreamType StreamType);

/// <summary>
/// Builds the 64-byte fixed TCI binary stream header used for IQ, RX audio,
/// TX audio, and TX_CHRONO frames. Layout matches the SunSDR / ExpertSDR3
/// TCI v1.x specification (TCI_Protocol_Spec §7.1).
///
/// Layout (all little-endian uint32):
///   [ 0]: receiver index
///   [ 4]: sample rate (Hz)
///   [ 8]: format / sample type — see <see cref="TciSampleType"/>
///   [12]: codec id (0 = uncompressed PCM)
///   [16]: crc32 of payload, or 0 to skip
///   [20]: length — count of float values in payload (total floats, not pairs)
///   [24]: stream type — see <see cref="TciStreamType"/>
///   [28..63]: reserved zeros (9 × uint32)
///   [64..]: sample payload bytes
/// </summary>
internal static class TciStreamPayload
{
    public const int HeaderSize = 64;

    public static byte[] Build(
        int receiver,
        int sampleRate,
        TciSampleType sampleType,
        int length,
        TciStreamType streamType,
        ReadOnlySpan<byte> samplePayload)
    {
        var payload = new byte[HeaderSize + samplePayload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0), (uint)receiver);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), (uint)sampleType);
        // [12] codec, [16] crc, [28..63] reserved — all zero (cleared by `new byte[]`)
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(20), (uint)length);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(24), (uint)streamType);
        if (samplePayload.Length > 0)
            samplePayload.CopyTo(payload.AsSpan(HeaderSize));
        return payload;
    }

    /// <summary>
    /// Parse a 64-byte TCI binary stream header. Returns false if the input is
    /// shorter than 64 bytes or carries an out-of-range stream-type / sample-type.
    /// Length is the total scalar count (not pair count for IQ, not frame count
    /// for stereo audio) per markdown spec §7.5.
    /// </summary>
    public static bool TryParseHeader(ReadOnlySpan<byte> frame, out TciStreamHeader header)
    {
        header = default;
        if (frame.Length < HeaderSize) return false;
        uint streamType = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(24));
        if (streamType > (uint)TciStreamType.LineOutStream) return false;
        uint sampleType = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(8));
        if (sampleType > (uint)TciSampleType.Float32) return false;
        header = new TciStreamHeader(
            Receiver: BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(0)),
            SampleRate: BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(4)),
            SampleType: (TciSampleType)sampleType,
            Length: BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(20)),
            StreamType: (TciStreamType)streamType);
        return true;
    }

    /// <summary>
    /// Build a TX_CHRONO sync frame (StreamType=3). Carries no audio payload —
    /// the client uses this as a "send another TX audio block now" signal per
    /// spec §3.4. Sent periodically by the server while MOX is on AND the
    /// session has TRX source = TCI.
    /// </summary>
    public static byte[] BuildTxChrono(int receiver, int sampleRate)
    {
        return Build(
            receiver,
            sampleRate,
            TciSampleType.Float32,
            length: 4096,
            streamType: TciStreamType.TxChrono,
            samplePayload: ReadOnlySpan<byte>.Empty);
    }

    public static byte[] BuildIqFromDoubles(int receiver, int sampleRate, ReadOnlySpan<double> interleavedIQ)
    {
        var floats = new float[interleavedIQ.Length];
        for (int i = 0; i < interleavedIQ.Length; i++)
            floats[i] = SanitizeIqSample(interleavedIQ[i]);
        return Build(
            receiver,
            sampleRate,
            TciSampleType.Float32,
            length: floats.Length,
            streamType: TciStreamType.IqStream,
            samplePayload: MemoryMarshal.AsBytes(floats.AsSpan()));
    }

    /// <summary>
    /// Builds an RX audio frame from mono FLOAT32 samples using the negotiated
    /// audio_stream_channels count. Stereo duplicates each sample to L=R for
    /// compatibility with the handshake default; mono preserves the scalar
    /// count requested by clients that opt into audio_stream_channels=1.
    /// </summary>
    public static byte[] BuildAudioFromFloats(int receiver, int sampleRate, ReadOnlySpan<float> samples, int channels = 2)
    {
        if (channels != 1 && channels != 2)
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "TCI RX audio supports one or two Float32 channels.");

        if (channels == 1)
        {
            if (!IsCleanAudio(samples))
            {
                var mono = new float[samples.Length];
                CopySanitizedAudio(samples, mono);
                return Build(
                    receiver,
                    sampleRate,
                    TciSampleType.Float32,
                    length: mono.Length,
                    streamType: TciStreamType.RxAudioStream,
                    samplePayload: MemoryMarshal.AsBytes(mono.AsSpan()));
            }

            return Build(
                receiver,
                sampleRate,
                TciSampleType.Float32,
                length: samples.Length,
                streamType: TciStreamType.RxAudioStream,
                samplePayload: MemoryMarshal.AsBytes(samples));
        }

        var stereo = new float[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            float sample = DspPipelineService.SanitizeAudioSample(samples[i]);
            stereo[i * 2] = sample;
            stereo[i * 2 + 1] = sample;
        }
        return Build(
            receiver,
            sampleRate,
            TciSampleType.Float32,
            length: stereo.Length,
            streamType: TciStreamType.RxAudioStream,
            samplePayload: MemoryMarshal.AsBytes(stereo.AsSpan()));
    }

    private static float SanitizeIqSample(double sample)
    {
        if (!double.IsFinite(sample)) return 0f;
        float value = (float)sample;
        return float.IsFinite(value) ? value : 0f;
    }

    private static bool IsCleanAudio(ReadOnlySpan<float> samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            float sample = samples[i];
            if (!float.IsFinite(sample) || sample < -1f || sample > 1f)
                return false;
        }
        return true;
    }

    private static void CopySanitizedAudio(ReadOnlySpan<float> source, Span<float> destination)
    {
        for (int i = 0; i < source.Length; i++)
            destination[i] = DspPipelineService.SanitizeAudioSample(source[i]);
    }
}
