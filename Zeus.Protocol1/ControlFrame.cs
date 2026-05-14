// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
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

using System.Buffers.Binary;
using System.Net;
using Zeus.Contracts;
using Zeus.Protocol1.Discovery;

namespace Zeus.Protocol1;

/// <summary>
/// Encodes Protocol-1 outbound packets: the 8-byte Metis header, the per-USB-frame
/// sync + C&amp;C preamble, and the CC payloads the MVP writes.
/// See docs/prd/02-protocol1-integration.md §3–§4 for wire-byte provenance.
/// </summary>
internal static class ControlFrame
{
    public const int PacketLength = 1032;
    public const int UsbFrameLength = 512;

    /// <summary>Round-robin CC0 register address selector (doc 02 §4).</summary>
    public enum CcRegister : byte
    {
        Config = 0x00,
        TxFreq = 0x02,
        RxFreq = 0x04,
        // RX2 NCO (DDC1). HL2-doc address 0x03 → wire byte 0x06. mi0bot
        // 4-DDC layout for HL2: DDC1 holds RX2 / second-receiver freq,
        // unrelated to PS. Zeus has no split-VFO so this just mirrors
        // VfoAHz; harmless when DDC1 isn't being audio-routed.
        RxFreq2 = 0x06,
        // RX3 NCO (DDC2). HL2-doc address 0x04 → wire byte 0x08.
        // mi0bot's HL2 4-DDC layout uses DDC2 as the **PureSignal RX
        // feedback** stream (post-PA tap, fed to pscc as the "rx" arg).
        // mi0bot networkproto1.c case 5 / WriteMainLoop_HL2: NCO = TX
        // frequency. cmaster.cs:8537 routes psrx=2 when tot=5 (MOX+PS).
        RxFreq3 = 0x08,
        // RX4 NCO (DDC3). HL2-doc address 0x05 → wire byte 0x0a.
        // mi0bot's HL2 4-DDC layout uses DDC3 as the **PureSignal TX
        // reference** stream (TX-DAC loopback / clean modulator, fed to
        // pscc as the "tx" arg). NCO = TX frequency. cmaster.cs:8538
        // routes pstx=3 when tot=5.
        RxFreq4 = 0x0a,
        DriveFilter = 0x12,
        // Extended RX attenuator + (HL2 only) PureSignal enable bit and LNA
        // mode. Protocol-1 writes these under C0=0x14, the wire-byte
        // encoding of register 0x0a (= 0x0a << 1). For bare HPSDR /
        // ANAN-class radios the payload is just the legacy step-attenuator
        // byte in C4. For HL2 the same address frame also carries the
        // puresignal_run bit in C2 bit 6 (mi0bot networkproto1.c:1102) and
        // the user_dig_out nibble in C3, plus an extended C4 attenuator
        // range (0x40 | (60-Db)) — see WriteAttenuatorPayload.
        Attenuator = 0x14,
        // ADC assignments + TX step attenuator. HL2-doc address 0x0e →
        // wire byte 0x1c. C1 carries P1_adc_cntrl bits [7:0] — per-RX
        // ADC source selector, 2 bits per RX (RX0 → bits[1:0],
        // RX1 → bits[3:2], …). Mi0bot networkproto1.c case 4 +
        // netInterface.c:917-930. For HL2 PureSignal during TX,
        // cntrl1 = 4 (= bits[3:2] = 1) routes DDC1 onto ADC1 (the
        // dedicated PA-coupler feedback ADC) so pscc sees the post-PA
        // signal. Without this register, HL2 leaves DDC1 on ADC0 and
        // pscc never converges (binfo[6]=0x0001 NaN cascade). C2 carries
        // bits [13:8] (zero for HL2 — only RX0..RX3 used), C3 = ADC TX
        // step attenuator (we leave 0).
        AdcRouting = 0x1c,
        // Predistortion config register 0x2b (HL2 PureSignal). C0 wire byte
        // = 0x2b << 1 = 0x56. bits [31:24] = predistortion subindex (C1),
        // bits [19:16] = predistortion value (C2 [3:0]). PR #119 review
        // documents the common encoding mistake of placing the value in
        // C2 [7:4] — do NOT shift it left.
        Predistortion = 0x56,
    }

