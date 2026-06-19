// SPDX-License-Identifier: GPL-2.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Plugins.Host;
using Zeus.Plugins.Host.Audio;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class TxAudioProfileServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "zeus-tx-audio-profile-" + Guid.NewGuid().ToString("N"));

    private readonly PluginSettingsStore _pluginSettings;
    private readonly PluginManager _pluginManager;
    private readonly DspSettingsStore _dsp;
    private readonly PaSettingsStore _pa;
    private readonly ChainOrderStore _orderStore;
    private readonly AudioChainSettingsStore _chainSettings;
    private readonly AudioProcessingModeStore _modeStore;
    private readonly TxFidelityPolicyStore _fidelity;
    private readonly TxAudioProfileStore _profileStore;
    private readonly VstEngineController _engine;
    private readonly RadioService _radio;
    private readonly ChainOrderService _chainOrder;
    private readonly AudioChainMasterBypassService _masterBypass;
    private readonly AudioProcessingModeService _mode;
    private readonly TxAudioProfileService _service;

    public TxAudioProfileServiceTests()
    {
        Directory.CreateDirectory(_root);
        string P(string n) => Path.Combine(_root, n);

        _pluginSettings = new PluginSettingsStore(P("plugins.db"));
        _pluginManager = new PluginManager(
            loader: new PluginLoader(NullLogger<PluginLoader>.Instance),
            settings: _pluginSettings,
            services: new ServiceCollection().BuildServiceProvider(),
            logFactory: NullLoggerFactory.Instance,
            options: new PluginManagerOptions { PluginRoot = P("plugins") });

        _dsp = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, P("dsp.db"));
        _pa = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, P("pa.db"));
        _orderStore = new ChainOrderStore(NullLogger<ChainOrderStore>.Instance, P("order.db"));
        _chainSettings = new AudioChainSettingsStore(NullLogger<AudioChainSettingsStore>.Instance, P("chain.db"));
        _modeStore = new AudioProcessingModeStore(NullLogger<AudioProcessingModeStore>.Instance, P("mode.db"));
        _fidelity = new TxFidelityPolicyStore(NullLogger<TxFidelityPolicyStore>.Instance, P("fidelity.db"));
        _profileStore = new TxAudioProfileStore(NullLogger<TxAudioProfileStore>.Instance, P("tx-profiles.db"));
        _engine = new VstEngineController();

        _radio = new RadioService(NullLoggerFactory.Instance, _dsp, _pa);

        var hub = new StreamingHub(NullLogger<StreamingHub>.Instance);
        _chainOrder = new ChainOrderService(_orderStore, hub, NullLogger<ChainOrderService>.Instance);
        _masterBypass = new AudioChainMasterBypassService(
            _chainSettings,
            new AudioPluginBridge(
                isMoxOn: () => false, isMonitorOn: () => false,
                log: NullLogger<AudioPluginBridge>.Instance),
            hub,
            NullLogger<AudioChainMasterBypassService>.Instance);
        _mode = new AudioProcessingModeService(
            _modeStore, _engine, _pluginManager, _chainOrder,
            NullLogger<AudioProcessingModeService>.Instance);

        _service = new TxAudioProfileService(
            _profileStore, _radio, _chainOrder, _masterBypass, _mode, _fidelity,
            _pluginManager, _pluginSettings,
            NullLogger<TxAudioProfileService>.Instance);
    }

    [Fact]
    public async Task SaveApply_RoundTrips_ScalarsAndConfigs()
    {
        await _masterBypass.StartAsync(CancellationToken.None);
        await _mode.StartAsync(CancellationToken.None);

        // Set up a distinctive live state.
        _radio.SetTxMicGain(-7);
        _radio.SetTxLevelerMaxGain(13.5);
        _radio.SetTxLeveling(new TxLevelingConfig(
            AlcMaxGainDb: 5, AlcDecayMs: 12,
            LevelerEnabled: true, LevelerDecayMs: 220,
            CompressorEnabled: true, CompressorGainDb: 4));
        _radio.SetTxFilter(120, 3100);
        _masterBypass.SetMasterBypassed(true);

        var saved = await _service.SaveCurrentAsync("Contest Voice");
        Assert.Equal("contest-voice", saved.Id);
        Assert.Equal(-7, saved.MicGainDb);
        Assert.Equal(13.5, saved.LevelerMaxGainDb);
        Assert.Equal(220, saved.TxLeveling.LevelerDecayMs);
        Assert.True(saved.TxLeveling.CompressorEnabled);
        Assert.Equal(120, saved.LowCutHz);
        Assert.Equal(3100, saved.HighCutHz);
        Assert.True(saved.MasterBypass);

        // Drift live state off the profile.
        _radio.SetTxMicGain(2);
        _radio.SetTxLevelerMaxGain(1);
        _radio.SetTxFilter(300, 2700);
        _masterBypass.SetMasterBypassed(false);

        var applied = await _service.ApplyAsync("contest-voice");
        Assert.NotNull(applied);

        var snap = _radio.Snapshot();
        Assert.Equal(-7, snap.MicGainDb);
        Assert.Equal(13.5, snap.LevelerMaxGainDb);
        Assert.Equal(220, snap.TxLeveling!.LevelerDecayMs);
        Assert.True(snap.TxLeveling.CompressorEnabled);
        // TX filter magnitudes restored (SignedFilterForMode re-signs per mode).
        Assert.Equal(120, Math.Min(Math.Abs(snap.TxFilterLowHz), Math.Abs(snap.TxFilterHighHz)));
        Assert.Equal(3100, Math.Max(Math.Abs(snap.TxFilterLowHz), Math.Abs(snap.TxFilterHighHz)));
        Assert.True(_masterBypass.IsBypassed);

        // Apply recorded the last-loaded pointer.
        Assert.Equal("contest-voice", _service.LastLoadedId);
    }

    [Fact]
    public async Task Apply_DoesNotTouchPureSignalOrDrive()
    {
        await _masterBypass.StartAsync(CancellationToken.None);
        await _mode.StartAsync(CancellationToken.None);

        var before = _radio.Snapshot();
        _radio.SetTxMicGain(-5);
        await _service.SaveCurrentAsync("Voice");

        var applied = await _service.ApplyAsync("voice");
        Assert.NotNull(applied);

        var after = _radio.Snapshot();
        // PS fields and drive untouched by the profile apply path.
        Assert.Equal(before.PsEnabled, after.PsEnabled);
        Assert.Equal(before.PsAuto, after.PsAuto);
        Assert.Equal(before.PsAutoAttenuate, after.PsAutoAttenuate);
        Assert.Equal(before.TwoToneMag, after.TwoToneMag);
    }

    [Fact]
    public async Task Save_CapturesNativePluginDumps_WhenSettingsExist()
    {
        await _masterBypass.StartAsync(CancellationToken.None);
        await _mode.StartAsync(CancellationToken.None);

        // Simulate a native plugin that is parked but has persisted settings.
        const string nativeId = "com.openhpsdr.zeus.samples.eq";
        var scoped = _pluginSettings.ForPlugin(nativeId);
        await scoped.SetAsync("band0", 4.5);
        await scoped.SetAsync("bypass", false);
        _chainOrder.OnPluginAttached(nativeId, Array.Empty<string>());

        var saved = await _service.SaveCurrentAsync("With native");
        Assert.True(saved.NativePluginStates.ContainsKey(nativeId));
        Assert.Equal(2, saved.NativePluginStates[nativeId].Count);

        // Drift the native settings, then apply restores them.
        await scoped.SetAsync("band0", 0.0);
        var applied = await _service.ApplyAsync(saved.Id);
        Assert.NotNull(applied);
        Assert.Equal(4.5, await scoped.GetAsync<double>("band0"));
    }

    [Fact]
    public async Task StartAsync_SeedsStarters_WhenEmpty()
    {
        await _service.StartAsync(CancellationToken.None);
        var all = _service.List();
        Assert.Contains(all, p => p.Id == "studio-ssb");
        Assert.Contains(all, p => p.Id == "essb-wide");
        Assert.Contains(all, p => p.Id == "dx-punch");
    }

    [Fact]
    public async Task StartAsync_DoesNotResurrectDeletedStarter()
    {
        await _service.StartAsync(CancellationToken.None);
        Assert.True(_service.Delete("dx-punch"));

        // Second StartAsync (simulated restart) must not re-seed since the
        // collection is non-empty.
        await _service.StartAsync(CancellationToken.None);
        Assert.Null(_service.Get("dx-punch"));
    }

    [Fact]
    public async Task Delete_RemovesProfile()
    {
        await _mode.StartAsync(CancellationToken.None);
        await _service.SaveCurrentAsync("Temp");
        Assert.True(_service.Delete("temp"));
        Assert.Null(_service.Get("temp"));
    }

    public void Dispose()
    {
        _engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _pluginManager.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _pluginSettings.Dispose();
        _dsp.Dispose();
        _pa.Dispose();
        _orderStore.Dispose();
        _chainSettings.Dispose();
        _modeStore.Dispose();
        _fidelity.Dispose();
        _profileStore.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
