// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.
//
// OperatorIdentity — the single shared station callsign + Maidenhead grid the
// operator sets once and every reporting/TX surface reads. Before this, identity
// was duplicated across SpottingSettingsStore and FreeDvReporterSettingsStore and
// (on the frontend) lived only in port-scoped localStorage, so FT8 TX stayed
// gated and the desktop lost the call on every restart. This is the server-
// persisted override base; the QRZ home station remains the fallback when blank.

namespace Zeus.Contracts;

/// <summary>
/// The operator's station identity (callsign + Maidenhead grid). This is the
/// shared OVERRIDE consulted first by every operator resolver (FT8/FT4 TX,
/// spotting uploaders, FreeDV Reporter); the QRZ home station is the fallback
/// when a field is blank. Empty (the default) means "no override — use QRZ".
/// </summary>
public sealed record OperatorIdentity(
    string Callsign = "",
    string Grid = "")
{
    public const int MaxGridLength = 6;

    /// <summary>
    /// Trim/normalize so a hand-crafted POST or stale persisted row stays sane:
    /// callsign upper-cased + trimmed, grid trimmed/capped to a Maidenhead-shaped
    /// prefix. Anything that doesn't start letter-letter is dropped so a typo
    /// can't be persisted as a bogus locator.
    /// </summary>
    public OperatorIdentity Normalized() => this with
    {
        Callsign = NormalizeCallsign(Callsign),
        Grid = NormalizeGrid(Grid),
    };

    /// <summary>True when both a callsign and a grid are present.</summary>
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(Callsign) && !string.IsNullOrWhiteSpace(Grid);

    public static string NormalizeCallsign(string? call) =>
        string.IsNullOrWhiteSpace(call) ? "" : call.Trim().ToUpperInvariant();

    public static string NormalizeGrid(string? grid)
    {
        var g = (grid ?? "").Trim();
        if (g.Length == 0) return "";
        if (g.Length > MaxGridLength) g = g[..MaxGridLength];
        // A Maidenhead locator is field/square[/subsquare]: it must start with two
        // letters. Drop anything that doesn't so a typo can't broadcast/persist a
        // bogus location.
        if (g.Length < 2 || !char.IsLetter(g[0]) || !char.IsLetter(g[1])) return "";
        return g.ToUpperInvariant();
    }
}

/// <summary>
/// GET /api/operator response. Carries the operator's saved override AND the
/// effective resolved identity (override first, QRZ home fallback) so the FT8
/// Settings page can show the live values greyed when they come from QRZ.
/// <paramref name="CallsignFromQrz"/> / <paramref name="GridFromQrz"/> are true
/// when the resolved field fell back to the QRZ home station.
/// </summary>
public sealed record OperatorIdentityStatus(
    string Callsign,            // saved override (empty if unset)
    string Grid,                // saved override (empty if unset)
    string ResolvedCallsign,    // effective: override else QRZ home
    string ResolvedGrid,        // effective: override else QRZ home
    bool CallsignFromQrz,
    bool GridFromQrz)
{
    /// <summary>True when a callsign + grid are available from any source.</summary>
    public bool IdentityResolved =>
        !string.IsNullOrWhiteSpace(ResolvedCallsign) &&
        !string.IsNullOrWhiteSpace(ResolvedGrid);
}
