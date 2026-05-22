// SPDX-License-Identifier: GPL-2.0-or-later
//
// AudioChainSettingsStore — first-run = null (caller substitutes the
// true-by-default master bypass) and round-trip after explicit set.
// Pin the null-on-first-run contract because the operator-facing
// default-true behaviour rides on it.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class AudioChainSettingsStoreTests : IDisposable
{
    private readonly string _dbPath;

    public AudioChainSettingsStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zeus-prefs-audiochainsettings-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private AudioChainSettingsStore NewStore() =>
        new AudioChainSettingsStore(NullLogger<AudioChainSettingsStore>.Instance, _dbPath);

    [Fact]
    public void FirstRun_ReturnsNull()
    {
        using var store = NewStore();
        Assert.Null(store.GetMasterBypassed());
    }

    [Fact]
    public void SetThenGet_RoundTripsTrue()
    {
        using var store = NewStore();
        store.SetMasterBypassed(true);
        Assert.True(store.GetMasterBypassed());
    }

    [Fact]
    public void SetThenGet_RoundTripsFalse()
    {
        using var store = NewStore();
        // Explicit set to false even though that's the "uninitialized
        // bool" value — verifies the row was actually written (vs the
        // store returning null because no row exists).
        store.SetMasterBypassed(false);
        var got = store.GetMasterBypassed();
        Assert.NotNull(got);
        Assert.False(got);
    }

    [Fact]
    public void SecondSet_OverwritesFirst()
    {
        using var store = NewStore();
        store.SetMasterBypassed(true);
        store.SetMasterBypassed(false);
        var got = store.GetMasterBypassed();
        Assert.NotNull(got);
        Assert.False(got);
    }

    [Fact]
    public void StatePersistsAcrossStoreInstances()
    {
        using (var first = NewStore())
        {
            first.SetMasterBypassed(false);
        }
        using var second = NewStore();
        var got = second.GetMasterBypassed();
        Assert.NotNull(got);
        Assert.False(got);
    }
}
