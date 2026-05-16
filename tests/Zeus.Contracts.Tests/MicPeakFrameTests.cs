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

using System.Buffers;
using Zeus.Contracts;
using Xunit;

namespace Zeus.Contracts.Tests;

public class MicPeakFrameTests
{
    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var frame = new MicPeakFrame(PeakDbfs: -23.5f, TimestampUnixMs: 1_700_000_000_000L);

        var writer = new ArrayBufferWriter<byte>();
        frame.Serialize(writer);

        Assert.Equal(MicPeakFrame.ByteLength, writer.WrittenCount);
        Assert.Equal(13, writer.WrittenCount);

        var bytes = writer.WrittenSpan;
        Assert.Equal((byte)MsgType.MicPeak, bytes[0]);

        var decoded = MicPeakFrame.Deserialize(bytes);
        Assert.Equal(frame.PeakDbfs, decoded.PeakDbfs);
        Assert.Equal(frame.TimestampUnixMs, decoded.TimestampUnixMs);
    }

    [Fact]
    public void Deserialize_RejectsWrongMsgType()
    {
        var bogus = new byte[MicPeakFrame.ByteLength];
        bogus[0] = (byte)MsgType.PaTemp; // 0x17, not 0x1C
        Assert.Throws<InvalidDataException>(() => MicPeakFrame.Deserialize(bogus));
    }

    [Fact]
    public void Deserialize_RejectsTruncated()
    {
        var buf = new byte[MicPeakFrame.ByteLength - 1];
        buf[0] = (byte)MsgType.MicPeak;
        Assert.Throws<InvalidDataException>(() => MicPeakFrame.Deserialize(buf));
    }

    [Fact]
    public void ByteLength_Is13()
    {
        // 1 type byte + 4 (f32 peak) + 8 (i64 ts) = 13.
        Assert.Equal(13, MicPeakFrame.ByteLength);
    }

    [Fact]
    public void MsgType_MicPeak_Is0x1C()
    {
        // Lock the wire-format byte assignment: 0x1A VstHostEvent and 0x1B
        // BandPlanChanged are taken; 0x1C is the next free slot above the
        // 0x1x server→client telemetry block. (0x20 is the client→server
        // MicPcm uplink — a different direction, deliberately separate.)
        Assert.Equal((byte)0x1C, (byte)MsgType.MicPeak);
    }

    [Fact]
    public void LinearToDbfs_SilenceClampsToFloor()
    {
        // Zero / negative peaks must hit the -120 dBFS floor without
        // throwing on log10(0) = -∞ — a silent (TCC-muted) stream is a
        // routine case, not an error.
        Assert.Equal(MicPeakFrame.MinDbfs, MicPeakFrame.LinearToDbfs(0f));
        Assert.Equal(MicPeakFrame.MinDbfs, MicPeakFrame.LinearToDbfs(-0.5f));
    }

    [Fact]
    public void LinearToDbfs_KnownLevels()
    {
        // 1.0 = 0 dBFS; 0.5 = ~-6.02 dBFS; 0.1 = -20 dBFS exactly.
        Assert.Equal(0f, MicPeakFrame.LinearToDbfs(1.0f), 3);
        Assert.Equal(-6.0206f, MicPeakFrame.LinearToDbfs(0.5f), 3);
        Assert.Equal(-20.0f, MicPeakFrame.LinearToDbfs(0.1f), 3);
    }

    [Fact]
    public void LinearToDbfs_AboveUnityClampsToZero()
    {
        // Intra-callback clipping (sample > 1.0) — the operator sees the
        // meter pinned at 0 dBFS rather than a misleading +1 dBFS readout.
        Assert.Equal(0f, MicPeakFrame.LinearToDbfs(1.5f));
        Assert.Equal(0f, MicPeakFrame.LinearToDbfs(10f));
    }

    [Fact]
    public void LinearToDbfs_BelowFloorClamps()
    {
        // 1e-10 → -200 dBFS theoretical; must clamp to MinDbfs.
        Assert.Equal(MicPeakFrame.MinDbfs, MicPeakFrame.LinearToDbfs(1e-10f));
    }

    [Fact]
    public void Serialize_WritesLittleEndian()
    {
        // 1.0 f32 LE = 0x00 0x00 0x80 0x3F; 1 i64 LE = 0x01 0x00 ... 0x00.
        var frame = new MicPeakFrame(PeakDbfs: 1.0f, TimestampUnixMs: 1L);
        var writer = new ArrayBufferWriter<byte>();
        frame.Serialize(writer);
        var bytes = writer.WrittenSpan;
        Assert.Equal(0x00, bytes[1]);
        Assert.Equal(0x00, bytes[2]);
        Assert.Equal(0x80, bytes[3]);
        Assert.Equal(0x3F, bytes[4]);
        Assert.Equal(0x01, bytes[5]);
        Assert.Equal(0x00, bytes[12]);
    }
}
