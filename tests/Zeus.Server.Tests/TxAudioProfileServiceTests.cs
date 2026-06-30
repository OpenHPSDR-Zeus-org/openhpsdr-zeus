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
    private readonly AudioPluginBridge _bridge;
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
        _bridge = new AudioPluginBridge(
            isMoxOn: () => false, isMonitorOn: () => false,
            log: NullLogger<AudioPluginBridge>.Instance);
        _masterBypass = new AudioChainMasterBypassService(
            _chainSettings,
            _bridge,
            hub,
            NullLogger<AudioChainMasterBypassService>.Instance);
        _mode = new AudioProcessingModeService(
            _modeStore, _engine, _pluginManager, _chainOrder,
            NullLogger<AudioProcessingModeService>.Instance);

        _service = new TxAudioProfileService(
            _profileStore, _radio, _chainOrder, _masterBypass, _mode, _fidelity,
            _pluginManager, _pluginSettings, _bridge,
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

    [Fact]
    public async Task ExportImport_RoundTrips_FromJsonBytes()
    {
        await _mode.StartAsync(CancellationToken.None);
        _radio.SetTxMicGain(-6);
        var saved = await _service.SaveCurrentAsync("Roundtrip Voice");

        var export = _service.ExportProfile(saved.Id);
        Assert.NotNull(export);
        var json = System.Text.Encoding.UTF8.GetString(export!.Value.Bytes);
        Assert.Equal("roundtrip-voice.json", export.Value.FileName);

        // Delete then re-import from the exported bytes — must restore the profile.
        Assert.True(_service.Delete(saved.Id));
        var imported = _service.ImportProfile(json, "ignored-fallback");
        Assert.Equal("roundtrip-voice", imported.Id);
        Assert.Equal("Roundtrip Voice", imported.Name);
        Assert.Equal(-6, imported.MicGainDb);
        Assert.NotNull(_service.Get("roundtrip-voice"));
    }

    [Fact]
    public async Task Import_IsNonDestructive_UniquifiesNameOnSlugCollision()
    {
        await _mode.StartAsync(CancellationToken.None);
        var saved = await _service.SaveCurrentAsync("Voice");
        var json = System.Text.Encoding.UTF8.GetString(_service.ExportProfile(saved.Id)!.Value.Bytes);

        // Re-import WITHOUT deleting: the existing profile must survive and the
        // import lands under a bumped id/name.
        var imported = _service.ImportProfile(json, null);
        Assert.Equal("voice-2", imported.Id);
        Assert.Equal("Voice 2", imported.Name);
        Assert.NotNull(_service.Get("voice"));     // original intact
        Assert.NotNull(_service.Get("voice-2"));   // import added
    }

    [Fact]
    public void Import_RejectsUnparseableJson()
    {
        Assert.Throws<ArgumentException>(() => _service.ImportProfile("not a profile", null));
    }

    [Fact]
    public void Import_ToleratesSparseFile_UsesFallbackName()
    {
        // A minimal hand-authored file: only a couple of fields, no name, no
        // collections. Import must default the nullable members and name it from
        // the fallback rather than throwing.
        const string json = "{\"micGainDb\": -2, \"targetSpectralDensity\": 40}";
        var imported = _service.ImportProfile(json, "My Import");

        Assert.Equal("my-import", imported.Id);
        Assert.Equal("My Import", imported.Name);
        Assert.Equal(-2, imported.MicGainDb);
        Assert.NotNull(imported.ChainOrder);
        Assert.NotNull(imported.CfcConfig);
        Assert.NotNull(imported.TxLeveling);
    }

    [Fact]
    public void Import_NativeProfile_StripsVstEntriesFromActiveChain()
    {
        const string json = """
        {
          "id": "voodoo-4k",
          "name": "VooDoo 4K",
          "micGainDb": -1,
          "levelerMaxGainDb": 7,
          "txLeveling": { "levelerEnabled": true },
          "cfcConfig": {
            "enabled": true,
            "postEqEnabled": true,
            "preCompDb": 0.5,
            "prePeqDb": 0,
            "bands": [
              { "freqHz": 80, "compLevelDb": 0.5, "postGainDb": -4 },
              { "freqHz": 150, "compLevelDb": 1, "postGainDb": -2 },
              { "freqHz": 250, "compLevelDb": 2, "postGainDb": -1 },
              { "freqHz": 500, "compLevelDb": 3, "postGainDb": 0 },
              { "freqHz": 900, "compLevelDb": 4, "postGainDb": 0.5 },
              { "freqHz": 1500, "compLevelDb": 5, "postGainDb": 1 },
              { "freqHz": 2200, "compLevelDb": 4.5, "postGainDb": 1.5 },
              { "freqHz": 2800, "compLevelDb": 3.5, "postGainDb": 1.5 },
              { "freqHz": 3500, "compLevelDb": 2, "postGainDb": -1 },
              { "freqHz": 5000, "compLevelDb": 1, "postGainDb": -3 }
            ]
          },
          "lowCutHz": 0,
          "highCutHz": 4000,
          "processingMode": "native",
          "chainOrder": [
            "com.openhpsdr.zeus.samples.noisegate",
            "com.openhpsdr.zeus.vst.clear",
            "com.openhpsdr.zeus.samples.eq"
          ],
          "chainParked": [ "com.openhpsdr.zeus.vst.clear" ],
          "vstPluginStates": { "com.openhpsdr.zeus.vst.clear": "opaque" },
          "nativePluginStates": {
            "com.openhpsdr.zeus.samples.eq": { "bypass": "true" }
          },
          "targetSpectralDensity": 55
        }
        """;

        var imported = _service.ImportProfile(json, "ignored");

        Assert.Equal("voodoo-4k", imported.Id);
        Assert.Equal("native", imported.ProcessingMode);
        Assert.DoesNotContain("com.openhpsdr.zeus.vst.clear", imported.ChainOrder);
        Assert.Contains("com.openhpsdr.zeus.samples.noisegate", imported.ChainOrder);
        Assert.Contains("com.openhpsdr.zeus.samples.eq", imported.ChainOrder);
        Assert.Empty(imported.VstPluginStates);
        Assert.Equal("true", imported.NativePluginStates["com.openhpsdr.zeus.samples.eq"]["bypass"]);
    }

    [Fact]
    public async Task Apply_NativeStoredProfile_DoesNotReplayVstChain()
    {
        await _mode.StartAsync(CancellationToken.None);
        _profileStore.Upsert(new TxAudioProfileDto(
            Id: "unsafe",
            Name: "Unsafe",
            MicGainDb: 0,
            LevelerMaxGainDb: 8,
            TxLeveling: new TxLevelingConfig(),
            CfcConfig: CfcConfig.Default,
            TxPhaseRotator: new TxPhaseRotatorConfig(),
            LowCutHz: 150,
            HighCutHz: 2900,
            ProcessingMode: "native",
            MasterBypass: false,
            ChainOrder: new List<string> { "com.openhpsdr.zeus.vst.clear", "com.openhpsdr.zeus.samples.eq" },
            ChainParked: new List<string>(),
            VstPluginStates: new Dictionary<string, string> { ["com.openhpsdr.zeus.vst.clear"] = "opaque" },
            NativePluginStates: new Dictionary<string, Dictionary<string, string>>(),
            TargetSpectralDensity: 55,
            CreatedUtc: DateTime.UtcNow,
            UpdatedUtc: DateTime.UtcNow));

        var applied = await _service.ApplyAsync("unsafe");

        Assert.NotNull(applied);
        Assert.DoesNotContain("com.openhpsdr.zeus.vst.clear", applied!.ChainOrder);
        Assert.Empty(applied.VstPluginStates);
        Assert.Equal("unsafe", _service.LastLoadedId);
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
