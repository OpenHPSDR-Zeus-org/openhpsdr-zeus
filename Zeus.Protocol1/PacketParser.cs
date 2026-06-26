// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Buffers;
using System.Buffers.Binary;

namespace Zeus.Protocol1;

/// <summary>
/// EP6 C&amp;C echo slot that carries one of the HL2/Hermes ADC pairs. The raw
/// <paramref name="C0Address"/> byte is preserved (status bits in [2:0] + address
/// in [7:3]) so consumers can disambiguate which physical ADC each u16 represents.
/// <para>
/// The slot-to-ADC mapping on HL2 is:
///   addr=1 (C0=0x08): Ain0 = exciter_pwr / HL2 temperature;   Ain1 = alex_forward_power
///   addr=2 (C0=0x10): Ain0 = alex_reverse_power;              Ain1 = ADC0 bias
///   addr=3 (C0=0x18): Ain0 = ADC1 bias;                        Ain1 = 0 (unused)
/// In each case Ain0 is the BE u16 at C1..C2 and Ain1 is the BE u16 at C3..C4.
/// </para>
/// </summary>
public readonly record struct TelemetryReading(byte C0Address, ushort Ain0, ushort Ain1);

/// <summary>
/// Per-packet ADC overload flags extracted from the echoed C&amp;C word
/// (C1[0] = ADC0, C2[0] = ADC1). OR-accumulated across both USB frames of
/// one EP6 packet so a single set frame is enough to report overload.
/// </summary>
public readonly record struct AdcOverloadStatus(bool Adc0, bool Adc1)
{
    public bool AnyOverload => Adc0 || Adc1;

    public static AdcOverloadStatus FromBits(byte bits) =>
        new((bits & 0x01) != 0, (bits & 0x02) != 0);
}

/// <summary>
/// Pure, allocation-free parser for Metis EP6 RX IQ packets.
/// Kept static so unit tests exercise the wire format without sockets.
/// </summary>
internal static class PacketParser
{
    public const int PacketLength = 1032;
    public const int UsbFrameLength = 512;
    public const int MetisHeaderLength = 8;
    public const int UsbHeaderLength = 8;       // 3-byte sync + 5-byte C&C
    public const int UsbPayloadLength = 504;    // 512 − 8
    public const int BytesPerSampleGroup = 8;   // 3 I + 3 Q + 2 mic
    public const int ComplexSamplesPerUsbFrame = UsbPayloadLength / BytesPerSampleGroup; // 63
    public const int ComplexSamplesPerPacket = ComplexSamplesPerUsbFrame * 2;            // 126

    private const byte Sync = 0x7F;
    private const byte MetisMagic0 = 0xEF;
    private const byte MetisMagic1 = 0xFE;
    private const byte MetisEp6 = 0x06;
    private const byte MetisTypeDataFrame = 0x01;

    private const double Int24Scale = 1.0 / 8_388_608.0; // 1 / 2^23

