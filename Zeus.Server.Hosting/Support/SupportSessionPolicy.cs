// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

namespace Zeus.Server.Hosting.Support;

/// <summary>
/// The read-only access policy for a maintainer support session — deliberately an
/// <b>ALLOWLIST</b>, the inverse of the operator's own remote tunnel (which is a
/// permissive read-write session restrained by a denylist). A support session
/// exists only to debug a crash/bug: it may READ a narrow set of diagnostic
/// surfaces and NOTHING else.
///
/// Hard rules enforced here:
///   1. Only safe, side-effect-free methods (GET / HEAD). Every mutating method
///      (POST/PUT/DELETE/PATCH) is refused — no control, no settings writes, no
///      TX, no PureSignal, ever.
///   2. The path must prefix-match the small <see cref="AllowedReadPrefixes"/>
///      allowlist. Anything not explicitly listed is denied.
///
/// What is intentionally NOT on the allowlist, and why:
///   /api/log            — the ham LOGBOOK (QSO history, ADIF/PII). The diagnostic
///                         application log reaches the maintainer over the session's
///                         dedicated live-"log" data channel, never as logbook PII.
///   /api/qrz, /api/chat — operator identity / messaging.
///   /api/prefs*         — preferences DB (carries the QRZ password + remote verifier).
///   /api/remote*        — the remote-access password store.
///   /api/support*       — the approve/deny surface itself (a session must never be
///                         able to widen its own authorisation).
/// When unsure, deny.
/// </summary>
public static class SupportSessionPolicy
{
    /// <summary>
    /// Read-only diagnostic surfaces a support session may GET. Prefix-matched
    /// (case-insensitive) on the canonical URL path. Kept narrow and reviewable:
    ///   /api/diagnostics  — the diagnostics v2 framework (providers, self-checks,
    ///                       symptoms). The maintainer's primary signal.
    ///   /api/version      — running build/version (is the operator on an old build?).
    ///   /api/capabilities — per-board capability fingerprint (what radio is this?).
    ///   /api/system/update— updater status (available/applied version).
    ///   /api/state        — the live radio state snapshot (mode/VFO/meters context;
    ///                       read-only, no secrets). Invaluable for "their radio shows X".
    /// </summary>
    public static readonly string[] AllowedReadPrefixes =
    {
        "/api/diagnostics",
        "/api/version",
        "/api/capabilities",
        "/api/system/update",
        "/api/state",
    };

    /// <summary>True for safe, side-effect-free methods (GET / HEAD); false for every mutating one.</summary>
    public static bool IsReadOnlyMethod(string? method)
        => !string.IsNullOrEmpty(method) && method.ToUpperInvariant() is "GET" or "HEAD";

    /// <summary>
    /// Whether <paramref name="path"/> is on the read allowlist. Prefix-matched on
    /// the path only — any query string is stripped first so it can't smuggle a
    /// non-allowlisted suffix past the check.
    /// </summary>
    public static bool IsAllowedPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        int q = path.IndexOf('?');
        var p = q >= 0 ? path[..q] : path;
        foreach (var prefix in AllowedReadPrefixes)
            if (p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// Whether a support session may proxy <paramref name="method"/> on
    /// <paramref name="path"/> to the radio's loopback. Fail-closed: any non-GET/HEAD
    /// method, any path off the allowlist, and any malformed input returns false.
    /// </summary>
    /// <param name="method">HTTP method (case-insensitive).</param>
    /// <param name="path">Canonical request path; a query string (if any) is ignored.</param>
    public static bool IsAllowed(string? method, string? path)
        => IsReadOnlyMethod(method) && IsAllowedPath(path);
}
