// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

namespace Zeus.VirtualRadio;

/// <summary>
/// Mutable, running snapshot of everything the host (Zeus) has commanded the
/// virtual radio to do, as decoded from inbound EP2 frames (and, later, P2
/// command packets). The decoder mutates one shared instance in place; the
/// engine reads it to drive RX/telemetry generation and clones it
/// (<see cref="Clone"/>) when publishing an immutable status snapshot.
///
/// Fields are deliberately primitive (no coupling to Zeus's internal
/// <c>CcState</c>) so the type is protocol-agnostic — the P1 decoder and the
/// future P2 decoder both populate the same shape.
/// </summary>
public sealed class HostCommandState
{
    /// <summary>Raw transmit drive level byte (0..255) from the DriveFilter (0x12) frame C1.</summary>
    public byte DriveByte;

    /// <summary>MOX / transmit-enable as commanded on the C0 MOX bit.</summary>
    public bool Mox;

    /// <summary>Host PTT request (distinct from MOX where the wire distinguishes them).</summary>
    public bool Ptt;

    /// <summary>Commanded TX NCO frequency in Hz (TxFreq 0x02 frame).</summary>
    public long TxFreqHz;

    /// <summary>Commanded RX1 NCO frequency in Hz (RxFreq 0x04 frame).</summary>
    public long RxFreqHz;

    /// <summary>Negotiated IQ sample rate in kHz (Config frame C1[1:0] → 48/96/192/384).</summary>
    public int SampleRateKhz = 48;

    /// <summary>RX step-attenuator value in dB (extended Attenuator 0x14 frame).</summary>
    public int AttenuatorDb;

    /// <summary>RX preamp / LNA gain enable (Config frame C3[2] on LT2208 boards).</summary>
    public bool PreampOn;

    /// <summary>User open-collector TX pin mask (Config frame C2, while MOX).</summary>
    public byte OcTxMask;

    /// <summary>User open-collector RX pin mask (Config frame C2, while not MOX).</summary>
    public byte OcRxMask;

    /// <summary>Number of receivers minus one (Config frame C4[5:3]).</summary>
    public byte NumReceiversMinusOne;

    /// <summary>Whether the host has sent the EP2 start (0x04 type 0x01) command.</summary>
    public bool Running;

    /// <summary>Codec mic-boost (DriveFilter frame C2[0] on Hermes-class codec boards).</summary>
    public bool MicBoost;

    /// <summary>Codec mic/line-in select (DriveFilter frame C2[1]).</summary>
    public bool MicLineIn;

    // ---- Protocol-2 specific (populated by the P2 decoder) ----------------

    /// <summary>
    /// PureSignal single-ADC time-mux armed for the current TX burst — the
    /// <c>Mux</c> register, P2 CmdRx byte 1363 bit 1 (0x02). On the ANAN-10E
    /// (HermesII) and ANAN-G2E (HermesC10) this swings DDC0's input to the
    /// interleaved coupler/TX-DAC-reference pair (firmware Hermes.v:684-687,
    /// :1080-1093). The emulator keys its feedback layout off this bit exactly
    /// as the gateware does. Zero on Protocol 1.
    /// </summary>
    public bool PsArmedBurst;

    /// <summary>
    /// Whether the host has routed the external PureSignal feedback tap into the
    /// RX ADC for this TX burst — P2 CmdHighPriority alex0 bit 11
    /// (<c>ALEX_RX_ANTENNA_BYPASS</c> / "RX 1 Out" relay, SPI.v:47). On the
    /// single-ADC ANAN-G2E (HermesC10) the one ADC has NO internal coupler
    /// (<c>temp_ADC = INA</c>, Hermes.v:1117-1130), so it can only see the
    /// post-PA sampler tap through this relay. <see cref="PsArmedBurst"/> (byte
    /// 1363) makes DDC0 stream the interleaved coupler/reference pair — frames
    /// flow — but the coupler carries real signal ONLY when this bit is also set;
    /// with it clear the ADC is on the antenna (disconnected during TX) and the
    /// coupler is silent, so calcc never converges and the PS meter never moves
    /// ("as if PS isn't on"). Modelling this is what lets the emulator reproduce
    /// the G2E-Internal dead-meter bug instead of green-washing over it. Zero on
    /// Protocol 1.
    /// </summary>
    public bool PsCouplerRouted;

    /// <summary>
    /// TX-time ADC step-attenuator in dB — P2 CmdTx byte 59
    /// (<c>Angelia_atten_Tx0</c>, Tx_specific_C&amp;C.v:182-183). On a single-ADC
    /// PS time-mux board the DAC feedback hits the only RX ADC during TX, so
    /// this must reach the protective floor whenever PS is armed (the byte-59
    /// safety seed). Surfaced for the safety assertion. Zero on Protocol 1.
    /// </summary>
    public byte TxStepAttnDb;

    /// <summary>Number of active ADCs the host commanded (P2 CmdRx byte 4).</summary>
    public byte NumAdc = 1;

    /// <summary>Return a deep copy for publishing an immutable status snapshot.</summary>
    public HostCommandState Clone() => (HostCommandState)MemberwiseClone();
}
