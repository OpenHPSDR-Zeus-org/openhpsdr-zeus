using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Audio;
using Zeus.Plugins.Host.Audio;

namespace Zeus.Plugins.Host.Tests;

// Mutates the process-global VstHostAudioPlugin.NativeLoadEnabledOverride
// static and the ZEUS_*_VST_LOAD env vars. Serialise with the other
// load-gate tests so a sibling class's ctor can't reassert the override
// mid-test (that race surfaced as an intermittent windows-arm64 flake:
// the kill-switch test fell through to file validation and threw
// "VST3 path not found").
[Collection("LoadSensitive")]
public class VstHostAudioPluginTests : IDisposable
{
    // Native load is gated OFF by default (real .vst3 loads can crash the
    // bridge until it's hardened). These tests exercise the load path with
    // a fake bridge, so opt in for the class lifetime, then reset.
    public VstHostAudioPluginTests() => VstHostAudioPlugin.NativeLoadEnabledOverride = true;
    public void Dispose() => VstHostAudioPlugin.NativeLoadEnabledOverride = null;

    private static AudioBlock AudioManifest(string vst3Path = "vst3/Fake.vst3", string slot = "tx.post-leveler")
        => new() { Vst3Path = vst3Path, Slot = slot, Channels = 1, SampleRate = 48000 };

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
            Assert.Equal(1024, plugin.Requirements.BlockSize);
            await plugin.InitializeAudioAsync(new StubHost(currentBlockSize: 1024), default);

            Assert.True(bridge.InitCalled);
            // VstHostAudioPlugin builds the absolute path via Path.Combine
            // with the manifest's vst3Path "vst3/Fake.vst3". On Windows
            // that mixes forward+back slashes; canonicalise both sides
            // before comparing.
            Assert.Equal(
                Path.GetFullPath(vst3Abs),
                Path.GetFullPath(bridge.LastLoadPath!));
            Assert.Equal(1024, bridge.LastBlockSize);
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
        public int LatencyToReturn { get; set; }