    /// <summary>
    /// Immutable snapshot of the parameters a single CC frame will encode.
    /// Thread-safety: the live client updates these via atomic writes; the TX
    /// thread copies a snapshot each tick.
    /// </summary>
    public readonly record struct CcState(
        long VfoAHz,
        HpsdrSampleRate Rate,
        bool PreampOn,
        HpsdrAtten Atten,
        HpsdrAntenna RxAntenna,
        bool Mox,
        // HL2 reuses C3 bit 3 — originally LT2208 DITHER on legacy HPSDR
        // hardware — as the **Band Volts PWM** enable. See
        // docs/references/protocol-1/hermes-lite2-protocol.md line 39:
        //   `| 0x00 | [11] | Fan or Band Volts PWM (0=Fan, 1=Band Volts) |`
        // HL2's AD9866 has no ADC dither, so mi0bot's HL2 fork piggybacks on
        // the same bit and labels its checkbox "Band Volts". When set, HL2's
        // FPGA emits the per-band-tagged PWM voltage on the FAN connector so
        // an external amplifier (e.g. Xiegu XPA125B) can auto-band-switch.
        // Honoured by HL2 only; harmless on legacy boards (it still maps to
        // the obsolete DITHER bit there, but Zeus never sets it for them).
        bool EnableHl2BandVolts,
        HpsdrBoardKind Board,
        bool HasN2adr = false,
        // Raw DriveFilter C1 payload byte (0..255). This is the transmitter
        // drive_level written directly to output_buffer[C1]. Units are
        // "hardware drive level 0..255"; UI-side percent is mapped in
        // Protocol1Client.SnapshotState.
        byte DriveLevel = 0,
        // User-configured OC pin masks (7-bit) from PaSettingsStore. OR'd with
        // the board's auto-filter output in WriteConfigPayload so the stock HL2
        // + N2ADR behavior keeps working when the user hasn't configured
        // anything. Selected by MOX: TX mask during transmit, RX mask otherwise
        // (piHPSDR `old_protocol.c:1884-1904`).
        byte UserOcTxMask = 0,
        byte UserOcRxMask = 0,
        // PureSignal enable for HL2 — wire-side bit 0x0a[22] = C2 bit 6 of
        // the C0=0x14 (Attenuator) frame, and a duplicate copy at the
        // Predistortion subindex. Set when the operator arms PS via
        // PsToggleButton; ignored on non-HL2 boards. Issue #172.
        bool PsEnabled = false,
        // PureSignal predistortion value (0..15) and subindex (0..255) —
        // written via the Predistortion (0x2b) register frame. Defaults
        // mirror the WDSP `calcc` initial state: subindex 0, value 0
        // (= "PS off, identity correction"). Sent as a paired write when
        // PsEnabled flips. Issue #172.
        byte PsPredistortionValue = 0,
        byte PsPredistortionSubindex = 0,
        // Number of receivers minus 1, packed into Config C4 [5:3]. Default
        // 0 (single RX); HL2 PS uses 1 (= 2 receivers, paired DDC0/DDC1
        // layout). mi0bot networkproto1.c:973 — `C4 |= (nddc - 1) << 3`.
        byte NumReceiversMinusOne = 0,
        // HL2 TX-side step attenuator (PGA) target in dB. Operator-tunable
        // via PsAutoAttenuateService when PS auto-attenuate is on; otherwise
        // a sentinel value of int.MinValue means "untouched, use the default
        // RX-side encoding for C4". Range when set: -28..+31 dB
        // (mi0bot console.cs:2084 udTXStepAttData min=-28; +31 is the AD9866
        // TX PGA upper). Wire encoding lives in WriteAttenuatorPayload.
        int Hl2TxAttnDb = int.MinValue);

