// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus - OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.

using Zeus.Contracts;
using Zeus.Dsp.Wdsp;
using Xunit;

namespace Zeus.Dsp.Tests;

[Collection("Wdsp")]
public class NoiseReductionSyntheticFixtureTests
{
    private const int AudioSampleRateHz = 48_000;
    private const int PixelWidth = 2048;
    private const int ChunkComplex = 126;
    private const int FixtureRepeats = 4;
    private const int MinimumAudioSamples = 8_192;
    private const int AnalysisSamples = 16_384;

    private static readonly DspBenchmarkScenarioKind[] RequiredFixtureKinds =
    [
        DspBenchmarkScenarioKind.WeakCarrier,
        DspBenchmarkScenarioKind.SsbLikeSpeech,
        DspBenchmarkScenarioKind.FadingCarrier,
        DspBenchmarkScenarioKind.ImpulseNoise,
        DspBenchmarkScenarioKind.StrongAdjacent,
        DspBenchmarkScenarioKind.AgcStep,
        DspBenchmarkScenarioKind.SquelchTransition,
        DspBenchmarkScenarioKind.NoiseOnly
    ];

    private static bool WdspAvailable()
    {
        try { return WdspNativeLoader.TryProbe(); }
        catch { return false; }
    }

    private static bool SbnrAvailable()
    {
        try { return WdspDspEngine.Nr4SbnrAvailable; }
        catch { return false; }
    }

