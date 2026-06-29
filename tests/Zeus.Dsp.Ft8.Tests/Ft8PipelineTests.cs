// SPDX-License-Identifier: GPL-2.0-or-later
//
// Tests for the RX pipeline building blocks: the 48k->12k decimating resampler
// and the UTC slot accumulator (both pure/deterministic), plus an end-to-end
// "upsample a known slot to 48k, run it through the resampler, decode" check
// that proves the resampler preserves decodability.

using Zeus.Dsp.Ft8;

namespace Zeus.Dsp.Ft8.Tests;

public class Ft8PipelineTests
{
    private static float[] Sine(double freq, int rate, int n, double amp = 1.0)
    {
        var s = new float[n];
        for (int i = 0; i < n; i++) s[i] = (float)(amp * Math.Sin(2 * Math.PI * freq * i / rate));
        return s;
    }

    private static double Rms(ReadOnlySpan<float> x)
    {
        double sum = 0;
        foreach (var v in x) sum += (double)v * v;
        return x.Length == 0 ? 0 : Math.Sqrt(sum / x.Length);
    }

    [Fact]
    public void Resampler_DecimatesByFour_LengthAndRate()
    {
        var r = new Ft8Resampler();
        var input = Sine(1000, 48_000, 48_000); // 1 s
        var output = r.Process(input);
        // ~1/4 the samples (allow small edge slack).
        Assert.InRange(output.Length, 48_000 / 4 - 4, 48_000 / 4 + 4);
    }

    [Fact]
    public void Resampler_PassesAudioBand_RejectsAboveNyquist()
    {
        // In-band 1500 Hz survives; 7000 Hz (above the 6 kHz decimated Nyquist)
        // is rejected by the anti-alias filter rather than aliasing back in.
        var pass = new Ft8Resampler().Process(Sine(1500, 48_000, 48_000));
        var stop = new Ft8Resampler().Process(Sine(7000, 48_000, 48_000));

        // Ignore filter warm-up at the edges.
        double passRms = Rms(pass.AsSpan(64, pass.Length - 128));
        double stopRms = Rms(stop.AsSpan(64, stop.Length - 128));

        Assert.True(passRms > 0.5, $"in-band tone attenuated: rms={passRms:0.000}");
        Assert.True(stopRms < 0.15, $"out-of-band tone not rejected: rms={stopRms:0.000}");
    }

    [Fact]
    public void SlotAccumulator_CompletesOnBoundaryCrossing()
    {
        var acc = new Ft8SlotAccumulator(slotSeconds: 15.0);
        var t0 = new DateTime(2026, 6, 25, 14, 25, 3, DateTimeKind.Utc); // inside slot @ :00..:15

        // Two adds within the same slot → no completion.
        Assert.Null(acc.Add(new float[1000], t0));
        Assert.Null(acc.Add(new float[1000], t0.AddSeconds(5)));

        // Crossing into the next slot (:15) completes the prior one.
        var done = acc.Add(new float[500], new DateTime(2026, 6, 25, 14, 25, 16, DateTimeKind.Utc));
        Assert.NotNull(done);
        Assert.Equal(2000, done!.Value.Samples.Length);    // 1000 + 1000 from the first slot
        // Completed slot started at 14:25:00 UTC (the 15 s window holding :03..:14).
        Assert.Equal(14, done.Value.SlotStartUtc.Hour);
        Assert.Equal(25, done.Value.SlotStartUtc.Minute);
        Assert.Equal(0, done.Value.SlotStartUtc.Second);
    }

    [Fact]
    public void SlotAccumulator_CapsAtOneSlotOfSamples()
    {
        var acc = new Ft8SlotAccumulator(slotSeconds: 15.0); // capacity 180000
        var t = new DateTime(2026, 6, 25, 0, 0, 1, DateTimeKind.Utc);
        // Overfill the slot, then cross the boundary.
        acc.Add(new float[200_000], t);
        var done = acc.Add(new float[10], t.AddSeconds(15));
        Assert.NotNull(done);
        Assert.Equal(180_000, done!.Value.Samples.Length); // capped at 15 s @ 12 kHz
    }

    [SkippableFact]
    public void ResamplerThenDecode_PreservesDecodability()
    {
        Skip.IfNot(Ft8Decoder.IsAvailable, "zeus_ft8 native library not staged for this RID");

        // Load the 12 kHz reference slot and upsample x4 (linear) to fake 48 kHz
        // radio audio, then run it back through the production decimator.
        float[] at12k = LoadWav(Path.Combine(AppContext.BaseDirectory, "TestVectors", "191111_110145.wav"));
        var at48k = new float[at12k.Length * 4];
        for (int i = 0; i < at12k.Length - 1; i++)
        {
            float a = at12k[i], b = at12k[i + 1];
            for (int j = 0; j < 4; j++) at48k[i * 4 + j] = a + (b - a) * (j / 4f);
        }

        float[] back = new Ft8Resampler().Process(at48k);

        using var dec = new Ft8Decoder();
        var results = dec.Decode(back, Ft8Protocol.Ft8, passes: 1);
        Assert.Contains(results, r => r.Text.Contains("RK9AX") && r.Text.Contains("GJ0KYZ"));
    }

    private static float[] LoadWav(string path)
    {
        byte[] b = File.ReadAllBytes(path);
        int i = 12, dataOff = -1, dataLen = 0;
        while (i + 8 <= b.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(b, i, 4);
            int sz = BitConverter.ToInt32(b, i + 4);
            if (id == "data") { dataOff = i + 8; dataLen = sz; break; }
            i += 8 + sz + (sz & 1);
        }
        int n = Math.Min(dataLen, b.Length - dataOff) / 2;
        var f = new float[n];
        for (int k = 0; k < n; k++) f[k] = BitConverter.ToInt16(b, dataOff + 2 * k) / 32768f;
        return f;
    }
}
