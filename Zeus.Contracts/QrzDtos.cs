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

public sealed record QrzLoginRequest(string Username, string Password);

public sealed record QrzLookupRequest(string Callsign);

public sealed record QrzSetApiKeyRequest(string? ApiKey);

public sealed record QrzStation(
    string Callsign,
    string? Name,
    string? FirstName,
    string? Country,
    string? State,
    string? City,
    string? Grid,
    double? Lat,
    double? Lon,
    int? Dxcc,
    int? CqZone,
    int? ItuZone,
    string? ImageUrl,
    // ── Extended QRZ XML fields (additive; older clients ignore these) ──────
    // License class ("Extra", "General", "CEPT", …) and codes string.
    string? LicenseClass = null,
    string? LicenseCodes = null,
    // Effective date of the current license grant (QRZ <efdate>, ISO yyyy-MM-dd).
    // NOTE: this is the current-grant date, not necessarily first-licensed —
    // it is the best the QRZ XML feed provides.
    string? LicenseEffectiveDate = null,
    string? LicenseExpiresDate = null,
    string? Email = null,
    // QSL preferences (QRZ flags: 1 = accepts, 0 = does not).
    bool? AcceptsLotw = null,
    bool? AcceptsEqsl = null,
    bool? AcceptsMailQsl = null,
    string? QslManager = null,
    // Time-zone hints for computing the contact's local time.
    double? GmtOffset = null,
    string? TimeZone = null,
    bool? ObservesDst = null,
    // Operator birth year, when public.
    int? Born = null);

public sealed record QrzStatus(
    bool Connected,
    bool HasXmlSubscription,
    QrzStation? Home,
    string? Error,
    bool HasStoredCredentials = false,
    bool HasApiKey = false);

// ── Point-to-point propagation (DE → DX) ──────────────────────────────────
// Sourced from the HamClock sidecar's ITU-R P.533-14 engine (with a built-in
// estimation fallback) via PropagationService. Zeus does not compute the
// physics itself — it proxies, caches, and shapes the result for the UI.

public sealed record PropagationBand(
    string Band,        // e.g. "20m"
    double FreqMhz,     // band centre used by the model
    int Reliability,    // 0..99 % circuit reliability for the current hour
    string Snr,         // e.g. "+12dB"
    string Status);     // GOOD | FAIR | POOR | CLOSED

public sealed record PropagationResult(
    bool Available,                 // false → engine unreachable / not running
    string? Unavailable,            // human-readable reason when !Available
    string Model,                   // "ITU-R P.533-14" or "Built-in estimation"
    double Sfi,
    double Ssn,
    double KIndex,
    double Muf,                     // MUF for the path, MHz
    double Luf,                     // LUF for the path, MHz
    int DistanceKm,
    int CurrentHourUtc,
    IReadOnlyList<PropagationBand> Bands,  // sorted best → worst
    PropagationBand? CurrentBand);  // prediction for the radio's active band, if known
