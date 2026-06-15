// SPDX-License-Identifier: GPL-2.0-or-later
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class AudioProfileStoreTests : IDisposable
{
    private readonly string _dbPath;

    public AudioProfileStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zeus-prefs-audioprofiles-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private AudioProfileStore NewStore() =>
        new(NullLogger<AudioProfileStore>.Instance, _dbPath);

    [Fact]
    public void Save_RecordsProcessingMode()
    {
        using var store = NewStore();

        var entry = store.Save(
            "VST rack",
            AudioProcessingMode.Vst,
            new[] { "com.example.vst" },
            Array.Empty<string>(),
            masterBypass: false,
            new Dictionary<string, string> { ["com.example.vst"] = "state" });

        Assert.Equal(AudioProcessingMode.Vst, entry.ProcessingMode);
        Assert.Equal(AudioProcessingMode.Vst, store.Get("VST rack")!.ProcessingMode);
    }

    [Fact]
    public void Save_OverwritesProcessingMode()
    {
        using var store = NewStore();

        store.Save(
            "Voice",
            AudioProcessingMode.Native,
            new[] { "com.openhpsdr.zeus.samples.eq" },
            Array.Empty<string>(),
            masterBypass: true);
        var updated = store.Save(
            "Voice",
            AudioProcessingMode.Vst,
            new[] { "com.example.vst" },
            Array.Empty<string>(),
            masterBypass: false);

        Assert.Equal(AudioProcessingMode.Vst, updated.ProcessingMode);
        Assert.Equal(new[] { "com.example.vst" }, updated.Order);
        Assert.False(updated.MasterBypass);
    }

    [Fact]
    public void ProcessingMode_PersistsAcrossStoreInstances()
    {
        using (var first = NewStore())
        {
            first.Save(
                "Native rack",
                AudioProcessingMode.Native,
                new[] { "com.openhpsdr.zeus.samples.compressor" },
                Array.Empty<string>(),
                masterBypass: true);
        }

        using var second = NewStore();
        var entry = second.Get("Native rack");

        Assert.NotNull(entry);
        Assert.Equal(AudioProcessingMode.Native, entry!.ProcessingMode);
        Assert.Equal(new[] { "com.openhpsdr.zeus.samples.compressor" }, entry.Order);
    }
}
