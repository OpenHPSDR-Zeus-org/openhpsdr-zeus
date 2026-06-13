// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Persistence coverage for issue #478 — panadapter dB scale now survives a
// backend restart. Verifies that all eight dB range fields round-trip through
// LiteDB and that a fresh (or legacy) row returns null so the frontend knows
// to fall back to its built-in defaults and push the localStorage value up.

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Zeus.Server.Tests;

public class DisplaySettingsPersistenceTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-display-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private DisplaySettingsStore BuildStore() =>
        new(NullLogger<DisplaySettingsStore>.Instance, _dbPath);

    [Fact]
    public void FreshDb_ReturnsNullForAllDbRangeFields()
    {
        using var store = BuildStore();
        var dto = store.Get();

        Assert.Null(dto.DbMin);
        Assert.Null(dto.DbMax);
        Assert.Null(dto.TxDbMin);
        Assert.Null(dto.TxDbMax);
        Assert.Null(dto.WfDbMin);
        Assert.Null(dto.WfDbMax);
        Assert.Null(dto.WfTxDbMin);
        Assert.Null(dto.WfTxDbMax);
    }

    [Fact]
    public void SaveMode_WithDbRanges_PersistsAllFields()
    {
        using (var store = BuildStore())
        {
            store.SaveMode("basic", "fill", "#FFA028",
                dbMin: -130, dbMax: -60,
                txDbMin: -75, txDbMax: 15,
                wfDbMin: -125, wfDbMax: -55,
                wfTxDbMin: -70, wfTxDbMax: 10);
        }

        // Reopen to prove the values survived the LiteDB file round-trip.
        using var fresh = BuildStore();
        var dto = fresh.Get();

        Assert.Equal(-130, dto.DbMin);
        Assert.Equal(-60, dto.DbMax);
        Assert.Equal(-75, dto.TxDbMin);
        Assert.Equal(15, dto.TxDbMax);
        Assert.Equal(-125, dto.WfDbMin);
        Assert.Equal(-55, dto.WfDbMax);
        Assert.Equal(-70, dto.WfTxDbMin);
        Assert.Equal(10, dto.WfTxDbMax);
    }

    [Fact]
    public void SaveMode_NullDbRanges_DoNotOverwriteExistingValues()
    {
        // Write concrete dB values first.
        using (var store = BuildStore())
        {
            store.SaveMode("basic", "fill", "#FFA028",
                dbMin: -130, dbMax: -60,
                txDbMin: -75, txDbMax: 15,
                wfDbMin: -125, wfDbMax: -55,
                wfTxDbMin: -70, wfTxDbMax: 10);
        }

        // Update mode/fit/color only — no dB range args (default null).
        using (var update = BuildStore())
        {
            update.SaveMode("beam-map", "fill", "#FF8800");
        }

        // dB values must still be the originals.
        using var check = BuildStore();
        var dto = check.Get();

        Assert.Equal("beam-map", dto.Mode);
        Assert.Equal(-130, dto.DbMin);
        Assert.Equal(-60, dto.DbMax);
        Assert.Equal(-75, dto.TxDbMin);
        Assert.Equal(15, dto.TxDbMax);
    }

    [Fact]
    public void SaveMode_UpdateDbRanges_OverwritesExistingValues()
    {
        using var store = BuildStore();
        store.SaveMode("basic", "fill", "#FFA028",
            dbMin: -140, dbMax: -50);
        store.SaveMode("basic", "fill", "#FFA028",
            dbMin: -120, dbMax: -40);

        var dto = store.Get();
        Assert.Equal(-120, dto.DbMin);
        Assert.Equal(-40, dto.DbMax);
    }

    // Regression for the "white waterfall" symptom: dragging the waterfall dB
    // scale far enough used to push both endpoints to the same ±DB_ABS_LIMIT
    // wall, persisting min == max. Next page load mapped the entire colormap
    // input to one colour. The store now drops degenerate writes so a buggy
    // client (or a stray API call) can't leave the DB in that state.
    [Theory]
    [InlineData(-200, -200)] // both at -DbAbsLimit (the original symptom)
    [InlineData(-50, -50)]   // min == max anywhere
    [InlineData(-60, -50)]   // span (10) below MinSpanDb (20)
    [InlineData(-50, -60)]   // inverted
    [InlineData(-300, -50)]  // min outside abs limit
    [InlineData(-50, 300)]   // max outside abs limit
    public void SaveMode_DegenerateWfRange_IsRejectedAndPriorValueKept(double badMin, double badMax)
    {
        using var store = BuildStore();
        store.SaveMode("basic", "fill", "#FFA028",
            wfDbMin: -125, wfDbMax: -55);

        store.SaveMode("basic", "fill", "#FFA028",
            wfDbMin: badMin, wfDbMax: badMax);

        var dto = store.Get();
        Assert.Equal(-125, dto.WfDbMin);
        Assert.Equal(-55, dto.WfDbMax);
    }

    [Fact]
    public void SaveMode_PartialDegenerateRange_OnlyAffectedPairIsRejected()
    {
        using var store = BuildStore();
        store.SaveMode("basic", "fill", "#FFA028",
            dbMin: -130, dbMax: -60,
            wfDbMin: -125, wfDbMax: -55);

        // Valid pan update, but degenerate wf update in the same call.
        store.SaveMode("basic", "fill", "#FFA028",
            dbMin: -120, dbMax: -50,
            wfDbMin: -200, wfDbMax: -200);

        var dto = store.Get();
        Assert.Equal(-120, dto.DbMin);
        Assert.Equal(-50, dto.DbMax);
        Assert.Equal(-125, dto.WfDbMin);
        Assert.Equal(-55, dto.WfDbMax);
    }
}
