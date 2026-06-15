// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// External-ports plan §4 — PTT-IN status lamp wire frame (0x33).

using System.Buffers;
using Zeus.Contracts;
using Xunit;

namespace Zeus.Contracts.Tests;

public class PttStatusFrameTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RoundTrip_PreservesKeyed(bool keyed)
    {
        var frame = new PttStatusFrame(Keyed: keyed);

        var writer = new ArrayBufferWriter<byte>();
        frame.Serialize(writer);

        Assert.Equal(PttStatusFrame.ByteLength, writer.WrittenCount);
        Assert.Equal(2, writer.WrittenCount);

        var bytes = writer.WrittenSpan;
        Assert.Equal((byte)MsgType.PttStatus, bytes[0]);
        Assert.Equal(keyed ? (byte)1 : (byte)0, bytes[1]);

        var decoded = PttStatusFrame.Deserialize(bytes);
        Assert.Equal(keyed, decoded.Keyed);
    }

    [Fact]
    public void MsgType_PttStatus_Is0x33()
    {
        // Next free control-plane slot above SpotList (0x32).
        Assert.Equal((byte)0x33, (byte)MsgType.PttStatus);
    }

    [Fact]
    public void Deserialize_RejectsWrongMsgType()
    {
        var bogus = new byte[PttStatusFrame.ByteLength];
        bogus[0] = (byte)MsgType.MoxState; // 0x1C, not 0x33
        Assert.Throws<InvalidDataException>(() => PttStatusFrame.Deserialize(bogus));
    }

    [Fact]
    public void Deserialize_RejectsTruncated()
    {
        var buf = new byte[PttStatusFrame.ByteLength - 1];
        buf[0] = (byte)MsgType.PttStatus;
        Assert.Throws<InvalidDataException>(() => PttStatusFrame.Deserialize(buf));
    }
}
