using Zeus.Plugins.Contracts.Audio;
using Zeus.Plugins.Host.Audio;

namespace Zeus.Plugins.Host.Tests;

/// <summary>
/// Tests for <see cref="VstEngineController"/> — the public lifecycle + realtime
/// facade over the VST-engine bridge.
///
/// The "inactive = passthrough" contract runs everywhere (no shared memory is
/// created until activation, so it's platform-safe). The real-engine activation
/// test is opt-in via <c>ZEUS_VST_ENGINE_PATH</c> pointing at a
/// <c>--zeus-bridge</c>-capable <c>VSTHostEngine.exe</c>.
/// </summary>
public class VstEngineControllerTests
{
    private static AudioBlockContext Ctx(int frames, int channels) =>
        new(sampleRate: 48000, channels: channels, frames: frames, sampleTime: 0, mox: true);

    private static float[] Ramp(int n, int seed = 0)
    {
        var a = new float[n];
        for (int i = 0; i < n; i++) a[i] = (float)Math.Sin((i + seed) * 0.01);
        return a;
    }

    // ── Inactive (no engine, any platform) ────────────────────────────────────
    [Fact]
    public async Task Process_WhenNotActivated_PassesThrough()
    {
        await using var controller = new VstEngineController(maxFrames: 512, rate: 48000, channels: 1);

        Assert.False(controller.IsActive);

        var input = Ramp(512);
        var output = new float[512];
        controller.Process(input, output, Ctx(512, 1));

        // Bit-identical copy — the realtime path never touched the (uncreated) engine.
        Assert.Equal(input, output);
        Assert.Equal(0, controller.DegradedBlocks);
    }

    [Fact]
    public async Task Deactivate_BeforeActivate_IsNoOp()
    {
        await using var controller = new VstEngineController(512, 48000, 1);
        controller.Deactivate();           // must not throw, must not create anything
        Assert.False(controller.IsActive);
    }

    // ── Full activation against the real engine (opt-in) ──────────────────────
    [SkippableFact]
    public async Task Activate_AgainstRealEngine_RoutesThenDeactivates()
    {
        var enginePath = Environment.GetEnvironmentVariable("ZEUS_VST_ENGINE_PATH");
        Skip.If(!OperatingSystem.IsWindows(), "Shared-memory bridge is Windows-only.");
        Skip.If(string.IsNullOrWhiteSpace(enginePath) || !File.Exists(enginePath),
            "Set ZEUS_VST_ENGINE_PATH to a --zeus-bridge-capable VSTHostEngine.exe to run.");

        const int frames = 512, channels = 1;
        await using var controller = new VstEngineController(frames, 48000, channels) { WaitBudgetMs = 2000 };

        var start = await controller.ActivateAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(VstEngineStartResult.Started, start);
        Assert.True(controller.IsActive);

        // Empty chain ⇒ engine passes audio through, but the round-trip must
        // actually happen (DegradedBlocks stays 0 — no timeouts / stale reads).
        var output = new float[frames];
        for (int b = 0; b < 16; b++)
        {
            var input = Ramp(frames, seed: b);
            controller.Process(input, output, Ctx(frames, channels));
            Assert.Equal(input, output);
        }
        Assert.Equal(0, controller.DegradedBlocks);

        // Switching back to Native: the tap stops touching the engine and
        // becomes a clean passthrough immediately.
        controller.Deactivate();
        Assert.False(controller.IsActive);

        var afterIn = Ramp(frames, seed: 99);
        var afterOut = new float[frames];
        controller.Process(afterIn, afterOut, Ctx(frames, channels));
        Assert.Equal(afterIn, afterOut);
    }
}
