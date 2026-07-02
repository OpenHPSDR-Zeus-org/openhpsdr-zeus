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
        // RX2 NCO (DDC1). HL2-doc address 0x03 → wire byte 0x06. In the
        // upstream HL2 gateware, DDC1 shares `mix2_2` with DDC3
        // (rtl/radio_openhpsdr1/radio.sv:515-540) — they take the same
        // ADC input. During PS+MOX `mix2_2.adc` is switched to
        // `tx_data_dac` (line 521: `(tx_on & pure_signal) ? tx_data_dac
        // : adcpipe[1]`), so DDC1 ends up demodulating the pre-PA DAC
        // samples at whatever NCO we set here. Zeus has no split-VFO
        // and consumes DDC1 only as audio (when PS is off), so this
        // just mirrors VfoAHz; the demodulated stream during PS+MOX is
        // ignored (DDC3 is the canonical TX reference for pscc).
        RxFreq2 = 0x06,
        // RX3 NCO (DDC2). HL2-doc address 0x04 → wire byte 0x08. DDC2
        // is fed by `mix2_0` (rtl/radio_openhpsdr1/radio.sv:484-495),
        // shared with DDC0 — i.e. DDC2 reads `adcpipe[0]`, the real
        // RF signal from HL2's single ADC. It is NEVER switched to
        // `tx_data_dac`. During PS+MOX Zeus tunes this NCO to TX freq
        // so DDC2 demodulates whatever the antenna is receiving at TX
        // frequency. While keyed that is the **RF-leakage of the
        // radiated TX signal** coupling back into the RX frontend —
        // functionally it serves as the pscc "rx" feedback, but the
        // mechanism is electromagnetic leakage, NOT a hardware coupler
        // (HL2 has no internal feedback coupler — see
        // docs/references/protocol-1/hermes-lite2-protocol.md
        // "External coupler is the only working configuration"). This
        // is why per-board HW peak calibration is mandatory; the
        // leakage level is hardware-unit specific. See
        // docs/lessons/hl2-ps-hwpeak-calibration.md.
        // mi0bot cmaster.cs:8537 also picks `psrx=2` when tot=5 (MOX+
        // PS); the wire layout matches even though the mi0bot comments
        // describe the topology incorrectly.
        RxFreq3 = 0x08,
        // RX4 NCO (DDC3). HL2-doc address 0x05 → wire byte 0x0a. DDC3
        // is fed by `mix2_2` (shared with DDC1 — see RxFreq2 above).
        // During PS+MOX `mix2_2.adc` is `tx_data_dac` (pre-PA DAC
        // output, 12-bit cordic sum from rtl/radio_openhpsdr1/radio.sv
        // ≈ line 1016). With NCO = TX freq, DDC3 produces a clean
        // baseband copy of the TX waveform straight out of the DAC —
        // this IS the pscc "tx" reference, and it's the only feedback
        // path on HL2 that is genuinely deterministic (independent of
        // antenna load, leakage, room coupling, etc.). mi0bot
        // cmaster.cs:8538 picks `pstx=3` when tot=5.
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
        // HL2 AD9866 PGA stable-gain control. HL2-doc address 0x0e →
        // wire byte 0x1c.
        //
        // NOTE: mi0bot Thetis (and prior Zeus comments) call this the
        // "ADC routing" / `P1_adc_cntrl` register, asserting that C1=0x04
        // "routes DDC1 onto ADC1 (the dedicated PA-coupler feedback ADC)".
        // That interpretation is WRONG for the upstream HL2 gateware:
        //   1. HL2 has no internal feedback coupler and no second ADC
        //      decoded in the gateware command path (see
        //      docs/references/protocol-1/hermes-lite2-protocol.md
        //      "External coupler is the only working configuration",
        //      and rtl/radio_openhpsdr1/radio.sv — no 6'h0e decoder).
        //   2. The actual gateware decoder for 6'h0e lives in
        //      rtl/ad9866.sv:137-140 (FAST_LNA block) and reads
        //      cmd_data[15] (en_tx_gain), cmd_data[14], cmd_data[13:8]
        //      = TX LNA gain — not C1 / not ADC routing.
        //
        // What Zeus's write (C1=0x04, C2=C3=C4=0) actually does on
        // upstream HL2: cmd_data[15]=0 → en_tx_gain=0, forcing the
        // AD9866 PGA to keep `rx_gain` (set via 0x0a) during TX instead
        // of switching to `tx_gain`. That keeps the PGA stable across
        // RX↔TX transitions, which the leakage-based PS feedback path
        // needs to converge — the C1=0x04 byte itself is ignored. The
        // empirical observation from Issue #172 (PS converges with this
        // write, NaN-cascades without it) was real, but for a different
        // reason than the original "ADC routing" comment claimed.
        //
        // The HL2 PS feedback in the upstream gateware is not from a
        // physical coupler. DDC2 reads adcpipe[0] (RF antenna) via
        // mix2_0 at TX-freq NCO → demodulates radiated-TX leakage during
        // MOX (= "post-PA" only in the loosest electromagnetic sense),
        // and DDC3 reads tx_data_dac via mix2_2 at TX-freq NCO →
        // demodulates the pre-PA DAC samples (the true clean TX
        // reference for pscc). See docs/lessons/hl2-ps-hwpeak-calibration.md
        // for why this leakage dependency requires per-board HW peak
        // calibration.
        LnaTxGainStable = 0x1c,
        // Predistortion config register 0x2b (HL2 PureSignal). C0 wire byte
        // = 0x2b << 1 = 0x56. The HL2-protocol doc table reserves bits
        // [19:16] = predistortion value (C2 [3:0]) for the host to write,
        // but the upstream gateware actually only reads cmd_data[17:16]
        // (= C2 [1:0]). See rtl/radio_openhpsdr1/radio.sv:288-293:
        //   6'h2b: if (cmd_data[31:24]==8'h00) tx_predistort_next = cmd_data[17:16];
        // Valid values fit in 2 bits: 0=off (identity), 1=on (LUT),
        // 2=EER envelope. Bits [19:18] are decoded but read as zero on
        // those values — keep writing the full nibble to stay forward-
        // compatible with any derivative gateware that widens the field.
        // bits [31:24] = predistortion subindex (C1) — the subindex value
        // must equal 0x00 for the gateware to accept the value. PR #119
        // review documents the common encoding mistake of placing the
        // value in C2 [7:4] — do NOT shift it left.
        Predistortion = 0x56,
        // On-board iambic CW keyer config. Gateware command address 0x0B →
        // wire byte 0x0b << 1 = 0x16. The HL2 (and the wider openHPSDR
        // family) decode this in rtl/cw_openhpsdr.sv:29-34:
        //   keyer_speed   = cmd_data[13:8]  → C3[5:0]  (0-60 WPM)
        //   keyer_mode    = cmd_data[15:14] → C3[7:6]  (00 straight, 01 A, 10 B)
        //   keyer_weight  = cmd_data[6:0]   → C4[6:0]  (33-66)
        //   keyer_spacing = cmd_data[7]     → C4[7]
        //   keyer_reverse = cmd_data[22]    → C2[6]
        // Wire encoding lives in WriteCwKeyerConfigPayload. See zeus-bks.
        CwKeyerConfig = 0x16,
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
        int Hl2TxAttnDb = int.MinValue,
        // HermesC10 (ANAN-G2E, P1) TX-time ADC attenuation in dB (0..31),
        // written to atten_on_Tx via C3[4:0] of the LnaTxGainStable (wire
        // 0x1c = register 0x0e) frame — G2E gateware Hermes.v:2187
        // `atten_on_Tx <= IF_Rx_ctrl_3[4:0]`, muxed onto the step attenuator
        // only while FPGA_PTT (Hermes.v:2278). NOT the same register, range,
        // or semantics as Hl2TxAttnDb above (AD9866 TX PGA, -28..+31), so it
        // gets its own field. Sentinel int.MinValue = "operator never set a
        // value" → the writer emits 31, the silicon reset default
        // (Hermes.v:2127), an honest no-op. The register is only scheduled by
        // the PS-armed rotation, so no other board ever emits it.
        int PsTxAttnOnTxDb = int.MinValue,
        // On-board CW keyer config (C&C 0x0B / wire 0x16). Speed is the
        // operator's WPM (clamped 0-60 to fit the 6-bit gateware field);
        // mode selects straight vs iambic A/B. Weight/spacing/reverse are
        // held at sensible fixed defaults for now (no UI yet) — see
        // WriteCwKeyerConfigPayload. Default mode Straight makes the write a
        // no-op until the operator opts into iambic. See zeus-bks.
        int CwKeyerSpeedWpm = 0,
        CwKeyerMode CwKeyerMode = CwKeyerMode.Straight,
        // ---- Audio front-end (external-audio-jacks re-port) -----------------
        // Two distinct P1 audio surfaces, both verified against ramdor Thetis
        // networkproto1.c and piHPSDR old_protocol.c:
        //
        //  (a) Hermes-class codec boards — DriveFilter (0x12) frame:
        //        C2[0] = mic_boost, C2[1] = mic_linein
        //      (Thetis networkproto1.c:581; piHPSDR old_protocol.c:2154-2156.)
        //      These default false → byte-identical to today's 0x12 frame.
        //
        //  (b) Hermes-Lite 2 — 0x0a / wire-0x14 frame (read-modify-write):
        //        C1[4] = mic_trs, C1[5] = mic_bias, C2[4:0] = line_in_gain
        //      (Thetis networkproto1.c:597-599 case 11; same in mi0bot.)
        //      The SAME frame carries C2[6] = puresignal_run and the C4 PGA /
        //      step-attenuator byte — those are written by the existing
        //      WriteAttenuatorPayload logic and MUST survive. The audio fields
        //      only set their own bits on top.
        //
        // mic_bias defaults OFF — enabling it on a floating connector can hang
        // PTT (BoardCapabilities.HermesLite2MicFrontEnd / HasMicBias guard the
        // surface). mic_ptt routing is a PTT-IN concern, not this feature, so
        // it is intentionally not written here (stays 0, as today).
        bool MicBoost = false,
        bool MicLineIn = false,
        bool MicTrs = false,
        bool MicBias = false,
        byte LineInGain = 0,
        // ATU (Apollo/Alex) auto-tune-start request. Set on the DriveFilter
        // (C0=0x12) frame, C2 bit 4 — register 0x09 bit [20] per the HL2
        // protocol doc ("Tune request: ... initiate an ATU tune"). Held by the
        // client for the tune duration then auto-cleared. Default false →
        // byte-identical to today on every board.
        bool AtuTune = false,
        // TX antenna relay select — Config-frame C4[1:0] (external-port parity
        // audit, GAP-P1-1). Thetis networkproto1.c:463-468 case 0: ANT3 → 0b10,
        // ANT2 → 0b01, ANT1 → 0b00. The HpsdrAntenna enum value (Ant1=0/Ant2=1/
        // Ant3=2) IS the 2-bit wire selector. Honoured ONLY on P1 boards with
        // full Alex TX relays (P1BoardHasTxAntennaRelays — ANAN-100D/200D); every
        // other P1 board is ANT1-hardwired on transmit and the encoder clamps to
        // 0. Default Ant1 → C4[1:0]=0, byte-identical to before this audit.
        HpsdrAntenna TxAntenna = HpsdrAntenna.Ant1,
        // HL2 user GPIO (external-ports plan, Phase 5; re-ported in the external-
        // port parity audit). The 4-bit user_dig_out mask lands in C3[3:0] of the
        // 0x0a / wire-0x14 frame → MCP23008 on the HL2 IO connector. Verified
        // Thetis-mi0bot networkproto1.c case 11 (`C3 = prn->user_dig_out & 0x0F`);
        // ramdor Thetis identical. HL2 only — gated at the wire layer. Default 0
        // → byte-identical to today (the 0x14 frame's C3 was previously 0).
        byte UserDigOut = 0,
        // LT2208 ADC dither / digital-output randomizer (Config frame C3 bits 3
        // and 4). Verified against ramdor Thetis networkproto1.c case 0
        // (lines 453-455: `(adc[0].dither << 3) | (adc[0].random << 4)`) and
        // piHPSDR old_protocol.c (`LT2208_DITHER_ON = 0x08`, `LT2208_RANDOM_ON =
        // 0x10`). LT2208-based boards only — HL2's AD9866 has no dither/random
        // and reuses C3 bit 3 as Band Volts PWM, so the wire encoder gates these
        // off for HL2. Both default false → matches Thetis' netInterface.c init
        // (`adc[i].dither = adc[i].random = 0`) and is byte-identical to today.
        bool AdcDitherEnabled = false,
        bool AdcRandomEnabled = false);

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
                // Hermes-class codec boards carry mic_boost (C2[0]) and
                // mic_linein (C2[1]) on this 0x12 frame — Thetis
                // networkproto1.c:581, piHPSDR old_protocol.c:2154-2156. HL2
                // has no stream codec and routes mic/line through the 0x14
                // frame instead, so it is excluded here. Both default false so
                // an operator who never touches the audio panel emits the same
                // bytes as today (byte-identical default-unsent).
                else if (state.Board != HpsdrBoardKind.HermesLite2)
                {
                    if (state.MicBoost) cc[2] |= 0x01;   // C2[0] mic_boost
                    if (state.MicLineIn) cc[2] |= 0x02;  // C2[1] mic_linein
                }
                // ATU auto-tune-start request — C2[4], register 0x09 bit [20]
                // ("Tune request") per the HL2 protocol doc; the Apollo/Alex
                // auto-tune bit on Hermes-class boards. Held only while the
                // operator's tune cycle is active (Protocol1Client clears it
                // after AtuTuneRequest.DurationMs).
                if (state.AtuTune) cc[2] |= 0x10;
                break;

            case CcRegister.Attenuator:
                WriteAttenuatorPayload(cc[1..], in state);
                break;

            case CcRegister.LnaTxGainStable:
                WriteLnaTxGainStablePayload(cc[1..], in state);
                break;

            case CcRegister.Predistortion:
                WritePredistortionPayload(cc[1..], in state);
                break;

            case CcRegister.CwKeyerConfig:
                WriteCwKeyerConfigPayload(cc[1..], in state);
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

        // HL2 mic front-end (external-audio-jacks re-port). This C0=0x14 frame
        // is register 0x0a, which on HL2 carries the real mic/line-in front-
        // end ALONGSIDE puresignal_run and the C4 step-attenuator byte. The
        // verified layout (Thetis networkproto1.c:597-599 case 11; identical in
        // mi0bot):
        //   C1[4] = mic_trs, C1[5] = mic_bias, C1[6] = mic_ptt
        //   C2[4:0] = line_in_gain, C2[6] = puresignal_run, C3[3:0] = user_dig_out
        // SINGLE-PASS COMPOSE — this method is the SOLE owner of the 0x0a register
        // frame, so every co-tenant bit must be (re)asserted here each tick: the
        // C4 PGA / step-attenuator byte (already in c14[3]), the C2[6] PS bit (set
        // below), the C2[4:0] line-in gain, and (HL2) the C3[3:0] user GPIO. The
        // frame is rebuilt from a freshly-cleared span every pass — it is NOT a
        // hardware-readback read-modify-write (mi0bot composes the same way). If a
        // future writer ever sets one field here, it must keep asserting all the
        // others or it will clobber them. mic_ptt is a PTT-IN concern, not this
        // feature, so C1[6] is intentionally left 0 — same as today. mic_bias
        // (C1[5]) defaults OFF: enabling it on a floating connector can hang PTT,
        // so the operator must opt in explicitly. At defaults (mic_trs/mic_bias
        // off, line_in_gain 0, GPIO 0) every byte is unchanged → byte-identical to
        // today on HL2.
        if (s.Board == HpsdrBoardKind.HermesLite2)
        {
            byte c1 = 0;
            if (s.MicTrs)  c1 |= 1 << 4;   // C1[4] mic_trs
            if (s.MicBias) c1 |= 1 << 5;   // C1[5] mic_bias
            c14[0] = c1;
            // line_in_gain is the low 5 bits of C2; OR it in so the PS bit
            // (set below) and the reserved high bits stay clear.
            c14[1] |= (byte)(s.LineInGain & 0x1F);
            // HL2 user GPIO: 4-bit user_dig_out → C3[3:0] (MCP23008 on the IO
            // connector). Thetis-mi0bot networkproto1.c case 11; ramdor identical.
            // Default 0 → byte-identical (C3 was 0 before this re-port). HL2-only;
            // RadioService gates the mask behind HasHl2UserGpio.
            c14[2] |= (byte)(s.UserDigOut & 0x0F);
        }

        // ANAN-10E line-in (HermesII, issue #667). The 10E TLV320 codec carries
        // its line-in gain on this SAME register-0x0a / wire-0x14 frame at
        // C2[4:0] — identical wire layout to HL2 (Thetis networkproto1.c). Unlike
        // HL2 the 10E does NOT carry puresignal_run here (C2[6] is HL2-only,
        // below; ANAN-class PS lives on a different wire path) and has no
        // mic_trs / mic_bias jack, so C1 stays 0. RadioService gates s.LineInGain
        // to non-zero ONLY when line-in is the active source, so at default Host
        // this emits 0 → byte-identical to today on the 10E. Board-gated to
        // HermesII so no other Hermes / ANAN board's 0x14 frame changes.
        if (s.Board == HpsdrBoardKind.HermesII)
        {
            c14[1] |= (byte)(s.LineInGain & 0x1F);
        }

        // ANAN codec mic_bias (external-port parity audit, GAP-AUD-1). Thetis
        // networkproto1.c case 11 (C0=0x14) is board-agnostic: C1[5] = mic_bias
        // for every codec board, yet the re-port only wired the bit for HL2 above
        // — so ANAN-100D/200D advertised HasMicBias but could never enable it
        // (the electret bias supply stayed off). RadioService.ClampAudioSource
        // gates s.MicBias to boards with HasMicBias (on P1: ANAN-100D/200D) AND
        // to the RadioMic/XLR source, so at the default (Host / bias off) this is
        // byte-identical to today. HL2 sets C1[5] in its own block above; this
        // covers the non-HL2 codec boards. mic_trs/mic_ptt stay 0 on ANAN (no
        // tip-ring jack split), matching today.
        if (s.Board != HpsdrBoardKind.HermesLite2 && s.MicBias)
        {
            c14[0] |= 1 << 5;   // C1[5] mic_bias
        }

        // HL2 PureSignal: register 0x0a bit 22 = puresignal_run. Bit 22 lives
        // in C2 bit 6 (22 - 16 = 6) of this same C0=0x14 frame. mi0bot
        // networkproto1.c:1102 — `C2 = (line_in_gain & 0b00011111) |
        // ((puresignal_run & 1) << 6);`. The HermesC10 (ANAN-G2E, P1) decodes
        // the SAME bit — classic Hermes v3.3 gateware Hermes.v:2170-2173,
        // `PureSignal_enable <= IF_Rx_ctrl_2[6]` under addr 0001_010 — where
        // it muxes RX4 onto the TX DAC samples, acted on only under FPGA_PTT
        // (Hermes.v:1401), so setting it while armed-at-rest is safe. Other
        // boards (Hermes / ANAN-class) have their PS-enable bit elsewhere on
        // the wire (Protocol 2's ALEX_PS_BIT) so we only flip C2[6] when we
        // know we're talking to an HL2 or a HermesC10. Issue #172. PR #119
        // placed this in C3 — that bug is the canonical regression to guard.
        if ((s.Board is HpsdrBoardKind.HermesLite2 or HpsdrBoardKind.HermesC10) && s.PsEnabled)
        {
            c14[1] |= 1 << 6;   // C2 bit 6 = puresignal_run
        }
    }

    private static void WriteLnaTxGainStablePayload(Span<byte> c14, in CcState s)
    {
        // HL2 register 0x0e (C0 wire byte 0x1c). The upstream HL2 gateware
        // decoder for this address (rtl/ad9866.sv:137-140, FAST_LNA block)
        // reads:
        //   cmd_data[15]    → en_tx_gain   (1 = use hardware-managed TX LNA gain)
        //   cmd_data[14]    → TX gain sign/mode helper
        //   cmd_data[13:8]  → TX gain value
        // These bits live in C3 / C2 of the CC frame, NOT C1.
        //
        // We send all zeros, which sets en_tx_gain=0. With en_tx_gain=0
        // the AD9866 PGA gain stays at `rx_gain` (set via 0x0a) during
        // TX instead of switching to `tx_gain` — i.e. the PGA is stable
        // across RX↔TX transitions. PS feedback on HL2 depends on this
        // stability because DDC2 carries RF-leakage from the radiated TX
        // (gain-dependent) and any PGA step on the MOX edge would shift
        // its amplitude.
        //
        // The historical c14[0]=0x04 byte (= mi0bot's `cntrl1=4` for
        // "route DDC1 onto ADC1") falls in cmd_data[31:24], which the
        // gateware doesn't read at this address. It is ignored. We zero
        // it out to make the wire honest about what we're actually
        // controlling. PS still converges because what mattered all
        // along was the en_tx_gain=0 (cmd_data[15]) bit — not the
        // imagined ADC routing.
        //
        // Outside PS+MOX we still emit zeros: this preserves the same
        // stable-gain invariant whenever the radio key-ups (MOX may flip
        // before the next register-rotation tick lands), and matches the
        // operator's expectation that Zeus does not touch hardware-
        // managed TX LNA gain. Any operator UI for TX-managed LNA gain
        // would need its own register slot; today Zeus has none.
        //
        // HermesC10 (ANAN-G2E, P1): the SAME wire byte 0x1c = register 0x0e
        // is decoded completely differently by the classic Hermes v3.3
        // gateware — `atten_on_Tx <= IF_Rx_ctrl_3[4:0]` (Hermes.v:2187),
        // muxed onto the step attenuator only while FPGA_PTT (Hermes.v:2278).
        // Reusing the HL2 all-zeros payload would command atten_on_Tx = 0 dB
        // — the opposite extreme of the silicon reset value 31 (Hermes.v:
        // 2127), an ADC-clip risk on a hot feedback tap. Board-branch: emit
        // the operator's persisted attenuation in C3[4:0]; the int.MinValue
        // sentinel ("never set") emits 31, the reset default — an honest
        // no-op. This register is only ever scheduled by the PS-armed
        // rotation, so no other board ever emits it; HL2 keeps the all-zero
        // payload below byte-identically.
        if (s.Board == HpsdrBoardKind.HermesC10)
        {
            int attn = s.PsTxAttnOnTxDb == int.MinValue
                ? 31
                : Math.Clamp(s.PsTxAttnOnTxDb, 0, 31);
            c14[0] = 0;               // C1 — reserved at 0x0e on Hermes v3.3
            c14[1] = 0;               // C2 — reserved at 0x0e on Hermes v3.3
            c14[2] = (byte)attn;      // C3[4:0] = atten_on_Tx (0..31 dB)
            c14[3] = 0;               // C4 — reserved at 0x0e on Hermes v3.3
            return;
        }
        c14[0] = 0;   // C1 — cmd_data[31:24]: not read by gateware at 0x0e
        c14[1] = 0;   // C2 — cmd_data[23:16]: not read by gateware at 0x0e
        c14[2] = 0;   // C3 — cmd_data[15:8]: en_tx_gain + TX gain. Zero → disabled.
        c14[3] = 0;   // C4 — cmd_data[7:0]:  not read by gateware at 0x0e
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

    // CW keyer weight / spacing / reverse have no UI yet (see zeus-bks), so
    // we hold them at the gateware-friendly neutral defaults: 50% weight
    // (1:1 dit:dah ratio), letter-spacing off, paddles un-swapped.
    private const byte CwKeyerDefaultWeight = 50;  // C4[6:0], range 33-66
    private const bool CwKeyerDefaultSpacing = false;
    private const bool CwKeyerDefaultReverse = false;
    // Gateware speed field is 6 bits; iambic.v documents 1-60 WPM.
    private const int CwKeyerMaxWpm = 60;

    private static void WriteCwKeyerConfigPayload(Span<byte> c14, in CcState s)
    {
        // C&C 0x0B layout (gateware rtl/cw_openhpsdr.sv:29-34, where
        // cmd_data[31:24]=C1, [23:16]=C2, [15:8]=C3, [7:0]=C4):
        //   keyer_reverse = cmd_data[22]    → C2[6]
        //   keyer_mode    = cmd_data[15:14] → C3[7:6]
        //   keyer_speed   = cmd_data[13:8]  → C3[5:0]
        //   keyer_spacing = cmd_data[7]     → C4[7]
        //   keyer_weight  = cmd_data[6:0]   → C4[6:0]
        int speed = Math.Clamp(s.CwKeyerSpeedWpm, 0, CwKeyerMaxWpm);
        byte mode = (byte)((byte)s.CwKeyerMode & 0x03);

        c14[0] = 0;                                              // C1 — unused
        c14[1] = (byte)(CwKeyerDefaultReverse ? 1 << 6 : 0);     // C2[6] reverse
        c14[2] = (byte)((mode << 6) | (speed & 0x3F));           // C3[7:6] mode | [5:0] speed
        c14[3] = (byte)((CwKeyerDefaultSpacing ? 1 << 7 : 0)
                        | (CwKeyerDefaultWeight & 0x7F));        // C4[7] spacing | [6:0] weight
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

        // C3: Atlas step attenuator [1:0], preamp [2], DITHER [3], RANDOM [4],
        // RX antenna [6:5], RX-out [7]. We leave [1:0] zero — the dedicated
        // extended attenuator register (C0=0x14) is the single source of truth
        // for RX attenuation on every board we target. Setting both would double
        // up on Atlas-era gateware. Bit positions verified against ramdor Thetis
        // networkproto1.c case 0 (lines 453-455) and piHPSDR old_protocol.c
        // (`LT2208_GAIN_ON = 0x04`, `LT2208_DITHER_ON = 0x08`,
        // `LT2208_RANDOM_ON = 0x10`).
        byte c3 = 0;
        if (s.Board == HpsdrBoardKind.HermesLite2)
        {
            // HL2 (AD9866) has no LT2208 preamp/dither/random. C3 bit 3 is the
            // Band Volts PWM enable — per
            // docs/references/protocol-1/hermes-lite2-protocol.md line 39
            // (`| 0x00 | [11] | Fan or Band Volts PWM (0=Fan, 1=Band Volts) |`),
            // it selects band-volts PWM on the FAN connector for external
            // amplifier band-steering. The preamp / dither / random bits below
            // are LT2208-only and are deliberately NOT written here: on HL2,
            // C3 bit 2 = VNA RX gain and bit 4 = FPGA PSU switching clock
            // (DATA[10] / DATA[12]). Driving bit 4 from the operator's preamp
            // toggle (the prior behaviour) disabled the PSU clock — a latent
            // bug; HL2 RX gain is the LNA register (0x0a), not a C3 bit.
            if (s.EnableHl2BandVolts) c3 |= 1 << 3;
        }
        else
        {
            // LT2208 boards (Mercury / Hermes / ANAN-class on P1): preamp/gain
            // at C3 bit 2, ADC dither at bit 3, digital-output randomizer at
            // bit 4. Bit positions verified against ramdor Thetis
            // networkproto1.c case 0 (lines 453-455) and piHPSDR old_protocol.c
            // (`LT2208_GAIN_ON = 0x04`, `LT2208_DITHER_ON = 0x08`,
            // `LT2208_RANDOM_ON = 0x10`). Preamp was previously emitted at bit 4
            // (the RANDOM bit) — corrected here so the two no longer collide.
            // Dither/random default off (Thetis netInterface.c init), so a board
            // the operator has not configured stays byte-identical to before.
            if (s.PreampOn) c3 |= 1 << 2;
            if (s.AdcDitherEnabled) c3 |= 1 << 3;
            if (s.AdcRandomEnabled) c3 |= 1 << 4;
        }
        // RX-antenna relay select C3[7:5]. Routed through the shared pure helper
        // so the wire-layer HL2 clamp (single jack → ANT1) and the external-port
        // encoder seam emit identical bytes. Co-tenant bits C3[2] (preamp), C3[3]
        // (HL2 band-volts / LT2208 dither) and C3[4] (LT2208 random) are already
        // OR'd above; the helper only touches [7:5]. During HermesC10 PS+MOX the
        // helper overrides the operator selection with the RX BYPASS relay code
        // (see EncodePsBypassOrRxAntennaC3Bits) — it is the SOLE writer of
        // C3[7:5] on this frame either way, so the two encodings can't collide.
        c3 |= EncodePsBypassOrRxAntennaC3Bits(s.RxAntenna, s.Board, s.PsEnabled, s.Mox);
        c14[2] = c3;

        // C4: Alex TX antenna [1:0], duplex [2] = 1 (always, per
        // old_protocol.c:2661), N-1 receivers at [5:3]. mi0bot
        // networkproto1.c:973 — `C4 |= (nddc - 1) << 3`. Single-RX default
        // is 0; HL2 PS armed bumps to 1 (= 2 receivers, paired DDC0/DDC1
        // layout). Capped at 7 by the 3-bit field.
        byte c4 = 1 << 2;
        c4 |= (byte)((s.NumReceiversMinusOne & 0x07) << 3);
        // TX antenna relay select C4[1:0] (external-port parity audit, GAP-P1-1).
        // Thetis networkproto1.c:463-468 case 0: ANT3 → 0b10, ANT2 → 0b01, ANT1 →
        // 0b00. Only emitted on P1 boards with full Alex TX relays (ANAN-100D/
        // 200D); EncodeTxAntennaC4Bits clamps every other board to ANT1 so a stale
        // per-band ANT2/3 (band rows are board-agnostic) can never reroute the
        // transmitter on a board that is ANT1-hardwired on transmit. Default Ant1
        // → 0 → byte-identical to before this audit.
        c4 |= EncodeTxAntennaC4Bits(s.TxAntenna, s.Board);
        c14[3] = c4;
    }

    /// <summary>
    /// Encode the Config-frame C3[7:5] antenna field, with the HermesC10
    /// (ANAN-G2E, P1) PureSignal bypass override. While PS is armed AND the
    /// radio is keyed on a HermesC10, the operator's RX-antenna selection is
    /// replaced by the RX BYPASS relay code C3[6:5] = 01 — classic Hermes v3.3
    /// gateware `IF_RX_relay &lt;= IF_Rx_ctrl_3[6:5]` (Hermes.v:2144) →
    /// `C122_Rx_1_in = (IF_RX_relay == 2'b01)` (Hermes.v:2474) → Mk2PA Alex SPI
    /// bit 11 = RX BYPASS OUT (Hermes.v:2492-2494), the same physical relay the
    /// P2 fix routes via alex0 bit 11. That relay carries the external PS
    /// feedback tap into the ADC; the gateware has no PTT term on it, so the
    /// host must drive the dance — the continuous C&amp;C rotation re-sends
    /// Config with MOX=0 at every unkey, restoring the operator's antenna via
    /// the fall-through below. C3[7] (`IF_Rout`) is deliberately NOT emitted:
    /// on the Mk2PA build it is decoded but never used (`C122_Rx_1_out` appears
    /// only in the non-Mk2PA Alex word, Hermes.v:2468/2509) — Thetis sets it,
    /// the gateware ignores it, byte-minimal wins. Deliberately independent of
    /// the Internal/External feedback pick (#1249 `ab30ee59` — Internal is a
    /// physically-impossible source on this board). This helper is the SINGLE
    /// writer of C3[7:5] per Config frame; every other board falls straight
    /// through to the plain antenna encoding, byte-identical to before.
    /// </summary>
    internal static byte EncodePsBypassOrRxAntennaC3Bits(
        HpsdrAntenna rxAntenna, HpsdrBoardKind board, bool psEnabled, bool mox)
    {
        if (board == HpsdrBoardKind.HermesC10 && psEnabled && mox)
        {
            return 0b001 << 5;   // C3[6:5] = 01 → RX BYPASS relay; C3[7] stays 0
        }
        return EncodeRxAntennaC3Bits(rxAntenna, board);
    }

    /// <summary>
    /// Encode the Config-frame RX-antenna relay bits (C3[7:5]) for the given
    /// board (external-ports plan — antenna slice, #804). Single source of the
    /// C3[7:5] math, called both on the wire path (WriteConfigPayload) and from
    /// the external-port encoder seam, so the two are byte-identical by
    /// construction.
    ///
    /// WIRE-LAYER CLAMP: on a board with no RX-antenna relay (Hermes-Lite 2 —
    /// its single antenna jack forwards to the N2ADR antenna pad, C3[5] does NOT
    /// drive an ANT1/2/3 relay), the selection is forced to ANT1 here so a stale
    /// per-band ANT2/3 value can never flip the N2ADR pad. This is the wire layer
    /// of the three-layer defence (UI gate / REST 409 / wire clamp) and holds
    /// even if an upstream layer is bypassed. Relay-capable boards emit the raw
    /// selection — byte-identical to before this slice, so the default-ANT1
    /// goldens stay green.
    /// </summary>
    internal static byte EncodeRxAntennaC3Bits(HpsdrAntenna rxAntenna, HpsdrBoardKind board)
    {
        // The capability record proper lives in Zeus.Server.Hosting (which this
        // assembly cannot reference), so the single relay-less P1 board is named
        // directly here — mirrors BoardCapabilities.HasRxAntennaRelays, false for
        // HL2 only across the P1 lineup.
        if (!P1BoardHasRxAntennaRelays(board)) rxAntenna = HpsdrAntenna.Ant1;
        return (byte)(((byte)rxAntenna & 0x07) << 5);
    }

    /// <summary>
    /// Whether a Protocol-1 board has switchable RX-antenna relays (ANT1/2/3).
    /// Mirrors <c>BoardCapabilities.HasRxAntennaRelays</c> for the P1 lineup:
    /// every ANAN / Hermes-class board does; Hermes-Lite 2 does not (single
    /// jack). Kept local to the protocol assembly so the wire-layer clamp does
    /// not need a reference to Zeus.Server.Hosting.
    /// </summary>
    internal static bool P1BoardHasRxAntennaRelays(HpsdrBoardKind board) =>
        board != HpsdrBoardKind.HermesLite2;

    /// <summary>
    /// Encode the Config-frame TX-antenna relay bits (C4[1:0]) for the given
    /// board (external-port parity audit — GAP-P1-1). Thetis networkproto1.c
    /// case 0 (lines 463-468): ANT3 → 0b10, ANT2 → 0b01, ANT1 → 0b00. The
    /// <see cref="HpsdrAntenna"/> enum value (Ant1=0/Ant2=1/Ant3=2) IS the 2-bit
    /// wire selector, so the byte is just the masked enum. Single source of the
    /// C4[1:0] math, shared by the wire path (WriteConfigPayload) and the
    /// external-port encoder seam so the two are byte-identical by construction.
    ///
    /// WIRE-LAYER CLAMP: a P1 board WITHOUT full Alex TX relays
    /// (<see cref="P1BoardHasTxAntennaRelays"/> false — every board except
    /// ANAN-100D/200D) is forced to ANT1 here, so a stale per-band ANT2/3 can
    /// never reroute the transmitter on a board that is ANT1-hardwired on
    /// transmit. This is the wire layer of the same UI-gate / REST-409 / wire-
    /// clamp defence the RX-antenna path uses; it holds even if an upstream layer
    /// is bypassed. Relay-capable boards emit the raw selection — byte-identical
    /// to before this audit at the default-ANT1, so the goldens stay green.
    /// </summary>
    internal static byte EncodeTxAntennaC4Bits(HpsdrAntenna txAntenna, HpsdrBoardKind board)
    {
        if (!P1BoardHasTxAntennaRelays(board)) return 0;
        return (byte)((byte)txAntenna & 0x03);
    }

    /// <summary>
    /// Whether a Protocol-1 board has switchable full-Alex TX-antenna relays
    /// (ANT1/2/3 on Config-frame C4[1:0]). Mirrors the P1 subset of
    /// <c>BoardCapabilities.HasTxAntennaRelays</c>: the full-Alex dual-ADC ANAN
    /// boards (ANAN-100D / ANAN-200D = <see cref="HpsdrBoardKind.Angelia"/> /
    /// <see cref="HpsdrBoardKind.Orion"/>). Hermes / Metis / ANAN-10 / ANAN-10E
    /// (no full Alex), ANAN-G2E (ANT1-hardwired on TX) and Hermes-Lite 2 (single
    /// jack) are ANT1-only on transmit. Kept local to the protocol assembly so the
    /// wire-layer clamp needs no reference to Zeus.Server.Hosting; it MUST stay in
    /// step with the BoardCapabilitiesTable HasTxAntennaRelays entries for P1.
    /// </summary>
    internal static bool P1BoardHasTxAntennaRelays(HpsdrBoardKind board) =>
        board is HpsdrBoardKind.Angelia or HpsdrBoardKind.Orion;

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
        ITxIqSource? iqSource = null,
        IRxAudioSource? rxAudioSource = null)
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

        WriteUsbFrame(packet.Slice(8, UsbFrameLength), evenRegister, in state, iqSource, rxAudioSource);
        WriteUsbFrame(packet.Slice(8 + UsbFrameLength, UsbFrameLength), oddRegister, in state, iqSource, rxAudioSource);
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

    private static void WriteUsbFrame(Span<byte> frame, CcRegister register, in CcState state, ITxIqSource? source, IRxAudioSource? rxAudioSource = null)
    {
        frame[0] = 0x7F;
        frame[1] = 0x7F;
        frame[2] = 0x7F;
        WriteCcBytes(frame.Slice(3, 5), register, in state);

        // Surface the current commanded drive byte for the 1 Hz p1.tx.rate log
        // regardless of which payload path runs below. The actual register
        // write happens inside WriteCcBytes when DriveFilter is the active
        // register; this tap just lets the diagnostic line reflect the live
        // state across every tick.
        LastDriveByte = state.DriveLevel;

        // EP2 504-byte payload = 63 groups × 8 bytes, each group =
        // [L_audio s16 BE][R_audio s16 BE][I s16 BE][Q s16 BE]
        // (both the audio ring fill and the IQ ring fill write into the same
        // 8-byte slot). HL2 has no audio codec in the MVP target, so audio
        // bytes stay zero. The LSB of I and Q low bytes is masked off
        // (`isample & 0xFE`) — originally an HL2 CWX workaround; harmless
        // ≤1 LSB precision loss on other Protocol-1 boards.
        //
        // Pre-conditions for writing a non-zero payload: MOX engaged and an IQ
        // source is plumbed through. The wire format (L/R audio + I/Q s16 BE)
        // is identical across all Protocol-1 boards (HL2, Hermes, ANAN-class,
        // Orion-MkII). PA enable is driven by the C0 MOX bit + board-specific
        // DriveFilter C2 bits in WriteCcBytes — see issue #294.
        if (!state.Mox)
        {
            // RX: the radio is not transmitting, so the EP2 I/Q slots stay zero
            // (no carrier) and the L/R slots may carry demodulated RX audio so
            // the radio's onboard codec drives its speaker/headphone/line-out
            // jacks (faithful to Thetis, which sends RX audio inline on EP2).
            // When no RX-audio source is plumbed, or the ring is empty, the L/R
            // slots stay zero — byte-identical to a radio that carries no audio.
            // HL2 has no codec and ignores L/R; the host simply never feeds the
            // ring for it, so this branch leaves the frame all-zero there too.
            if (rxAudioSource is not null) WriteRxAudioLr(frame[8..], rxAudioSource);
            return;
        }

        // From here MOX is engaged; the TX I/Q path needs a real source.
        if (source is null) return;

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
    }

    /// <summary>
    /// Fill the L/R audio slots (bytes 0..3 of each 8-byte group) of one EP2
    /// USB-frame payload from the RX-audio source, leaving the I/Q slots (4..7)
    /// untouched. The mono sample is written to BOTH the left and right channels
    /// as big-endian s16: duplicating mono sidesteps the per-board hardware L/R
    /// swap entirely (both channels carry identical audio), exactly as the
    /// Saturn/G2 P2 speaker sink already does. On underrun the source returns
    /// fewer samples than requested and the remaining groups keep their cleared
    /// (zero) L/R, which is silence rather than wrap noise.
    /// </summary>
    private static void WriteRxAudioLr(Span<byte> payload, IRxAudioSource rxAudioSource)
    {
        Span<short> mono = stackalloc short[IqSamplesPerUsbFrame];
        int n = rxAudioSource.Read(mono);
        for (int s = 0; s < n; s++)
        {
            int off = s * 8;
            byte hi = (byte)((mono[s] >> 8) & 0xFF);
            byte lo = (byte)(mono[s] & 0xFF);
            payload[off + 0] = hi;   // L high byte
            payload[off + 1] = lo;   // L low byte
            payload[off + 2] = hi;   // R high byte
            payload[off + 3] = lo;   // R low byte
        }
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
