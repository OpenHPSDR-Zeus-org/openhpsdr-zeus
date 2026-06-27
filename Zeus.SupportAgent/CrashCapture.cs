// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Runtime.InteropServices;
using System.Text.Json;
using Zeus.Support.Contracts;

namespace Zeus.SupportAgent;

/// <summary>
/// Builds and persists a <see cref="SupportCrashRecord"/> when the supervised
/// backend dies unexpectedly. The record bundles the exit code with the tail of
/// the runtime and startup logs as they stood at death — the artifact a
/// maintainer wants for a "random crash" report.
/// </summary>
public static class CrashCapture
{
    private const int AppLogTailLines = 200;
    private const int StartupLogTailLines = 120;
    private const int MaxRetainedRecords = 25;

    /// <summary>
    /// Assemble a crash record from the current logs. <paramref name="nowUnixMs"/>
    /// is injected so callers (and tests) control the timestamp.
    /// </summary>
    public static SupportCrashRecord BuildRecord(SidecarOptions opts, int? exitCode, long nowUnixMs, string? note = null)
    {
        return new SupportCrashRecord(
            SchemaVersion: SupportCrashRecord.CurrentSchemaVersion,
            DetectedUnixMs: nowUnixMs,
            Pid: opts.SupervisePid,
            ExitCode: exitCode,
            Crashed: true,
            AppVersion: opts.AppVersion,
            Platform: RuntimeInformation.OSDescription,
            AppLogTail: LogTail.ReadAppLogTail(opts.AppLogPath, AppLogTailLines),
            StartupLogTail: LogTail.ReadLastLines(opts.StartupLogPath, StartupLogTailLines),
            Note: note);
    }

    /// <summary>
    /// Build and write a crash record under the crash directory, returning the
    /// written path (or null on failure). Best-effort: a write failure must not
    /// throw — the sidecar's job is to help, never to add its own crash.
    /// </summary>
    public static string? WriteCrashRecord(SidecarOptions opts, int? exitCode, long nowUnixMs, string? note = null)
    {
        try
        {
            Directory.CreateDirectory(opts.CrashDir);
            var record = BuildRecord(opts, exitCode, nowUnixMs, note);
            var json = JsonSerializer.Serialize(record, SupportIpcJsonContext.Default.SupportCrashRecord);

            var path = Path.Combine(opts.CrashDir, SupportPaths.CrashRecordFileName(nowUnixMs, opts.SupervisePid));
            File.WriteAllText(path, json);

            PruneOldRecords(opts.CrashDir);
            return path;
        }
        catch
        {
            return null;
        }
    }

    // Keep the crash directory bounded — newest records win.
    private static void PruneOldRecords(string crashDir)
    {
        try
        {
            var files = Directory.GetFiles(crashDir, "crash-*.json");
            if (files.Length <= MaxRetainedRecords) return;

            foreach (var stale in files
                         .OrderByDescending(f => f, StringComparer.Ordinal) // filename is time-sortable
                         .Skip(MaxRetainedRecords))
            {
                try { File.Delete(stale); } catch { /* best effort */ }
            }
        }
        catch
        {
            // Pruning is housekeeping; never let it surface.
        }
    }
}