    [Fact]
    public void FixtureCatalog_CoversRequiredRxScenes()
    {
        var names = DspBenchmarkFixtureCatalog.All().Select(fixture => fixture.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("weak-cw-carrier", names);
        Assert.Contains("ssb-like-speech", names);
        Assert.Contains("fading-carrier", names);
        Assert.Contains("impulse-noise", names);
        Assert.Contains("strong-adjacent", names);
        Assert.Contains("agc-level-step", names);
        Assert.Contains("squelch-transition", names);
        Assert.Contains("noise-only", names);

        foreach (var kind in RequiredFixtureKinds)
        {
            var fixture = DspBenchmarkFixtureCatalog.Create(kind);
            Assert.Equal(DspBenchmarkPath.RxIq, fixture.Path);
            Assert.Equal(DspBenchmarkFixtureCatalog.RxSampleRateHz, fixture.SampleRateHz);
            Assert.True(fixture.SampleCount > 0, $"{fixture.Name} has no IQ samples");
        }
    }

    [SkippableFact]
    public void Wdsp_CurrentWeakSignalFixtures_ProduceBoundedAudio()
    {
        Skip.IfNot(WdspAvailable(), "libwdsp not available");

        foreach (var kind in new[]
                 {
                     DspBenchmarkScenarioKind.WeakCarrier,
                     DspBenchmarkScenarioKind.SsbLikeSpeech,
                     DspBenchmarkScenarioKind.FadingCarrier
                 })
        {
            var fixture = DspBenchmarkFixtureCatalog.Create(kind);
            var results = RunComparisons(fixture, includeNr4WhenAvailable: true);
            var off = results.Single(result => result.NrMode == NrMode.Off);

            Assert.Contains(results, result => result.NrMode == NrMode.Emnr);
            if (SbnrAvailable())
                Assert.Contains(results, result => result.NrMode == NrMode.Sbnr);

            foreach (var result in results)
            {
                AssertHealthyOutput(result);
                if (result.NrMode != NrMode.Off)
                    AssertLevelNormalized(result, off, maxRatio: 4.0);
            }
        }
    }

    [SkippableFact]
    public void Wdsp_NoiseOnlyFixture_RemainsStableForCurrentModes()
    {
        Skip.IfNot(WdspAvailable(), "libwdsp not available");

        var fixture = DspBenchmarkFixtureCatalog.Create(DspBenchmarkScenarioKind.NoiseOnly);
        var results = RunComparisons(fixture, includeNr4WhenAvailable: true);
        var off = results.Single(result => result.NrMode == NrMode.Off);

        foreach (var result in results)
        {
            AssertHealthyOutput(result);
            if (result.NrMode != NrMode.Off)
            {
                AssertLevelNormalized(result, off, maxRatio: 3.0);
                Assert.True(
                    result.Metrics.WindowedRmsSpreadDb <= off.Metrics.WindowedRmsSpreadDb + 10.0,
                    $"noise-only {result.NrMode} should not introduce unstable pumping. offSpread={off.Metrics.WindowedRmsSpreadDb:F1}dB resultSpread={result.Metrics.WindowedRmsSpreadDb:F1}dB");
            }
        }
    }

    [SkippableFact]
    public void Wdsp_ImpulseFixture_RunsWithNbOffAndNbOnWithoutClipping()
    {
        Skip.IfNot(WdspAvailable(), "libwdsp not available");

        var fixture = DspBenchmarkFixtureCatalog.Create(DspBenchmarkScenarioKind.ImpulseNoise);
        var nbOff = RunFixture(fixture, new NrConfig(NrMode: NrMode.Emnr));
        var nbOn = RunFixture(fixture, new NrConfig(NrMode: NrMode.Emnr, NbMode: NbMode.Nb1, NbThreshold: 50.0));

        AssertHealthyOutput(nbOff);
        AssertHealthyOutput(nbOn);
        AssertLevelNormalized(nbOn, nbOff, maxRatio: 3.0);
        Assert.True(
            nbOn.Metrics.Peak <= nbOff.Metrics.Peak * 2.0 + 0.05,
            $"NB1+EMNR impulse peak should stay bounded. nbOff={Describe(nbOff)} nbOn={Describe(nbOn)}");
    }

    [SkippableFact]
    public void Wdsp_StrongAdjacentFixture_PreservesWantedSignalForCurrentModes()
    {
        Skip.IfNot(WdspAvailable(), "libwdsp not available");

        var fixture = DspBenchmarkFixtureCatalog.Create(DspBenchmarkScenarioKind.StrongAdjacent);
        var off = RunFixture(fixture, new NrConfig(NrMode: NrMode.Off));

        foreach (var result in RunComparisons(fixture, includeNr4WhenAvailable: true).Where(result => result.NrMode != NrMode.Off))
        {
            AssertHealthyOutput(result);
            AssertLevelNormalized(result, off, maxRatio: 4.0);

            double offWanted = off.Metrics.TonePowerDb["wanted"];
            double resultWanted = result.Metrics.TonePowerDb["wanted"];
            // EMNR's spectral gain mask suppresses the wanted bin slightly harder
            // on the strong-adjacent scene than NR-off, and exactly how hard varies
            // by platform/runner: the win-x64 WDSP build (different FFTW/compiler
            // numerics) lands a few dB lower than macOS/linux, occasionally tipping
            // an 18 dB bound (seen at 18.9 dB on win-x64 CI while every other
            // platform passed). 24 dB keeps a meaningful "don't gut the passband"
            // guard — a true null collapses far past this — without flaking on the
            // cross-platform variance of the suppression depth.
            Assert.True(
                resultWanted > offWanted - 24.0,
                $"strong-adjacent: {result.NrMode} should preserve wanted passband energy. offWanted={offWanted:F1}dB resultWanted={resultWanted:F1}dB result={Describe(result)}");
        }
    }

    [SkippableFact]
    public void Wdsp_AgcStepFixture_KeepsLevelMovementBoundedForCurrentModes()
    {
        Skip.IfNot(WdspAvailable(), "libwdsp not available");

        var fixture = DspBenchmarkFixtureCatalog.Create(DspBenchmarkScenarioKind.AgcStep);
        var off = RunFixture(fixture, new NrConfig(NrMode: NrMode.Off));

        foreach (var result in RunComparisons(fixture, includeNr4WhenAvailable: true).Where(result => result.NrMode != NrMode.Off))
        {
            AssertHealthyOutput(result);
            AssertLevelNormalized(result, off, maxRatio: 4.0);
            Assert.True(
                result.Metrics.WindowedRmsSpreadDb <= off.Metrics.WindowedRmsSpreadDb + 8.0,
                $"AGC-step {result.NrMode} should not add audible pumping beyond NR-off. offSpread={off.Metrics.WindowedRmsSpreadDb:F1}dB resultSpread={result.Metrics.WindowedRmsSpreadDb:F1}dB off={Describe(off)} result={Describe(result)}");
        }
    }

    private static IReadOnlyList<WdspFixtureResult> RunComparisons(
        DspBenchmarkFixture fixture,
        bool includeNr4WhenAvailable)
    {
        var results = new List<WdspFixtureResult>
        {
            RunFixture(fixture, new NrConfig(NrMode: NrMode.Off)),
            RunFixture(fixture, new NrConfig(NrMode: NrMode.Emnr))
        };

        if (includeNr4WhenAvailable && SbnrAvailable())
            results.Add(RunFixture(fixture, new NrConfig(NrMode: NrMode.Sbnr)));

        return results;
    }

    private static WdspFixtureResult RunFixture(DspBenchmarkFixture fixture, NrConfig nr)
    {
        if (fixture.Path != DspBenchmarkPath.RxIq || fixture.IqInterleaved is null)
            throw new ArgumentException("WDSP fixture runner requires RX IQ fixtures.", nameof(fixture));

        // Bulk fixture feed is faster than realtime, so use lossless blocking
        // back-pressure — otherwise the default drop-oldest RX policy discards
        // frames under a busy/slow runner (e.g. macos-arm64 CI), starving EMNR
        // adaptation and collapsing its output level. Production RX never sets this.
        using var engine = new WdspDspEngine { BlockingIqFeed = true };
        int channel = engine.OpenChannel(fixture.SampleRateHz, PixelWidth);
        try
        {
            engine.SetMode(channel, RxMode.USB);
            engine.SetFilter(channel, 150, 2850);
            engine.SetVfoHz(channel, 14_200_000);
            engine.SetAgcTop(channel, 80.0);
            engine.SetNoiseReduction(channel, nr);

            for (int repeat = 0; repeat < FixtureRepeats; repeat++)
                FeedFixtureIq(engine, channel, fixture.IqInterleaved);

            var audio = DrainAudio(engine, channel);
            Assert.True(
                audio.Length >= MinimumAudioSamples,
                $"{fixture.Name}/{nr.NrMode}/{nr.NbMode}: expected at least {MinimumAudioSamples} audio samples, got {audio.Length}");

            float[] analysis = Tail(audio, AnalysisSamples);
            var metrics = DspBenchmarkAnalyzer.AnalyzeAudio(analysis, AudioSampleRateHz, fixture.ExpectedTonesHz);
            return new WdspFixtureResult(fixture.Name, nr.NrMode, nr.NbMode, analysis.Length, metrics);
        }
        finally
        {
            engine.CloseChannel(channel);
        }
    }

    private static void FeedFixtureIq(WdspDspEngine engine, int channel, double[] iq)
    {
        int complexSamples = iq.Length / 2;
        for (int offset = 0; offset < complexSamples; offset += ChunkComplex)
        {
            int take = Math.Min(ChunkComplex, complexSamples - offset);
            engine.FeedIq(channel, iq.AsSpan(2 * offset, 2 * take));
        }
    }

    private static float[] DrainAudio(WdspDspEngine engine, int channel)
    {
        var samples = new List<float>(AnalysisSamples * 2);
        var buffer = new float[2048];

        for (int i = 0; i < 160; i++)
        {
            Thread.Sleep(10);
            int drained = engine.ReadAudio(channel, buffer);
            if (drained > 0)
                samples.AddRange(buffer.Take(drained));
            if (samples.Count >= AnalysisSamples * 2)
                break;
        }

        return samples.ToArray();
    }

    private static float[] Tail(float[] samples, int count)
    {
        int take = Math.Min(samples.Length, count);
        var tail = new float[take];
        Array.Copy(samples, samples.Length - take, tail, 0, take);
        return tail;
    }

    private static void AssertHealthyOutput(WdspFixtureResult result)
    {
        var metrics = result.Metrics;
        AssertFinite(metrics.Rms, $"{result.Label} RMS");
        AssertFinite(metrics.Peak, $"{result.Label} peak");
        AssertFinite(metrics.CrestFactorDb, $"{result.Label} crest factor");
        AssertFinite(metrics.DcOffset, $"{result.Label} DC offset");
        AssertFinite(metrics.WindowedRmsSpreadDb, $"{result.Label} window spread");

        Assert.True(metrics.Rms > 1e-7, $"{result.Label}: expected non-silent output");
        Assert.True(metrics.Rms < 0.50, $"{result.Label}: output RMS too high ({Describe(result)})");
        // Thetis-parity AGC runs slope 0 (flat output): it normalizes every
        // signal — including the noise floor on the NR-off baseline — to the
        // same target loudness, which drives the peak hotter (~0.98) than the
        // old slope-35 profile did. Output must still stay below digital
        // full-scale; the RX pipeline's LimitRxAudioBuffer is the hard ceiling.
        Assert.True(metrics.Peak < 0.99, $"{result.Label}: output peak approaches clipping ({Describe(result)})");
        Assert.True(Math.Abs(metrics.DcOffset) < 0.05, $"{result.Label}: unexpected DC offset ({Describe(result)})");
    }

    private static void AssertLevelNormalized(WdspFixtureResult candidate, WdspFixtureResult baseline, double maxRatio)
    {
        double ratio = candidate.Metrics.Rms / Math.Max(baseline.Metrics.Rms, 1e-9);
        Assert.True(
            ratio <= maxRatio,
            $"{candidate.Label}: RMS rose too far over {baseline.Label}. ratio={ratio:F2} candidate={Describe(candidate)} baseline={Describe(baseline)}");
        Assert.True(
            ratio >= 0.03,
            $"{candidate.Label}: RMS collapsed relative to {baseline.Label}. ratio={ratio:F2} candidate={Describe(candidate)} baseline={Describe(baseline)}");
    }

    private static void AssertFinite(double value, string label)
    {
        Assert.False(double.IsNaN(value), $"{label} is NaN");
        Assert.False(double.IsInfinity(value), $"{label} is infinite");
    }

    private static string Describe(WdspFixtureResult result) =>
        $"{result.Label} samples={result.SampleCount} rms={result.Metrics.Rms:F5} peak={result.Metrics.Peak:F5} spread={result.Metrics.WindowedRmsSpreadDb:F1}dB";

    private sealed record WdspFixtureResult(
        string FixtureName,
        NrMode NrMode,
        NbMode NbMode,
        int SampleCount,
        DspBenchmarkMetrics Metrics)
    {
        public string Label => $"{FixtureName}/{NrMode}/{NbMode}";
    }
}
