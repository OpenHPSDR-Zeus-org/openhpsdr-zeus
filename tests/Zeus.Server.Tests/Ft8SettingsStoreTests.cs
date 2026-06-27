// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Pins the FT8/FT4 settings store: defaults match the pre-settings behaviour
// (so surfacing them changes nothing an operator feels), out-of-range POSTs are
// clamped on write, and the choice survives a store reopen (the desktop-restart
// fix this whole feature exists for).

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
}
