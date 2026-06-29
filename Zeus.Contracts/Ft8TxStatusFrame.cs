// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Ft8TxStatusFrame (MsgType 0x3A) — server→client push of the FT8/FT4/WSPR TX
// keyer state. JSON envelope, mirroring Ft8DecodeFrame (0x38): low rate (only on
// arm/stage/transmit edges) and the shape is small. One DTO serves both the
// FT8/FT4 auto-sequence keyer (Slot = "even"/"odd") and the WSPR beacon
// (Slot = "", WatchdogSecsRemaining still meaningful). The workspace renders the
// arm/transmit lamps from this so the UI tracks what the backend ACTUALLY keyed,
// not just what the operator staged.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zeus.Contracts;

/// <summary>The live state of the digital-mode TX keyer for the wire/UI.</summary>
public sealed record Ft8TxStatusDto(
    bool Armed,                   // ENABLE-TX master; defaults false, never auto-set
    bool Transmitting,            // true while a slot is being keyed on the air
    string Mode,                  // "FT8" | "FT4" | "WSPR"
    string? Message,              // currently staged / transmitting message, if any
    int AudioHz,                  // TX audio offset of tone 0
    string Slot,                  // "even" | "odd" (FT8/FT4); "" for WSPR
    int WatchdogSecsRemaining,    // seconds until the unattended watchdog disarms (0 = disarmed)
    long? LastTxSlotMs,           // unix-ms of the last slot the keyer transmitted, if any
    bool NativeAvailable);        // encode/synth path present on this platform

/// <summary>Wire codec for the digital TX-status frame (MsgType 0x3A).</summary>
public static class Ft8TxStatusFrame
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Serialise a status to a 0x3A frame ([type:1][UTF-8 JSON]).</summary>
    public static byte[] Encode(Ft8TxStatusDto status)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(status, JsonOptions);
        var frame = new byte[1 + json.Length];
        frame[0] = (byte)MsgType.Ft8TxStatus;
        json.CopyTo(frame, 1);
        return frame;
    }

    /// <summary>Decode a 0x3A frame back to a status (for tests / the frontend mirror).</summary>
    public static Ft8TxStatusDto Decode(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 1)
            throw new InvalidDataException("Ft8TxStatus frame is empty");
        if (frame[0] != (byte)MsgType.Ft8TxStatus)
            throw new InvalidDataException(
                $"expected Ft8TxStatus (0x{(byte)MsgType.Ft8TxStatus:X2}), got 0x{frame[0]:X2}");
        var status = JsonSerializer.Deserialize<Ft8TxStatusDto>(frame[1..], JsonOptions);
        return status ?? throw new InvalidDataException("Ft8TxStatus payload did not deserialise");
    }
}
