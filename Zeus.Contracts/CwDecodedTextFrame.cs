// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Zeus.Contracts;

/// <summary>
/// Wire frame for server-side CW receive decoding. Broadcast by
/// CwDecoderService whenever the decoder emits one or more characters from the
/// demodulated RX audio while the radio is in a CW mode. Format:
///
/// <code>
/// [type:1=0x31][wpm:u16 LE][snrDb:f32 LE][confidence:f32 LE]
/// [textLen:u16 LE][text:UTF-8 textLen bytes]
/// </code>
///
/// 13-byte fixed header + variable text payload. <see cref="Text"/> is the
/// chunk of characters decoded during one audio tick (usually 0–2 chars,
/// including ' ' on a word gap); the client appends them in order. Decoding
/// happens server-side so it works in the desktop/native-audio host and
/// headless — see CwDecoderService. Text is capped at <see cref="MaxTextBytes"/>.
///
/// Wire-frozen: future additions append-only at the tail.
/// </summary>
public readonly record struct CwDecodedTextFrame(
    string Text,
    int Wpm,
    float SnrDb,
    float Confidence)
{
    /// <summary>Hard cap on the text payload. One tick decodes a handful of
    /// characters at most, so this is comfortably generous and keeps the
    /// frame well under one MTU.</summary>
    public const int MaxTextBytes = 256;

    public const int HeaderByteLength = 13; // type(1) + wpm(2) + snr(4) + conf(4) + textLen(2)

    public void Serialize(IBufferWriter<byte> writer)
    {
        // Encode text first so we know the actual UTF-8 byte count.
        var rawBytes = Encoding.UTF8.GetBytes(Text ?? string.Empty);
        int textBytes = Math.Min(rawBytes.Length, MaxTextBytes);
        int total = HeaderByteLength + textBytes;
        var span = writer.GetSpan(total);
        span[0] = (byte)MsgType.CwDecodedText;
        // Clamp Wpm to u16 so an upstream logic bug can't silently truncate.
        ushort wpmU16 = (ushort)Math.Clamp(Wpm, 0, ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(1, 2), wpmU16);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(3, 4), SnrDb);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(7, 4), Confidence);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(11, 2), (ushort)textBytes);
        if (textBytes > 0)
            rawBytes.AsSpan(0, textBytes).CopyTo(span.Slice(HeaderByteLength, textBytes));
        writer.Advance(total);
    }

    public static CwDecodedTextFrame Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderByteLength)
            throw new InvalidDataException(
                $"CwDecodedTextFrame requires ≥{HeaderByteLength} bytes, got {bytes.Length}");
        if (bytes[0] != (byte)MsgType.CwDecodedText)
            throw new InvalidDataException(
                $"expected CwDecodedText (0x{(byte)MsgType.CwDecodedText:X2}), got 0x{bytes[0]:X2}");
        int wpm = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(1, 2));
        float snr = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(3, 4));
        float conf = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(7, 4));
        int textLen = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(11, 2));
        if (HeaderByteLength + textLen > bytes.Length)
            throw new InvalidDataException(
                $"CwDecodedTextFrame textLen {textLen} exceeds payload");
        string text = textLen == 0
            ? string.Empty
            : Encoding.UTF8.GetString(bytes.Slice(HeaderByteLength, textLen));
        return new CwDecodedTextFrame(text, wpm, snr, conf);
    }
}
