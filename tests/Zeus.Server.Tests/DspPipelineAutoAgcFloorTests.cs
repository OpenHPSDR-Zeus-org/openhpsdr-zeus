// SPDX-License-Identifier: GPL-2.0-or-later

using Zeus.Server;

namespace Zeus.Server.Tests;

/// <summary>
/// Unit coverage for the Auto-AGC noise-floor percentile (#806). Exercises the
/// pure <see cref="DspPipelineService.TryNoiseFloorFromDisplayBins"/> directly —
/// the integration path injects a panadapter snapshot, so this is where the
/// sentinel filtering, the min-valid-bins gate, and the percentile math are
/// pinned down.
/// </summary>
public sealed class DspPipelineAutoAgcFloorTests
{
    [Fact]
    public void NoiseFloor_RejectsWhenTooFewValidBins()
    {
        // All-invalid span (the -200 sentinel from SanitizeDisplayBuffer) → no
        // trustworthy floor.
        var bins = new float[2048];
        Array.Fill(bins, -200f);

        bool ok = DspPipelineService.TryNoiseFloorFromDisplayBins(
            bins, percentile: 0.20, minValidBins: 64, out double floor);

        Assert.False(ok);
        Assert.True(double.IsNaN(floor));
    }

    [Fact]
    public void NoiseFloor_FiltersSentinelBins_AndReturnsValidPercentile()
    {
        // 100 real bins at -110 dB surrounded by -200 sentinels: the floor must
        // ignore the sentinels and land on the real noise.
        var bins = new float[2048];
        Array.Fill(bins, -200f);
        for (int i = 0; i < 100; i++) bins[i * 5] = -110f;

        bool ok = DspPipelineService.TryNoiseFloorFromDisplayBins(
            bins, percentile: 0.20, minValidBins: 64, out double floor);

        Assert.True(ok);
        Assert.Equal(-110.0, floor);
    }

    [Fact]
    public void NoiseFloor_LowPercentile_PicksNoiseNotSignals()
    {
        // 100 bins ramped -120..-21 dB (signals are the high bins). The 20th
        // percentile must sit down in the noise: sorted idx = round(99*0.2)=20 →
        // value -120+20 = -100.
        var bins = new float[100];
        for (int i = 0; i < bins.Length; i++) bins[i] = -120f + i;

        bool ok = DspPipelineService.TryNoiseFloorFromDisplayBins(
            bins, percentile: 0.20, minValidBins: 64, out double floor);

        Assert.True(ok);
        Assert.Equal(-100.0, floor);
    }

    [Fact]
    public void NoiseFloor_MinValidBins_IsInclusiveBoundary()
    {
        var exactly = new float[2048];
        Array.Fill(exactly, -200f);
        for (int i = 0; i < 64; i++) exactly[i] = -105f;
        Assert.True(DspPipelineService.TryNoiseFloorFromDisplayBins(
            exactly, 0.20, 64, out _));

        var oneShort = new float[2048];
        Array.Fill(oneShort, -200f);
        for (int i = 0; i < 63; i++) oneShort[i] = -105f;
        Assert.False(DspPipelineService.TryNoiseFloorFromDisplayBins(
            oneShort, 0.20, 64, out _));
    }
}
