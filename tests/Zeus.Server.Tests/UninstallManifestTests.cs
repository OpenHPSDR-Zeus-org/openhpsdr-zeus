// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Runtime.InteropServices;
using Zeus.Server.Uninstall;

namespace Zeus.Server.Tests;

// The safety case for the destructive "Reset & Uninstall" wipe. A destructive
// feature is proven by showing it ABORTS or stays in-scope under hostile inputs,
// never by actually deleting. Every test asserts either a fail-closed abort or
// that all emitted paths are Zeus-owned, absolute, leaf-correct strict descendants
// that are never the user home / a base root.
//
// Inputs use the HOST OS's path style (Path.* APIs are host-specific), so the
// matrix is exercised per-platform by CI (macOS, Windows, Linux).
public sealed class UninstallManifestTests
{
    private sealed class FakeEnv : IUninstallEnv
    {
        public OsKind Os { get; set; }
        public string? Home { get; set; }
        public string? LocalAppData { get; set; }
        public string? RoamingAppData { get; set; }
        public string? XdgDataHome { get; set; }
        public string? ProcessPath { get; set; }
        public string CurrentDirectory { get; set; } = Directory.GetCurrentDirectory();
        private readonly Dictionary<string, string?> _env = new(StringComparer.Ordinal);
        public string? GetEnv(string name) => _env.GetValueOrDefault(name);
        public void SetEnv(string name, string? value) => _env[name] = value;
        public HashSet<string> ReparsePoints { get; } = new(StringComparer.Ordinal);
        public bool IsReparsePoint(string path) => ReparsePoints.Contains(path);
    }

    private static OsKind HostOs() => new SystemUninstallEnv().Os;

    // A valid, host-appropriate environment rooted under a unique temp dir so
    // every produced path is host-fully-qualified.
    private static FakeEnv ValidEnv()
    {
        var os = HostOs();
        var home = Path.Combine(Path.GetTempPath(), "zeus-uatest-home-" + Guid.NewGuid().ToString("N"));
        var exe = Path.Combine(Path.GetTempPath(), "zeus-uatest-app", os == OsKind.Windows ? "OpenhpsdrZeus.exe" : "OpenhpsdrZeus");
        return new FakeEnv
        {
            Os = os,
            Home = home,
            LocalAppData = os == OsKind.Windows ? Path.Combine(home, "AppData", "Local") : home,
            RoamingAppData = os == OsKind.Windows ? Path.Combine(home, "AppData", "Roaming") : home,
            XdgDataHome = null, // → ~/.local/share fallback (Linux)
            ProcessPath = exe,
        };
    }

    // ---- The CRITICAL guard: empty/unset base must NOT collapse to <cwd>/Zeus. ----

