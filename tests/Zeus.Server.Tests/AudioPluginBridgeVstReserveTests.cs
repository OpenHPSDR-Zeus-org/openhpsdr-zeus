using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Audio;
using Zeus.Plugins.Contracts.Extensions;
using Zeus.Plugins.Host.Audio;
using Zeus.Server;

namespace Zeus.Server.Tests;

/// <summary>
/// Regression coverage: in out-of-process VST mode, a TX VST plugin must be
/// RESERVED for the external engine and NOT loaded in the in-process VST bridge —
/// mirroring the receive path's <c>EnsureRxPluginInitialized</c>. Before the fix
/// the TX init path always tried the in-process load, so a plugin the in-process
/// host can't load (e.g. a Waves shell, VST3 load status=5) threw during attach
/// and got DETACHED, making it impossible to add to the chain even though the
/// active engine could host it. The gate is the engine <see cref="VstEngineState"/>
/// (not IsActive) so it also holds during the startup window before the engine
/// finishes its handshake; Native mode (Inactive) still loads in-process.
/// </summary>
public sealed class AudioPluginBridgeVstReserveTests
{
    [Theory]
    [InlineData(VstEngineState.Active)]      // engine live
    [InlineData(VstEngineState.Activating)]  // startup window — engine not up yet
    [InlineData(VstEngineState.Backoff)]     // crash-loop: still VST mode, don't fall back
    [InlineData(VstEngineState.Faulted)]
    public void VstMode_ReservesTxPluginForEngine_WithoutInProcessLoad(VstEngineState state)
    {
        var bridge = NewBridge();
        var engine = NewEngineInState(state);
        SetPrivateField(bridge, "_vstEngine", engine);

        var native = new FailingVstBridge();
        var plugin = NewVstPlugin(native);
        const string id = "com.openhpsdr.zeus.vst.waveshellsub";

        var ok = EnsureTxPluginInitialized(bridge, id, plugin);

        Assert.True(ok);                                        // reserved → success
        Assert.Equal(0, native.LoadCount);                     // in-process load NEVER attempted
        Assert.Contains(id, TxInitializedSet(bridge));         // marked reserved
    }

    // Native mode (engine Inactive) is intentionally not asserted here: the in-
    // process load path it falls through to is env-gated (ZEUS_ENABLE_VST_LOAD) and
    // unchanged by this fix — the new early-return is scoped strictly to a
    // non-Inactive engine, so Native behaviour is identical to before.

    // -- harness ---------------------------------------------------------

    private static AudioPluginBridge NewBridge() =>
        new(
            isMoxOn: () => false,
            isMonitorOn: () => false,
            log: NullLogger<AudioPluginBridge>.Instance);

    private static VstHostAudioPlugin NewVstPlugin(IVstBridgeNative native) =>
        new(
            native,
            new AudioBlock { Vst3Path = "Fake.vst3", Slot = "tx.post-leveler" },
            Path.GetTempPath(),
            "Fake VST");

    private static VstEngineController NewEngineInState(VstEngineState state)
    {
        var engine = new VstEngineController(maxFrames: 512, rate: 48000, channels: 1);
        var field = typeof(VstEngineController).GetField(
            "_state", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(engine, state);
        return engine;
    }

    private static bool EnsureTxPluginInitialized(
        AudioPluginBridge bridge, string id, IAudioPlugin plugin)
    {
        var method = typeof(AudioPluginBridge).GetMethod(
            "EnsureTxPluginInitialized", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (bool)method!.Invoke(bridge, [id, plugin, "tx.post-leveler"])!;
    }

    private static HashSet<string> TxInitializedSet(AudioPluginBridge b)
    {
        var field = typeof(AudioPluginBridge).GetField(
            "_txInitializedIds", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (HashSet<string>)field!.GetValue(b)!;
    }

    private static void SetPrivateField(object target, string name, object? value)
    {
        var field = target.GetType().GetField(
            name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    /// <summary>A native bridge whose VST3 load always fails (like a Waves shell
    /// returning status=5 in-process), counting attempts so a reservation that
    /// SKIPS the in-process load is observable.</summary>
    private sealed class FailingVstBridge : IVstBridgeNative
    {
        public int LoadCount { get; private set; }

        public int Init(int abi) => VstBridgeStatus.Ok;

        public int LoadVst3(string path, int channels, int sampleRate, int blockSize, out nint handle)
        {
            LoadCount++;
            handle = 0;
            return 5; // zvst_status_t load failure (matches the observed WaveShell status)
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
