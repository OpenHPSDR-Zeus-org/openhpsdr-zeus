// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus - OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.

namespace Zeus.Dsp.Tests;

internal enum DspBenchmarkPath
{
    RxIq,
    TxAudio
}

internal enum DspBenchmarkScenarioKind
{
    WeakCarrier,
    SsbLikeSpeech,
    FadingCarrier,
    ImpulseNoise,
    StrongAdjacent,
    NoiseOnly,
    AgcStep,
    SquelchTransition,
    TxTwoTone,
    TxVoiceLike
}

internal sealed record DspBenchmarkFixture(
    string Name,
    DspBenchmarkScenarioKind Kind,
    DspBenchmarkPath Path,
    int SampleRateHz,
    double[]? IqInterleaved,
    float[]? Audio,
    IReadOnlyDictionary<string, double> ExpectedTonesHz)
{
    public int SampleCount => Path == DspBenchmarkPath.RxIq
        ? (IqInterleaved?.Length ?? 0) / 2
        : Audio?.Length ?? 0;
}

internal static class DspBenchmarkFixtureCatalog
{
    public const int RxSampleRateHz = 192_000;
    public const int TxSampleRateHz = 48_000;
    public const int RxSamples = 65_536;
    public const int TxSamples = 16_384;

    public static IReadOnlyList<DspBenchmarkFixture> All() =>
        new[]
        {
            WeakCarrier(),
            SsbLikeSpeech(),
            FadingCarrier(),
            ImpulseNoise(),
            StrongAdjacent(),
            NoiseOnly(),
            AgcStep(),
            SquelchTransition(),
            TxTwoTone(),
            TxVoiceLike()
        };

