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
using Zeus.Protocol1; // internal PacketParser, via InternalsVisibleTo
using Zeus.VirtualRadio.Rf;

namespace Zeus.VirtualRadio.P1;

/// <summary>
/// Encodes a Protocol-1 EP6 RX-IQ packet — the inverse of
/// <c>Zeus.Protocol1.PacketParser.TryParsePacket</c>. Writes the 8-byte Metis
/// header (<c>0xEF 0xFE 0x01 0x06</c> + BE seq), two USB frames each with the
/// triple <c>0x7F</c> sync, the C&amp;C echo (round-robin address + ADC
/// telemetry slots + overload bits), and 63 sample groups of int24-BE I/Q (+ the
/// mic field). Anti-drift: shares <c>PacketParser</c>'s length / offset / scale
/// constants directly (via InternalsVisibleTo) and is validated by feeding its
/// output back into <c>PacketParser.TryParsePacket</c> in a round-trip test.
/// </summary>
internal sealed class Ep6Encoder
{
    // Share the Protocol-1 framing constants with the decoder it inverts — also
    // the compile-time proof that the emulator reaches Zeus.Protocol1 internals.
    public const int Ep6PacketLength = PacketParser.PacketLength;                 // 1032
    public const int ComplexSamplesPerPacket = PacketParser.ComplexSamplesPerPacket; // 126

    // The remaining geometry constants come straight from PacketParser so the
    // encoder and decoder agree on the layout by construction.
    private const int MetisHeaderLength = PacketParser.MetisHeaderLength;          // 8
    private const int UsbFrameLength = PacketParser.UsbFrameLength;                // 512
    private const int UsbHeaderLength = PacketParser.UsbHeaderLength;              // 8 (3 sync + 5 C&C)
    private const int BytesPerSampleGroup = PacketParser.BytesPerSampleGroup;      // 8 (3 I + 3 Q + 2 mic)
    private const int ComplexSamplesPerUsbFrame = PacketParser.ComplexSamplesPerUsbFrame; // 63

    private const byte Sync = 0x7F;

    // Inverse of PacketParser.ScaleInt24 — the same 2^23 full-scale constant.
    private const double Int24FullScale = 8_388_608.0;   // 2^23
    private const int Int24Max = 8_388_607;              // +2^23 - 1
    private const int Int24Min = -8_388_608;             // -2^23

    // C&C echo round-robin addresses Zeus reads back for the FWD/REF meter pair
    // (TxMetersService.cs: C0AddrAlexFwd=0x08, C0AddrAlexRef=0x10). PacketParser
    // recovers the address as (C0 >> 3) & 0x1F, so addr 1 → 0x08, addr 2 → 0x10.
    //   addr=1 (C0=0x08): Ain0 = exciter/PA-temp (unused here, 0); Ain1 = FWD
    //   addr=2 (C0=0x10): Ain0 = REF;                              Ain1 = 0
    // Ain0 is the BE u16 at C1..C2 (usb[4..6]); Ain1 is the BE u16 at C3..C4
    // (usb[6..8]).
    private const byte AddrAlexFwd = 1;
    private const byte AddrAlexRef = 2;

