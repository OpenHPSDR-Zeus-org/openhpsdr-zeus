// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Zeus.Server;
using Zeus.Support.Contracts;

namespace OpenhpsdrZeus;

/// <summary>
/// Spawns the out-of-process support sidecar (Zeus.SupportAgent) from the desktop
/// host. The sidecar is launched DETACHED — a plain child process, not killed
/// when this process dies — so it survives a backend crash and can capture the
/// crash. We hand it every path it needs as arguments (the host owns the
/// platform data-dir logic); it never re-derives them.
///
/// The sidecar is a diagnostics enhancement: any failure to launch is logged and
/// swallowed and MUST never block or slow Zeus startup.
/// </summary>
internal static class SupportSidecarLauncher
{
    public static void TryLaunch()
    {
        try
        {
            var exe = ResolveSidecarExe();
            if (exe is null)
            {
                Console.Error.WriteLine(
                    "support-agent: sidecar executable not found next to host; crash capture disabled this session.");
                return;
            }

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };

            void Add(string key, string? value)
            {
                if (string.IsNullOrEmpty(value)) return;
                psi.ArgumentList.Add(key);
                psi.ArgumentList.Add(value);
            }

            Add("--supervise-pid", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            Add("--session", Guid.NewGuid().ToString("N"));
            Add("--app-log", PrefsDbPath.AppLogPath());
            Add("--startup-log", StartupDiagnostics.LogPath);
            Add("--crash-dir", PrefsDbPath.CrashDir());
            Add("--app-version", ResolveAppVersion());

            Process.Start(psi);
            StartupDiagnostics.Log($"support-agent: launched sidecar to supervise pid {Environment.ProcessId}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"support-agent: launch failed: {ex.Message}");
        }
    }

    private static string? ResolveSidecarExe()
    {
        var name = OperatingSystem.IsWindows() ? "Zeus.SupportAgent.exe" : "Zeus.SupportAgent";
        var path = Path.Combine(AppContext.BaseDirectory, name);
        return File.Exists(path) ? path : null;
    }

    private static string? ResolveAppVersion()
    {
        var asm = Assembly.GetEntryAssembly();
        return asm?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? asm?.GetName().Version?.ToString();
    }
}