    public static DspBenchmarkFixture Create(DspBenchmarkScenarioKind kind) =>
        kind switch
        {
            DspBenchmarkScenarioKind.WeakCarrier => WeakCarrier(),
            DspBenchmarkScenarioKind.SsbLikeSpeech => SsbLikeSpeech(),
            DspBenchmarkScenarioKind.FadingCarrier => FadingCarrier(),
            DspBenchmarkScenarioKind.ImpulseNoise => ImpulseNoise(),
            DspBenchmarkScenarioKind.StrongAdjacent => StrongAdjacent(),
            DspBenchmarkScenarioKind.NoiseOnly => NoiseOnly(),
            DspBenchmarkScenarioKind.AgcStep => AgcStep(),
            DspBenchmarkScenarioKind.SquelchTransition => SquelchTransition(),
            DspBenchmarkScenarioKind.TxTwoTone => TxTwoTone(),
            DspBenchmarkScenarioKind.TxVoiceLike => TxVoiceLike(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

    private static DspBenchmarkFixture WeakCarrier()
    {
        var prng = new DeterministicNoise(0x5743_0001u);
        var iq = BuildRxIq(n =>
        {
            Complex tone = ComplexTone(1_500.0, n, 0.006);
            Complex noise = ComplexNoise(ref prng, 0.018);
            return tone + noise;
        });

        return Rx("weak-cw-carrier", DspBenchmarkScenarioKind.WeakCarrier, iq, ("wanted", 1_500.0));
    }

    private static DspBenchmarkFixture SsbLikeSpeech()
    {
        var prng = new DeterministicNoise(0x5353_4201u);
        var iq = BuildRxIq(n =>
        {
            double t = n / (double)RxSampleRateHz;
            double envelope = 0.45 + 0.35 * Math.Sin(2.0 * Math.PI * 4.2 * t);
            envelope += 0.15 * Math.Sin(2.0 * Math.PI * 6.7 * t + 0.4);
            envelope = Math.Clamp(envelope, 0.1, 0.95);

            Complex formants =
                ComplexTone(420.0, n, 0.020 * envelope) +
                ComplexTone(1_050.0, n, 0.026 * envelope) +
                ComplexTone(2_100.0, n, 0.014 * envelope);

            return formants + ComplexNoise(ref prng, 0.010);
        });

        return Rx("ssb-like-speech", DspBenchmarkScenarioKind.SsbLikeSpeech, iq,
            ("f1", 420.0), ("f2", 1_050.0), ("f3", 2_100.0));
    }

    private static DspBenchmarkFixture FadingCarrier()
    {
        var prng = new DeterministicNoise(0x4641_4401u);
        var iq = BuildRxIq(n =>
        {
            double t = n / (double)RxSampleRateHz;
            double fade = 0.18 + 0.82 * Math.Pow(0.5 + 0.5 * Math.Sin(2.0 * Math.PI * 1.3 * t), 2.0);
            return ComplexTone(1_250.0, n, 0.040 * fade) + ComplexNoise(ref prng, 0.008);
        });

        return Rx("fading-carrier", DspBenchmarkScenarioKind.FadingCarrier, iq, ("wanted", 1_250.0));
    }

    private static DspBenchmarkFixture ImpulseNoise()
    {
        var prng = new DeterministicNoise(0x494d_5001u);
        var iq = BuildRxIq(n =>
        {
            Complex sample = ComplexTone(1_700.0, n, 0.030) + ComplexNoise(ref prng, 0.010);
            if (n % 1_531 == 0)
                sample += new Complex(1.15, -0.95);
            return sample;
        });

        return Rx("impulse-noise", DspBenchmarkScenarioKind.ImpulseNoise, iq, ("wanted", 1_700.0));
    }

    private static DspBenchmarkFixture StrongAdjacent()
    {
        var prng = new DeterministicNoise(0x4144_4a01u);
        var iq = BuildRxIq(n =>
            ComplexTone(1_200.0, n, 0.010) +
            ComplexTone(5_500.0, n, 0.180) +
            ComplexNoise(ref prng, 0.010));

        return Rx("strong-adjacent", DspBenchmarkScenarioKind.StrongAdjacent, iq,
            ("wanted", 1_200.0), ("adjacent", 5_500.0));
    }

    private static DspBenchmarkFixture NoiseOnly()
    {
        var prng = new DeterministicNoise(0x4e4f_4901u);
        var iq = BuildRxIq(_ => ComplexNoise(ref prng, 0.020));
        return Rx("noise-only", DspBenchmarkScenarioKind.NoiseOnly, iq);
    }

    private static DspBenchmarkFixture AgcStep()
    {
        var prng = new DeterministicNoise(0x4147_4301u);
        var iq = BuildRxIq(n =>
        {
            double amp = n switch
            {
                < RxSamples / 3 => 0.008,
                < 2 * RxSamples / 3 => 0.170,
                _ => 0.020
            };

            return ComplexTone(1_400.0, n, amp) + ComplexNoise(ref prng, 0.006);
        });

        return Rx("agc-level-step", DspBenchmarkScenarioKind.AgcStep, iq, ("wanted", 1_400.0));
    }

    private static DspBenchmarkFixture SquelchTransition()
    {
        var prng = new DeterministicNoise(0x5351_4c01u);
        var iq = BuildRxIq(n =>
        {
            double amp = n switch
            {
                < RxSamples / 4 => 0.0,
                < RxSamples / 2 => 0.045,
                < 3 * RxSamples / 4 => 0.006,
                _ => 0.0
            };

            return ComplexTone(1_650.0, n, amp) + ComplexNoise(ref prng, 0.014);
        });

        return Rx("squelch-transition", DspBenchmarkScenarioKind.SquelchTransition, iq, ("wanted", 1_650.0));
    }

    private static DspBenchmarkFixture TxTwoTone()
    {
        var audio = BuildTxAudio(n =>
            0.22 * Math.Sin(2.0 * Math.PI * 700.0 * n / TxSampleRateHz) +
            0.22 * Math.Sin(2.0 * Math.PI * 1_900.0 * n / TxSampleRateHz));

        return Tx("tx-two-tone", DspBenchmarkScenarioKind.TxTwoTone, audio, ("low", 700.0), ("high", 1_900.0));
    }

    private static DspBenchmarkFixture TxVoiceLike()
    {
        var prng = new DeterministicNoise(0x5658_0101u);
        var audio = BuildTxAudio(n =>
        {
            double t = n / (double)TxSampleRateHz;
            double envelope = 0.52 + 0.25 * Math.Sin(2.0 * Math.PI * 3.7 * t);
            double voiced =
                0.16 * Math.Sin(2.0 * Math.PI * 180.0 * t) +
                0.09 * Math.Sin(2.0 * Math.PI * 720.0 * t + 0.1) +
                0.06 * Math.Sin(2.0 * Math.PI * 1_820.0 * t + 0.6);
            return envelope * voiced + prng.NextSignedDouble() * 0.010;
        });

        return Tx("tx-voice-like", DspBenchmarkScenarioKind.TxVoiceLike, audio,
            ("fundamental", 180.0), ("formant", 720.0), ("presence", 1_820.0));
    }

    private static DspBenchmarkFixture Rx(
        string name,
        DspBenchmarkScenarioKind kind,
        double[] iq,
        params (string Name, double Hz)[] tones) =>
        new(name, kind, DspBenchmarkPath.RxIq, RxSampleRateHz, iq, null, ToneMap(tones));

    private static DspBenchmarkFixture Tx(
        string name,
        DspBenchmarkScenarioKind kind,
        float[] audio,
        params (string Name, double Hz)[] tones) =>
        new(name, kind, DspBenchmarkPath.TxAudio, TxSampleRateHz, null, audio, ToneMap(tones));

    private static IReadOnlyDictionary<string, double> ToneMap((string Name, double Hz)[] tones)
    {
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var tone in tones)
            map.Add(tone.Name, tone.Hz);
        return map;
    }

    private static double[] BuildRxIq(Func<int, Complex> sample)
    {
        var iq = new double[RxSamples * 2];
        for (int n = 0; n < RxSamples; n++)
        {
            Complex value = sample(n);
            iq[2 * n] = value.I;
            iq[2 * n + 1] = value.Q;
        }
        return iq;
    }

    private static float[] BuildTxAudio(Func<int, double> sample)
    {
        var audio = new float[TxSamples];
        for (int n = 0; n < TxSamples; n++)
            audio[n] = (float)sample(n);
        return audio;
    }

    private static Complex ComplexTone(double frequencyHz, int n, double amplitude)
    {
        double phase = 2.0 * Math.PI * frequencyHz * n / RxSampleRateHz;
        return new Complex(amplitude * Math.Cos(phase), amplitude * Math.Sin(phase));
    }

    private static Complex ComplexNoise(ref DeterministicNoise prng, double peak)
    {
        return new Complex(prng.NextSignedDouble() * peak, prng.NextSignedDouble() * peak);
    }

    private readonly record struct Complex(double I, double Q)
    {
        public static Complex operator +(Complex left, Complex right) =>
            new(left.I + right.I, left.Q + right.Q);
    }

    private struct DeterministicNoise
    {
        private uint _state;

        public DeterministicNoise(uint seed)
        {
            _state = seed == 0 ? 1u : seed;
        }

        public double NextSignedDouble()
        {
            uint x = _state;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            _state = x;
            return ((x >> 8) / 16_777_216.0) * 2.0 - 1.0;
        }
    }
}

internal sealed record DspBenchmarkMetrics(
    double Rms,
    double Peak,
    double CrestFactorDb,
    double DcOffset,
    double WindowedRmsSpreadDb,
    IReadOnlyDictionary<string, double> TonePowerDb);

internal static class DspBenchmarkAnalyzer
{
    public static DspBenchmarkMetrics Analyze(DspBenchmarkFixture fixture)
    {
        if (fixture.Path == DspBenchmarkPath.RxIq)
        {
            if (fixture.IqInterleaved is null)
                throw new ArgumentException("RX fixture is missing IQ samples.", nameof(fixture));

            return AnalyzeIq(fixture.IqInterleaved, fixture.SampleRateHz, fixture.ExpectedTonesHz);
        }

        if (fixture.Audio is null)
            throw new ArgumentException("TX fixture is missing audio samples.", nameof(fixture));

        return AnalyzeAudio(fixture.Audio, fixture.SampleRateHz, fixture.ExpectedTonesHz);
    }

