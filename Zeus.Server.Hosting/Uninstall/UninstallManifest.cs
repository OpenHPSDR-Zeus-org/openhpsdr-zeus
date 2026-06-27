// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance statement.

using System.Runtime.InteropServices;

namespace Zeus.Server.Uninstall;

// Builds the SERVER-OWNED list of paths/registry keys a full Zeus uninstall may
// delete. This is the safety heart of the feature: a destructive wipe that must
// NEVER resolve to a shared/parent/system location. The endpoint takes NO path
// from the client — everything here is derived from the live path-producers and
// then validated, fail-closed: if ANY required base is missing or ANY essential
// entry fails validation, the WHOLE manifest aborts and nothing is deleted.
//
// Why this re-derives paths instead of calling the live producers' combined
// output: producers like PrefsDbPath.DataDir do Path.Combine(LocalApplicationData,
// "Zeus") with no null-guard. When HOME/$XDG is unset (a systemd/launchd/cron/CI
// context, or the zeus-agent rbash service user) GetFolderPath returns "" and the
// producer hands back the RELATIVE string "Zeus"; Path.GetFullPath then silently
// re-anchors it to the current working directory, yielding an absolute, leaf-"Zeus"
// path that would pass a naive check and get rm -rf'd (e.g. <cwd>/Zeus = the repo
// checkout). So we fetch every base RAW, assert it is non-empty AND fully-qualified
// BEFORE any Combine, and abort otherwise.

public enum OsKind { Windows, MacOs, Linux }

public enum EntryKind { File, Directory }

public sealed record UninstallPath(string Path, EntryKind Kind);

public sealed record RegistryDelete(string Hive, string SubKey);

public sealed record UninstallManifest(
    IReadOnlyList<UninstallPath> Paths,
    IReadOnlyList<RegistryDelete> RegistryKeys,
    string? WindowsUninstallerCommand,
    bool RemoveBinary,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> AbortReasons)
{
    public bool Ok => AbortReasons.Count == 0;

    public static UninstallManifest Aborted(params string[] reasons) =>
        new([], [], null, false, [], reasons);
}

// Abstracts every environment input the builder reads, so the hostile-env test
// matrix (HOME unset, env overrides, metachar paths, reparse points) can run
// without mutating real process state or touching the disk.
public interface IUninstallEnv
{
    OsKind Os { get; }
    string? Home { get; }          // UserProfile, RAW (may be null/empty)
    string? LocalAppData { get; }  // RAW
    string? RoamingAppData { get; }// RAW (Windows roaming)
    string? XdgDataHome { get; }   // RAW $XDG_DATA_HOME
    string? GetEnv(string name);
    string? ProcessPath { get; }
    string CurrentDirectory { get; }
    bool IsReparsePoint(string path);
}

public sealed class SystemUninstallEnv : IUninstallEnv
{
    public OsKind Os =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? OsKind.Windows
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? OsKind.MacOs
        : OsKind.Linux;

    // NOTE: deliberately NO SpecialFolderOption.Create here — we are reading
    // locations to delete, not to create, and Create could materialise a stray dir.
    public string? Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public string? LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public string? RoamingAppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    public string? XdgDataHome => Environment.GetEnvironmentVariable("XDG_DATA_HOME");
    public string? GetEnv(string name) => Environment.GetEnvironmentVariable(name);
    public string? ProcessPath => Environment.ProcessPath;
    public string CurrentDirectory => Environment.CurrentDirectory;

    public bool IsReparsePoint(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            return (attrs & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false; // can't stat → not treated as a reparse point; descendant guard still applies
        }
    }
}

public static class UninstallManifestBuilder
{
    // Control characters that would corrupt the NUL-delimited manifest file or
    // the result marker. Shell metacharacters ($, `, *, ", …) are NOT forbidden:
    // the helper passes paths as DATA (NUL-delimited file → quoted "$p" / -LiteralPath),
    // so they can neither glob nor execute, and they ARE legal in macOS/Linux paths.
    // Rejecting them would wrongly refuse to uninstall for users whose home dir
    // legitimately contains one.
    private static readonly char[] ForbiddenChars = ['\0', '\n', '\r'];

