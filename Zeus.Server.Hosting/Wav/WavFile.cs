// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.

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

    /// <summary>Read just the format and length of a WAV without loading the
    /// data chunk into memory. Parses the <c>fmt </c> chunk and the <c>data</c>
    /// chunk's declared size, then returns the sample rate and the mono frame
    /// count (<c>dataBytes / bytesPerSample / channels</c>) — the same length
    /// <see cref="ReadAllSamples"/> would return. Cheap enough to call for every
    /// file in a directory listing. Throws <see cref="InvalidDataException"/> on
    /// a malformed or unsupported header.</summary>
    public static (int SampleRate, long SampleCount) ReadInfo(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);

        Span<byte> tag = stackalloc byte[4];
        ReadExactly(br, tag);
        if (!tag.SequenceEqual("RIFF"u8)) throw new InvalidDataException("not a RIFF file");
        br.ReadUInt32(); // riff size — ignored
        ReadExactly(br, tag);
        if (!tag.SequenceEqual("WAVE"u8)) throw new InvalidDataException("not a WAVE file");

        ushort channels = 0, bitsPerSample = 0;
        int sampleRate = 0;
        long dataBytes = -1;

        while (fs.Position + 8 <= fs.Length)
        {
            ReadExactly(br, tag);
            uint chunkSize = br.ReadUInt32();
            if (tag.SequenceEqual("fmt "u8))
            {
                br.ReadUInt16(); // format tag
                channels = br.ReadUInt16();
                sampleRate = (int)br.ReadUInt32();
                br.ReadUInt32(); // byte rate
                br.ReadUInt16(); // block align
                bitsPerSample = br.ReadUInt16();
                int consumed = 16;
                if (chunkSize > consumed) fs.Seek(chunkSize - consumed, SeekOrigin.Current);
            }
            else if (tag.SequenceEqual("data"u8))
            {
                // Record the declared size; do NOT read the payload.
                dataBytes = chunkSize;
                fs.Seek(chunkSize, SeekOrigin.Current);
            }
            else
            {
                fs.Seek(chunkSize, SeekOrigin.Current);
            }
            if ((chunkSize & 1) == 1) fs.Seek(1, SeekOrigin.Current); // RIFF word-align
        }

        if (dataBytes < 0 || channels == 0 || sampleRate == 0 || bitsPerSample == 0)
            throw new InvalidDataException("WAV missing fmt/data");

        long bytesPerSample = bitsPerSample / 8;
        long frames = dataBytes / Math.Max(1, bytesPerSample * channels);
        return (sampleRate, frames);
    }

    /// <summary>Downsample mono samples into a peak envelope of
    /// <paramref name="buckets"/> values, each the maximum |sample| over its
    /// time slice (0..1). Used to draw the waveform overview for a clip. When
    /// there are fewer samples than buckets, returns one |sample| per sample.</summary>
    public static float[] Envelope(ReadOnlySpan<float> samples, int buckets)
    {
        buckets = Math.Clamp(buckets, 1, 4096);
        if (samples.Length == 0) return new float[buckets];
        if (samples.Length <= buckets)
        {
            var one = new float[samples.Length];
            for (int i = 0; i < samples.Length; i++) one[i] = MathF.Abs(samples[i]);
            return one;
        }
        var env = new float[buckets];
        for (int b = 0; b < buckets; b++)
        {
            int start = (int)((long)b * samples.Length / buckets);
            int end = (int)((long)(b + 1) * samples.Length / buckets);
            if (end <= start) end = start + 1;
            float peak = 0f;
            for (int i = start; i < end && i < samples.Length; i++)
            {
                float a = MathF.Abs(samples[i]);
                if (a > peak) peak = a;
            }
            env[b] = peak;
        }
        return env;
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
                acc += DspPipelineService.SanitizeAudioSample(
                    BinaryPrimitives.ReadSingleLittleEndian(span.Slice((f * channels + c) * 4, 4)));
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