    public static DspBenchmarkMetrics AnalyzeIq(
        ReadOnlySpan<double> iqInterleaved,
        int sampleRateHz,
        IReadOnlyDictionary<string, double> expectedTonesHz)
    {
        if (iqInterleaved.Length == 0 || (iqInterleaved.Length & 1) != 0)
            throw new ArgumentException("IQ input must contain an even number of interleaved samples.", nameof(iqInterleaved));

        int complexSamples = iqInterleaved.Length / 2;
        double sumSquares = 0.0;
        double peak = 0.0;
        double dcI = 0.0;
        double dcQ = 0.0;

        for (int n = 0; n < complexSamples; n++)
        {
            double i = iqInterleaved[2 * n];
            double q = iqInterleaved[2 * n + 1];
            double power = i * i + q * q;
            sumSquares += power;
            peak = Math.Max(peak, Math.Sqrt(power));
            dcI += i;
            dcQ += q;
        }

        double rms = Math.Sqrt(sumSquares / complexSamples);
        double dc = Math.Sqrt(Square(dcI / complexSamples) + Square(dcQ / complexSamples));

        return new DspBenchmarkMetrics(
            rms,
            peak,
            ToDb(peak / Math.Max(rms, 1e-300)),
            dc,
            WindowedRmsSpreadDb(iqInterleaved, complexSamples, complex: true),
            TonePowerIq(iqInterleaved, complexSamples, sampleRateHz, expectedTonesHz));
    }