    /// <summary>
    /// Write the 5 C&amp;C bytes for <paramref name="register"/> given the current
    /// <paramref name="state"/>. Returns the number of bytes written (always 5).
    /// </summary>
    public static int WriteCcBytes(Span<byte> cc, CcRegister register, in CcState state)
    {
        if (cc.Length < 5) throw new ArgumentException("cc span < 5 bytes", nameof(cc));

        // CcRegister values are already the wire-byte encodings (pre-shifted
        // address with bit 0 cleared for MOX). Just OR the MOX bit in.
        cc[0] = (byte)(((byte)register & 0xFE) | (state.Mox ? 1 : 0));

        switch (register)
        {
            case CcRegister.Config:
                WriteConfigPayload(cc[1..], in state);
                break;

            case CcRegister.RxFreq:
            case CcRegister.TxFreq:
            case CcRegister.RxFreq2:
            case CcRegister.RxFreq3:
            case CcRegister.RxFreq4:
                // Frequency payload is a BE uint32 in C1..C4 (doc 02 §4 "Frequency payload").
                // All five frequency registers (TxFreq + four RX NCOs) carry the
                // same VfoAHz here — Zeus has no separate TX VFO. During HL2
                // PS+MOX, mi0bot tunes DDC2 and DDC3 to TX freq, which is the
                // operator-tuned freq for SSB; for CW, EffectiveLoHz is already
                // baked into VfoAHz upstream in RadioService.SetVfo.
                BinaryPrimitives.WriteUInt32BigEndian(cc[1..5], (uint)state.VfoAHz);
                break;

            case CcRegister.DriveFilter:
                // Protocol-1 writes C0=0x12, C1 = drive_level & 0xFF, then C2..C4
                // carry mic/filter/PA bits. On HermesLite2 that same block zeroes
                // C2/C3/C4 and lights C2[3] for PA enable when pa_enabled &&
                // !txband->disablePA. Without this bit the HL2 gateware never
                // energizes the PA regardless of drive level. We gate on MOX so
                // PA-enable is only asserted while transmitting.
                cc[1] = state.DriveLevel;
                cc[2] = 0;
                cc[3] = 0;
                cc[4] = 0;
                if (state.Board == HpsdrBoardKind.HermesLite2 && state.Mox)
                {
                    cc[2] |= 0x08;
                }
                break;

            case CcRegister.Attenuator:
                WriteAttenuatorPayload(cc[1..], in state);
                break;

            case CcRegister.AdcRouting:
                WriteAdcRoutingPayload(cc[1..], in state);
                break;

            case CcRegister.Predistortion:
                WritePredistortionPayload(cc[1..], in state);
                break;

            default:
                cc[1] = cc[2] = cc[3] = cc[4] = 0;
                break;
        }

        return 5;
    }

    private static void WriteAttenuatorPayload(Span<byte> c14, in CcState s)
    {
        // Bare HPSDR (Hermes/Angelia/Orion/MkII): C4 = 0x20 | (Db & 0x1F).
        // HL2: C4 = 0x40 | (60 - Db) — HL2 has no physical RX step attenuator,
        // so the UI "attenuate by N dB" maps to "reduce firmware RX gain by N
        // from its max of 60" (HL2 gateware ad9866 rxgain register).
        int db = s.Atten.ClampedDb;
        byte c4 = s.Board == HpsdrBoardKind.HermesLite2
            ? (byte)(0x40 | Math.Clamp(60 - db, 0, 60))
            : (byte)(0x20 | (db & 0x1F));

        // HL2 PS auto-attenuate: during MOX with PS enabled, mi0bot
        // networkproto1.c:1086-1088 swaps the C4 source from rx_step_attn to
        // tx_step_attn so the AD9866 TX-side PGA presents the operator's
        // ATTOnTX value (the feedback path PGA register, NOT a separate RX
        // attenuator). C# UI value (-28..+31 dB) → wire byte (31 - db),
        // matching mi0bot console.cs:10947-10948
        // `NetworkIO.SetTxAttenData(31 - _tx_attenuator_data)`. Bit 6 stays
        // set (0x40, PGA select); the low 6 bits carry the wire byte clamped
        // to the same 0..60 RX-side range so a stale operator value can't
        // overflow the field. Sentinel int.MinValue keeps the default RX-
        // side encoding above untouched — first PS arm matches today's
        // behaviour exactly.
        if (s.Board == HpsdrBoardKind.HermesLite2
            && s.Mox
            && s.Hl2TxAttnDb != int.MinValue)
        {
            c4 = (byte)(Math.Clamp(31 - s.Hl2TxAttnDb, 0, 60) | 0x40);
        }

        c14[0] = 0;   // C1 — reserved on this register
        c14[1] = 0;   // C2
        c14[2] = 0;   // C3
        c14[3] = c4;

        // HL2 PureSignal: register 0x0a bit 22 = puresignal_run. Bit 22 lives
        // in C2 bit 6 (22 - 16 = 6) of this same C0=0x14 frame. mi0bot
        // networkproto1.c:1102 — `C2 = (line_in_gain & 0b00011111) |
        // ((puresignal_run & 1) << 6);`. Other boards (Hermes / ANAN-class)
        // have their PS-enable bit elsewhere on the wire (Protocol 2's
        // ALEX_PS_BIT) so we only flip C2[6] when we know we're talking to
        // an HL2. Issue #172. PR #119 placed this in C3 — that bug is the
        // canonical regression to guard.
        if (s.Board == HpsdrBoardKind.HermesLite2 && s.PsEnabled)
        {
            c14[1] |= 1 << 6;   // C2 bit 6 = puresignal_run
        }
    }

