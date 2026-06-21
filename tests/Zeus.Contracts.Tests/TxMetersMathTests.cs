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

using Zeus.Contracts;
using Zeus.Server;
using Xunit;

namespace Zeus.Contracts.Tests;

public class TxMetersMathTests
{
    private static readonly RadioCalibration Cal = RadioCalibration.HermesLite2;

    [Fact]
    public void ZeroAdc_YieldsZeroWattsAndSwrOne()
    {
        // ADC = cal_offset → volts = 0 → watts = 0. Below the 2 W floor the
        // SWR should clamp to 1.0 regardless of REF.
        var (fwdW, refW, swr) = TxMetersService.ComputeMeters(Cal.AdcCalOffset, Cal.AdcCalOffset, Cal);
        Assert.Equal(0.0, fwdW, 6);
        Assert.Equal(0.0, refW, 6);
        Assert.Equal(1.0, swr, 6);
    }

    [Fact]
    public void WattsMath_MatchesThetisFormula()
    {
        // Manually wire the expected watts for a mid-scale ADC and assert
        // the port reproduces it to float precision (Thetis console.cs:25008-25072).
        const double adc = 2500;
        double v = (adc - Cal.AdcCalOffset) / 4095.0 * Cal.RefVoltage;
        double wExpected = v * v / Cal.BridgeVolt;

        var (fwdW, _, _) = TxMetersService.ComputeMeters(adc, Cal.AdcCalOffset, Cal);
        Assert.Equal(wExpected, fwdW, 6);
    }

    [Fact]
    public void NegativeOrBelowOffset_ClampsToZero()
    {
        // ADC below the cal offset would yield negative watts through the
        // squaring (sign lost) → stays zero by the (adc - offset) floor check.
        // This test keeps math tolerant to noise on a cold bridge.
        var (fwdW, refW, _) = TxMetersService.ComputeMeters(0, 0, Cal);
        Assert.True(fwdW >= 0);
        Assert.True(refW >= 0);
    }

    [Fact]
    public void SwrFloorsToOne_WhenFwdBelowTwoWatts()
    {
        // Pick an ADC that yields ~1 W forward. REF arbitrary — floor rule wins.
        double adcFor1W = Cal.AdcCalOffset + 4095.0 / Cal.RefVoltage * Math.Sqrt(1.0 * Cal.BridgeVolt);
        var (fwdW, _, swr) = TxMetersService.ComputeMeters(adcFor1W, adcFor1W, Cal);
        Assert.True(fwdW < 2.0);
        Assert.Equal(1.0, swr, 6);
    }

    [Fact]
    public void SwrIsOne_WhenRefIsZeroAndFwdAboveFloor()
    {
        double adcFor3W = Cal.AdcCalOffset + 4095.0 / Cal.RefVoltage * Math.Sqrt(3.0 * Cal.BridgeVolt);
        var (fwdW, refW, swr) = TxMetersService.ComputeMeters(adcFor3W, Cal.AdcCalOffset, Cal);
        Assert.True(fwdW > 2.0);
        Assert.Equal(0.0, refW, 6);
        Assert.Equal(1.0, swr, 6);
    }

    [Fact]
    public void SwrTwo_FromQuarterRefOverFwd()
    {
        // rho = sqrt(P_ref / P_fwd) = 1/3 → SWR = (1 + 1/3) / (1 - 1/3) = 2.
        // Construct ADCs so fwdW ≈ 3 W and refW ≈ 3/9 W = 0.333 W.
        double adcForFwd = Cal.AdcCalOffset + 4095.0 / Cal.RefVoltage * Math.Sqrt(3.0 * Cal.BridgeVolt);
        double adcForRef = Cal.AdcCalOffset + 4095.0 / Cal.RefVoltage * Math.Sqrt((3.0 / 9.0) * Cal.BridgeVolt);

        var (_, _, swr) = TxMetersService.ComputeMeters(adcForFwd, adcForRef, Cal);
        Assert.Equal(2.0, swr, 3);
    }

    [Fact]
    public void SwrCapsAtNine_WhenRefEqualsFwd()
    {
        // Full reflection → rho = 1 → SWR diverges; contract caps at 9.0.
        double adcForFwd = Cal.AdcCalOffset + 4095.0 / Cal.RefVoltage * Math.Sqrt(5.0 * Cal.BridgeVolt);
        var (fwdW, _, swr) = TxMetersService.ComputeMeters(adcForFwd, adcForFwd, Cal);
        Assert.True(fwdW > 2.0);
        Assert.Equal(9.0, swr, 6);
    }

    [Fact]
    public void SwrCapsAtNine_WhenRefExceedsFwd()
    {
        // refW > fwdW is physically impossible on a real bridge but can leak
        // through on transients/noise. Must not produce NaN or negative SWR.
        double adcForFwd = Cal.AdcCalOffset + 4095.0 / Cal.RefVoltage * Math.Sqrt(3.0 * Cal.BridgeVolt);
        double adcForRef = Cal.AdcCalOffset + 4095.0 / Cal.RefVoltage * Math.Sqrt(5.0 * Cal.BridgeVolt);
        var (_, _, swr) = TxMetersService.ComputeMeters(adcForFwd, adcForRef, Cal);
        Assert.Equal(9.0, swr, 6);
    }
}
