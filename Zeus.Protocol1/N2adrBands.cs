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

namespace Zeus.Protocol1;

/// <summary>
/// N2ADR 7-relay filter board band-to-OC-pin lookup. Returns the raw pin
/// mask (bits 0..6 = pins 1..7) for a given VFO-A frequency. The caller is
/// responsible for shifting into its final wire location — the mask is written
/// as <c>output_buffer[C2] |= rxband-&gt;OCrx &lt;&lt; 1</c>.
///
/// Masks match the Thetis "HERMESLITE" defaults at setup.cs:14655-14699
/// (line numbers from OpenHPSDR-Thetis commit on bek's machine 2026-04-15).
/// The N2ADR board has 6 distinct LPFs sharing pin 7 as a common enable:
/// <list type="bullet">
/// <item>160m: pin 1 (alone, no pin 7)</item>
/// <item>80m:  pins 2 + 7</item>
/// <item>60m + 40m share pin 3 + 7</item>
/// <item>30m + 20m share pin 4 + 7</item>
/// <item>17m + 15m share pin 5 + 7</item>
/// <item>12m + 10m share pin 6 + 7</item>
/// </list>
/// See docs/prd/09-n2adr-bands.md for the full band-edge rationale.
/// </summary>
public static class N2adrBands
{
    /// <summary>
    /// Compute the N2ADR RX OC pin mask for <paramref name="vfoHz"/>.
    /// Returns 0 for frequencies outside 160m..10m (no LPF engaged).
    /// </summary>
    public static byte RxOcMask(long vfoHz)
    {
        // Ranges are keyed on "lowest frequency above which this band is the
        // active filter", matching the Thetis band table's lower edges. The
        // gap between band edges (e.g. 4.0-5.3 MHz) falls into the previous
        // band; that matches how real SDRs behave when the user parks between
        // ham bands, and keeps the nearest useful LPF engaged.
        return vfoHz switch
        {
            <   1_800_000 => (byte)0x00,   // below 160m — no filter
            <   3_500_000 => (byte)0x01,   // 160m: pin 1
            <   5_300_000 => (byte)0x42,   // 80m:  pins 2+7
            <   7_000_000 => (byte)0x44,   // 60m:  pins 3+7
            <  10_100_000 => (byte)0x44,   // 40m:  pins 3+7
            <  14_000_000 => (byte)0x48,   // 30m:  pins 4+7
            <  18_068_000 => (byte)0x48,   // 20m:  pins 4+7
            <  21_000_000 => (byte)0x50,   // 17m:  pins 5+7
            <  24_890_000 => (byte)0x50,   // 15m:  pins 5+7
            <  28_000_000 => (byte)0x60,   // 12m:  pins 6+7
            <= 29_700_000 => (byte)0x60,   // 10m:  pins 6+7
            _             => (byte)0x00,   // 6m and up — no LPF on N2ADR
        };
    }

    /// <summary>
    /// Same lookup keyed on the canonical band name used by per-band stores
    /// (PaSettingsStore / BandUtils.HfBands). Used by the PA Settings panel
    /// to surface the auto-mask alongside the operator-editable OC TX/RX
    /// fields, so the operator can see what pins the N2ADR LPF table is
    /// already driving for a given band. Unknown band returns 0.
    /// </summary>
    public static byte RxOcMaskForBand(string bandName) => bandName switch
    {
        "160m" => (byte)0x01,
        "80m"  => (byte)0x42,
        "60m"  => (byte)0x44,
        "40m"  => (byte)0x44,
        "30m"  => (byte)0x48,
        "20m"  => (byte)0x48,
        "17m"  => (byte)0x50,
        "15m"  => (byte)0x50,
        "12m"  => (byte)0x60,
        "10m"  => (byte)0x60,
        _      => (byte)0x00,
    };
}
