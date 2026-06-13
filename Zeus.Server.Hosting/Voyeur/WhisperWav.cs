// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see ATTRIBUTIONS.md for provenance.

using System.Buffers.Binary;

namespace Zeus.Server.Voyeur;

/// <summary>
/// Prepares a captured "over" WAV for <c>whisper-cli</c>, which HARD-REQUIRES
/// 16 kHz input: whisper.cpp v1.7.4 prints <c>"WAV file must be 16 kHz"</c> and
/// fails to read anything else — yet still exits 0 and writes no transcript, so
/// the failure is SILENT. Voyeur overs are written by <see cref="Wav.WavWriter"/>
/// at the radio's audio rate (typically 48 kHz, mono float32), so every over was
/// being rejected and left captured-only with nothing logged.
///
/// We down-convert to 16 kHz mono 16-bit PCM here, IN-PROCESS — no ffmpeg / sox
/// shell-out, because the Voyeur engine bundle ships only whisper + llama, and a
/// resampler dependency that may be absent on the operator's box would just trade
/// one silent failure for another. The original over file is left UNTOUCHED so
/// the operator's archived recording keeps its full-rate fidelity; only the
/// throw-away copy fed to whisper is down-converted.
/// </summary>
internal static class WhisperWav
{
    /// <summary>whisper.cpp's required input rate.</summary>
    public const int TargetRate = 16000;

    /// <summary>
    /// Returns a path to a WAV suitable for whisper-cli (16 kHz mono). If the
    /// source is already 16 kHz it is returned unchanged (<paramref name="createdTemp"/>
    /// = false); otherwise a temp 16-bit-PCM copy is written and returned
    /// (<paramref name="createdTemp"/> = true — the caller deletes it). Returns
    /// null if the source cannot be parsed as a PCM / IEEE-float WAV.
    /// </summary>
    public static string? Prepare(string srcPath, out bool createdTemp)
    {
        createdTemp = false;
        if (!TryReadMono(srcPath, out float[] samples, out int rate) || samples.Length == 0)
            return null;

        // Already at the target rate — whisper reads it directly, no copy needed.
        if (rate == TargetRate) return srcPath;

        float[] resampled = Resample(samples, rate, TargetRate);
        string tmp = Path.Combine(
            Path.GetTempPath(), "zeus-w16k-" + Guid.NewGuid().ToString("N") + ".wav");
        WritePcm16Mono(tmp, resampled);
        createdTemp = true;
        return tmp;
    }

    // --- WAV reading (PCM 16/24/32 + IEEE float, any channel count → mono) ----

