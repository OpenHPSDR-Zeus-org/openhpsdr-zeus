using System.Text.Json;
using Zeus.Plugins.Contracts.Audio;
using Zeus.Plugins.Host.Audio;

namespace Zeus.Plugins.Host.Tests;

/// <summary>
/// Tests for the Zeus side of the VST-engine bridge
/// (<see cref="VstEngineBridge"/> / <see cref="VstEngineProcess"/>).
///
/// The protocol-math + passthrough tests run everywhere (Windows for the SHM
/// ones). The full round-trip integration test is opt-in via
/// <c>ZEUS_VST_ENGINE_PATH</c> pointing at a <c>--zeus-bridge</c>-capable
/// <c>VSTHostEngine.exe</c> (the installed 1.4.0 predates the feature, so we do
/// NOT auto-discover it here — that would run against a stale engine).
/// </summary>
public class VstEngineBridgeTests
{
    // ── Protocol layout (always run) ──────────────────────────────────────────
    [Fact]
    public void Protocol_Layout_IsConsistent()
    {
        Assert.Equal(64, VstEngineProtocol.HeaderBytes);
        Assert.Equal(0x5A565342u, VstEngineProtocol.Magic);

        // mono 512f: region = 512*4 = 2048 (already 64-aligned)
        Assert.Equal(2048, VstEngineProtocol.RegionBytes(512, 1));
        Assert.Equal(64 + 2 * 2048L, VstEngineProtocol.TotalBytes(512, 1));

        // alignment rounds up to 64
        Assert.Equal(64, VstEngineProtocol.AlignUp64(1));
        Assert.Equal(128, VstEngineProtocol.RegionBytes(20, 1)); // 20*4=80 → 128
    }

    private static bool Win => OperatingSystem.IsWindows();
    private const string NotWin = "Shared-memory bridge is Windows-only.";

    private static string UniqueShm() => "zeus-vst-test-" + Guid.NewGuid().ToString("N");

    // ── Passthrough behaviour (Windows, no engine needed) ─────────────────────
    [SkippableFact]
    public void Process_WhenNotReady_PassesThrough()
    {
        Skip.If(!Win, NotWin);
        using var bridge = VstEngineBridge.Create(UniqueShm(), maxFrames: 512, rate: 48000, channels: 1);

        var input = Ramp(512);
        var output = new float[512];
        bridge.EngineReady = false;
        bridge.Process(input, output, Ctx(512, 1));

        Assert.Equal(input, output);
        Assert.Equal(0, bridge.DegradedBlocks); // not-ready isn't a "degraded" round-trip
    }

    [SkippableFact]
    public void Process_WhenReadyButNoEngine_TimesOutToPassthrough()
    {
        Skip.If(!Win, NotWin);
        using var bridge = VstEngineBridge.Create(UniqueShm(), 512, 48000, 1) ;
        bridge.WaitBudgetMs = 2;       // nobody will ever signal .out
        bridge.EngineReady = true;     // ...so this must bounded-wait then passthrough

        var input = Ramp(512);
        var output = new float[512];
        bridge.Process(input, output, Ctx(512, 1));

        Assert.Equal(input, output);                 // radio still fed
        Assert.Equal(1, bridge.DegradedBlocks);      // and we counted the timeout
    }

    [SkippableFact]
    public void Process_OnChannelMismatch_PassesThrough()
    {
        Skip.If(!Win, NotWin);
        using var bridge = VstEngineBridge.Create(UniqueShm(), 512, 48000, channels: 1);
        bridge.EngineReady = true;

        var input = Ramp(2 * 256);
        var output = new float[2 * 256];
        bridge.Process(input, output, Ctx(256, 2)); // 2 channels ≠ bridge's 1

        Assert.Equal(input, output);
        Assert.Equal(0, bridge.DegradedBlocks); // rejected before any round-trip
    }

    // ── Full round-trip against the real engine (opt-in) ──────────────────────
    [SkippableFact]
    public void RoundTrip_AgainstRealEngine_ProcessesBlocks()
    {
        var enginePath = Environment.GetEnvironmentVariable("ZEUS_VST_ENGINE_PATH");
        Skip.If(!Win, NotWin);
        Skip.If(string.IsNullOrWhiteSpace(enginePath) || !File.Exists(enginePath),
            "Set ZEUS_VST_ENGINE_PATH to a --zeus-bridge-capable VSTHostEngine.exe to run.");

        const int frames = 512, rate = 48000, channels = 1;
        var shm = UniqueShm();
        var stderr = new List<string>();

        using var bridge = VstEngineBridge.Create(shm, frames, rate, channels);
        bridge.WaitBudgetMs = 2000; // generous in a test; asserts the PROCESSED path

        using var proc = VstEngineProcess.Launch(enginePath!, shm, frames, rate, channels);
        proc.StdErrLine += s => { lock (stderr) stderr.Add(s); };

        if (!proc.Ready.Wait(TimeSpan.FromSeconds(15)))
            Assert.Fail("engine did not emit 'ready' within 15s. stderr:\n" + string.Join("\n", stderr));

        bridge.EngineReady = true;

        // Drive several blocks; empty chain ⇒ engine passes audio through, but
        // the round-trip must actually happen (DegradedBlocks stays 0).
        var output = new float[frames];
        for (int b = 0; b < 32; b++)
        {
            var input = Ramp(frames, seed: b);
            bridge.Process(input, output, Ctx(frames, channels));
            Assert.Equal(input, output);
        }

        Assert.Equal(0, bridge.DegradedBlocks); // every block got a matching response from the engine
    }

