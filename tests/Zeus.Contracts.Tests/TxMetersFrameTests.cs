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

public class TxMetersFrameTests
{
    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var frame = new TxMetersFrame(
            FwdWatts: 4.75f,
            RefWatts: 0.08f,
            Swr: 1.32f,
            MicDbfs: -23.5f,
            EqPk: -18.2f,
            LvlrPk: -12.1f,
            AlcPk: -6.0f,
            AlcGr: 3.5f,
            OutPk: -2.0f);

        var writer = new ArrayBufferWriter<byte>();
        frame.Serialize(writer);

        Assert.Equal(TxMetersFrame.ByteLength, writer.WrittenCount);
        Assert.Equal(37, writer.WrittenCount);

        var bytes = writer.WrittenSpan;
        Assert.Equal((byte)MsgType.TxMeters, bytes[0]);

        var decoded = TxMetersFrame.Deserialize(bytes);
        Assert.Equal(frame.FwdWatts, decoded.FwdWatts);
        Assert.Equal(frame.RefWatts, decoded.RefWatts);
        Assert.Equal(frame.Swr, decoded.Swr);
        Assert.Equal(frame.MicDbfs, decoded.MicDbfs);
        Assert.Equal(frame.EqPk, decoded.EqPk);
        Assert.Equal(frame.LvlrPk, decoded.LvlrPk);
        Assert.Equal(frame.AlcPk, decoded.AlcPk);
        Assert.Equal(frame.AlcGr, decoded.AlcGr);
        Assert.Equal(frame.OutPk, decoded.OutPk);
    }

    [Fact]
    public void Deserialize_RejectsWrongMsgType()
    {
        var bogus = new byte[TxMetersFrame.ByteLength];
        bogus[0] = (byte)MsgType.DisplayFrame; // 0x01, not 0x11
        Assert.Throws<InvalidDataException>(() => TxMetersFrame.Deserialize(bogus));
    }

    [Fact]
    public void Deserialize_RejectsTruncated()
    {
        var buf = new byte[TxMetersFrame.ByteLength - 1];
        buf[0] = (byte)MsgType.TxMeters;
        Assert.Throws<InvalidDataException>(() => TxMetersFrame.Deserialize(buf));
    }

    [Fact]
    public void Serialize_WritesLittleEndian()
    {
        // 1.0 f32 LE = 0x00 0x00 0x80 0x3F
        var frame = new TxMetersFrame(1.0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f);
        var writer = new ArrayBufferWriter<byte>();
        frame.Serialize(writer);
        var bytes = writer.WrittenSpan;
        Assert.Equal(0x00, bytes[1]);
        Assert.Equal(0x00, bytes[2]);
        Assert.Equal(0x80, bytes[3]);
        Assert.Equal(0x3F, bytes[4]);
    }
}
