// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.

using Xunit;

namespace Zeus.Protocol1.Tests;

/// <summary>
/// Per-radio frequency-correction factor (issue #325). The correction is
/// a single multiplicative scalar applied inside
/// <see cref="Protocol1Client.SetFreqs"/> right before the wire-bound
/// <c>_vfoAHz</c> slot. These tests pin down the math against the
/// piHPSDR / Thetis / deskHPSDR reference convention:
///
///   <c>corrected = round(dial * factor)</c>
///
/// where <c>factor = 1 + (Δf / f_ref)</c>. A +1 ppm crystal (factor =
/// 1.000001) makes the radio's NCO run 1 Hz fast at 1 MHz, 10 Hz fast at
/// 10 MHz, etc.; the host-side correction subtracts that drift so the
/// dial reads the truth.
/// </summary>
public class FrequencyCorrectionTests
{
    [Fact]
    public void Factor_One_Means_No_Correction()
    {
        using var client = new Protocol1Client();
        client.SetFrequencyCorrectionFactor(1.0);
        client.SetFreqs(14_250_000, 14_250_000);

        Assert.Equal(14_250_000L, client.SnapshotState().RxFreqAHz);
    }

    [Fact]
    public void Default_Factor_Is_One()
    {
        using var client = new Protocol1Client();
        Assert.Equal(1.0, client.FrequencyCorrectionFactor);

        client.SetFreqs(7_100_000, 7_100_000);
        Assert.Equal(7_100_000L, client.SnapshotState().RxFreqAHz);
    }

    [Fact]
    public void Plus_One_Ppm_Adds_Ten_Hz_At_Ten_MHz()
    {
        using var client = new Protocol1Client();
        client.SetFrequencyCorrectionFactor(1.000001);
        client.SetFreqs(10_000_000, 10_000_000);

        Assert.Equal(10_000_010L, client.SnapshotState().RxFreqAHz);
    }

    [Fact]
    public void Minus_One_Ppm_Subtracts_Ten_Hz_At_Ten_MHz()
    {
        using var client = new Protocol1Client();
        client.SetFrequencyCorrectionFactor(0.999999);
        client.SetFreqs(10_000_000, 10_000_000);

        Assert.Equal(9_999_990L, client.SnapshotState().RxFreqAHz);
    }

    [Fact]
    public void Correction_Scales_Linearly_Across_Hf()
    {
        using var client = new Protocol1Client();
        client.SetFrequencyCorrectionFactor(1.000001); // +1 ppm

        // +1 Hz per MHz at the cardinal HF anchors. Inputs chosen so the
        // mathematical product isn't a half-integer — double-precision
        // multiplication of these gives a result comfortably on one side
        // of the midpoint, so the AwayFromZero rounding direction is not
        // implementation-defined here.
        client.SetFreqs(1_000_000, 1_000_000);
        Assert.Equal(1_000_001L, client.SnapshotState().RxFreqAHz);

        client.SetFreqs(14_250_000, 14_250_000);
        Assert.Equal(14_250_014L, client.SnapshotState().RxFreqAHz);

        client.SetFreqs(50_000_000, 50_000_000);
        Assert.Equal(50_000_050L, client.SnapshotState().RxFreqAHz);
    }

    [Fact]
    public void Factor_Change_Affects_Next_Tune_Not_Current_Slot()
    {
        // The protocol client's SetFrequencyCorrectionFactor does NOT
        // re-apply to the currently-stored VFO — that's RadioService's
        // job (it calls SetVfo with the same dial after the factor
        // change). Confirm here that calling SetFrequencyCorrectionFactor
        // alone is non-destructive.
        using var client = new Protocol1Client();
        client.SetFrequencyCorrectionFactor(1.0);
        client.SetFreqs(14_250_000, 14_250_000);
        Assert.Equal(14_250_000L, client.SnapshotState().RxFreqAHz);

        client.SetFrequencyCorrectionFactor(1.000001);
        // freq slots weren't touched — still the value written under factor=1.
        Assert.Equal(14_250_000L, client.SnapshotState().RxFreqAHz);

        // Re-tune with same dial: now the new factor is applied.
        client.SetFreqs(14_250_000, 14_250_000);
        Assert.Equal(14_250_014L, client.SnapshotState().RxFreqAHz);
    }
}
