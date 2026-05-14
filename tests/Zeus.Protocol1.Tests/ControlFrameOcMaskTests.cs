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
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.

using Zeus.Contracts;
using Zeus.Protocol1.Discovery;

namespace Zeus.Protocol1.Tests;

// The OC-mask merge in WriteConfigPayload (Config, C2) is the integration seam
// where user-configured PA bits meet the board's existing N2ADR auto-filter
// output. A regression that drops N2ADR bits would silently break HL2 filter
// switching on the wire — worth pinning.
public class ControlFrameOcMaskTests
{
    private static ControlFrame.CcState BaseState(byte userTx = 0, byte userRx = 0, bool mox = false) =>
        new(
            VfoAHz: 7_100_000, // 40m — N2adrBands.RxOcMask returns 0x44 (pins 3+7)
            Rate: HpsdrSampleRate.Rate48k,
            PreampOn: false,
            Atten: HpsdrAtten.Zero,
            RxAntenna: HpsdrAntenna.Ant1,
            Mox: mox,
            EnableHl2BandVolts: false,
            Board: HpsdrBoardKind.HermesLite2,
            HasN2adr: true,
            DriveLevel: 0,
            UserOcTxMask: userTx,
            UserOcRxMask: userRx);

    private static byte WriteConfigC2(ControlFrame.CcState state)
    {
        Span<byte> cc = stackalloc byte[5];
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.Config, state);
        return cc[2]; // C2 — OC pins in bits 1..7
    }

    [Fact]
    public void N2adr_Auto_Mask_Alone_Survives_When_User_Bits_Are_Zero()
    {
        // Pre-Phase-2 behavior: user bits default to 0, so only N2ADR bits flow.
        // 40m → pins 3 + 7 → mask 0x44 → shifted << 1 → 0x88.
        Assert.Equal((byte)0x88, WriteConfigC2(BaseState()));
    }

    [Fact]
    public void User_Tx_Mask_Or_Merges_With_N2adr_When_Mox_On()
    {
        // User picks pin 1 for TX (mask 0x01). While MOX, merge → 0x44 | 0x01 = 0x45 → << 1 = 0x8A.
        var state = BaseState(userTx: 0x01, mox: true);
        Assert.Equal((byte)0x8A, WriteConfigC2(state));
    }

    [Fact]
    public void User_Rx_Mask_Selected_When_Not_Mox()
    {
        // RX mask=0x02, TX mask=0x40 (should NOT appear). MOX off → use RX.
        // 0x44 | 0x02 = 0x46 → << 1 = 0x8C.
        var state = BaseState(userTx: 0x40, userRx: 0x02, mox: false);
        Assert.Equal((byte)0x8C, WriteConfigC2(state));
    }

    [Fact]
    public void User_Bits_Above_7_Are_Masked_Off()
    {
        // Passing 0xFF for TX must not corrupt bit 0 (class-E) or spill past C2.
        var state = BaseState(userTx: 0xFF, mox: true);
        byte c2 = WriteConfigC2(state);
        // bit 0 (class-E) stays 0; bits 1..7 carry 0x44 | 0x7F = 0x7F → << 1 = 0xFE.
        Assert.Equal(0, c2 & 0x01);
        Assert.Equal((byte)0xFE, c2);
    }

    [Fact]
    public void Non_Hl2_Board_Falls_Back_To_User_Only()
    {
        // Hermes without N2ADR → no auto mask. User bits stand alone.
        var state = new ControlFrame.CcState(
            VfoAHz: 7_100_000,
            Rate: HpsdrSampleRate.Rate48k,
            PreampOn: false,
            Atten: HpsdrAtten.Zero,
            RxAntenna: HpsdrAntenna.Ant1,
            Mox: true,
            EnableHl2BandVolts: false,
            Board: HpsdrBoardKind.Hermes,
            HasN2adr: false,
            DriveLevel: 0,
            UserOcTxMask: 0x03);

        Span<byte> cc = stackalloc byte[5];
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.Config, state);
        // Only user bits 0x03 → << 1 = 0x06.
        Assert.Equal((byte)0x06, cc[2]);
    }
}
