// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// External-ports plan, Phase 4 — global audio front-end persistence round-trip.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class AudioSettingsStoreTests : IDisposable
{
    private readonly string _dbPath;

    public AudioSettingsStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zeus-prefs-audio-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private AudioSettingsStore NewStore() =>
        new AudioSettingsStore(NullLogger<AudioSettingsStore>.Instance, _dbPath);

    [Fact]
    public void Unset_Defaults_To_MicInput_NoBias()
    {
        using var store = NewStore();
        var sel = store.Get();
        Assert.False(sel.LineIn);
        Assert.False(sel.MicBoost);
        Assert.False(sel.MicBias); // mic_bias defaults OFF
        Assert.False(sel.BalancedInput);
        Assert.Equal(0, sel.LineInGain);
    }

    [Fact]
    public void Set_RoundTrips_AcrossReopen()
    {
        using (var store = NewStore())
        {
            store.Set(new AudioFrontEndSelection(
                LineIn: true, MicBoost: true, MicBias: true, BalancedInput: true, LineInGain: 21));
        }

        using var reopened = NewStore();
        var sel = reopened.Get();
        Assert.True(sel.LineIn);
        Assert.True(sel.MicBoost);
        Assert.True(sel.MicBias);
        Assert.True(sel.BalancedInput);
        Assert.Equal(21, sel.LineInGain);
    }

    [Fact]
    public void Set_Twice_Updates_NotDuplicates_DeleteManyInsert()
    {
        // The DeleteMany+Insert upsert must replace the single global row, not
        // accumulate rows (the LiteDB Id=0-always-inserts bug, PR #387).
        using var store = NewStore();
        store.Set(new AudioFrontEndSelection(true, false, false, false, 5));
        store.Set(new AudioFrontEndSelection(false, true, true, false, 12));

        var sel = store.Get();
        Assert.False(sel.LineIn);
        Assert.True(sel.MicBoost);
        Assert.True(sel.MicBias);
        Assert.Equal(12, sel.LineInGain);
    }

    [Fact]
    public void LineInGain_ClampedTo31()
    {
        using var store = NewStore();
        store.Set(new AudioFrontEndSelection(false, false, false, false, 99));
        Assert.Equal(31, store.Get().LineInGain);
    }

    [Fact]
    public void Changed_Fires_On_Set()
    {
        using var store = NewStore();
        int fired = 0;
        store.Changed += () => fired++;
        store.Set(AudioFrontEndSelection.Default with { MicBoost = true });
        Assert.Equal(1, fired);
    }
}
