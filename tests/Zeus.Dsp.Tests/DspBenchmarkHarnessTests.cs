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

namespace Zeus.Dsp.Tests;

public class DspBenchmarkHarnessTests
{
    [Fact]
    public void Catalog_CoversModernizationPlanScenarios()
    {
        var kinds = DspBenchmarkFixtureCatalog.All().Select(f => f.Kind).ToHashSet();

        foreach (var kind in Enum.GetValues<DspBenchmarkScenarioKind>())
            Assert.Contains(kind, kinds);
    }

    [Fact]
    public void Fixtures_AreDeterministic()
    {
        foreach (var kind in Enum.GetValues<DspBenchmarkScenarioKind>())
        {
            var first = DspBenchmarkFixtureCatalog.Create(kind);
            var second = DspBenchmarkFixtureCatalog.Create(kind);

            Assert.Equal(first.Name, second.Name);
            Assert.Equal(first.SampleRateHz, second.SampleRateHz);
            Assert.Equal(first.IqInterleaved ?? Array.Empty<double>(), second.IqInterleaved ?? Array.Empty<double>());
            Assert.Equal(first.Audio ?? Array.Empty<float>(), second.Audio ?? Array.Empty<float>());
        }
    }

    [Fact]
    public void AllFixtures_AreFiniteAndHaveExpectedShape()
    {
        foreach (var fixture in DspBenchmarkFixtureCatalog.All())
        {
            Assert.True(fixture.SampleCount > 0, $"{fixture.Name} has no samples");

            var metrics = DspBenchmarkAnalyzer.Analyze(fixture);
            AssertFinite(metrics.Rms, $"{fixture.Name} RMS");
            AssertFinite(metrics.Peak, $"{fixture.Name} peak");
            AssertFinite(metrics.CrestFactorDb, $"{fixture.Name} crest factor");
            AssertFinite(metrics.DcOffset, $"{fixture.Name} DC offset");
            AssertFinite(metrics.WindowedRmsSpreadDb, $"{fixture.Name} window spread");

            Assert.True(metrics.Rms > 0.0, $"{fixture.Name} RMS should be positive");
            Assert.True(metrics.Peak > 0.0, $"{fixture.Name} peak should be positive");
            Assert.True(metrics.Peak < 2.0, $"{fixture.Name} peak should stay below full-scale stress limits");
        }
    }

    [Fact]
    public void WeakCarrier_HasCoherentToneAboveNoiseOnlyAtSameFrequency()
    {
        var weak = DspBenchmarkFixtureCatalog.Create(DspBenchmarkScenarioKind.WeakCarrier);
        var noise = DspBenchmarkFixtureCatalog.Create(DspBenchmarkScenarioKind.NoiseOnly) with
        {
            ExpectedTonesHz = new Dictionary<string, double>(StringComparer.Ordinal) { ["wanted"] = 1_500.0 }
        };

        var weakMetrics = DspBenchmarkAnalyzer.Analyze(weak);
        var noiseMetrics = DspBenchmarkAnalyzer.Analyze(noise);

        Assert.True(
            weakMetrics.TonePowerDb["wanted"] > noiseMetrics.TonePowerDb["wanted"] + 8.0,
            $"weak={weakMetrics.TonePowerDb["wanted"]:F1}dB noise={noiseMetrics.TonePowerDb["wanted"]:F1}dB");
    }

    [Fact]
    public void AgcStep_FixtureExposesLevelMovementForPumpingMetrics()
    {
        var fixture = DspBenchmarkFixtureCatalog.Create(DspBenchmarkScenarioKind.AgcStep);
        var metrics = DspBenchmarkAnalyzer.Analyze(fixture);

        Assert.True(metrics.WindowedRmsSpreadDb > 18.0,
            $"expected a visible level step, got {metrics.WindowedRmsSpreadDb:F1} dB");
    }

    [Fact]
    public void TxTwoTone_PinsExpectedTonePair()
    {
        var fixture = DspBenchmarkFixtureCatalog.Create(DspBenchmarkScenarioKind.TxTwoTone);
        var metrics = DspBenchmarkAnalyzer.Analyze(fixture);
        var audio = fixture.Audio!;

        var probe = DspBenchmarkAnalyzer.AnalyzeAudio(
            audio,
            fixture.SampleRateHz,
            new Dictionary<string, double>(StringComparer.Ordinal) { ["mid"] = 1_200.0 });

        Assert.True(metrics.TonePowerDb["low"] > probe.TonePowerDb["mid"] + 20.0,
            $"low={metrics.TonePowerDb["low"]:F1}dB mid={probe.TonePowerDb["mid"]:F1}dB");
        Assert.True(metrics.TonePowerDb["high"] > probe.TonePowerDb["mid"] + 20.0,
            $"high={metrics.TonePowerDb["high"]:F1}dB mid={probe.TonePowerDb["mid"]:F1}dB");
        Assert.True(Math.Abs(metrics.DcOffset) < 1e-3, $"unexpected DC offset {metrics.DcOffset}");
    }

    [Fact]
    public void StrongAdjacent_CapturesWantedAndBlockerRelationship()
    {
        var fixture = DspBenchmarkFixtureCatalog.Create(DspBenchmarkScenarioKind.StrongAdjacent);
        var metrics = DspBenchmarkAnalyzer.Analyze(fixture);

        Assert.True(metrics.TonePowerDb["adjacent"] > metrics.TonePowerDb["wanted"] + 18.0,
            $"adjacent={metrics.TonePowerDb["adjacent"]:F1}dB wanted={metrics.TonePowerDb["wanted"]:F1}dB");
    }

    private static void AssertFinite(double value, string label)
    {
        Assert.False(double.IsNaN(value), $"{label} is NaN");
        Assert.False(double.IsInfinity(value), $"{label} is infinite");
    }
}
