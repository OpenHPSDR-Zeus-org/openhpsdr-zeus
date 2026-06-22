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

// One-time fold of the legacy encrypted zeus.db (credentials + logbook) into
// the config DB (credentials, plaintext) and a dedicated logbook DB.
public sealed class LegacyZeusDbMigrationTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;
    private readonly string _logbookPath;
    // Any 16/24/32-byte base64 key works; LiteDB derives the AES key from it.
    private const string Key = "dGVzdC1rZXktMzItYnl0ZXMtZm9yLW1pZ3JhdGU=";

    public LegacyZeusDbMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"zeus-migrate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "zeus-prefs.db");
        _logbookPath = Path.Combine(_dir, "zeus-logbook.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private void WriteLegacyDb(int credentials, int logs)
    {
        File.WriteAllText(Path.Combine(_dir, ".dbkey"), Key);
        using var db = new LiteDatabase(
            $"Filename={Path.Combine(_dir, "zeus.db")};Password={Key};Connection=shared");

        var creds = db.GetCollection("credentials");
        creds.EnsureIndex("Service", unique: true);
        for (var i = 0; i < credentials; i++)
        {
            creds.Insert(new BsonDocument
            {
                ["_id"] = i + 1,
                ["Service"] = $"svc{i}",
                ["Username"] = $"user{i}",
                ["Password"] = $"pass{i}",
                ["UpdatedUtc"] = DateTime.UtcNow,
            });
        }

        var book = db.GetCollection("logs");
        for (var i = 0; i < logs; i++)
        {
            book.Insert(new BsonDocument
            {
                ["_id"] = $"qso-{i}",
                ["Callsign"] = $"N9WA{i}",
                ["Band"] = "20M",
                ["Mode"] = "SSB",
            });
        }
    }

    private static int Count(string dbPath, string collection)
    {
        using var db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        return db.GetCollection(collection).Count();
    }

    [Fact]
    public void NoLegacyFile_IsNoOp()
    {
        LegacyZeusDbMigration.RunIfNeeded(_dir, _configPath, _logbookPath, NullLogger.Instance);

        Assert.False(File.Exists(_configPath));
        Assert.False(File.Exists(_logbookPath));
    }

    [Fact]
    public async Task Migrates_Credentials_And_Logs_ThenMovesLegacyAside()
    {
        WriteLegacyDb(credentials: 2, logs: 3);

        LegacyZeusDbMigration.RunIfNeeded(_dir, _configPath, _logbookPath, NullLogger.Instance);

        // Credentials landed in the (plaintext) config DB and are readable by the
        // real store — no password required.
        using (var store = new CredentialStore(NullLogger<CredentialStore>.Instance, _configPath))
        {
            var c = await store.GetAsync("svc0");
            Assert.NotNull(c);
            Assert.Equal("user0", c!.Username);
            Assert.Equal("pass0", c.Password);
        }

        // Logbook rows landed in the dedicated logbook DB.
        Assert.Equal(3, Count(_logbookPath, "logs"));

        // Legacy file moved aside (preserved, not deleted) so it won't re-run.
        Assert.False(File.Exists(Path.Combine(_dir, "zeus.db")));
        Assert.Single(Directory.GetFiles(_dir, "zeus.db.migrated-*"));
    }

    [Fact]
    public void SecondRun_IsNoOp_AndDoesNotDuplicate()
    {
        WriteLegacyDb(credentials: 1, logs: 1);

        LegacyZeusDbMigration.RunIfNeeded(_dir, _configPath, _logbookPath, NullLogger.Instance);
        // Legacy now gone; a second run has nothing to do and must not duplicate.
        LegacyZeusDbMigration.RunIfNeeded(_dir, _configPath, _logbookPath, NullLogger.Instance);

        Assert.Equal(1, Count(_configPath, "credentials"));
        Assert.Equal(1, Count(_logbookPath, "logs"));
        Assert.Single(Directory.GetFiles(_dir, "zeus.db.migrated-*"));
    }

    [Fact]
    public async Task DestinationWins_ExistingCredentialNotClobbered()
    {
        // Pre-seed the config DB with a credential for svc0 (through the real
        // store), then migrate a legacy svc0 with different values. The
        // destination must win and there must be exactly one svc0 row.
        using (var seed = new CredentialStore(NullLogger<CredentialStore>.Instance, _configPath))
        {
            await seed.SetAsync("svc0", "keep-me", "keep-pass");
        }
        WriteLegacyDb(credentials: 1, logs: 0);

        LegacyZeusDbMigration.RunIfNeeded(_dir, _configPath, _logbookPath, NullLogger.Instance);

        using var verify = new CredentialStore(NullLogger<CredentialStore>.Instance, _configPath);
        var c = await verify.GetAsync("svc0");
        Assert.NotNull(c);
        Assert.Equal("keep-me", c!.Username);
        Assert.Equal(1, Count(_configPath, "credentials"));
    }
}
