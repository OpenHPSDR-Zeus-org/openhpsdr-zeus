// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Zeus.Server.Wav;

/// <summary>
/// Owns the managed recordings tree on disk: the root folder, the one-time
/// migration of loose legacy files, traversal-guarded path resolution, the
/// directory listing, and folder/file CRUD. Deliberately free of any audio /
/// DSP / radio dependency so it can be exercised directly against a temp root
/// in unit tests; <see cref="WavRecorderService"/> composes it for the live
/// host.
///
/// Every path that crosses the wire is root-relative with forward slashes;
/// every path that comes in from the wire is resolved through
/// <see cref="ResolveRel"/>, which rejects absolute paths and any "<c>..</c>"
/// that would escape the root.
/// </summary>
public sealed class WavLibrary
{
    // Managed root folder name appended to the OS Downloads folder. The whole
    // tree under this folder belongs to Zeus.
    public const string ManagedFolderName = "Zeus Recordings";

    // Files we create carry this prefix (zeus-rx-… / zeus-tx-…) so the source
    // can be inferred from the name and the legacy migration knows what is ours.
    public const string FilePrefix = "zeus-";

    private string _root;
    private readonly ILogger _log;

    /// <param name="migrate">When true (the default), run the one-time
    /// loose-Downloads migration. This MUST be false for an operator-chosen
    /// custom root: that root's parent is an arbitrary user directory and we
    /// must never scan it for, or move, files.</param>
    public WavLibrary(string root, ILogger? log = null, bool migrate = true)
    {
        _root = root;
        _log = log ?? NullLogger.Instance;
        Directory.CreateDirectory(_root);
        if (migrate) MigrateLooseDownloads();
    }

    public string Root => _root;

    /// <summary>Point the library at a different root, creating it if needed.
    /// NEVER migrates — relocating to a user-chosen directory must not touch its
    /// parent. All path helpers use the new root immediately afterward.</summary>
    public void SetRoot(string newRoot)
    {
        if (string.IsNullOrWhiteSpace(newRoot))
            throw new ArgumentException("root is required", nameof(newRoot));
        Directory.CreateDirectory(newRoot);
        _root = newRoot;
        _log.LogInformation("wav.root set {Root}", newRoot);
    }

