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
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.

namespace Zeus.Server;

public static class BandUtils
{
    /// <summary>
    /// Canonical HF band names used by per-band settings stores (PA gain,
    /// band memory, etc.). Case-sensitive — keep them identical everywhere.
    /// </summary>
    public static readonly IReadOnlyList<string> HfBands = new[]
    {
        "160m", "80m", "60m", "40m", "30m", "20m", "17m", "15m", "12m", "10m", "6m",
    };

    /// <summary>
    /// Resolve a VFO frequency (Hz) to the enclosing ham band name. Uses
    /// lowest-frequency-above-edge ranges so a VFO parked between bands
    /// (e.g. 4.0 MHz) still resolves to the nearest-lower band — matches
    /// how N2adrBands.RxOcMask() keeps filters engaged off-band.
    /// Returns null below 160m or above 6m.
    /// </summary>
    public static string? FreqToBand(long vfoHz) => vfoHz switch
    {
        <   1_800_000 => null,
        <   3_500_000 => "160m",
        <   5_300_000 => "80m",
        <   7_000_000 => "60m",
        <  10_100_000 => "40m",
        <  14_000_000 => "30m",
        <  18_068_000 => "20m",
        <  21_000_000 => "17m",
        <  24_890_000 => "15m",
        <  28_000_000 => "12m",
        <  50_000_000 => "10m",
        <  54_000_000 => "6m",
        _             => null,
    };
}