    [Fact]
    public void HomeUnset_AbortsEntireWipe()
    {
        var env = ValidEnv();
        env.Home = null;
        env.LocalAppData = HostOs() == OsKind.Windows ? null : null;
        env.XdgDataHome = null;

        var m = UninstallManifestBuilder.Build(env, removeBinary: false);

        Assert.False(m.Ok);
        Assert.NotEmpty(m.AbortReasons);
        Assert.Empty(m.Paths); // nothing to delete when a base is missing
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void WhitespaceBase_Aborts(string blank)
    {
        var env = ValidEnv();
        env.Home = blank;
        env.LocalAppData = blank;
        env.XdgDataHome = blank;

        var m = UninstallManifestBuilder.Build(env, removeBinary: false);

        Assert.False(m.Ok);
        Assert.Empty(m.Paths);
    }

    [Fact]
    public void RelativeBase_Aborts_AndNeverAnchorsToCwd()
    {
        var env = ValidEnv();
        env.Home = "Zeus";          // relative — the exact GetFolderPath-returns-"" footgun
        env.LocalAppData = "Zeus";
        env.XdgDataHome = "zeus";

        var m = UninstallManifestBuilder.Build(env, removeBinary: false);

        Assert.False(m.Ok);
        Assert.Empty(m.Paths);
        // Belt: even if it had produced anything, none may sit under the CWD.
        var cwd = Directory.GetCurrentDirectory();
        Assert.DoesNotContain(m.Paths, p => p.Path.StartsWith(cwd, StringComparison.OrdinalIgnoreCase));
    }

    // ---- Valid env produces ONLY Zeus-owned, in-scope paths. ----

    [Fact]
    public void ValidEnv_ProducesZeusOwnedPaths_NoneEscapeHome()
    {
        var env = ValidEnv();

        var m = UninstallManifestBuilder.Build(env, removeBinary: false);

        Assert.True(m.Ok, string.Join(" | ", m.AbortReasons));
        Assert.NotEmpty(m.Paths);

        var home = Path.GetFullPath(env.Home!);
        foreach (var p in m.Paths)
        {
            Assert.True(Path.IsPathFullyQualified(p.Path), $"not absolute: {p.Path}");
            Assert.NotEqual(TrimSep(home), TrimSep(p.Path));      // never the home dir itself
            Assert.NotEqual(TrimSep(p.Path), TrimSep(Path.GetPathRoot(p.Path) ?? "")); // never a root
            // every emitted path is under the home tree (all our bases are)
            Assert.StartsWith(home, p.Path, StringComparison.OrdinalIgnoreCase);
        }

        // The DataDir entry is always present with the exact "Zeus" leaf.
        Assert.Contains(m.Paths, p => string.Equals(Path.GetFileName(TrimSep(p.Path)), "Zeus", StringComparison.Ordinal));
    }

    // ---- ZEUS_PREFS_PATH override outside DataDir → FILE entries only, never a dir. ----

    [Fact]
    public void PrefsPathOverride_OutsideDataDir_NeverDeletesTheDirectory()
    {
        var env = ValidEnv();
        var sharedDir = Path.Combine(Path.GetTempPath(), "shared-prefs-" + Guid.NewGuid().ToString("N"));
        env.SetEnv("ZEUS_PREFS_PATH", Path.Combine(sharedDir, "zeus-prefs.db"));

        var m = UninstallManifestBuilder.Build(env, removeBinary: false);

        Assert.True(m.Ok);
        // The containing dir must NEVER be a delete target...
        Assert.DoesNotContain(m.Paths, p => p.Kind == EntryKind.Directory
            && TrimSep(p.Path).Equals(TrimSep(Path.GetFullPath(sharedDir)), StringComparison.OrdinalIgnoreCase));
        // ...but the named DB files may be (as File entries).
        Assert.Contains(m.Paths, p => p.Kind == EntryKind.File && p.Path.EndsWith("zeus-prefs.db", StringComparison.Ordinal));
        Assert.NotEmpty(m.Warnings);
    }

    // ---- ZEUS_PLUGINS_PATH override → never recursively delete a shared dir. ----

    [Fact]
    public void PluginsPathOverride_IsNeverDeleted()
    {
        var env = ValidEnv();
        var shared = Path.Combine(Path.GetTempPath(), "shared", "plugins");
        env.SetEnv(Zeus.Plugins.Host.PluginRoot.EnvVar, shared);

        var m = UninstallManifestBuilder.Build(env, removeBinary: false);

        Assert.True(m.Ok);
        Assert.DoesNotContain(m.Paths, p => p.Path.Contains(shared, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(m.Warnings, w => w.Contains("ZEUS_PLUGINS_PATH", StringComparison.Ordinal));
    }

    [Fact]
    public void NewlineInOverridePath_IsRejected()
    {
        var env = ValidEnv();
        env.SetEnv("ZEUS_PREFS_PATH", Path.Combine(Path.GetTempPath(), "ev\nil", "zeus-prefs.db"));

        var m = UninstallManifestBuilder.Build(env, removeBinary: false);

        // Essential entries still fine; the newline path is simply never emitted.
        Assert.DoesNotContain(m.Paths, p => p.Path.Contains('\n'));
    }

    // ---- Recordings: only the subfolder, never the Downloads parent. ----

    [Fact]
    public void Recordings_OnlyTheSubfolder_NeverDownloadsItself()
    {
        var env = ValidEnv();
        var downloads = Path.Combine(Path.GetFullPath(env.Home!), "Downloads");

        var m = UninstallManifestBuilder.Build(env, removeBinary: false);

        Assert.DoesNotContain(m.Paths, p => TrimSep(p.Path).Equals(TrimSep(downloads), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(m.Paths, p => Path.GetFileName(TrimSep(p.Path)) == "Zeus Recordings");
    }

    // ---- Reparse-point entries are refused. ----

    [Fact]
    public void ReparsePointEntry_IsSkipped()
    {
        var env = ValidEnv();
        // Mark the DataDir as a reparse point → essential entry fails → whole abort.
        var dataDir = HostOs() switch
        {
            OsKind.Windows => Path.Combine(env.LocalAppData!, "Zeus"),
            OsKind.MacOs => Path.Combine(env.Home!, "Library", "Application Support", "Zeus"),
            _ => Path.Combine(env.Home!, ".local", "share", "Zeus"),
        };
        env.ReparsePoints.Add(Path.GetFullPath(dataDir));

        var m = UninstallManifestBuilder.Build(env, removeBinary: false);

        Assert.False(m.Ok); // DataDir is essential; a symlinked DataDir aborts the wipe
    }

    private static string TrimSep(string p) =>
        p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}

// Helper-generation safety: the detached executor must pass paths as DATA, never
// interpolate them into the script, and use a bounded wait + liveness re-check.
public sealed class UninstallExecutorTests
{
    private static UninstallManifest ValidManifest()
    {
        var p1 = Path.Combine(Path.GetTempPath(), "zeus-x", "Zeus");
        var p2 = Path.Combine(Path.GetTempPath(), "zeus-x", "plugins");
        return new UninstallManifest(
            [new UninstallPath(p1, EntryKind.Directory), new UninstallPath(p2, EntryKind.Directory)],
            [], null, false, [], []);
    }

    [Fact]
    public void BuildPlan_PassesPathsAsData_NotInArgv()
    {
        var m = ValidManifest();
        var plan = UninstallExecutor.BuildPlan(m, parentPid: 4242, dryRun: true);

        // The shell binary + a FIXED script constant; nothing else (no path) in argv.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.EndsWith("powershell.exe", plan.StartInfo.FileName);
            Assert.Contains(UninstallExecutor.WindowsScript, plan.StartInfo.ArgumentList);
        }
        else
        {
            Assert.Equal("/bin/sh", plan.StartInfo.FileName);
            Assert.Equal(["-c", UninstallExecutor.PosixScript], plan.StartInfo.ArgumentList);
        }

        // No delete path may appear anywhere in argv — they live only in the data file.
        foreach (var arg in plan.StartInfo.ArgumentList)
            foreach (var p in m.Paths)
                Assert.DoesNotContain(p.Path, arg);

        // Paths ARE present in the manifest bytes (NUL-delimited).
        var data = System.Text.Encoding.UTF8.GetString(plan.ManifestBytes);
        foreach (var p in m.Paths)
            Assert.Contains(p.Path, data);

        // Dynamic values travel via environment, not the script text.
        Assert.Equal("4242", plan.StartInfo.Environment[UninstallExecutor.EnvPid]);
        Assert.Equal(plan.ManifestPath, plan.StartInfo.Environment[UninstallExecutor.EnvManifest]);
        Assert.Equal("1", plan.StartInfo.Environment[UninstallExecutor.EnvDryRun]);
    }

    [Fact]
    public void Scripts_WaitBounded_AndReCheckLiveness()
    {
        // Both fixed scripts must bound the wait and ABORT if the parent is still
        // alive after the loop (never fall through to deletion).
        Assert.Contains("ABORT parent still alive", UninstallExecutor.PosixScript);
        Assert.Contains("ABORT parent still alive", UninstallExecutor.WindowsScript);
        Assert.Contains("240", UninstallExecutor.PosixScript);   // bounded loop
        Assert.Contains("240", UninstallExecutor.WindowsScript);
    }

    [Fact]
    public void BuildPlan_ThrowsOnAbortedManifest()
    {
        var aborted = UninstallManifest.Aborted("nope");
        Assert.Throws<InvalidOperationException>(() => UninstallExecutor.BuildPlan(aborted, 1, false));
    }
}
