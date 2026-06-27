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
/// A single, operator-approved authorisation to open ONE read-only support
/// session for a specific maintainer request. Minted locally by the radio the
/// instant the operator clicks "Allow" (<see cref="SupportRequestCoordinator"/>)
/// and consumed once by <see cref="SupportWebRtcService"/> when the matching
/// WebRTC offer arrives.
///
/// This is the ADR-0008 end-to-end authority for a support session: the grant is
/// produced by the operator's own Zeus on a local human decision, never by the
/// broker. A support session can therefore exist only because (1) the maintainer
/// proved an admin credential to the broker AND (2) the operator personally
/// approved THIS request id — two independent gates, the second enforced here at
/// the radio.
/// </summary>
/// <param name="RequestId">The maintainer request this grant authorises (opaque, broker-relayed).</param>
/// <param name="AdminCallsign">The admin callsign the operator approved (display/audit only).</param>
/// <param name="IssuedAt">When the operator approved (monotonic via the store's TimeProvider).</param>
/// <param name="ExpiresAt">Hard deadline; the maintainer must connect before this or re-request.</param>
public sealed record SupportGrant(
    string RequestId,
    string AdminCallsign,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);
