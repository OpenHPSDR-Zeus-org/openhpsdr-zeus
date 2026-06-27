// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Persistence coverage for the radio-side speaker output opt-in (Protocol-1
// codec radios). Default is OFF (safe, opt-in); the choice survives a backend
// restart; the Changed event fires only on an actual transition.

using Microsoft.Extensions.Logging.Abstractions;

namespace Zeus.Server.Tests;

public class RadioSpeakerSettingsStoreTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-spk-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private RadioSpeakerSettingsStore BuildStore() =>
        new(NullLogger<RadioSpeakerSettingsStore>.Instance, _dbPath);

    [Fact]
    public void FreshDb_DefaultsToOff()
    {
        using var store = BuildStore();
        Assert.False(store.Enabled);
        Assert.False(RadioSpeakerSettingsStore.DefaultEnabled);
    }

    [Fact]
    public void Set_RoundTripsThroughDb()
    {
        using (var store = BuildStore())
        {
            store.Set(true);
            Assert.True(store.Enabled);
        }
        // New instance, same file — value survives a "restart".
        using (var reopened = BuildStore())
        {
            Assert.True(reopened.Enabled);
        }
    }

    [Fact]
    public void Set_BackToOff_RoundTrips()
    {
        using var store = BuildStore();
        store.Set(true);
        store.Set(false);
        Assert.False(store.Enabled);
    }

    [Fact]
    public void Changed_FiresOnlyOnActualTransition()
    {
        using var store = BuildStore();
        int fires = 0;
        store.Changed += () => fires++;

        store.Set(false);   // no change (already off) — no fire
        Assert.Equal(0, fires);

        store.Set(true);    // off -> on
        Assert.Equal(1, fires);

        store.Set(true);    // no change (already on) — no fire
        Assert.Equal(1, fires);

        store.Set(false);   // on -> off
        Assert.Equal(2, fires);
    }
}