    // Leaf names that, if they were ever the FINAL segment of a delete target,
    // mean a base collapsed — hard-abort, never delete one of these.
    private static readonly HashSet<string> ForbiddenLeaves =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "", ".", "..", "Library", "Caches", ".cache", ".local", "share",
            "Application Support", "AppData", "Local", "Roaming", "Downloads",
            "Documents", "Desktop", "Users", "home", "tmp", "var", "etc", "usr",
            "Program Files", "Program Files (x86)", "Applications", "System",
        };

    private sealed record Candidate(
        string Path, EntryKind Kind, string ExpectedLeaf, string SafeRoot, bool Essential);

    public static UninstallManifest Build(IUninstallEnv env, bool removeBinary)
    {
        var aborts = new List<string>();
        var warnings = new List<string>();

        // ---- 1. Validate the RAW bases this OS needs, BEFORE any Combine. ----
        string? RequireBase(string? raw, string label)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                aborts.Add($"{label} is empty/unset — refusing to uninstall (a missing base could resolve to the working directory).");
                return null;
            }
            if (!Path.IsPathFullyQualified(raw))
            {
                aborts.Add($"{label} '{raw}' is not an absolute path — refusing to uninstall.");
                return null;
            }
            return raw;
        }

        var home = RequireBase(env.Home, "User home");
        var localAppData = RequireBase(env.LocalAppData, "LocalApplicationData");
        string? roaming = env.Os == OsKind.Windows ? RequireBase(env.RoamingAppData, "ApplicationData (Roaming)") : null;

        // Linux data root = $XDG_DATA_HOME or ~/.local/share (home already required).
        string? linuxDataRoot = null;
        if (env.Os == OsKind.Linux && home is not null)
        {
            var xdg = env.XdgDataHome;
            linuxDataRoot = string.IsNullOrWhiteSpace(xdg) ? Path.Combine(home, ".local", "share") : xdg;
            if (!Path.IsPathFullyQualified(linuxDataRoot))
            {
                aborts.Add($"XDG_DATA_HOME '{xdg}' is not absolute — refusing to uninstall.");
                linuxDataRoot = null;
            }
        }

        if (aborts.Count > 0)
            return UninstallManifest.Aborted([.. aborts]);

        // ---- 2. Assemble candidates from the GUARDED bases. ----
        var candidates = new List<Candidate>();

        // DataDir (the big recursive tree: prefs, profiles, logbook, wisdom,
        // certs, nr3-models, freedv, hamclock, node, crash-dumps, markers).
        string dataDir = env.Os switch
        {
            OsKind.Windows => Path.Combine(localAppData!, "Zeus"),
            OsKind.MacOs => Path.Combine(home!, "Library", "Application Support", "Zeus"),
            _ => Path.Combine(linuxDataRoot!, "Zeus"),
        };
        string dataRoot = env.Os switch
        {
            OsKind.Windows => localAppData!,
            OsKind.MacOs => Path.Combine(home!, "Library", "Application Support"),
            _ => linuxDataRoot!,
        };
        candidates.Add(new(dataDir, EntryKind.Directory, "Zeus", dataRoot, Essential: true));

        // Plugins root — can live OUTSIDE DataDir (Windows Roaming, Linux lowercase
        // "zeus"). Only delete the DEFAULT location; an operator ZEUS_PLUGINS_PATH
        // override points at a possibly-shared dir and is NEVER recursively deleted.
        if (!string.IsNullOrEmpty(env.GetEnv(Zeus.Plugins.Host.PluginRoot.EnvVar)))
        {
            warnings.Add("ZEUS_PLUGINS_PATH is set to a custom plugins folder; it is left untouched. Clear that env var if you want it removed too.");
        }
        else
        {
            (string path, string root) plugins = env.Os switch
            {
                OsKind.Windows => (Path.Combine(roaming!, "Zeus", "plugins"), Path.Combine(roaming!, "Zeus")),
                OsKind.MacOs => (Path.Combine(home!, "Library", "Application Support", "Zeus", "plugins"), dataDir),
                _ => (Path.Combine(linuxDataRoot!, "zeus", "plugins"), Path.Combine(linuxDataRoot!, "zeus")),
            };
            candidates.Add(new(plugins.path, EntryKind.Directory, "plugins", plugins.root, Essential: false));
        }

        // Recordings — ONLY the "Zeus Recordings" subfolder of Downloads, never
        // Downloads itself. (The user's chosen backup also lands in Downloads root,
        // which this never touches.)
        string downloads = Path.Combine(home!, "Downloads");
        candidates.Add(new(Path.Combine(downloads, "Zeus Recordings"), EntryKind.Directory, "Zeus Recordings", downloads, Essential: false));

        // Webview caches — leaf is the HARD-CODED Photino product name, never derived.
        const string Product = "OpenhpsdrZeus";
        switch (env.Os)
        {
            case OsKind.MacOs:
                candidates.Add(new(Path.Combine(home!, "Library", "Caches", Product), EntryKind.Directory, Product, Path.Combine(home!, "Library", "Caches"), Essential: false));
                candidates.Add(new(Path.Combine(home!, "Library", "WebKit", Product), EntryKind.Directory, Product, Path.Combine(home!, "Library", "WebKit"), Essential: false));
                break;
            case OsKind.Linux:
                string cacheRoot = Path.Combine(home!, ".cache");
                candidates.Add(new(Path.Combine(cacheRoot, Product), EntryKind.Directory, Product, cacheRoot, Essential: false));
                break;
            case OsKind.Windows:
                // The unambiguous, co-located WebView2 user-data folder next to the
                // exe. The relocated %LOCALAPPDATA%\<vendor> case is intentionally NOT
                // guessed — a wrong guess could wipe another WebView2 app's profile;
                // the audit's rule is "resolve from the live instance or skip".
                var exe = env.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exe) && Path.IsPathFullyQualified(exe))
                {
                    var exeDir = Path.GetDirectoryName(exe);
                    if (!string.IsNullOrEmpty(exeDir))
                    {
                        var wv = Path.Combine(exeDir, "OpenhpsdrZeus.exe.WebView2", "EBWebView");
                        candidates.Add(new(wv, EntryKind.Directory, "EBWebView", Path.Combine(exeDir, "OpenhpsdrZeus.exe.WebView2"), Essential: false));
                    }
                }
                break;
        }

        // ---- 3. External ZEUS_PREFS_PATH override — FILE-only, never the dir. ----
        var prefsOverride = env.GetEnv("ZEUS_PREFS_PATH");
        // GetFullPath throws on illegal characters (e.g. a newline on Windows);
        // a malformed override is simply skipped, not crashed on.
        string? full = null;
        if (!string.IsNullOrWhiteSpace(prefsOverride) && Path.IsPathFullyQualified(prefsOverride))
        {
            try { full = Path.GetFullPath(prefsOverride); } catch { full = null; }
        }
        if (full is not null)
        {
            // Only act if it resolves OUTSIDE DataDir (inside is already covered).
            if (!IsStrictDescendant(full, dataDir))
            {
                warnings.Add($"ZEUS_PREFS_PATH points outside the data folder ({full}); only the database FILES it names are removed, not the containing directory.");
                var dir = Path.GetDirectoryName(full);
                if (dir is not null && Path.IsPathFullyQualified(dir))
                {
                    foreach (var f in new[] { full, full + "-log" })
                        candidates.Add(new(f, EntryKind.File, Path.GetFileName(f), dir, Essential: false));
                    var logbook = Path.Combine(dir, "zeus-logbook.db");
                    foreach (var f in new[] { logbook, logbook + "-log" })
                        candidates.Add(new(f, EntryKind.File, Path.GetFileName(f), dir, Essential: false));
                }
            }
        }

        // ---- 4. Validate every candidate; essential failure aborts the wipe. ----
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var paths = new List<UninstallPath>();
        foreach (var c in candidates)
        {
            var reason = Validate(c, env, home!);
            if (reason is not null)
            {
                if (c.Essential)
                    aborts.Add($"Safety check failed for '{c.Path}': {reason}");
                else
                    warnings.Add($"Skipping '{c.Path}': {reason}");
                continue;
            }
            var canon = Path.GetFullPath(c.Path);
            if (seen.Add(canon))
                paths.Add(new(canon, c.Kind));
        }

        if (aborts.Count > 0)
            return UninstallManifest.Aborted([.. aborts]);

        // ---- 5. Registry (Windows: exactly the one app-owned WER key). ----
        var registry = new List<RegistryDelete>();
        if (env.Os == OsKind.Windows)
            registry.Add(new("HKCU", @"Software\Microsoft\Windows\Windows Error Reporting\LocalDumps\OpenhpsdrZeus.exe"));

        // ---- 6. Binary removal (the install-type-aware part). ----
        string? winUninstaller = null;
        bool binary = false;
        if (removeBinary)
        {
            (binary, winUninstaller) = ResolveBinaryRemoval(env, paths, warnings);
        }

        return new UninstallManifest(paths, registry, winUninstaller, binary, warnings, []);
    }

    // Per-entry validation. Returns null when safe, else the reason it is rejected.
    private static string? Validate(Candidate c, IUninstallEnv env, string home)
    {
        var raw = c.Path;
        if (string.IsNullOrWhiteSpace(raw)) return "empty path";
        if (raw.IndexOfAny(ForbiddenChars) >= 0) return "path contains a forbidden character";
        if (!Path.IsPathFullyQualified(raw)) return "not absolute";

        string full;
        try { full = Path.GetFullPath(raw); }
        catch { return "could not canonicalize"; }

        var leaf = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!string.Equals(leaf, c.ExpectedLeaf, StringComparison.Ordinal))
            return $"leaf '{leaf}' != expected '{c.ExpectedLeaf}'";
        if (ForbiddenLeaves.Contains(leaf))
            return $"leaf '{leaf}' is a protected directory name";

        // Strict descendant of its declared safe root, AND the root must itself be
        // a real, fully-qualified path that is NOT the home/filesystem root.
        if (string.IsNullOrWhiteSpace(c.SafeRoot) || !Path.IsPathFullyQualified(c.SafeRoot))
            return "safe-root not absolute";
        var safeRoot = Path.GetFullPath(c.SafeRoot);
        if (!IsStrictDescendant(full, safeRoot))
            return $"not a strict descendant of '{safeRoot}'";

        // Never equal to / an ancestor of: home, the filesystem root, or a drive root.
        if (PathEquals(full, home)) return "resolves to the user home";
        if (IsRootLike(full)) return "resolves to a filesystem/drive root";
        if (IsAncestorOrEqual(full, home)) return "is an ancestor of the user home";

        // Reparse-point containment: never delete THROUGH a symlink/junction. The
        // managed deleter re-checks at delete time too (TOCTOU defence).
        if (env.IsReparsePoint(full)) return "target is a symlink/junction";

        return null;
    }

    private static (bool, string?) ResolveBinaryRemoval(IUninstallEnv env, List<UninstallPath> paths, List<string> warnings)
    {
        switch (env.Os)
        {
            case OsKind.MacOs:
            {
                // The running binary lives inside the bundle
                // (.../OpenHPSDR Zeus.app/Contents/.../OpenhpsdrZeus). Walk up to the
                // enclosing *.app and remove THAT (handles installs outside
                // /Applications), plus the sibling Server wrapper. Each is validated
                // as a non-reparse strict descendant of its own parent.
                var added = false;
                var pp = env.ProcessPath;
                if (!string.IsNullOrWhiteSpace(pp) && Path.IsPathFullyQualified(pp))
                {
                    var dir = Path.GetDirectoryName(Path.GetFullPath(pp));
                    while (dir is not null)
                    {
                        if (dir.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                        {
                            var parent = Path.GetDirectoryName(dir);
                            if (parent is not null
                                && Validate(new(dir, EntryKind.Directory, Path.GetFileName(dir), parent, false), env, env.Home ?? "") is null)
                            {
                                paths.Add(new(dir, EntryKind.Directory));
                                added = true;
                                var server = Path.Combine(parent, "OpenHPSDR Zeus Server.app");
                                if (Validate(new(server, EntryKind.Directory, "OpenHPSDR Zeus Server.app", parent, false), env, env.Home ?? "") is null)
                                    paths.Add(new(server, EntryKind.Directory));
                            }
                            break;
                        }
                        dir = Path.GetDirectoryName(dir);
                    }
                }
                if (!added)
                {
                    const string apps = "/Applications";
                    foreach (var name in new[] { "OpenHPSDR Zeus.app", "OpenHPSDR Zeus Server.app" })
                    {
                        var p = Path.Combine(apps, name);
                        if (Validate(new(p, EntryKind.Directory, name, apps, false), env, env.Home ?? "") is null)
                            paths.Add(new(Path.GetFullPath(p), EntryKind.Directory));
                    }
                }
                // If no .app could be located, report binary removal as unsupported.
                var foundApp = paths.Any(x => x.Path.EndsWith(".app", StringComparison.OrdinalIgnoreCase));
                if (!foundApp)
                    warnings.Add("Could not locate the Zeus app bundle; data was wiped — drag the app to the Trash to finish.");
                return (foundApp, null);
            }
            case OsKind.Linux:
            {
                // AppImage: the artifact path is in $APPIMAGE (ProcessPath is the
                // read-only FUSE mount). If neither is resolvable, skip the binary.
                var appImage = env.GetEnv("APPIMAGE");
                if (!string.IsNullOrWhiteSpace(appImage) && Path.IsPathFullyQualified(appImage)
                    && appImage.IndexOfAny(ForbiddenChars) < 0)
                {
                    paths.Add(new(Path.GetFullPath(appImage), EntryKind.File));
                    return (true, null);
                }
                warnings.Add("Could not safely resolve the Zeus binary on Linux (tarball install / no $APPIMAGE); data was wiped — delete the install folder manually.");
                return (false, null);
            }
            case OsKind.Windows:
            default:
                // Binary removal on Windows is the installer's job. The caller fills
                // the QuietUninstallString (per-user installs only); HKLM/Program-Files
                // installs are refused upstream and fall back to data-only.
                return (true, null); // RemoveBinary=true signals "invoke uninstaller if command present"
        }
    }

    internal static bool IsStrictDescendant(string path, string root)
    {
        var p = TrimSep(Path.GetFullPath(path));
        var r = TrimSep(Path.GetFullPath(root));
        if (PathEquals(p, r)) return false;
        var prefix = r + Path.DirectorySeparatorChar;
        return p.StartsWith(prefix, PathComparison);
    }

    private static bool IsAncestorOrEqual(string maybeAncestor, string path)
    {
        var a = TrimSep(maybeAncestor);
        var p = TrimSep(Path.GetFullPath(path));
        if (PathEquals(a, p)) return true;
        return p.StartsWith(a + Path.DirectorySeparatorChar, PathComparison);
    }

    private static bool IsRootLike(string full)
    {
        var t = TrimSep(full);
        if (t.Length == 0) return true;
        var root = Path.GetPathRoot(full) ?? "";
        return PathEquals(TrimSep(root), t);
    }

    private static string TrimSep(string p) =>
        p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    // macOS/Windows are case-insensitive filesystems by default; Linux is case-
    // sensitive (the "Zeus" vs "zeus" split is real there).
    private static StringComparison PathComparison =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

    private static bool PathEquals(string a, string b) =>
        string.Equals(TrimSep(a), TrimSep(b), PathComparison);
}
