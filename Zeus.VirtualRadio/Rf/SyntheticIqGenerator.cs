// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

namespace Zeus.VirtualRadio.Rf;

/// <summary>
/// Produces time-domain interleaved I/Q at the negotiated DDC rate: the
/// profile's configured tones placed at baseband offset <c>(toneHz − tunedHz)</c>,
/// plus a Gaussian noise floor so the S-meter / waterfall / AGC behave. Rate-
/// aware (48/96/192/384k on P1). Models the <c>ChannelState</c> ideas in
/// <c>Zeus.Dsp/SyntheticDspEngine.cs</c>; one generator feeds the per-protocol
/// EP6 / DDC packers.
/// </summary>
internal sealed class SyntheticIqGenerator
{
    private const double TwoPi = 2.0 * Math.PI;

    // A fixed seed makes the noise stream deterministic so round-trip / spectrum
    // tests are reproducible across platforms. Phase-1 runs one radio at a time,
    // so a shared constant is fine; a per-instance seed can be threaded later if
    // multiple emulators must decorrelate.
    private const int NoiseSeed = 0x5EED5EED;

    private readonly double _sampleRateHz;
    private readonly long[] _toneFreqHz;
    private readonly double[] _toneAmp;
    private readonly double[] _tonePhase;   // persisted → phase-continuous across Generate calls
    private readonly double _noiseSigmaPerComponent;
    private readonly Random _rng;

    // Box–Muller produces two independent normals per draw; cache the spare.
    private bool _haveSpareGaussian;
    private double _spareGaussian;

    public SyntheticIqGenerator(VirtualRadioProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        // Negotiated DDC rate (48/96/192/384 kHz on P1). Guard against 0 so a
        // misconfigured profile can't divide-by-zero in the phase increment.
        _sampleRateHz = Math.Max(1, profile.SampleRateKhz) * 1000.0;

        var tones = profile.Tones ?? Array.Empty<ToneSpec>();
        int n = tones.Count;
        _toneFreqHz = new long[n];
        _toneAmp = new double[n];
        _tonePhase = new double[n];
        for (int i = 0; i < n; i++)
        {
            _toneFreqHz[i] = tones[i].FreqHz;
            _toneAmp[i] = DbToLinear(tones[i].Dbc);
        }

        // Total complex-noise RMS = 10^(dBc/20) of full scale. Split equally
        // across I and Q so E[|z|²] = noiseRms²: each component σ = rms/√2.
        double noiseRms = DbToLinear(profile.NoiseFloorDbc);
        _noiseSigmaPerComponent = noiseRms / Math.Sqrt(2.0);

        _rng = new Random(NoiseSeed);
    }

    /// <summary>
    /// Fill <paramref name="interleavedOut"/> with <paramref name="complexSamples"/>
    /// I/Q pairs (<c>[I0,Q0,I1,Q1,…]</c>) for a receiver tuned to
    /// <paramref name="tunedHz"/>. Each tone is rendered at its baseband offset
    /// <c>(FreqHz − tunedHz)</c>; a Gaussian noise floor is summed on top.
    /// Amplitude is in full-scale units (1.0 = 0 dBFS, matching
    /// <c>PacketParser.ScaleInt24</c>). Advances the per-tone phase so successive
    /// calls are phase-continuous.
    /// </summary>
    public void Generate(Span<double> interleavedOut, int complexSamples, long tunedHz)
    {
        if (complexSamples < 0)
            throw new ArgumentOutOfRangeException(nameof(complexSamples));
        if (interleavedOut.Length < complexSamples * 2)
            throw new ArgumentException(
                $"interleavedOut needs {complexSamples * 2} doubles, got {interleavedOut.Length}.",
                nameof(interleavedOut));

        int toneCount = _toneFreqHz.Length;
        double sigma = _noiseSigmaPerComponent;

        for (int sample = 0; sample < complexSamples; sample++)
        {
            double iAcc = 0.0;
            double qAcc = 0.0;

            for (int t = 0; t < toneCount; t++)
            {
                double amp = _toneAmp[t];
                double phase = _tonePhase[t];
                iAcc += amp * Math.Cos(phase);
                qAcc += amp * Math.Sin(phase);

                double inc = TwoPi * (_toneFreqHz[t] - tunedHz) / _sampleRateHz;
                // IEEERemainder keeps the accumulator bounded in [-π, π] for any
                // increment (incl. aliasing past Nyquist) without unbounded drift.
                _tonePhase[t] = Math.IEEERemainder(phase + inc, TwoPi);
            }

            if (sigma > 0.0)
            {
                iAcc += NextGaussian() * sigma;
                qAcc += NextGaussian() * sigma;
            }

            interleavedOut[2 * sample] = iAcc;
            interleavedOut[2 * sample + 1] = qAcc;
        }
    }

    private static double DbToLinear(double db) => Math.Pow(10.0, db / 20.0);

    /// <summary>Standard-normal draw via Box–Muller, caching the paired sample.</summary>
    private double NextGaussian()
    {
        if (_haveSpareGaussian)
        {
            _haveSpareGaussian = false;
            return _spareGaussian;
        }

        double u1;
        do { u1 = _rng.NextDouble(); } while (u1 <= double.Epsilon);
        double u2 = _rng.NextDouble();

        double mag = Math.Sqrt(-2.0 * Math.Log(u1));
        _spareGaussian = mag * Math.Sin(TwoPi * u2);
        _haveSpareGaussian = true;
        return mag * Math.Cos(TwoPi * u2);
    }
}
