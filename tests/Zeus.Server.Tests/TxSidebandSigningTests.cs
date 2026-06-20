// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.

using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

// Lock in the TX-sideband signing invariant. WDSP selects the SSB sideband from
// the SIGN of the TXA bandpass edges (negative = LSB-family, positive =
// USB-family), so RadioService.SignedFilterForMode is the single source of truth
// that turns operator-typed magnitudes into the correct signed pair. The bug
// these tests guard against: a state that comes up with Mode=LSB but a positive
// (USB) TX bandpass — from a legacy prefs DB or a mode writer that forgot to
// re-sign — transmits on the wrong sideband while the mode readout still says
// LSB. DspPipelineService now re-derives the sign from the live mode at the
// engine-apply seam; that path leans entirely on this mapping.
public class TxSidebandSigningTests
{
    [Theory]
    [InlineData(RxMode.USB, +150, +2850)]
    [InlineData(RxMode.CWU, +150, +2850)]
    [InlineData(RxMode.LSB, -2850, -150)]
    [InlineData(RxMode.CWL, -2850, -150)]
    [InlineData(RxMode.DIGU, 0, +2850)]
    [InlineData(RxMode.DIGL, -2850, 0)]
    [InlineData(RxMode.AM, -2850, +2850)]
    [InlineData(RxMode.SAM, -2850, +2850)]
    [InlineData(RxMode.DSB, -2850, +2850)]
    [InlineData(RxMode.FM, -2850, +2850)]
    public void SignedFilterForMode_signs_per_sideband(RxMode mode, int expectedLow, int expectedHigh)
    {
        var (low, high) = RadioService.SignedFilterForMode(mode, 150, 2850);

        Assert.Equal(expectedLow, low);
        Assert.Equal(expectedHigh, high);
    }

    [Fact]
    public void LSB_never_yields_a_positive_USB_passband()
    {
        // The core regression: 150..2850 typed as positive magnitudes must come
        // out NEGATIVE in LSB so WDSP keys up on the lower sideband.
        var (low, high) = RadioService.SignedFilterForMode(RxMode.LSB, 150, 2850);

        Assert.True(low < 0, "LSB low edge must be negative");
        Assert.True(high < 0, "LSB high edge must be negative");
    }

    [Theory]
    [InlineData(RxMode.USB)]
    [InlineData(RxMode.LSB)]
    [InlineData(RxMode.DIGU)]
    [InlineData(RxMode.DIGL)]
    [InlineData(RxMode.AM)]
    [InlineData(RxMode.FM)]
    [InlineData(RxMode.CWU)]
    [InlineData(RxMode.CWL)]
    public void Re_signing_from_magnitudes_is_idempotent(RxMode mode)
    {
        // The engine-apply seam re-signs every tick from the absolute magnitudes
        // of the stored pair. That must reproduce the same signed pair for a
        // well-formed state, otherwise it would fight the operator's width.
        var (low, high) = RadioService.SignedFilterForMode(mode, 150, 2850);

        int loAbs = System.Math.Min(System.Math.Abs(low), System.Math.Abs(high));
        int hiAbs = System.Math.Max(System.Math.Abs(low), System.Math.Abs(high));
        var (low2, high2) = RadioService.SignedFilterForMode(mode, loAbs, hiAbs);

        Assert.Equal(low, low2);
        Assert.Equal(high, high2);
    }
}
