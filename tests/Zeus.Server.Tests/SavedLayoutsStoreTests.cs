// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// Saved-layouts library CRUD round-trip — the reusable layout-preset pool
// (separate from the working tabs). Exercises create / replace / rename /
// delete plus persistence across a store reopen.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class SavedLayoutsStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string? _previous;

    public SavedLayoutsStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zeus-prefs-saved-{Guid.NewGuid():N}.db");
        _previous = Environment.GetEnvironmentVariable("ZEUS_PREFS_PATH");
        Environment.SetEnvironmentVariable("ZEUS_PREFS_PATH", _dbPath);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ZEUS_PREFS_PATH", _previous);
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (File.Exists(_dbPath + "-log")) File.Delete(_dbPath + "-log"); } catch { }
    }

    private static LayoutStore NewStore() => new(NullLogger<LayoutStore>.Instance);

    private const string Json = "{\"schemaVersion\":8,\"tiles\":[]}";

    [Fact]
    public void Empty_Radio_Has_No_Saved_Layouts()
    {
        using var store = NewStore();
        var dto = store.GetSavedLayouts("HermesLite2");
        Assert.Empty(dto.SavedLayouts);
    }

    [Fact]
    public void Upsert_Adds_New_Saved_Layout_With_Metadata()
    {
        using var store = NewStore();
        var dto = store.UpsertSavedLayout("HermesLite2", "s1", "My Backup", Json, "⭐", "portable");
        var entry = Assert.Single(dto.SavedLayouts);
        Assert.Equal("s1", entry.Id);
        Assert.Equal("My Backup", entry.Name);
        Assert.Equal("⭐", entry.Icon);
        Assert.Equal("portable", entry.Description);
    }

    [Fact]
    public void Upsert_Same_Id_Replaces_In_Place()
    {
        using var store = NewStore();
        store.UpsertSavedLayout("HermesLite2", "s1", "First", Json, null, null);
        const string newJson = "{\"schemaVersion\":8,\"tiles\":[{\"uid\":\"a\",\"panelId\":\"vfo\",\"x\":0,\"y\":0,\"w\":2,\"h\":2}]}";
        var dto = store.UpsertSavedLayout("HermesLite2", "s1", "Renamed", newJson, null, null);

        var entry = Assert.Single(dto.SavedLayouts); // replaced, not appended
        Assert.Equal("Renamed", entry.Name);
        Assert.Equal(newJson, entry.LayoutJson);
    }

    [Fact]
    public void Saved_Layouts_Are_Per_Radio()
    {
        using var store = NewStore();
        store.UpsertSavedLayout("HermesLite2", "s1", "HL2 preset", Json, null, null);
        store.UpsertSavedLayout("AnanG2", "s2", "G2 preset", Json, null, null);

        Assert.Equal("HL2 preset", Assert.Single(store.GetSavedLayouts("HermesLite2").SavedLayouts).Name);
        Assert.Equal("G2 preset", Assert.Single(store.GetSavedLayouts("AnanG2").SavedLayouts).Name);
    }

    [Fact]
    public void Saved_Layouts_Persist_Across_Reopen()
    {
        using (var store = NewStore())
            store.UpsertSavedLayout("HermesLite2", "s1", "Backup", Json, "📡", null);

        using var reopened = NewStore();
        var entry = Assert.Single(reopened.GetSavedLayouts("HermesLite2").SavedLayouts);
        Assert.Equal("Backup", entry.Name);
        Assert.Equal("📡", entry.Icon);
    }

    [Fact]
    public void Delete_Removes_Only_The_Target()
    {
        using var store = NewStore();
        store.UpsertSavedLayout("HermesLite2", "s1", "Keep", Json, null, null);
        store.UpsertSavedLayout("HermesLite2", "s2", "Drop", Json, null, null);

        var dto = store.DeleteSavedLayout("HermesLite2", "s2");
        var entry = Assert.Single(dto.SavedLayouts);
        Assert.Equal("s1", entry.Id);
    }

    [Fact]
    public void Saved_Layouts_Are_Independent_From_The_Tabs()
    {
        // The library and the working-tab collection (UpsertNamed) must not
        // bleed into each other even under the same radio key.
        using var store = NewStore();
        store.UpsertNamed("HermesLite2", "tab1", "Tab One", Json);
        store.UpsertSavedLayout("HermesLite2", "preset1", "Preset One", Json, null, null);

        Assert.Equal("Tab One", Assert.Single(store.GetForRadio("HermesLite2").Layouts).Name);
        Assert.Equal("Preset One", Assert.Single(store.GetSavedLayouts("HermesLite2").SavedLayouts).Name);
    }
}
