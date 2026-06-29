// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Microsoft.Extensions.Logging;
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

    /// <summary>
    /// The live backend→sidecar IPC bridge (remote-diag P3c), set by
    /// <see cref="Hosting.Support.SupportSidecarBridge"/> when it starts. Null in
    /// host modes that don't launch a sidecar (e.g. headless server / tests), in
    /// which case <see cref="NotifyAvailabilityChanged"/> is a no-op.
    /// </summary>
    internal static Hosting.Support.SupportSidecarBridge? Bridge { get; set; }

    /// <summary>
    /// Tell the out-of-process sidecar that the operator changed an opt-in setting
    /// (the L1 "Remote Diagnostics available" master switch and/or the crash
    /// auto-share sub-toggle). The sidecar owns the broker presence, so it uses
    /// this to register/deregister the radio's availability and to gate crash
    /// sharing — see <see cref="SupportStateChanged"/>.
    ///
    /// <para>The bridge re-reads the authoritative posture (persisted store +
    /// current QRZ identity) when it sends, so the arguments here are advisory for
    /// logging only — there is no stale-snapshot risk. Best-effort and MUST never
    /// throw into the request path.</para>
    /// </summary>
    public static void NotifyAvailabilityChanged(
        bool remoteDiagnosticsEnabled,
        bool autoShareOnCrash,
        string? qrzCallsign = null,
        ILogger? log = null)
    {
        try
        {
            Bridge?.NotifyStateChanged();
            log?.LogInformation(
                "support.sidecar availability change pushed: remoteDiagnostics={Enabled} autoShareOnCrash={AutoShare} bridge={Bridge}",
                remoteDiagnosticsEnabled, autoShareOnCrash, Bridge is null ? "absent" : "live");
        }
        catch
        {
            // Never let a diagnostics-only notification disturb the operator's
            // toggle; the persisted store is already the source of truth.
        }
    }
}
