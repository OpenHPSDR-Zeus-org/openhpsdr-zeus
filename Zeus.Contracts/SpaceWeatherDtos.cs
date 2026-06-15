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
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

namespace Zeus.Contracts;

// ── Solar / space-weather snapshot ────────────────────────────────────────
// Sourced from N0NBH (hamqsl.com solarxml) via the HamClock sidecar's
// /api/n0nbh endpoint, proxied by SpaceWeatherService. Values are kept as
// strings because the feed itself is free-form ("No Report", "NoRpt", "B7.2",
// "S0-S1", ""), and the panel formats/colours them client-side.

public sealed record SpaceWeatherBand(
    string Name,        // e.g. "80m-40m"
    string Time,        // "day" | "night"
    string Condition);  // Good | Fair | Poor | Closed

public sealed record SpaceWeatherVhf(
    string Name,        // e.g. "E-Skip", "vhf-aurora"
    string Location,    // e.g. "europe", "north_america"
    string Condition);  // free-form, e.g. "Band Closed", "50MHz ES"

public sealed record SpaceWeatherSnapshot(
    bool Available,
    string? Unavailable,
    string? Source,
    string? Updated,
    long? FetchedAt,
    // solarData.*
    string? SolarFlux,
    string? Sunspots,
    string? AIndex,
    string? KIndex,
    string? KIndexNt,
    string? Xray,
    string? HeliumLine,
    string? ProtonFlux,
    string? ElectronFlux,
    string? Aurora,
    string? Normalization,
    string? LatDegree,
    string? SolarWind,
    string? MagneticField,
    string? Fof2,
    string? MufFactor,
    string? Muf,
    // top-level
    string? GeomagField,
    string? SignalNoise,
    IReadOnlyList<SpaceWeatherBand> BandConditions,
    IReadOnlyList<SpaceWeatherVhf> VhfConditions);
