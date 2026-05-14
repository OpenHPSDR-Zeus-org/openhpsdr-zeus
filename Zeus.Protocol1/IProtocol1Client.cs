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

using System.Net;
using System.Threading.Channels;
using Zeus.Contracts;
using Zeus.Protocol1.Discovery;

namespace Zeus.Protocol1;

/// <summary>
/// Surface of the Protocol-1 streaming client. One instance per radio.
/// Not thread-safe for Connect/Start/Stop/Disconnect (single-writer UI model).
/// Mutation setters are thread-safe.
/// </summary>
public interface IProtocol1Client : IDisposable
{
    /// <summary>Bind the local UDP socket and remember the radio endpoint.</summary>
    Task ConnectAsync(IPEndPoint radioEndpoint, CancellationToken ct);

    /// <summary>Send Metis start, spin up the RX + TX loops, begin IQ streaming.</summary>
    Task StartAsync(StreamConfig config, CancellationToken ct);

    /// <summary>Send Metis stop, join the RX thread, drain the socket.</summary>
    Task StopAsync(CancellationToken ct);

    /// <summary>Release the socket. Idempotent.</summary>
    Task DisconnectAsync(CancellationToken ct);

    ChannelReader<IqFrame> IqFrames { get; }

    /// <summary>Monotonic count of UDP sequence gaps observed since Start.</summary>
    long DroppedFrames { get; }

    /// <summary>Monotonic count of valid RX packets parsed since Start.</summary>
    long TotalFrames { get; }

    void SetVfoAHz(long hz);
    void SetSampleRate(HpsdrSampleRate rate);
    void SetPreamp(bool on);
    void SetAttenuator(HpsdrAtten atten);
    void SetAntennaRx(HpsdrAntenna ant);

    /// <summary>
    /// Flip the outgoing C&amp;C MOX bit (C0 LSB on every register). Read from
    /// the internal CcState snapshot on the TX thread, so every register
    /// emitted after this call carries the updated bit until cleared.
    /// </summary>
    void SetMox(bool on);

    /// <summary>
    /// UI-level TX drive, 0..100 (values outside clamp). Mapped to the 0..255
    /// raw HPSDR drive byte (C0=0x12, C1) inside SnapshotState via
    /// <c>raw = percent * 255 / 100</c>, matching the Protocol-1
    /// <c>transmitter-&gt;drive_level</c> range.
    /// </summary>
    void SetDrive(int percent);

    /// <summary>
    /// Push a fully-computed raw drive byte (0..255), overriding the percent
    /// path. RadioService uses this when PA calibration converts target watts
    /// → drive byte via the per-band gain lookup.
    /// </summary>
    void SetDriveByte(byte value);

    /// <summary>
    /// User-configured Open-Collector pin masks (7 bits each). OR'd with the
    /// board's auto-filter output. <paramref name="txMask"/> is asserted when
    /// MOX is on; <paramref name="rxMask"/> otherwise.
    /// </summary>
    void SetOcMasks(byte txMask, byte rxMask);

    /// <summary>
    /// Raised from the RX loop whenever a successfully parsed EP6 packet carried
    /// a C&amp;C echo on an AIN-bearing address (addresses 1/2/3 → C0 bytes
    /// 0x08/0x10/0x18). Fire-and-forget — handlers run synchronously on the RX
    /// thread and must not block.
    /// </summary>
    event Action<TelemetryReading>? TelemetryReceived;

    /// <summary>
    /// Raised once per successfully parsed EP6 packet with the OR-aggregated
    /// ADC overload flags from the echoed C&amp;C word. Fires at the packet rate
    /// (~1.2 kHz at 192 kSps); downstream is responsible for any throttling.
    /// Handlers run synchronously on the RX thread and must not block.
    /// </summary>
    event Action<AdcOverloadStatus>? AdcOverloadObserved;

    /// <summary>
    /// Select the radio's wire-level board family. Affects the extended
    /// attenuator byte layout (HL2 vs bare HPSDR) and the N2ADR filter-board
    /// OC pin encoding. Defaults to <see cref="HpsdrBoardKind.HermesLite2"/>.
    /// </summary>
    void SetBoardKind(HpsdrBoardKind board);

    /// <summary>
    /// Current board kind as latched via <see cref="SetBoardKind"/>. Defaults
    /// to <see cref="HpsdrBoardKind.HermesLite2"/> when discovery did not
    /// supply one.
    /// </summary>
    HpsdrBoardKind BoardKind { get; }

    /// <summary>
    /// Toggle the HL2 + N2ADR 7-relay filter board. When on, C2 bits [7:1]
    /// carry the per-band OC pin mask from <see cref="N2adrBands"/>.
    /// Defaults to <c>false</c> (bare HL2, no filter board).
    /// </summary>
    void SetHasN2adr(bool hasN2adr);

