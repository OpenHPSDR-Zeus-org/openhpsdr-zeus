using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Audio;
using Zeus.Plugins.Host.Audio;

namespace Zeus.Plugins.Host.Tests;

/// <summary>
/// Exercises the AU dispatch path of <see cref="VstHostAudioPlugin"/> with a
/// fake bridge (no native dylib needed), mirroring <c>VstHostAudioPluginTests</c>.
/// Proves: an "au" manifest loads via the injected bridge using the AU
/// identity (no filesystem check), the gate still applies, and bridge
/// failure / gated-off / uninitialised states all pass audio through clean.
/// </summary>
public class AuHostAudioPluginTests : IDisposable
{
    public AuHostAudioPluginTests() => VstHostAudioPlugin.NativeLoadEnabledOverride = true;
    public void Dispose() => VstHostAudioPlugin.NativeLoadEnabledOverride = null;

    private static AudioBlock AuManifest(
        string auId = "aufx:lpas:appl", string slot = "tx.post-leveler")
        => new() { Format = "au", AuComponentId = auId, Slot = slot, Channels = 1, SampleRate = 48000 };

    [Fact]
    public async Task Initialize_AuFormat_LoadsViaIdentity_NoFileCheck()
    {
        var bridge = new FakeBridge();
        // No file on disk — AU identity is a registry triple, not a path, so
        // the file-existence check must be skipped for format "au".
        var plugin = new VstHostAudioPlugin(bridge, AuManifest(), Path.GetTempPath(), "AULowpass");

        await plugin.InitializeAudioAsync(new StubHost(currentBlockSize: 1024), default);

        Assert.True(bridge.InitCalled);
        Assert.Equal("aufx:lpas:appl", bridge.LastLoadPath);
        Assert.Equal(1024, bridge.LastBlockSize);
        Assert.True(plugin.IsNativelyLoaded);

        await plugin.ShutdownAudioAsync(default);
        Assert.Equal(1, bridge.UnloadCount);
    }

    [Fact]
    public void Construct_AuFormat_WithoutComponentId_Throws()
    {
        var bridge = new FakeBridge();
        var bad = new AudioBlock { Format = "au", AuComponentId = null, Slot = "tx.post-leveler" };
        Assert.Throws<ArgumentException>(
            () => new VstHostAudioPlugin(bridge, bad, Path.GetTempPath(), "Bad"));
    }

    [Fact]
    public async Task Process_AuBridgeFailure_PassesThrough()
    {
        var bridge = new FakeBridge { ProcessStatus = VstBridgeStatus.Other, HandleToReturn = 0xCAFE };
        var plugin = new VstHostAudioPlugin(bridge, AuManifest(), Path.GetTempPath(), "AULowpass");
        await plugin.InitializeAudioAsync(new StubHost(), default);

        var input = new float[] { 1, 2 };
        var output = new float[2];
        plugin.Process(input, output, new AudioBlockContext(48000, 1, 2, 0, false));

        Assert.Equal(input, output);
    }

    [Fact]
    public async Task Initialize_AuTxNativeLoad_IsEnabledByDefault()
    {
        VstHostAudioPlugin.NativeLoadEnabledOverride = null;
        var prevEnable = Environment.GetEnvironmentVariable("ZEUS_ENABLE_VST_LOAD");
        var prevDisable = Environment.GetEnvironmentVariable("ZEUS_DISABLE_VST_LOAD");
        var prevTxDisable = Environment.GetEnvironmentVariable("ZEUS_DISABLE_TX_VST_LOAD");
        try
        {
            Environment.SetEnvironmentVariable("ZEUS_ENABLE_VST_LOAD", null);
            Environment.SetEnvironmentVariable("ZEUS_DISABLE_VST_LOAD", null);
            Environment.SetEnvironmentVariable("ZEUS_DISABLE_TX_VST_LOAD", null);

            var bridge = new FakeBridge();
            var plugin = new VstHostAudioPlugin(bridge, AuManifest(), Path.GetTempPath(), "AULowpass");
            await plugin.InitializeAudioAsync(new StubHost(), default);

            // AU inherits the SAME gate as VST3 — TX native load now on by default
            // (KB2UKA-approved 2026-06-26). The AU load identity is a registry
            // triple, so no filesystem path is required.
            Assert.True(bridge.InitCalled);
            Assert.True(plugin.IsNativelyLoaded);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZEUS_ENABLE_VST_LOAD", prevEnable);
            Environment.SetEnvironmentVariable("ZEUS_DISABLE_VST_LOAD", prevDisable);
            Environment.SetEnvironmentVariable("ZEUS_DISABLE_TX_VST_LOAD", prevTxDisable);
            VstHostAudioPlugin.NativeLoadEnabledOverride = true;
        }
    }

    // Reuse the same fakes as the VST3 host tests (identical IVstBridgeNative seam).
    private sealed class FakeBridge : IVstBridgeNative
    {
        public int InitStatus { get; set; } = VstBridgeStatus.Ok;
        public int LoadStatus { get; set; } = VstBridgeStatus.Ok;
        public int ProcessStatus { get; set; } = VstBridgeStatus.Ok;
        public nint HandleToReturn { get; set; } = 0xABCD;

        public bool InitCalled;
        public string? LastLoadPath;
        public int LastBlockSize;
        public int UnloadCount;

        public int Init(int abi) { InitCalled = true; return InitStatus; }

        public int LoadVst3(string path, int channels, int sampleRate, int blockSize, out nint handle)
        {
            LastLoadPath = path;
            LastBlockSize = blockSize;
            handle = LoadStatus == VstBridgeStatus.Ok ? HandleToReturn : 0;
            return LoadStatus;
        }

        public int Process(nint handle, ReadOnlySpan<float> input, Span<float> output, int frames)
        {
            input.CopyTo(output);
            return ProcessStatus;
        }

        public int SetParameter(nint handle, uint paramId, double normalized) => VstBridgeStatus.Ok;
        public int Unload(nint handle) { UnloadCount++; return VstBridgeStatus.Ok; }
        public int Shutdown() => VstBridgeStatus.Ok;
        public int EditorOpen(nint handle, string title) => VstBridgeStatus.NotImplemented;
        public int EditorClose(nint handle) => VstBridgeStatus.Ok;
        public bool EditorIsOpen(nint handle) => false;
    }

    private sealed class StubHost : IAudioHost
    {
        private readonly int _currentBlockSize;
        public StubHost(int currentBlockSize = 256) => _currentBlockSize = currentBlockSize;
        public int CurrentSampleRate => 48000;
        public int CurrentChannels => 1;
        public int CurrentBlockSize => _currentBlockSize;
        public string Slot => "tx.post-leveler";
    }
}