    private static void WriteAdcRoutingPayload(Span<byte> c14, in CcState s)
    {
        // HL2 register 0x0e (C0 wire byte 0x1c). C1 carries the
        // P1_adc_cntrl byte = per-RX ADC source selector packed 2 bits per
        // RX (RX0 → bits[1:0], RX1 → bits[3:2], RX2 → bits[5:4],
        // RX3 → bits[7:6]). 0 = ADC0 (main antenna), 1 = ADC1 (HL2's
        // dedicated PA-coupler feedback ADC). For HL2 PureSignal during
        // TX, mi0bot sets cntrl1=4 (= bits[3:2]=1) so DDC1 sources ADC1
        // and pscc sees the post-PA signal. Outside PS+MOX, all RX → ADC0.
        // C2 carries cntrl2 (RX4..RX6 — HL2 only has 4 DDCs in this
        // codepath, leave 0). C3 = ADC[0].tx_step_attn (Zeus drives TX
        // attenuation through the dedicated 0x14 path, leave 0 here).
        bool psActive = s.PsEnabled && s.Mox && s.Board == HpsdrBoardKind.HermesLite2;
        c14[0] = psActive ? (byte)0x04 : (byte)0;  // C1: cntrl1
        c14[1] = 0;                                 // C2: cntrl2 (high bits of P1_adc_cntrl)
        c14[2] = 0;                                 // C3: tx_step_attn (unused here)
        c14[3] = 0;                                 // C4: reserved
    }

    private static void WritePredistortionPayload(Span<byte> c14, in CcState s)
    {
        // HL2 register 0x2b (C0 wire byte 0x56). Per the HL2 protocol doc:
        //   bits [31:24] = predistortion subindex  → C1 (whole byte)
        //   bits [19:16] = predistortion value      → C2 [3:0] (low nibble)
        // PR #119 placed the value in C2 [7:4] — that's bits [23:20], which
        // are reserved. Do NOT shift the value left. mi0bot's clsHardwareSpecific
        // / cmaster.cs writes via the same address space, with the value
        // word in the low nibble of C2.
        c14[0] = s.PsPredistortionSubindex;            // C1
        c14[1] = (byte)(s.PsPredistortionValue & 0x0F); // C2 [3:0]; high nibble = reserved (0)
        c14[2] = 0;                                     // C3
        c14[3] = 0;                                     // C4
    }

