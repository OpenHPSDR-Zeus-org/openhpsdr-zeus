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
using Zeus.Protocol1; // internal ControlFrame / PacketParser, via InternalsVisibleTo

namespace Zeus.VirtualRadio.P1;

/// <summary>
/// Decodes inbound Protocol-1 host packets — the inverse of
/// <c>Zeus.Protocol1.ControlFrame</c>. Handles both the 64-byte start/stop
/// command (<c>0xEF 0xFE 0x04 …</c>) and the 1032-byte EP2 data frame
/// (<c>0xEF 0xFE 0x01 0x02 …</c>), reading the per-USB-frame C&amp;C round-robin
/// (Config / TxFreq / RxFreq / DriveFilter / Attenuator …) into the shared
/// <see cref="HostCommandState"/> and emitting a <see cref="DecodedHostCommand"/>
/// per frame. Anti-drift: it consumes <c>ControlFrame.CcRegister</c> /
/// <c>PacketParser</c> constants directly (via InternalsVisibleTo), so a wire-
/// format change breaks the build or the round-trip test.
/// </summary>
internal sealed class Ep2Decoder
{
    // Reference the Protocol-1 framing constants directly so the decoder shares
    // ONE definition with the encoder it inverts — these are also the
    // compile-time proof that the emulator reaches Zeus.Protocol1 internals.
    public const int Ep2PacketLength = ControlFrame.PacketLength; // 1032
    public const int UsbFrameLength = ControlFrame.UsbFrameLength; // 512

    private const int MetisHeaderLength = 8;     // 0xEF 0xFE 0x01 0x02 + BE seq
    private const byte Sync = 0x7F;

    private static readonly IReadOnlyList<DecodedHostCommand> Empty = Array.Empty<DecodedHostCommand>();

    /// <summary>
    /// Decode one inbound host packet, mutating <paramref name="state"/> in
    /// place and returning the command event(s) it produced (one per decoded
    /// C&amp;C frame; a start/stop yields a single event). Returns an empty list
    /// for packets that are not recognised host frames.
    /// </summary>
    public IReadOnlyList<DecodedHostCommand> Decode(ReadOnlySpan<byte> packet, HostCommandState state)
    {
        // Start/stop command: 0xEF 0xFE 0x04 {0x01 start | 0x03 start+wideband |
        // 0x00 stop}. ControlFrame.BuildStartStop produces a 64-byte packet; we
        // only need the 4-byte preamble to classify it.
        if (packet.Length >= 4 &&
            packet[0] == 0xEF && packet[1] == 0xFE && packet[2] == 0x04)
        {
            byte cmd = packet[3];
            bool start = cmd is 0x01 or 0x03;
            bool wideband = cmd == 0x03;
            state.Running = start;
            return new[]
            {
                start
                    ? Make("Start", $"running=1 wideband={(wideband ? 1 : 0)}")
                    : Make("Stop", "running=0"),
            };
        }

        // EP2 data frame: 0xEF 0xFE 0x01 0x02, exactly 1032 bytes, two USB frames
        // each carrying one round-robin C&C register.
        if (packet.Length == Ep2PacketLength &&
            packet[0] == 0xEF && packet[1] == 0xFE &&
            packet[2] == 0x01 && packet[3] == 0x02)
        {
            var events = new List<DecodedHostCommand>(2);
            for (int frame = 0; frame < 2; frame++)
            {
                ReadOnlySpan<byte> usb = packet.Slice(MetisHeaderLength + frame * UsbFrameLength, UsbFrameLength);
                if (usb[0] != Sync || usb[1] != Sync || usb[2] != Sync) continue;

                ReadOnlySpan<byte> cc = usb.Slice(3, 5);
                byte c0 = cc[0];
                // MOX is bit 0 of every frame's C0 (ControlFrame ORs it onto the
                // pre-shifted register byte). Set it BEFORE decoding the payload
                // so the Config frame routes the OC mask to the right (TX/RX) slot.
                state.Mox = (c0 & 0x01) != 0;
                var register = (ControlFrame.CcRegister)(byte)(c0 & 0xFE);
                events.Add(DecodeCcFrame(register, cc, state));
            }
            return events;
        }

        return Empty;
    }

