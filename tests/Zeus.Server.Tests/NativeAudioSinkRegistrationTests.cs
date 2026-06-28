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

public class NativeAudioSinkRegistrationTests : IDisposable
{
    // ZeusHost.Build constructs the full host DI graph, which opens zeus-prefs.db
    // through a dozen-plus Connection=shared LiteDB stores (WSJT-X / spotting /
    // operator-identity / FT8-settings / HamClock / cloud-log, added with the
    // digital suite). Isolate to a unique throw-away prefs file so those opens
    // never contend with the shared default DB under xUnit's parallel test
    // classes — on Windows that contention surfaced as a LiteDB exclusive-lock
    // IOException at WsjtxConfigStore.Get(). Mirrors SavedLayoutsStoreTests.
    private readonly string _dbPath;
    private readonly string? _previousPrefs;

    public NativeAudioSinkRegistrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zeus-prefs-nativeaudio-{Guid.NewGuid():N}.db");
        _previousPrefs = Environment.GetEnvironmentVariable("ZEUS_PREFS_PATH");
        Environment.SetEnvironmentVariable("ZEUS_PREFS_PATH", _dbPath);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ZEUS_PREFS_PATH", _previousPrefs);
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (File.Exists(_dbPath + "-log")) File.Delete(_dbPath + "-log"); } catch { }
    }

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
        // Server mode keeps the WS sink — bit-for-bit equivalent of the
        // pre-seam direct hub broadcast.
        Assert.Contains(sinks, s => s.GetType().Name == "WebSocketAudioSink");
        // Plus the Protocol-1 radio-speaker sink, registered in BOTH host modes
        // (a headless Zeus next to a P1 radio can drive its codec speaker). It's
        // a managed ring-feeder, not a native audio device, and self-gates to
        // off-by-default, so it never opens hardware on its own.
        Assert.Contains(sinks, s => s.GetType().Name == "RadioSpeakerAudioSink");
        Assert.DoesNotContain(sinks, s => s.GetType().Name == "NativeAudioSink");
        Assert.DoesNotContain(sinks, s => s.GetType().Name == "SaturnSpeakerAudioSink");

        // Native capture / device-owning services must never be registered in
        // server mode. RadioSpeakerAudioSink is NOT among them — it owns no
        // device and is not a hosted service.
        var hosted = app.Services.GetServices<IHostedService>().ToArray();
        Assert.DoesNotContain(hosted, h => h.GetType().Name == "NativeAudioSink");
        Assert.DoesNotContain(hosted, h => h.GetType().Name == "SaturnSpeakerAudioSink");
        Assert.DoesNotContain(hosted, h => h.GetType().Name == "RadioSpeakerAudioSink");
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
        // Desktop mode swaps in the native sink so RX audio goes straight to
        // the OS default output device (Phase 2b).
        Assert.Contains(sinks, sink => sink.GetType().Name == "NativeAudioSink");
        Assert.Contains(sinks, sink => sink.GetType().Name == "SaturnSpeakerAudioSink");
        Assert.Contains(sinks, sink => sink.GetType().Name == "GatedWebSocketAudioSink");
        // P1 radio-speaker sink is present in desktop mode too (both modes).
        Assert.Contains(sinks, sink => sink.GetType().Name == "RadioSpeakerAudioSink");
        Assert.DoesNotContain(sinks, sink => sink.GetType().Name == "WebSocketAudioSink");

        // Same NativeAudioSink instance must also be wired as a hosted
        // service so its StartAsync opens the playback device.
        var hosted = app.Services.GetServices<IHostedService>().ToArray();
        Assert.Contains(hosted, h => h.GetType().Name == "NativeAudioSink");
        Assert.Contains(hosted, h => h.GetType().Name == "SaturnSpeakerAudioSink");
        Assert.Contains(hosted, h => h.GetType().Name == "NativeMicCapture");

        // Local side-channel playback: desktop mode binds IPreviewAudioSink
        // to the same NativeAudioSink instance so published mono monitor
        // samples share the RX playback path.
        var preview = app.Services.GetRequiredService<IPreviewAudioSink>();
        var nativeSink = app.Services.GetRequiredService<NativeAudioSink>();
        Assert.Same(nativeSink, preview);

        await app.DisposeAsync();
    }

    [Fact]
    public async Task ServerMode_RegistersNoOpPreviewAudioSink()
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

        var preview = app.Services.GetRequiredService<IPreviewAudioSink>();
        // Browser mode gets the no-op local side-channel implementation.
        Assert.IsType<NoOpPreviewAudioSink>(preview);
        Assert.False(preview.IsEnabled);

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
