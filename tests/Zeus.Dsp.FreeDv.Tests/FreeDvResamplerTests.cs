// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Dsp.FreeDv;

namespace Zeus.Dsp.FreeDv.Tests;

public class FreeDvSampleRingTests
{
    [Fact]
    public void WriteThenRead_PreservesOrderAndCount()
    {
        var ring = new FreeDvSampleRing(8);
        Assert.Equal(3, ring.Write([1f, 2f, 3f]));
        Assert.Equal(3, ring.Count);

        var dst = new float[3];
        Assert.Equal(3, ring.Read(dst));
        Assert.Equal(0, ring.Count);
        Assert.Equal(new[] { 1f, 2f, 3f }, dst);
    }

    [Fact]
    public void Write_BeyondCapacity_ReturnsShortCount()
    {
        var ring = new FreeDvSampleRing(4); // fixed, never grows
        var data = new float[10];
        for (int i = 0; i < data.Length; i++) data[i] = i;
        Assert.Equal(4, ring.Write(data)); // only 4 fit
        Assert.Equal(4, ring.Count);
    }

    [Fact]
    public void Capacity_MustBePowerOfTwo()
    {
        Assert.Throws<ArgumentException>(() => new FreeDvSampleRing(6));
    }

    [Fact]
    public void Read_ReturnsAvailableWhenUnderfilled()
    {
        var ring = new FreeDvSampleRing(8);
        ring.Write([9f]);
        Assert.Equal(1, ring.Read(new float[4]));
    }

    [Fact]
    public void Drop_RemovesOldest()
    {
        var ring = new FreeDvSampleRing(8);
        ring.Write([1f, 2f, 3f, 4f]);
        ring.Drop(2);
        Assert.Equal(2, ring.Count);
        var dst = new float[2];
        ring.Read(dst);
        Assert.Equal(new[] { 3f, 4f }, dst);
    }

    [Fact]
    public void Clear_DiscardsAll()
    {
        var ring = new FreeDvSampleRing(8);
        ring.Write([1f, 2f, 3f]);
        ring.Clear();
        Assert.Equal(0, ring.Count);
    }

    [Fact]
    public void WrapAround_WorksAcrossManyRounds()
    {
        var ring = new FreeDvSampleRing(8);
        var dst = new float[2];
        for (int round = 0; round < 200; round++)
        {
            ring.Write([round, round + 0.5f]);
            Assert.Equal(2, ring.Read(dst));
            Assert.Equal(round, dst[0]);
            Assert.Equal(round + 0.5f, dst[1]);
        }
        Assert.Equal(0, ring.Count);
    }
}

public class FreeDvResamplerTests
{
    private const int FsHigh = 48000;

    [Fact]
    public void DcSignal_RoundTrips_ToUnityGain()
    {
        var dec = FreeDvResampler.NewDecimator();
        var interp = FreeDvResampler.NewInterpolator();

        var dc = new float[FsHigh]; // 1 second
        Array.Fill(dc, 1.0f);

        var low = new float[FreeDvResampler.MaxDecimatedLength(dc.Length)];
        int n8 = dec.Process(dc, low);

        var high = new float[FreeDvResampler.InterpolatedLength(n8)];
        int n48 = interp.Process(low.AsSpan(0, n8), high);

        int lo = n48 / 4, hi = n48 * 3 / 4;
        double sum = 0; int cnt = 0;
        for (int i = lo; i < hi; i++) { sum += high[i]; cnt++; }
        Assert.InRange(sum / cnt, 0.95, 1.05);
    }

    [Fact]
    public void PassbandTone_RoundTrips_PreservingEnergy()
    {
        var dec = FreeDvResampler.NewDecimator();
        var interp = FreeDvResampler.NewInterpolator();

        int n = FsHigh; // 1 s
        var input = new float[n];
        for (int i = 0; i < n; i++)
            input[i] = MathF.Sin(2f * MathF.PI * 1000f * i / FsHigh);

        var low = new float[FreeDvResampler.MaxDecimatedLength(n)];
        int n8 = dec.Process(input, low);
        var high = new float[FreeDvResampler.InterpolatedLength(n8)];
        int n48 = interp.Process(low.AsSpan(0, n8), high);

        double inRms = Rms(input, n / 4, n * 3 / 4);
        double outRms = Rms(high, n48 / 4, n48 * 3 / 4);
        Assert.InRange(outRms / inRms, 0.85, 1.15);
        for (int i = 0; i < n48; i++)
            Assert.False(float.IsNaN(high[i]) || float.IsInfinity(high[i]));
    }

    [Fact]
    public void Decimator_Produces_OneSixthSampleCount()
    {
        var dec = FreeDvResampler.NewDecimator();
        var outp = new float[FreeDvResampler.MaxDecimatedLength(600)];
        Assert.Equal(100, dec.Process(new float[600], outp)); // 600 / 6
    }

    [Fact]
    public void Interpolator_Produces_SixTimesSampleCount()
    {
        var interp = FreeDvResampler.NewInterpolator();
        var outp = new float[FreeDvResampler.InterpolatedLength(100)];
        Assert.Equal(600, interp.Process(new float[100], outp)); // 100 * 6
    }

    private static double Rms(float[] x, int lo, int hi)
    {
        double s = 0; int c = 0;
        for (int i = lo; i < hi; i++) { s += (double)x[i] * x[i]; c++; }
        return Math.Sqrt(s / Math.Max(1, c));
    }
}
