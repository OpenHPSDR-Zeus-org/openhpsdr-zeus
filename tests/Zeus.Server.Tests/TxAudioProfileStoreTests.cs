// SPDX-License-Identifier: GPL-2.0-or-later
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class TxAudioProfileStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string _dbPath;

    public TxAudioProfileStoreTests()
    {
        // Per-test directory so the on-disk profile mirror folder
        // (<dir>/tx-audio-profiles) is isolated and cleaned with the test.
        _root = Path.Combine(Path.GetTempPath(), $"zeus-txaudioprofiles-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "zeus-prefs.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private TxAudioProfileStore NewStore() =>
        new(NullLogger<TxAudioProfileStore>.Instance, _dbPath);

    private static TxAudioProfileDto Sample(string id, string name, int mic = 0) => new(
        Id: id,
        Name: name,
        MicGainDb: mic,
        LevelerMaxGainDb: 8,
        TxLeveling: new TxLevelingConfig(),
        CfcConfig: CfcConfig.Default,
        TxPhaseRotator: new TxPhaseRotatorConfig(),
        LowCutHz: 150, HighCutHz: 2900,
        ProcessingMode: "native",
        MasterBypass: false,
        ChainOrder: new List<string> { "com.example.eq" },
        ChainParked: new List<string>(),
        VstPluginStates: new Dictionary<string, string> { ["com.example.vst"] = "blob" },
        NativePluginStates: new Dictionary<string, Dictionary<string, string>>
        {
            ["com.example.eq"] = new() { ["gain"] = "3" },
        },
        TargetSpectralDensity: 55,
        CreatedUtc: DateTime.UtcNow, UpdatedUtc: DateTime.UtcNow);

    [Fact]
    public void Upsert_RoundTrips_FullCatalog()
    {
        using var store = NewStore();
        var saved = store.Upsert(Sample("studio-ssb", "Studio SSB", mic: -3));

        var read = store.Get("studio-ssb");
        Assert.NotNull(read);
        Assert.Equal("Studio SSB", read!.Name);
        Assert.Equal(-3, read.MicGainDb);
        Assert.Equal("blob", read.VstPluginStates["com.example.vst"]);
        Assert.Equal("3", read.NativePluginStates["com.example.eq"]["gain"]);
        Assert.Equal(55, read.TargetSpectralDensity);
    }

    [Fact]
    public void Upsert_OverwritesById_PreservesCreatedUtc()
    {
        using var store = NewStore();
        var first = store.Upsert(Sample("dx-punch", "DX Punch", mic: -2));
        var second = store.Upsert(Sample("dx-punch", "DX Punch v2", mic: -5));

        Assert.Equal(first.CreatedUtc, second.CreatedUtc);
        Assert.True(second.UpdatedUtc >= first.UpdatedUtc);
        Assert.Single(store.GetAll());
        Assert.Equal("DX Punch v2", store.Get("dx-punch")!.Name);
        Assert.Equal(-5, store.Get("dx-punch")!.MicGainDb);
    }

    [Fact]
    public void Upsert_NormalizesIdToLowerSlug()
    {
        using var store = NewStore();
        store.Upsert(Sample("Studio-SSB", "Studio SSB"));
        Assert.NotNull(store.Get("studio-ssb"));
        Assert.NotNull(store.Get("STUDIO-SSB")); // Get normalizes too
    }

    [Fact]
    public void Delete_RemovesProfile_AndClearsLastLoadedPointer()
    {
        using var store = NewStore();
        store.Upsert(Sample("essb-wide", "eSSB Wide"));
        store.SetLastLoadedId("essb-wide");
        Assert.Equal("essb-wide", store.GetLastLoadedId());

        Assert.True(store.Delete("essb-wide"));
        Assert.Null(store.Get("essb-wide"));
        Assert.Null(store.GetLastLoadedId());
    }

    [Fact]
    public void LastLoaded_PersistsAcrossInstances()
    {
        using (var first = NewStore())
        {
            first.Upsert(Sample("studio-ssb", "Studio SSB"));
            first.SetLastLoadedId("studio-ssb");
        }
        using var second = NewStore();
        Assert.Equal("studio-ssb", second.GetLastLoadedId());
        // Profiles persist too.
        Assert.NotNull(second.Get("studio-ssb"));
    }

    [Fact]
    public void Any_ReflectsCollectionState()
    {
        using var store = NewStore();
        Assert.False(store.Any());
        store.Upsert(Sample("studio-ssb", "Studio SSB"));
        Assert.True(store.Any());
    }

    [Fact]
    public void SetLastLoaded_NullClearsPointer()
    {
        using var store = NewStore();
        store.Upsert(Sample("studio-ssb", "Studio SSB"));
        store.SetLastLoadedId("studio-ssb");
        store.SetLastLoadedId(null);
        Assert.Null(store.GetLastLoadedId());
    }

    [Fact]
    public void Upsert_MirrorsProfileToFolder()
    {
        using var store = NewStore();
        store.Upsert(Sample("studio-ssb", "Studio SSB"));

        var file = Path.Combine(store.ProfileFolder, "studio-ssb.json");
        Assert.True(File.Exists(file));
        Assert.Contains("Studio SSB", File.ReadAllText(file));
    }

    [Fact]
    public void Delete_RemovesProfileFile()
    {
        using var store = NewStore();
        store.Upsert(Sample("dx-punch", "DX Punch"));
        var file = Path.Combine(store.ProfileFolder, "dx-punch.json");
        Assert.True(File.Exists(file));

        Assert.True(store.Delete("dx-punch"));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void NewStore_SyncsExistingProfilesToFolder()
    {
        using (var first = NewStore())
            first.Upsert(Sample("essb-wide", "eSSB Wide"));

        // Wipe the mirror folder, then re-open: the ctor must re-materialize it
        // from the DB rows.
        var folder = Path.Combine(_root, "tx-audio-profiles");
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);

        using var second = NewStore();
        Assert.True(File.Exists(Path.Combine(second.ProfileFolder, "essb-wide.json")));
    }

    [Fact]
    public void ParseJson_RoundTripsAndRejectsGarbage()
    {
        using var store = NewStore();
        var saved = store.Upsert(Sample("studio-ssb", "Studio SSB", mic: -4));
        var json = File.ReadAllText(Path.Combine(store.ProfileFolder, "studio-ssb.json"));

        var parsed = TxAudioProfileStore.ParseJson(json);
        Assert.NotNull(parsed);
        Assert.Equal("studio-ssb", parsed!.Id);
        Assert.Equal(-4, parsed.MicGainDb);

        Assert.Null(TxAudioProfileStore.ParseJson("not json"));
        Assert.Null(TxAudioProfileStore.ParseJson(""));
    }

    [Fact]
    public void Upsert_SanitizesSparseUnsafeProfile()
    {
        using var store = NewStore();
        var saved = store.Upsert(Sample("", "", mic: 99) with
        {
            LevelerMaxGainDb = double.PositiveInfinity,
            TxLeveling = new TxLevelingConfig(
                AlcMaxGainDb: double.NaN,
                AlcDecayMs: -1,
                LevelerDecayMs: 99_999,
                CompressorGainDb: double.NaN),
            CfcConfig = new CfcConfig(true, true, 0, 0, Array.Empty<CfcBand>()),
            LowCutHz = 0,
            HighCutHz = 0,
            ChainOrder = null!,
            ChainParked = null!,
            VstPluginStates = null!,
            NativePluginStates = null!,
            TargetSpectralDensity = -1,
        });

        Assert.Equal("profile", saved.Id);
        Assert.Equal("profile", saved.Name);
        Assert.Equal(10, saved.MicGainDb);
        Assert.Equal(8, saved.LevelerMaxGainDb);
        Assert.Equal(3, saved.TxLeveling.AlcMaxGainDb);
        Assert.Equal(1, saved.TxLeveling.AlcDecayMs);
        Assert.Equal(5000, saved.TxLeveling.LevelerDecayMs);
        Assert.Equal(10, saved.CfcConfig.Bands.Length);
        Assert.Equal(150, saved.LowCutHz);
        Assert.Equal(2900, saved.HighCutHz);
        Assert.Empty(saved.ChainOrder);
        Assert.Empty(saved.VstPluginStates);
        Assert.Empty(saved.NativePluginStates);
        Assert.Equal(55, saved.TargetSpectralDensity);
    }

    [Fact]
    public async Task StartupRepair_ParksVstIdsFromNativeLastLoadedProfile()
    {
        using var profiles = NewStore();
        using var chain = new ChainOrderStore(NullLogger<ChainOrderStore>.Instance, _dbPath);
        const string vstId = "com.openhpsdr.zeus.vst.clear";
        const string nativeId = "com.openhpsdr.zeus.samples.eq";

        profiles.Upsert(Sample("unsafe", "Unsafe") with
        {
            ProcessingMode = "native",
            ChainOrder = new List<string> { vstId, nativeId },
            ChainParked = new List<string>(),
            VstPluginStates = new Dictionary<string, string> { [vstId] = "opaque" },
        });
        profiles.SetLastLoadedId("unsafe");
        chain.SetState(new List<string> { vstId, nativeId }, new List<string>());

        var repair = new TxAudioProfileStartupRepairService(
            profiles,
            chain,
            NullLogger<TxAudioProfileStartupRepairService>.Instance);

        await repair.StartAsync(CancellationToken.None);

        Assert.Contains(vstId, chain.GetParked());
        Assert.DoesNotContain(nativeId, chain.GetParked());
    }
}
