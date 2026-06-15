// SPDX-License-Identifier: GPL-2.0-or-later
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class CfcPresetStoreTests : IDisposable
{
    private readonly string _dbPath;

    public CfcPresetStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zeus-prefs-cfcpresets-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private CfcPresetStore NewStore() =>
        new(NullLogger<CfcPresetStore>.Instance, _dbPath);

    [Fact]
    public void Save_OverwritesExistingNameCaseInsensitively()
    {
        using var store = NewStore();

        var first = store.Save("Ragchew", Config(preCompDb: 1.0, band5CompDb: 2.0));
        var updated = store.Save("ragchew", Config(preCompDb: 3.0, band5CompDb: 4.0));
        var presets = store.List();

        Assert.Single(presets);
        Assert.Equal("ragchew", updated.Name);
        Assert.True(
            Math.Abs((updated.CreatedUtc - first.CreatedUtc).TotalSeconds) < 1.0,
            "Overwrite should preserve the original created timestamp.");
        Assert.True(updated.UpdatedUtc >= first.UpdatedUtc.AddSeconds(-1));
        Assert.Equal(3.0, presets[0].Config.PreCompDb);
        Assert.Equal(4.0, presets[0].Config.Bands[4].CompLevelDb);
        Assert.Equal(4.0, store.Get("RAGCHEW")!.Config.Bands[4].CompLevelDb);
    }

    [Fact]
    public void Save_PersistsAcrossStoreInstances()
    {
        using (var first = NewStore())
            first.Save("DX Wide", Config(preCompDb: 1.5, band5CompDb: 6.5));

        using var second = NewStore();
        var preset = second.Get("dx wide");

        Assert.NotNull(preset);
        Assert.Equal("DX Wide", preset!.Name);
        Assert.Equal(1.5, preset.Config.PreCompDb);
        Assert.Equal(6.5, preset.Config.Bands[4].CompLevelDb);
        Assert.Equal(10, preset.Config.Bands.Length);
    }

    [Fact]
    public void Save_RejectsBlankName()
    {
        using var store = NewStore();

        Assert.Throws<ArgumentException>(() => store.Save(" ", CfcConfig.Default));
    }

    private static CfcConfig Config(double preCompDb, double band5CompDb)
    {
        var bands = CfcConfig.Default.Bands
            .Select((band, idx) => idx == 4
                ? band with { CompLevelDb = band5CompDb }
                : band with { })
            .ToArray();

        return CfcConfig.Default with
        {
            Enabled = true,
            PostEqEnabled = true,
            PreCompDb = preCompDb,
            Bands = bands,
        };
    }
}
