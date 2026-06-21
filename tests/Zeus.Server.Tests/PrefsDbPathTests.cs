// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using LiteDB;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server;

namespace Zeus.Server.Tests;

// Startup corruption guard for the shared zeus-prefs.db (#637 / #635).
public class PrefsDbPathTests : IDisposable
{
    private readonly string _dbPath;

    public PrefsDbPathTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zeus-prefs-guard-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        var dir = Path.GetDirectoryName(_dbPath)!;
        var stem = Path.GetFileName(_dbPath);
        foreach (var f in Directory.GetFiles(dir, stem + "*"))
        {
            try { File.Delete(f); } catch { /* best effort */ }
        }
    }

    private static void WriteValidDb(string path)
    {
        using var db = new LiteDatabase($"Filename={path};Connection=shared");
        var col = db.GetCollection("prefs");
        col.Insert(new BsonDocument { ["_id"] = 1, ["value"] = "hello" });
        col.Insert(new BsonDocument { ["_id"] = 2, ["value"] = "world" });
    }

    private string[] CorruptBackups()
    {
        var dir = Path.GetDirectoryName(_dbPath)!;
        return Directory.GetFiles(dir, Path.GetFileName(_dbPath) + ".corrupt-*.bak");
    }

    [Fact]
    public void MissingFile_ReturnsTrue_AndCreatesNothing()
    {
        Assert.False(File.Exists(_dbPath));
        Assert.True(PrefsDbPath.EnsureUsable(_dbPath, NullLogger.Instance));
        // A fresh install has no DB to validate; the guard must not fabricate one.
        Assert.False(File.Exists(_dbPath));
        Assert.Empty(CorruptBackups());
    }

    [Fact]
    public void ValidDb_ReturnsTrue_AndLeavesItUntouched()
    {
        WriteValidDb(_dbPath);
        var before = File.ReadAllBytes(_dbPath);

        Assert.True(PrefsDbPath.EnsureUsable(_dbPath, NullLogger.Instance));

        Assert.True(File.Exists(_dbPath));
        Assert.Empty(CorruptBackups());
        // Untouched — the guard must not rewrite a healthy DB.
        Assert.Equal(before, File.ReadAllBytes(_dbPath));
    }

    [Fact]
    public void CorruptFile_IsMovedAside_AndPathBecomesUsable()
    {
        // Not a valid LiteDB file at all — opening it throws a LiteException.
        File.WriteAllBytes(_dbPath, Enumerable.Repeat((byte)0xAB, 9000).ToArray());

        Assert.True(PrefsDbPath.EnsureUsable(_dbPath, NullLogger.Instance));

        // The corrupt file was moved aside (preserved for diagnosis)...
        Assert.False(File.Exists(_dbPath));
        Assert.Single(CorruptBackups());
        // ...and a store can now open a fresh DB at the original path.
        using var db = new LiteDatabase($"Filename={_dbPath};Connection=shared");
        Assert.Empty(db.GetCollection("prefs").FindAll());
    }

    [Fact]
    public void LockedFile_IsNotWiped()
    {
        // A healthy DB that is exclusively locked (another instance / antivirus)
        // must be treated as transient and left ALONE — never moved aside.
        WriteValidDb(_dbPath);
        using (var hold = new FileStream(_dbPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(PrefsDbPath.EnsureUsable(_dbPath, NullLogger.Instance));
        }

        Assert.True(File.Exists(_dbPath));
        Assert.Empty(CorruptBackups()); // load-bearing: a busy DB is NEVER wiped
    }
}
