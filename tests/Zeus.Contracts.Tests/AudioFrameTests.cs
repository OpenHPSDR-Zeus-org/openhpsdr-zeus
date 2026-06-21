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

public class AudioFrameTests
{
    [Theory]
    [InlineData(1, 256)]
    [InlineData(1, 2048)]
    [InlineData(2, 1024)]
    public void RoundTrip_PreservesAllFields(int channels, int sampleCount)
    {
        int total = channels * sampleCount;
        var samples = new float[total];
        for (int i = 0; i < total; i++)
            samples[i] = MathF.Sin(i * 0.01f) * 0.5f;

        var frame = new AudioFrame(
            Seq: 7,
            TsUnixMs: 1_700_000_000_456.25,
            RxId: 0,
            Channels: (byte)channels,
            SampleRateHz: 48_000u,
            SampleCount: (ushort)sampleCount,
            Samples: samples);

        var writer = new ArrayBufferWriter<byte>();
        frame.Serialize(writer);
        Assert.Equal(frame.TotalByteLength, writer.WrittenCount);

        int expectedBody = 1 + 1 + 4 + 2 + total * 4;
        WireFormat.ReadHeader(writer.WrittenSpan, out var mt, out _, out var payloadLen, out var seq, out var ts);
        Assert.Equal(MsgType.AudioPcm, mt);
        Assert.Equal(expectedBody, payloadLen);
        Assert.Equal(7u, seq);
        Assert.Equal(1_700_000_000_456.25, ts);

        var decoded = AudioFrame.Deserialize(writer.WrittenSpan);
        Assert.Equal(frame.Seq, decoded.Seq);
        Assert.Equal(frame.TsUnixMs, decoded.TsUnixMs);
        Assert.Equal(frame.RxId, decoded.RxId);
        Assert.Equal(frame.Channels, decoded.Channels);
        Assert.Equal(frame.SampleRateHz, decoded.SampleRateHz);
        Assert.Equal(frame.SampleCount, decoded.SampleCount);
        Assert.Equal(samples, decoded.Samples.ToArray());
    }

    [Fact]
    public void Serialize_RejectsLengthMismatch()
    {
        var frame = new AudioFrame(
            Seq: 1,
            TsUnixMs: 0,
            RxId: 0,
            Channels: 1,
            SampleRateHz: 48_000u,
            SampleCount: 10,
            Samples: new float[9]);

        var writer = new ArrayBufferWriter<byte>();
        Assert.Throws<InvalidOperationException>(() => frame.Serialize(writer));
    }

    [Fact]
    public void MsgType_IsAudioPcm_InHeader()
    {
        var frame = new AudioFrame(
            Seq: 1,
            TsUnixMs: 0,
            RxId: 0,
            Channels: 1,
            SampleRateHz: 48_000u,
            SampleCount: 4,
            Samples: new float[4]);
        var writer = new ArrayBufferWriter<byte>();
        frame.Serialize(writer);
        Assert.Equal((byte)MsgType.AudioPcm, writer.WrittenSpan[0]);
    }
}