    private static void WriteConfigPayload(Span<byte> c14, in CcState s)
    {
        // C1: sample rate at [1:0], clock source (Atlas-era) at [6:4] — left 0 for Hermes+.
        byte c1 = (byte)((byte)s.Rate & 0x03);
        c14[0] = c1;

        // C2: class-E PA at bit 0; OC pins (N2ADR filter board on HL2, user-
        // configured OC outputs on Orion-class) at bits 1..7. Class-E stays 0
        // for RX-only MVP. We OR three sources so stock behavior holds when
        // the user hasn't touched PA Settings:
        //   1. Board auto-filter mask (N2ADR on HL2) — legacy path
        //   2. User's per-band OC-TX mask when MOX, else OC-RX mask
        byte ocPins = 0;
        if (s.Board == HpsdrBoardKind.HermesLite2 && s.HasN2adr)
        {
            ocPins |= N2adrBands.RxOcMask(s.VfoAHz);
        }
        ocPins |= (byte)((s.Mox ? s.UserOcTxMask : s.UserOcRxMask) & 0x7F);
        byte c2 = (byte)(ocPins << 1);
        c14[1] = c2;

        // C3: Atlas step attenuator [1:0], RAND [2], DITHER [3], preamp [4],
        // RX antenna [7:5]. We leave [1:0] zero — the dedicated extended
        // attenuator register (C0=0x14) is the single source of truth for RX
        // attenuation on every board we target. Setting both would double
        // up on Atlas-era gateware.
        byte c3 = 0;
        // HL2 Band Volts PWM enable. Per
        // docs/references/protocol-1/hermes-lite2-protocol.md line 39
        // (`| 0x00 | [11] | Fan or Band Volts PWM (0=Fan, 1=Band Volts) |`),
        // C3 bit 3 selects band-volts PWM on the FAN connector for external
        // amplifier band-steering. Off by default; flipped on per-HL2 by the
        // operator through the RADIO settings panel. The same bit reads as
        // LT2208 DITHER on legacy HPSDR boards — Zeus never sets it for
        // them, so the rename is purely client-side.
        if (s.EnableHl2BandVolts) c3 |= 1 << 3;
        if (s.PreampOn) c3 |= 1 << 4;             // Q#2: single global preamp bit for MVP.
        c3 |= (byte)(((byte)s.RxAntenna & 0x07) << 5);
        c14[2] = c3;

        // C4: Alex TX antenna [1:0] = 0 (RX-only MVP), duplex [2] = 1 (always, per
        // old_protocol.c:2661), N-1 receivers at [5:3]. mi0bot
        // networkproto1.c:973 — `C4 |= (nddc - 1) << 3`. Single-RX default
        // is 0; HL2 PS armed bumps to 1 (= 2 receivers, paired DDC0/DDC1
        // layout). Capped at 7 by the 3-bit field.
        byte c4 = 1 << 2;
        c4 |= (byte)((s.NumReceiversMinusOne & 0x07) << 3);
        c14[3] = c4;
    }

    /// <summary>
    /// Build a complete 1032-byte Metis data frame with two USB frames carrying
    /// the two given registers back-to-back, an increasing sequence number, and
    /// (when MOX is on and a tone generator is supplied) an IQ test-tone payload.
    /// </summary>
    public static void BuildDataPacket(
        Span<byte> packet,
        uint sendSequence,
        CcRegister evenRegister,
        CcRegister oddRegister,
        in CcState state,
        ITxIqSource? iqSource = null)
    {
        if (packet.Length != PacketLength)
            throw new ArgumentException("packet span must be 1032 bytes", nameof(packet));

        packet.Clear();

        // Metis header: 0xEF 0xFE 0x01 0x02 + BE uint32 seq. Endpoint 0x02 = TX/audio.
        packet[0] = 0xEF;
        packet[1] = 0xFE;
        packet[2] = 0x01;
        packet[3] = 0x02;
        BinaryPrimitives.WriteUInt32BigEndian(packet[4..8], sendSequence);

        WriteUsbFrame(packet.Slice(8, UsbFrameLength), evenRegister, in state, iqSource);
        WriteUsbFrame(packet.Slice(8 + UsbFrameLength, UsbFrameLength), oddRegister, in state, iqSource);
    }

    /// <summary>
    /// Build a 64-byte Metis start/stop packet.
    /// </summary>
    public static void BuildStartStop(Span<byte> packet, bool start, bool includeWideband = false)
    {
        if (packet.Length < 64) throw new ArgumentException("packet span must be ≥ 64 bytes", nameof(packet));
        packet[..64].Clear();
        packet[0] = 0xEF;
        packet[1] = 0xFE;
        packet[2] = 0x04;
        packet[3] = start ? (byte)(includeWideband ? 0x03 : 0x01) : (byte)0x00;
    }

