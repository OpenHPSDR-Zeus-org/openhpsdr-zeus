// SPDX-License-Identifier: GPL-2.0-or-later
//
// Phase 2b acceptance gate: ZeusHost.Build must register NativeAudioSink (not
// WebSocketAudioSink) when HostMode=Desktop, and must also register
// NativeMicCapture as an IHostedService so the OS mic feeds TxAudioIngest in
// place of the browser MicPcm WS frames. Server mode must keep
// WebSocketAudioSink and never register the native services.
//
// We resolve via WebApplication.Services (DI introspection) WITHOUT calling
// StartAsync, so the hosted services don't actually open audio devices on
// the test runner. The audio I/O path is exercised separately by RX-only
// smoke on the dev box.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class NativeAudioSinkRegistrationTests
{
    [Fact]
    public async Task ServerMode_RegistersWebSocketAudioSink_AndNoNativeServices()
    {
        var opts = new ZeusHostOptions
        {
            HostMode = ZeusHostMode.Server,
            HttpPort = 0,
            BindAllInterfaces = false,
            UseHttpsLanCert = false,
            PrintConsoleBanner = false,
        };
        var app = ZeusHost.Build(Array.Empty<string>(), opts);

        var sinks = app.Services.GetServices<IRxAudioSink>().ToArray();
        // Server mode keeps the WS sink (host-audio path) and additionally
        // registers RadioCodecAudioSink so RX audio is also routed to the
        // radio's on-board codec on Hermes / ANAN-class / OrionMkII boards
        // (issue #426). HL2 short-circuits inside the codec sink.
        Assert.Equal(2, sinks.Length);
        Assert.Contains(sinks, s => s.GetType().Name == "WebSocketAudioSink");
        Assert.Contains(sinks, s => s.GetType().Name == "RadioCodecAudioSink");

        // Native capture must never be registered in server mode.
        var hosted = app.Services.GetServices<IHostedService>().ToArray();
        Assert.DoesNotContain(hosted, h => h.GetType().Name == "NativeAudioSink");
        Assert.DoesNotContain(hosted, h => h.GetType().Name == "NativeMicCapture");

        await app.DisposeAsync();
    }

    [Fact]
    public async Task DesktopMode_RegistersNativeAudioSink_AndNativeMicCaptureHostedService()
    {
        var opts = new ZeusHostOptions
        {
            HostMode = ZeusHostMode.Desktop,
            HttpPort = 0,
            BindAllInterfaces = false,
            UseHttpsLanCert = false,
            PrintConsoleBanner = false,
        };
        var app = ZeusHost.Build(Array.Empty<string>(), opts);

        var sinks = app.Services.GetServices<IRxAudioSink>().ToArray();
        // Desktop mode swaps the WS path for the native sink, plus the new
        // radio-codec sink (#426). With ShareOverLan=false (default) the
        // total is exactly two: native + codec.
        Assert.Equal(2, sinks.Length);
        Assert.Contains(sinks, s => s.GetType().Name == "NativeAudioSink");
        Assert.Contains(sinks, s => s.GetType().Name == "RadioCodecAudioSink");

        // Same NativeAudioSink instance must also be wired as a hosted
        // service so its StartAsync opens the playback device.
        var hosted = app.Services.GetServices<IHostedService>().ToArray();
        Assert.Contains(hosted, h => h.GetType().Name == "NativeAudioSink");
        Assert.Contains(hosted, h => h.GetType().Name == "NativeMicCapture");

        // Audio Suite audition: desktop mode binds IAuditionAudioSink to
        // the same NativeAudioSink instance so the audition mix happens
        // in the same playback path as RX.
        var audition = app.Services.GetRequiredService<IAuditionAudioSink>();
        var nativeSink = app.Services.GetRequiredService<NativeAudioSink>();
        Assert.Same(nativeSink, audition);

        await app.DisposeAsync();
    }

    [Fact]
    public async Task ServerMode_RegistersNoOpAuditionAudioSink()
    {
        var opts = new ZeusHostOptions
        {
            HostMode = ZeusHostMode.Server,
            HttpPort = 0,
            BindAllInterfaces = false,
            UseHttpsLanCert = false,
            PrintConsoleBanner = false,
        };
        var app = ZeusHost.Build(Array.Empty<string>(), opts);

        var audition = app.Services.GetRequiredService<IAuditionAudioSink>();
        // Browser mode gets the no-op — audition is desktop-only in v1.
        Assert.IsType<NoOpAuditionAudioSink>(audition);
        Assert.False(audition.IsEnabled);

        await app.DisposeAsync();
    }

    [SkippableFact]
    public void MiniAudioInterop_NativeLibraryLoadsAndExposesVersionString()
    {
        // Forces NativeLibrary.SetDllImportResolver + the runtimes/<rid>/native/
        // probe to actually resolve `libminiaudio.{dylib,so,dll}`. Failure here
        // means the native build wasn't staged into Zeus.Dsp/runtimes/, which
        // is the most likely break shape for this phase.
        //
        // Skipped on platforms where no libminiaudio is staged yet — at the
        // time of this commit only osx-arm64 has a committed blob; Linux /
        // Windows binaries will follow once build-native-libs.yml learns to
        // produce them. The other two tests in this file (DI registration)
        // still run everywhere and cover the wiring contract.
        MiniAudioInterop.EnsureResolverRegistered();
        string v;
        try
        {
            v = MiniAudioInterop.Version();
        }
        catch (DllNotFoundException ex)
        {
            Skip.If(true,
                "libminiaudio not staged for this RID; build via " +
                "`native/build.sh miniaudio` and stage into " +
                "Zeus.Dsp/runtimes/<rid>/native/. " + ex.Message);
            return; // unreachable — Skip throws.
        }
        Assert.StartsWith("zeus-miniaudio ", v);
        // Vendored 0.11.x at the time of writing — assert the prefix not the
        // exact patch so a future re-vendor doesn't flap this test.
        Assert.Contains("0.11.", v);
    }
}
