// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

using System.Buffers;
using Zeus.Contracts;
using Xunit;

namespace Zeus.Contracts.Tests;

public class CwEngineStatusFrameTests
{
    [Fact]
    public void SerializeDeserialize_RoundTrip()
    {
        var frame = new CwEngineStatusFrame(
            State: CwEngineState.Sending,
            Wpm: 22,
            QueueDepth: 3,
            Text: "CQ CQ CQ DE EA5IUE");
        var writer = new ArrayBufferWriter<byte>();
        frame.Serialize(writer);

        var decoded = CwEngineStatusFrame.Deserialize(writer.WrittenSpan);

        Assert.Equal(CwEngineState.Sending, decoded.State);
        Assert.Equal(22, decoded.Wpm);
        Assert.Equal(3, decoded.QueueDepth);
        Assert.Equal("CQ CQ CQ DE EA5IUE", decoded.Text);
    }

    [Fact]
    public void Serialize_WritesCorrectMsgType()
    {
        var frame = new CwEngineStatusFrame(CwEngineState.Idle, 20, 0, "");
        var writer = new ArrayBufferWriter<byte>();
        frame.Serialize(writer);

        Assert.Equal((byte)MsgType.CwEngineStatus, writer.WrittenSpan[0]);
    }

    [Fact]
    public void Serialize_EmptyText_Produces9ByteFrame()
    {
        var frame = new CwEngineStatusFrame(CwEngineState.Idle, 0, 0, "");
        var writer = new ArrayBufferWriter<byte>();
        frame.Serialize(writer);

        // 1 byte type + 1 state + 2 wpm + 2 depth + 2 textLen + 0 text = 9.
        Assert.Equal(CwEngineStatusFrame.HeaderByteLength, writer.WrittenSpan.Length);
    }

    [Fact]
    public void Serialize_Utf8Text_RoundTripsCleanly()
    {
        // UTF-8-encoded macro lengths can exceed .Length; the frame must
        // round-trip the bytes, not the char count.
        var frame = new CwEngineStatusFrame(CwEngineState.Sending, 18, 1, "73 — gud QSO");
        var writer = new ArrayBufferWriter<byte>();
        frame.Serialize(writer);
        var decoded = CwEngineStatusFrame.Deserialize(writer.WrittenSpan);

        Assert.Equal("73 — gud QSO", decoded.Text);
    }

    [Fact]
    public void Serialize_OversizedText_TruncatesToMax()
    {
        // Defensive cap so a runaway macro can't blow a single broadcast
        // frame. Truncation happens silently at the serializer; deserialize
        // sees only the truncated text and is still well-formed.
        var huge = new string('X', CwEngineStatusFrame.MaxTextBytes * 2);
        var frame = new CwEngineStatusFrame(CwEngineState.Sending, 20, 0, huge);
        var writer = new ArrayBufferWriter<byte>();
        frame.Serialize(writer);

        var decoded = CwEngineStatusFrame.Deserialize(writer.WrittenSpan);
        Assert.Equal(CwEngineStatusFrame.MaxTextBytes, decoded.Text.Length);
    }

    [Fact]
    public void Deserialize_RejectsWrongMsgType()
    {
        var bytes = new byte[CwEngineStatusFrame.HeaderByteLength];
        bytes[0] = (byte)MsgType.Alert;       // wrong type
        Assert.Throws<InvalidDataException>(() => CwEngineStatusFrame.Deserialize(bytes));
    }

    [Fact]
    public void Deserialize_RejectsTruncatedHeader()
    {
        var bytes = new byte[CwEngineStatusFrame.HeaderByteLength - 1];
        bytes[0] = (byte)MsgType.CwEngineStatus;
        Assert.Throws<InvalidDataException>(() => CwEngineStatusFrame.Deserialize(bytes));
    }

    [Fact]
    public void Deserialize_RejectsTruncatedText()
    {
        // Header claims 50 bytes of text but only 10 follow — the frame
        // is malformed and must be rejected rather than silently returning
        // a short string.
        var frame = new CwEngineStatusFrame(CwEngineState.Sending, 20, 0, new string('A', 50));
        var writer = new ArrayBufferWriter<byte>();
        frame.Serialize(writer);
        var truncated = writer.WrittenSpan.Slice(0, CwEngineStatusFrame.HeaderByteLength + 10).ToArray();

        Assert.Throws<InvalidDataException>(() => CwEngineStatusFrame.Deserialize(truncated));
    }

    [Fact]
    public void FromStatus_LiftsDtoFieldsOntoWire()
    {
        var status = new CwEngineStatus(
            State: CwEngineState.Stopping,
            Text: "AGN?",
            Wpm: 25,
            QueueDepth: 7,
            Reason: "operator request");

        var frame = CwEngineStatusFrame.FromStatus(status);

        Assert.Equal(CwEngineState.Stopping, frame.State);
        Assert.Equal(25, frame.Wpm);
        Assert.Equal(7, frame.QueueDepth);
        Assert.Equal("AGN?", frame.Text);
        // Reason is intentionally not in the wire frame — frontend doesn't
        // need it; logged server-side only. Just sanity-check the lift
        // didn't crash on the nullable.
    }
}
