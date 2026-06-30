//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Zeus.Contracts;

namespace Zeus.Server.Tests;

public sealed class TxPhaseRotatorSettingsPersistenceTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-txphrot-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private DspSettingsStore BuildStore() =>
        new(NullLogger<DspSettingsStore>.Instance, _dbPath);

    [Fact]
    public void SetTxPhaseRotator_FullConfig_RoundTripsAllFields()
    {
        var cfg = new TxPhaseRotatorConfig(
            Enabled: true,
            CornerHz: 472,
            Stages: 10,
            Reverse: true);

        using (var store = BuildStore())
            store.SetTxPhaseRotator(cfg);

        using var fresh = BuildStore();
        var back = fresh.GetTxPhaseRotator();
        Assert.NotNull(back);
        Assert.True(back!.Enabled);
        Assert.Equal(472, back.CornerHz);
        Assert.Equal(10, back.Stages);
        Assert.True(back.Reverse);
    }

    [Fact]
    public void GetTxPhaseRotator_FreshOrLegacyStore_ReturnsNull()
    {
        using (var store = BuildStore())
            Assert.Null(store.GetTxPhaseRotator());

        using (var store = BuildStore())
            store.Upsert(new NrConfig(NrMode: NrMode.Off));

        using var fresh = BuildStore();
        Assert.Null(fresh.GetTxPhaseRotator());
    }

    [Fact]
    public void SetTxPhaseRotator_UpsertOverwritesExistingConfig()
    {
        using var store = BuildStore();
        store.SetTxPhaseRotator(new TxPhaseRotatorConfig(Enabled: true, CornerHz: 300, Stages: 6));
        store.SetTxPhaseRotator(new TxPhaseRotatorConfig(Enabled: false, CornerHz: 860, Stages: 12, Reverse: true));

        var back = store.GetTxPhaseRotator();
        Assert.NotNull(back);
        Assert.False(back!.Enabled);
        Assert.Equal(860, back.CornerHz);
        Assert.Equal(12, back.Stages);
        Assert.True(back.Reverse);
    }
}