    /// <summary>
    /// Hermes-Lite 2 Band Volts PWM enable. C3 bit 3 of the Config frame is
    /// the same bit legacy HPSDR boards used for LT2208 ADC dither, which
    /// HL2's AD9866 doesn't need. Per
    /// <c>docs/references/protocol-1/hermes-lite2-protocol.md</c> line 39
    /// (<c>| 0x00 | [11] | Fan or Band Volts PWM (0=Fan, 1=Band Volts) |</c>),
    /// HL2 reuses this bit as the Band Volts PWM enable on the FAN
    /// connector — when set, the gateware emits a per-band-tagged PWM
    /// voltage so an external amplifier (e.g. Xiegu XPA125B) can
    /// auto-band-switch. mi0bot's HL2-specific Thetis fork exposes this in
    /// its UI as "Band Volts". Defaults to <c>false</c>; persisted per-
    /// radio via <c>PreferredRadioStore</c> and honoured on HL2 only.
    /// </summary>
    bool EnableHl2BandVolts { get; set; }

    /// <summary>
    /// Arm or disarm PureSignal predistortion on the wire. HL2-only effect:
    /// flips bit 22 of register 0x0a (= C2 bit 6 of the C0=0x14 frame), adds
    /// the Predistortion (0x2b) register to the rotation, and asks the
    /// gateware for 2 receivers so the EP6 packet layout switches to the
    /// 2-DDC paired form (DDC0 + DDC1, with DDC1 carrying feedback ADC
    /// samples during MOX). On non-HL2 boards this stores the flag for
    /// state-tracking only — the wire stays untouched. Issue #172.
    /// </summary>
    void SetPsEnabled(bool on);

    /// <summary>
    /// Current PS arm state, as set by <see cref="SetPsEnabled"/>. Read by
    /// DspPipelineService to gate the P1 PS feedback pump.
    /// </summary>
    bool PsEnabled { get; }

    /// <summary>
    /// Push the latest WDSP <c>calcc</c> predistortion subindex/value to
    /// register 0x2b. Subindex (0..255) lands in C1; value (clamped to
    /// 0..15) lands in C2 [3:0]. Per the HL2 protocol doc, value bits
    /// [19:16] = C2 [3:0], NOT [23:20] / C2 [7:4] (PR #119 regression).
    /// </summary>
    void SetPsPredistortion(byte value, byte subindex);

    /// <summary>
    /// HL2 TX-side step attenuator (AD9866 TX PGA) target in dB, range
    /// -28..+31. Used by <c>PsAutoAttenuateService</c> to bring the PS
    /// feedback envelope into calcc's [128, 181] convergence window. Out-of-
    /// range values are clamped to the bounds. Honoured only on HL2 during
    /// MOX with PS enabled; <see cref="ControlFrame.WriteAttenuatorPayload"/>
    /// overrides C4 with the mi0bot networkproto1.c:1086-1088 / console.cs:
    /// 10947-10948 wire encoding (<c>(31 - db) | 0x40</c>). Non-HL2 boards
    /// store the flag for state-tracking only — the wire stays untouched.
    /// </summary>
    void SetHl2TxStepAttenuationDb(int db);

    /// <summary>
    /// 1024-sample paired feedback blocks decoded from the EP6 stream when
    /// PS is armed. TX side comes from the in-flight TX-IQ ring (the
    /// samples we just wrote to the wire); RX side is DDC1, the dedicated
    /// feedback-ADC path. Single reader (the DspPipelineService PS pump).
    /// </summary>
    ChannelReader<PsFeedbackFrame> PsFeedbackFrames { get; }

    /// <summary>
    /// Diagnostic: monotonic count of PS-armed paired EP6 packets the RX
    /// loop has decoded since Start. Surfaces "is the radio actually
    /// emitting paired DDC0/DDC1 frames after PS arm?" — a value that
    /// stays at 0 after arming is the canonical "PS armed but no
    /// feedback samples reached the engine" symptom.
    /// </summary>
    long PsPairedPacketCount { get; }

    /// <summary>
    /// Register a synchronous sink to receive decoded RX frames directly on
    /// the RX OS thread, bypassing the <see cref="IqFrames"/> /
    /// <see cref="PsFeedbackFrames"/> channels. Call BEFORE
    /// <see cref="StartAsync"/> for stable lifetime semantics; a runtime swap
    /// uses <see cref="System.Threading.Interlocked.Exchange{T}(ref T, T)"/>
    /// internally and is safe but not race-free against an in-flight frame
    /// (the previous sink may receive one more callback after the call
    /// returns).
    ///
    /// While a non-null sink is attached, the RX loop calls the sink methods
    /// INSTEAD of writing to the public channels. With no sink attached, the
    /// channel-write path remains the only producer (preserves existing
    /// test-side consumers).
    ///
    /// See <see cref="IRxPacketSink"/> for the full threading contract.
    /// </summary>
    void AttachRxSink(IRxPacketSink sink);

    /// <summary>
    /// Detach the currently attached RX sink. After this returns, the
    /// channel-write path is the only producer. Safe to call from any thread;
    /// at most one further callback may complete on the detached sink before
    /// the change is observed.
    /// </summary>
    void DetachRxSink();
}
