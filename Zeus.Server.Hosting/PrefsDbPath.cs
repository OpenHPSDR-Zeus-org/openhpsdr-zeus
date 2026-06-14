// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using LiteDB;
using Microsoft.Extensions.Logging;

namespace Zeus.Server;

// Central database-path resolver for all zeus-prefs.db stores.
// When ZEUS_PREFS_PATH is set, all stores share that path instead of the
// platform default — useful for dev (/run fresh gives a throw-away DB),
// CI, or running two Zeus instances side-by-side without colliding prefs.
public static class PrefsDbPath
{
    public static string Get() =>
        Environment.GetEnvironmentVariable("ZEUS_PREFS_PATH")
        ?? DefaultPath();

    private static string DefaultPath()
    {
        var appDataDir = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);
        return Path.Combine(appDataDir, "Zeus", "zeus-prefs.db");
    }

    // ---- Startup corruption guard (#637) --------------------------------
    //
    // ~20 preference stores share ONE physical zeus-prefs.db, and each opens
    // it directly in its constructor. If that file is corrupt — most often an
    // interrupted write (power loss, a network/IP change that knocked the app
    // over mid-flush) — the FIRST store's open throws a LiteException during DI
    // build, the host fails to start, and on the desktop the window just
    // flashes and closes (#635). A saved-settings file must never be able to
    // stop the app from launching.
    //
    // EnsureUsable runs ONCE, before any store opens the file, and disposes its
    // own handle before returning — so it can move a corrupt file aside without
    // racing a live store handle. By the time any store constructor runs the
    // file is guaranteed openable. The 20-odd `new LiteDatabase(...)` call sites
    // are deliberately left unchanged.

    private const string SharedConn = ";Connection=shared";
    private enum ProbeResult { Ok, Transient, Corrupt }

    /// <summary>
    /// Probe the shared prefs DB and recover a corrupt one. Returns true when
    /// the path is usable afterwards (it was fine, fresh, busy, or successfully
    /// moved aside and will be recreated); false only when the file is corrupt
    /// AND could not be moved (e.g. still locked) — the caller must then fall
    /// back to a throwaway path so the app still launches.
    ///
    /// A merely BUSY/locked file (second instance, antivirus) is left untouched.
    /// Corruption is judged only after it reproduces across several probes, so a
    /// torn read from a concurrent writer is never mistaken for a corrupt file.
    /// </summary>
    public static bool EnsureUsable(string dbPath, ILogger? log = null)
    {
        try
        {
            var dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (!File.Exists(dbPath))
                return true; // fresh install / fresh DB — nothing to validate

            const int attempts = 3;
            const int backoffMs = 150;
            for (int i = 0; i < attempts; i++)
            {
                var result = Probe(dbPath, log);
                if (result is ProbeResult.Ok or ProbeResult.Transient)
                    return true; // valid, or busy → leave it alone
                if (i < attempts - 1)
                    System.Threading.Thread.Sleep(backoffMs);
            }

            Warn(log, $"prefs DB at '{dbPath}' is corrupt across {attempts} probes — moving it aside.");
            return MoveCorruptAside(dbPath, log);
        }
        catch (Exception ex)
        {
            // Never let the guard itself crash startup — worst case the caller
            // falls back to a temp path.
            Error(log, $"prefs DB EnsureUsable failed unexpectedly for '{dbPath}'", ex);
            return false;
        }
    }

    private static ProbeResult Probe(string dbPath, ILogger? log)
    {
        try
        {
            using var db = new LiteDatabase($"Filename={dbPath}{SharedConn}");
            // Page 0 / collection dictionary.
            var names = db.GetCollectionNames().ToList();
            // Force a DATA-page read on every collection. Interrupted writes most
            // often damage a data page, not the header — a header-only probe would
            // sail past that and let a store crash later. These prefs tables are
            // tiny single-row stores, so the scan costs a few ms.
            foreach (var name in names)
                _ = db.GetCollection(name).FindAll().FirstOrDefault();
            return ProbeResult.Ok;
        }
        catch (IOException)
        {
            // Raw OS file lock (another instance, antivirus) — never corruption.
            return ProbeResult.Transient;
        }
        catch (Exception ex) when (ex.InnerException is IOException)
        {
            // Lock surfaced wrapped in a LiteException (or similar).
            return ProbeResult.Transient;
        }
        catch (Exception ex)
        {
            // Any other failure to open/read the file is corruption: a bad
            // header, a torn/truncated data page, or garbage where a DB should
            // be. LiteDB raises several exception types here (LiteException,
            // end-of-stream, format errors) — all mean "this file won't open".
            // The caller retries before judging, so a torn read from a live
            // writer mid-flush is never mistaken for a corrupt file.
            Warn(log, $"prefs DB probe failed: {ex.GetType().Name}: {ex.Message}", ex);
            return ProbeResult.Corrupt;
        }
    }

    private static bool MoveCorruptAside(string dbPath, ILogger? log)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
        var bak = $"{dbPath}.corrupt-{stamp}.bak";
        try
        {
            // Move the DB and any LiteDB sidecar (the -log transaction file) so a
            // half-checkpointed pair can't re-corrupt the fresh DB. Best-effort
            // per file; only move what exists.
            foreach (var (src, dst) in new[]
            {
                (dbPath, bak),
                (dbPath + "-log", bak + "-log"),
            })
            {
                if (File.Exists(src)) File.Move(src, dst);
            }
            Warn(log,
                $"prefs DB moved aside to '{bak}'; a fresh one will be created. " +
                "Prior settings (including TX/PA calibration) were lost — the moved " +
                "file is preserved for developer diagnosis only and cannot be auto-restored.");
            return true;
        }
        catch (Exception ex)
        {
            Error(log, $"prefs DB move-aside failed (still locked?) for '{dbPath}'", ex);
            return false;
        }
    }

    // Dual sink: ILogger when one is available (it usually isn't — the guard runs
    // before the DI container exists) AND Console.Error, because the desktop app
    // calls FreeConsole() and would otherwise swallow the message entirely.
    private static void Warn(ILogger? log, string msg, Exception? ex = null)
    {
        if (ex is null) log?.LogWarning("{Msg}", msg);
        else log?.LogWarning(ex, "{Msg}", msg);
        Console.Error.WriteLine(ex is null ? $"prefs.guard {msg}" : $"prefs.guard {msg} :: {ex.GetType().Name}: {ex.Message}");
    }

    private static void Error(ILogger? log, string msg, Exception ex)
    {
        log?.LogError(ex, "{Msg}", msg);
        Console.Error.WriteLine($"prefs.guard {msg} :: {ex.GetType().Name}: {ex.Message}");
    }
}
