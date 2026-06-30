// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

// Lock in the FreeDV band-convention sideband. FreeDV adopted the SSB voice-mode
// convention — LSB below 10 MHz, USB at/above — so every station on a band shares
// one OFDM-carrier orientation; a mismatch mirror-images the spectrum in RF and
// nothing decodes. WDSP has no FreeDv sideband of its own, so
// RadioService.EffectiveEngineMode resolves it from the dial at the engine-apply
// seam (DspPipelineService). RxMode.FreeDv stays the radio's mode everywhere else.
public class FreeDvSidebandConventionTests
{
    [Theory]
    [InlineData(1_800_000, RxMode.LSB)]    // 160 m
    [InlineData(3_573_000, RxMode.LSB)]    // 80 m
    [InlineData(5_249_999, RxMode.LSB)]    // just below 60 m window
    [InlineData(5_330_500, RxMode.USB)]    // 60 m FCC channel 1 — USB-only by regulation
    [InlineData(5_357_000, RxMode.USB)]    // 60 m IARU R1 FreeDV calling — USB-only
    [InlineData(5_403_500, RxMode.USB)]    // 60 m FCC channel 5 — USB-only
    [InlineData(5_450_001, RxMode.LSB)]    // just above 60 m window — back to the 10 MHz rule
    [InlineData(7_177_000, RxMode.LSB)]    // 40 m — the band that bit us
    [InlineData(9_999_999, RxMode.LSB)]    // just below the threshold
    [InlineData(10_000_000, RxMode.USB)]   // exactly 10 MHz → USB
    [InlineData(14_236_000, RxMode.USB)]   // 20 m
    [InlineData(28_330_000, RxMode.USB)]   // 10 m
    public void FreeDv_resolves_sideband_from_dial(long dialHz, RxMode expected)
    {
        Assert.Equal(expected, RadioService.EffectiveEngineMode(RxMode.FreeDv, dialHz));
    }

    [Theory]
    [InlineData(5_330_500)]   // 60 m FCC channel 1
    [InlineData(5_357_000)]   // 60 m IARU R1 FreeDV calling
    [InlineData(5_403_500)]   // 60 m FCC channel 5
    public void FreeDv_on_60m_signs_the_bandpass_positive_for_USB(long dialHz)
    {
        // 60 m is USB-only by regulation (FCC §97.305, Ofcom IR 2002 et al.),
        // so the FreeDV-on-60m demod must orient USB even though the dial is below
        // the 10 MHz threshold. Without the exception, the OFDM carriers mirror to
        // LSB and the operator transmits on a sideband that's not legally allowed.
        var engineMode = RadioService.EffectiveEngineMode(RxMode.FreeDv, dialHz);
        var (low, high) = RadioService.SignedFilterForMode(engineMode, 300, 2700);

        Assert.Equal(RxMode.USB, engineMode);
        Assert.Equal(300, low);
        Assert.Equal(2700, high);
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
    public void Non_FreeDv_modes_pass_through_unchanged(RxMode mode)
    {
        // Every other mode is dial-independent — the convention must never touch it,
        // on either side of the 10 MHz threshold.
        Assert.Equal(mode, RadioService.EffectiveEngineMode(mode, 7_177_000));
        Assert.Equal(mode, RadioService.EffectiveEngineMode(mode, 14_236_000));
    }

    [Fact]
    public void FreeDv_sub_10MHz_signs_the_bandpass_negative_for_LSB()
    {
        // End-to-end: the FreeDV spec passband (300..2700 stored USB-positive) must
        // come out NEGATIVE once resolved through the convention on 40 m, so WDSP
        // keys/demods on the lower sideband like every other 40 m FreeDV station.
        var engineMode = RadioService.EffectiveEngineMode(RxMode.FreeDv, 7_177_000);
        var (low, high) = RadioService.SignedFilterForMode(engineMode, 300, 2700);

        Assert.Equal(RxMode.LSB, engineMode);
        Assert.True(low < 0 && high < 0, "sub-10 MHz FreeDV must sign the passband negative (LSB)");
        Assert.Equal(-2700, low);
        Assert.Equal(-300, high);
    }

    [Fact]
    public void FreeDv_at_or_above_10MHz_keeps_the_bandpass_positive_for_USB()
    {
        // ≥10 MHz is byte-identical to the legacy FreeDv→USB behaviour.
        var engineMode = RadioService.EffectiveEngineMode(RxMode.FreeDv, 14_236_000);
        var (low, high) = RadioService.SignedFilterForMode(engineMode, 300, 2700);

        Assert.Equal(RxMode.USB, engineMode);
        Assert.Equal(300, low);
        Assert.Equal(2700, high);
    }
}
