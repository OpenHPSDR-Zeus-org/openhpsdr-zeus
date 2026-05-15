// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Zeus.Protocol2.Tests;

/// <summary>
/// Per-radio frequency-correction factor (issue #325). Same math as the
/// Protocol-1 path — verifies the Protocol-2 client (which feeds the DUC
/// / DDC NCO phase-word) applies the same multiplicative correction
/// inside <see cref="Protocol2Client.SetVfoAHz"/>. Without this gate,
/// G2 / OrionMkII / Saturn-class radios would calibrate the dial but the
/// NCO phase-word at <c>line 951</c> would still drift.
/// </summary>
public class FrequencyCorrectionTests
{
    private static Protocol2Client NewClient() => new(NullLogger<Protocol2Client>.Instance);

    [Fact]
    public void Factor_One_Means_No_Correction()
    {
        var client = NewClient();
        client.SetFrequencyCorrectionFactor(1.0);
        client.SetVfoAHz(14_250_000);
        Assert.Equal((uint)14_250_000, client.CorrectedRxFreqHzForTesting);
    }

    [Fact]
    public void Default_Factor_Is_One()
    {
        var client = NewClient();
        Assert.Equal(1.0, client.FrequencyCorrectionFactor);

        client.SetVfoAHz(7_100_000);
        Assert.Equal((uint)7_100_000, client.CorrectedRxFreqHzForTesting);
    }

    [Fact]
    public void Plus_One_Ppm_Adds_Ten_Hz_At_Ten_MHz()
    {
        var client = NewClient();
        client.SetFrequencyCorrectionFactor(1.000001);
        client.SetVfoAHz(10_000_000);
        Assert.Equal((uint)10_000_010, client.CorrectedRxFreqHzForTesting);
    }

    [Fact]
    public void Minus_One_Ppm_Subtracts_Ten_Hz_At_Ten_MHz()
    {
        var client = NewClient();
        client.SetFrequencyCorrectionFactor(0.999999);
        client.SetVfoAHz(10_000_000);
        Assert.Equal((uint)9_999_990, client.CorrectedRxFreqHzForTesting);
    }

    [Fact]
    public void Correction_Scales_Linearly_Across_Hf()
    {
        var client = NewClient();
        client.SetFrequencyCorrectionFactor(1.000001); // +1 ppm

        // +1 Hz per MHz at cardinal HF anchors. Inputs chosen so the
        // mathematical product isn't a half-integer — keeps the result
        // robust against double-precision rounding direction.
        client.SetVfoAHz(1_000_000);
        Assert.Equal((uint)1_000_001, client.CorrectedRxFreqHzForTesting);

        client.SetVfoAHz(14_250_000);
        Assert.Equal((uint)14_250_014, client.CorrectedRxFreqHzForTesting);

        client.SetVfoAHz(50_000_000);
        Assert.Equal((uint)50_000_050, client.CorrectedRxFreqHzForTesting);
    }

    [Fact]
    public void Factor_Change_Affects_Next_Tune_Not_Current_Slot()
    {
        var client = NewClient();
        client.SetFrequencyCorrectionFactor(1.0);
        client.SetVfoAHz(14_250_000);
        Assert.Equal((uint)14_250_000, client.CorrectedRxFreqHzForTesting);

        client.SetFrequencyCorrectionFactor(1.000001);
        // _rxFreqHz unchanged — RadioService is responsible for the re-tune.
        Assert.Equal((uint)14_250_000, client.CorrectedRxFreqHzForTesting);

        client.SetVfoAHz(14_250_000);
        Assert.Equal((uint)14_250_014, client.CorrectedRxFreqHzForTesting);
    }
}