    /// <summary>
    /// Extract the hardware-PTT echo bit from a parsed EP6 packet — C0[0] of
    /// each USB frame OR'd together. The HL2 gateware sets this whenever the
    /// rear KEY tip is grounded or an external PTT line is asserted (HL2
    /// protocol doc §"Hermes Lite 2-specific behaviour", line 200): "When the
    /// tip is grounded, both C0[0] and C0[2] are sent as one." Same wire bit
    /// also echoes a host-driven MOX request, so consumers MUST gate on the
    /// host's current MOX state to disambiguate "external key" from "echo of
    /// our own outbound MOX".
    /// </summary>
    /// <returns>true if either USB frame's C0[0] is set, false otherwise.
    /// Returns false for packets shorter than expected (the only caller has
    /// already validated length via <see cref="TryParsePacket(System.ReadOnlySpan{byte},System.Span{double},out uint,out int)"/>).</returns>
    public static bool ExtractHardwarePtt(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < PacketLength) return false;
        // usb[3] is c0; bit 0 is the PTT/MOX echo.
        byte c0Frame0 = packet[MetisHeaderLength + 3];
        byte c0Frame1 = packet[MetisHeaderLength + UsbFrameLength + 3];
        return ((c0Frame0 | c0Frame1) & 0x01) != 0;
    }

    /// <summary>
    /// Extract the CW key-down state from a parsed EP6 packet — C0[2] of each
    /// USB frame OR'd together. This is the gateware's *shaped keyer output*
    /// (HL2 response slot: <c>{3'b000, resp_addr[1:0], cw_key_status, 1'b0,
    /// ptt_resp}</c>, so <c>cw_key_status = C0[2]</c>), which toggles per
    /// dit/dah — NOT the level-held <c>ptt_resp</c> at C0[0] that
    /// <see cref="ExtractHardwarePtt"/> reads. Used to drive the local CW
    /// sidetone so it follows individual elements instead of staying on for
    /// the whole keyed period + hang time. HL2 protocol doc: "C0[2] follows
    /// the signal at the tip, and a shaped CW signal is generated." (zeus-cl2)
    /// </summary>
    public static bool ExtractCwKeyDown(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < PacketLength) return false;
        byte c0Frame0 = packet[MetisHeaderLength + 3];
        byte c0Frame1 = packet[MetisHeaderLength + UsbFrameLength + 3];
        return ((c0Frame0 | c0Frame1) & 0x04) != 0;
    }

    /// <summary>
    /// Read a 24-bit big-endian signed integer with sign-extension to int32.
    /// </summary>
    public static int ReadInt24BigEndian(ReadOnlySpan<byte> b)
    {
        // high byte is signed — shifts preserve sign. Middle/low are unsigned.
        int v = ((sbyte)b[0]) << 16;
        v |= b[1] << 8;
        v |= b[2];
        return v;
    }

    /// <summary>
    /// Scale a signed int24 sample to a double in [-1.0, +1.0].
    /// </summary>
    public static double ScaleInt24(int sample) => sample * Int24Scale;

    /// <summary>
    /// Parse an EP6 RX IQ packet.
    /// </summary>
    /// <param name="packet">1032-byte Metis data frame.</param>
    /// <param name="interleavedOut">
    /// Destination buffer; must be ≥ <c>2 × <see cref="ComplexSamplesPerPacket"/></c>
    /// entries long. Populated as <c>[I0,Q0,I1,Q1,…]</c>.
    /// </param>
    /// <param name="sequence">Radio-assigned monotonic seq (BE uint32 at bytes 4..7).</param>
    /// <param name="complexSamples">Number of I/Q pairs written to <paramref name="interleavedOut"/>.</param>
    /// <returns>true on valid packet; false on bad magic, wrong length, bad sync, or wrong endpoint.</returns>
    public static bool TryParsePacket(
        ReadOnlySpan<byte> packet,
        Span<double> interleavedOut,
        out uint sequence,
        out int complexSamples)
        => TryParsePacket(packet, interleavedOut, out sequence, out complexSamples, out _, out _);

    /// <summary>
    /// Back-compat overload that exposes a single telemetry reading. When both
    /// USB frames in a packet carry AIN-bearing addresses, the second is
    /// returned (legacy "last wins" semantics) — callers that need both must
    /// use the 7-arg overload below.
    /// </summary>
    public static bool TryParsePacket(
        ReadOnlySpan<byte> packet,
        Span<double> interleavedOut,
        out uint sequence,
        out int complexSamples,
        out TelemetryReading telemetry)
    {
        bool ok = TryParsePacket(packet, interleavedOut, out sequence, out complexSamples,
            out var t0, out var t1, out _);
        telemetry = t1.C0Address != 0 ? t1 : t0;
        return ok;
    }

    /// <summary>
    /// 6-arg back-compat overload without per-frame telemetry: collapses both
    /// frames into one (last-wins).
    /// </summary>
    public static bool TryParsePacket(
        ReadOnlySpan<byte> packet,
        Span<double> interleavedOut,
        out uint sequence,
        out int complexSamples,
        out TelemetryReading telemetry,
        out byte adcOverloadBits)
    {
        bool ok = TryParsePacket(packet, interleavedOut, out sequence, out complexSamples,
            out var t0, out var t1, out adcOverloadBits);
        telemetry = t1.C0Address != 0 ? t1 : t0;
        return ok;
    }

    /// <summary>
    /// Parse an EP6 RX IQ packet. Emits telemetry independently for each of the
    /// two USB frames — Protocol-1 dispatches per frame, so each frame's C&amp;C
    /// switch contributes its own ADC reading.
    /// An empty reading is indicated by <see cref="TelemetryReading.C0Address"/> == 0
    /// — valid echoes always carry a non-zero addr byte (0x08 / 0x10 / 0x18,
    /// possibly OR'd with C0[0]=MOX echo).
    /// </summary>
    /// <param name="adcOverloadBits">
    /// Bit 0 = ADC0 overload, bit 1 = ADC1 overload. OR of both USB frames so a
    /// single set frame reports the overload for the whole packet.
    /// </param>
    public static bool TryParsePacket(
        ReadOnlySpan<byte> packet,
        Span<double> interleavedOut,
        out uint sequence,
        out int complexSamples,
        out TelemetryReading telemetry0,
        out TelemetryReading telemetry1,
        out byte adcOverloadBits)
    {
        sequence = 0;
        complexSamples = 0;
        telemetry0 = default;
        telemetry1 = default;
        adcOverloadBits = 0;

        if (packet.Length != PacketLength) return false;
        if (packet[0] != MetisMagic0 || packet[1] != MetisMagic1) return false;
        if (packet[2] != MetisTypeDataFrame) return false;
        if (packet[3] != MetisEp6) return false;

        sequence = BinaryPrimitives.ReadUInt32BigEndian(packet[4..8]);

        int needed = 2 * ComplexSamplesPerPacket;
        if (interleavedOut.Length < needed) return false;

        int written = 0;
        for (int frame = 0; frame < 2; frame++)
        {
            int frameStart = MetisHeaderLength + frame * UsbFrameLength;
            ReadOnlySpan<byte> usb = packet.Slice(frameStart, UsbFrameLength);
            if (usb[0] != Sync || usb[1] != Sync || usb[2] != Sync) return false;

            // usb[3..8] are the echoed C&C bytes. addr = (C0 >> 3) & 0x1F;
            // addresses 1/2/3 carry the ADC pairs we want. Each USB frame is
            // an independent reading.
            byte c0 = usb[3];
            int addr = (c0 >> 3) & 0x1F;
            if (addr is 1 or 2 or 3)
            {
                var reading = new TelemetryReading(
                    C0Address: c0,
                    Ain0: BinaryPrimitives.ReadUInt16BigEndian(usb.Slice(4, 2)),
                    Ain1: BinaryPrimitives.ReadUInt16BigEndian(usb.Slice(6, 2)));
                if (frame == 0) telemetry0 = reading;
                else telemetry1 = reading;
            }

            adcOverloadBits |= (byte)(usb[4] & 0x01);        // C1[0] → bit 0 (ADC0)
            adcOverloadBits |= (byte)((usb[5] & 0x01) << 1); // C2[0] → bit 1 (ADC1)

            ReadOnlySpan<byte> payload = usb[UsbHeaderLength..];
            for (int g = 0; g < ComplexSamplesPerUsbFrame; g++)
            {
                int off = g * BytesPerSampleGroup;
                int i = ReadInt24BigEndian(payload.Slice(off, 3));
                int q = ReadInt24BigEndian(payload.Slice(off + 3, 3));
                // off+6, off+7 carry the 16-bit mic / line-in sample. Extracted on
                // demand by ExtractMicSamples when a radio-mic relay handler is
                // attached — used to forward the codec input back to TX (issue #992).
                interleavedOut[written++] = ScaleInt24(i);
                interleavedOut[written++] = ScaleInt24(q);
            }
        }

        complexSamples = ComplexSamplesPerPacket;
        return true;
    }

    /// <summary>
    /// Extract the 126 codec mic / line-in samples (int16 big-endian) from a
    /// standard 1-DDC EP6 packet. The 2 mic bytes live at offsets 6..7 of every
    /// 8-byte sample group (3I + 3Q + 2 mic). Per the original HPSDR USB protocol
    /// (V1.58 §"The balance of the frame…", NOTE 2), the mic stream is always
    /// 48 kHz regardless of the IQ rate — at 96/192/384 kHz IQ rates the same
    /// mic sample is duplicated N = rate/48 kHz times across consecutive sample
    /// groups, and consumers are expected to decimate. The decimation is the
    /// receiver's job; this method just emits the raw 126-sample group, in
    /// USB-frame order.
    /// </summary>
    /// <param name="packet">1032-byte Metis EP6 data frame (same format
    /// <see cref="TryParsePacket(System.ReadOnlySpan{byte},System.Span{double},out uint,out int)"/>
    /// accepts).</param>
    /// <param name="micOut">Destination buffer; must be ≥ 126 entries long.</param>
    /// <returns>Number of mic samples written (126 on success, 0 on bad header /
    /// sync / short packet).</returns>
    public static int ExtractMicSamples(ReadOnlySpan<byte> packet, Span<short> micOut)
    {
        if (packet.Length != PacketLength) return 0;
        if (packet[0] != MetisMagic0 || packet[1] != MetisMagic1) return 0;
        if (packet[2] != MetisTypeDataFrame) return 0;
        if (packet[3] != MetisEp6) return 0;
        if (micOut.Length < ComplexSamplesPerPacket) return 0;

        int written = 0;
        for (int frame = 0; frame < 2; frame++)
        {
            int frameStart = MetisHeaderLength + frame * UsbFrameLength;
            ReadOnlySpan<byte> usb = packet.Slice(frameStart, UsbFrameLength);
            if (usb[0] != Sync || usb[1] != Sync || usb[2] != Sync) return 0;

            ReadOnlySpan<byte> payload = usb[UsbHeaderLength..];
            for (int g = 0; g < ComplexSamplesPerUsbFrame; g++)
            {
                int off = g * BytesPerSampleGroup;
                micOut[written++] = BinaryPrimitives.ReadInt16BigEndian(payload.Slice(off + 6, 2));
            }
        }
        return written;
    }

    /// <summary>
    /// Convenience overload that rents a <c>double[]</c> of the exact required
    /// size from <see cref="ArrayPool{Double}.Shared"/>. Returns <c>null</c> on
    /// parse failure; the caller owns the rented buffer on success.
    /// </summary>
    public static double[]? TryParsePacketRented(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out int complexSamples)
    {
        var buffer = ArrayPool<double>.Shared.Rent(2 * ComplexSamplesPerPacket);
        if (!TryParsePacket(packet, buffer, out sequence, out complexSamples))
        {
            ArrayPool<double>.Shared.Return(buffer);
            return null;
        }
        return buffer;
    }

    // ---- HL2 PureSignal 4-DDC layout (mi0bot canonical) -------------------
    // mi0bot's HL2 path always uses nddc=4 during PS+MOX (console.cs:8186-
    // 8265, networkproto1.c:WriteMainLoop_HL2 case 5/6). The EP6 packet
    // sample-time-slot is then 26 bytes:
    //   3I+3Q DDC0 (RX1 audio at RX freq, operator's listening)
    //   3I+3Q DDC1 (RX2)
    //   3I+3Q DDC2 (PS-RX feedback at TX freq, mapped to feedback ADC) → pscc rx
    //   3I+3Q DDC3 (PS-TX reference at TX freq, mapped to feedback ADC) → pscc tx
    //   2 bytes mic (HL2 has no codec, ignored)
    // 504 / 26 = 19 sample slots per USB-frame; 38 paired samples per packet
    // per DDC. cmaster.cs:8537-8538 wires psrx=2 / pstx=3 in the FOUR_DDC
    // routing — DDC2 → pscc "rx" (Irxbuff/Qrxbuff), DDC3 → pscc "tx"
    // (Itxbuff/Qtxbuff). DDC0 stays alive on the operator's RX freq the
    // entire time, so the panadapter and audio don't drop during PS+TX.
    public const int Hl2Ps4DdcBytesPerSlot = 26;        // 4 × (3I+3Q) + 2 mic
    public const int Hl2Ps4DdcSamplesPerUsbFrame = UsbPayloadLength / Hl2Ps4DdcBytesPerSlot; // 19
    public const int Hl2Ps4DdcSamplesPerPacket = Hl2Ps4DdcSamplesPerUsbFrame * 2;           // 38

    /// <summary>
    /// Decode the four interleaved DDC streams from an HL2 PS-armed 4-DDC
    /// EP6 packet. Each output buffer must have room for at least
    /// <c>2 × <see cref="Hl2Ps4DdcSamplesPerPacket"/></c> doubles
    /// (interleaved I/Q). Returns false on bad framing.
    ///
    /// Telemetry (FWD/REF/PA-temp via the C&amp;C echo) and ADC-overload bits
    /// are emitted on the same <c>usb[3..8]</c> bytes as the standard
    /// single-DDC layout — only the payload below the 8-byte USB header
    /// differs between the two parsers. Surfaced here because PS-armed
    /// TUN windows are routinely held for many seconds during HL2
    /// calibration; without this extraction the meter pipeline reads zero
    /// throughout PS+TUN even though the radio is keying.
    /// </summary>
    public static bool TryParseHl2Ps4DdcPacket(
        ReadOnlySpan<byte> packet,
        Span<double> ddc0Out, Span<double> ddc1Out,
        Span<double> ddc2Out, Span<double> ddc3Out,
        out uint sequence,
        out int complexSamples,
        out TelemetryReading telemetry0,
        out TelemetryReading telemetry1,
        out byte adcOverloadBits)
    {
        sequence = 0;
        complexSamples = 0;
        telemetry0 = default;
        telemetry1 = default;
        adcOverloadBits = 0;

        if (packet.Length != PacketLength) return false;
        if (packet[0] != MetisMagic0 || packet[1] != MetisMagic1) return false;
        if (packet[2] != MetisTypeDataFrame) return false;
        if (packet[3] != MetisEp6) return false;

        sequence = BinaryPrimitives.ReadUInt32BigEndian(packet[4..8]);

        int needed = 2 * Hl2Ps4DdcSamplesPerPacket;
        if (ddc0Out.Length < needed || ddc1Out.Length < needed) return false;
        if (ddc2Out.Length < needed || ddc3Out.Length < needed) return false;

        int wrote = 0;
        for (int frame = 0; frame < 2; frame++)
        {
            int frameStart = MetisHeaderLength + frame * UsbFrameLength;
            ReadOnlySpan<byte> usb = packet.Slice(frameStart, UsbFrameLength);
            if (usb[0] != Sync || usb[1] != Sync || usb[2] != Sync) return false;

            // Same C&C echo extraction as the standard 1-DDC parser
            // (TryParsePacket, line ~219). usb[3..8] is identical wire
            // format; only the payload below differs by layout.
            byte c0 = usb[3];
            int addr = (c0 >> 3) & 0x1F;
            if (addr is 1 or 2 or 3)
            {
                var reading = new TelemetryReading(
                    C0Address: c0,
                    Ain0: BinaryPrimitives.ReadUInt16BigEndian(usb.Slice(4, 2)),
                    Ain1: BinaryPrimitives.ReadUInt16BigEndian(usb.Slice(6, 2)));
                if (frame == 0) telemetry0 = reading;
                else telemetry1 = reading;
            }
            adcOverloadBits |= (byte)(usb[4] & 0x01);
            adcOverloadBits |= (byte)((usb[5] & 0x01) << 1);

            ReadOnlySpan<byte> payload = usb[UsbHeaderLength..];
            for (int g = 0; g < Hl2Ps4DdcSamplesPerUsbFrame; g++)
            {
                int off = g * Hl2Ps4DdcBytesPerSlot;
                int i0 = ReadInt24BigEndian(payload.Slice(off,      3));
                int q0 = ReadInt24BigEndian(payload.Slice(off + 3,  3));
                int i1 = ReadInt24BigEndian(payload.Slice(off + 6,  3));
                int q1 = ReadInt24BigEndian(payload.Slice(off + 9,  3));
                int i2 = ReadInt24BigEndian(payload.Slice(off + 12, 3));
                int q2 = ReadInt24BigEndian(payload.Slice(off + 15, 3));
                int i3 = ReadInt24BigEndian(payload.Slice(off + 18, 3));
                int q3 = ReadInt24BigEndian(payload.Slice(off + 21, 3));
                // off+24, off+25 = mic (ignored).
                ddc0Out[wrote]     = ScaleInt24(i0);
                ddc0Out[wrote + 1] = ScaleInt24(q0);
                ddc1Out[wrote]     = ScaleInt24(i1);
                ddc1Out[wrote + 1] = ScaleInt24(q1);
                ddc2Out[wrote]     = ScaleInt24(i2);
                ddc2Out[wrote + 1] = ScaleInt24(q2);
                ddc3Out[wrote]     = ScaleInt24(i3);
                ddc3Out[wrote + 1] = ScaleInt24(q3);
                wrote += 2;
            }
        }

        complexSamples = Hl2Ps4DdcSamplesPerPacket;
        return true;
    }

    // ---- Standard HPSDR dual-receiver layout (2 DDCs) ---------------------
    // When the host requests NumReceiversMinusOne=1, the radio interleaves two
    // receivers in the EP6 stream. This is the canonical Protocol-1 N-receiver
    // layout (Thetis networkproto1.c:358 — `spr = 504 / (6*nddc + 2)`,
    // `k = 8 + isample*(6*nddc+2) + iddc*6`): each sample time-slot is
    // (6*nddc + 2) bytes, here 6*2 + 2 = 14:
    //   3I+3Q DDC0 (RX1, operator's main receiver) — slot offset 0
    //   3I+3Q DDC1 (RX2, second receiver at its own NCO) — slot offset 6
    //   2 bytes mic (mic/line in; ignored for RX)
    // 504 / 14 = 36 sample slots per USB-frame; 72 paired samples per packet
    // per DDC. Used by ANAN/Orion-class dual-ADC boards (and Hermes) for a
    // basic non-diversity two-receiver session. NOTE: Orion diversity / 5-DDC
    // and PureSignal (4-DDC) configs remap DDC→RX differently — those are NOT
    // handled here (see TryParseHl2Ps4DdcPacket for the PS path).
    public const int TwoDdcBytesPerSlot = 14;        // 2 × (3I+3Q) + 2 mic
    public const int TwoDdcSamplesPerUsbFrame = UsbPayloadLength / TwoDdcBytesPerSlot; // 36
    public const int TwoDdcSamplesPerPacket = TwoDdcSamplesPerUsbFrame * 2;            // 72

    /// <summary>
    /// Decode the two interleaved DDC streams from a standard dual-receiver EP6
    /// packet (NumReceiversMinusOne=1). Each output buffer must have room for at
    /// least <c>2 × <see cref="TwoDdcSamplesPerPacket"/></c> doubles (interleaved
    /// I/Q). Returns false on bad framing. Telemetry + ADC-overload extraction
    /// mirrors the single-DDC parser (identical <c>usb[3..8]</c> wire format);
    /// only the per-sample payload layout differs.
    /// </summary>
    public static bool TryParse2DdcPacket(
        ReadOnlySpan<byte> packet,
        Span<double> ddc0Out, Span<double> ddc1Out,
        out uint sequence,
        out int complexSamples,
        out TelemetryReading telemetry0,
        out TelemetryReading telemetry1,
        out byte adcOverloadBits)
    {
        sequence = 0;
        complexSamples = 0;
        telemetry0 = default;
        telemetry1 = default;
        adcOverloadBits = 0;

        if (packet.Length != PacketLength) return false;
        if (packet[0] != MetisMagic0 || packet[1] != MetisMagic1) return false;
        if (packet[2] != MetisTypeDataFrame) return false;
        if (packet[3] != MetisEp6) return false;

        sequence = BinaryPrimitives.ReadUInt32BigEndian(packet[4..8]);

        int needed = 2 * TwoDdcSamplesPerPacket;
        if (ddc0Out.Length < needed || ddc1Out.Length < needed) return false;

        int wrote = 0;
        for (int frame = 0; frame < 2; frame++)
        {
            int frameStart = MetisHeaderLength + frame * UsbFrameLength;
            ReadOnlySpan<byte> usb = packet.Slice(frameStart, UsbFrameLength);
            if (usb[0] != Sync || usb[1] != Sync || usb[2] != Sync) return false;

            // Same C&C echo extraction as the single-DDC parser (TryParsePacket).
            byte c0 = usb[3];
            int addr = (c0 >> 3) & 0x1F;
            if (addr is 1 or 2 or 3)
            {
                var reading = new TelemetryReading(
                    C0Address: c0,
                    Ain0: BinaryPrimitives.ReadUInt16BigEndian(usb.Slice(4, 2)),
                    Ain1: BinaryPrimitives.ReadUInt16BigEndian(usb.Slice(6, 2)));
                if (frame == 0) telemetry0 = reading;
                else telemetry1 = reading;
            }
            adcOverloadBits |= (byte)(usb[4] & 0x01);
            adcOverloadBits |= (byte)((usb[5] & 0x01) << 1);

            ReadOnlySpan<byte> payload = usb[UsbHeaderLength..];
            for (int g = 0; g < TwoDdcSamplesPerUsbFrame; g++)
            {
                int off = g * TwoDdcBytesPerSlot;
                int i0 = ReadInt24BigEndian(payload.Slice(off,     3));
                int q0 = ReadInt24BigEndian(payload.Slice(off + 3, 3));
                int i1 = ReadInt24BigEndian(payload.Slice(off + 6, 3));
                int q1 = ReadInt24BigEndian(payload.Slice(off + 9, 3));
                // off+12, off+13 = mic (ignored).
                ddc0Out[wrote]     = ScaleInt24(i0);
                ddc0Out[wrote + 1] = ScaleInt24(q0);
                ddc1Out[wrote]     = ScaleInt24(i1);
                ddc1Out[wrote + 1] = ScaleInt24(q1);
                wrote += 2;
            }
        }

        complexSamples = TwoDdcSamplesPerPacket;
        return true;
    }

    /// <summary>
    /// Sequence gap counter state; zero-initialized.
    /// </summary>
    public struct SequenceTracker
    {
        private bool _seen;
        private uint _last;
        public long DroppedFrames;
        public long TotalFrames;

        public void Observe(uint seq)
        {
            TotalFrames++;
            if (_seen)
            {
                // Monotonic wrap or radio restart (seq < _last) → reset, no drop count.
                if (seq > _last)
                {
                    long gap = (long)seq - (long)_last - 1;
                    if (gap > 0) DroppedFrames += gap;
                }
            }
            _seen = true;
            _last = seq;
        }
    }
}