    // ── Real VST loaded + processing through the bridge host (opt-in) ─────────
    [SkippableFact]
    public void RoundTrip_WithRealPlugin_LoadsAndProcesses()
    {
        var enginePath = Environment.GetEnvironmentVariable("ZEUS_VST_ENGINE_PATH");
        var vstPath = Environment.GetEnvironmentVariable("ZEUS_VST_TEST_PATH");
        Skip.If(!Win, NotWin);
        Skip.If(string.IsNullOrWhiteSpace(enginePath) || !File.Exists(enginePath),
            "Set ZEUS_VST_ENGINE_PATH to a --zeus-bridge-capable VSTHostEngine.exe to run.");
        Skip.If(string.IsNullOrWhiteSpace(vstPath) || (!File.Exists(vstPath) && !Directory.Exists(vstPath)),
            "Set ZEUS_VST_TEST_PATH to a real .vst3 to run this test.");

        const int frames = 512, rate = 48000, channels = 2;
        var shm = UniqueShm();
        var stderr = new List<string>();
        var chainLoaded = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var bridge = VstEngineBridge.Create(shm, frames, rate, channels);
        bridge.WaitBudgetMs = 2000; // generous in a test

        using var proc = VstEngineProcess.Launch(enginePath!, shm, frames, rate, channels);
        proc.StdErrLine += s => { lock (stderr) stderr.Add(s); };
        proc.EngineEvent += e =>
        {
            if (!e.TryGetProperty("event", out var ev) || ev.ValueKind != JsonValueKind.String) return;
            switch (ev.GetString())
            {
                case "chain" when e.TryGetProperty("plugins", out var pl)
                                  && pl.ValueKind == JsonValueKind.Array && pl.GetArrayLength() >= 1:
                    chainLoaded.TrySetResult(pl.GetArrayLength());
                    break;
                case "error" when e.TryGetProperty("message", out var m):
                    chainLoaded.TrySetException(new InvalidOperationException("engine error: " + m.GetString()));
                    break;
            }
        };

        if (!proc.Ready.Wait(TimeSpan.FromSeconds(15)))
            Assert.Fail("engine did not emit 'ready' within 15s. stderr:\n" + string.Join("\n", stderr));

        // Load the plugin (empty uid → first plugin in the file).
        proc.Send(new { cmd = "add_plugin", file = vstPath, uid = "" });
        try
        {
            Assert.True(chainLoaded.Task.Wait(TimeSpan.FromSeconds(20)),
                "plugin did not load within 20s. stderr:\n" + string.Join("\n", stderr));
        }
        catch (AggregateException ax)
        {
            Assert.Fail((ax.InnerException?.Message ?? ax.Message) + "\nstderr:\n" + string.Join("\n", stderr));
        }

        // Deterministic, plugin-independent change: -6 dB post-plugin slot gain.
        proc.Send(new { cmd = "set_slot_gain", index = 0, gainDb = -6.0 });
        Thread.Sleep(150); // let the command apply between blocks

        bridge.EngineReady = true;

        var output = new float[frames * channels];
        bool changed = false;
        for (int b = 0; b < 64; b++)
        {
            var input = Ramp(frames * channels, seed: b);
            bridge.Process(input, output, Ctx(frames, channels));
            if (!input.AsSpan().SequenceEqual(output)) changed = true;
        }

        Assert.True(changed, "output never differed from input — the loaded plugin + gain did not process the signal.");
        Assert.Equal(0, bridge.DegradedBlocks); // every block round-tripped through the engine
    }

    // ── helpers ───────────────────────────────────────────────────────────────
    private static float[] Ramp(int n, int seed = 0)
    {
        var a = new float[n];
        for (int i = 0; i < n; i++) a[i] = (float)Math.Sin((i + seed) * 0.01);
        return a;
    }

    private static AudioBlockContext Ctx(int frames, int channels) =>
        new(sampleRate: 48000, channels: channels, frames: frames, sampleTime: 0, mox: true);
}
