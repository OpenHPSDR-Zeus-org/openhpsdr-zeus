// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
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

// Broadcast frame carrying the current RX filter state with optional preset
// context. Emitted on every SetFilter call and on mode change.
public sealed record FilterStateFrame(
    int ChannelId,
    RxMode Mode,
    int LowHz,
    int HighHz,
    string? PresetName = null,
    int? PresetIndex = null);

// REST / hub request to set the active filter. PresetName is optional;
// nudges without a preset context (drag edits) omit it and clear the
// active-preset highlight on the frontend.
public sealed record FilterSetRequest(
    int LowHz,
    int HighHz,
    string? PresetName = null,
    int Receiver = 0);

// Request to write a VAR1 or VAR2 slot. Server rejects SlotName values
// outside {VAR1, VAR2} with HTTP 409.
public sealed record FilterPresetWriteRequest(
    RxMode Mode,
    string SlotName,
    int LowHz,
    int HighHz);

// DTO returned by GET /api/filter/presets. Carries the merged Thetis default
// plus any operator VAR1/VAR2 overrides for a given mode.
public sealed record FilterPresetDto(
    string SlotName,
    string Label,
    int LowHz,
    int HighHz,
    bool IsVar);

// Advanced-ribbon pane visibility toggle.
public sealed record FilterAdvancedPaneRequest(bool Open);

// Get favorite filter slots for a mode.
public sealed record FilterFavoriteSlotsResponse(string[] SlotNames);

// Set favorite filter slots for a mode (up to 3).
public sealed record FilterFavoriteSlotsRequest(RxMode Mode, string[] SlotNames);
