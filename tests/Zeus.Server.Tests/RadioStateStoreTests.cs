// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

// Pin the behaviour of the snapshot store that drives RadioService rehydration
// (issue #287). Failures here mean an operator's VFO / mode / filter / zoom /
// per-board sample rate stop persisting across backend restart — the exact
// regression #287 was filed to prevent.
//
// Per-test temp DB keeps these hermetic; pre-isolation they would have shared
// zeus-prefs.db with production and any live operator state would have polluted
// the round-trip assertions.
public class RadioStateStoreTests : IDisposable
{
    private readonly string _dbPath;

    public RadioStateStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zeus-prefs-radiostate-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private RadioStateStore NewStore() =>
        new RadioStateStore(NullLogger<RadioStateStore>.Instance, _dbPath);

    [Fact]
    public void Get_OnFirstRun_ReturnsNull()
    {
        using var store = NewStore();
        Assert.Null(store.Get());
    }

    [Fact]
    public void Save_Then_Get_RoundTrips_AllFields()
    {
        using (var store = NewStore())
        {
            store.Save(new RadioStateEntry
            {
                VfoHz = 7_255_000,
                Mode = RxMode.LSB,
                FilterLowHz = -2400,
                FilterHighHz = -300,
                TxFilterLowHz = -2400,
                TxFilterHighHz = -300,
                FilterPresetName = "VAR2",
                AutoAttEnabled = false,
                AttenDb = 12,
                AutoAgcEnabled = true,
                RxAfGainDb = -6.5,
                ZoomLevel = 4,
                SsbFilterLoAbs = 300, SsbFilterHiAbs = 2400,
                CwFilterLoAbs = 400, CwFilterHiAbs = 800,
                SsbTxFilterLoAbs = 300, SsbTxFilterHiAbs = 2400,
                UpdatedUtc = DateTime.UtcNow,
            });
        }

        // Reopen — the second instance proves the data lives in LiteDB, not just memory.
        using var reopened = NewStore();
        var got = reopened.Get();
        Assert.NotNull(got);
        Assert.Equal(7_255_000, got!.VfoHz);
        Assert.Equal(RxMode.LSB, got.Mode);
        Assert.Equal(-2400, got.FilterLowHz);
        Assert.Equal(-300, got.FilterHighHz);
        Assert.Equal(-2400, got.TxFilterLowHz);
        Assert.Equal(-300, got.TxFilterHighHz);
        Assert.Equal("VAR2", got.FilterPresetName);
        Assert.False(got.AutoAttEnabled);
        Assert.Equal(12, got.AttenDb);
        Assert.True(got.AutoAgcEnabled);
        Assert.Equal(-6.5, got.RxAfGainDb);
        Assert.Equal(4, got.ZoomLevel);
        Assert.Equal(300, got.SsbFilterLoAbs);
        Assert.Equal(2400, got.SsbFilterHiAbs);
        Assert.Equal(400, got.CwFilterLoAbs);
        Assert.Equal(800, got.CwFilterHiAbs);
        Assert.Equal(300, got.SsbTxFilterLoAbs);
        Assert.Equal(2400, got.SsbTxFilterHiAbs);
    }

    // Snapshot is a single global row — saving twice should update, not insert.
    // RadioService's debounce flush hits Save() repeatedly on a live system;
    // if this drifted to insert-each-time the DB would grow unboundedly.
    [Fact]
    public void Save_TwiceWithDifferentData_UpdatesExistingRow_NotInsert()
    {
        using var store = NewStore();
        store.Save(new RadioStateEntry { VfoHz = 14_200_000, Mode = RxMode.USB });
        store.Save(new RadioStateEntry { VfoHz = 21_300_000, Mode = RxMode.USB });

        var got = store.Get();
        Assert.NotNull(got);
        Assert.Equal(21_300_000, got!.VfoHz);
    }

    [Fact]
    public void GetBoardSampleRate_OnUnseenBoard_ReturnsNull()
    {
        using var store = NewStore();
        Assert.Null(store.GetBoardSampleRate(HpsdrBoardKind.HermesLite2));
    }

    // The point of per-board sample-rate scoping: switching between a HL2 and
    // a G2 shouldn't bleed one radio's rate onto the other. The board-byte key
    // distinguishes them so each sees its own restored rate.
    [Fact]
    public void BoardSampleRate_Hl2_And_OrionMkII_Are_Independent()
    {
        using var store = NewStore();
        store.SetBoardSampleRate(HpsdrBoardKind.HermesLite2, 48_000);
        store.SetBoardSampleRate(HpsdrBoardKind.OrionMkII, 192_000, OrionMkIIVariant.G2);

        Assert.Equal(48_000, store.GetBoardSampleRate(HpsdrBoardKind.HermesLite2));
        Assert.Equal(192_000, store.GetBoardSampleRate(HpsdrBoardKind.OrionMkII, OrionMkIIVariant.G2));
    }

    // The 0x0A wire-byte alias family (G2 / G2-1K / 7000DLE / 8000DLE / ...)
    // all report the same board byte. The variant byte disambiguates them in
    // the key so an operator who swaps a G2 for a G2-1K doesn't restore the
    // wrong rate. If this regresses we'll silently mis-rate one of the family.
    [Fact]
    public void BoardSampleRate_OrionMkII_G2_vs_G2_1K_Are_Independent()
    {
        using var store = NewStore();
        store.SetBoardSampleRate(HpsdrBoardKind.OrionMkII, 96_000, OrionMkIIVariant.G2);
        store.SetBoardSampleRate(HpsdrBoardKind.OrionMkII, 384_000, OrionMkIIVariant.G2_1K);

        Assert.Equal(96_000, store.GetBoardSampleRate(HpsdrBoardKind.OrionMkII, OrionMkIIVariant.G2));
        Assert.Equal(384_000, store.GetBoardSampleRate(HpsdrBoardKind.OrionMkII, OrionMkIIVariant.G2_1K));
    }

    // Setting the same board twice should update the existing entry, not
    // accumulate duplicates. With unique indexing on BoardKey a duplicate
    // insert would actually throw; this test pins the "second write wins"
    // contract.
    [Fact]
    public void SetBoardSampleRate_Twice_UpdatesExisting()
    {
        using var store = NewStore();
        store.SetBoardSampleRate(HpsdrBoardKind.HermesLite2, 48_000);
        store.SetBoardSampleRate(HpsdrBoardKind.HermesLite2, 96_000);

        Assert.Equal(96_000, store.GetBoardSampleRate(HpsdrBoardKind.HermesLite2));
    }

    // dbPathOverride is the path-injection seam used by /run fresh
    // (ZEUS_PREFS_PATH) and by these tests. If this stopped honoring the
    // override, /run fresh would silently scribble on the production DB.
    [Fact]
    public void DbPathOverride_TargetsSpecifiedFile()
    {
        Assert.False(File.Exists(_dbPath));
        using (var store = NewStore())
        {
            store.Save(new RadioStateEntry { VfoHz = 1_800_000 });
        }
        Assert.True(File.Exists(_dbPath));
    }
}
