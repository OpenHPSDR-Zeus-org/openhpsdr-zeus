// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class CwSettingsStoreTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-cwset-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private CwSettingsStore Build() =>
        new(NullLogger<CwSettingsStore>.Instance, _dbPath);

    [Fact]
    public void Get_OnFreshDb_ReturnsSeededDefaults()
    {
        using var store = Build();

        var s = store.Get();

        Assert.Equal(CwSettingsStore.DefaultWpm, s.Wpm);
        Assert.Null(s.FarnsworthWpm);
        Assert.Equal(CwSettingsStore.DefaultSidetoneGainDb, s.SidetoneGainDb);
        Assert.Equal(CwSettingsStore.DefaultSidetoneHz, s.SidetoneHz);
        // Fresh install gets the six seeded defaults — operator can grow
        // the list to MaxMacros via PATCH.
        Assert.Equal(CwSettingsStore.DefaultMacros.Length, s.Macros.Length);
        Assert.Equal(CwSettingsStore.DefaultMacros, s.Macros);
    }

    [Fact]
    public void Save_RoundtripsSingleField_LeavesOthersUntouched()
    {
        // PATCH semantics — saving Wpm alone must not wipe the macros.
        using var store = Build();

        var after = store.Save(new CwSettingsSetRequest(Wpm: 30));

        Assert.Equal(30, after.Wpm);
        Assert.Equal(CwSettingsStore.DefaultMacros, after.Macros);
        Assert.Equal(CwSettingsStore.DefaultSidetoneGainDb, after.SidetoneGainDb);
    }

    [Fact]
    public void Save_ClampsOutOfRangeFields()
    {
        // The store is the last line of defence before LiteDB persists —
        // any pathological value (UI bug, hand-rolled curl) must clamp
        // rather than write garbage that survives restarts.
        using var store = Build();

        var after = store.Save(new CwSettingsSetRequest(
            Wpm: 999,
            FarnsworthWpm: 999,
            SidetoneGainDb: +10.0,
            SidetoneHz: 50));

        Assert.Equal(50, after.Wpm);                  // clamped to WpmMax
        Assert.Equal(50, after.FarnsworthWpm);
        Assert.Equal(0.0, after.SidetoneGainDb);      // clamped to ≤ 0
        Assert.Equal(200, after.SidetoneHz);          // clamped to ≥ 200
    }

    [Fact]
    public void Save_PreservesMacroLength_AndDoesNotPad()
    {
        // Variable-length: if the operator saves two macros, only two are
        // persisted (add/delete are first-class operations from the UI).
        // The previous fixed-6 padding behaviour would have hidden a
        // genuine "user deleted 4 of 6 macros" intent behind invisible
        // empty slots that the macro pad would still render.
        using var store = Build();

        var raw = new[] { "ONE", "TWO" };
        var after = store.Save(new CwSettingsSetRequest(Macros: raw));

        Assert.Equal(2, after.Macros.Length);
        Assert.Equal("ONE", after.Macros[0]);
        Assert.Equal("TWO", after.Macros[1]);
    }

    [Fact]
    public void Save_CapsMacroCount_AtMaxMacros()
    {
        // Defensive upper bound — a programmatic mass-save (or a bug in
        // an upcoming "import macros" feature) can't blow past the cap.
        using var store = Build();
        var huge = Enumerable.Range(0, CwSettingsStore.MaxMacros * 2)
            .Select(i => $"M{i}")
            .ToArray();

        var after = store.Save(new CwSettingsSetRequest(Macros: huge));

        Assert.Equal(CwSettingsStore.MaxMacros, after.Macros.Length);
    }

    [Fact]
    public void Save_CapsMacroLength()
    {
        using var store = Build();
        string huge = new string('A', CwSettingsStore.MaxMacroChars * 3);

        var after = store.Save(new CwSettingsSetRequest(Macros: new[] { huge }));

        Assert.Equal(CwSettingsStore.MaxMacroChars, after.Macros[0].Length);
    }

    [Fact]
    public void Get_AfterDispose_AndReopen_ReturnsPersistedValues()
    {
        // Survives a backend restart — the whole point of the store.
        // Empty-string slots in the middle of the list are preserved
        // (LiteDB serialises "" as null; SanitizeStored maps back).
        using (var store = Build())
        {
            store.Save(new CwSettingsSetRequest(
                Wpm: 28,
                Macros: new[] { "X", "Y", "Z", "", "M5" }));
        }

        using var reopened = Build();
        var s = reopened.Get();

        Assert.Equal(28, s.Wpm);
        Assert.Equal(5, s.Macros.Length);
        Assert.Equal("X", s.Macros[0]);
        Assert.Equal("Y", s.Macros[1]);
        Assert.Equal("Z", s.Macros[2]);
        Assert.Equal(string.Empty, s.Macros[3]);
        Assert.Equal("M5", s.Macros[4]);
    }
}
