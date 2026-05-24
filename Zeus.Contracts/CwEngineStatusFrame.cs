// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Zeus.Contracts;

/// <summary>
/// Wire frame for <see cref="CwEngineStatus"/>. Broadcast on every state
/// edge of the host-side CW engine so the macro pad can show in-flight
/// text + queue depth without polling. Format:
///
/// <code>
/// [type:1=0x30][state:u8][wpm:u16 LE][queueDepth:u16 LE]
/// [textLen:u16 LE][text:UTF-8 textLen bytes]
/// </code>
///
/// 9-byte fixed header + variable text payload. Text is capped at
/// <see cref="MaxTextBytes"/> so a runaway macro can't blow the wire — the
/// frontend reconstructs per-character position from <see cref="Wpm"/> +
/// the local-arrival timestamp (cheap to do client-side; no need to push
/// per-character progress frames at audio rate).
///
/// Wire-frozen: state is <see cref="CwEngineState"/> as a byte; future
/// additions append-only at the tail.
/// </summary>
public readonly record struct CwEngineStatusFrame(
    CwEngineState State,
    int Wpm,
    int QueueDepth,
    string Text)
{
    /// <summary>Hard cap on the text payload — well above any realistic CW
    /// macro length. Senders that hand us a longer string get truncated to
    /// this size; the frame stays under one MTU on a typical LAN.</summary>
    public const int MaxTextBytes = 512;

    public const int HeaderByteLength = 9;

    public void Serialize(IBufferWriter<byte> writer)
    {
        // Encode text first so we know the actual byte count (UTF-8 may be
        // longer than .Length for non-ASCII macros).
        var rawBytes = Encoding.UTF8.GetBytes(Text ?? string.Empty);
        int textBytes = Math.Min(rawBytes.Length, MaxTextBytes);
        int total = HeaderByteLength + textBytes;
        var span = writer.GetSpan(total);
        span[0] = (byte)MsgType.CwEngineStatus;
        span[1] = (byte)State;
        // Clamp Wpm / QueueDepth to u16 so a logic bug upstream can't write
        // a wider integer and silently truncate at the bit boundary.
        ushort wpmU16 = (ushort)Math.Clamp(Wpm, 0, ushort.MaxValue);
        ushort depthU16 = (ushort)Math.Clamp(QueueDepth, 0, ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(2, 2), wpmU16);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(4, 2), depthU16);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(6, 2), (ushort)textBytes);
        if (textBytes > 0)
            rawBytes.AsSpan(0, textBytes).CopyTo(span.Slice(HeaderByteLength, textBytes));
        writer.Advance(total);
    }

    public static CwEngineStatusFrame Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderByteLength)
            throw new InvalidDataException(
                $"CwEngineStatusFrame requires ≥{HeaderByteLength} bytes, got {bytes.Length}");
        if (bytes[0] != (byte)MsgType.CwEngineStatus)
            throw new InvalidDataException(
                $"expected CwEngineStatus (0x{(byte)MsgType.CwEngineStatus:X2}), got 0x{bytes[0]:X2}");
        var state = (CwEngineState)bytes[1];
        int wpm = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(2, 2));
        int depth = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(4, 2));
        int textLen = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(6, 2));
        if (HeaderByteLength + textLen > bytes.Length)
            throw new InvalidDataException(
                $"CwEngineStatusFrame textLen {textLen} exceeds payload");
        string text = textLen == 0
            ? string.Empty
            : Encoding.UTF8.GetString(bytes.Slice(HeaderByteLength, textLen));
        return new CwEngineStatusFrame(state, wpm, depth, text);
    }

    /// <summary>Lift a <see cref="CwEngineStatus"/> into the wire shape.</summary>
    public static CwEngineStatusFrame FromStatus(CwEngineStatus s) =>
        new(s.State, s.Wpm, s.QueueDepth, s.Text ?? string.Empty);
}
