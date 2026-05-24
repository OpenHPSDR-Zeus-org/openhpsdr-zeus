// SPDX-License-Identifier: GPL-2.0-or-later
//
// Regression coverage for the WASAPI low-latency output fix (issue #468).
//
// Root cause: WASAPI shared-mode low-latency (IAudioClient3) is silently
// disabled when the app sample rate differs from the device native rate,
// because miniaudio then sets AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM which
// IAudioClient3 rejects — so it falls back to the DEFAULT shared-mode buffer,
// which on RDP / non-48 kHz endpoints is ~1-2 SECONDS deep. That deep output
// buffer was the entire PublishAudio→audible gap the t4 resume probe measured
// (+1677 ms vs the backend delivering audio in ~22 ms).
//
// Fix: native/miniaudio/zeus_miniaudio.c sets cfg.wasapi.noAutoConvertSRC =
// MA_TRUE on the playback (and capture) device, which keeps IAudioClient3
// happy so low-latency shared mode engages and the buffer collapses to the
// requested ~20 ms. The device's rate conversion is handled by miniaudio's
// internal (linear) resampler instead. WASAPI-only field — no-op on
// CoreAudio / ALSA — so this must NOT break device init on macOS / Linux.
//
// These tests pin the cross-platform-safety half of the fix: the rebuilt
// miniaudio device still opens and reports a sane rate on this platform.
// They cannot assert the Windows buffer depth (no WASAPI here) — that is
// confirmed on the operator's bench via the rx.resume.probe t4 line, which
// must drop from ~1677 ms to tens of ms.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class MiniAudioLowLatencyConfigTests
{
    [SkippableFact]
    public void OutputDevice_OpensAndReportsSaneRate_WithLowLatencyConfig()
    {
        MiniAudioInterop.EnsureResolverRegistered();

        // No device / no staged native lib (headless CI) → skip. The point is
        // to prove the noAutoConvertSRC config path doesn't break device init
        // on a real backend, not to require one.
        MiniAudioOutput? output = null;
        try
        {
            output = new MiniAudioOutput(
                onFrames: (_, _, _) => { /* silence is fine for the open test */ },
                onNotify: null,
                preferSampleRate: 48_000,
                preferChannels: 2,
                periodFrames: 480,
                periods: 2);
        }
        catch (DllNotFoundException ex)
        {
            Skip.If(true, "libminiaudio not staged for this RID. " + ex.Message);
            return;
        }
        catch (InvalidOperationException ex)
        {
            // No default playback device on the runner (headless CI).
            Skip.If(true, "no playback device available: " + ex.Message);
            return;
        }

        try
        {
            // Device negotiated a rate — proves init succeeded with
            // noAutoConvertSRC set (the macOS / Linux backends ignore the flag,
            // but the config must still produce a working device).
            Assert.True(output.SampleRate >= 8_000 && output.SampleRate <= 768_000,
                $"negotiated rate {output.SampleRate} Hz is implausible");
            Assert.True(output.Channels >= 1, "device reported zero channels");

            // Start + stop must round-trip cleanly — the device runs as ONE
            // continuous stream (no per-TX stop/start in the fix).
            output.Start();
            Assert.True(output.IsRunning);
            output.Stop();
            Assert.False(output.IsRunning);
        }
        finally
        {
            output.Dispose();
        }
    }

    [Fact]
    public void NativeAudioSink_TxRxEdges_DoNotThrow_WithoutDevice()
    {
        // Belt-and-suspenders for the resume-probe arming on the TX→RX edge:
        // with no real device (output == null) the edge handlers must stay
        // safe — clear the ring, arm the probe, never throw.
        var sink = new NativeAudioSink(NullLogger<NativeAudioSink>.Instance);
        var ex = Record.Exception(() =>
        {
            sink.OnTxActiveChanged(true);
            sink.OnTxActiveChanged(false);
        });
        Assert.Null(ex);
        Assert.Equal(0, sink.CurrentRingDepth);
    }
}
