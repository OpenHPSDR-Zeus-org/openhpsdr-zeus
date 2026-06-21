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

public class DisplayFrameTests
{
    [Theory]
    [InlineData(64)]
    [InlineData(2048)]
    public void RoundTrip_PreservesAllFields(int width)
    {
        var pan = new float[width];
        var wf = new float[width];
        for (int i = 0; i < width; i++)
        {
            pan[i] = -90f + i * 0.25f;
            wf[i] = -80f - i * 0.125f;
        }

        var frame = new DisplayFrame(
            Seq: 42,
            TsUnixMs: 1_700_000_000_123.5,
            RxId: 0,
            BodyFlags: DisplayBodyFlags.PanValid | DisplayBodyFlags.WfValid,
            Width: (ushort)width,
            CenterHz: 14_200_000,
            HzPerPixel: 192_000f / width,
            PanDb: pan,
            WfDb: wf);

        var writer = new ArrayBufferWriter<byte>();
        frame.Serialize(writer);
        Assert.Equal(frame.TotalByteLength, writer.WrittenCount);

        int expectedBody = 1 + 1 + 2 + 8 + 4 + width * 4 * 2;
        WireFormat.ReadHeader(writer.WrittenSpan, out var mt, out _, out var payloadLen, out var seq, out var ts);
        Assert.Equal(MsgType.DisplayFrame, mt);
        Assert.Equal(expectedBody, payloadLen);
        Assert.Equal(42u, seq);
        Assert.Equal(1_700_000_000_123.5, ts);

        var decoded = DisplayFrame.Deserialize(writer.WrittenSpan);
        Assert.Equal(frame.Seq, decoded.Seq);
        Assert.Equal(frame.TsUnixMs, decoded.TsUnixMs);
        Assert.Equal(frame.RxId, decoded.RxId);
        Assert.Equal(frame.BodyFlags, decoded.BodyFlags);
        Assert.Equal(frame.Width, decoded.Width);
        Assert.Equal(frame.CenterHz, decoded.CenterHz);
        Assert.Equal(frame.HzPerPixel, decoded.HzPerPixel);
        Assert.Equal(pan, decoded.PanDb.ToArray());
        Assert.Equal(wf, decoded.WfDb.ToArray());
    }

    [Fact]
    public void WireFormat_IsLittleEndian()
    {
        Span<byte> buf = stackalloc byte[WireFormat.HeaderSize];
        WireFormat.WriteHeader(buf, MsgType.DisplayFrame, 0x01, 0x1234, 0xAABBCCDD, 0.0);
        Assert.Equal(0x01, buf[0]);
        Assert.Equal(0x01, buf[1]);
        Assert.Equal(0x34, buf[2]);
        Assert.Equal(0x12, buf[3]);
        Assert.Equal(0xDD, buf[4]);
        Assert.Equal(0xCC, buf[5]);
        Assert.Equal(0xBB, buf[6]);
        Assert.Equal(0xAA, buf[7]);
    }
}
