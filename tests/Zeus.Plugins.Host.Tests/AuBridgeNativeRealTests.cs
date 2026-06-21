using System.Runtime.InteropServices;
using Zeus.Plugins.Host.Audio;

namespace Zeus.Plugins.Host.Tests;

/// <summary>
/// Exercises <see cref="AuBridgeNative"/> against the actually-built native
/// AU bridge dylib (when present). Mirrors <c>VstBridgeNativeRealTests</c>.
/// The dylib is produced by the CMake build under
/// <c>native/zeus-au-bridge/</c>; the test project's CopyAuBridgeDylib
/// MSBuild target copies it next to the test binary on each build. The AU
/// bridge is macOS-only, so EVERY test here skips off macOS or when the
/// dylib is absent — keeping `dotnet test` green out of the box on every
/// platform.
///
/// Unlike VST3, AUv2 effects are resolved from the OS AudioComponent
/// registry, so we can use Apple's always-present built-ins (AULowpass) as
/// a real, zero-install integration target — no ZEUS_VST_TEST_PATH needed.
/// </summary>
public class AuBridgeNativeRealTests
{
    // Apple AULowpass: type 'aufx', subtype 'lpas', manufacturer 'appl'.
    private const string AULowpassId = "aufx:lpas:appl";

    private static bool OnMac => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    private static bool NativeAvailable()
    {
        if (!OnMac) return false;
        var path = Path.Combine(AppContext.BaseDirectory, "libzeus-au-bridge.dylib");
        return File.Exists(path);
    }

    private static readonly bool SkipBecauseNoNative = !NativeAvailable();
    private const string SkipReason =
        "Native zeus-au-bridge not built or not on macOS — run `cmake -B native/zeus-au-bridge/build && cmake --build native/zeus-au-bridge/build` on macOS.";

    [SkippableFact]
    public void Init_WithCurrentAbi_ReturnsOk()
    {
        Skip.If(SkipBecauseNoNative, SkipReason);
        var bridge = new AuBridgeNative();
        Assert.Equal(VstBridgeStatus.Ok, bridge.Init(VstBridgeAbi.Current));
        bridge.Shutdown();
    }

    [SkippableFact]
    public void Load_MalformedIdentifier_ReturnsNotAnAu()
    {
        Skip.If(SkipBecauseNoNative, SkipReason);
        var bridge = new AuBridgeNative();
        Assert.Equal(VstBridgeStatus.Ok, bridge.Init(VstBridgeAbi.Current));
        try
        {
            // "NotAVst3" == ZAU_NOT_AN_AU (status 3) — missing colon separators.
            var status = bridge.LoadVst3(
                "not-a-valid-au-identifier",
                channels: 1, sampleRate: 48000, blockSize: 256,
                out var handle);
            Assert.Equal(VstBridgeStatus.NotAVst3, status);
            Assert.Equal(IntPtr.Zero, handle);
        }
        finally { bridge.Shutdown(); }
    }

    [SkippableFact]
    public void Load_UnknownComponent_ReturnsFileNotFound()
    {
        Skip.If(SkipBecauseNoNative, SkipReason);
        var bridge = new AuBridgeNative();
        Assert.Equal(VstBridgeStatus.Ok, bridge.Init(VstBridgeAbi.Current));
        try
        {
            // Well-formed effect triple that no installed AU matches.
            var status = bridge.LoadVst3(
                "aufx:zzzz:zzzz",
                channels: 1, sampleRate: 48000, blockSize: 256,
                out var handle);
            Assert.Equal(VstBridgeStatus.FileNotFound, status);
            Assert.Equal(IntPtr.Zero, handle);
        }
        finally { bridge.Shutdown(); }
    }

    [SkippableFact]
    public void Load_InvalidChannelCount_ReturnsInvalidArguments()
    {
        Skip.If(SkipBecauseNoNative, SkipReason);
        var bridge = new AuBridgeNative();
        Assert.Equal(VstBridgeStatus.Ok, bridge.Init(VstBridgeAbi.Current));
        try
        {
            var status = bridge.LoadVst3(
                AULowpassId,
                channels: 99, sampleRate: 48000, blockSize: 256,
                out _);
            Assert.Equal(VstBridgeStatus.InvalidArguments, status);
        }
        finally { bridge.Shutdown(); }
    }

    /// <summary>
    /// Integration test: load Apple's AULowpass, drive its cutoff to the
    /// minimum, push a high-frequency tone through it, and assert the output
    /// is clearly attenuated — proving the AU actually processed the audio
    /// (non-passthrough). This is the AU analogue of the VST3 real-plugin
    /// render test and the regression guard for the render-callback wiring.
    /// </summary>
    [SkippableFact]
    public void Load_AULowpass_AttenuatesHighTone()
    {
        Skip.If(SkipBecauseNoNative, SkipReason);
        var bridge = new AuBridgeNative();
        Assert.Equal(VstBridgeStatus.Ok, bridge.Init(VstBridgeAbi.Current));
        try
        {
            const int channels = 1, frames = 256, sr = 48000;
            var status = bridge.LoadVst3(AULowpassId, channels, sr, frames, out var handle);
            Assert.Equal(VstBridgeStatus.Ok, status);
            Assert.NotEqual(IntPtr.Zero, handle);

            // AULowpass param 0 = cutoff frequency; normalized 0 → lowest cutoff.
            Assert.Equal(VstBridgeStatus.Ok, bridge.SetParameter(handle, 0, 0.0));

            // 12 kHz tone the lowpass should crush.
            var input = new float[channels * frames];
            for (int i = 0; i < frames; i++)
                input[i] = 0.8f * MathF.Sin(2f * MathF.PI * 12000f * i / sr);
            double inRms = Rms(input);

            // Render several blocks so the filter state settles.
            var output = new float[channels * frames];
            int pst = VstBridgeStatus.Ok;
            for (int b = 0; b < 8; b++)
                pst = bridge.Process(handle, input, output, frames);
            Assert.Equal(VstBridgeStatus.Ok, pst);

            double outRms = Rms(output);
            Assert.True(outRms < inRms * 0.7,
                $"expected lowpass to attenuate 12kHz tone: inRms={inRms:F5} outRms={outRms:F5}");

            Assert.Equal(VstBridgeStatus.Ok, bridge.Unload(handle));
        }
        finally { bridge.Shutdown(); }
    }

    [SkippableFact]
    public void Editor_IsNotImplementedForAu()
    {
        Skip.If(SkipBecauseNoNative, SkipReason);
        var bridge = new AuBridgeNative();
        // AUv2 Cocoa editor hosting is out of scope for v1 — matches the
        // non-Windows VST3 behaviour.
        Assert.Equal(VstBridgeStatus.NotImplemented, bridge.EditorOpen(0xABCD, "x"));
        Assert.False(bridge.EditorIsOpen(0xABCD));
    }

    [SkippableFact]
    public void Unload_OnNullHandle_IsNoOp()
    {
        Skip.If(SkipBecauseNoNative, SkipReason);
        var bridge = new AuBridgeNative();
        Assert.Equal(VstBridgeStatus.Ok, bridge.Init(VstBridgeAbi.Current));
        Assert.Equal(VstBridgeStatus.Ok, bridge.Unload(IntPtr.Zero));
        bridge.Shutdown();
    }

    private static double Rms(float[] x)
    {
        double s = 0;
        foreach (var v in x) s += (double)v * v;
        return Math.Sqrt(s / x.Length);
    }
}
