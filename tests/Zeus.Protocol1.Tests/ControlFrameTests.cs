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

using System.Buffers.Binary;
using Zeus.Contracts;
using Zeus.Protocol1.Discovery;

namespace Zeus.Protocol1.Tests;

public class ControlFrameTests
{
    private static ControlFrame.CcState BaseState() => new(
        VfoAHz: 14_200_000,
        Rate: HpsdrSampleRate.Rate48k,
        PreampOn: false,
        Atten: HpsdrAtten.Zero,
        RxAntenna: HpsdrAntenna.Ant1,
        Mox: false,
        EnableHl2BandVolts: false,
        Board: HpsdrBoardKind.HermesLite2);

    [Fact]
    public void ControlFrame_VfoA_At_14_200_000_EncodesBigEndianFreq()
    {
        Span<byte> cc = stackalloc byte[5];
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.RxFreq, BaseState());

        // CC0 = RxFreq wire byte (0x04) with MOX bit 0 clear. Wire encoding is
        // output_buffer[C0] = 0x04 + (current_rx * 2) — no shift.
        Assert.Equal(0x04, cc[0]);

        // CC1..CC4 = big-endian 14_200_000 = 0x00D8ACC0
        Assert.Equal(0x00, cc[1]);
        Assert.Equal(0xD8, cc[2]);
        Assert.Equal(0xAC, cc[3]);
        Assert.Equal(0xC0, cc[4]);

