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

using Zeus.Contracts;
using Xunit;

namespace Zeus.Contracts.Tests;

public class AlertFrameTests
{
    [Fact]
    public void SerializeDeserialize_RoundTrip()
    {
        var frame = new AlertFrame(AlertKind.SwrTrip, "SWR 3.0:1 sustained >500 ms");
        var writer = new System.Buffers.ArrayBufferWriter<byte>();
        frame.Serialize(writer);
        var bytes = writer.WrittenSpan;

        Assert.Equal((byte)MsgType.Alert, bytes[0]);
        Assert.Equal((byte)AlertKind.SwrTrip, bytes[1]);

        var decoded = AlertFrame.Deserialize(bytes);
        Assert.Equal(AlertKind.SwrTrip, decoded.Kind);
        Assert.Equal("SWR 3.0:1 sustained >500 ms", decoded.Message);
    }

    [Fact]
    public void Serialize_WritesCorrectMsgType()
    {
        var frame = new AlertFrame(AlertKind.SwrTrip, "test");
        var writer = new System.Buffers.ArrayBufferWriter<byte>();
        frame.Serialize(writer);
        Assert.Equal((byte)MsgType.Alert, writer.WrittenSpan[0]);
    }

    [Fact]
    public void Deserialize_RejectsWrongMsgType()
    {
        var bytes = new byte[] { (byte)MsgType.TxMeters, 0, 0x74, 0x65, 0x73, 0x74 }; // "test"
        Assert.Throws<InvalidDataException>(() => AlertFrame.Deserialize(bytes));
    }

    [Fact]
    public void Deserialize_RequiresAtLeast2Bytes()
    {
        var bytes = new byte[] { (byte)MsgType.Alert };
        Assert.Throws<InvalidDataException>(() => AlertFrame.Deserialize(bytes));
    }

    [Fact]
    public void Serialize_EmptyMessage()
    {
        var frame = new AlertFrame(AlertKind.SwrTrip, "");
        var writer = new System.Buffers.ArrayBufferWriter<byte>();
        frame.Serialize(writer);
        var bytes = writer.WrittenSpan;

        Assert.Equal(2, bytes.Length); // type + kind only
        var decoded = AlertFrame.Deserialize(bytes);
        Assert.Equal(string.Empty, decoded.Message);
    }

    [Fact]
    public void Serialize_Utf8EncodedMessage()
    {
        var frame = new AlertFrame(AlertKind.SwrTrip, "SWR 3.0:1 — dropped TX");
        var writer = new System.Buffers.ArrayBufferWriter<byte>();
        frame.Serialize(writer);
        var bytes = writer.WrittenSpan;

        var decoded = AlertFrame.Deserialize(bytes);
        Assert.Equal("SWR 3.0:1 — dropped TX", decoded.Message);
    }
}
