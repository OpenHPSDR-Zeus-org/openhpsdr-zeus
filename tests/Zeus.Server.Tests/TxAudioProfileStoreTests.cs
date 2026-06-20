// SPDX-License-Identifier: GPL-2.0-or-later
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class TxAudioProfileStoreTests : IDisposable
{
    private readonly string _dbPath;

    public TxAudioProfileStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zeus-prefs-txaudioprofiles-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
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
}