    public static DspBenchmarkMetrics AnalyzeAudio(
        ReadOnlySpan<float> audio,
        int sampleRateHz,
        IReadOnlyDictionary<string, double> expectedTonesHz)
    {
        if (audio.Length == 0)
            throw new ArgumentException("Audio input must not be empty.", nameof(audio));

        double sumSquares = 0.0;
        double peak = 0.0;
        double dc = 0.0;

        for (int n = 0; n < audio.Length; n++)
        {
            double x = audio[n];
            sumSquares += x * x;
            peak = Math.Max(peak, Math.Abs(x));
            dc += x;
        }

        double rms = Math.Sqrt(sumSquares / audio.Length);

        return new DspBenchmarkMetrics(
            rms,
            peak,
            ToDb(peak / Math.Max(rms, 1e-300)),
            dc / audio.Length,
            WindowedRmsSpreadDb(audio, audio.Length, complex: false),
            TonePowerAudio(audio, sampleRateHz, expectedTonesHz));
    }

    private static IReadOnlyDictionary<string, double> TonePowerIq(
        ReadOnlySpan<double> iqInterleaved,
        int complexSamples,
        int sampleRateHz,
        IReadOnlyDictionary<string, double> expectedTonesHz)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var tone in expectedTonesHz)
        {
            double re = 0.0;
            double im = 0.0;
            double omega = -2.0 * Math.PI * tone.Value / sampleRateHz;

            for (int n = 0; n < complexSamples; n++)
            {
                double i = iqInterleaved[2 * n];
                double q = iqInterleaved[2 * n + 1];
                double c = Math.Cos(omega * n);
                double s = Math.Sin(omega * n);
                re += i * c - q * s;
                im += i * s + q * c;
            }

            double power = (re * re + im * im) / Square(complexSamples);
            result.Add(tone.Key, PowerToDb(power));
        }

        return result;
    }

    private static IReadOnlyDictionary<string, double> TonePowerAudio(
        ReadOnlySpan<float> audio,
        int sampleRateHz,
        IReadOnlyDictionary<string, double> expectedTonesHz)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var tone in expectedTonesHz)
        {
            double re = 0.0;
            double im = 0.0;
            double omega = -2.0 * Math.PI * tone.Value / sampleRateHz;

            for (int n = 0; n < audio.Length; n++)
            {
                double c = Math.Cos(omega * n);
                double s = Math.Sin(omega * n);
                re += audio[n] * c;
                im += audio[n] * s;
            }

            double power = 4.0 * (re * re + im * im) / Square(audio.Length);
            result.Add(tone.Key, PowerToDb(power));
        }

        return result;
    }

    private static double WindowedRmsSpreadDb<T>(ReadOnlySpan<T> samples, int logicalSamples, bool complex)
        where T : struct
    {
        int window = Math.Max(128, logicalSamples / 16);
        double min = double.PositiveInfinity;
        double max = 0.0;

        for (int start = 0; start + window <= logicalSamples; start += window)
        {
            double sumSquares = 0.0;
            if (complex)
            {
                var iq = System.Runtime.InteropServices.MemoryMarshal.Cast<T, double>(samples);
                for (int n = start; n < start + window; n++)
                {
                    double i = iq[2 * n];
                    double q = iq[2 * n + 1];
                    sumSquares += i * i + q * q;
                }
            }
            else
            {
                var audio = System.Runtime.InteropServices.MemoryMarshal.Cast<T, float>(samples);
                for (int n = start; n < start + window; n++)
                    sumSquares += audio[n] * audio[n];
            }

            double rms = Math.Sqrt(sumSquares / window);
            min = Math.Min(min, rms);
            max = Math.Max(max, rms);
        }

        return ToDb(max / Math.Max(min, 1e-300));
    }

    private static double Square(double value) => value * value;

    private static double ToDb(double ratio) => 20.0 * Math.Log10(Math.Max(ratio, 1e-300));

    private static double PowerToDb(double power) => 10.0 * Math.Log10(Math.Max(power, 1e-300));
}
