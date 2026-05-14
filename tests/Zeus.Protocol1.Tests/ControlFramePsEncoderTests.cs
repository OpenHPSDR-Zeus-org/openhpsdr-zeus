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

using Zeus.Contracts;
using Zeus.Protocol1.Discovery;

namespace Zeus.Protocol1.Tests;

/// <summary>
/// Pin the on-the-wire encoding of the HL2 PureSignal register frames so the
/// PR #119 encoding bugs (PS bit in C3 instead of C2; predistortion value
/// shifted into C2 [7:4] instead of [3:0]) can never silently regress.
///
/// References:
///   - mi0bot networkproto1.c:1102 — HL2 0x0a write loop case 11.
///   - HL2 protocol doc, "PureSignal feedback path" section.
///   - PR #119 review: https://github.com/Kb2uka/openhpsdr-zeus/pull/119
/// </summary>
public class ControlFramePsEncoderTests
{
    private static ControlFrame.CcState BaseHl2(bool psEnabled = false) => new(
        VfoAHz: 14_200_000,
        Rate: HpsdrSampleRate.Rate48k,
        PreampOn: false,
        Atten: HpsdrAtten.Zero,
        RxAntenna: HpsdrAntenna.Ant1,
        Mox: false,
        EnableHl2BandVolts: false,
        Board: HpsdrBoardKind.HermesLite2,
        PsEnabled: psEnabled);

    // ---- 0x0a (Attenuator wire byte, == register 0x0a) — puresignal_run bit ----

    [Fact]
    public void Attenuator_Hl2_PsEnabled_Sets_C2_Bit6_Not_C3()
    {
        Span<byte> cc = stackalloc byte[5];
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.Attenuator, BaseHl2(psEnabled: true));

