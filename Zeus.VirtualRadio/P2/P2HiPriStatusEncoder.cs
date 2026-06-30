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
using Zeus.VirtualRadio.Rf;

namespace Zeus.VirtualRadio.P2;

/// <summary>
/// Encodes the Protocol-2 hi-priority status packet (UDP source port 1025) —
/// the inverse of <c>Protocol2Client.DecodeHiPriStatus</c>. A 4-byte BE u32
/// sequence prefixes the packet; the status body begins at byte 4, so the
/// decoder's payload-relative offsets map to absolute offsets as
/// <c>absolute = 4 + payload</c>:
/// <list type="bullet">
///   <item>[4] status — bit 0 PTT, bit 4 PLL locked</item>
///   <item>[5] ADC overload bits</item>
///   <item>[6..7] exciter power ADC (BE u16)</item>
///   <item>[14..15] PA forward power ADC (BE u16)</item>
///   <item>[22..23] PA reverse power ADC (BE u16)</item>
/// </list>
/// FWD/REV are PTT-gated in the firmware, so the caller passes an all-zero
/// telemetry reading at rest. Feeding this packet's body back through
/// <c>DecodeHiPriStatus</c> recovers the same fields — the anti-drift guard.
/// </summary>
internal sealed class P2HiPriStatusEncoder
{
    /// <summary>
    /// Build a hi-priority status packet into a freshly-allocated buffer of
    /// <see cref="P2Wire.HiPriStatusPacketLength"/> bytes.
    /// </summary>
    /// <param name="sequence">Monotonic packet sequence (BE u32 at [0..3]).</param>
    /// <param name="telemetry">FWD/REF ADC counts (PTT-gated upstream — all-zero at rest).</param>
    /// <param name="ptt">Whether the radio is keyed (status bit 0).</param>
    /// <param name="pllLocked">PLL-lock flag (status bit 4) — true on a healthy radio.</param>
    /// <param name="adcOverloadBits">ADC overload byte ([5]); 0 = no overload.</param>
    public byte[] Build(uint sequence, in RfTelemetry telemetry, bool ptt, bool pllLocked, byte adcOverloadBits)
    {
        var p = new byte[P2Wire.HiPriStatusPacketLength];
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(0, 4), sequence);

        // Status byte (absolute [4]): bit 0 PTT, bit 4 PLL locked.
        byte status = 0;
        if (ptt) status |= 0x01;
        if (pllLocked) status |= 0x10;
        p[4] = status;

        // ADC overload (absolute [5]).
        p[5] = adcOverloadBits;

        // Exciter power ADC (absolute [6..7]) — mirror the forward count so an
        // observer sees the exciter move with drive while keyed.
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(6, 2), telemetry.FwdAdc);

        // PA forward power ADC (absolute [14..15]).
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(14, 2), telemetry.FwdAdc);

        // PA reverse power ADC (absolute [22..23]).
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(22, 2), telemetry.RefAdc);

        return p;
    }
}
