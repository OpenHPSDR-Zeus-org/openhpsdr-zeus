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

using Zeus.Contracts;
using Zeus.Protocol1.Discovery;

namespace Zeus.Protocol1.Tests;

public class TestToneGeneratorTests
{
    private static ControlFrame.CcState Hl2Tx(byte driveLevel = 255, bool mox = true) => new(
        VfoAHz: 14_200_000,
        Rate: HpsdrSampleRate.Rate48k,
        PreampOn: false,
        Atten: HpsdrAtten.Zero,
        RxAntenna: HpsdrAntenna.Ant1,
        Mox: mox,
        EnableHl2BandVolts: false,
        Board: HpsdrBoardKind.HermesLite2,
        HasN2adr: false,
        DriveLevel: driveLevel);

    [Fact]
    public void Ep2Payload_MoxOff_AllIqBytesZero()
    {
        // Without MOX the radio is in pure RX — the EP2 IQ payload must stay
        // silent even if the test-tone generator is wired through. Regression
        // guard: if someone moves the gate to `tone != null` only, we'd start
        // bleeding a 1 kHz spur onto every RX packet.
        var buf = new byte[ControlFrame.PacketLength];
        var tone = new TestToneGenerator();
        ControlFrame.BuildDataPacket(
            buf, 0,
            ControlFrame.CcRegister.Config, ControlFrame.CcRegister.RxFreq,
            Hl2Tx(mox: false),
            tone);

        AssertPayloadIqZero(buf);
    }

    [Fact]
    public void Ep2Payload_MoxOn_NonHl2_AllIqBytesZero()
    {
        // Don't accidentally key RF on boards whose PA bits we don't model yet.
        var buf = new byte[ControlFrame.PacketLength];
        var tone = new TestToneGenerator();
        var state = Hl2Tx() with { Board = HpsdrBoardKind.Hermes };
        ControlFrame.BuildDataPacket(
            buf, 0,
            ControlFrame.CcRegister.Config, ControlFrame.CcRegister.RxFreq,
            state, tone);

        AssertPayloadIqZero(buf);
    }

    [Fact]
    public void Ep2Payload_MoxOn_Hl2_ProducesNonZeroIq()
    {
        var buf = new byte[ControlFrame.PacketLength];
        var tone = new TestToneGenerator();
        ControlFrame.BuildDataPacket(
            buf, 0,
            ControlFrame.CcRegister.TxFreq, ControlFrame.CcRegister.RxFreq,
            Hl2Tx(),
            tone);

        // With drive=255 + mox=true, we expect non-trivial IQ energy on the wire.
        // The generator starts at phase 0 so sample 0 is (+amplitude, 0); sample
        // 12 at 48 kHz is at 90° so Q peaks. Assert at least *some* bytes non-zero.
        int nonZero = 0;
        for (int f = 0; f < 2; f++)
        {
            int payloadStart = 8 + f * ControlFrame.UsbFrameLength + 8;
            for (int s = 0; s < 63; s++)
            {
                int off = payloadStart + s * 8;
                if (buf[off + 4] != 0 || buf[off + 5] != 0 ||
                    buf[off + 6] != 0 || buf[off + 7] != 0)
                    nonZero++;
            }
        }
        Assert.True(nonZero > 100, $"expected >100 non-zero IQ slots, got {nonZero}");
    }

    [Fact]
    public void Ep2Payload_Hl2_IqLowBytesHaveLsbClear()
    {
        // Protocol-1 clears the LSB of I and Q on HL2 as a CWX firmware
        // workaround. We mirror that — every low byte of I and Q must be even,
        // so verify across a full packet.
        var buf = new byte[ControlFrame.PacketLength];
        var tone = new TestToneGenerator();
        ControlFrame.BuildDataPacket(
            buf, 0,
            ControlFrame.CcRegister.TxFreq, ControlFrame.CcRegister.RxFreq,
            Hl2Tx(),
            tone);

        for (int f = 0; f < 2; f++)
        {
            int payloadStart = 8 + f * ControlFrame.UsbFrameLength + 8;
            for (int s = 0; s < 63; s++)
            {
                int off = payloadStart + s * 8;
                Assert.Equal(0, buf[off + 5] & 0x01);  // I low byte LSB clear
                Assert.Equal(0, buf[off + 7] & 0x01);  // Q low byte LSB clear
            }
        }
    }

