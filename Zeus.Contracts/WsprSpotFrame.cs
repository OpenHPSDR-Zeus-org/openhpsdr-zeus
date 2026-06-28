// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// WsprSpotFrame (MsgType 0x39) — server→client push of one 120 s WSPR slot's
// decoded spots for one receiver. JSON envelope, mirroring Ft8DecodeFrame
// (0x38): the rate is very low (once per 2 min) and the per-spot shape is rich.
// The WSPR workspace renders these into its spot table. WSPR is a beacon mode —
// there is no QSO sequencing, just received-spot reporting.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zeus.Contracts;

/// <summary>One decoded WSPR spot for the wire/UI. Message text is
/// "&lt;callsign&gt; &lt;grid4&gt; &lt;dBm&gt;".</summary>
public sealed record WsprSpotDto(
    float SnrDb,
    float DtSec,
    float FreqMhz,
    int DriftHz,
    string Message);

/// <summary>All spots from one completed 120 s slot on one receiver.</summary>
public sealed record WsprSpotBatchDto(
    int Receiver,
    long SlotStartUnixMs,
    double DialFreqMhz,
    IReadOnlyList<WsprSpotDto> Spots);

/// <summary>Wire codec for the WSPR spot-batch frame (MsgType 0x39).</summary>
public static class WsprSpotFrame
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Serialise a batch to a 0x39 frame ([type:1][UTF-8 JSON]).</summary>
    public static byte[] Encode(WsprSpotBatchDto batch)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(batch, JsonOptions);
        var frame = new byte[1 + json.Length];
        frame[0] = (byte)MsgType.WsprSpot;
        json.CopyTo(frame, 1);
        return frame;
    }

    /// <summary>Decode a 0x39 frame back to a batch (for tests / the frontend mirror).</summary>
    public static WsprSpotBatchDto Decode(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 1)
            throw new InvalidDataException("WsprSpot frame is empty");
        if (frame[0] != (byte)MsgType.WsprSpot)
            throw new InvalidDataException(
                $"expected WsprSpot (0x{(byte)MsgType.WsprSpot:X2}), got 0x{frame[0]:X2}");
        var batch = JsonSerializer.Deserialize<WsprSpotBatchDto>(frame[1..], JsonOptions);
        return batch ?? throw new InvalidDataException("WsprSpot payload did not deserialise");
    }
}
