// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Zeus.Support.Contracts;

namespace Zeus.Server;

/// <summary>
/// Backend-side touchpoints for the out-of-process support sidecar. Phase 1 only
/// needs the clean-exit handshake: the sidecar treats the backend's death as a
/// crash UNLESS it finds a per-PID marker the backend wrote at a deliberate exit.
/// Call <see cref="MarkCleanExit"/> on every intentional shutdown path (window
/// close, operator quit, profile-switch relaunch) so a normal exit is never
/// mis-reported as a crash.
/// </summary>
public static class SupportSidecar
{
    /// <summary>
    /// Drop the clean-exit marker for THIS process so a supervising sidecar
    /// classifies the imminent exit as expected. Best-effort and synchronous — it
    /// must complete before the process exits, and must never throw on a shutdown
    /// path.
    /// </summary>
    public static void MarkCleanExit()
    {
        try
        {
            var dir = PrefsDbPath.CrashDir();
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, SupportPaths.CleanExitMarkerName(Environment.ProcessId));
            File.WriteAllText(path, DateTimeOffset.UtcNow.ToString("o"));
        }
        catch
        {
            // A missing marker only costs a spurious crash record; never let it
            // interfere with shutdown.
        }
    }
}
