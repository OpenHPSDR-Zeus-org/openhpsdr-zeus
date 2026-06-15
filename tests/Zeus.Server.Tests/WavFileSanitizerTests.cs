// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using Zeus.Server.Wav;

namespace Zeus.Server.Tests;

public sealed class WavFileSanitizerTests
{
    [Fact]
    public void WavWriter_ClampsOverrangeAndZerosNonFiniteSamples()
    {
        string path = Path.Combine(Path.GetTempPath(), $"zeus-wav-writer-sanitize-{Guid.NewGuid():N}.wav");
        try
        {
            using (var writer = new WavWriter(path, 48_000))
            {
                writer.Append(new[]
                {
                    float.NaN,
                    float.PositiveInfinity,
                    float.NegativeInfinity,
                    1.5f,
                    -2f,
                    0.25f,
                });
            }

            var (samples, rate) = WavFile.ReadAllSamples(path);

            Assert.Equal(48_000, rate);
            Assert.Equal(new[] { 0f, 0f, 0f, 1f, -1f, 0.25f }, samples);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadAllSamples_SanitizesFloat32BeforeStereoDownmix()
    {
        string path = Path.Combine(Path.GetTempPath(), $"zeus-wav-read-sanitize-{Guid.NewGuid():N}.wav");
        try
        {
            WriteFloat32Wav(path, sampleRate: 48_000, channels: 2, new[]
            {
                float.NaN, 0.5f,
                float.PositiveInfinity, 1.5f,
                float.NegativeInfinity, -2f,
            });

            var (samples, rate) = WavFile.ReadAllSamples(path);

            Assert.Equal(48_000, rate);
            Assert.Equal(3, samples.Length);
            Assert.Equal(0.25f, samples[0]);
            Assert.Equal(0.5f, samples[1]);
            Assert.Equal(-0.5f, samples[2]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void WriteFloat32Wav(string path, int sampleRate, ushort channels, float[] samples)
    {
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        uint dataBytes = (uint)(samples.Length * sizeof(float));
        ushort bitsPerSample = 32;
        ushort blockAlign = (ushort)(channels * (bitsPerSample / 8));
        uint byteRate = (uint)(sampleRate * blockAlign);

        bw.Write("RIFF"u8);
        bw.Write((uint)(36 + dataBytes));
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16u);
        bw.Write((ushort)3);
        bw.Write(channels);
        bw.Write((uint)sampleRate);
        bw.Write(byteRate);
        bw.Write(blockAlign);
        bw.Write(bitsPerSample);
        bw.Write("data"u8);
        bw.Write(dataBytes);

        Span<byte> scratch = stackalloc byte[sizeof(float)];
        foreach (float sample in samples)
        {
            BinaryPrimitives.WriteSingleLittleEndian(scratch, sample);
            bw.Write(scratch);
        }
    }
}
