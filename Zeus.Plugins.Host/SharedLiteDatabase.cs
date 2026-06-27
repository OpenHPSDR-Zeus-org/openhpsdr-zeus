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

namespace Zeus.Data;

// Process-wide, reference-counted registry of LiteDatabase instances keyed by
// normalized file path. Guarantees EXACTLY ONE LiteDatabase (one LiteEngine)
// per physical database file per process.
//
// Why this exists (the corruption root cause): Zeus had ~40 stores that each
// did `new LiteDatabase("Filename=...;Connection=shared")` against the SAME
// zeus-prefs.db (and two more against the encrypted zeus.db). Many independent
// engines on one file — each with its own page cache and its own
// open/checkpoint/close lifecycle — is the documented LiteDB corruption
// antipattern: an interrupted write (power loss, the app killed on a network
// change) can leave the shared -log half-checkpointed and brick the file.
// LiteDB's own guidance is the opposite: a LiteDatabase is thread-safe and is
// meant to be opened ONCE and shared.
//
// Every store now Acquire()s a lease here instead of opening its own database.
// The underlying database is opened on the first lease for a given path and
// disposed — flushing a clean WAL checkpoint to disk — when the last lease for
// that path is released. Reference counting (rather than a single DI singleton)
// is deliberate: it serves both production (all stores resolve the same prefs
// path → one engine) and the test suites (each test points its stores at an
// isolated temp file → its own engine, disposed when that test's stores
// dispose, after which a reopen of the same path sees the flushed data).
public static class SharedLiteDatabase
{
    private sealed class Entry
    {
        public required LiteDatabase Database { get; init; }
        public int RefCount;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> Open = new(PathComparer);

    // Windows and macOS default to case-insensitive filesystems; Linux is
    // case-sensitive. Key the registry to match so two spellings of the same
    // file collapse to one engine where the OS treats them as one file.
    private static StringComparer PathComparer =>
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    // A borrowed handle to a shared database. The store holds this for its
    // lifetime and disposes it (NOT the LiteDatabase directly) when it is
    // disposed. Disposing the lease decrements the refcount; the last release
    // closes the underlying database. Idempotent and thread-safe.
    public sealed class Lease : IDisposable
    {
        private readonly string _key;
        private int _disposed;

        internal Lease(string key, LiteDatabase database)
        {
            _key = key;
            Database = database;
        }

        public LiteDatabase Database { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Release(_key);
        }
    }

    /// <summary>
    /// Acquire a lease on the single shared database for <paramref name="dbPath"/>,
    /// opening it on first use. The caller MUST dispose the returned lease (never
    /// the underlying <see cref="LiteDatabase"/>). Thread-safe.
    /// </summary>
    public static Lease Acquire(string dbPath, string? password = null)
    {
        if (string.IsNullOrWhiteSpace(dbPath))
            throw new ArgumentException("Database path is required.", nameof(dbPath));

        var key = Normalize(dbPath);

        // Make sure the directory exists before LiteDB tries to create the file.
        var dir = Path.GetDirectoryName(key);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        lock (Gate)
        {
            if (Open.TryGetValue(key, out var existing))
            {
                existing.RefCount++;
                return new Lease(key, existing.Database);
            }

            var db = OpenWithRetry(key, password);
            Open[key] = new Entry { Database = db, RefCount = 1 };
            return new Lease(key, db);
        }
    }

    private static void Release(string key)
    {
        lock (Gate)
        {
            if (!Open.TryGetValue(key, out var entry)) return;
            if (--entry.RefCount > 0) return;

            Open.Remove(key);
            try
            {
                entry.Database.Dispose(); // flushes the WAL checkpoint to disk
            }
            catch
            {
                // A failed checkpoint at shutdown must never throw out of a
                // Dispose path. The next launch's integrity guard handles a file
                // left in a bad state.
            }
        }
    }

    // Open the database exclusively (LiteDB's default "direct" connection — one
    // engine, no per-operation reopen churn). A short bounded retry absorbs the
    // one realistic transient on the single-process design: an on-access virus
    // scanner (common on Windows) holding a just-created file for a few hundred
    // milliseconds. A non-lock exception — a corrupt/torn file — is NOT a
    // transient and is not retried: it propagates immediately so the caller's
    // startup integrity guard (PrefsDbPath.EnsureUsable) can move the file aside
    // before any store opens it.
    private static LiteDatabase OpenWithRetry(string path, string? password)
    {
        Exception? lastLock = null;
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                return new LiteDatabase(ConnectionString(path, password));
            }
            catch (Exception ex) when (IsTransientLock(ex))
            {
                lastLock = ex;
                Thread.Sleep(100 + attempt * 50);
            }
        }

        throw lastLock ?? new IOException($"Unable to open database '{path}'.");
    }

    private static string ConnectionString(string path, string? password)
    {
        var s = $"Filename={path}";
        if (!string.IsNullOrEmpty(password)) s += $";Password={password}";
        return s;
    }

    private static bool IsTransientLock(Exception ex) =>
        ex is IOException
        || ex is UnauthorizedAccessException
        || ex.InnerException is IOException
        || ex.InnerException is UnauthorizedAccessException;

    private static string Normalize(string path) => Path.GetFullPath(path);

    /// <summary>Number of distinct database files currently open. Diagnostics/tests.</summary>
    public static int OpenCount
    {
        get { lock (Gate) { return Open.Count; } }
    }
}
