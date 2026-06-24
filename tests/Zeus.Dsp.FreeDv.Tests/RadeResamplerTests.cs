// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Dsp.FreeDv;

namespace Zeus.Dsp.FreeDv.Tests;

// Pure-managed tests for the RADE wideband resamplers (no native dependency).
// The 16k<->48k interpolator already shipped with RX; these cover the new 48k->16k
// decimator added for the RADE TX speech path and the round-trip fidelity that
// keeps the LPCNet analyzer fed with full-band speech.
public class RadeResamplerTests
{
    [Fact]
    public void Decimator_Produces_OneOutputPerThreeInputs()
    {
        var dec = RadeResampler.NewDecimator();
        var input = new float[3000];        // 48 kHz
        var output = new float[RadeResampler.MaxDecimatedLength(input.Length)];
        int n = dec.Process(input, output);
        Assert.Equal(1000, n);              // exact 3:1
    }

    [Fact]
    public void Decimator_PreservesDcGain()
    {
        // A constant (DC) input must come out at the same level — the prototype is
        // normalised to unity DC gain. Skip the filter warm-up transient.
        var dec = RadeResampler.NewDecimator();
        var input = new float[6000];
        Array.Fill(input, 0.5f);
        var output = new float[RadeResampler.MaxDecimatedLength(input.Length)];
        int n = dec.Process(input, output);
        Assert.True(n > 100);
        for (int i = 80; i < n; i++)
            Assert.True(MathF.Abs(output[i] - 0.5f) < 1e-3f, $"sample {i} = {output[i]}");
    }

    [Fact]
    public void DecimateThenInterpolate_PreservesLowFrequencyTone()
    {
        // 300 Hz tone (deep in the speech band) must survive 48k -> 16k -> 48k with
        // its amplitude largely intact: the wideband prototype is the point. We
        // compare RMS after the round trip, ignoring group-delay alignment.
        const int fs = 48000;
        const double f = 300.0;
        var input = new float[fs]; // 1 s
        for (int i = 0; i < input.Length; i++) input[i] = 0.5f * MathF.Sin((float)(2 * Math.PI * f * i / fs));

        var dec = RadeResampler.NewDecimator();
        var mid = new float[RadeResampler.MaxDecimatedLength(input.Length)];
        int nMid = dec.Process(input, mid);

        var interp = RadeResampler.NewInterpolator();
        var outp = new float[RadeResampler.InterpolatedLength(nMid)];
        int nOut = interp.Process(mid.AsSpan(0, nMid), outp);

        // RMS of the steady-state portion (drop the first/last 10% for transients).
        double rmsIn = Rms(input, input.Length / 10, input.Length - input.Length / 10);
        double rmsOut = Rms(outp, nOut / 10, nOut - nOut / 10);
        Assert.True(rmsOut > 0.6 * rmsIn, $"round-trip RMS collapsed: in={rmsIn:F4} out={rmsOut:F4}");
        Assert.True(rmsOut < 1.4 * rmsIn, $"round-trip RMS ballooned: in={rmsIn:F4} out={rmsOut:F4}");
    }

    [Fact]
    public void Decimator_Reset_ClearsHistory()
    {
        var dec = RadeResampler.NewDecimator();
        var ramp = new float[300];
        for (int i = 0; i < ramp.Length; i++) ramp[i] = i;
        var o1 = new float[RadeResampler.MaxDecimatedLength(ramp.Length)];
        dec.Process(ramp, o1);
        dec.Reset();
        var zeros = new float[300];
        var o2 = new float[RadeResampler.MaxDecimatedLength(zeros.Length)];
        int n = dec.Process(zeros, o2);
        for (int i = 0; i < n; i++) Assert.Equal(0f, o2[i], 5);
    }

    private static double Rms(float[] x, int from, int to)
    {
        double s = 0; int c = 0;
        for (int i = from; i < to; i++) { s += (double)x[i] * x[i]; c++; }
        return c > 0 ? Math.Sqrt(s / c) : 0;
    }
}
