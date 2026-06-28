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

/// <summary>Outcome of the post-crash auto-share decision (for logging/tests).</summary>
public enum CrashShareOutcome
{
    /// <summary>The auto-share flag was OFF — nothing was uploaded (the default, safe path).</summary>
    SkippedNotOptedIn,
    /// <summary>Opted in but no broker identity was available (no callsign/session/URL).</summary>
    SkippedNoBroker,
    /// <summary>Opted in but the freshly written crash record could not be read back.</summary>
    SkippedNoRecord,
    /// <summary>Uploaded successfully.</summary>
    Uploaded,
    /// <summary>Upload was attempted but the broker rejected it / was unreachable.</summary>
    UploadFailed,
}

/// <summary>
/// Decides whether a freshly captured crash record should be auto-shared to the
/// broker, and performs the upload. This runs AFTER the supervised backend has
/// died, so it cannot consult a live IPC — the authoritative gate is the
/// auto-share posture the host pre-authorised at launch
/// (<see cref="SidecarOptions.AutoShareOnCrash"/>), which defaults to OFF.
///
/// Strict opt-in: with the flag off, <see cref="TryShareAsync"/> returns
/// <see cref="CrashShareOutcome.SkippedNotOptedIn"/> WITHOUT touching the broker,
/// so a crash never leaves the operator's machine unless they explicitly enabled
/// auto-share. The record is already redacted server-side by the backend's
/// diagnostics layer; the sidecar uploads it verbatim.
/// </summary>
public static class CrashAutoShare
{
    /// <summary>
    /// Apply the auto-share gate and, if it passes, upload the crash record JSON.
    /// <paramref name="broker"/> is null when no broker identity/URL is configured
    /// (which yields <see cref="CrashShareOutcome.SkippedNoBroker"/> when opted in).
    /// </summary>
    /// <param name="autoShareEnabled">The launch-time auto-share pre-authorisation.</param>
    /// <param name="crashRecordJson">The crash record JSON to upload, or null/blank if unwritten/unreadable.</param>
    /// <param name="broker">Configured broker client, or null when remote sharing is unavailable.</param>
    public static async Task<CrashShareOutcome> TryShareAsync(
        bool autoShareEnabled,
        string? crashRecordJson,
        ISupportBrokerClient? broker,
        CancellationToken ct)
    {
        // Gate FIRST — never read identity or touch the broker when opted out.
        if (!autoShareEnabled) return CrashShareOutcome.SkippedNotOptedIn;
        if (broker is null) return CrashShareOutcome.SkippedNoBroker;
        if (string.IsNullOrWhiteSpace(crashRecordJson)) return CrashShareOutcome.SkippedNoRecord;

        var ok = await broker.UploadCrashAsync(crashRecordJson, ct).ConfigureAwait(false);
        return ok ? CrashShareOutcome.Uploaded : CrashShareOutcome.UploadFailed;
    }
}