    /// <summary>
    /// Encode one EP6 RX-IQ packet into <paramref name="packet"/>.
    /// </summary>
    /// <param name="packet">Destination, exactly <see cref="Ep6PacketLength"/> bytes.</param>
    /// <param name="sequence">Radio-assigned monotonic sequence number (BE u32 at bytes 4..7).</param>
    /// <param name="interleavedIq">Source I/Q (<c>[I0,Q0,…]</c>), ≥
    /// <c>2 × <see cref="ComplexSamplesPerPacket"/></c> doubles.</param>
    /// <param name="telemetry">FWD/REF ADC counts to place in the C&amp;C echo
    /// slots (PTT/MOX-gated upstream).</param>
    /// <param name="pttEcho">Decoded host PTT/MOX state to echo back in the C0[0]
    /// hardware-PTT bit. This mirrors the firmware's <c>clean_PTT_in</c> line,
    /// which is the debounced PTT — asserted whenever keyed and wholly independent
    /// of drive amplitude / forward power. Pass the decoded MOX flag, NOT a watts
    /// threshold, or the echo drops on a keyed-with-zero-drive frame.</param>
    /// <param name="micSamples">Optional mic / line-in samples for the mic field
    /// (int16); pass an empty span to emit silence.</param>
    public void Encode(
        Span<byte> packet,
        uint sequence,
        ReadOnlySpan<double> interleavedIq,
        in RfTelemetry telemetry,
        bool pttEcho = false,
        ReadOnlySpan<short> micSamples = default)
    {
        if (packet.Length != Ep6PacketLength)
            throw new ArgumentException(
                $"packet must be exactly {Ep6PacketLength} bytes", nameof(packet));

        int needed = 2 * ComplexSamplesPerPacket; // interleaved I+Q for 126 samples = 252
        if (interleavedIq.Length < needed)
            throw new ArgumentException(
                $"interleavedIq must hold at least {needed} doubles", nameof(interleavedIq));

        packet.Clear();

        // Metis header: 0xEF 0xFE 0x01 0x06 + BE uint32 seq. Endpoint 0x06 = RX IQ.
        packet[0] = 0xEF;
        packet[1] = 0xFE;
        packet[2] = 0x01;
        packet[3] = 0x06;
        BinaryPrimitives.WriteUInt32BigEndian(packet.Slice(4, 4), sequence);

        // C0[0] is the firmware's debounced PTT echo (clean_PTT_in in
        // Hermes_Tx_fifo_ctrl.v: Tx_fifo_wdata = {8'h7F, tx_addr, clean_dot,
        // clean_dash, clean_PTT_in}). It is asserted whenever the radio is keyed
        // and is wholly independent of forward power / drive amplitude — so it is
        // driven by the decoded host MOX flag passed in, NOT by a watts threshold
        // (a watts threshold would drop the echo on a keyed-with-zero-drive
        // frame). PacketParser.ExtractHardwarePtt reads exactly this bit; the
        // meter slot match masks C0[0] off (C0 & 0x7E), so the meter is unaffected
        // either way — this only drives the hardware-PTT echo.
        int sampleIndex = 0;
        for (int frame = 0; frame < 2; frame++)
        {
            Span<byte> usb = packet.Slice(MetisHeaderLength + frame * UsbFrameLength, UsbFrameLength);

            // 3-byte sync.
            usb[0] = Sync;
            usb[1] = Sync;
            usb[2] = Sync;

            // C&C echo: frame 0 carries the FWD slot (addr 1), frame 1 the REF
            // slot (addr 2). One packet therefore delivers both meter axes every
            // tick (Zeus's PacketParser emits telemetry0/telemetry1 independently
            // and TxMetersService.OnTelemetry routes each by C0 address).
            byte addr = frame == 0 ? AddrAlexFwd : AddrAlexRef;
            usb[3] = (byte)((addr << 3) | (pttEcho ? 0x01 : 0x00)); // C0
            ushort ain0 = frame == 0 ? (ushort)0 : telemetry.RefAdc; // C1..C2
            ushort ain1 = frame == 0 ? telemetry.FwdAdc : (ushort)0; // C3..C4
            BinaryPrimitives.WriteUInt16BigEndian(usb.Slice(4, 2), ain0);
            BinaryPrimitives.WriteUInt16BigEndian(usb.Slice(6, 2), ain1);
            // ADC-overload bits (C1[0]/C2[0]) are left as they fall out of the
            // telemetry bytes — faithful to the wire, where REF shares C1/C2 with
            // those flag positions. See the implementer note in the handoff.

            Span<byte> payload = usb.Slice(UsbHeaderLength); // 504 bytes
            for (int g = 0; g < ComplexSamplesPerUsbFrame; g++)
            {
                double iVal = interleavedIq[2 * sampleIndex];
                double qVal = interleavedIq[2 * sampleIndex + 1];

                int off = g * BytesPerSampleGroup;
                WriteInt24BigEndian(payload.Slice(off, 3), ToInt24(iVal));
                WriteInt24BigEndian(payload.Slice(off + 3, 3), ToInt24(qVal));

                // Mic / line-in field (int16 BE). Audio is a later phase — pass
                // an empty span to emit silence; any shortfall is zero-filled.
                short mic = sampleIndex < micSamples.Length ? micSamples[sampleIndex] : (short)0;
                BinaryPrimitives.WriteInt16BigEndian(payload.Slice(off + 6, 2), mic);

                sampleIndex++;
            }
        }
    }

    /// <summary>
    /// Quantise a [-1.0, +1.0] sample to signed int24, the exact inverse of
    /// <c>PacketParser.ScaleInt24</c> (which multiplies by 1/2^23). Values are
    /// clamped to the int24 range so a full-scale +1.0 maps to the largest
    /// representable code rather than overflowing.
    /// </summary>
    private static int ToInt24(double value)
    {
        double scaled = Math.Round(value * Int24FullScale);
        if (scaled > Int24Max) return Int24Max;
        if (scaled < Int24Min) return Int24Min;
        return (int)scaled;
    }

    /// <summary>
    /// Write a signed 24-bit value big-endian — the inverse of
    /// <c>PacketParser.ReadInt24BigEndian</c> (high byte first, sign-extended on
    /// read).
    /// </summary>
    private static void WriteInt24BigEndian(Span<byte> dest, int value)
    {
        dest[0] = (byte)((value >> 16) & 0xFF);
        dest[1] = (byte)((value >> 8) & 0xFF);
        dest[2] = (byte)(value & 0xFF);
    }
}
