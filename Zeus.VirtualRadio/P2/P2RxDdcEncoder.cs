// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Buffers.Binary;

namespace Zeus.VirtualRadio.P2;

/// <summary>
/// Encodes the two shapes a DDC0 data packet (UDP source port 1035) takes — the
/// inverse of <c>Protocol2Client.HandleDdcPacket</c> (plain user RX) and
/// <c>Protocol2Client.HandlePsPairedPacket</c> (PureSignal single-ADC time-mux
/// feedback). Both are 1444 bytes with a BE u32 sequence at [0..3].
///
/// Plain RX: 238 complex samples, interleaved int24-BE I/Q from byte 16.
///
/// PS feedback (only emitted while the host's CmdRx byte 1363 <c>Mux</c> bit is
/// set, i.e. an armed TX burst): a samplesPerFrame=238 marker at [14..15] then
/// 119 12-byte pairs from byte 16. Per pair, COUPLER FIRST (DDC0 → pscc's "rx",
/// the post-PA feedback) then REFERENCE SECOND (DDC1 → pscc's "tx", the clean
/// TX-DAC loopback) — the exact order <c>Protocol2Client.DecodePsPairForTest</c>
/// expects, so the de-interleave is reused unchanged.
/// </summary>
internal sealed class P2RxDdcEncoder
{
    /// <summary>Plain user-RX samples per packet.</summary>
    public const int RxSamplesPerPacket = P2Wire.RxSamplesPerPacket;   // 238

    /// <summary>PS feedback sample-pairs per packet.</summary>
    public const int PsPairsPerPacket = P2Wire.PsPairsPerPacket;       // 119

    /// <summary>
    /// Encode a plain user-RX DDC packet from <paramref name="interleavedIq"/>
    /// (<c>[I0,Q0,…]</c>, full-scale units where 1.0 = 0 dBFS, ≥
    /// <c>2 × <see cref="RxSamplesPerPacket"/></c> doubles).
    /// </summary>
    public void EncodeRxIq(Span<byte> packet, uint sequence, ReadOnlySpan<double> interleavedIq)
    {
        if (packet.Length != P2Wire.BufLen)
            throw new ArgumentException($"packet must be exactly {P2Wire.BufLen} bytes", nameof(packet));
        int needed = 2 * RxSamplesPerPacket;
        if (interleavedIq.Length < needed)
            throw new ArgumentException($"interleavedIq must hold at least {needed} doubles", nameof(interleavedIq));

        packet.Clear();
        BinaryPrimitives.WriteUInt32BigEndian(packet.Slice(0, 4), sequence);
        // samplesPerFrame marker — the plain-RX decoder uses a fixed 238 and
        // ignores this, but writing it keeps both packet shapes self-describing.
        BinaryPrimitives.WriteUInt16BigEndian(
            packet.Slice(P2Wire.PsSamplesPerFrameOffset, 2), P2Wire.RxSamplesPerPacket);

        for (int s = 0; s < RxSamplesPerPacket; s++)
        {
            int off = P2Wire.RxPayloadOffset + s * P2Wire.RxSampleStride;
            WriteInt24BigEndian(packet.Slice(off, 3), ToInt24(interleavedIq[2 * s]));
            WriteInt24BigEndian(packet.Slice(off + 3, 3), ToInt24(interleavedIq[2 * s + 1]));
        }
    }

    /// <summary>
    /// Encode a PureSignal feedback packet. <paramref name="couplerIq"/> is the
    /// post-PA feedback (becomes pscc's "rx"); <paramref name="referenceIq"/> is
    /// the clean TX-DAC loopback (becomes pscc's "tx"). Both are interleaved
    /// <c>[I0,Q0,…]</c> with ≥ <c>2 × <see cref="PsPairsPerPacket"/></c> doubles.
    /// </summary>
    public void EncodePsFeedback(
        Span<byte> packet, uint sequence,
        ReadOnlySpan<double> couplerIq, ReadOnlySpan<double> referenceIq)
    {
        if (packet.Length != P2Wire.BufLen)
            throw new ArgumentException($"packet must be exactly {P2Wire.BufLen} bytes", nameof(packet));
        int needed = 2 * PsPairsPerPacket;
        if (couplerIq.Length < needed)
            throw new ArgumentException($"couplerIq must hold at least {needed} doubles", nameof(couplerIq));
        if (referenceIq.Length < needed)
            throw new ArgumentException($"referenceIq must hold at least {needed} doubles", nameof(referenceIq));

        packet.Clear();
        BinaryPrimitives.WriteUInt32BigEndian(packet.Slice(0, 4), sequence);
        // samplesPerFrame = 238 → 119 pairs (Protocol2Client.HandlePsPairedPacket
        // reads this at [14..15] and computes pairs = 238/2).
        BinaryPrimitives.WriteUInt16BigEndian(
            packet.Slice(P2Wire.PsSamplesPerFrameOffset, 2), P2Wire.PsSamplesPerFrame);

        for (int i = 0; i < PsPairsPerPacket; i++)
        {
            int off = P2Wire.PsPayloadOffset + i * P2Wire.PsPairStride;
            // DDC0 (coupler → rx) first.
            WriteInt24BigEndian(packet.Slice(off, 3), ToInt24(couplerIq[2 * i]));
            WriteInt24BigEndian(packet.Slice(off + 3, 3), ToInt24(couplerIq[2 * i + 1]));
            // DDC1 (reference → tx) second.
            WriteInt24BigEndian(packet.Slice(off + 6, 3), ToInt24(referenceIq[2 * i]));
            WriteInt24BigEndian(packet.Slice(off + 9, 3), ToInt24(referenceIq[2 * i + 1]));
        }
    }

    private static int ToInt24(double value)
    {
        double scaled = Math.Round(value * P2Wire.Int24FullScale);
        if (scaled > P2Wire.Int24Max) return P2Wire.Int24Max;
        if (scaled < P2Wire.Int24Min) return P2Wire.Int24Min;
        return (int)scaled;
    }

    private static void WriteInt24BigEndian(Span<byte> dest, int value)
    {
        dest[0] = (byte)((value >> 16) & 0xFF);
        dest[1] = (byte)((value >> 8) & 0xFF);
        dest[2] = (byte)(value & 0xFF);
    }
}
