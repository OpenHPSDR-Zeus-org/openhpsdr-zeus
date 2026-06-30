// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Dsp.FreeDv;

namespace Zeus.Dsp.FreeDv.Tests;

/// <summary>
/// Objective magnitude-response tests for the RADE speech resampler (16↔48 kHz).
/// RADE decodes WIDEBAND 16 kHz speech, so this resampler — not the codec2 one —
/// sets the brightness/presence of decoded audio (RX, interpolator) and the
/// fidelity of the mic feed into the LPCNet analyzer (TX, decimator). These are
/// deterministic, native-free DSP measurements (a Goertzel-style single-bin DFT),
/// so they run everywhere and act as a hard regression guard on the prototype
/// filter: they fail if the passband narrows, the presence band rolls off, or the
/// stopband stops rejecting images/aliases.
///
/// Inspired by RADE's own validation discipline (drowe67/radae "Testing RADE"):
/// objective, reproducible metrics rather than subjective on-air listening.
/// </summary>
public class RadeResamplerFidelityTests
{
    // Single-frequency DFT magnitude (peak amplitude of a unit sine reads ~1.0).
    private static double BinAmplitude(ReadOnlySpan<float> x, double freqHz, double fsHz)
    {
        double w = 2.0 * Math.PI * freqHz / fsHz;
        double re = 0, im = 0;
        for (int n = 0; n < x.Length; n++)
        {
            re += x[n] * Math.Cos(w * n);
            im += x[n] * Math.Sin(w * n);
        }
        return 2.0 * Math.Sqrt(re * re + im * im) / x.Length;
    }

    private static float[] Sine(int n, double freqHz, double fsHz)
    {
        var b = new float[n];
        for (int i = 0; i < n; i++) b[i] = (float)Math.Sin(2.0 * Math.PI * freqHz * i / fsHz);
        return b;
    }

    // ---- Interpolator: 16 kHz -> 48 kHz (the RX decoded-speech up-sampler) ----

    [Theory]
    [InlineData(300, 0.93, 1.06)]   // low end — flat
    [InlineData(1000, 0.93, 1.06)]  // core voice — flat
    [InlineData(3000, 0.90, 1.06)]  // upper voice — flat
    [InlineData(6000, 0.70, 1.06)]  // PRESENCE band — must survive (the 96-tap win)
    public void Interpolator_Passband_IsFlatThroughPresenceBand(double freqHz, double lo, double hi)
    {
        var interp = RadeResampler.NewInterpolator();
        var input = Sine(16000, freqHz, 16000);      // 1 s @ 16 kHz
        var output = new float[input.Length * 3];     // 48 kHz
        int n = interp.Process(input, output);

        double mag = BinAmplitude(output.AsSpan(0, n), freqHz, 48000);
        Assert.True(mag >= lo && mag <= hi,
            $"interp |H({freqHz} Hz)| = {mag:F3}, expected [{lo},{hi}] — presence/passband regressed");
    }

    [Fact]
    public void Interpolator_RejectsImages_NoAliasingArtifacts()
    {
        // A 5 kHz tone at 16 kHz produces an image at 16000-5000 = 11000 after
        // up-sampling; the prototype low-pass must crush it (else the listener
        // hears spurious HF "edge"). Wanted tone passes; image is gone.
        var interp = RadeResampler.NewInterpolator();
        var input = Sine(16000, 5000, 16000);
        var output = new float[input.Length * 3];
        int n = interp.Process(input, output);
        var span = output.AsSpan(0, n);

        double wanted = BinAmplitude(span, 5000, 48000);
        double image = BinAmplitude(span, 11000, 48000);
        Assert.True(wanted > 0.85, $"wanted 5 kHz tone attenuated: {wanted:F3}");
        Assert.True(image < 0.05, $"image at 11 kHz not rejected: {image:F3} (>{0.05}) — stopband too weak");
    }

    // ---- Decimator: 48 kHz -> 16 kHz (the TX mic anti-alias into LPCNet) ----

    [Theory]
    [InlineData(300)]
    [InlineData(1000)]
    [InlineData(3000)]
    public void Decimator_Passband_PassesVoice(double freqHz)
    {
        var dec = RadeResampler.NewDecimator();
        var input = Sine(48000, freqHz, 48000);          // 1 s @ 48 kHz
        var output = new float[input.Length / 3 + 8];
        int n = dec.Process(input, output);

        double mag = BinAmplitude(output.AsSpan(0, n), freqHz, 16000);
        Assert.True(mag > 0.90, $"dec |H({freqHz} Hz)| = {mag:F3} — mic passband regressed");
    }

    [Fact]
    public void Decimator_RejectsAliases_CleanMicFeed()
    {
        // A 9 kHz mic component is ABOVE the 8 kHz (16 kHz-rate) Nyquist; without
        // a proper anti-alias filter it folds to 16000-9000 = 7000 Hz and colours
        // the speech the vocoder analyses. The decimator must reject it pre-decimate.
        var dec = RadeResampler.NewDecimator();
        var input = Sine(48000, 9000, 48000);
        var output = new float[input.Length / 3 + 8];
        int n = dec.Process(input, output);

        double alias = BinAmplitude(output.AsSpan(0, n), 7000, 16000);
        Assert.True(alias < 0.05,
            $"9 kHz mic tone aliased to 7 kHz at amplitude {alias:F3} (>{0.05}) — anti-alias too weak");

        // Sanity: a legitimate 7 kHz tone is NOT what we just rejected — it passes
        // (near the cutoff, so attenuated but clearly present), proving we rejected
        // the alias specifically and didn't just gut the top of the band.
        var legit = Sine(48000, 7000, 48000);
        var lout = new float[legit.Length / 3 + 8];
        int ln = RadeResampler.NewDecimator().Process(legit, lout);
        double legitMag = BinAmplitude(lout.AsSpan(0, ln), 7000, 16000);
        Assert.True(legitMag > 0.40, $"legit 7 kHz tone over-attenuated: {legitMag:F3}");
    }
}
