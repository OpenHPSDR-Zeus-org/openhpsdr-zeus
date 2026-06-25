// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// Detached workspace windows survive a desktop restart: the set persisted at
// shutdown is what the next launch reopens.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class OpenWorkspaceWindowsStoreTests : IDisposable
{
    private readonly string _dbPath;

    public OpenWorkspaceWindowsStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zeus-prefs-wswin-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private OpenWorkspaceWindowsStore NewStore() =>
        new(NullLogger<OpenWorkspaceWindowsStore>.Instance, _dbPath);

    [Fact]
    public void Empty_Store_Returns_No_Windows()
    {
        using var store = NewStore();
        Assert.Empty(store.GetAll());
    }

    [Fact]
    public void Replace_RoundTrips_Across_Restart()
    {
        using (var store = NewStore())
        {
            store.Replace(new[]
            {
                new OpenWorkspaceWindowDto("layout-a", "Bench"),
                new OpenWorkspaceWindowDto("layout-b", "Mobile"),
            });
        }

        // Reopen to prove it survives a backend restart — the whole point.
        using var reopened = NewStore();
        var all = reopened.GetAll();
        Assert.Equal(2, all.Count);
        Assert.Equal("layout-a", all[0].LayoutId);
        Assert.Equal("Bench", all[0].Title);
        Assert.Equal("layout-b", all[1].LayoutId);
    }

    [Fact]
    public void Replace_Overwrites_The_Whole_Set()
    {
        using var store = NewStore();
        store.Replace(new[] { new OpenWorkspaceWindowDto("layout-a", "A") });
        store.Replace(new[] { new OpenWorkspaceWindowDto("layout-b", "B") });
        var all = store.GetAll();
        Assert.Single(all);
        Assert.Equal("layout-b", all[0].LayoutId);
    }

    [Fact]
    public void Replace_With_Empty_Clears_Everything()
    {
        using var store = NewStore();
        store.Replace(new[] { new OpenWorkspaceWindowDto("layout-a", "A") });
        store.Replace(Array.Empty<OpenWorkspaceWindowDto>());
        Assert.Empty(store.GetAll());
    }

    [Fact]
    public void Replace_Dedupes_LayoutIds_And_Drops_Blanks()
    {
        using var store = NewStore();
        store.Replace(new[]
        {
            new OpenWorkspaceWindowDto("layout-a", "First"),
            new OpenWorkspaceWindowDto("layout-a", "Duplicate"),
            new OpenWorkspaceWindowDto("", "Blank"),
            new OpenWorkspaceWindowDto("   ", "Whitespace"),
        });
        var all = store.GetAll();
        Assert.Single(all);
        Assert.Equal("layout-a", all[0].LayoutId);
        Assert.Equal("First", all[0].Title);
    }
}