    /// <summary>
    /// Decode the 5 C&amp;C bytes of one USB frame into <paramref name="state"/>,
    /// returning the decoded event. The <paramref name="register"/> is the
    /// <c>ControlFrame.CcRegister</c> identified from C0 (address bits + MOX).
    /// </summary>
    public DecodedHostCommand DecodeCcFrame(
        ControlFrame.CcRegister register,
        ReadOnlySpan<byte> cc,
        HostCommandState state)
    {
        byte c1 = cc[1], c2 = cc[2], c3 = cc[3], c4 = cc[4];

        switch (register)
        {
            case ControlFrame.CcRegister.Config:
            {
                // C1[1:0] = sample-rate code (ControlFrame.WriteConfigPayload).
                int rateCode = c1 & 0x03;
                state.SampleRateKhz = rateCode switch
                {
                    0 => 48,
                    1 => 96,
                    2 => 192,
                    3 => 384,
                    _ => 48,
                };
                // C2 = ocPins << 1. MOX selects the TX vs RX mask source.
                byte ocMask = (byte)((c2 >> 1) & 0x7F);
                if (state.Mox) state.OcTxMask = ocMask;
                else state.OcRxMask = ocMask;
                // C3[2] = preamp / LNA gain on LT2208 boards.
                state.PreampOn = (c3 & 0x04) != 0;
                // C3[6:5] = RX-input relay code (IF_RX_relay). Carries the
                // operator's RX antenna normally; 0b01 = the RX BYPASS relay
                // (Mk2PA Alex SPI bit 11) — the HermesC10 PS feedback route
                // while armed + keyed (EncodePsBypassOrRxAntennaC3Bits).
                state.P1RxRelayCode = (byte)((c3 >> 5) & 0x03);
                // C4[5:3] = number of receivers minus one.
                state.NumReceiversMinusOne = (byte)((c4 >> 3) & 0x07);

                return Make("Config",
                    $"rate={state.SampleRateKhz}k oc{(state.Mox ? "Tx" : "Rx")}=0x{ocMask:X2} " +
                    $"preamp={(state.PreampOn ? 1 : 0)} numRx={state.NumReceiversMinusOne + 1} " +
                    $"rxRelay={state.P1RxRelayCode} mox={(state.Mox ? 1 : 0)}");
            }

            case ControlFrame.CcRegister.TxFreq:
                state.TxFreqHz = BinaryPrimitives.ReadUInt32BigEndian(cc.Slice(1, 4));
                return Make("TxFreq", $"txFreq={state.TxFreqHz}Hz mox={(state.Mox ? 1 : 0)}");

            case ControlFrame.CcRegister.RxFreq:
                state.RxFreqHz = BinaryPrimitives.ReadUInt32BigEndian(cc.Slice(1, 4));
                return Make("RxFreq", $"rxFreq={state.RxFreqHz}Hz");

            case ControlFrame.CcRegister.RxFreq2:
            case ControlFrame.CcRegister.RxFreq3:
            case ControlFrame.CcRegister.RxFreq4:
            {
                // Secondary DDC NCOs — logged for observability; Zeus has no
                // split-VFO so these mirror VfoAHz. Not persisted into the
                // single RxFreqHz state field.
                long f = BinaryPrimitives.ReadUInt32BigEndian(cc.Slice(1, 4));
                return Make(register.ToString(), $"{register}={f}Hz");
            }

            case ControlFrame.CcRegister.DriveFilter:
            {
                // C1 = raw drive level. C2[0]=mic_boost, C2[1]=mic_linein
                // (Hermes-class codec boards), C2[3]=PA enable (HL2 while MOX),
                // C2[4]=ATU tune request.
                state.DriveByte = c1;
                state.MicBoost = (c2 & 0x01) != 0;
                state.MicLineIn = (c2 & 0x02) != 0;
                bool paEnable = (c2 & 0x08) != 0;
                bool atuTune = (c2 & 0x10) != 0;
                return Make("DriveFilter",
                    $"drive={c1} micBoost={(state.MicBoost ? 1 : 0)} " +
                    $"micLineIn={(state.MicLineIn ? 1 : 0)} paEnable={(paEnable ? 1 : 0)} " +
                    $"atuTune={(atuTune ? 1 : 0)}");
            }

            case ControlFrame.CcRegister.Attenuator:
            {
                // C4 carries the extended RX attenuator. Board-agnostic decode:
                //   standard HPSDR (ANAN/Hermes): 0x20 | (db & 0x1F)
                //   HL2:                          0x40 | (60 - db)
                // Distinguish by the encoding-select bit so the decoder works for
                // any P1 board without needing the board kind in HostCommandState.
                int attenDb;
                if ((c4 & 0x40) != 0)
                    attenDb = 60 - (c4 & 0x3F);          // HL2 gain-reduction form
                else
                    attenDb = c4 & 0x1F;                 // standard step-attenuator
                state.AttenuatorDb = attenDb;
                // C2[4:0] = line-in gain (HL2 + ANAN-10E share this layout).
                int lineInGain = c2 & 0x1F;
                // C2[6] = puresignal_run — the receiver-mux enable the HL2
                // AND the HermesC10 (ANAN-G2E, P1) gateware both decode at
                // register 0x0a bit 22. Persisted into state so the
                // composer→decoder tests pin the board gate non-vacuously.
                state.P1PsRun = (c2 & 0x40) != 0;
                return Make("Attenuator",
                    $"atten={attenDb}dB lineInGain={lineInGain} psRun={(state.P1PsRun ? 1 : 0)}");
            }

            case ControlFrame.CcRegister.LnaTxGainStable:
            {
                // Wire 0x1c = register 0x0e. On the HermesC10 (ANAN-G2E, P1)
                // classic Hermes v3.3 gateware C3[4:0] is atten_on_Tx — the
                // PTT-muxed TX-time ADC attenuation protecting the PS
                // feedback tap (silicon reset 31). On HL2 the same address is
                // the AD9866 FAST_LNA block and Zeus's payload is all-zero.
                // Decoding C3[4:0] pins both shapes at the emulator seam.
                state.P1AttenOnTxDb = (byte)(c3 & 0x1F);
                return Make("LnaTxGainStable",
                    $"attenOnTx={state.P1AttenOnTxDb}dB mox={(state.Mox ? 1 : 0)}");
            }

            default:
                return Make(register.ToString(),
                    $"c1=0x{c1:X2} c2=0x{c2:X2} c3=0x{c3:X2} c4=0x{c4:X2}");
        }
    }

    private static DecodedHostCommand Make(string kind, string summary) =>
        new(DateTimeOffset.UtcNow, ProtocolVersion.P1, kind, summary);
}
