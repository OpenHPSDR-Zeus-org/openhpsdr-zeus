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

using Zeus.Server;
using Xunit;

namespace Zeus.Server.Tests;

/// <summary>
/// Sanity-check the HL2 PA-temperature ADC → °C conversion (MCP9700-class
/// sensor) and the protective clamp around it. The numeric reference points
/// come from HL2 community operator data cited in task P2.4:
///   idle 30 °C, 1 W ~44 °C, 5 W ~47-51 °C, gateware trip at 55 °C.
/// Formula: tempC = (3.26 * raw / 4096 - 0.5) * 100 — see
/// <c>TxMetersService.ConvertPaTempAdcToCelsius</c> for provenance.
/// </summary>
public class PaTempConversionTests
{
    // Invert the formula to build raw ADC values that should decode to a
    // known °C: raw = (tempC / 100 + 0.5) * 4096 / 3.26
    private static int RawForTempC(double tempC)
        => (int)Math.Round((tempC / 100.0 + 0.5) * 4096.0 / 3.26);

    [Theory]
    [InlineData(30.0)]  // idle
    [InlineData(44.0)]  // 1 W
    [InlineData(51.0)]  // 5 W
    [InlineData(55.0)]  // HL2 gateware trip threshold
    public void Convert_CommunityReferencePoints_RoundTripWithinTolerance(double expectedC)
    {
        int raw = RawForTempC(expectedC);
        float actualC = TxMetersService.ConvertPaTempAdcToCelsius(raw);

        // Half-degree tolerance — the ADC quantization is ~0.08 °C per LSB
        // at 3.26 V ref, so round-trip drift stays well within ±0.1 °C.
        Assert.InRange(actualC, expectedC - 0.5, expectedC + 0.5);
    }

    [Fact]
    public void Convert_ZeroAdc_ClampsToFloor()
    {
        // A raw reading of 0 decodes mathematically to -50 °C; clamp pins
        // it to -40 so the UI never shows a physically-impossible value
        // on a floating-ADC boot.
        Assert.Equal(-40f, TxMetersService.ConvertPaTempAdcToCelsius(0));
    }

    [Fact]
    public void Convert_MaxAdc_ClampsToCeiling()
    {
        // Full-scale ADC (4095) decodes to ~275 °C; clamp pins it to
        // 125 °C so a disconnected sensor can't light the 55 °C red
        // zone with a wild value.
        Assert.Equal(125f, TxMetersService.ConvertPaTempAdcToCelsius(4095));
    }

    [Fact]
    public void Convert_Monotonic_Between_ClampedReference_Points()
    {
        // Across the operating band (idle → trip) the conversion must be
        // strictly monotonic: a hotter ADC reading must yield a hotter °C.
        int rawIdle = RawForTempC(30);
        int raw1w = RawForTempC(44);
        int raw5w = RawForTempC(51);
        int rawTrip = RawForTempC(55);

        float cIdle = TxMetersService.ConvertPaTempAdcToCelsius(rawIdle);
        float c1w = TxMetersService.ConvertPaTempAdcToCelsius(raw1w);
        float c5w = TxMetersService.ConvertPaTempAdcToCelsius(raw5w);
        float cTrip = TxMetersService.ConvertPaTempAdcToCelsius(rawTrip);

        Assert.True(cIdle < c1w);
        Assert.True(c1w < c5w);
        Assert.True(c5w < cTrip);
    }
}
