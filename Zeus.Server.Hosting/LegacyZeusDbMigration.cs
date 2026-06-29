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
using Microsoft.Extensions.Logging;

namespace Zeus.Server;

// One-time migration off the legacy combined, encrypted zeus.db.
//
// Older builds kept BOTH the credentials and the QSO logbook in an encrypted
// %LOCALAPPDATA%/Zeus/zeus.db, separate from the unencrypted settings in
// zeus-prefs.db — so an install carried two databases. Zeus now keeps exactly:
//
//   * the config DB (zeus-prefs.db)  — all settings AND credentials, plaintext
//   * the logbook DB (zeus-logbook.db) — the QSO logbook, plaintext
//
// This copies any rows out of the old encrypted zeus.db into those two files
// and renames the legacy file aside so it never re-runs. It is strictly
// best-effort: a failure here must NEVER stop Zeus from launching, and it never
// clobbers data already present in the destination (the destination wins).
public static class LegacyZeusDbMigration
{
    public const string LegacyFileName = "zeus.db";
    private const string KeyFileName = ".dbkey";

    /// <summary>
    /// Migrate a legacy zeus.db found under <paramref name="legacyDir"/> into
    /// the config DB (credentials) and logbook DB, then move the legacy file
    /// aside. No-op when there is no legacy file. Swallows all errors.
    /// </summary>
    public static void RunIfNeeded(
        string legacyDir,
        string configDbPath,
        string logbookDbPath,
        ILogger? log = null)
    {
        try
        {
            var legacyPath = Path.Combine(legacyDir, LegacyFileName);
            if (!File.Exists(legacyPath))
                return; // nothing to migrate (fresh install or already migrated)

            // The legacy DB was encrypted with the key in .dbkey. If the key is
            // missing we still try an unencrypted open — a pre-encryption file or
            // a hand-restored one may not be encrypted at all.
            var keyPath = Path.Combine(legacyDir, KeyFileName);
            string? password = null;
            if (File.Exists(keyPath))
            {
                try { password = File.ReadAllText(keyPath).Trim(); }
                catch { /* fall through and try without a password */ }
            }

            int creds, logs;
            try
            {
                (creds, logs) = Copy(legacyPath, password, configDbPath, logbookDbPath);
            }
            catch (Exception ex) when (string.IsNullOrEmpty(password) is false)
            {
                // The stored key may be wrong/stale; a second attempt with no
                // password covers a legacy file that was never encrypted.
                log?.LogWarning(ex, "Legacy zeus.db open with stored key failed; retrying unencrypted.");
                (creds, logs) = Copy(legacyPath, null, configDbPath, logbookDbPath);
            }

            // Move the legacy DB (and its -log sidecar) aside so this never runs
            // again. The moved file is preserved for diagnosis, never deleted.
            var stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
            var aside = $"{legacyPath}.migrated-{stamp}";
            foreach (var (src, dst) in new[]
            {
                (legacyPath, aside),
                (legacyPath + "-log", aside + "-log"),
            })
            {
                if (File.Exists(src)) File.Move(src, dst);
            }

            log?.LogInformation(
                "Migrated legacy zeus.db ({Creds} credential(s), {Logs} log entr(ies)) into the config + logbook DBs; legacy file moved to {Aside}.",
                creds, logs, aside);
        }
        catch (Exception ex)
        {
            // Never block launch. Leave the legacy file in place so a later
            // launch — or a developer — can retry / diagnose.
            log?.LogWarning(ex, "Legacy zeus.db migration skipped due to an error; legacy file left in place.");
            Console.Error.WriteLine(
                $"prefs.migrate legacy zeus.db migration failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static (int creds, int logs) Copy(
        string legacyPath, string? password, string configDbPath, string logbookDbPath)
    {
        // Open the legacy file directly and dispose it at the end of this method
        // — BEFORE the caller renames it aside — so the handle is flushed and
        // unlocked cleanly. Connection=shared matches every other LiteDB open in
        // Zeus; the legacy file was AES-encrypted with the key in .dbkey.
        var legacyConn = string.IsNullOrEmpty(password)
            ? $"Filename={legacyPath};Connection=shared"
            : $"Filename={legacyPath};Password={password};Connection=shared";

        using var legacy = new LiteDatabase(legacyConn);
        var names = legacy.GetCollectionNames()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var creds = names.Contains("credentials")
            ? MigrateCredentials(legacy, configDbPath)
            : 0;
        var logs = names.Contains("logs")
            ? MigrateLogs(legacy, logbookDbPath)
            : 0;
        return (creds, logs);
    }

    // Copy credentials into the config DB. Written through the TYPED collection
    // so the destination assigns an Int32 auto-id (matching CredentialStore's
    // mapping) rather than an ObjectId. Keyed by Service; an entry already
    // present in the destination is left untouched (destination wins).
    private static int MigrateCredentials(LiteDatabase legacy, string configDbPath)
    {
        var src = legacy.GetCollection("credentials").FindAll().ToList();
        if (src.Count == 0) return 0;

        using var cfg = new LiteDatabase($"Filename={configDbPath};Connection=shared");
        var dst = cfg.GetCollection<StoredCredential>("credentials");
        dst.EnsureIndex(x => x.Service, unique: true);

        var existing = new HashSet<string>(
            dst.FindAll().Select(c => c.Service ?? string.Empty),
            StringComparer.OrdinalIgnoreCase);

        var n = 0;
        foreach (var doc in src)
        {
            var cred = BsonMapper.Global.ToObject<StoredCredential>(doc);
            var service = cred.Service ?? string.Empty;
            if (existing.Contains(service)) continue;
            cred.Id = 0; // 0 → let LiteDB assign a fresh auto-increment id
            dst.Insert(cred);
            existing.Add(service);
            n++;
        }
        return n;
    }

    // Copy logbook rows into the logbook DB. Keyed by _id (the string QSO id);
    // an entry already present in the destination is left untouched.
    private static int MigrateLogs(LiteDatabase legacy, string logbookDbPath)
    {
        var src = legacy.GetCollection("logs").FindAll().ToList();
        if (src.Count == 0) return 0;

        using var book = new LiteDatabase($"Filename={logbookDbPath};Connection=shared");
        var dst = book.GetCollection("logs");

        var existing = new HashSet<string>(
            dst.FindAll().Select(IdOf),
            StringComparer.OrdinalIgnoreCase);

        var n = 0;
        foreach (var doc in src)
        {
            if (existing.Contains(IdOf(doc))) continue;
            dst.Insert(doc); // preserve the original string _id
            n++;
        }
        return n;
    }

    private static string IdOf(BsonDocument doc) =>
        doc.TryGetValue("_id", out var id)
            ? (id.IsString ? id.AsString : id.ToString())
            : string.Empty;
}
