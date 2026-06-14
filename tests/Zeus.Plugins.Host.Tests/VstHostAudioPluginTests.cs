using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Audio;
using Zeus.Plugins.Host.Audio;

namespace Zeus.Plugins.Host.Tests;

public class VstHostAudioPluginTests : IDisposable
{
    // Native load is gated OFF by default (real .vst3 loads can crash the
    // bridge until it's hardened). These tests exercise the load path with
    // a fake bridge, so opt in for the class lifetime, then reset.
    public VstHostAudioPluginTests() => VstHostAudioPlugin.NativeLoadEnabledOverride = true;
    public void Dispose() => VstHostAudioPlugin.NativeLoadEnabledOverride = null;

    private static AudioBlock AudioManifest(string vst3Path = "vst3/Fake.vst3")
        => new() { Vst3Path = vst3Path, Slot = "tx.post-leveler", Channels = 1, SampleRate = 48000 };

    [Fact]
    public async Task Initialize_HappyPath_CallsLoadVst3()
    {
        var bridge = new FakeBridge();
        var pluginDir = Path.GetTempPath();
        var vst3Abs = Path.Combine(pluginDir, "vst3", "Fake.vst3");
        Directory.CreateDirectory(Path.GetDirectoryName(vst3Abs)!);
        File.WriteAllText(vst3Abs, "stub");
        try
        {
            var plugin = new VstHostAudioPlugin(bridge, AudioManifest(), pluginDir, "FakeFx");
            await plugin.InitializeAudioAsync(new StubHost(), default);

            Assert.True(bridge.InitCalled);
            // VstHostAudioPlugin builds the absolute path via Path.Combine
            // with the manifest's vst3Path "vst3/Fake.vst3". On Windows
            // that mixes forward+back slashes; canonicalise both sides
            // before comparing.
            Assert.Equal(
                Path.GetFullPath(vst3Abs),
                Path.GetFullPath(bridge.LastLoadPath!));
            await plugin.ShutdownAudioAsync(default);
            Assert.Equal(1, bridge.UnloadCount);
        }
        finally
        {
            File.Delete(vst3Abs);
        }
    }

    [Fact]
    public async Task Initialize_MissingVst3_Throws()
    {
        var bridge = new FakeBridge();
        var plugin = new VstHostAudioPlugin(
            bridge, AudioManifest("vst3/Missing.vst3"), Path.GetTempPath(), "FakeFx");

        await Assert.ThrowsAsync<PluginLoadException>(
            () => plugin.InitializeAudioAsync(new StubHost(), default));
    }

    [Fact]
    public async Task Initialize_BridgeAbiMismatch_Throws()
    {
        var bridge = new FakeBridge { InitStatus = VstBridgeStatus.AbiMismatch };
        var plugin = new VstHostAudioPlugin(bridge, AudioManifest(), Path.GetTempPath(), "FakeFx");

        await Assert.ThrowsAsync<PluginLoadException>(
            () => plugin.InitializeAudioAsync(new StubHost(), default));
    }

    [Fact]
    public async Task Process_BeforeInitialise_PassesThrough()
    {
        var bridge = new FakeBridge();
        var plugin = new VstHostAudioPlugin(bridge, AudioManifest(), Path.GetTempPath(), "FakeFx");

        var input  = new float[] { 1, 2, 3 };
        var output = new float[3];
        plugin.Process(input, output,
            new AudioBlockContext(48000, 1, 3, 0, false));

        Assert.Equal(input, output);
        Assert.Equal(0, bridge.ProcessCount);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Process_BridgeFailure_PassesThrough()
    {
        var bridge = new FakeBridge
        {
            ProcessStatus = VstBridgeStatus.Other,
            HandleToReturn = 0xCAFE,
        };
        var pluginDir = Path.GetTempPath();
        var vst3Abs = Path.Combine(pluginDir, "vst3", "Fake.vst3");
        Directory.CreateDirectory(Path.GetDirectoryName(vst3Abs)!);
        File.WriteAllText(vst3Abs, "stub");
        try
        {
            var plugin = new VstHostAudioPlugin(bridge, AudioManifest(), pluginDir, "FakeFx");
            await plugin.InitializeAudioAsync(new StubHost(), default);

            var input  = new float[] { 1, 2 };
            var output = new float[2];
            plugin.Process(input, output,
                new AudioBlockContext(48000, 1, 2, 0, false));

            Assert.Equal(input, output);
        }
        finally
        {
            File.Delete(vst3Abs);
        }
    }

    private sealed class FakeBridge : IVstBridgeNative
    {
        public int InitStatus { get; set; } = VstBridgeStatus.Ok;
        public int LoadStatus { get; set; } = VstBridgeStatus.Ok;
        public int ProcessStatus { get; set; } = VstBridgeStatus.Ok;
        public nint HandleToReturn { get; set; } = 0xABCD;

        public bool InitCalled;
        public string? LastLoadPath;
        public int ProcessCount;
        public int UnloadCount;

        public int Init(int abi)
        {
            InitCalled = true;
            return InitStatus;
        }

        public int LoadVst3(string path, int channels, int sampleRate, int blockSize, out nint handle)
        {
            LastLoadPath = path;
            handle = LoadStatus == VstBridgeStatus.Ok ? HandleToReturn : 0;
            return LoadStatus;
        }

        public int Process(nint handle, ReadOnlySpan<float> input, Span<float> output, int frames)
        {
            ProcessCount++;
            input.CopyTo(output);
            return ProcessStatus;
        }

        public int SetParameter(nint handle, uint paramId, double normalized) => VstBridgeStatus.Ok;

        public int Unload(nint handle)
        {
            UnloadCount++;
            return VstBridgeStatus.Ok;
        }

        public int Shutdown() => VstBridgeStatus.Ok;

        public int EditorOpen(nint handle, string title) { EditorOpenCount++; return VstBridgeStatus.Ok; }
        public int EditorClose(nint handle) { EditorCloseCount++; return VstBridgeStatus.Ok; }
        public bool EditorIsOpen(nint handle) => false;

        public int EditorOpenCount;
        public int EditorCloseCount;
    }

    private sealed class StubHost : IAudioHost
    {
        public int CurrentSampleRate => 48000;
        public int CurrentChannels => 1;
        public int CurrentBlockSize => 256;
        public string Slot => "tx.post-leveler";
    }
}
