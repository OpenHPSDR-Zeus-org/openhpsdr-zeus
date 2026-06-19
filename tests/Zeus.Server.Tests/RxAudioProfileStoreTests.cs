// SPDX-License-Identifier: GPL-2.0-or-later
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class RxAudioProfileStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(),
        $"zeus-prefs-rx-audioprofiles-{Guid.NewGuid():N}.db");

    [Fact]
    public void SelectedProfile_PersistsAcrossStoreInstances()
    {
        using (var first = NewStore())
        {
            first.Save(
                "Clear receive",
                ["com.openhpsdr.zeus.rxvst.clear"],
                [],
                masterBypass: false);
            first.SetSelectedProfile("Clear receive");
        }

        using var second = NewStore();

        Assert.Equal("Clear receive", second.GetSelectedProfile());
    }

    [Fact]
    public void Delete_ClearsSelectedProfile()
    {
        using var store = NewStore();
        store.Save(
            "Clear receive",
            ["com.openhpsdr.zeus.rxvst.clear"],
            [],
            masterBypass: false);
        store.SetSelectedProfile("Clear receive");

        Assert.True(store.Delete("Clear receive"));

        Assert.Null(store.GetSelectedProfile());
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private RxAudioProfileStore NewStore() =>
        new(NullLogger<RxAudioProfileStore>.Instance, _dbPath);
}