        public bool InitCalled;
        public string? LastLoadPath;
        public int LastBlockSize;
        public int LastSampleRate;
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
            LastBlockSize = blockSize;
            LastSampleRate = sampleRate;
            handle = LoadStatus == VstBridgeStatus.Ok ? HandleToReturn : 0;
            return LoadStatus;
        }

        public int GetLatencySamples(nint handle) => LatencyToReturn;

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
        private readonly int _currentBlockSize;

        private readonly int _currentSampleRate;

        public StubHost(int currentBlockSize = 256, int currentSampleRate = 48000)
        {
            _currentBlockSize = currentBlockSize;
            _currentSampleRate = currentSampleRate;
        }

        public int CurrentSampleRate => _currentSampleRate;
        public int CurrentChannels => 1;
        public int CurrentBlockSize => _currentBlockSize;
        public string Slot => "tx.post-leveler";
    }

    [Fact]
    public async Task Initialize_CapturesReportedLatency()
    {
        // ABI v3: the plugin surfaces the bridge's reported processing latency.
        var bridge = new FakeBridge { HandleToReturn = 0xCAFE, LatencyToReturn = 2238 };
        var pluginDir = Path.GetTempPath();
        var vst3Abs = Path.Combine(pluginDir, "vst3", "Fake.vst3");
        Directory.CreateDirectory(Path.GetDirectoryName(vst3Abs)!);
        File.WriteAllText(vst3Abs, "stub");
        try
        {
            var plugin = new VstHostAudioPlugin(bridge, AudioManifest(), pluginDir, "FakeFx");
            Assert.Equal(0, plugin.ReportedLatencySamples); // before load
            await plugin.InitializeAudioAsync(new StubHost(currentBlockSize: 1024), default);
            Assert.Equal(2238, plugin.ReportedLatencySamples);
        }
        finally { File.Delete(vst3Abs); }
    }

    [Fact]
    public async Task Initialize_LoadsAtHostSampleRate_NotManifestRate()
    {
        // The plugin must run at the host's ACTUAL processing rate, not the
        // manifest's declared rate, or its time-based DSP is detuned.
        var bridge = new FakeBridge();
        var pluginDir = Path.GetTempPath();
        var vst3Abs = Path.Combine(pluginDir, "vst3", "Fake.vst3");
        Directory.CreateDirectory(Path.GetDirectoryName(vst3Abs)!);
        File.WriteAllText(vst3Abs, "stub");
        try
        {
            // Manifest declares 48000; host reports 44100 → host wins.
            var plugin = new VstHostAudioPlugin(bridge, AudioManifest(), pluginDir, "FakeFx");
            await plugin.InitializeAudioAsync(
                new StubHost(currentBlockSize: 1024, currentSampleRate: 44100), default);
            Assert.Equal(44100, bridge.LastSampleRate);
        }
        finally { File.Delete(vst3Abs); }
    }

    [Fact]
    public async Task Initialize_TxNativeLoad_IsEnabledByDefault()
    {
        // TX native load now defaults on (KB2UKA-approved 2026-06-26). With no
        // env overrides a tx.* slot loads natively, same as rx.*.
        VstHostAudioPlugin.NativeLoadEnabledOverride = null;
        var previousEnable = Environment.GetEnvironmentVariable("ZEUS_ENABLE_VST_LOAD");
        var previousDisable = Environment.GetEnvironmentVariable("ZEUS_DISABLE_VST_LOAD");
        var previousTxDisable = Environment.GetEnvironmentVariable("ZEUS_DISABLE_TX_VST_LOAD");
        var bridge = new FakeBridge();
        var pluginDir = Path.GetTempPath();
        var vst3Abs = Path.Combine(pluginDir, "vst3", "FakeTx.vst3");
        Directory.CreateDirectory(Path.GetDirectoryName(vst3Abs)!);
        File.WriteAllText(vst3Abs, "stub");
        try
        {
            Environment.SetEnvironmentVariable("ZEUS_ENABLE_VST_LOAD", null);
            Environment.SetEnvironmentVariable("ZEUS_DISABLE_VST_LOAD", null);
            Environment.SetEnvironmentVariable("ZEUS_DISABLE_TX_VST_LOAD", null);

            var plugin = new VstHostAudioPlugin(
                bridge, AudioManifest("vst3/FakeTx.vst3", "tx.post-leveler"), pluginDir, "FakeTx");

            await plugin.InitializeAudioAsync(new StubHost(currentBlockSize: 2048), default);

            Assert.True(bridge.InitCalled);
            Assert.True(plugin.IsNativelyLoaded);
        }
        finally
        {
            File.Delete(vst3Abs);
            Environment.SetEnvironmentVariable("ZEUS_ENABLE_VST_LOAD", previousEnable);
            Environment.SetEnvironmentVariable("ZEUS_DISABLE_VST_LOAD", previousDisable);
            Environment.SetEnvironmentVariable("ZEUS_DISABLE_TX_VST_LOAD", previousTxDisable);
            VstHostAudioPlugin.NativeLoadEnabledOverride = null;
        }
    }

    [Fact]
    public async Task Initialize_TxNativeLoad_DisabledBy_TxKillSwitch()
    {
        // ZEUS_DISABLE_TX_VST_LOAD=1 falls a tx.* slot back to passthrough
        // (crash-isolated out-of-process engine) without touching rx.*.
        VstHostAudioPlugin.NativeLoadEnabledOverride = null;
        var previousEnable = Environment.GetEnvironmentVariable("ZEUS_ENABLE_VST_LOAD");
        var previousDisable = Environment.GetEnvironmentVariable("ZEUS_DISABLE_VST_LOAD");
        var previousTxDisable = Environment.GetEnvironmentVariable("ZEUS_DISABLE_TX_VST_LOAD");
        try
        {
            Environment.SetEnvironmentVariable("ZEUS_ENABLE_VST_LOAD", null);
            Environment.SetEnvironmentVariable("ZEUS_DISABLE_VST_LOAD", null);
            Environment.SetEnvironmentVariable("ZEUS_DISABLE_TX_VST_LOAD", "1");

            var bridge = new FakeBridge();
            var plugin = new VstHostAudioPlugin(bridge, AudioManifest(), Path.GetTempPath(), "FakeFx");

            await plugin.InitializeAudioAsync(new StubHost(), default);

            Assert.False(bridge.InitCalled);
            Assert.False(plugin.IsNativelyLoaded);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZEUS_ENABLE_VST_LOAD", previousEnable);
            Environment.SetEnvironmentVariable("ZEUS_DISABLE_VST_LOAD", previousDisable);
            Environment.SetEnvironmentVariable("ZEUS_DISABLE_TX_VST_LOAD", previousTxDisable);
            VstHostAudioPlugin.NativeLoadEnabledOverride = null;
        }
    }

    [Fact]
    public async Task Initialize_RxNativeLoad_IsEnabledByDefault()
    {
        VstHostAudioPlugin.NativeLoadEnabledOverride = null;
        var previousEnable = Environment.GetEnvironmentVariable("ZEUS_ENABLE_VST_LOAD");
        var previousDisable = Environment.GetEnvironmentVariable("ZEUS_DISABLE_VST_LOAD");
        var previousRxDisable = Environment.GetEnvironmentVariable("ZEUS_DISABLE_RX_VST_LOAD");
        var bridge = new FakeBridge();
        var pluginDir = Path.GetTempPath();
        var vst3Abs = Path.Combine(pluginDir, "vst3", "FakeRx.vst3");
        Directory.CreateDirectory(Path.GetDirectoryName(vst3Abs)!);
        File.WriteAllText(vst3Abs, "stub");
        try
        {
            Environment.SetEnvironmentVariable("ZEUS_ENABLE_VST_LOAD", null);
            Environment.SetEnvironmentVariable("ZEUS_DISABLE_VST_LOAD", null);
            Environment.SetEnvironmentVariable("ZEUS_DISABLE_RX_VST_LOAD", null);

            var plugin = new VstHostAudioPlugin(
                bridge,
                AudioManifest("vst3/FakeRx.vst3", "rx.post-demod"),
                pluginDir,
                "FakeRx");

            await plugin.InitializeAudioAsync(new StubHost(currentBlockSize: 2048), default);

            Assert.True(bridge.InitCalled);
            Assert.True(plugin.IsNativelyLoaded);
            Assert.Equal(2048, bridge.LastBlockSize);
        }
        finally
        {
            File.Delete(vst3Abs);
            Environment.SetEnvironmentVariable("ZEUS_ENABLE_VST_LOAD", previousEnable);
            Environment.SetEnvironmentVariable("ZEUS_DISABLE_VST_LOAD", previousDisable);
            Environment.SetEnvironmentVariable("ZEUS_DISABLE_RX_VST_LOAD", previousRxDisable);
            VstHostAudioPlugin.NativeLoadEnabledOverride = null;
        }
    }
}
