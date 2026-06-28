// SPDX-License-Identifier: GPL-2.0-or-later
//
// End-to-end tests for the managed FT8 decoder over the zeus_ft8 native ABI.
// Native-dependent tests SkippableFact-skip when the library isn't staged for
// the current RID (e.g. a platform whose binary wasn't built), so the suite is
// green everywhere while still proving real decode where the lib is present.

using Zeus.Dsp.Ft8;

namespace Zeus.Dsp.Ft8.Tests;

public class Ft8DecoderTests
{
    private static string VectorDir =>
        Path.Combine(AppContext.BaseDirectory, "TestVectors");

    // Minimal RIFF/WAVE reader for the 12 kHz mono int16 reference vectors.
    private static float[] LoadWav(string path)
    {
        byte[] b = File.ReadAllBytes(path);
        // Locate the "data" chunk.
        int i = 12; // skip RIFF....WAVE
        int dataOff = -1, dataLen = 0;
        while (i + 8 <= b.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(b, i, 4);
            int sz = BitConverter.ToInt32(b, i + 4);
            if (id == "data") { dataOff = i + 8; dataLen = sz; break; }
            i += 8 + sz + (sz & 1);
        }
        Assert.True(dataOff > 0, "no data chunk in WAV");
        int n = Math.Min(dataLen, b.Length - dataOff) / 2;
        var f = new float[n];
        for (int k = 0; k < n; k++)
            f[k] = BitConverter.ToInt16(b, dataOff + 2 * k) / 32768f;
        return f;
    }

    [SkippableFact]
    public void NativeLibrary_IsAvailable_AndReportsVersion()
    {
        Skip.IfNot(Ft8Decoder.IsAvailable, "zeus_ft8 native library not staged for this RID");
        Assert.NotNull(Ft8Decoder.NativeVersion);
        Assert.Contains("zeus_ft8", Ft8Decoder.NativeVersion!);
    }

    [SkippableFact]
    public void Encode_StandardMessage_Produces79Tones()
    {
        Skip.IfNot(Ft8Decoder.IsAvailable, "zeus_ft8 native library not staged for this RID");
        byte[]? tones = Ft8Decoder.Encode("CQ KB2UKA FN12", Ft8Protocol.Ft8);
        Assert.NotNull(tones);
        Assert.Equal(79, tones!.Length);
        Assert.All(tones, t => Assert.InRange(t, (byte)0, (byte)7)); // FT8 = 8-FSK
    }

    [SkippableFact]
    public void Decode_CleanSlot_FindsKnownMessage()
    {
        Skip.IfNot(Ft8Decoder.IsAvailable, "zeus_ft8 native library not staged for this RID");
        float[] audio = LoadWav(Path.Combine(VectorDir, "191111_110145.wav"));

        using var dec = new Ft8Decoder();
        var results = dec.Decode(audio, Ft8Protocol.Ft8, passes: 1);

        // Answer key for this slot contains "GJ0KYZ RK9AX MO05".
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Text.Contains("RK9AX") && r.Text.Contains("GJ0KYZ"));
        // Decoded frequency/SNR should be sane (audio passband, plausible SNR).
        var hit = results.First(r => r.Text.Contains("RK9AX"));
        Assert.InRange(hit.FreqHz, 200f, 3000f);
        Assert.InRange(hit.SnrDb, -30f, 30f);
    }

    [SkippableFact]
    public void Decode_BusySlot_DecodesMany()
    {
        Skip.IfNot(Ft8Decoder.IsAvailable, "zeus_ft8 native library not staged for this RID");
        float[] audio = LoadWav(Path.Combine(VectorDir, "websdr_test13.wav"));

        using var dec = new Ft8Decoder();
        var results = dec.Decode(audio, Ft8Protocol.Ft8, passes: 3);

        // This clean slot's answer key has 13 messages; we decode all of them.
        Assert.True(results.Count >= 12, $"expected ~13 decodes, got {results.Count}");
        Assert.All(results, r => Assert.False(string.IsNullOrWhiteSpace(r.Text)));
    }

    [SkippableFact]
    public void TwoDecoders_DecodeConcurrently_NoCrashOrCorruption()
    {
        Skip.IfNot(Ft8Decoder.IsAvailable, "zeus_ft8 native library not staged for this RID");
        // Per-RX contexts must decode in parallel safely (multi-band requirement).
        float[] audio = LoadWav(Path.Combine(VectorDir, "websdr_test13.wav"));

        int[] counts = new int[8];
        Parallel.For(0, counts.Length, i =>
        {
            using var dec = new Ft8Decoder();
            counts[i] = dec.Decode(audio, Ft8Protocol.Ft8, passes: 1).Count;
        });

        // Every parallel decoder must return the same deterministic count.
        Assert.All(counts, c => Assert.Equal(counts[0], c));
        Assert.True(counts[0] > 0);
    }
}