    [Fact]
    public void Ep2Payload_AudioBytesAlwaysZeroOnHl2()
    {
        // HL2 (no audio codec) ignores the audio s16 pair; send zeros to avoid
        // unintentional extended-address writes. Pin this so a future
        // audio-uplink refactor doesn't silently poke HL2 registers.
        var buf = new byte[ControlFrame.PacketLength];
        var tone = new TestToneGenerator();
        ControlFrame.BuildDataPacket(
            buf, 0,
            ControlFrame.CcRegister.TxFreq, ControlFrame.CcRegister.RxFreq,
            Hl2Tx(),
            tone);

        for (int f = 0; f < 2; f++)
        {
            int payloadStart = 8 + f * ControlFrame.UsbFrameLength + 8;
            for (int s = 0; s < 63; s++)
            {
                int off = payloadStart + s * 8;
                Assert.Equal(0, buf[off + 0]);  // L audio hi
                Assert.Equal(0, buf[off + 1]);  // L audio lo
                Assert.Equal(0, buf[off + 2]);  // R audio hi
                Assert.Equal(0, buf[off + 3]);  // R audio lo
            }
        }
    }

    [Fact]
    public void Ep2Payload_PhaseIsContinuousAcrossPackets()
    {
        // 126 IQ samples per packet. Build two consecutive packets with one
        // generator, then compare the first IQ sample of packet 2 against a
        // reference generator that's been independently advanced by 126 samples.
        // Byte-exact equality catches both phase drift AND the HL2 LSB-mask
        // so the spliced waveform has no step discontinuity.
        var buf1 = new byte[ControlFrame.PacketLength];
        var buf2 = new byte[ControlFrame.PacketLength];
        var tone = new TestToneGenerator();
        var state = Hl2Tx();

        ControlFrame.BuildDataPacket(
            buf1, 0,
            ControlFrame.CcRegister.TxFreq, ControlFrame.CcRegister.RxFreq,
            state, tone);
        ControlFrame.BuildDataPacket(
            buf2, 1,
            ControlFrame.CcRegister.TxFreq, ControlFrame.CcRegister.RxFreq,
            state, tone);

        // Reference: advance by 126 samples directly. IQ stays at full-scale
        // regardless of drive (drive% is applied by the HL2 TXG stage, not by
        // scaling IQ — double-scaling would give drive⁴ power response).
        // Zeus now sends at amplitude=1.0: WDSP's ALC clamps the TXA output
        // to ≤ 0 dBFS and the TUN post-gen tone is a fixed-amplitude carrier,
        // so neither source can overshoot. The prior 0.85 factor cost ~1.4 dB
        // of achievable HL2 output on TUNE for no protection benefit.
        var reference = new TestToneGenerator();
        const double amplitude = 1.0;
        for (int n = 0; n < 126; n++) reference.Next(amplitude);
        var (expectedI, expectedQ) = reference.Next(amplitude);

        int firstSampleOffset = 8 + 8;  // metis header + sync+CC
        short actualI = (short)(((buf2[firstSampleOffset + 4] & 0xFF) << 8) | (buf2[firstSampleOffset + 5] & 0xFF));
        short actualQ = (short)(((buf2[firstSampleOffset + 6] & 0xFF) << 8) | (buf2[firstSampleOffset + 7] & 0xFF));

        Assert.Equal((short)(expectedI & unchecked((short)0xFFFE)), actualI);
        Assert.Equal((short)(expectedQ & unchecked((short)0xFFFE)), actualQ);
    }

    [Fact]
    public void Ep2Payload_AmplitudeScalesWithDrive()
    {
        // drive=0 → zero IQ even with MOX on. Without this, the user can't mute
        // the carrier with the drive slider, and an unintentional key-up at 0%
        // would still radiate.
        var buf = new byte[ControlFrame.PacketLength];
        var tone = new TestToneGenerator();
        ControlFrame.BuildDataPacket(
            buf, 0,
            ControlFrame.CcRegister.TxFreq, ControlFrame.CcRegister.RxFreq,
            Hl2Tx(driveLevel: 0),
            tone);
        AssertPayloadIqZero(buf);
    }

    [Fact]
    public void TestToneGenerator_DefaultFrequency_Produces1kHzOver48Samples()
    {
        // 48 samples at 48 kHz of a 1 kHz tone = exactly one period. Sample 0 at
        // phase 0 should be (max, 0); sample 24 (half period) should approach
        // (-max, 0); after 48 samples phase wraps back near 0.
        var tone = new TestToneGenerator();
        var (i0, q0) = tone.Next(1.0);
        Assert.True(i0 > 32000);
        Assert.InRange(q0, -1000, 1000);

        for (int n = 1; n < 24; n++) tone.Next(1.0);
        var (i24, q24) = tone.Next(1.0);
        Assert.True(i24 < -32000);
        Assert.InRange(q24, -1000, 1000);
    }

    // --- helpers ----------------------------------------------------------

    private static void AssertPayloadIqZero(byte[] packet)
    {
        for (int f = 0; f < 2; f++)
        {
            int payloadStart = 8 + f * ControlFrame.UsbFrameLength + 8;
            for (int s = 0; s < 63; s++)
            {
                int off = payloadStart + s * 8;
                Assert.Equal(0, packet[off + 4]);
                Assert.Equal(0, packet[off + 5]);
                Assert.Equal(0, packet[off + 6]);
                Assert.Equal(0, packet[off + 7]);
            }
        }
    }
}
