// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server;

namespace Zeus.Server.Tests;

/// <summary>
/// issue #733/#742: the native-output cushion stops intermittent RX crackle
/// (output-ring underflow splicing silence under DSP-tick jitter). It was
/// raised 20 ms -> 60 ms (#733) and then to a 120 ms floor that is also made
/// adaptive to the device's real callback period (#742, the dominant cause: a
/// bursty ~33 ms producer vs a steady device drain, a thin cushion, and a
/// possibly-large negotiated period). The underrun/overrun/rebuffer counters
/// are surfaced via <see cref="NativeAudioSink.GetDiagnostics"/> (and
/// /api/audio/native) so the condition is measurable rather than log-only.
/// </summary>
public sealed class NativeAudioSinkDiagnosticsTests
{
    [Fact]
    public void GetDiagnostics_FreshSink_ReportsCushionAndZeroCounters()
    {
        // Constructing the sink does NOT open an audio device (that happens in
        // StartAsync), so this is safe in a headless test.
        using var sink = new NativeAudioSink(NullLogger<NativeAudioSink>.Instance);

        var d = sink.GetDiagnostics();

        Assert.Equal(5760, d.PrebufferSamples);   // ~120 ms @ 48 kHz floor (#742)
        Assert.Equal(48_000, d.SampleRateHz);
        Assert.Equal(65_536, d.RingCapacitySamples);
        Assert.Equal(0L, d.UnderrunSamplesTotal);
        Assert.Equal(0L, d.OverrunSamplesTotal);
        Assert.Equal(0L, d.RebufferEvents);
        Assert.Equal(0, d.RingDepthSamples);
        // A fresh sink starts in the rebuffering state — it holds silence until
        // the prebuffer cushion fills before (re)starting playback.
        Assert.True(d.Rebuffering);
    }

    [Theory]
    // Small/typical device period (10 ms @ 48 kHz): the 120 ms floor dominates.
    [InlineData(480, 5760)]
    [InlineData(960, 5760)]
    [InlineData(1024, 5760)]
    // Large negotiated period: cushion grows to 4× the callback so a big-period
    // default device can't outrun a thin fixed cushion (#742 hypothesis #2).
    [InlineData(2048, 8192)]
    [InlineData(4096, 16384)]
    // Degenerate inputs stay safe (never negative, never >= ring capacity).
    [InlineData(0, 5760)]
    public void ComputePrebufferTarget_IsFloorOrFourCallbacks_Clamped(int totalFrames, int expected)
    {
        Assert.Equal(expected, NativeAudioSink.ComputePrebufferTarget(totalFrames));
    }

    [Fact]
    public void ComputePrebufferTarget_NeverMeetsOrExceedsRingCapacity()
    {
        // A target >= capacity would wedge the sink in permanent rebuffering.
        Assert.True(NativeAudioSink.ComputePrebufferTarget(40_000) <= 65_536 - 40_000);
        Assert.True(NativeAudioSink.ComputePrebufferTarget(60_000) < 65_536);
    }
}
