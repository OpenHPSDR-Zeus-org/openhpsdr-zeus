// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Xunit;
using Zeus.Contracts;

namespace Zeus.Protocol2.Tests;

/// <summary>
/// Per-board, per-sample-rate RX IQ gain correction (issue #171, Brick2).
/// Reference: deskhpsdr <c>src/new_protocol.c:2516-2530</c>.
///
/// Anti-regression discipline: every non-Hermes board AND every non-48 kHz
/// Hermes rate MUST return exactly 1.0. The whole point of the gate is that
/// the correction is invisible to vanilla HL2 / ANAN-G2 / OrionMkII / Saturn
/// operators — if one of these returns anything other than 1.0, somebody's
/// S-meter has just silently shifted on their main rig.
/// </summary>
public class IqGainCorrectionTests
{
    [Fact]
    public void Hermes_At_48Khz_Applies_Minus29dB()
    {
        // 10^(-29/20) ≈ 0.0354813389 — match deskhpsdr literal exactly so
        // a Brick2 user comparing S-meter readings across the two clients
        // gets the same number.
        Assert.Equal(0.0354813389, Protocol2Client.IqGainCorrection(HpsdrBoardKind.Hermes, 48));
    }

    [Theory]
    [InlineData(96)]
    [InlineData(192)]
    [InlineData(384)]
    public void Hermes_Above_48Khz_Unaffected(int rateKhz)
    {
        Assert.Equal(1.0, Protocol2Client.IqGainCorrection(HpsdrBoardKind.Hermes, rateKhz));
    }

    [Theory]
    [InlineData(HpsdrBoardKind.Metis)]
    [InlineData(HpsdrBoardKind.HermesII)]
    [InlineData(HpsdrBoardKind.Angelia)]
    [InlineData(HpsdrBoardKind.Orion)]
    [InlineData(HpsdrBoardKind.HermesLite2)]
    [InlineData(HpsdrBoardKind.OrionMkII)]
    [InlineData(HpsdrBoardKind.HermesC10)]
    [InlineData(HpsdrBoardKind.Unknown)]
    public void NonHermes_Boards_At_48Khz_Unaffected(HpsdrBoardKind board)
    {
        Assert.Equal(1.0, Protocol2Client.IqGainCorrection(board, 48));
    }

    [Theory]
    [InlineData(HpsdrBoardKind.HermesLite2, 96)]
    [InlineData(HpsdrBoardKind.OrionMkII, 192)]
    [InlineData(HpsdrBoardKind.HermesC10, 384)]
    [InlineData(HpsdrBoardKind.Unknown, 48)]
    public void NonHermes_Boards_At_Any_Rate_Unaffected(HpsdrBoardKind board, int rateKhz)
    {
        Assert.Equal(1.0, Protocol2Client.IqGainCorrection(board, rateKhz));
    }

    [Theory]
    [InlineData(48)]
    [InlineData(96)]
    [InlineData(192)]
    [InlineData(384)]
    public void HermesII_Never_Applies_Scaler(int rateKhz)
    {
        // ANAN-10E / ANAN-100B firmware (wire byte 0x02) shares the
        // single-ADC DDC0 wire shape with Hermes/Brick2 but does NOT exhibit
        // the +29 dB lift at 48 kHz — deskhpsdr only gates the scaler on
        // NEW_DEVICE_HERMES (0x01). Widening it to HermesII would knock
        // 29 dB off legitimate ANAN-10E / ANAN-100B RX. Pin to 1.0 at every
        // P2 sample rate so a future refactor can't silently drift the gate.
        Assert.Equal(1.0, Protocol2Client.IqGainCorrection(HpsdrBoardKind.HermesII, rateKhz));
    }
}
