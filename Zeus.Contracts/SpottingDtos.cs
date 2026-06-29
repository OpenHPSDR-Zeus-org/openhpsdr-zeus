// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

namespace Zeus.Contracts;

/// <summary>
/// Persisted config for the digital-mode spotting uploaders. When enabled, FT8/
/// FT4 decodes are uploaded to PSK Reporter (report.pskreporter.info) and/or WSPR
/// spots to WSPRnet (wsprnet.org). Both are NEW network egress and DISABLED by
/// default — they only run when the operator explicitly opts in AND a callsign +
/// Maidenhead grid are available (from this override, or the QRZ home station).
/// This is RX-spot push only; it never controls the radio or transmits.
/// </summary>
public sealed record SpottingRuntimeConfig(
    bool PskReporterEnabled = false,
    bool WsprnetEnabled = false,
    string Callsign = "",
    string Grid = "");

/// <summary>
/// Status/config view for the spotting uploaders. UDP/HTTP egress has no
/// listener, so config applies live — there is no RequiresRestart.
/// <paramref name="IdentityResolved"/> is true when a callsign + grid are
/// available (override or QRZ home), so the panel can warn when they are missing.
/// </summary>
public sealed record SpottingStatus(
    bool PskReporterEnabled,
    bool WsprnetEnabled,
    string Callsign,
    string Grid,
    bool IdentityResolved);
