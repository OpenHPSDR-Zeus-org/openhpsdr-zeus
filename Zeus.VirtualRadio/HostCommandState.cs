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

    /// <summary>Return a deep copy for publishing an immutable status snapshot.</summary>
    public HostCommandState Clone() => (HostCommandState)MemberwiseClone();
}
