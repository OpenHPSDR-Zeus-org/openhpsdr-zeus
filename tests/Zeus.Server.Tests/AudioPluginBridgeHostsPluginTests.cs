using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Audio;
using Zeus.Plugins.Contracts.Extensions;
using Zeus.Plugins.Host;
using Zeus.Plugins.Host.Audio;
using Zeus.Server;

namespace Zeus.Server.Tests;

/// <summary>
/// Covers <see cref="AudioPluginBridge.HostsPlugin"/> — the predicate the editor
/// endpoints use to skip the "install the VST engine" guard for AU/VST3 plugins
/// the bridge hosts in-process (the only host on macOS).
///
/// It MUST gate on <c>IsNativelyLoaded</c>, not bare map membership. On Windows
/// VST mode a scanned VST is registered in BOTH the engine-slot map AND the
/// bridge map (the maps are parallel, not exclusive), but it is never natively
/// loaded in the bridge (handle stays 0). So HostsPlugin must report false for a
/// not-natively-loaded plugin, leaving the guard / engine routing byte-for-byte
/// unchanged for the Windows out-of-process path.
/// </summary>
public sealed class AudioPluginBridgeHostsPluginTests
{
    [Theory]
    [InlineData(false)] // TX map (_idToPlugin)
    [InlineData(true)]  // RX map (_rxIdToPlugin)
    public void HostsPlugin_IsTrueOnlyWhenPluginIsNativelyLoaded(bool rx)
    {
        var bridge = NewBridge();
        var plugin = new VstHostAudioPlugin(
            new NoopVstBridge(),
            new AudioBlock
            {
                Vst3Path = "Fake.vst3",
                Slot = rx ? "rx.post-demod" : "tx.post-leveler",
            },
            Path.GetTempPath(),
            "Fake VST");
        const string id = "com.openhpsdr.zeus.fake";

        (rx ? RxPluginMap(bridge) : TxPluginMap(bridge))[id] = plugin;

        // Parked in the bridge map but handle == 0 — the Windows engine-routed
        // state and the TX opt-in-gated state. NOT hosted in-process: the guard
        // must still fire for this id.
        Assert.False(plugin.IsNativelyLoaded);
        Assert.False(bridge.HostsPlugin(id));

        // Once natively loaded (a real handle behind it) the bridge hosts it,
        // so the editor endpoints skip the engine guard and open it in-process.
        SetHandle(plugin, 1);
        Assert.True(plugin.IsNativelyLoaded);
        Assert.True(bridge.HostsPlugin(id));
    }

    [Fact]
    public void HostsPlugin_IsFalseForUnknownId()
    {
        var bridge = NewBridge();
        Assert.False(bridge.HostsPlugin("com.openhpsdr.zeus.not.here"));
    }

    // -- harness ---------------------------------------------------------

    private static AudioPluginBridge NewBridge() =>
        new(
            isMoxOn: () => false,
            isMonitorOn: () => false,
            log: NullLogger<AudioPluginBridge>.Instance);

    private static Dictionary<string, IAudioPlugin> TxPluginMap(AudioPluginBridge b) =>
        PrivateField<Dictionary<string, IAudioPlugin>>(b, "_idToPlugin");

    private static Dictionary<string, IAudioPlugin> RxPluginMap(AudioPluginBridge b) =>
        PrivateField<Dictionary<string, IAudioPlugin>>(b, "_rxIdToPlugin");

    private static void SetHandle(VstHostAudioPlugin plugin, nint handle)
    {
        var field = typeof(VstHostAudioPlugin).GetField(
            "_handle", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(plugin, handle);
    }

    private static T PrivateField<T>(object target, string name)
    {
        var field = target.GetType().GetField(
            name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<T>(field!.GetValue(target));
    }

    private sealed class NoopVstBridge : IVstBridgeNative
    {
        public int Init(int abi) => VstBridgeStatus.Ok;

        public int LoadVst3(string path, int channels, int sampleRate, int blockSize, out nint handle)
        {
            handle = 0;
            return VstBridgeStatus.Ok;
        }

        public int Process(nint handle, ReadOnlySpan<float> input, Span<float> output, int frames)
        {
            input.CopyTo(output);
            return VstBridgeStatus.Ok;
        }

        public int SetParameter(nint handle, uint paramId, double normalized) => VstBridgeStatus.Ok;
        public int Unload(nint handle) => VstBridgeStatus.Ok;
        public int Shutdown() => VstBridgeStatus.Ok;
        public int EditorOpen(nint handle, string title) => VstBridgeStatus.Ok;
        public int EditorClose(nint handle) => VstBridgeStatus.Ok;
        public bool EditorIsOpen(nint handle) => false;
    }
}
