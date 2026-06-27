// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Pins the FT8/FT4/WSPR settings store: defaults match the pre-settings
// behaviour (so surfacing them changes nothing an operator feels), out-of-range
// POSTs are clamped on write, each digital mode (FT8/FT4/WSPR) persists its OWN
// row, the waterfall/display block survives a store reopen, and a legacy single
// un-keyed row is migrated cleanly to FT8 (the desktop-restart fix this whole
// feature exists for).

using LiteDB;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class Ft8SettingsStoreTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-ft8-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private Ft8SettingsStore NewStore() =>
        new(NullLogger<Ft8SettingsStore>.Instance, _dbPath);

    [Fact]
    public void Defaults_Match_Pre_Settings_Behaviour()
    {
        using var store = NewStore();
        var s = store.Get();

        Assert.True(s.AutoSequence);
        Assert.True(s.DisableTxAfter73);
        Assert.False(s.CallFirst);
        Assert.False(s.HoldTxFreq);
        Assert.Equal(0, s.DefaultTxSlot);
        Assert.Equal(1500, s.DefaultTxOffsetHz);
        Assert.Equal(3, s.DecodePasses); // matches Ft8Service.DecodePasses (Deep/multi)
        Assert.True(s.Rr73InsteadOfRrr); // RR73 ack — the engine's pre-settings default
        Assert.True(s.AutoLog);
        Assert.False(s.PromptBeforeLog);
        Assert.True(s.ClearDxAfterLog);
        Assert.Equal("CQ", s.CqMessage);
        Assert.Equal("CQ DX", s.CqDxMessage);
    }

    [Fact]
    public void Display_Defaults_Are_Sane()
    {
        using var store = NewStore();
        var s = store.Get();

        Assert.Equal(Ft8Settings.DefaultWfDbMin, s.WfDbMin);
        Assert.Equal(Ft8Settings.DefaultWfDbMax, s.WfDbMax);
        Assert.Equal("blue", s.Palette);
        Assert.Equal("auto", s.Rbw);
        Assert.Equal(0, s.Smoothing);
        Assert.Equal(1.0, s.Zoom);
        Assert.Equal(3000, s.SpanHz);
    }

    [Fact]
    public void Set_Clamps_Offset_Passes_And_Slot()
    {
        using var store = NewStore();
        var saved = store.Set(new Ft8Settings(
            DefaultTxOffsetHz: 99999,
            DecodePasses: 9,
            DefaultTxSlot: 7,
            CallerMaxRetries: -3));

        Assert.Equal(Ft8Settings.MaxTxOffsetHz, saved.DefaultTxOffsetHz);
        Assert.Equal(Ft8Settings.MaxPasses, saved.DecodePasses);
        Assert.Equal(1, saved.DefaultTxSlot); // any non-zero collapses to 1 (odd)
        Assert.Equal(0, saved.CallerMaxRetries);
    }

    [Fact]
    public void Set_Clamps_Offset_Below_Minimum()
    {
        using var store = NewStore();
        var saved = store.Set(new Ft8Settings(DefaultTxOffsetHz: 10));
        Assert.Equal(Ft8Settings.MinOffsetHz, saved.DefaultTxOffsetHz);
    }

    [Fact]
    public void Set_Caps_Free_Text_Macro_To_Thirteen()
    {
        using var store = NewStore();
        var saved = store.Set(new Ft8Settings(FreeTextMacro: "THIS IS WAY TOO LONG FOR FT8"));
        Assert.Equal(13, saved.FreeTextMacro.Length);
    }

    [Fact]
    public void Set_Clamps_And_Whitelists_Display_Fields()
    {
        using var store = NewStore();
        var saved = store.Set(new Ft8Settings(
            WfDbMin: -50, WfDbMax: -140, // inverted span → reset to defaults
            Palette: "rainbow",          // not whitelisted → default
            Rbw: "  ",                   // blank → default
            Smoothing: 99,               // out of range → clamp to max
            Zoom: 0.1,                   // below min → clamp to 1.0
            SpanHz: 99999));             // out of range → clamp to max

        Assert.Equal(Ft8Settings.DefaultWfDbMin, saved.WfDbMin);
        Assert.Equal(Ft8Settings.DefaultWfDbMax, saved.WfDbMax);
        Assert.Equal("blue", saved.Palette);
        Assert.Equal("auto", saved.Rbw);
        Assert.Equal(Ft8Settings.MaxSmoothing, saved.Smoothing);
        Assert.Equal(Ft8Settings.MinZoom, saved.Zoom);
        Assert.Equal(Ft8Settings.MaxSpanHz, saved.SpanHz);
    }

    [Fact]
    public void Set_Persists_Across_Store_Instances()
    {
        using (var store = NewStore())
            store.Set(new Ft8Settings(
                AutoSequence: false, CallFirst: true, DecodePasses: 4,
                DefaultTxOffsetHz: 1200, AutoLog: false));

        using var reopened = NewStore();
        var s = reopened.Get();
        Assert.False(s.AutoSequence);
        Assert.True(s.CallFirst);
        Assert.Equal(4, s.DecodePasses);
        Assert.Equal(1200, s.DefaultTxOffsetHz);
        Assert.False(s.AutoLog);
    }

    [Fact]
    public void Display_Prefs_Persist_Across_Store_Instances()
    {
        using (var store = NewStore())
            store.Set("FT8", new Ft8Settings(
                WfDbMin: -120, WfDbMax: -40,
                Palette: "viridis", Rbw: "6.25", Smoothing: 3,
                Zoom: 4.0, SpanHz: 2500));

        using var reopened = NewStore();
        var s = reopened.Get("FT8");
        Assert.Equal(-120, s.WfDbMin);
        Assert.Equal(-40, s.WfDbMax);
        Assert.Equal("viridis", s.Palette);
        Assert.Equal("6.25", s.Rbw);
        Assert.Equal(3, s.Smoothing);
        Assert.Equal(4.0, s.Zoom);
        Assert.Equal(2500, s.SpanHz);
    }

    [Fact]
    public void Modes_Are_Independent_Rows()
    {
        using var store = NewStore();
        store.Set("FT8", new Ft8Settings(DefaultTxOffsetHz: 1100, Palette: "blue"));
        store.Set("FT4", new Ft8Settings(DefaultTxOffsetHz: 2200, Palette: "inferno"));
        store.Set("WSPR", new Ft8Settings(DefaultTxOffsetHz: 1500, Palette: "viridis"));

        Assert.Equal(1100, store.Get("FT8").DefaultTxOffsetHz);
        Assert.Equal("blue", store.Get("FT8").Palette);
        Assert.Equal(2200, store.Get("FT4").DefaultTxOffsetHz);
        Assert.Equal("inferno", store.Get("FT4").Palette);
        Assert.Equal("viridis", store.Get("WSPR").Palette);
    }

    [Fact]
    public void Per_Mode_Persists_Across_Store_Instances()
    {
        using (var store = NewStore())
        {
            store.Set("FT8", new Ft8Settings(DecodePasses: 1));
            store.Set("FT4", new Ft8Settings(DecodePasses: 4));
        }

        using var reopened = NewStore();
        Assert.Equal(1, reopened.Get("FT8").DecodePasses);
        Assert.Equal(4, reopened.Get("FT4").DecodePasses);
        // A mode never written returns defaults, not another mode's row.
        Assert.Equal(3, reopened.Get("WSPR").DecodePasses);
    }

    [Fact]
    public void Unknown_Mode_Normalizes_To_Ft8()
    {
        using var store = NewStore();
        store.Set("FT8", new Ft8Settings(DefaultTxOffsetHz: 1234));

        // A bogus mode token resolves to the FT8 row on both read and write.
        Assert.Equal(1234, store.Get("nonsense").DefaultTxOffsetHz);
        Assert.Equal("FT8", Ft8SettingsStore.NormalizeMode("nonsense"));
        Assert.Equal("FT8", Ft8SettingsStore.NormalizeMode(null));
        Assert.Equal("FT4", Ft8SettingsStore.NormalizeMode("ft4"));
        Assert.Equal("WSPR", Ft8SettingsStore.NormalizeMode(" wspr "));
    }

    [Fact]
    public void Legacy_Unkeyed_Row_Migrates_To_Ft8()
    {
        // Simulate a row written by the pre-per-mode schema: no Mode field at all.
        using (var db = new LiteDatabase($"Filename={_dbPath};Connection=shared"))
        {
            var col = db.GetCollection("ft8_settings");
            col.Insert(new BsonDocument
            {
                // Legacy rows carried an int _id (the old Ft8SettingsEntry.Id);
                // set it explicitly so LiteDB doesn't assign an ObjectId that
                // can't map back to the typed entry's int Id.
                ["_id"] = 1,
                ["AutoSequence"] = false,
                ["CallFirst"] = true,
                ["DecodePasses"] = 1,
                ["DefaultTxOffsetHz"] = 1700,
                ["CqMessage"] = "CQ TEST",
            });
        }

        // Prove the migration mechanism directly: a row with NO Mode field
        // hydrates with Mode == "FT8" (LiteDB leaves the missing field at the
        // Ft8SettingsEntry.Mode initializer), which is what lets the ordinary
        // exact-match lookup in FindEntry adopt it as the FT8 row — there is no
        // separate null-Mode fallback branch.
        using (var db = new LiteDatabase($"Filename={_dbPath};Connection=shared"))
        {
            var typed = db.GetCollection<Ft8SettingsEntry>("ft8_settings");
            var legacy = typed.FindAll().Single();
            Assert.Equal(Ft8SettingsStore.DefaultMode, legacy.Mode);
        }

        using var store = NewStore();
        // The legacy row is adopted as FT8 — its values, not defaults.
        var ft8 = store.Get("FT8");
        Assert.False(ft8.AutoSequence);
        Assert.True(ft8.CallFirst);
        Assert.Equal(1, ft8.DecodePasses);
        Assert.Equal(1700, ft8.DefaultTxOffsetHz);
        Assert.Equal("CQ TEST", ft8.CqMessage);

        // FT4 / WSPR stay at defaults — the legacy row only seeds FT8.
        Assert.Equal(3, store.Get("FT4").DecodePasses);
        Assert.True(store.Get("FT4").AutoSequence);

        // Writing FT8 upgrades the legacy row in place (no duplicate row).
        store.Set("FT8", ft8 with { DefaultTxOffsetHz = 1800 });
        Assert.Equal(1800, store.Get("FT8").DefaultTxOffsetHz);
        // FT4 still independent after the upgrade.
        Assert.Equal(3, store.Get("FT4").DecodePasses);
    }
}