        // PR #119 bug: it set bit 6 of C3 (= reg bit 14) instead of bit 6 of
        // C2 (= reg bit 22). Pin both.
        Assert.Equal(1 << 6, cc[2] & (1 << 6));            // C2 bit 6 = puresignal_run
        Assert.Equal(0,      cc[3] & (1 << 6));            // C3 bit 6 = NOT puresignal_run
    }

    [Fact]
    public void Attenuator_Hl2_PsDisabled_Does_Not_Set_C2_Bit6()
    {
        Span<byte> cc = stackalloc byte[5];
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.Attenuator, BaseHl2(psEnabled: false));
        Assert.Equal(0, cc[2] & (1 << 6));
    }

    [Fact]
    public void Attenuator_NonHl2_PsEnabled_Does_Not_Set_C2_Bit6()
    {
        // Bare HPSDR / ANAN-class radios use a different PS path (Protocol 2
        // ALEX_PS_BIT). Even with PsEnabled=true on a Hermes board, the
        // C0=0x14 frame must not flip its C2 bit 6 — it would land on a
        // reserved bit and could confuse the gateware.
        Span<byte> cc = stackalloc byte[5];
        var s = BaseHl2(psEnabled: true) with { Board = HpsdrBoardKind.Hermes };
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.Attenuator, s);
        Assert.Equal(0, cc[2] & (1 << 6));
    }

    [Fact]
    public void Attenuator_Hl2_PsEnabled_Preserves_C4_Attenuator_Byte()
    {
        // Adding the PS bit must not corrupt the existing C4 step-attenuator
        // payload — the radio reads both fields out of the same C0=0x14
        // frame.
        Span<byte> cc = stackalloc byte[5];
        var s = BaseHl2(psEnabled: true) with { Atten = new HpsdrAtten(20) };
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.Attenuator, s);
        // HL2 atten encoding: 0x40 | (60 - 20) = 0x68.
        Assert.Equal(0x68, cc[4]);
    }

    [Fact]
    public void Attenuator_Cc0_WireByte_Is_0x14()
    {
        // Address C0 wire byte for register 0x0a = 0x0a << 1 = 0x14. MOX bit
        // OR'd into bit 0; pin both states.
        Span<byte> cc = stackalloc byte[5];
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.Attenuator, BaseHl2(psEnabled: true));
        Assert.Equal(0x14, cc[0]);

        var moxOn = BaseHl2(psEnabled: true) with { Mox = true };
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.Attenuator, moxOn);
        Assert.Equal(0x15, cc[0]);
    }

    // ---- 0x2b (Predistortion) — value/subindex placement ----

    [Fact]
    public void Predistortion_Cc0_WireByte_Is_0x56()
    {
        // 0x2b << 1 = 0x56. PR #119 had the right wire byte, only the
        // payload was wrong.
        Span<byte> cc = stackalloc byte[5];
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.Predistortion, BaseHl2());
        Assert.Equal(0x56, cc[0]);
    }

    [Fact]
    public void Predistortion_Subindex_Lands_In_C1_Whole_Byte()
    {
        // bits [31:24] = subindex → C1 (whole byte).
        Span<byte> cc = stackalloc byte[5];
        var s = BaseHl2() with { PsPredistortionSubindex = 0xA5 };
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.Predistortion, s);
        Assert.Equal(0xA5, cc[1]);
    }

    [Theory]
    // PR #119 bug: it wrote `c14[1] = (value & 0x0F) << 4`, which puts the
    // value in C2 bits [7:4] = register bits [23:20] (reserved) instead of
    // C2 bits [3:0] = register bits [19:16] (the real PS-value field).
    [InlineData(0x00, 0x00)]
    [InlineData(0x01, 0x01)]
    [InlineData(0x05, 0x05)]
    [InlineData(0x0F, 0x0F)]
    // Higher bits must be masked off — the field is only 4 bits wide.
    [InlineData(0xFF, 0x0F)]
    [InlineData(0xF0, 0x00)]
    public void Predistortion_Value_Lands_In_C2_LowNibble_Not_HighNibble(byte value, byte expectedC2)
    {
        Span<byte> cc = stackalloc byte[5];
        var s = BaseHl2() with { PsPredistortionValue = value };
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.Predistortion, s);
        Assert.Equal(expectedC2, cc[2]);
    }

    [Fact]
    public void Predistortion_C3_C4_Reserved_Zero()
    {
        // No HL2-defined fields in C3 or C4 of register 0x2b — keep them
        // zero so we don't accidentally write into a reserved-but-allocated
        // future bit.
        Span<byte> cc = stackalloc byte[5];
        var s = BaseHl2() with { PsPredistortionSubindex = 0xFF, PsPredistortionValue = 0x0F };
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.Predistortion, s);
        Assert.Equal(0, cc[3]);
        Assert.Equal(0, cc[4]);
    }

    // ---- Config (0x00) — number-of-receivers in C4 [5:3] ----

    [Theory]
    [InlineData(0, 0b000)]   // 1 receiver (Zeus default)
    [InlineData(1, 0b001)]   // 2 receivers (HL2 PS armed, paired DDC0/DDC1)
    [InlineData(3, 0b011)]   // 4 receivers (HL2 4-DDC layout)
    [InlineData(7, 0b111)]   // 8 receivers (cap)
    public void Config_NumReceiversMinusOne_Lands_In_C4_Bits5to3(byte nMinus1, byte expectedFieldValue)
    {
        Span<byte> cc = stackalloc byte[5];
        var s = BaseHl2() with { NumReceiversMinusOne = nMinus1 };
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.Config, s);
        Assert.Equal(expectedFieldValue << 3, cc[4] & 0b00111000);
        // Duplex bit (C4[2] = 1) preserved.
        Assert.Equal(1 << 2, cc[4] & (1 << 2));
    }

    [Fact]
    public void Config_NumReceivers_Default_Single_PreservesLegacyBitLayout()
    {
        // With NumReceiversMinusOne defaulting to 0, C4 = duplex bit only =
        // 0b00000100. This is the historic Zeus encoding; pin it so the
        // default RX path doesn't shift.
        Span<byte> cc = stackalloc byte[5];
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.Config, BaseHl2());
        Assert.Equal(0b00000100, cc[4] & 0b11111111);
    }
}