    /// <summary>Number of IQ samples per 504-byte EP2 USB-frame payload (63 × 8 bytes).</summary>
    internal const int IqSamplesPerUsbFrame = 63;

    private static void WriteUsbFrame(Span<byte> frame, CcRegister register, in CcState state, ITxIqSource? source)
    {
        frame[0] = 0x7F;
        frame[1] = 0x7F;
        frame[2] = 0x7F;
        WriteCcBytes(frame.Slice(3, 5), register, in state);

        // EP2 504-byte payload = 63 groups × 8 bytes, each group =
        // [L_audio s16 BE][R_audio s16 BE][I s16 BE][Q s16 BE]
        // (both the audio ring fill and the IQ ring fill write into the same
        // 8-byte slot). HL2 has no audio codec in the MVP target, so audio
        // bytes stay zero. HL2 also clears the LSB of the I and Q low bytes as
        // a CWX workaround (`isample & 0xFE`) — we mirror that.
        //
        // Pre-conditions for writing a non-zero payload: MOX engaged, board is
        // HL2 (don't accidentally drive RF on other boards whose PA bits live
        // elsewhere), and an IQ source is plumbed through.
        if (source is null || !state.Mox || state.Board != HpsdrBoardKind.HermesLite2)
        {
            // frame[8..] was cleared by BuildDataPacket; leave zero.
            return;
        }

        // The HL2's TXG stage (DriveFilter C1 = DriveLevel byte) scales the
        // transmit path by drive%. Scaling IQ here on top would double-multiply
        // (drive⁴ power response). Send at unity — WDSP's ALC already clamps
        // the TXA output to ≤ 0 dBFS and the TUN post-gen tone is a
        // fixed-amplitude single-tone carrier, so neither source can overshoot
        // +1.0 here. The prior 0.85 factor cost ~1.4 dB of achievable output
        // and was observed to leave HL2 at 1.2 W when deskHPSDR hit 6.6 W on
        // the same antenna/band; it was belt-and-suspenders on top of ALC.
        // At DriveLevel=0 the HL2 TXG is already 0 (silent), but zero the IQ
        // too so the wire bytes are silent regardless of board.
        if (state.DriveLevel == 0) return;
        const double amplitude = 1.0;

        var payload = frame[8..];
        int peak = 0;
        long sumAbs = 0;
        int firstI = 0, firstQ = 0;
        for (int s = 0; s < IqSamplesPerUsbFrame; s++)
        {
            var (iSample, qSample) = source.Next(amplitude);
            if (s == 0) { firstI = iSample; firstQ = qSample; }
            int ai = Math.Abs((int)iSample);
            int aq = Math.Abs((int)qSample);
            if (ai > peak) peak = ai;
            if (aq > peak) peak = aq;
            sumAbs += ai + aq;
            int off = s * 8;
            // Audio L/R stay zero (payload was cleared).
            payload[off + 4] = (byte)((iSample >> 8) & 0xFF);
            payload[off + 5] = (byte)(iSample & 0xFE);
            payload[off + 6] = (byte)((qSample >> 8) & 0xFF);
            payload[off + 7] = (byte)(qSample & 0xFE);
        }
        LastPeakAbs = peak;
        LastMeanAbs = (int)(sumAbs / (2 * IqSamplesPerUsbFrame));
        LastFirstI = firstI;
        LastFirstQ = firstQ;
        LastDriveByte = state.DriveLevel;
    }

    // Diagnostic tap — read by Protocol1Client.TxLoopAsync to log what's
    // actually on the wire. Each WriteUsbFrame call updates these; TxLoopAsync
    // logs them at 1 Hz so we can tell whether the IQ reaching the HL2 is
    // really at rated amplitude vs being attenuated somewhere in the chain.
    public static volatile int LastPeakAbs;
    public static volatile int LastMeanAbs;
    public static volatile int LastFirstI;
    public static volatile int LastFirstQ;
    public static volatile byte LastDriveByte;

    public static IPEndPoint Port1024(IPAddress address) => new IPEndPoint(address, 1024);
}
