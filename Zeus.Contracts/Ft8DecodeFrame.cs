// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Ft8DecodeFrame (MsgType 0x38) — server→client push of one UTC slot's worth of
// FT8/FT4 decodes for one receiver. JSON envelope (like ChatEvent 0x35) since
// the rate is low (once per 15 s FT8 / 7.5 s FT4 slot) and the per-decode shape
// is richer than the fixed-binary telemetry frames. The FT8 workspace renders
// these into its decode table.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zeus.Contracts;

/// <summary>One decoded FT8/FT4 message line for the wire/UI.</summary>
public sealed record Ft8DecodeDto(
    float SnrDb,
    float DtSec,
    float FreqHz,
    int Score,
    string Text);

/// <summary>All decodes from one completed slot on one receiver.</summary>
public sealed record Ft8DecodeBatchDto(
    int Receiver,
    long SlotStartUnixMs,
    string Protocol,                 // "FT8" | "FT4"
    IReadOnlyList<Ft8DecodeDto> Decodes);

/// <summary>Request body for POST /api/ft8/enable.</summary>
public sealed record Ft8EnableRequest(
    int? Receiver,
    string? Protocol,                // "FT8" | "FT4" (default FT8)
    int? Passes);                    // 1 = NORMAL, &gt;1 = DEEP/MULTI

/// <summary>Request body for POST /api/wspr/enable.</summary>
public sealed record WsprEnableRequest(
    int? Receiver,
    double? DialFreqMhz);            // transceiver dial freq, e.g. 14.0956 (20 m)

/// <summary>Wire codec for the FT8 decode-batch frame (MsgType 0x38).</summary>
public static class Ft8DecodeFrame
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Serialise a batch to a 0x38 frame ([type:1][UTF-8 JSON]).</summary>
    public static byte[] Encode(Ft8DecodeBatchDto batch)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(batch, JsonOptions);
        var frame = new byte[1 + json.Length];
        frame[0] = (byte)MsgType.Ft8Decode;
        json.CopyTo(frame, 1);
        return frame;
    }

    /// <summary>Decode a 0x38 frame back to a batch (for tests / the frontend mirror).</summary>
    public static Ft8DecodeBatchDto Decode(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 1)
            throw new InvalidDataException("Ft8Decode frame is empty");
        if (frame[0] != (byte)MsgType.Ft8Decode)
            throw new InvalidDataException(
                $"expected Ft8Decode (0x{(byte)MsgType.Ft8Decode:X2}), got 0x{frame[0]:X2}");
        var batch = JsonSerializer.Deserialize<Ft8DecodeBatchDto>(frame[1..], JsonOptions);
        return batch ?? throw new InvalidDataException("Ft8Decode payload did not deserialise");
    }
}
