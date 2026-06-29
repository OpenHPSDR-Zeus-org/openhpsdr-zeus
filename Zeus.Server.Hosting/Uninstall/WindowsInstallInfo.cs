// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Diagnostics;

namespace Zeus.Server.Uninstall;

// Resolves how (or whether) Zeus's own binary can be removed on Windows, by
// reading the installer's Add/Remove-Programs entry via reg.exe (the project
// targets net10.0, not net10.0-windows, so Microsoft.Win32.Registry isn't
// available — and the wipe helper already shells reg.exe, so this stays
// consistent).
//
// FAIL SAFE: binary removal is only offered for a confirmed PER-USER install
// whose uninstaller we can read. A per-machine (HKLM / Program Files) install is
// admin-owned and unremovable by this non-elevated process, so we return null +
// a reason and the caller falls back to a DATA-ONLY wipe — never hand-deleting
// the install dir (which would orphan the Add/Remove-Programs entry).
public static class WindowsInstallInfo
{
    private static readonly string[] PerUserRoots =
    [
        @"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall",
    ];

    private static readonly string[] PerMachineRoots =
    [
        @"HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall",
        @"HKLM\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    ];

    // Returns (command, null) when a per-user uninstaller is found; otherwise
    // (null, reason) → caller does data-only + tells the operator to use Settings.
    public static (string? Command, string? Reason) ResolveUninstaller()
    {
        if (!OperatingSystem.IsWindows())
            return (null, null);

        // A per-machine install present at all means binary removal needs elevation.
        foreach (var root in PerMachineRoots)
        {
            if (FindZeusEntry(root) is { } machine && machine.IsZeus)
                return (null, "Zeus was installed for all users; remove it from Windows Settings → Apps (needs administrator rights).");
        }

        foreach (var root in PerUserRoots)
        {
            if (FindZeusEntry(root) is { } e && e.IsZeus)
            {
                if (e.InstallLocation is { Length: > 0 } loc && IsUnderProgramFiles(loc))
                    return (null, "Zeus is installed under Program Files; remove it from Windows Settings → Apps.");

                var cmd = e.QuietUninstallString ?? AppendSilent(e.UninstallString);
                if (!string.IsNullOrWhiteSpace(cmd))
                    return (cmd, null);
            }
        }

        return (null, "Could not locate Zeus's uninstaller; data was wiped — remove the program folder via Windows Settings → Apps.");
    }

    private sealed record Entry(bool IsZeus, string? QuietUninstallString, string? UninstallString, string? InstallLocation);

    private static Entry? FindZeusEntry(string root)
    {
        var dump = RegQuery(root + " /s");
        if (dump is null) return null;

        string? display = null, quiet = null, uninstall = null, loc = null;
        Entry? best = null;

        void Flush()
        {
            if (display is not null && IsZeusName(display))
                best ??= new Entry(true, quiet, uninstall, loc);
            display = quiet = uninstall = loc = null;
        }

        foreach (var raw in dump.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("HKEY", StringComparison.OrdinalIgnoreCase))
            {
                Flush();
                continue;
            }
            var (name, value) = ParseValue(line);
            if (name is null) continue;
            switch (name.ToLowerInvariant())
            {
                case "displayname": display = value; break;
                case "quietuninstallstring": quiet = value; break;
                case "uninstallstring": uninstall = value; break;
                case "installlocation": loc = value; break;
            }
        }
        Flush();
        return best;
    }

    private static bool IsZeusName(string displayName) =>
        displayName.Contains("OpenHPSDR", StringComparison.OrdinalIgnoreCase)
        || displayName.Contains("Zeus", StringComparison.OrdinalIgnoreCase);

    // reg.exe value line: "    <Name>    REG_SZ    <value>" (3+ spaces / tab between fields).
    private static (string? Name, string? Value) ParseValue(string line)
    {
        var t = line.TrimStart();
        if (t.Length == 0 || t.StartsWith("HKEY", StringComparison.OrdinalIgnoreCase)) return (null, null);
        // Split on the REG_<TYPE> token.
        var marker = FindTypeMarker(t);
        if (marker < 0) return (null, null);
        var name = t[..marker].Trim();
        var afterType = t[marker..];
        var sp = afterType.IndexOf(' ');
        var value = sp >= 0 ? afterType[(sp + 1)..].Trim() : "";
        return (name.Length == 0 ? null : name, value);
    }

    private static int FindTypeMarker(string s)
    {
        foreach (var tok in new[] { "REG_SZ", "REG_EXPAND_SZ", "REG_DWORD", "REG_MULTI_SZ", "REG_QWORD", "REG_BINARY" })
        {
            var idx = s.IndexOf(tok, StringComparison.Ordinal);
            if (idx > 0) return idx;
        }
        return -1;
    }

    private static string? AppendSilent(string? uninstallString)
    {
        if (string.IsNullOrWhiteSpace(uninstallString)) return null;
        // Inno Setup uninstallers accept /VERYSILENT; only add it if not already present.
        return uninstallString.Contains("/VERYSILENT", StringComparison.OrdinalIgnoreCase)
            ? uninstallString
            : uninstallString + " /VERYSILENT /NORESTART";
    }

    private static bool IsUnderProgramFiles(string path)
    {
        foreach (var v in new[] { "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432" })
        {
            var pf = Environment.GetEnvironmentVariable(v);
            if (!string.IsNullOrEmpty(pf)
                && path.StartsWith(pf, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string? RegQuery(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("reg.exe", "query " + args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return stdout;
        }
        catch
        {
            return null;
        }
    }
}
