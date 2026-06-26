using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Audio;
using Zeus.Plugins.Contracts.Extensions;
using Zeus.Plugins.Host.Audio;
using Zeus.Server;

namespace Zeus.Server.Tests;

/// <summary>
/// Regression coverage for issue #827 "plugins kept trying to launch on their
/// own": a SCANNED (parked) TX plugin must be registered but NOT natively
/// initialized — native instantiation is deferred until the operator un-parks
/// it into the live chain, mirroring the receive path's
/// <c>EnsureRxPluginInitialized</c> deferral. Before the fix,
/// <c>OnPluginActivated</c> called <c>InitializeAudioAsync</c> unconditionally
/// (even for parked plugins), so scanning the whole macOS AudioComponent
/// registry instantiated every Audio Unit at scan time.
/// </summary>
public sealed class AudioPluginBridgeTxDeferredInitTests
{
    // A non-DefaultOrder id parks on attach (the shape every scanned VST/AU id has).
    private const string ScannedId = "com.openhpsdr.zeus.au.fakefx";

    [Fact]
    public void ScannedTxPlugin_StaysParkedAndUninitialized_UntilUnparked()
    {
        using var fx = new TxOrderFixture();
        var bridge = NewBridgeWithChainOrder(fx.Service);
        var spy = new SpyTxPlugin();

        // 1) Simulate the parked-attach OnPluginActivated takes for a scanned
        //    plugin: register it + record its slot name, park it in the order
        //    service — but do NOT initialize it.
        fx.Service.OnPluginAttached(ScannedId, []);
        TxPluginMap(bridge)[ScannedId] = spy;
        TxSlotNameMap(bridge)[ScannedId] = "tx.post-leveler";

        Assert.True(fx.Service.IsParked(ScannedId));
        Assert.Equal(0, spy.InitCount);                 // nothing launched it
        Assert.DoesNotContain(ScannedId, TxInitializedSet(bridge));

        // A re-slot while parked must keep it OUT of the live chain.
        ReapplySlots(bridge);
        Assert.Null(TxChain(bridge).GetSlot(0));
        Assert.False(TxSlotMap(bridge).ContainsKey(ScannedId));
        Assert.Equal(0, spy.InitCount);

        // 2) Operator un-parks it → the OrderChanged path initializes it ONCE
        //    and slots it into the live chain.
        Assert.True(fx.Service.TrySetParked(ScannedId, parked: false, out _));
        ApplyChainOrder(bridge);

        Assert.Equal(1, spy.InitCount);                 // initialized exactly on un-park
        Assert.Contains(ScannedId, TxInitializedSet(bridge));
        Assert.Same(spy, TxChain(bridge).GetSlot(0));   // now live in the chain

        // 3) Idempotent: a further reorder must NOT re-initialize.
        ApplyChainOrder(bridge);
        Assert.Equal(1, spy.InitCount);
    }

    [Fact]
    public void ReloadActiveTxPlugins_RecyclesInitializedLiveTxPlugin()
    {
        using var fx = new TxOrderFixture();
        var bridge = NewBridgeWithChainOrder(fx.Service);
        var spy = new SpyTxPlugin();
        const string pluginId = "com.openhpsdr.zeus.samples.eq";

        fx.Service.OnPluginAttached(pluginId, []);
        TxPluginMap(bridge)[pluginId] = spy;
        TxSlotNameMap(bridge)[pluginId] = "tx.post-leveler";
        ApplyChainOrder(bridge);

        Assert.Equal(1, spy.InitCount);
        Assert.Equal(0, spy.ShutdownCount);
        Assert.Same(spy, TxChain(bridge).GetSlot(0));

        bridge.ReloadActiveTxPlugins([pluginId]);

        Assert.Equal(2, spy.InitCount);
        Assert.Equal(1, spy.ShutdownCount);
        Assert.Contains(pluginId, TxInitializedSet(bridge));
        Assert.Same(spy, TxChain(bridge).GetSlot(0));
    }

    // -- harness ---------------------------------------------------------

    private static AudioPluginBridge NewBridgeWithChainOrder(ChainOrderService chainOrder)
    {
        // Realtime-only ctor leaves _chainOrder null; inject the real service via
        // reflection so the deferred-init path under test (ApplyChainOrder →
        // EnsureActiveTxPluginsInitialized → ReapplySlotsUnderLock) is exercised.
        var bridge = new AudioPluginBridge(
            isMoxOn: () => false,
            isMonitorOn: () => false,
            log: NullLogger<AudioPluginBridge>.Instance);
        SetPrivateField(bridge, "_chainOrder", chainOrder);
        return bridge;
    }

    private static void ApplyChainOrder(AudioPluginBridge bridge) =>
        InvokePrivate(bridge, "ApplyChainOrder", [Array.Empty<string>()]);

    private static void ReapplySlots(AudioPluginBridge bridge) =>
        InvokePrivate(bridge, "ReapplySlotsUnderLock", []);

    private static Dictionary<string, IAudioPlugin> TxPluginMap(AudioPluginBridge b) =>
        PrivateField<Dictionary<string, IAudioPlugin>>(b, "_idToPlugin");

    private static Dictionary<string, int> TxSlotMap(AudioPluginBridge b) =>
        PrivateField<Dictionary<string, int>>(b, "_idToSlot");

    private static Dictionary<string, string> TxSlotNameMap(AudioPluginBridge b) =>
        PrivateField<Dictionary<string, string>>(b, "_txIdToSlotName");

    private static HashSet<string> TxInitializedSet(AudioPluginBridge b) =>
        PrivateField<HashSet<string>>(b, "_txInitializedIds");

    private static AudioChain TxChain(AudioPluginBridge b) =>
        PrivateField<AudioChain>(b, "_chain");

    private static void InvokePrivate(object target, string name, object?[] args)
    {
        var method = target.GetType().GetMethod(
            name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(target, args);
    }

    private static void SetPrivateField(object target, string name, object? value)
    {
        var field = target.GetType().GetField(
            name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(target, value);
    }

    private static T PrivateField<T>(object target, string name)
    {
        var field = target.GetType().GetField(
            name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<T>(field.GetValue(target));
    }

    private sealed class TxOrderFixture : IDisposable
    {
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(), "zeus-tx-deferred-init-" + Guid.NewGuid().ToString("N") + ".db");
        private readonly ChainOrderStore _store;
        private readonly StreamingHub _hub;

        public TxOrderFixture()
        {
            _store = new ChainOrderStore(NullLogger<ChainOrderStore>.Instance, _dbPath);
            _hub = new StreamingHub(NullLogger<StreamingHub>.Instance);
            Service = new ChainOrderService(_store, _hub, NullLogger<ChainOrderService>.Instance);
        }

        public ChainOrderService Service { get; }

        public void Dispose()
        {
            _store.Dispose();
            try { File.Delete(_dbPath); } catch { }
        }
    }

    private sealed class SpyTxPlugin : IAudioPlugin
    {
        public int InitCount { get; private set; }
        public int ShutdownCount { get; private set; }
        public string DisplayName => "Spy TX";
        public AudioPluginRequirements Requirements => new(48_000, 1, 1_024);

        public Task InitializeAudioAsync(IAudioHost host, CancellationToken ct)
        {
            InitCount++;
            return Task.CompletedTask;
        }

        public void Process(ReadOnlySpan<float> input, Span<float> output, AudioBlockContext ctx) =>
            input.CopyTo(output);

        public Task ShutdownAudioAsync(CancellationToken ct)
        {
            ShutdownCount++;
            return Task.CompletedTask;
        }
    }
}
