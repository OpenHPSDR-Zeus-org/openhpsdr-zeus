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
using System.Text;
using Zeus.Contracts;

namespace Zeus.Server;

// Central database-path resolver for all zeus-prefs.db stores.
// When ZEUS_PREFS_PATH is set, all stores share that path instead of the
// platform default — useful for dev (/run fresh gives a throw-away DB),
// CI, or running two Zeus instances side-by-side without colliding prefs.
//
// Profiles (issue: prefs-database selector). The operator can keep multiple
// named prefs databases under DataDir/profiles/*.db and switch between them.
// The active choice is held in DataDir/active-profile.txt — a tiny pointer
// file NOT stored inside any prefs DB, since the path resolver runs before any
// store is open (chicken/egg). The legacy DataDir/zeus-prefs.db is always the
// "Default" profile. Switching applies on the next launch (every store reads
// PrefsDbPath.Get() once at startup), so the endpoint that flips the pointer
// also asks the host to relaunch.
public static class PrefsDbPath
{
    public const string LegacyFileName = "zeus-prefs.db";
    private const string ProfilesDirName = "profiles";
    private const string PointerFileName = "active-profile.txt";

    // %LOCALAPPDATA%/Zeus — the directory that has always held zeus-prefs.db.
    public static string DataDir
    {
        get
        {
            var appDataDir = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.Create);
            return Path.Combine(appDataDir, "Zeus");
        }
    }

    public static string Get() =>
        Environment.GetEnvironmentVariable("ZEUS_PREFS_PATH")
        ?? Path.Combine(DataDir, ActiveRelativePath());

    // The QSO logbook lives in its own plaintext DB (zeus-logbook.db), separate
    // from the prefs profiles. It sits in the SAME directory as the active prefs
    // file, so a ZEUS_PREFS_PATH override (dev `/run fresh`, CI, tests) isolates
    // the logbook alongside the throwaway prefs. Unlike the prefs DB it does NOT
    // fork per profile — there is exactly one logbook per data directory.
    public static string LogbookPath()
    {
        var env = Environment.GetEnvironmentVariable("ZEUS_PREFS_PATH");
        var dir = string.IsNullOrEmpty(env)
            ? DataDir
            : (Path.GetDirectoryName(Path.GetFullPath(env)) ?? DataDir);
        return Path.Combine(dir, "zeus-logbook.db");
    }

    // Directory for the rolling on-disk runtime log (DiagnosticLogFileSink). Lives
    // under DataDir/logs so it can be tailed by the out-of-process support sidecar
    // after a backend crash. Follows the same ZEUS_PREFS_PATH override as the
    // logbook so dev `/run fresh`, CI, and tests write their logs into the
    // throwaway data dir instead of the operator's real %LOCALAPPDATA%/Zeus.
    public static string LogDir()
    {
        var env = Environment.GetEnvironmentVariable("ZEUS_PREFS_PATH");
        var dir = string.IsNullOrEmpty(env)
            ? DataDir
            : (Path.GetDirectoryName(Path.GetFullPath(env)) ?? DataDir);
        return Path.Combine(dir, "logs");
    }

    // Full path of the active rolling runtime log file.
    public static string AppLogPath() => Path.Combine(LogDir(), "zeus-app.log");

    // Relative path (under DataDir) of the active profile. Reads the pointer
    // file; falls back to the legacy zeus-prefs.db when the pointer is absent,
    // unreadable, or names a file that no longer exists.
    public static string ActiveRelativePath()
    {
        try
        {
            var pointer = Path.Combine(DataDir, PointerFileName);
            if (!File.Exists(pointer)) return LegacyFileName;

            var rel = File.ReadAllText(pointer).Trim();
            if (string.IsNullOrEmpty(rel)) return LegacyFileName;

            // Normalize and confirm the target sits under DataDir and exists.
            var full = ResolveUnderDataDir(rel);
            if (full is not null && File.Exists(full)) return Normalize(rel);
        }
        catch
        {
            // Pointer is best-effort; any IO/parse failure falls back to legacy.
        }
        return LegacyFileName;
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
    // is a GUI-subsystem binary with no console and would otherwise swallow the
    // message entirely.
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

    // Point the pointer file at relativePath. Validates the target exists and
    // is within DataDir (no traversal). Throws on failure so the endpoint can
    // surface the message.
    public static void SetActive(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("Relative path is required.", nameof(relativePath));

        var full = ResolveUnderDataDir(relativePath)
            ?? throw new ArgumentException("Path is outside the Zeus data directory.", nameof(relativePath));
        if (!File.Exists(full))
            throw new FileNotFoundException("Target database does not exist.", full);

        Directory.CreateDirectory(DataDir);
        File.WriteAllText(Path.Combine(DataDir, PointerFileName), Normalize(relativePath));
    }

    // The Default (legacy) profile + every profiles/*.db, with metadata. The
    // Default file is listed even when it doesn't exist yet (size 0) — it's
    // created on the first store write.
    public static IReadOnlyList<PrefsDatabaseInfo> ListProfiles()
    {
        var active = ActiveRelativePath();
        var list = new List<PrefsDatabaseInfo> { DescribeDefault(active) };

        var profilesDir = Path.Combine(DataDir, ProfilesDirName);
        if (Directory.Exists(profilesDir))
        {
            foreach (var file in Directory.EnumerateFiles(profilesDir, "*.db").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var rel = ProfilesDirName + "/" + Path.GetFileName(file);
                var info = new FileInfo(file);
                list.Add(new PrefsDatabaseInfo(
                    Name: Path.GetFileNameWithoutExtension(file),
                    RelativePath: rel,
                    SizeBytes: info.Length,
                    ModifiedUtcMs: new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                    Active: PathsEqual(rel, active)));
            }
        }

        return list;
    }

    // Create profiles/<name>.db as a valid (empty) LiteDB file. Returns the
    // relative path. Throws if the name is invalid or the profile exists.
    public static string CreateProfile(string name)
    {
        var safe = SanitizeName(name);
        var profilesDir = Path.Combine(DataDir, ProfilesDirName);
        Directory.CreateDirectory(profilesDir);

        var target = Path.Combine(profilesDir, safe + ".db");
        if (File.Exists(target))
            throw new InvalidOperationException($"A profile named \"{safe}\" already exists.");

        // Open + dispose to materialize a valid empty LiteDB file on disk.
        using (var db = new LiteDatabase(target))
        {
            db.Checkpoint();
        }

        return ProfilesDirName + "/" + safe + ".db";
    }

    // True when the file at <paramref name="path"/> opens as a real LiteDB
    // database (header + every collection's first data page readable). Used to
    // reject a corrupt or wrong-type file at IMPORT time, so a bad upload is
    // refused up front with a clear message instead of silently becoming a reset
    // database the next time the profile is activated (EnsureUsable would move
    // it aside on launch — losing the operator's settings without warning).
    public static bool IsValidDatabase(string path) =>
        Probe(path, null) != ProbeResult.Corrupt;

    // Delete a database file and its LiteDB -log sidecar, best effort.
    private static void TryDeleteDbFiles(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
        try { if (File.Exists(path + "-log")) File.Delete(path + "-log"); } catch { /* best effort */ }
    }

    // Copy an existing .db into profiles/<name>.db. Derives the name from the
    // source file when name is null/blank. Refuses to overwrite an existing
    // profile, and rejects a file that is not a valid LiteDB database.
    public static string ImportProfile(string sourcePath, string? name)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("Source path is required.", nameof(sourcePath));
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Source database does not exist.", sourcePath);

        var derived = string.IsNullOrWhiteSpace(name)
            ? Path.GetFileNameWithoutExtension(sourcePath)
            : name;
        var safe = SanitizeName(derived);

        var profilesDir = Path.Combine(DataDir, ProfilesDirName);
        Directory.CreateDirectory(profilesDir);

        var target = Path.Combine(profilesDir, safe + ".db");
        if (File.Exists(target))
            throw new InvalidOperationException($"A profile named \"{safe}\" already exists.");

        File.Copy(sourcePath, target, overwrite: false);
        if (!IsValidDatabase(target))
        {
            TryDeleteDbFiles(target);
            throw new InvalidOperationException(
                "That file is not a valid Zeus settings database (it may be corrupt or a different kind of file).");
        }
        return ProfilesDirName + "/" + safe + ".db";
    }

    // Save an uploaded .db stream into profiles/<name>.db. Used by the file-
    // picker import in the UI, which uploads the chosen file's bytes (the
    // webview can't hand the server a filesystem path). Refuses to overwrite an
    // existing profile, and rejects a file that is not a valid LiteDB database.
    public static string ImportProfileFromStream(Stream source, string name)
    {
        ArgumentNullException.ThrowIfNull(source);
        var safe = SanitizeName(name);

        var profilesDir = Path.Combine(DataDir, ProfilesDirName);
        Directory.CreateDirectory(profilesDir);

        var target = Path.Combine(profilesDir, safe + ".db");
        if (File.Exists(target))
            throw new InvalidOperationException($"A profile named \"{safe}\" already exists.");

        // Close the file handle before validating so the probe can open it.
        using (var dest = File.Create(target))
            source.CopyTo(dest);

        if (!IsValidDatabase(target))
        {
            TryDeleteDbFiles(target);
            throw new InvalidOperationException(
                "That file is not a valid Zeus settings database (it may be corrupt or a different kind of file).");
        }
        return ProfilesDirName + "/" + safe + ".db";
    }

    // Read a profile's raw bytes for export/download. Opens with
    // FileShare.ReadWrite so the active (currently open) database can still be
    // exported. Throws if the path escapes the data dir or the file is missing.
    public static byte[] ReadProfileBytes(string relativePath, out string fileName)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("Relative path is required.", nameof(relativePath));

        var full = ResolveUnderDataDir(relativePath)
            ?? throw new ArgumentException("Path is outside the Zeus data directory.", nameof(relativePath));
        if (!File.Exists(full))
            throw new FileNotFoundException("Database does not exist.", full);

        fileName = Path.GetFileName(full);
        using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    private static PrefsDatabaseInfo DescribeDefault(string active)
    {
        var path = Path.Combine(DataDir, LegacyFileName);
        long size = 0;
        long modified = 0;
        if (File.Exists(path))
        {
            var info = new FileInfo(path);
            size = info.Length;
            modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeMilliseconds();
        }
        return new PrefsDatabaseInfo(
            Name: "Default",
            RelativePath: LegacyFileName,
            SizeBytes: size,
            ModifiedUtcMs: modified,
            Active: PathsEqual(LegacyFileName, active));
    }

    // Letters/digits/-/_/space; trim; reject empty. Keeps the profile filename
    // safe and predictable across platforms.
    private static string SanitizeName(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            throw new ArgumentException("Profile name is required.");

        var sb = new StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_' or ' ')
                sb.Append(ch);
        }

        var safe = sb.ToString().Trim();
        if (safe.Length == 0)
            throw new ArgumentException("Profile name must contain at least one letter, digit, '-', or '_'.");
        return safe;
    }

    // Resolve a relative path to a full path and assert it stays under DataDir.
    // Returns null when the resolved path escapes DataDir (traversal attempt).
    private static string? ResolveUnderDataDir(string relativePath)
    {
        var dataDir = Path.GetFullPath(DataDir);
        var full = Path.GetFullPath(Path.Combine(dataDir, relativePath));

        var prefix = dataDir.EndsWith(Path.DirectorySeparatorChar)
            ? dataDir
            : dataDir + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    // Normalize separators to forward slashes for the pointer file / wire shape
    // so a path written on Windows reads back consistently.
    private static string Normalize(string relativePath) =>
        relativePath.Replace('\\', '/').Trim();

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
}