        uint decoded = BinaryPrimitives.ReadUInt32BigEndian(cc[1..5]);
        Assert.Equal(14_200_000u, decoded);
    }

    [Fact]
    public void ControlFrame_SampleRate_192k_SetsC1Bits()
    {
        Span<byte> cc = stackalloc byte[5];
        var s = BaseState() with { Rate = HpsdrSampleRate.Rate192k };
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.Config, s);

        // CC0 = Config register wire byte (0x00), MOX off.
        Assert.Equal(0x00, cc[0]);
        // C1 low 2 bits = 0b10 for 192k (doc 02 §6)
        Assert.Equal(0b10, cc[1] & 0b11);
    }

    [Theory]
    // CcRegister values are the wire-byte encodings already (pre-shifted
    // address with bit 0 reserved for MOX). Pinned here to catch any future
    // drift that reintroduces a spurious shift.
    [InlineData(ControlFrame.CcRegister.Config, false, 0x00)]
    [InlineData(ControlFrame.CcRegister.Config, true, 0x01)]
    [InlineData(ControlFrame.CcRegister.TxFreq, false, 0x02)]
    [InlineData(ControlFrame.CcRegister.TxFreq, true, 0x03)]
    [InlineData(ControlFrame.CcRegister.RxFreq, false, 0x04)]
    [InlineData(ControlFrame.CcRegister.RxFreq, true, 0x05)]
    [InlineData(ControlFrame.CcRegister.DriveFilter, false, 0x12)]
    [InlineData(ControlFrame.CcRegister.DriveFilter, true, 0x13)]
    internal void ControlFrame_Cc0_WireByte_IsAddressPlusMoxBit(
        ControlFrame.CcRegister register, bool mox, byte expectedCc0)
    {
        Span<byte> cc = stackalloc byte[5];
        var s = BaseState() with { Mox = mox };
        ControlFrame.WriteCcBytes(cc, register, s);
        Assert.Equal(expectedCc0, cc[0]);
    }

    [Fact]
    public void ControlFrame_ConfigPayload_DoesNotSetC3AttenuatorBits()
    {
        // The Atlas step attenuator at C3[1:0] is obsolete for every board we
        // target — RX attenuation is driven exclusively via the dedicated
        // Attenuator register (C0=0x14). Pinning this keeps future edits from
        // reintroducing a double-attenuate path.
        Span<byte> cc = stackalloc byte[5];
        var s = BaseState() with { Atten = new HpsdrAtten(20) };
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.Config, s);
        Assert.Equal(0, cc[3] & 0b11);
    }

    [Theory]
    // HL2 (extended firmware gain): C4 = 0x40 | (60 - Db). So Db=0 → 0x7C,
    // Db=31 → 0x5D, Db=60 clamps to the max, and Db above clamps to 31.
    [InlineData(0, 0x7C)]
    [InlineData(1, 0x7B)]
    [InlineData(10, 0x72)]
    [InlineData(20, 0x68)]
    [InlineData(31, 0x5D)]
    public void ControlFrame_Attenuator_Hl2_WritesExtendedGainByte(int db, byte expectedC4)
    {
        Span<byte> cc = stackalloc byte[5];
        var s = BaseState() with { Board = HpsdrBoardKind.HermesLite2, Atten = new HpsdrAtten(db) };
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.Attenuator, s);

        Assert.Equal(0x14, cc[0]);        // register wire byte, MOX clear
        Assert.Equal(0, cc[1]);           // C1 reserved
        Assert.Equal(0, cc[2]);           // C2 reserved
        Assert.Equal(0, cc[3]);           // C3 reserved
        Assert.Equal(expectedC4, cc[4]);  // C4 = 0x40 | (60 - db)
    }

    [Theory]
    // Bare HPSDR (Hermes/Angelia/Orion/MkII): C4 = 0x20 | (Db & 0x1F).
    [InlineData(0, 0x20)]
    [InlineData(1, 0x21)]
    [InlineData(10, 0x2A)]
    [InlineData(20, 0x34)]
    [InlineData(31, 0x3F)]
    public void ControlFrame_Attenuator_BareHpsdr_WritesExtendedStepByte(int db, byte expectedC4)
    {
        Span<byte> cc = stackalloc byte[5];
        var s = BaseState() with { Board = HpsdrBoardKind.Hermes, Atten = new HpsdrAtten(db) };
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.Attenuator, s);

        Assert.Equal(0x14, cc[0]);
        Assert.Equal(expectedC4, cc[4]);
    }

    [Fact]
    public void HpsdrAtten_ClampsOutOfRangeValues()
    {
        Assert.Equal(0, new HpsdrAtten(-5).ClampedDb);
        Assert.Equal(31, new HpsdrAtten(99).ClampedDb);
        Assert.Equal(17, new HpsdrAtten(17).ClampedDb);
    }

    [Fact]
    public void ControlFrame_Preamp_On_SetsC3Bit4()
    {
        Span<byte> cc = stackalloc byte[5];
        var s = BaseState() with { PreampOn = true };
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.Config, s);

        // C3[4] = preamp bit (doc 02 §4.1)
        Assert.Equal(1 << 4, cc[3] & (1 << 4));
    }

    [Fact]
    public void ControlFrame_Hl2BandVolts_OffByDefault()
    {
        Span<byte> cc = stackalloc byte[5];
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.Config, BaseState());
        // C3[3] = HL2 Band Volts PWM enable (legacy LT2208 DITHER on bare
        // HPSDR boards). Off by default; honoured on HL2 only. See
        // docs/references/protocol-1/hermes-lite2-protocol.md line 39.
        Assert.Equal(0, cc[3] & (1 << 3));
    }

    [Fact]
    public void ControlFrame_Hl2BandVolts_On_Sets_Config_C3_Bit3()
    {
        // Positive-path coverage for issue #279 / Band Volts PWM rename.
        // When the operator flips Band Volts on for an HL2, the Config
        // frame's C3 must light bit 3. Per
        // docs/references/protocol-1/hermes-lite2-protocol.md line 39
        // (`| 0x00 | [11] | Fan or Band Volts PWM (0=Fan, 1=Band Volts) |`),
        // this enables per-band-tagged PWM voltage on the FAN connector so
        // an external amp (Xiegu XPA125B etc.) can auto-band-switch.
        Span<byte> cc = stackalloc byte[5];
        var s = BaseState() with { EnableHl2BandVolts = true };
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.Config, s);

        Assert.Equal(0x00, cc[0]);                  // Config register, MOX clear
        Assert.Equal(1 << 3, cc[3] & (1 << 3));     // C3 bit 3 set
        // Other C3 bits stay clean — antenna ANT1 = 000, preamp off,
        // duplex / atten bits not in C3.
        Assert.Equal(1 << 3, cc[3]);
    }

    [Fact]
    public void ControlFrame_DuplexBitInC4_AlwaysOne()
    {
        Span<byte> cc = stackalloc byte[5];
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.Config, BaseState());
        // C4[2] = duplex = 1 (required even RX-only, doc 02 §4.1)
        Assert.Equal(1 << 2, cc[4] & (1 << 2));
    }

    [Fact]
    public void ControlFrame_AntennaSelection_Ant2_SetsC3UpperBits()
    {
        Span<byte> cc = stackalloc byte[5];
        var s = BaseState() with { RxAntenna = HpsdrAntenna.Ant2 };
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.Config, s);
        // C3 [7:5] = antenna select; ANT2 = 0b001 → value 0x20.
        Assert.Equal(0b001 << 5, cc[3] & 0b11100000);
    }

    [Fact]
    public void BuildDataPacket_Writes1032ByteFrameWithHeaderAndTwoSyncs()
    {
        var buf = new byte[1032];
        ControlFrame.BuildDataPacket(
            buf,
            sendSequence: 0x0A0B0C0D,
            evenRegister: ControlFrame.CcRegister.Config,
            oddRegister: ControlFrame.CcRegister.RxFreq,
            state: BaseState());

        // Metis header
        Assert.Equal(0xEF, buf[0]);
        Assert.Equal(0xFE, buf[1]);
        Assert.Equal(0x01, buf[2]);
        Assert.Equal(0x02, buf[3]); // EP2 = TX/audio endpoint
        Assert.Equal(0x0A0B0C0Du, BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(4, 4)));

        // First USB frame sync + CC0 = Config wire byte 0x00 (MOX off).
        Assert.Equal(0x7F, buf[8]);
        Assert.Equal(0x7F, buf[9]);
        Assert.Equal(0x7F, buf[10]);
        Assert.Equal(0x00, buf[11]);

        // Second USB frame sync + CC0 = RxFreq wire byte 0x04 (MOX off).
        int f2 = 8 + 512;
        Assert.Equal(0x7F, buf[f2 + 0]);
        Assert.Equal(0x7F, buf[f2 + 1]);
        Assert.Equal(0x7F, buf[f2 + 2]);
        Assert.Equal(0x04, buf[f2 + 3]);
    }

    [Fact]
    public void Protocol1Client_SetMox_FlipsC0LsbOnEveryRegister()
    {
        // End-to-end: after SetMox(true), the client's CcState snapshot feeds
        // WriteCcBytes and the MOX bit shows up as C0 LSB on every CC0 the TX
        // loop could emit. Protects the wire-level parity (output_buffer[C0]
        // |= mox) from silent drift if the setter ever gets rewired to a
        // separate field.
        using var client = new Protocol1Client();
        client.SetMox(true);
        var state = client.SnapshotState();
        Assert.True(state.Mox);

        Span<byte> cc = stackalloc byte[5];
        foreach (var register in new[]
        {
            ControlFrame.CcRegister.Config,
            ControlFrame.CcRegister.RxFreq,
            ControlFrame.CcRegister.TxFreq,
            ControlFrame.CcRegister.DriveFilter,
            ControlFrame.CcRegister.Attenuator,
        })
        {
            ControlFrame.WriteCcBytes(cc, register, state);
            Assert.Equal(1, cc[0] & 0x01);
        }

        client.SetMox(false);
        state = client.SnapshotState();
        Assert.False(state.Mox);
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.RxFreq, state);
        Assert.Equal(0, cc[0] & 0x01);
    }

    [Theory]
    // UI percent → raw HPSDR drive byte per Protocol1Client.SnapshotState:
    // raw = pct * 255 / 100. Pinned so regressions in the mapping are caught.
    [InlineData(0, 0x00)]
    [InlineData(50, 127)]      // 50 * 255 / 100 = 127
    [InlineData(100, 0xFF)]
    [InlineData(25, 63)]       // 25 * 255 / 100 = 63
    public void Protocol1Client_SetDrive_EncodesDriveFilterC1(int percent, byte expectedC1)
    {
        using var client = new Protocol1Client();
        client.SetDrive(percent);
        var state = client.SnapshotState();

        Span<byte> cc = stackalloc byte[5];
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.DriveFilter, state);

        // C0 wire byte = 0x12 (MOX off), C1 = drive byte, C2..C4 zero on HL2 MVP path.
        Assert.Equal(0x12, cc[0]);
        Assert.Equal(expectedC1, cc[1]);
        Assert.Equal(0, cc[2]);
        Assert.Equal(0, cc[3]);
        Assert.Equal(0, cc[4]);
    }

    [Theory]
    [InlineData(-10, 0)]       // clamp low
    [InlineData(250, 0xFF)]    // clamp high
    public void Protocol1Client_SetDrive_ClampsOutOfRange(int percent, byte expectedC1)
    {
        using var client = new Protocol1Client();
        client.SetDrive(percent);
        Span<byte> cc = stackalloc byte[5];
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.DriveFilter, client.SnapshotState());
        Assert.Equal(expectedC1, cc[1]);
    }

    [Fact]
    public void DriveFilter_Hl2_MoxOn_SetsC2PaEnableBit()
    {
        // On HL2, C2[3] = PA enable. Without this bit the HL2 gateware never
        // energizes the PA, so `no power out at 100% drive` shows up as silence
        // on the meter even with MOX + drive set.
        Span<byte> cc = stackalloc byte[5];
        var s = BaseState() with { Board = HpsdrBoardKind.HermesLite2, Mox = true };
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.DriveFilter, s);
        Assert.Equal(0x08, cc[2]);
    }

    [Fact]
    public void DriveFilter_Hl2_MoxOff_LeavesC2Zero()
    {
        // Don't assert PA enable while RX-only — matches the radio_is_transmitting
        // gate pattern applied everywhere else in the TX path.
        Span<byte> cc = stackalloc byte[5];
        var s = BaseState() with { Board = HpsdrBoardKind.HermesLite2, Mox = false };
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.DriveFilter, s);
        Assert.Equal(0, cc[2]);
    }

    [Fact]
    public void DriveFilter_NonHl2_MoxOn_LeavesC2Zero()
    {
        // The PA-enable bit we added is HL2-specific; other boards drive their PA
        // through different bits (Alex T/R, Apollo, etc.) which the MVP doesn't
        // touch. Keep C2 at zero for non-HL2 so we don't accidentally key unrelated
        // hardware paths.
        Span<byte> cc = stackalloc byte[5];
        var s = BaseState() with { Board = HpsdrBoardKind.Hermes, Mox = true };
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.DriveFilter, s);
        Assert.Equal(0, cc[2]);
    }

    [Fact]
    public void PhaseTable_MoxOn_SendsTxFreqInAtLeastHalfTheSlots()
    {
        // HL2 needs TxFreq (0x02) written continuously with duplex=1 or its TX
        // mixer sits at power-on default. Count how many of the 8 CC0 slots in
        // a full 4-phase cycle carry TxFreq — must be ≥ 4 (half), ensuring QSY
        // during MOX takes effect within two phase ticks.
        int txFreqSlots = 0;
        for (int phase = 0; phase < 4; phase++)
        {
            var (first, second) = Protocol1Client.PhaseRegisters(phase, mox: true);
            if (first == ControlFrame.CcRegister.TxFreq) txFreqSlots++;
            if (second == ControlFrame.CcRegister.TxFreq) txFreqSlots++;
        }
        Assert.True(txFreqSlots >= 4, $"expected ≥ 4 TxFreq slots per 4-phase cycle, got {txFreqSlots}");
    }

    [Fact]
    public void PhaseTable_MoxOff_NeverSendsTxFreq()
    {
        // Don't burn bandwidth on TX-freq updates the radio can't use. Regression
        // guard: if somebody ever conflates the two tables, RX-idle packets would
        // start carrying TxFreq unnecessarily.
        for (int phase = 0; phase < 4; phase++)
        {
            var (first, second) = Protocol1Client.PhaseRegisters(phase, mox: false);
            Assert.NotEqual(ControlFrame.CcRegister.TxFreq, first);
            Assert.NotEqual(ControlFrame.CcRegister.TxFreq, second);
        }
    }

    [Fact]
    public void PhaseTable_MoxOn_StillSendsRxFreqEveryCycle()
    {
        // In duplex=1 the RX channel keeps decoding while MOX is on, so RxFreq
        // must still appear at least once per 4-phase cycle. Protects against a
        // TX-only rewrite that would freeze RX demod at the last RxFreq from
        // before MOX engaged.
        bool sawRxFreq = false;
        for (int phase = 0; phase < 4; phase++)
        {
            var (first, second) = Protocol1Client.PhaseRegisters(phase, mox: true);
            if (first == ControlFrame.CcRegister.RxFreq || second == ControlFrame.CcRegister.RxFreq)
            {
                sawRxFreq = true;
                break;
            }
        }
        Assert.True(sawRxFreq);
    }

    [Fact]
    public void PhaseTable_MoxOn_TxFreqPayloadIsVfoA()
    {
        // Protocol-1 uses channel_freq(-1) (which resolves to the TX VFO) for
        // the 0x02 payload. We don't have a split TX VFO yet, so TxFreq should
        // encode VfoAHz — same VFO used for RxFreq. Pinning this so a future
        // split-VFO implementation changes the phase-table registers together
        // with the CcState field.
        var state = new ControlFrame.CcState(
            VfoAHz: 7_074_000,
            Rate: HpsdrSampleRate.Rate48k,
            PreampOn: false,
            Atten: HpsdrAtten.Zero,
            RxAntenna: HpsdrAntenna.Ant1,
            Mox: true,
            EnableHl2BandVolts: false,
            Board: HpsdrBoardKind.HermesLite2);

        Span<byte> cc = stackalloc byte[5];
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.TxFreq, state);

        Assert.Equal(0x03, cc[0]); // TxFreq (0x02) | MOX (0x01)
        Assert.Equal(7_074_000u, BinaryPrimitives.ReadUInt32BigEndian(cc[1..5]));
    }

    [Fact]
    public void BuildStartStop_EncodesStartEp6()
    {
        Span<byte> buf = stackalloc byte[64];
        ControlFrame.BuildStartStop(buf, start: true);
        Assert.Equal(0xEF, buf[0]);
        Assert.Equal(0xFE, buf[1]);
        Assert.Equal(0x04, buf[2]);
        Assert.Equal(0x01, buf[3]);
        for (int i = 4; i < 64; i++) Assert.Equal(0, buf[i]);
    }

    [Fact]
    public void BuildStartStop_EncodesStop()
    {
        Span<byte> buf = stackalloc byte[64];
        ControlFrame.BuildStartStop(buf, start: false);
        Assert.Equal(0xEF, buf[0]);
        Assert.Equal(0xFE, buf[1]);
        Assert.Equal(0x04, buf[2]);
        Assert.Equal(0x00, buf[3]);
    }
}
