// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

namespace Zeus.SupportAgent;

/// <summary>
/// Parsed command line for the support sidecar. The desktop host owns the
/// platform data-dir logic, so it resolves every path and hands them in as
/// arguments — the sidecar never re-derives <c>%LOCALAPPDATA%/Zeus</c> on its own
/// (that keeps the ZEUS_PREFS_PATH override and profile rules in one place).
/// </summary>
public sealed record SidecarOptions(
    int SupervisePid,
    string? SessionToken,
    string? AppLogPath,
    string? StartupLogPath,
    string CrashDir,
    string? AppVersion)
{
    /// <summary>
    /// Parse <c>--key value</c> arguments. Only <c>--supervise-pid</c> and
    /// <c>--crash-dir</c> are required; everything else degrades gracefully (a
    /// missing log path just yields an empty tail). Returns false with a message
    /// on a malformed/missing required argument.
    /// </summary>
    public static bool TryParse(string[] args, out SidecarOptions? options, out string? error)
    {
        options = null;
        error = null;

        int? pid = null;
        string? session = null, appLog = null, startupLog = null, crashDir = null, appVersion = null;

        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i];
            string? Next()
            {
                if (i + 1 >= args.Length) return null;
                return args[++i];
            }

            switch (key)
            {
                case "--supervise-pid":
                    var pidStr = Next();
                    if (!int.TryParse(pidStr, out var p) || p <= 0)
                    {
                        error = $"--supervise-pid requires a positive integer (got '{pidStr ?? "<none>"}').";
                        return false;
                    }
                    pid = p;
                    break;
                case "--session": session = Next(); break;
                case "--app-log": appLog = Next(); break;
                case "--startup-log": startupLog = Next(); break;
                case "--crash-dir": crashDir = Next(); break;
                case "--app-version": appVersion = Next(); break;
                default:
                    error = $"Unknown argument '{key}'.";
                    return false;
            }
        }

        if (pid is null)
        {
            error = "--supervise-pid is required.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(crashDir))
        {
            error = "--crash-dir is required.";
            return false;
        }

        options = new SidecarOptions(pid.Value, session, appLog, startupLog, crashDir, appVersion);
        return true;
    }
}