    private static bool TryReadMono(string path, out float[] mono, out int sampleRate)
    {
        mono = Array.Empty<float>();
        sampleRate = 0;
        try
        {
            byte[] b = File.ReadAllBytes(path);
            if (b.Length < 12 ||
                b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F' ||
                b[8] != 'W' || b[9] != 'A' || b[10] != 'V' || b[11] != 'E')
                return false;

            int channels = 0, bits = 0, fmtTag = 0;
            int dataOff = -1, dataLen = 0;

            // Walk the RIFF chunks. Each: 4-byte id, 4-byte LE size, payload
            // (padded to an even byte count).
            int p = 12;
            while (p + 8 <= b.Length)
            {
                var id = b.AsSpan(p, 4);
                int size = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p + 4, 4));
                int body = p + 8;
                if (size < 0 || body + size > b.Length) size = b.Length - body;

                if (id.SequenceEqual("fmt "u8))
                {
                    fmtTag = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(body, 2));
                    channels = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(body + 2, 2));
                    sampleRate = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(body + 4, 4));
                    bits = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(body + 14, 2));
                    // WAVE_FORMAT_EXTENSIBLE: the real format lives in the GUID's
                    // first two bytes.
                    if (fmtTag == 0xFFFE && size >= 26)
                        fmtTag = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(body + 24, 2));
                }
                else if (id.SequenceEqual("data"u8))
                {
                    dataOff = body;
                    dataLen = size;
                }
                p = body + size + (size & 1); // skip payload + pad byte
            }

            if (dataOff < 0 || channels <= 0 || sampleRate <= 0) return false;

            int bytesPerSample = bits / 8;
            if (bytesPerSample <= 0) return false;
            int frameBytes = bytesPerSample * channels;
            int frames = dataLen / frameBytes;
            if (frames <= 0) return false;

            var outBuf = new float[frames];
            for (int f = 0; f < frames; f++)
            {
                float acc = 0f;
                int baseOff = dataOff + f * frameBytes;
                for (int c = 0; c < channels; c++)
                {
                    int o = baseOff + c * bytesPerSample;
                    acc += fmtTag == 3
                        ? ReadFloatSample(b, o, bits)
                        : ReadPcmSample(b, o, bits);
                }
                outBuf[f] = acc / channels; // downmix to mono
            }
            mono = outBuf;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static float ReadFloatSample(byte[] b, int o, int bits) =>
        bits == 64
            ? (float)BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(o, 8)))
            : BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(o, 4));

    private static float ReadPcmSample(byte[] b, int o, int bits)
    {
        switch (bits)
        {
            case 16:
                return BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(o, 2)) / 32768f;
            case 24:
                int v24 = b[o] | (b[o + 1] << 8) | (b[o + 2] << 16);
                if ((v24 & 0x800000) != 0) v24 |= unchecked((int)0xFF000000); // sign-extend
                return v24 / 8388608f;
            case 32:
                return BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(o, 4)) / 2147483648f;
            case 8:
                return (b[o] - 128) / 128f; // 8-bit PCM is unsigned
            default:
                return 0f;
        }
    }

    // --- Resampling -----------------------------------------------------------

    // Down-convert to the target rate. When decimating we first apply a simple
    // box-filter low-pass (width ≈ the decimation factor) to knock down aliasing,
    // then resample by linear interpolation. This is not studio-grade SRC, but it
    // is more than adequate for 3 kHz-bandwidth SSB voice feeding an ASR model,
    // and it is dependency-free.
    private static float[] Resample(float[] x, int srcRate, int dstRate)
    {
        if (srcRate == dstRate) return x;

        float[] s = x;
        if (srcRate > dstRate)
        {
            int w = Math.Max(1, (int)Math.Round((double)srcRate / dstRate));
            if (w > 1) s = BoxFilter(x, w);
        }

        double ratio = (double)srcRate / dstRate;
        int outLen = Math.Max(1, (int)(x.Length / ratio));
        var y = new float[outLen];
        for (int i = 0; i < outLen; i++)
        {
            double pos = i * ratio;
            int i0 = (int)pos;
            int i1 = Math.Min(i0 + 1, s.Length - 1);
            float frac = (float)(pos - i0);
            y[i] = s[i0] + (s[i1] - s[i0]) * frac;
        }
        return y;
    }

    private static float[] BoxFilter(float[] x, int w)
    {
        var y = new float[x.Length];
        int half = w / 2;
        float inv = 1f / w;
        float running = 0f;
        // Prime the window.
        for (int i = 0; i < Math.Min(w, x.Length); i++) running += x[i];
        for (int i = 0; i < x.Length; i++)
        {
            int center = i + half;
            int add = center + 1, drop = center - w + 1;
            if (add > 0 && add < x.Length) running += x[add];
            if (drop > 0 && drop - 1 < x.Length && drop - 1 >= 0) running -= x[drop - 1];
            y[i] = running * inv;
        }
        return y;
    }

    // --- WAV writing (16 kHz mono 16-bit PCM) ---------------------------------

    private static void WritePcm16Mono(string path, float[] samples)
    {
        const int channels = 1, bits = 16, rate = TargetRate;
        int byteRate = rate * channels * (bits / 8);
        ushort blockAlign = channels * (bits / 8);
        int dataBytes = samples.Length * 2;

        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write("RIFF"u8);
        bw.Write((uint)(36 + dataBytes));
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16u);
        bw.Write((ushort)1);          // PCM
        bw.Write((ushort)channels);
        bw.Write((uint)rate);
        bw.Write((uint)byteRate);
        bw.Write(blockAlign);
        bw.Write((ushort)bits);
        bw.Write("data"u8);
        bw.Write((uint)dataBytes);

        Span<byte> buf = stackalloc byte[2];
        foreach (float f in samples)
        {
            float c = f < -1f ? -1f : (f > 1f ? 1f : f);
            BinaryPrimitives.WriteInt16LittleEndian(buf, (short)(c * 32767f));
            bw.Write(buf);
        }
    }
}
