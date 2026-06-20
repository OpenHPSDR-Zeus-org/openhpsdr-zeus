// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server;

namespace Zeus.Server.Tests;

/// <summary>
/// issue #733: the native-output cushion was raised 20 ms -> 60 ms to stop the
/// intermittent RX crackle (output-ring underflow splicing silence under
/// DSP-tick jitter), and the underrun/overrun/rebuffer counters are now
/// surfaced via <see cref="NativeAudioSink.GetDiagnostics"/> (and
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

        Assert.Equal(2880, d.PrebufferSamples);   // ~60 ms @ 48 kHz cushion
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
}
