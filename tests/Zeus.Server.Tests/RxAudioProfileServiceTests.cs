using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Plugins.Host;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class RxAudioProfileServiceTests : IDisposable
{
    private const string RxClear = "com.openhpsdr.zeus.rxvst.clear";
    private const string RxNoise = "com.openhpsdr.zeus.rxvst.noise";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "zeus-rx-profile-" + Guid.NewGuid().ToString("N"));
    private readonly PluginSettingsStore _pluginSettings;
    private readonly PluginManager _pluginManager;
    private readonly RxChainOrderStore _rxOrderStore;
    private readonly RxAudioProfileStore _profileStore;
    private readonly AudioChainSettingsStore _chainSettingsStore;
    private readonly RxVstEngineService _rxVst;

    public RxAudioProfileServiceTests()
    {
        Directory.CreateDirectory(_root);
        _pluginSettings = new PluginSettingsStore(Path.Combine(_root, "plugins.db"));
        _pluginManager = new PluginManager(
            loader: new PluginLoader(NullLogger<PluginLoader>.Instance),
            settings: _pluginSettings,
            services: new ServiceCollection().BuildServiceProvider(),
            logFactory: NullLoggerFactory.Instance,
            options: new PluginManagerOptions { PluginRoot = Path.Combine(_root, "plugins") });
        _rxOrderStore = new RxChainOrderStore(
            NullLogger<RxChainOrderStore>.Instance,
            Path.Combine(_root, "rx-order.db"));
        _profileStore = new RxAudioProfileStore(
            NullLogger<RxAudioProfileStore>.Instance,
            Path.Combine(_root, "rx-profiles.db"));
        _chainSettingsStore = new AudioChainSettingsStore(
            NullLogger<AudioChainSettingsStore>.Instance,
            Path.Combine(_root, "chain-settings.db"));

        var rxChainOrder = new RxChainOrderService(
            _rxOrderStore,
            NullLogger<RxChainOrderService>.Instance);
        _rxVst = new RxVstEngineService(
            _pluginManager,
            rxChainOrder,
            NullLogger<RxVstEngineService>.Instance);

        RxChainOrder = rxChainOrder;
        MasterBypass = new AudioChainMasterBypassService(
            _chainSettingsStore,
            new AudioPluginBridge(
                isMoxOn: () => false,
                isMonitorOn: () => false,
                log: NullLogger<AudioPluginBridge>.Instance),
            new StreamingHub(NullLogger<StreamingHub>.Instance),
            NullLogger<AudioChainMasterBypassService>.Instance);
        Service = new RxAudioProfileService(
            _profileStore,
            RxChainOrder,
            MasterBypass,
            _rxVst,
            NullLogger<RxAudioProfileService>.Instance);
    }

    private RxChainOrderService RxChainOrder { get; }
    private AudioChainMasterBypassService MasterBypass { get; }
    private RxAudioProfileService Service { get; }

    [Fact]
    public async Task SaveApply_RestoresRxOrderAndBypassWithoutTouchingTxBypass()
    {
        await MasterBypass.StartAsync(CancellationToken.None);
        MasterBypass.SetMasterBypassed(false);
        MasterBypass.SetRxMasterBypassed(false);
        RxChainOrder.OnPluginAttached(RxClear);
        RxChainOrder.OnPluginAttached(RxNoise);
        Assert.True(RxChainOrder.TrySetParked(RxClear, parked: false, out _));
        Assert.True(RxChainOrder.TrySetParked(RxNoise, parked: false, out _));
        Assert.True(RxChainOrder.TrySetOrder([RxNoise, RxClear], out _));

        var saved = await Service.SaveCurrentAsync("Clear receive");

        Assert.Equal([RxNoise, RxClear], saved.Order);
        Assert.Empty(saved.Parked);
        Assert.False(saved.MasterBypass);

        Assert.True(RxChainOrder.TrySetParked(RxClear, parked: true, out _));
        MasterBypass.SetRxMasterBypassed(true);

        var applied = await Service.ApplyAsync("Clear receive");

        Assert.NotNull(applied);
        Assert.Equal([RxNoise, RxClear], RxChainOrder.CurrentOrder);
        Assert.False(MasterBypass.IsRxBypassed);
        Assert.False(MasterBypass.IsBypassed);
    }

    [Fact]
    public async Task SaveApplyAndDelete_MaintainSelectedProfile()
    {
        await MasterBypass.StartAsync(CancellationToken.None);
        RxChainOrder.OnPluginAttached(RxClear);
        RxChainOrder.OnPluginAttached(RxNoise);
        Assert.True(RxChainOrder.TrySetParked(RxClear, parked: false, out _));
        Assert.True(RxChainOrder.TrySetParked(RxNoise, parked: false, out _));

        await Service.SaveCurrentAsync("Clear receive");

        Assert.Equal("Clear receive", Service.SelectedProfileName);

        await Service.ApplyAsync("Clear receive");

        Assert.Equal("Clear receive", Service.SelectedProfileName);

        Assert.True(Service.Delete("Clear receive"));

        Assert.Null(Service.SelectedProfileName);
    }

    [Fact]
    public async Task StartupService_ReappliesSelectedRxProfile()
    {
        await MasterBypass.StartAsync(CancellationToken.None);
        MasterBypass.SetRxMasterBypassed(false);
        RxChainOrder.OnPluginAttached(RxClear);
        RxChainOrder.OnPluginAttached(RxNoise);
        Assert.True(RxChainOrder.TrySetParked(RxClear, parked: false, out _));
        Assert.True(RxChainOrder.TrySetParked(RxNoise, parked: false, out _));
        Assert.True(RxChainOrder.TrySetOrder([RxNoise, RxClear], out _));

        await Service.SaveCurrentAsync("Clear receive");

        Assert.True(RxChainOrder.TrySetParked(RxClear, parked: true, out _));
        MasterBypass.SetRxMasterBypassed(true);

        var startup = new RxAudioProfileStartupService(
            Service,
            NullLogger<RxAudioProfileStartupService>.Instance);
        await startup.StartAsync(CancellationToken.None);

        Assert.Equal("Clear receive", Service.SelectedProfileName);
        Assert.Equal([RxNoise, RxClear], RxChainOrder.CurrentOrder);
        Assert.False(MasterBypass.IsRxBypassed);
    }

    public void Dispose()
    {
        _rxVst.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _pluginManager.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _pluginSettings.Dispose();
        _rxOrderStore.Dispose();
        _profileStore.Dispose();
        _chainSettingsStore.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
