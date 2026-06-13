// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

using System.Buffers.Binary;

namespace Zeus.Server.Wav;

/// <summary>
/// Minimal WAV read/write for the recorder/player feature. Canonical format is
/// 32-bit IEEE float, mono, at whatever sample rate the audio path runs (48 kHz
/// for RX/TX-monitor audio). Float32 is chosen so a recording of the operator's
/// processed TX audio plays back bit-identical with no requantisation — the
/// "what you record is what goes out" requirement.
///
/// We write a standard RIFF/WAVE container: <c>RIFF</c> + <c>WAVE</c>, a 16-byte
/// <c>fmt </c> chunk tagged <c>WAVE_FORMAT_IEEE_FLOAT</c> (3), and a <c>data</c>
/// chunk. The reader tolerates extra chunks (skips anything that isn't
/// <c>fmt </c>/<c>data</c>) and both float32 and 16-bit PCM input so externally
/// produced clips still load.
/// </summary>
public static class WavFile
{
    private const ushort FormatPcm = 1;
    private const ushort FormatIeeeFloat = 3;

    /// <summary>Read an entire WAV file into mono float32 samples. Stereo input
    /// is downmixed (channel average); 16-bit PCM is scaled to ±1.0. Returns the
    /// samples and the file's sample rate.</summary>
    public static (float[] Samples, int SampleRate) ReadAllSamples(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);

        Span<byte> tag = stackalloc byte[4];
        ReadExactly(br, tag);
        if (!tag.SequenceEqual("RIFF"u8)) throw new InvalidDataException("not a RIFF file");
        br.ReadUInt32(); // riff size — ignored
        ReadExactly(br, tag);
        if (!tag.SequenceEqual("WAVE"u8)) throw new InvalidDataException("not a WAVE file");

        ushort format = 0, channels = 0, bitsPerSample = 0;
        int sampleRate = 0;
        byte[]? data = null;

        while (fs.Position + 8 <= fs.Length)
        {
            ReadExactly(br, tag);
            uint chunkSize = br.ReadUInt32();
            if (tag.SequenceEqual("fmt "u8))
            {
                format = br.ReadUInt16();
                channels = br.ReadUInt16();
                sampleRate = (int)br.ReadUInt32();
                br.ReadUInt32(); // byte rate
                br.ReadUInt16(); // block align
                bitsPerSample = br.ReadUInt16();
                // Skip any extension bytes (e.g. WAVE_FORMAT_EXTENSIBLE / fact).
                int consumed = 16;
                if (chunkSize > consumed) fs.Seek(chunkSize - consumed, SeekOrigin.Current);
            }
            else if (tag.SequenceEqual("data"u8))
            {
                data = br.ReadBytes((int)chunkSize);
            }
            else
            {
                fs.Seek(chunkSize, SeekOrigin.Current);
            }
            if ((chunkSize & 1) == 1) fs.Seek(1, SeekOrigin.Current); // RIFF word-align
        }

        if (data is null || channels == 0 || sampleRate == 0)
            throw new InvalidDataException("WAV missing fmt/data");

        float[] mono = format switch
        {
            FormatIeeeFloat when bitsPerSample == 32 => DecodeFloat32(data, channels),
            FormatPcm when bitsPerSample == 16 => DecodePcm16(data, channels),
            _ => throw new InvalidDataException(
                $"unsupported WAV format tag={format} bits={bitsPerSample}")
        };
        return (mono, sampleRate);
    }

    private static float[] DecodeFloat32(byte[] data, int channels)
    {
        int totalSamples = data.Length / 4;
        int frames = totalSamples / channels;
        var mono = new float[frames];
        var span = data.AsSpan();
        for (int f = 0; f < frames; f++)
        {
            float acc = 0f;
            for (int c = 0; c < channels; c++)
                acc += BinaryPrimitives.ReadSingleLittleEndian(span.Slice((f * channels + c) * 4, 4));
            mono[f] = acc / channels;
        }
        return mono;
    }

    private static float[] DecodePcm16(byte[] data, int channels)
    {
        int totalSamples = data.Length / 2;
        int frames = totalSamples / channels;
        var mono = new float[frames];
        var span = data.AsSpan();
        for (int f = 0; f < frames; f++)
        {
            int acc = 0;
            for (int c = 0; c < channels; c++)
                acc += BinaryPrimitives.ReadInt16LittleEndian(span.Slice((f * channels + c) * 2, 2));
            mono[f] = acc / (channels * 32768f);
        }
        return mono;
    }

    private static void ReadExactly(BinaryReader br, Span<byte> buf)
    {
        int read = br.Read(buf);
        if (read != buf.Length) throw new EndOfStreamException();
    }
}
