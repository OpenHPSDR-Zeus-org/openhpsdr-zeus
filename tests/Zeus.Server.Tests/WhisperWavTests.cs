// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.

using System.Buffers.Binary;
using Xunit;
using Zeus.Server.Voyeur;

namespace Zeus.Server.Tests;

/// <summary>
/// Guards the regression that broke Voyeur transcription: whisper-cli rejects
/// any input that isn't 16 kHz, so over WAVs (written at 48 kHz mono float32)
/// must be down-converted before they reach the binary. These tests assert the
/// converter emits a real 16 kHz mono 16-bit PCM WAV and preserves enough signal
/// fidelity for ASR.
/// </summary>
public class WhisperWavTests
{
    // Minimal copy of WhisperWav's own writer style — produces the exact format
    // a Voyeur over uses: mono float32 (IEEE) at the given rate.
    private static string WriteFloatWav(int rate, float[] samples)
    {
        string path = Path.Combine(Path.GetTempPath(), "zeus-wwtest-" + Guid.NewGuid().ToString("N") + ".wav");
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        int dataBytes = samples.Length * 4;
        bw.Write("RIFF"u8); bw.Write((uint)(36 + dataBytes)); bw.Write("WAVE"u8);
        bw.Write("fmt "u8); bw.Write(16u);
        bw.Write((ushort)3);   // IEEE float
        bw.Write((ushort)1);   // mono
        bw.Write((uint)rate);
        bw.Write((uint)(rate * 4));
        bw.Write((ushort)4); bw.Write((ushort)32);
        bw.Write("data"u8); bw.Write((uint)dataBytes);
        Span<byte> b = stackalloc byte[4];
        foreach (var s in samples) { BinaryPrimitives.WriteSingleLittleEndian(b, s); bw.Write(b); }
        return path;
    }

    private static (int rate, int channels, int bits, int frames) ReadHeader(string path)
    {
        byte[] b = File.ReadAllBytes(path);
        Assert.True(b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F');
        int channels = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(22, 2));
        int rate = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(24, 4));
        int bits = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(34, 2));
        int dataLen = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(40, 4));
        int fmt = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(20, 2));
        Assert.Equal(1, fmt); // PCM
        return (rate, channels, bits, dataLen / (bits / 8 * channels));
    }

    [Fact]
    public void Resamples48kFloatOverTo16kMonoPcm()
    {
        // 1 second of 48 kHz mono float — a 1 kHz tone (well inside the 8 kHz
        // Nyquist after down-conversion, so it must survive).
        int srcRate = 48000;
        var samples = new float[srcRate];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = 0.5f * MathF.Sin(2f * MathF.PI * 1000f * i / srcRate);
        string src = WriteFloatWav(srcRate, samples);

        try
        {
            string? outPath = WhisperWav.Prepare(src, out bool createdTemp);
            Assert.NotNull(outPath);
            Assert.True(createdTemp);
            Assert.NotEqual(src, outPath);

            try
            {
                var (rate, channels, bits, frames) = ReadHeader(outPath!);
                Assert.Equal(16000, rate);     // the whole point
                Assert.Equal(1, channels);     // mono
                Assert.Equal(16, bits);        // 16-bit PCM
                // ~1 s of audio at 16 kHz, within a few samples of the ratio.
                Assert.InRange(frames, 15900, 16100);

                // The 1 kHz tone must still carry energy (not silenced by the
                // anti-alias filter). Peak amplitude should be a meaningful
                // fraction of full scale.
                byte[] b = File.ReadAllBytes(outPath!);
                short peak = 0;
                for (int o = 44; o + 1 < b.Length; o += 2)
                {
                    short s = BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(o, 2));
                    if (Math.Abs((int)s) > Math.Abs((int)peak)) peak = s;
                }
                Assert.True(Math.Abs((int)peak) > 8000, $"tone too attenuated, peak={peak}");
            }
            finally { File.Delete(outPath!); }
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void PassesThrough16kInputUnchanged()
    {
        string src = WriteFloatWav(16000, new float[16000]);
        try
        {
            string? outPath = WhisperWav.Prepare(src, out bool createdTemp);
            Assert.Equal(src, outPath);   // no copy when already at target rate
            Assert.False(createdTemp);
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void ReturnsNullForGarbageInput()
    {
        string src = Path.Combine(Path.GetTempPath(), "zeus-wwtest-" + Guid.NewGuid().ToString("N") + ".wav");
        File.WriteAllText(src, "not a wav file");
        try
        {
            string? outPath = WhisperWav.Prepare(src, out bool createdTemp);
            Assert.Null(outPath);
            Assert.False(createdTemp);
        }
        finally { File.Delete(src); }
    }
}