    // Recordings save under the OS Downloads folder by default so they land
    // where the operator expects. UserProfile/Downloads is the default on
    // macOS, Windows, and Linux; fall back to the profile root, then the
    // working dir, if Downloads can't be resolved.
    public static string ResolveDownloadsDir()
    {
        string? home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) return Environment.CurrentDirectory;
        string downloads = Path.Combine(home, "Downloads");
        return Directory.Exists(downloads) ? downloads : home;
    }

    public static string DefaultRoot()
        => Path.Combine(ResolveDownloadsDir(), ManagedFolderName);

    // One-time migration: pull any loose zeus-*.wav files that older builds
    // wrote directly into the parent (Downloads) folder up into the managed
    // root. Best-effort and defensive — never throws.
    private void MigrateLooseDownloads()
    {
        try
        {
            var parent = Directory.GetParent(_root);
            if (parent is null || !parent.Exists) return;
            foreach (var path in Directory.EnumerateFiles(parent.FullName, FilePrefix + "*.wav"))
            {
                try
                {
                    string dest = Path.Combine(_root, Path.GetFileName(path));
                    if (File.Exists(dest))
                    {
                        _log.LogInformation("wav.migrate skip (exists) file={File}", path);
                        continue;
                    }
                    File.Move(path, dest);
                    _log.LogInformation("wav.migrate moved {From} -> {To}", path, dest);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _log.LogWarning(ex, "wav.migrate failed file={File}", path);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "wav.migrate scan failed");
        }
    }

    // ---- Listing -----------------------------------------------------------

    public IReadOnlyList<WavRecordingInfo> ListRecordings()
    {
        var list = new List<WavRecordingInfo>();
        if (!Directory.Exists(_root)) return list;

        foreach (var path in Directory.EnumerateFiles(_root, "*.wav", SearchOption.AllDirectories))
        {
            var fi = new FileInfo(path);
            string rel = RelOf(path)!;
            string folder = Path.GetDirectoryName(rel)?.Replace('\\', '/') ?? "";
            double durationSec = 0;
            try
            {
                var (rate, count) = WavFile.ReadInfo(path);
                durationSec = Math.Round(count / (double)Math.Max(1, rate), 1);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                // Corrupt/locked file — list it with duration 0 rather than
                // dropping the whole listing.
                _log.LogDebug(ex, "wav.list info failed file={File}", path);
            }

            list.Add(new WavRecordingInfo(
                Name: Path.GetFileNameWithoutExtension(fi.Name),
                FileName: fi.Name,
                RelPath: rel,
                Folder: folder,
                Bytes: fi.Length,
                DurationSec: durationSec,
                Source: DetectSource(fi.Name),
                ModifiedUnixMs: new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds()));
        }
        list.Sort((a, b) => b.ModifiedUnixMs.CompareTo(a.ModifiedUnixMs));
        return list;
    }

    public IReadOnlyList<string> ListFolders()
    {
        var list = new List<string>();
        if (!Directory.Exists(_root)) return list;
        foreach (var dir in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories))
            list.Add(RelOf(dir)!.Replace('\\', '/'));
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    public static string DetectSource(string fileName)
    {
        if (fileName.StartsWith(FilePrefix + "rx-", StringComparison.OrdinalIgnoreCase)) return "rx";
        if (fileName.StartsWith(FilePrefix + "tx-", StringComparison.OrdinalIgnoreCase)) return "tx";
        return "unknown";
    }

    // ---- New recording path ------------------------------------------------

    /// <summary>Resolve (and create if needed) the absolute directory a new
    /// recording in <paramref name="folder"/> should land in.</summary>
    public string ResolveRecordDir(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return _root;
        string dir = ResolveRel(folder);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---- CRUD --------------------------------------------------------------

    public bool DeleteRecording(string relPath)
    {
        string path = ResolveRel(relPath);
        if (!File.Exists(path)) throw new FileNotFoundException("recording not found", relPath);
        File.Delete(path);
        _log.LogInformation("wav.delete file={File}", path);
        return true;
    }

    /// <summary>Rename a recording within its current folder. The new name is
    /// sanitised (path separators and illegal chars stripped) and always keeps
    /// the <c>.wav</c> extension. Refuses to overwrite an existing file.
    /// Returns the new root-relative path.</summary>
    public string RenameRecording(string relFrom, string newDisplayName)
    {
        string from = ResolveRel(relFrom);
        if (!File.Exists(from)) throw new FileNotFoundException("recording not found", relFrom);

        string safe = SanitizeFileStem(newDisplayName);
        string dir = Path.GetDirectoryName(from)!;
        string to = Path.Combine(dir, safe + ".wav");
        if (File.Exists(to) && !string.Equals(to, from, StringComparison.Ordinal))
            throw new InvalidOperationException("a recording with that name already exists");

        File.Move(from, to, overwrite: false);
        _log.LogInformation("wav.rename {From} -> {To}", from, to);
        return RelOf(to)!;
    }

    /// <summary>Move a recording into another folder under the root (created if
    /// missing). Refuses to overwrite. Returns the new root-relative path.</summary>
    public string MoveRecording(string relFrom, string destFolder)
    {
        string from = ResolveRel(relFrom);
        if (!File.Exists(from)) throw new FileNotFoundException("recording not found", relFrom);

        string destDir = string.IsNullOrWhiteSpace(destFolder) ? _root : ResolveRel(destFolder);
        Directory.CreateDirectory(destDir);

        string to = Path.Combine(destDir, Path.GetFileName(from));
        if (File.Exists(to) && !string.Equals(to, from, StringComparison.Ordinal))
            throw new InvalidOperationException("a recording with that name already exists in the destination");

        File.Move(from, to, overwrite: false);
        _log.LogInformation("wav.move {From} -> {To}", from, to);
        return RelOf(to)!;
    }

    /// <summary>Create a folder under the root. Returns its root-relative path.</summary>
    public string CreateFolder(string relPath)
    {
        string dir = ResolveRel(relPath);
        Directory.CreateDirectory(dir);
        _log.LogInformation("wav.folder.create {Dir}", dir);
        return RelOf(dir)!.Replace('\\', '/');
    }

    /// <summary>Delete a folder under the root. Recursively deletes only if the
    /// folder (and its subfolders) contain solely <c>.wav</c> files and
    /// ignorable OS sidecars (<c>.DS_Store</c>, <c>Thumbs.db</c>, dot-files);
    /// refuses on any genuine user content. Returns the deleted root-relative
    /// path.</summary>
    public string DeleteFolder(string relPath)
    {
        string dir = ResolveRel(relPath);
        if (string.Equals(Path.GetFullPath(dir), Path.GetFullPath(_root), StringComparison.Ordinal))
            throw new InvalidOperationException("cannot delete the recordings root");
        if (!Directory.Exists(dir)) throw new FileNotFoundException("folder not found", relPath);

        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) continue;
            if (IsIgnorableSidecar(Path.GetFileName(file))) continue; // .DS_Store / Thumbs.db etc.
            throw new InvalidOperationException("folder contains non-recording files; refusing to delete");
        }

        // recursive: true sweeps the recordings AND any ignorable sidecars.
        Directory.Delete(dir, recursive: true);
        _log.LogInformation("wav.folder.delete {Dir}", dir);
        return RelOf(dir)!.Replace('\\', '/');
    }

    // ---- Path helpers ------------------------------------------------------

    /// <summary>Resolve a root-relative path to an absolute path, rejecting
    /// absolute inputs and anything that escapes the managed root via "<c>..</c>".
    /// The single guarded entry point for every file/folder operation.</summary>
    public string ResolveRel(string rel)
    {
        if (string.IsNullOrWhiteSpace(rel))
            throw new ArgumentException("path is required", nameof(rel));

        // Normalise separators. A rooted input — Unix "/etc", a leading slash,
        // or a Windows drive/UNC path — is rejected outright; wire paths are
        // root-relative with no leading slash.
        string normalized = rel.Replace('\\', '/').Trim();
        if (normalized.Length == 0)
            throw new ArgumentException("path is required", nameof(rel));
        if (normalized.StartsWith('/') || Path.IsPathRooted(normalized))
            throw new ArgumentException("absolute paths are not allowed", nameof(rel));

        string rootFull = Path.GetFullPath(_root);
        string combined = Path.GetFullPath(Path.Combine(rootFull, normalized));

        string rootWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;
        if (!string.Equals(combined, rootFull, StringComparison.Ordinal)
            && !combined.StartsWith(rootWithSep, StringComparison.Ordinal))
            throw new ArgumentException("path escapes the recordings root", nameof(rel));

        return combined;
    }

    /// <summary>Absolute path → root-relative path with forward slashes, or null
    /// if the input is null/empty.</summary>
    public string? RelOf(string? absPath)
    {
        if (string.IsNullOrEmpty(absPath)) return null;
        string rel = Path.GetRelativePath(_root, absPath);
        return rel.Replace('\\', '/');
    }

    // ---- Server-side directory browser -------------------------------------

    /// <summary>List the immediate subdirectories of <paramref name="path"/> so a
    /// remote web client can pick a folder on the machine the backend runs on.
    /// This is a read-only filesystem browse, deliberately NOT confined to the
    /// recordings root — the whole point is choosing a new root anywhere the
    /// operator can write.
    ///
    /// <para>An empty/whitespace <paramref name="path"/> starts at the user's
    /// home directory (falling back to the working directory). The path is
    /// normalised with <see cref="Path.GetFullPath(string)"/>; a path that does
    /// not exist or is not a directory throws
    /// <see cref="DirectoryNotFoundException"/>. Entries that can't be read are
    /// skipped (each is wrapped) rather than failing the whole listing. The
    /// result is sorted by name, ordinal-ignore-case. <c>Parent</c> is null when
    /// the resolved path is a filesystem/drive root.</para></summary>
    public static WavDirListing BrowseDirectories(string? path)
    {
        string start;
        if (string.IsNullOrWhiteSpace(path))
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            start = string.IsNullOrEmpty(home) ? Environment.CurrentDirectory : home;
        }
        else
        {
            start = path;
        }

        string full = Path.GetFullPath(start);
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException($"not a directory: {full}");

        var dirs = new List<WavDirEntry>();
        foreach (var dir in Directory.EnumerateDirectories(full))
        {
            try
            {
                dirs.Add(new WavDirEntry(Path.GetFileName(dir), dir));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Skip entries we can't read rather than dropping the listing.
            }
        }
        dirs.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        string? parent = Directory.GetParent(full)?.FullName;

        return new WavDirListing(
            Path: full,
            Parent: parent,
            Separator: Path.DirectorySeparatorChar.ToString(),
            Dirs: dirs);
    }

    // A FIXED, portable invalid-filename set applied on every platform. Cannot
    // use Path.GetInvalidFileNameChars(): on Unix that is only {'/','\0'}, so a
    // backslash or a Windows-reserved char ( : * ? " < > | ) would survive into
    // an on-disk name. The wire path is always forward-slash and root-relative,
    // so any such char desyncs the on-disk name from RelOf's path and the
    // recording becomes visible-but-unmanageable (Play/Delete/Move throw
    // FileNotFoundException). Strip the same set everywhere, plus control chars.
    private static readonly char[] PortableInvalidFileNameChars =
        { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

    // Windows reserved DEVICE names. A file whose stem (case-insensitive,
    // extension ignored) is one of these is unopenable on Windows even though it
    // is perfectly legal on macOS/Linux. Suffix an underscore on EVERY platform
    // so a clip named on a Mac/Linux box stays portable to a Windows operator.
    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    // Strip path separators and illegal filename characters, drop any trailing
    // .wav the caller included, and reject an empty result. Portable across all
    // platforms Zeus runs on (see PortableInvalidFileNameChars).
    public static string SanitizeFileStem(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("name is required", nameof(name));
        string stem = name.Trim();
        if (stem.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            stem = stem[..^4];
        stem = Path.GetFileName(stem); // strips OS-native directory separators

        var sb = new System.Text.StringBuilder(stem.Length);
        foreach (char c in stem)
        {
            if (c < 0x20) continue;                                   // control chars 0x00-0x1F
            if (Array.IndexOf(PortableInvalidFileNameChars, c) >= 0) continue;
            sb.Append(c);
        }
        stem = sb.ToString().Trim().Trim('.');
        if (stem.Length == 0)
            throw new ArgumentException("name has no usable characters", nameof(name));

        // A Windows reserved device name (CON, NUL, COM1, …) is unopenable on
        // Windows; disambiguate with a trailing underscore on all platforms.
        if (WindowsReservedNames.Contains(stem))
            stem += "_";
        return stem;
    }

    // OS-generated sidecar / metadata files that must never block a folder
    // delete: macOS Finder (.DS_Store), Windows Explorer (Thumbs.db /
    // desktop.ini), and any dot-file. These get deleted alongside the .wavs.
    private static bool IsIgnorableSidecar(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return false;
        if (fileName.StartsWith('.')) return true; // .DS_Store and any dot-file
        return fileName.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>One subdirectory in a <see cref="WavLibrary.BrowseDirectories"/>
/// listing: its display name and its absolute path.</summary>
public sealed record WavDirEntry(string Name, string Path);

/// <summary>Result of <see cref="WavLibrary.BrowseDirectories"/>: the resolved
/// absolute <c>Path</c>, its <c>Parent</c> (null at a filesystem root), the OS
/// directory <c>Separator</c> for display, and the immediate subdirectories.</summary>
public sealed record WavDirListing(
    string Path, string? Parent, string Separator, IReadOnlyList<WavDirEntry> Dirs);
