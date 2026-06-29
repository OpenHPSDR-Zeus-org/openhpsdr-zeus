// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// PttSettingsStore — per-install hardware-PTT-IN → MOX enable gate.
//   • Default ON: a fresh DB (no row) returns true (Thetis-faithful — the
//     footswitch keys out of the box; promotion is still edge-triggered so a
//     persisted/default ON never auto-keys on its own).
//   • Set/Get round-trips both directions.
//   • The flag PERSISTS across a store re-open on the same DB path (a persisted
//     ON flag arms the gate after restart; it never auto-keys MOX — that stays
//     edge-triggered in ExternalPttService).

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class PttSettingsStoreTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-pttstore-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public void Get_FreshDb_DefaultsOn()
    {
        using var store = new PttSettingsStore(NullLogger<PttSettingsStore>.Instance, _dbPath);
        Assert.True(store.Get());
    }

    [Fact]
    public void SetGet_RoundTripsBothDirections()
    {
        using var store = new PttSettingsStore(NullLogger<PttSettingsStore>.Instance, _dbPath);

        store.Set(true);
        Assert.True(store.Get());

        store.Set(false);
        Assert.False(store.Get());
    }

    [Fact]
    public void Set_RaisesChanged()
    {
        using var store = new PttSettingsStore(NullLogger<PttSettingsStore>.Instance, _dbPath);
        int fired = 0;
        store.Changed += () => fired++;

        store.Set(true);
        store.Set(false);

        Assert.Equal(2, fired);
    }

    [Fact]
    public void Enabled_PersistsAcrossReopen()
    {
        // Set ON, dispose, reopen on the same DB path — the gate stays armed.
        using (var store = new PttSettingsStore(NullLogger<PttSettingsStore>.Instance, _dbPath))
        {
            store.Set(true);
        }

        using (var reopened = new PttSettingsStore(NullLogger<PttSettingsStore>.Instance, _dbPath))
        {
            Assert.True(reopened.Get());
        }
    }

    [Fact]
    public void Disabled_PersistsAcrossReopen()
    {
        // Flipping back OFF must also survive a restart (no stale ON row).
        using (var store = new PttSettingsStore(NullLogger<PttSettingsStore>.Instance, _dbPath))
        {
            store.Set(true);
            store.Set(false);
        }

        using (var reopened = new PttSettingsStore(NullLogger<PttSettingsStore>.Instance, _dbPath))
        {
            Assert.False(reopened.Get());
        }
    }
}
