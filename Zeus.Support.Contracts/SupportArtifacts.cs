// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

namespace Zeus.Support.Contracts;

/// <summary>
/// On-disk filename conventions shared by the backend (writer) and the support
/// sidecar (reader). Kept here — dependency-free — so the two processes can never
/// disagree on a name. The directories themselves are resolved by the backend
/// (which owns the platform data-dir logic) and passed to the sidecar as launch
/// arguments, so this type holds only the pure, path-free filename rules.
/// </summary>
public static class SupportPaths
{
    /// <summary>
    /// Marker the backend writes at a DELIBERATE exit (window close, operator
    /// quit, profile-switch relaunch). The sidecar supervising that PID looks for
    /// this file when the process dies: present ⇒ clean shutdown (delete it, exit
    /// quietly); absent ⇒ the process died unexpectedly ⇒ capture a crash record.
    /// Keyed by PID so a relaunch's fresh backend can never have its marker read
    /// by the previous generation's sidecar.
    /// </summary>
    public static string CleanExitMarkerName(int pid) => $"clean-exit-{pid}.marker";

    /// <summary>Filename for a captured crash record (sortable by time, disambiguated by PID).</summary>
    public static string CrashRecordFileName(long detectedUnixMs, int pid) =>
        $"crash-{detectedUnixMs:D13}-{pid}.json";
}

/// <summary>
/// A captured backend-crash record, written by the sidecar to the crash directory
/// when the supervised Zeus process dies without a clean-exit marker. This is the
/// artifact a maintainer ultimately wants for a "random crash" report: the exit
/// code plus the tail of the runtime and startup logs as they stood at death.
///
/// Persisted as JSON via <see cref="SupportIpcJsonContext"/>. Evolve by ADDING
/// optional fields and bumping <see cref="SchemaVersion"/>.
/// </summary>
public sealed record SupportCrashRecord(
    int SchemaVersion,
    long DetectedUnixMs,
    int Pid,
    int? ExitCode,
    bool Crashed,
    string? AppVersion,
    string Platform,
    IReadOnlyList<string> AppLogTail,
    IReadOnlyList<string> StartupLogTail,
    string? Note)
{
    /// <summary>Current schema version for newly written crash records.</summary>
    public const int CurrentSchemaVersion = 1;
}
