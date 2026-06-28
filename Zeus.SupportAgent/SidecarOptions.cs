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
/// <param name="SupervisePid">PID of the Zeus backend to supervise.</param>
/// <param name="SessionToken">Per-session IPC token (pipe name disambiguator).</param>
/// <param name="AppLogPath">Path to the runtime log the crash record tails.</param>
/// <param name="StartupLogPath">Path to the startup log the crash record tails.</param>
/// <param name="CrashDir">Directory for crash records, markers, and the breadcrumb log.</param>
/// <param name="AppVersion">Zeus version string stamped into crash records.</param>
/// <param name="BrokerUrl">
/// Broker base URL for presence/crash POSTs. The host passes the SAME value
/// <see cref="RemoteBrokerClient"/> uses (a <c>wss://…/signal</c> URL or the
/// <c>ZEUS_REMOTE_BROKER_URL</c> override); the sidecar derives the https origin
/// from it via <see cref="BrokerEndpoints"/>. Null disables all remote presence/
/// crash sharing (the Phase-1 local-only behaviour).
/// </param>
/// <param name="OperatorCallsign">
/// The operator's QRZ callsign, resolved by the backend at launch. Required for
/// presence/crash (it is the broker's identity key). Null/blank ⇒ no remote
/// sharing even if a broker URL is set.
/// </param>
/// <param name="RemoteDiagnosticsEnabled">
/// Initial L1 master-switch posture at launch. The live value can change at
/// runtime via a <c>SupportStateChanged</c> IPC; this is only the seed.
/// </param>
/// <param name="AutoShareOnCrash">
/// Initial crash auto-share posture at launch (default OFF). Gates the post-crash
/// upload, which runs AFTER the backend is dead and so cannot consult live IPC —
/// the launch-time value is the authoritative pre-authorisation for that path.
/// </param>
public sealed record SidecarOptions(
    int SupervisePid,
    string? SessionToken,
    string? AppLogPath,
    string? StartupLogPath,
    string CrashDir,
    string? AppVersion,
    string? BrokerUrl = null,
    string? OperatorCallsign = null,
    bool RemoteDiagnosticsEnabled = false,
    bool AutoShareOnCrash = false)
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
        string? brokerUrl = null, operatorCallsign = null;
        bool remoteDiagnostics = false, autoShare = false;

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
                case "--broker-url": brokerUrl = Next(); break;
                case "--operator-callsign": operatorCallsign = Next(); break;
                case "--remote-diagnostics":
                    if (!TryParseBool(Next(), out remoteDiagnostics, out error, key)) return false;
                    break;
                case "--auto-share-crash":
                    if (!TryParseBool(Next(), out autoShare, out error, key)) return false;
                    break;
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

        options = new SidecarOptions(
            pid.Value, session, appLog, startupLog, crashDir, appVersion,
            brokerUrl, operatorCallsign, remoteDiagnostics, autoShare);
        return true;
    }

    // Accept on/off/true/false/1/0 for the boolean opt-in flags.
    private static bool TryParseBool(string? value, out bool result, out string? error, string key)
    {
        result = false;
        error = null;
        switch (value?.Trim().ToLowerInvariant())
        {
            case "on" or "true" or "1" or "yes": result = true; return true;
            case "off" or "false" or "0" or "no" or null or "": result = false; return true;
            default:
                error = $"{key} expects on/off (got '{value}').";
                return false;
        }
    }
}
