// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Zeus.Server;

// Relaunches the running Zeus process in place. Used by the prefs-database
// (profile) selector: switching the active database only takes effect on the
// next launch (every store reads PrefsDbPath.Get() once at startup), so flipping
// the pointer file is paired with a relaunch.
//
// The trick is that this process cannot start its own replacement and then exit
// without a race — the new process may grab the same loopback port before the
// old one releases it. So we spawn a tiny detached helper (cmd / sh) that WAITS
// for this PID to die, THEN starts a fresh copy with the same args, and only
// after that do we exit. Environment.Exit is used deliberately — it tears down
// the Photino window cleanly without marshaling to the UI thread (see Program.cs
// notes on why ProcessExit-time window.Close() deadlocks WebView2 on Windows).
public sealed class AppRestartService
{
    // Optional hook invoked just before this process exits (e.g. to flush state
    // or stop a sidecar). Best-effort — failures are swallowed.
    public Action? OnRestartRequested { get; set; }

    public void RequestRestart()
    {
        var exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Environment.ProcessPath is null; cannot relaunch.");
        var pid = Environment.ProcessId;

        // Original args minus the executable path at [0], so the relaunch keeps
        // the same mode flags (--desktop / --server / none).
        var argv = Environment.GetCommandLineArgs();
        var args = argv.Skip(1).ToArray();

        var psi = BuildRelauncher(exe, pid, args);
        Process.Start(psi);

        try
        {
            OnRestartRequested?.Invoke();
        }
        catch
        {
            // Never block the relaunch on a best-effort hook.
        }

        // Let the in-flight HTTP response flush before the process dies.
        _ = Task.Run(async () =>
        {
            await Task.Delay(300).ConfigureAwait(false);
            Environment.Exit(0);
        });
    }

    private static ProcessStartInfo BuildRelauncher(string exe, int pid, string[] args)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Poll tasklist until the PID is gone (up to ~120 × ~1s), then start a
            // detached copy. `ping -n 2 127.0.0.1` is the classic batch sleep (~1s).
            // %i is doubled to %%i because the loop body runs inside `cmd /c`.
            var pidStr = pid.ToString();
            var sb = new StringBuilder();
            sb.Append("/c ");
            sb.Append("for /l %%i in (1,1,120) do (");
            sb.Append("tasklist /fi \"PID eq ").Append(pidStr).Append("\" | findstr ").Append(pidStr).Append(" >nul || goto go");
            sb.Append(") & ping -n 2 127.0.0.1 >nul & ");
            sb.Append(":go ");
            sb.Append("start \"Zeus\" \"").Append(exe).Append('"');
            foreach (var a in args)
            {
                sb.Append(" \"").Append(a).Append('"');
            }

            return new ProcessStartInfo("cmd.exe", sb.ToString())
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            };
        }

        // POSIX: wait for the PID to die, then relaunch in the background.
        var shArgs = new StringBuilder();
        shArgs.Append("while kill -0 ").Append(pid).Append(" 2>/dev/null; do sleep 0.5; done; ");
        shArgs.Append('"').Append(exe).Append('"');
        foreach (var a in args)
        {
            shArgs.Append(" \"").Append(a).Append('"');
        }
        shArgs.Append(" &");

        return new ProcessStartInfo("/bin/sh", "-c \"" + shArgs.ToString().Replace("\"", "\\\"") + "\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        };
    }
}
