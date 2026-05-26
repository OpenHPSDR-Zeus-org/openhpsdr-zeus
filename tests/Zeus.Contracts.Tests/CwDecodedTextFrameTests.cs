// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

using System.Buffers;
using Zeus.Contracts;
using Xunit;

namespace Zeus.Contracts.Tests;

public class CwDecodedTextFrameTests
{
    [Fact]
    public void Serialize_EmptyText_ProducesHeaderOnlyFrame()
    {
        var frame = new CwDecodedTextFrame("", 0, 0f, 0f);
        var writer = new ArrayBufferWriter<byte>();
        frame.Serialize(writer);

        // type(1) + wpm(2) + snr(4) + conf(4) + textLen(2) = 13, no text.
        Assert.Equal(CwDecodedTextFrame.HeaderByteLength, writer.WrittenSpan.Length);
    }

    [Fact]
    public void Serialize_RoundTripsAllFields()
    {
        var frame = new CwDecodedTextFrame("CQ DE EA", 22, 9.5f, 0.8f);
        var writer = new ArrayBufferWriter<byte>();
        frame.Serialize(writer);
        var decoded = CwDecodedTextFrame.Deserialize(writer.WrittenSpan);

        Assert.Equal("CQ DE EA", decoded.Text);
        Assert.Equal(22, decoded.Wpm);
        Assert.Equal(9.5f, decoded.SnrDb);
        Assert.Equal(0.8f, decoded.Confidence);
    }

    [Fact]
    public void Serialize_OversizedText_TruncatesToMax()
    {
        var huge = new string('X', CwDecodedTextFrame.MaxTextBytes * 2);
        var frame = new CwDecodedTextFrame(huge, 20, 0f, 0f);
        var writer = new ArrayBufferWriter<byte>();
        frame.Serialize(writer);

        var decoded = CwDecodedTextFrame.Deserialize(writer.WrittenSpan);
        Assert.Equal(CwDecodedTextFrame.MaxTextBytes, decoded.Text.Length);
    }

    [Fact]
    public void Deserialize_RejectsWrongMsgType()
    {
        var bytes = new byte[CwDecodedTextFrame.HeaderByteLength];
        bytes[0] = (byte)MsgType.CwEngineStatus; // wrong type (0x30, not 0x31)
        Assert.Throws<InvalidDataException>(() => CwDecodedTextFrame.Deserialize(bytes));
    }

    [Fact]
    public void Deserialize_RejectsTruncatedHeader()
    {
        var bytes = new byte[CwDecodedTextFrame.HeaderByteLength - 1];
        bytes[0] = (byte)MsgType.CwDecodedText;
        Assert.Throws<InvalidDataException>(() => CwDecodedTextFrame.Deserialize(bytes));
    }

    [Fact]
    public void Deserialize_RejectsTruncatedText()
    {
        var frame = new CwDecodedTextFrame(new string('A', 50), 20, 0f, 0f);
        var writer = new ArrayBufferWriter<byte>();
        frame.Serialize(writer);
        var truncated = writer.WrittenSpan
            .Slice(0, CwDecodedTextFrame.HeaderByteLength + 10).ToArray();

        Assert.Throws<InvalidDataException>(() => CwDecodedTextFrame.Deserialize(truncated));
    }
}
