// SPDX-License-Identifier: GPL-2.0-or-later
//
// AudioProcessingModeStore — first-run = null (caller substitutes the
// Native-by-default route) and round-trip after explicit set. The
// null-on-first-run contract is load-bearing: a brand-new operator must
// land on the native chain, never silently in VST mode.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class AudioProcessingModeStoreTests : IDisposable
{
    private readonly string _dbPath;

    public AudioProcessingModeStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zeus-prefs-audioprocmode-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private AudioProcessingModeStore NewStore() =>
        new AudioProcessingModeStore(NullLogger<AudioProcessingModeStore>.Instance, _dbPath);

    [Fact]
    public void FirstRun_ReturnsNull()
    {
        using var store = NewStore();
        Assert.Null(store.GetMode());
    }

    [Fact]
    public void SetThenGet_RoundTripsVst()
    {
        using var store = NewStore();
        store.SetMode(AudioProcessingMode.Vst);
        Assert.Equal(AudioProcessingMode.Vst, store.GetMode());
    }

    [Fact]
    public void SetThenGet_RoundTripsNative()
    {
        using var store = NewStore();
        // Explicit set to Native (the enum's zero value) — verifies a row was
        // actually written, vs the store returning null for no row.
        store.SetMode(AudioProcessingMode.Native);
        var got = store.GetMode();
        Assert.NotNull(got);
        Assert.Equal(AudioProcessingMode.Native, got);
    }

    [Fact]
    public void SecondSet_OverwritesFirst()
    {
        using var store = NewStore();
        store.SetMode(AudioProcessingMode.Vst);
        store.SetMode(AudioProcessingMode.Native);
        Assert.Equal(AudioProcessingMode.Native, store.GetMode());
    }

    [Fact]
    public void StatePersistsAcrossStoreInstances()
    {
        using (var first = NewStore())
        {
            first.SetMode(AudioProcessingMode.Vst);
        }
        using var second = NewStore();
        Assert.Equal(AudioProcessingMode.Vst, second.GetMode());
    }
}
