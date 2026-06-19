// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

namespace Zeus.Contracts;

// ── ZeusChat — operator-to-operator chat over a Cloudflare relay ───────────
//
// These DTOs mirror the relay wire protocol (cloud/zeuschat-relay/src/
// protocol.ts) on the Zeus side AND form the JSON envelopes Zeus pushes down
// to its own web clients inside the ChatEvent (0x35) binary frame. All field
// names serialise camelCase on the wire (JsonSerializerDefaults.Web).

/// <summary>
/// A single chat message as broadcast by the relay (and echoed back to the
/// sender for ordering). <paramref name="Ts"/> is epoch milliseconds.
/// </summary>
public sealed record ChatMessage(
    string Id,
    string From,
    string Text,
    long Ts,
    string Room);

/// <summary>
/// A connected operator in the relay roster. <paramref name="FreqHz"/> is the
/// operator's VFO frequency in Hz; <paramref name="Status"/> is "rx"|"tx"|
/// "away"; <paramref name="Since"/> is epoch milliseconds the operator joined.
/// </summary>
public sealed record ChatOperator(
    string Callsign,
    string? Grid,
    long? FreqHz,
    string? Mode,
    string? Status,
    long Since);

/// <summary>
/// Snapshot of the local chat node's state, surfaced via
/// <c>GET /api/chat/status</c> and pushed as a ChatEvent status envelope.
/// <paramref name="Enabled"/> is the persisted opt-in; <paramref name="Connected"/>
/// is whether the relay WebSocket is currently live.
/// </summary>
public sealed record ChatStatusDto(
    bool Enabled,
    bool Connected,
    string? Callsign,
    string RelayUrl,
    string? Error);

// ── REST request/response shapes ──────────────────────────────────────────

public sealed record ChatEnableRequest(bool Enabled);

public sealed record ChatSendRequest(string Text);
