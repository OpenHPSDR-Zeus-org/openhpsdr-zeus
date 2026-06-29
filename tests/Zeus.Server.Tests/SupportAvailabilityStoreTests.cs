// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server.Hosting.Support;

namespace Zeus.Server.Tests;

public sealed class SupportAvailabilityStoreTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-support-avail-{Guid.NewGuid():N}.db");

    private SupportAvailabilityStore New() =>
        new(NullLogger<SupportAvailabilityStore>.Instance, _dbPath);

    [Fact]
    public void DefaultsOff_OnFirstRun()
    {
        using var store = New();
        Assert.False(store.IsAvailable);
        Assert.False(store.AutoShareOnCrash);
    }

    [Fact]
    public void Set_TogglesAvailable_AndAutoShare()
    {
        using var store = New();
        var (available, autoShare) = store.Set(available: true, autoShareCrashes: true);
        Assert.True(available);
        Assert.True(autoShare);
        Assert.True(store.IsAvailable);
        Assert.True(store.AutoShareOnCrash);
    }

    [Fact]
    public void Set_FlagsAreIndependent()
    {
        using var store = New();
        store.Set(available: true, autoShareCrashes: false);
        Assert.True(store.IsAvailable);
        Assert.False(store.AutoShareOnCrash);

        store.Set(available: false, autoShareCrashes: true);
        Assert.False(store.IsAvailable);
        Assert.True(store.AutoShareOnCrash);
    }

    [Fact]
    public void Persists_AcrossReopen()
    {
        using (var store = New())
            store.Set(available: true, autoShareCrashes: true);

        // A fresh store over the same DB file must read back the opt-in.
        using var reopened = New();
        Assert.True(reopened.IsAvailable);
        Assert.True(reopened.AutoShareOnCrash);
    }

    [Fact]
    public void Set_OverwritesSingleRow_NoDuplicate()
    {
        using var store = New();
        store.Set(available: true, autoShareCrashes: true);
        store.Set(available: false, autoShareCrashes: false);
        Assert.False(store.IsAvailable);
        Assert.False(store.AutoShareOnCrash);
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* best effort */ }
        try { if (File.Exists(_dbPath + "-log")) File.Delete(_dbPath + "-log"); } catch { /* best effort */ }
    }
}
