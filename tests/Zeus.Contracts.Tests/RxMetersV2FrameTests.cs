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

using System.Buffers;
using Zeus.Contracts;
using Xunit;

namespace Zeus.Contracts.Tests;

public class RxMetersV2FrameTests
{
    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var frame = new RxMetersV2Frame(
            SignalPk: -73.4f,
            SignalAv: -82.1f,
            AdcPk: -32.7f,
            AdcAv: -45.2f,
            AgcGain: 18.5f,
            AgcEnvPk: -68.0f,
            AgcEnvAv: -76.9f);

        var writer = new ArrayBufferWriter<byte>();
        frame.Serialize(writer);

        Assert.Equal(RxMetersV2Frame.ByteLength, writer.WrittenCount);
        Assert.Equal(29, writer.WrittenCount);

        var bytes = writer.WrittenSpan;
        Assert.Equal((byte)MsgType.RxMetersV2, bytes[0]);

        var decoded = RxMetersV2Frame.Deserialize(bytes);
        Assert.Equal(frame.SignalPk, decoded.SignalPk);
        Assert.Equal(frame.SignalAv, decoded.SignalAv);
        Assert.Equal(frame.AdcPk, decoded.AdcPk);
        Assert.Equal(frame.AdcAv, decoded.AdcAv);
        Assert.Equal(frame.AgcGain, decoded.AgcGain);
        Assert.Equal(frame.AgcEnvPk, decoded.AgcEnvPk);
        Assert.Equal(frame.AgcEnvAv, decoded.AgcEnvAv);
    }

    [Fact]
    public void RoundTrip_PreservesNegativeAgcGain()
    {
        // RX AGC gain genuinely swings both ways: positive when boosting a
        // weak signal, negative when cutting a hot one. The wire format
        // must preserve the sign — flipping to a one-sided "reduction"
        // scale would lose operator information. Assert the round-trip
        // for a negative (cutting) value to lock that behavior.
        var frame = new RxMetersV2Frame(
            SignalPk: -10f, SignalAv: -12f,
            AdcPk: -5f, AdcAv: -8f,
            AgcGain: -12.5f,
            AgcEnvPk: -10f, AgcEnvAv: -12f);

        var writer = new ArrayBufferWriter<byte>();
        frame.Serialize(writer);
        var decoded = RxMetersV2Frame.Deserialize(writer.WrittenSpan);
        Assert.Equal(-12.5f, decoded.AgcGain);
    }

    [Fact]
    public void Deserialize_RejectsWrongMsgType()
    {
        var bogus = new byte[RxMetersV2Frame.ByteLength];
        bogus[0] = (byte)MsgType.TxMetersV2; // 0x16, not 0x19 — should be rejected
        Assert.Throws<InvalidDataException>(() => RxMetersV2Frame.Deserialize(bogus));
    }

    [Fact]
    public void Deserialize_RejectsTruncated()
    {
        var buf = new byte[RxMetersV2Frame.ByteLength - 1];
        buf[0] = (byte)MsgType.RxMetersV2;
        Assert.Throws<InvalidDataException>(() => RxMetersV2Frame.Deserialize(buf));
    }

    [Fact]
    public void Serialize_WritesLittleEndian()
    {
        // 1.0 f32 LE = 0x00 0x00 0x80 0x3F — first float slot (SignalPk)
        // lives at bytes 1..4.
        var frame = new RxMetersV2Frame(
            SignalPk: 1.0f,
            SignalAv: 0f,
            AdcPk: 0f,
            AdcAv: 0f,
            AgcGain: 0f,
            AgcEnvPk: 0f,
            AgcEnvAv: 0f);
        var writer = new ArrayBufferWriter<byte>();
        frame.Serialize(writer);
        var bytes = writer.WrittenSpan;
        Assert.Equal(0x00, bytes[1]);
        Assert.Equal(0x00, bytes[2]);
        Assert.Equal(0x80, bytes[3]);
        Assert.Equal(0x3F, bytes[4]);
    }

    [Fact]
    public void ByteLength_Is29()
    {
        // Sanity check so a future field insertion without updating
        // ByteLength fails the test suite immediately: 1 type byte +
        // 7 × f32 = 29 bytes.
        Assert.Equal(29, RxMetersV2Frame.ByteLength);
    }

    [Fact]
    public void MsgType_RxMetersV2_Is0x19()
    {
        // Lock the wire-format byte assignment: 0x14 RxMeter, 0x16 TxMetersV2,
        // 0x17 PaTemp, 0x18 PsMeters are taken; 0x19 is the next free slot.
        Assert.Equal((byte)0x19, (byte)MsgType.RxMetersV2);
    }
}
