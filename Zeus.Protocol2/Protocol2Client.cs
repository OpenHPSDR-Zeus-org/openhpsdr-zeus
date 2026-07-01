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
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Zeus.Contracts;

namespace Zeus.Protocol2;

public readonly record struct Protocol2TxIqDiagnostics(
    long InputComplexSamples,
    long PacketsQueued,
    long PacketsSent,
    long QueuedPackets,
    long QueueWriteFailures,
    long SendFailures,
    long ResetDrainedPackets,
    int ScratchComplexSamples,
    uint NextSequence,
    int LastPacketsPerSecond,
    long LastFifoModelSamples,
    DateTimeOffset? LastRateTimestampUtc,
    bool SenderRunning);

/// <summary>
/// Protocol 2 (OpenHPSDR "new protocol" / Thetis "ETH") streaming client.
/// Mirrors Zeus.Protocol1.Protocol1Client's lifecycle surface where it
/// overlaps; Protocol-1-only methods (HL2 dither, N2ADR filter board) are
/// absent here. Wire format verified against Thetis ChannelMaster network.c.
/// </summary>
public sealed class Protocol2Client : IDisposable, IAsyncDisposable
{
    private const int BufLen = 1444;
    private const int DiscoverySamplesPerPacket = 238;

    // Hi-priority status packet (radio → host on UDP 1025). Thetis treats it
    // as a 60-byte payload (network.c:683-756 reads up through byte 55), but
    // some firmwares pad to a longer length. Gate on "at least byte 19 valid"
    // — that's the highest offset the FWD/REV/exciter decode touches — so we
    // do not silently drop a short packet that still carries the meter ADCs.
    // 4-byte BE u32 P2 sequence header that prefixes every UDP packet, plus
    // the 20-byte hi-pri field range we actually decode (PTT/PLL @ +0,
    // exciter @ +2, FWD @ +10, REV @ +18). Real radios send 60-byte packets;
    // the guard is the minimum we need to safely read every field.
    private const int HiPriSeqHeaderBytes = 4;
    private const int HiPriStatusMinBytes = HiPriSeqHeaderBytes + 20;

    // On ANAN G2 MkII (Orion-II / Saturn) the first two DDC slots are wired
    // to the PureSignal / diversity feedback path. User-visible receivers
    // start at DDC2. pihpsdr's `new_protocol_receive_specific` and
    // `new_protocol_high_priority` both do `ddc = 2 + i` for these boards;
    // we follow the same convention. Firmware differs on whether the RX IQ
    // UDP source port is indexed by logical receiver stream or DDC slot; the
    // demux helper below accepts both forms for RX2.
    private const int G2RxDdc = 2;

    // OpenHPSDR Protocol-2 "DDC Command" (port 1025) enables DDCs via a SINGLE
    // bitmask byte at offset 7 → DDC0..DDC7, so 8 concurrent DDCs is the hard
    // protocol ceiling (verified 2026-06-21 against the openHPSDR P2 spec and
    // the matthew-wolf-n4mtt/openhpsdr-e Wireshark dissector; pihpsdr + Zeus
    // both already use only byte[7]). There is no second enable byte — "10
    // DDCs" is not representable in standard P2. On Orion-family boards DDC0/1
    // are reserved for the PureSignal feedback pair when PS is armed, leaving 6
    // user receivers; with PS off, all 8. Keep this P2 wire ceiling separate
    // from WireContract.MaxReceivers, which is the shared state capacity for
    // newer protocols such as P3.
    public const int MaxRxDdc = Zeus.Contracts.WireContract.Protocol2MaxDdc;

    // RX IQ data arrives on UDP source ports RxDataPortBase + ddcIndex, i.e.
    // 1035 (DDC0) .. 1035 + MaxRxDdc - 1 (DDC7).
    private const int RxDataPortBase = 1035;

    // Hermes-class radios (Brick2 is the live consumer) use a single ADC
    // and have no PureSignal feedback DDCs reserved at the front of the pool.
    // The user-visible RX maps to DDC0 directly. Radio sends DDC0 IQ from
    // port 1035. Reference: deskhpsdr src/new_protocol.c:1692-1698 — Hermes
    // sets `receiver[i].ddc = i` (not `i + 2` as for Orion/Angelia/MkII).
    private const int HermesRxDdc = 0;

    // 2^32 / 122_880_000 — converts Hz to a 32-bit phase-increment word.
    // The HPSDR Protocol-2 receiver mixers always operate on phase-word
    // input: the upstream HDL wires the host-supplied 32-bit phase
    // straight into the receiver cores (see `C122_phase_word` in
    // `Hermes.v:1135` and `Hermes.v:1284`, identical pattern in
    // `Orion.v`). The "bit 3 of CmdGeneral[37]" mode-select pihpsdr and
    // Thetis both set has no decoder in `General_CC.v` (and no
    // secondary decoder elsewhere in `Hermes.v` / `Orion.v` —
    // verified). Kept for parity with pihpsdr; this constant is the
    // unconditional Hz→phase scale. Issue #416.
    private const double HzToPhase = 34.952533333333333;

    private readonly ILogger<Protocol2Client> _log;
    private readonly Channel<IqFrame> _iqFrames = Channel.CreateUnbounded<IqFrame>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private Socket? _sock;
    private IPEndPoint? _radioEndpoint;
    private CancellationTokenSource? _rxCts;
    private Task? _rxTask;
    private Task? _keepaliveTask;
    private int _sampleRateKhz = 48;
    private uint _rxFreqHz = 14_200_000;
    // Second receiver (RX2) on its own DDC. When enabled, a second DDC
    // (RxBaseDdc + 1) is streamed and tuned to _rx2FreqHz independently of RX1,
    // so RX2 can sit on a different band. Read on the RX thread (HandleDdcPacket
    // tags frames by receiver) and on the command threads, so kept as volatile-
    // accessed ints/uints. _rx2Enabled: 0 = off, 1 = on.
    private int _rx2Enabled;
    private uint _rx2FreqHz = 7_100_000;
    // TX DUC NCO frequency. Normally tracks _rxFreqHz (the shared RX0/TX LO) so
    // the TX carrier lands at the RX0 frequency — byte-identical to the historic
    // single-frequency model. For dual-RX split TX (TX VFO = B with RX2 on) the
    // hosting layer sets this INDEPENDENTLY to VFO B's effective LO via
    // SetTxDucFrequency, so the TX carrier goes to VFO B WITHOUT dragging RX0/RX1
    // off VFO A (the dual-RX TUNE "two carriers, one on each RX" bug).
    // _txDucIndependent latches that override; it is cleared when split ends so
    // the DUC follows RX0 again.
    private uint _txDucFreqHz = 14_200_000;
    private volatile bool _txDucIndependent;
    // Extra user receivers (RX3+) for full multi-DDC. Contiguous after RX2:
    // receiver index i (>= 2) sits on DDC RxBaseDdc(board)+i (so RX3 = DDC
    // base+2). _extraReceiverCount counts RX3.. ; total user receivers =
    // 2 + _extraReceiverCount and RX2 MUST be enabled (no DDC gaps). Indexed by
    // receiver index: _extraRxFreqHz[2] = RX3 NCO, _extraRxAdc[2] = RX3 ADC
    // source, etc. When _extraReceiverCount == 0 the RX1/RX2 path below is
    // entirely unchanged and SendCmdRx keeps using the byte-pinned legacy
    // composer — extras are purely additive and never touch the PureSignal
    // DDC0/1 branch. Read on the RX thread + command threads (volatile-accessed).
    private int _extraReceiverCount;
    private readonly uint[] _extraRxFreqHz = new uint[MaxRxDdc];
    private readonly byte[] _extraRxAdc = new byte[MaxRxDdc];
    // Per-source-port IQ packet rate (packets in the last completed ~1 s
    // window) for UDP ports RxDataPortBase..RxDataPortBase+MaxRxDdc-1 (1035..1042)
    // → index 0..7 → DDC 0..7. Written by the single RX thread on each window
    // roll; read by the diagnostics thread under _rxPortRateLock. Surfaces
    // per-DDC RX streaming health (e.g. a receiver whose DDC the radio isn't
    // actually sending) — the ground truth for full multi-DDC operation.
    private readonly object _rxPortRateLock = new();
    private readonly long[] _rxPortRateSnapshot = new long[MaxRxDdc];
    private long _rxPortRateWindowMs;
    // Frequency-correction factor (issue #325) — dimensionless multiplier
    // near 1.0 applied to the incoming dial Hz before _rxFreqHz is updated,
    // matching piHPSDR / Thetis. Stored as int64 bits for atomic
    // Interlocked.Exchange access from arbitrary threads. 1.0 = factory
    // default (no correction).
    private long _freqCorrectionBits = BitConverter.DoubleToInt64Bits(1.0);
    private byte _numAdc = 2;
    // Connected board kind, plumbed from RadioService's ConnectedBoardKind via
    // DspPipelineService at connect time. Used by HandleDdcPacket for the
    // Hermes-on-P2 48 kHz amplitude correction (Brick2 firmware delivers IQ
    // ~29 dB hotter at 48 kHz than at higher rates — deskhpsdr
    // src/new_protocol.c:2516-2530). Unknown == no correction applied.
    private HpsdrBoardKind _boardKind = HpsdrBoardKind.Unknown;
    // 0x0A wire-byte alias variant. Default G2 matches the pre-#218 Zeus
    // assumption for every Saturn-class board; flipped to AnvelinaPro3 by
    // DspPipelineService (issue #407) when the operator (or discovery)
    // identifies the radio as Anvelina-PRO3, which unlocks the byte-1397
    // DX OC write in SendCmdHighPriority. Read-only outside the variant
    // setter.
    private OrionMkIIVariant _variant = OrionMkIIVariant.G2;
    // Mercury preamp defaults OFF — on a G2 the ADC has enough dynamic range
    // that the preamp is a crutch, not a default. Operator enables it when
    // needed via the UI. Attenuator 0 dB so the front-end isn't knocked down.
    private bool _preampOn;
    private byte _rxStepAttnDb;
    // DLE_outputs byte (`High_Priority_CC.v:195` on Orion_MkII, byte 1400).
    // Drives three physical control lines on ANAN-8000DLE / AnvelinaPro3
    // chassis (`Orion.v:2119,2122,2125`):
    //   bit 0 = XVTR_enable        (0=disabled, 1=enabled)
    //   bit 1 = IO1                (0=enabled,  1=muted)  — inverted polarity
    //   bit 2 = AUTO_TUNE          (0=disabled, 1=enabled)
    // Defaults 0 (current effective state — Zeus has been leaving the byte
    // zeroed since the buffer is `new byte[BufLen]`). Use SetXvtrEnabled /
    // SetIo1Muted / SetAutoTuneEnabled to compose. Hermes RTL doesn't
    // decode byte 1400, so emission is harmless on non-Orion_MkII boards.
    // On non-8000DLE Orion_MkII variants (G2, G2 MkII) the pins exist but
    // typically drive unconnected hardware. Variant-aware gating is a
    // separable follow-up. Issue #414.
    private byte _dleOutputs;
    // Second ADC step attenuator (0..31 dB) for ADC1 — bytes the upstream
    // HDL decodes at `High_Priority_CC.v:186-189` as `Attenuator1`. Used
    // by operators running dual-RX on dual-ADC boards (Orion_MkII, G2,
    // Saturn) where RX0/RX1 sit on separate ADCs and need independent
    // gain. Default 0 dB matches the radio's power-on state; no UI surface
    // yet, but the wire byte is in place. Issue #415.
    private byte _rx1StepAttnDb;
    // ANAN-G2/Saturn ADC dither and digital-output randomizer. These land in
    // CmdRx bytes 5 and 6 respectively; Thetis toggles ADC0..2 together from
    // the G2 options block. Defaults remain off until RadioService gates and
    // replays the persisted G2 options for a verified G2-class board.
    private bool _adcDitherEnabled;
    private bool _adcRandomEnabled;
    // TX step attenuator (0..31 dB) — Thetis network.c:1238-1242 writes the
    // same value to bytes 57/58/59 of CmdTx (one per ADC tap). The PS
    // auto-attenuate loop adjusts this when info[4] feedback level lands
    // outside the 128..181 ideal window so calcc has a chance to converge.
    // Default 0 matches the radio's power-on state and pihpsdr's untouched
    // baseline.
    private byte _txStepAttnDb;
    // Operator's TX step-att value captured at the instant the single-ADC PS
    // time-mux byte-59 protective seed (SeedsTxAdcProtection) overrode it, so the
    // value is restored verbatim on PS disarm. null = no seed is currently
    // active (the only state on every board except the 10E with its interlock
    // lifted). See SetPsFeedbackEnabled / PsTxAdcProtectFloorDb.
    private byte? _txAttnBeforePsSeed;
    // PA settings — pushed from RadioService when PaSettingsStore changes or
    // the VFO crosses a band edge. _paEnabled is the global toggle that lands
    // in CmdGeneral[58]; _driveByte is the pre-calibrated drive level for
    // CmdHighPriority[345]; _ocTxMask/_ocRxMask drive CmdHighPriority[1401].
    // The piHPSDR-style global "OCtune" override was removed in #124 for
    // hardware-safety: OC during TUN follows OcTx, identical to TX.
    private bool _paEnabled = true;
    private byte _driveByte;
    private byte _ocTxMask;
    private byte _ocRxMask;
    // Anvelina-PRO3 DX OC extension (issue #407, EU2AV
    // Open_Collector_Anvelina_DX spec). 4-bit masks (bits 0..3 -> DX OUT
    // 7..10). Wire-encoded into CmdHighPriority[1397] bits [4:1] only when
    // _boardKind == OrionMkII && _variant == AnvelinaPro3 — every other
    // board sees byte 1397 stay at 0, which is what the EU2AV spec calls
    // out as the reserved-bit safe default for non-Anvelina firmware.
    private byte _ocDxTxMask;
    private byte _ocDxRxMask;
    // TX/RX antenna relay selection (external-ports plan — antenna slice, #804).
    // 1-based wire selector (1=ANT1, 2=ANT2, 3=ANT3) — same convention as
    // EncodeTxAntennaBits, so this assembly needs no reference to the
    // HpsdrAntenna enum (which lives in Zeus.Protocol1; P2 references only
    // Zeus.Contracts). _txAntenna feeds alex0[26:24]; _rxAntenna is the
    // state-multiplexed RX-side antenna (carried on alex0 during RX). These are
    // the *applied* values — the wire reads them. While keyed, an operator
    // setter stashes into _pending* and is flushed on the next unkey edge so the
    // Alex relay matrix is never hot-switched under power. The [26:24] emission
    // is additionally gated on _hasTxAntennaRelays so non-relay boards never
    // advertise ANT2/3. -1 = nothing pending.
    private int _txAntenna = 1;
    private int _rxAntenna = 1;
    private int _pendingTxAntenna = -1;
    private int _pendingRxAntenna = -1;
    private bool _hasTxAntennaRelays;
    // RX auxiliary input select (external-ports plan — antenna slice, #804).
    // 0 = base ANT relay (no aux), 1=EXT1, 2=EXT2, 3=XVTR, 4=BYPASS(K36). When
    // non-zero the alex0 aux bits replace the base RX path. Defaults 0 →
    // byte-identical to today. The K36/BYPASS pick can NEVER override PS feedback
    // routing — PS arbitration runs AFTER the operator aux in
    // SendCmdHighPriority (the PS-K36 firewall). Deferred while keyed alongside
    // the antennas. -1 = nothing pending.
    private int _rxAuxInput;
    private int _pendingRxAuxInput = -1;
    // Whether the connected board's Alex/BPF board uses the ANAN-7000/Saturn
    // master RX-select gating (bit 14 must accompany any aux selection). Set
    // alongside the relay-population gate. MkII-BPF boards (0x0A / G2E) use it;
    // classic-Alex boards do not.
    private bool _mkiiBpfRxSelect;
    // Operator-editable RF filter matrix (Thetis-style Ant/Filters parity).
    // Null or CustomMatrixEnabled=false preserves the built-in Alex BPF/LPF
    // tables byte-for-byte. Like antenna relays, changes are deferred while
    // keyed so an edited range cannot hot-switch the filter board under power.
    private RfFilterRuntimeSettings? _rfFilters;
    private RfFilterRuntimeSettings? _pendingRfFilters;
    private bool _hasPendingRfFilters;

    // TX audio front-end (external-audio-jacks re-port). Wire-encoded into the
    // TxSpecific (CmdTx, port 1026) packet byte 50 (mic_control flags) + byte
    // 51 (line_in_gain) — Thetis network.c:1234,1236. Both default 0, which is
    // byte-identical to today's all-zero CmdTx tail. The byte-50 / byte-51
    // values are resolved as pure functions of the selected TxAudioSource
    // upstream (RadioService / ExternalPortEncoder); this client just latches
    // them and re-pushes CmdTx via SetAudioFrontEndBytes.
    private byte _micControl;
    private byte _lineInGain;

    // Internal (FPGA) CW keyer config — issue #1032. Wire-encoded into the
    // TxSpecific (CmdTx, port 1026) packet bytes 5-13/17. These are the
    // operator-driven values (CW-mode active, keyer mode, speed, sidetone);
    // the remaining gateware parameters are pinned to the Cw* constants
    // below, mirroring how the Protocol-1 path pins weight/spacing/reverse in
    // ControlFrame. Latched by SetCwKeyerConfig and re-pushed on the next
    // CmdTx, exactly like the audio front-end bytes. All-default (Inactive)
    // leaves byte 5 = 0, so a non-CW transmit is byte-identical to today.
    private bool _cwActive;
    private CwKeyerMode _cwMode;
    private int _cwSpeedWpm;
    private int _cwSidetoneHz;
    private byte _cwSidetoneLevel;

    // TxSpecific byte-5 CW mode_control bit layout — pihpsdr new_protocol.c
    // new_protocol_tx_specific (transmit_specific_buffer[5]) and OpenHPSDR
    // Ethernet Protocol v4.4 "Transmitter Specific" packet (pp.29-30). Public
    // so the wire-format golden tests can assert the canonical bit positions.
    public const byte CwModeCwSelected   = 0x02; // bit1 — internal keyer / CW selected
    public const byte CwModeReverse      = 0x04; // bit2 — swap dit/dah paddles
    public const byte CwModeIambic       = 0x08; // bit3 — iambic (Mode A)
    public const byte CwModeModeB        = 0x20; // bit5 — Mode B (set with Iambic)
    public const byte CwModeSidetone     = 0x10; // bit4 — radio sidetone enable
    public const byte CwModeStrictSpacing = 0x40; // bit6 — strict char spacing
    public const byte CwModeBreakIn      = 0x80; // bit7 — break-in (radio handles TX/RX)

    // Gateware keyer parameters that have no operator UI yet (mirrors the
    // Protocol-1 neutral defaults in ControlFrame). Held as constants and
    // sent on every CW transmit.
    //   Weight  : TxSpecific byte 10, 50% => 1:1 dit:dah (pihpsdr default).
    //   Spacing : strict letter spacing off (bug/keyer pass-through).
    //   Reverse : paddles un-swapped.
    //   BreakIn : ON. *Load-bearing for issue #1032* — without break-in the
    //             radio will not switch to TX on key-down, so the jack stays
    //             silent (the original bug). Semi/full/off is a CW-operator
    //             preference and a candidate follow-up control; ON is the only
    //             value that makes "plug a key in and it works" true.
    //   HangMs  : semi-break-in tail before the radio drops back to RX.
    //   RfDelayMs: PTT-to-RF (key-down) delay; gateware-clamped to 900/WPM
    //             (pihpsdr "FPGA iambic keyer bug" workaround, byte 13).
    //   RampMs  : CW envelope rise/fall; Thetis forces 9 (setup.cs:29327).
    private const byte CwKeyerWeight    = 50;
    private const bool CwKeyerSpacing   = false;
    private const bool CwKeyerReverse   = false;
    private const bool CwKeyerBreakIn   = true;
    private const int  CwKeyerHangMs    = 300;
    private const int  CwKeyerRfDelayMs = 8;
    private const byte CwKeyerRampMs    = 9;

    // TxSpecific byte-50 mic_control bit layout (Thetis network.c:1226-1233).
    // Public so the wire-format golden tests can assert against the canonical
    // bit positions. The byte itself is resolved upstream from the selected
    // TxAudioSource (ExternalPortAudio in Zeus.Server.Hosting).
    public const byte MicControlLineIn   = 0x01; // bit0 — line-in select
    public const byte MicControlMicBoost = 0x02; // bit1 — mic 20 dB boost
    public const byte MicControlMicBias  = 0x10; // bit4 — Orion mic bias enable
    public const byte MicControlXlr      = 0x20; // bit5 — balanced/XLR input

    private bool _moxOn;
    private bool _tuneActive;
    private long _totalFrames;
    private long _droppedFrames;
    private uint _lastDdc0Seq;
    private bool _haveFirstDdc0;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    // Per-stream sequence counters. The G2 firmware tracks seq per destination
    // port; sharing one counter across CmdGeneral/CmdRx/CmdTx/CmdHighPriority
    // makes the first HighPriority packet land at seq=2+, which the radio
    // treats as "stream started mid-flight" and silently drops — leaving the
    // DDC locked to whatever the previous client's last tune was.
    private uint _seqCmdGeneral;
    private uint _seqCmdRx;
    private uint _seqCmdTx;
    private uint _seqCmdHp;
    private uint _seqCmdTxIq;

    // TX-DUC IQ accumulator. WDSP TXA emits 1024/2048-sample blocks; the P2
    // wire format wants 240 complex samples per 1444-byte packet on port 1029.
    // We buffer into this 240-pair scratch and enqueue whenever it fills.
    private const int TxIqSamplesPerPacket = 240;
    // DAC rate = 192 kHz. 240 samples = 1.25 ms of audio per packet. A steady
    // 1 packet / 1.25 ms stream keeps the radio's TX FIFO level instead of
    // bursting (which shows up on the air as a pulsed / AM-modulated carrier
    // with multi-tone-looking sidebands).
    private const double TxDacSampleRate = 192_000.0;
    // Mirrors pihpsdr new_protocol.c:1972 — target FIFO fill of ~1250 samples
    // (6.5 ms of audio buffered in the radio). Past that we pace; below it
    // we send as fast as packets are ready so the radio never underruns.
    private const double TxFifoTargetSamples = 1250.0;
    // Keep live TX realtime if the producer briefly overruns the paced DUC
    // sender. 32 packets is about 40 ms at 800 packets/s, enough for normal
    // WDSP bursts but too small to become audible delayed speech.
    private const int TxIqMaxQueuedPackets = 32;
    private readonly float[] _txIqScratch = new float[TxIqSamplesPerPacket * 2];
    private int _txIqScratchCount;
    private readonly object _txIqGate = new();
    private readonly Channel<byte[]> _txIqQueue = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true });
    private Task? _txIqSenderTask;
    private long _txIqInputComplexSamples;
    private long _txIqPacketsQueued;
    private long _txIqPacketsSent;
    private long _txIqQueuedPackets;
    private long _txIqQueueWriteFailures;
    private long _txIqSendFailures;
    private long _txIqResetDrainedPackets;
    private int _txIqLastPacketsPerSecond;
    private long _txIqLastFifoModelSamples;
    private long _txIqLastRateUtcTicks;

    // ---- PureSignal feedback (DDC0 + DDC1 paired on UDP 1035) ----
    // PS feedback decoder: when armed, packets on port 1035 carry interleaved
    // (DDC0=PS_RX_FEEDBACK=post-PA coupler IQ, DDC1=PS_TX_FEEDBACK=TX-DAC
    // loopback IQ) sample pairs (pihpsdr new_protocol.c:1615-1616, 2463-2510;
    // transmitter.c:2015-2030, 2066). DDC0 feeds pscc's "rx" arg; DDC1 feeds
    // pscc's "tx" arg. The accumulator collects 1024 complex pairs across
    // packets before emitting a frame. When PS is disarmed the radio reverts
    // to single-DDC packets and the standard RX demuxer takes over.
    // Volatile because RxLoop reads it across threads.
    private volatile bool _psFeedbackEnabled;
    // PS feedback source — false=Internal coupler (default), true=External
    // (Bypass). When externally bypassing, alex0 gains ALEX_RX_ANTENNA_BYPASS
    // (bit 11) during xmit + PS armed. RxSpecific/TxSpecific are byte-
    // identical between sources — only this one alex0 bit differs.
    // Reference: pihpsdr new_protocol.c:1284-1296 alex0 bypass selection.
    private volatile bool _psFeedbackExternal;
    private const int PsFeedbackBlockSize = 1024;
    private readonly Channel<PsFeedbackFrame> _psFeedbackFrames = Channel.CreateUnbounded<PsFeedbackFrame>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly float[] _psTxI = new float[PsFeedbackBlockSize];
    private readonly float[] _psTxQ = new float[PsFeedbackBlockSize];
    private readonly float[] _psRxI = new float[PsFeedbackBlockSize];
    private readonly float[] _psRxQ = new float[PsFeedbackBlockSize];
    private int _psBlockFill;
    private ulong _psBlockStartSeq;

    // ---- HermesC10 (ANAN-G2E) PS wire instrumentation (issue #960) ----
    // Read-only ~1 Hz diagnostic of the single-ADC time-mux PS feedback burst,
    // gated on _boardKind == HermesC10 (see ShouldLogG2ePsWireDiag) so it is
    // inert for every other board. It logs the byte-59 attenuation actually on
    // the wire, the DDC0 feedback frame count on port 1035, and the peak coupler
    // (rx_I[0]) and reference (rx_I[NR]) sample magnitudes from the de-interleaved
    // pair — the numbers that make the next real-G2E bench (lb5va, #960) decisive
    // on whether feedback is flowing and at what level. Pure logging: no wire
    // write, no socket I/O, no hot-path allocation. Touched only from the RX
    // thread inside HandlePsPairedPacket, so no synchronisation is needed.
    private long _g2ePsDiagWindowStartMs;
    private long _g2ePsDiagFramesInWindow;
    private float _g2ePsDiagPeakCoupler;
    private float _g2ePsDiagPeakReference;

    public Protocol2Client(ILogger<Protocol2Client> log)
    {
        _log = log;
    }

    public ChannelReader<IqFrame> IqFrames => _iqFrames.Reader;
    public long TotalFrames => Interlocked.Read(ref _totalFrames);
    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);
    public Protocol2TxIqDiagnostics TxIqDiagnosticsSnapshot()
    {
        int scratchComplexSamples;
        uint nextSequence;
        lock (_txIqGate)
        {
            scratchComplexSamples = _txIqScratchCount / 2;
            nextSequence = _seqCmdTxIq;
        }

        long lastRateTicks = Interlocked.Read(ref _txIqLastRateUtcTicks);
        return new Protocol2TxIqDiagnostics(
            InputComplexSamples: Interlocked.Read(ref _txIqInputComplexSamples),
            PacketsQueued: Interlocked.Read(ref _txIqPacketsQueued),
            PacketsSent: Interlocked.Read(ref _txIqPacketsSent),
            QueuedPackets: Math.Max(0, Interlocked.Read(ref _txIqQueuedPackets)),
            QueueWriteFailures: Interlocked.Read(ref _txIqQueueWriteFailures),
            SendFailures: Interlocked.Read(ref _txIqSendFailures),
            ResetDrainedPackets: Interlocked.Read(ref _txIqResetDrainedPackets),
            ScratchComplexSamples: scratchComplexSamples,
            NextSequence: nextSequence,
            LastPacketsPerSecond: Volatile.Read(ref _txIqLastPacketsPerSecond),
            LastFifoModelSamples: Interlocked.Read(ref _txIqLastFifoModelSamples),
            LastRateTimestampUtc: lastRateTicks > 0
                ? new DateTimeOffset(lastRateTicks, TimeSpan.Zero)
                : null,
            SenderRunning: _txIqSenderTask is { IsCompleted: false });
    }

    // ---- Synchronous RX sink (iter5: collapse pumps onto RxLoop thread) -----
    // Optional sink attached via AttachRxSink. When non-null, RxLoop calls
    // sink.OnIqFrame / sink.OnPsFeedbackFrame DIRECTLY instead of writing to
    // the channels — this eliminates the Channel<T> -> WaitToReadAsync ->
    // ThreadPool wake-up amplification we measured in iter4. The channel-write
    // fallback remains when no sink is attached, so tests and in-process
    // probes continue to work.
    //
    // Mirrors the P1 surface; the IqFrame / PsFeedbackFrame types are
    // per-protocol because the protocol projects do not reference each other.
    private IRxPacketSink? _rxSink;

    // ---- Radio-mic stream (UDP 1026) — external-audio-jacks re-port --------
    // The radio digitises its analog jack (mic / line-in / balanced-XLR) and
    // ships it on source port 1026. We DROP it by default (the RX loop just
    // skips it) — there is ZERO added RX cost unless a radio audio source is
    // armed and a handler is attached via AttachRadioMicHandler. The handler is
    // called SYNCHRONOUSLY on the RX loop thread with a span over the receive
    // buffer; it must copy/consume before returning (RadioMicReceiver does, by
    // re-blocking into its own accumulator). volatile so the attach/detach on a
    // source switch is a full fence against the RX thread.
    private volatile RadioMicPacketHandler? _radioMicHandler;

    /// <summary>
    /// Synchronous handler for one decoded UDP-1026 radio-mic packet. Invoked on
    /// the RX loop thread with a span over the live receive buffer — copy before
    /// returning. The packet is the raw 132-byte 1026 payload (4-byte BE seq +
    /// 64 × int16 BE @ 48 kHz); decoding/re-blocking is the handler's job.
    /// </summary>
    public delegate void RadioMicPacketHandler(ReadOnlySpan<byte> packet);

    /// <summary>
    /// Attach the radio-mic (UDP 1026) handler. Until this is set the 1026
    /// stream is dropped with one cheap srcPort comparison — Host-default has no
    /// added cost. Pass <see cref="DetachRadioMicHandler"/> on a switch back to
    /// Host.
    /// </summary>
    public void AttachRadioMicHandler(RadioMicPacketHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _radioMicHandler = handler;
    }

    /// <summary>Detach the radio-mic handler, reverting to drop-on-RX.</summary>
    public void DetachRadioMicHandler() => _radioMicHandler = null;

    /// <summary>
    /// Attach a synchronous RX sink. While non-null, the RX loop calls the
    /// sink directly INSTEAD of writing to <see cref="IqFrames"/> /
    /// <see cref="PsFeedbackFrames"/>. See <see cref="IRxPacketSink"/> for
    /// the threading contract.
    /// </summary>
    public void AttachRxSink(IRxPacketSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        Interlocked.Exchange(ref _rxSink, sink);
    }

    /// <summary>Detach the current RX sink, reverting to the channel-write path.</summary>
    public void DetachRxSink() => Interlocked.Exchange(ref _rxSink, null);

    /// <summary>
    /// Raised from the RX loop on every successfully received hi-priority
    /// status packet (UDP 1025, 60 B). Carries the FWD/REF/exciter ADC
    /// readings that drive the operator's TX power meter, plus the PTT-in
    /// and PLL-lock status bits. Mirrors P1's
    /// <c>IProtocol1Client.TelemetryReceived</c> surface.
    /// Fire-and-forget — handlers run synchronously on the RX thread and must
    /// not block. Issue #174 (G2 / P2 TX power meter shows zero).
    /// </summary>
    public event Action<P2TelemetryReading>? TelemetryReceived;

    /// <summary>
    /// Raised with the raw Protocol-2 hi-priority status payload, excluding
    /// the 4-byte sequence prefix. The byte array is copied from the RX loop's
    /// reusable socket buffer so diagnostic consumers can build maps safely.
    /// </summary>
    public event Action<byte[]>? HiPriorityStatusPayloadReceived;

    /// <summary>
    /// Monotonic count of hi-priority status (UDP 1025) packets parsed since
    /// Start. Diagnostic — lets the operator confirm the radio is actually
    /// publishing PA telemetry, separately from whether the watts math
    /// looks right. Read by the 1 Hz log line in
    /// <see cref="RxLoop(System.Threading.CancellationToken)"/>.
    /// </summary>
    public long HiPriPacketCount => Interlocked.Read(ref _hiPriPackets);
    private long _hiPriPackets;

    public Task ConnectAsync(IPEndPoint radioEndpoint, CancellationToken ct)
    {
        if (_sock is not null)
            throw new InvalidOperationException("Already connected.");

        _radioEndpoint = new IPEndPoint(radioEndpoint.Address, 1024);
        var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        DisableUdpConnReset(sock);
        var localBind = FindLocalAddressForSubnet(radioEndpoint.Address) ?? IPAddress.Any;
        // Matched port convention — PC binds 1025, radio sends back with source
        // ports 1025/1026/1027/1035.. which we demux by fromaddr.
        //
        // Fall back to an ephemeral local port if 1025 is already taken. This
        // happens when Zeus runs on the SAME host as the radio's network app —
        // e.g. a Saturn/ANAN-G2 all-in-one where the on-board p2app already
        // owns UDP 1025 — and the hard-coded bind would otherwise fail the
        // whole P2 connect with EADDRINUSE (issue: 500 on /api/connect/p2,
        // co-located radio). Every openHPSDR P2 radio streams its data back to
        // the client's *source* port (p2app sets reply_addr.sin_port =
        // sender's port and never overrides it per stream), so any local port
        // works — we don't need 1025 to receive, only to send from. Separate-
        // host setups keep 1025 and behave exactly as before.
        int localPort = 1025;
        try
        {
            sock.Bind(new IPEndPoint(localBind, 1025));
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            sock.Bind(new IPEndPoint(localBind, 0));
            localPort = ((IPEndPoint)sock.LocalEndPoint!).Port;
            _log.LogInformation(
                "p2.connect local UDP 1025 in use (co-located radio app?) — bound ephemeral port {Port}",
                localPort);
        }
        // 4 MiB RX socket buffer. At 1536 kHz a single DDC pushes ~9.3 MB/s, and
        // with several DDCs streaming concurrently the kernel buffer must absorb
        // scheduling jitter without dropping datagrams. 4 MiB ≈ 110 ms of one
        // full-rate DDC (the OS may clamp to rmem_max on Linux — a hint, not a
        // guarantee). See ComputeInQueueCapacity for the matching DSP-side cushion.
        try { sock.ReceiveBufferSize = 4 << 20; }
        catch (SocketException) { sock.ReceiveBufferSize = 1 << 20; }
        _sock = sock;
        _log.LogInformation(
            "p2.connect radio={Radio} localBind={Local} localPort={Port}",
            radioEndpoint.Address,
            localBind.Equals(IPAddress.Any) ? "ANY (no subnet match)" : localBind.ToString(),
            localPort);
        return Task.CompletedTask;
    }

    public Task StartAsync(int sampleRateKhz, CancellationToken ct)
    {
        if (_sock is null || _radioEndpoint is null)
            throw new InvalidOperationException("Call ConnectAsync first.");
        if (_rxTask is not null)
            throw new InvalidOperationException("Already started.");

        _sampleRateKhz = sampleRateKhz;

        // macOS-only: pre-prime the kernel ARP table for the radio's IP
        // before we fire off any real packets. The Brick2 firmware answers
        // ARP requests incorrectly (DL1BZ in Zeus issue #171: deskhpsdr hit
        // the same problem and shipped this workaround at
        // src/new_protocol.c:453-474). Without an entry the first SendTo
        // races the ARP resolution and the radio's streams never start.
        // Linux/Windows hosts haven't shown the same symptom; gate strictly
        // on macOS so we don't introduce side-effects on platforms where the
        // OS handles ARP correctly out of the box.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            PrimeMacOSUdpRoute(_radioEndpoint!.Address, _log);
        }

        // Startup sequence matches Thetis SendStart() and Priapus/NextGenSDR:
        // CmdGeneral → CmdRx → CmdTx → CmdHighPriority(run=1). Skipping CmdTx
        // leaves the G2 MkII in a half-configured state where its BPF board
        // latches a random band instead of honouring CmdHighPriority filter
        // bits on subsequent tunes.
        SendCmdGeneral();
        Thread.Sleep(50);
        SendCmdRx();
        Thread.Sleep(50);
        SendCmdTx();
        Thread.Sleep(50);
        SendCmdHighPriority(run: true);

        _rxCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // LongRunning → dedicated thread (not a pooled worker). The RX loop and
        // the TX-IQ sender both run for the whole session and promote their own
        // thread to the platform pro-audio class (RealtimeThreadPriority). The
        // promotion is per-thread, so they must own a thread that won't be
        // recycled into the pool with elevated priority still attached. This is
        // the desktop-mode fix (#559): under Photino the WebView render shares
        // the process and at default priority preempts the TX feed in bursts —
        // average pkts/s looks fine but the sub-block jitter starves the radio
        // TX FIFO → dirty two-tone. Clean in web (separate processes) without it.
        _rxTask = Task.Factory.StartNew(
            () => RxLoop(_rxCts.Token),
            _rxCts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _keepaliveTask = Task.Run(() => KeepaliveLoop(_rxCts.Token));
        // Paced TX IQ sender — drains the queue FlushTxIqLocked fills and
        // holds the radio's DUC FIFO at a steady level. Dedicated promoted
        // thread + synchronous pacing (no await → can't bounce to the pool and
        // lose the promotion mid-transmit).
        _txIqSenderTask = Task.Factory.StartNew(
            () => TxIqSenderLoop(_rxCts.Token),
            _rxCts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _log.LogInformation("p2.start rate={Rate}kHz freq={Freq}Hz", _sampleRateKhz, _rxFreqHz);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_rxTask is null) return;

        SendCmdHighPriority(run: false);
        _rxCts?.Cancel();
        try { await _rxTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _rxTask = null;
        if (_keepaliveTask is not null)
        {
            try { await _keepaliveTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            _keepaliveTask = null;
        }
        _txIqQueue.Writer.TryComplete();
        if (_txIqSenderTask is not null)
        {
            try { await _txIqSenderTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            _txIqSenderTask = null;
        }
        _rxCts?.Dispose();
        _rxCts = null;
        _iqFrames.Writer.TryComplete();
        _log.LogInformation("p2.stop totalFrames={Total} dropped={Drop}", _totalFrames, _droppedFrames);
    }

    public void SetVfoAHz(long hz)
    {
        double factor = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _freqCorrectionBits));
        // Host-side multiplicative correction, applied right before the
        // phase-word feed slot (matches piHPSDR src/new_protocol.c:765,824,
        // Thetis NetworkIO.VFOfreq, deskHPSDR src/new_protocol.c:909,967).
        // _rxFreqHz then feeds the NCO phase-word at line 951
        // (`rxPhase = _rxFreqHz * HzToPhase`).
        long corrected = (long)Math.Round(hz * factor, MidpointRounding.AwayFromZero);
        _rxFreqHz = (uint)Math.Clamp(corrected, 0L, uint.MaxValue);
        // The TX DUC follows RX0 unless an independent split-TX freq is latched.
        if (!_txDucIndependent) _txDucFreqHz = _rxFreqHz;
        var running = _rxTask is not null;
        _log.LogInformation("p2.tune hz={Hz} running={Running} hpSeq={Seq}",
            _rxFreqHz, running, _seqCmdHp);
        if (running) SendCmdHighPriority(run: true);
    }

    /// <summary>
    /// Set the TX DUC NCO frequency independently of the shared RX0/TX LO. Used
    /// for dual-RX split TX (TX VFO = B with RX2 enabled): the TX carrier must
    /// land on VFO B while RX1 keeps receiving VFO A, so the TX DUC can no longer
    /// be tied to RX0's frequency. <paramref name="hz"/> &lt;= 0 clears the
    /// override and the DUC follows RX0 again (the non-split / single-VFO model,
    /// byte-identical to before). Applies the same host-side frequency correction
    /// as <see cref="SetVfoAHz"/>. Re-sends the high-priority packet when live.
    /// </summary>
    public void SetTxDucFrequency(long hz)
    {
        if (hz <= 0)
        {
            if (!_txDucIndependent && _txDucFreqHz == _rxFreqHz) return;
            _txDucIndependent = false;
            _txDucFreqHz = _rxFreqHz;
        }
        else
        {
            double factor = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _freqCorrectionBits));
            long corrected = (long)Math.Round(hz * factor, MidpointRounding.AwayFromZero);
            uint next = (uint)Math.Clamp(corrected, 0L, uint.MaxValue);
            if (_txDucIndependent && _txDucFreqHz == next) return;
            _txDucFreqHz = next;
            _txDucIndependent = true;
        }
        if (_rxTask is not null) SendCmdHighPriority(run: true);
    }

    /// <summary>
    /// The wire DDC index used for the second receiver (RX2): the DDC right
    /// after the primary RX DDC. On Orion-family boards RX1 is DDC2, so RX2 is
    /// DDC3; on Hermes-class RX1 is DDC0, so RX2 is DDC1. The PureSignal
    /// feedback pair (DDC0/DDC1 on Orion-family) is reserved separately and is
    /// only active while PS is armed, so it does not collide with RX2's DDC3.
    /// </summary>
    public static int Rx2Ddc(HpsdrBoardKind board) => RxBaseDdc(board) + 1;

    internal static int ReceiverIndexForRxStream(
        int streamIndex,
        HpsdrBoardKind board,
        bool rx2Enabled)
    {
        if (!rx2Enabled) return 0;
        if (streamIndex == 1) return 1;
        return streamIndex == Rx2Ddc(board) ? 1 : 0;
    }

    /// <summary>
    /// N-receiver overload: maps an incoming RX IQ stream (UDP source port
    /// 1035+streamIndex) to its logical receiver index for the full multi-DDC
    /// case. <paramref name="extraReceiverCount"/> is the number of receivers
    /// beyond RX2 (RX3..). With no extras this delegates to the RX1/RX2 mapping.
    /// Receivers are contiguous DDCs base+0..base+(1+extra); G2/Orion firmware
    /// tags streams by DDC slot (preferred), other builds by logical stream
    /// index (fallback).
    /// </summary>
    internal static int ReceiverIndexForRxStream(
        int streamIndex,
        HpsdrBoardKind board,
        bool rx2Enabled,
        int extraReceiverCount)
    {
        if (extraReceiverCount <= 0)
            return ReceiverIndexForRxStream(streamIndex, board, rx2Enabled);
        int baseDdc = RxBaseDdc(board);
        int top = 1 + extraReceiverCount;          // highest active receiver index
        if (streamIndex >= baseDdc && streamIndex - baseDdc <= top)
            return streamIndex - baseDdc;          // DDC-slot firmware (G2/Orion)
        if (streamIndex >= 0 && streamIndex <= top)
            return streamIndex;                    // logical-stream firmware
        return 0;
    }

    /// <summary>
    /// IQ packet rate (packets in the last completed ~1 s window) per UDP
    /// source port 1035..1042 → index 0..7 → DDC 0..7. The RX2 DDC is
    /// <see cref="Rx2Ddc"/>. A zero at an enabled DDC's index means the radio
    /// isn't streaming that receiver at all. Returns a
    /// fresh copy; safe to call from any thread.
    /// </summary>
    public long[] SnapshotRxPortPacketRates()
    {
        lock (_rxPortRateLock)
        {
            return (long[])_rxPortRateSnapshot.Clone();
        }
    }

    /// <summary>
    /// Enable or disable the second receiver's DDC. Toggling re-sends the RX
    /// command (DDC enable mask) and the high-priority packet (NCO phase words).
    /// Idempotent — a no-op when the state is unchanged so steady RX traffic
    /// doesn't reconfigure the DDCs every state push.
    /// </summary>
    public void SetRx2Enabled(bool on)
    {
        int next = on ? 1 : 0;
        if (Interlocked.Exchange(ref _rx2Enabled, next) == next) return;
        if (_rxTask is not null)
        {
            SendCmdRx();
            SendCmdHighPriority(run: true);
        }
    }

    /// <summary>
    /// Tune the second receiver's DDC NCO (RX2). Mirrors <see cref="SetVfoAHz"/>:
    /// applies the host-side frequency-correction factor, then re-pushes the
    /// high-priority packet so the RX2 DDC retunes. Caller passes the effective
    /// LO (dial ∓ CW pitch), matching how RX1 is fed RadioLoHz.
    /// </summary>
    public void SetVfoBHz(long hz)
    {
        double factor = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _freqCorrectionBits));
        long corrected = (long)Math.Round(hz * factor, MidpointRounding.AwayFromZero);
        _rx2FreqHz = (uint)Math.Clamp(corrected, 0L, uint.MaxValue);
        if (_rxTask is not null && Volatile.Read(ref _rx2Enabled) != 0)
            SendCmdHighPriority(run: true);
    }

    /// <summary>
    /// Configure the extra user receivers beyond RX2 (RX3..) for full multi-DDC.
    /// <paramref name="count"/> is the number of receivers past RX2 (0 = RX1/RX2
    /// only, the legacy path). Extras are contiguous — receiver index 2+i sits on
    /// DDC <see cref="RxBaseDdc"/>+2+i — so RX2 must be enabled when count &gt; 0.
    /// <paramref name="adcSources"/> gives the per-extra ADC source (index 0 =
    /// RX3); null/short entries default to ADC0. Re-sends the RX command (DDC
    /// enable mask) and high-priority packet (NCO phase words). The RX1/RX2 path
    /// and the PureSignal DDC0/1 branch are untouched.
    /// </summary>
    public void SetExtraReceivers(int count, IReadOnlyList<byte>? adcSources = null)
    {
        int maxExtras = MaxRxDdc - RxBaseDdc(_boardKind) - 2;
        int clamped = Math.Clamp(count, 0, Math.Max(0, maxExtras));
        for (int k = 0; k < clamped; k++)
        {
            int rcvr = 2 + k;
            _extraRxAdc[rcvr] = adcSources is not null && k < adcSources.Count ? adcSources[k] : (byte)0;
        }
        if (Interlocked.Exchange(ref _extraReceiverCount, clamped) == clamped) return;
        if (_rxTask is not null)
        {
            SendCmdRx();
            SendCmdHighPriority(run: true);
        }
    }

    /// <summary>
    /// Tune an extra receiver's DDC NCO. <paramref name="receiverIndex"/> is the
    /// logical receiver index (2 = RX3, 3 = RX4, ...). Applies the host-side
    /// frequency-correction factor like <see cref="SetVfoAHz"/> /
    /// <see cref="SetVfoBHz"/>. No-op for indices outside the configured extra
    /// range.
    /// </summary>
    public void SetExtraReceiverFreqHz(int receiverIndex, long hz)
    {
        if (receiverIndex < 2 || receiverIndex >= 2 + Volatile.Read(ref _extraReceiverCount))
            return;
        double factor = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _freqCorrectionBits));
        long corrected = (long)Math.Round(hz * factor, MidpointRounding.AwayFromZero);
        uint next = (uint)Math.Clamp(corrected, 0L, uint.MaxValue);
        // Idempotent — a no-op when this DDC's NCO is unchanged. OnRadioStateChanged
        // re-pushes every extra receiver's frequency on EVERY state broadcast, and
        // those fire continuously during steady RX. Without this guard each one
        // re-sent the high-priority packet, re-latching the alex/OC band relays —
        // which chattered the relays the moment RX3 was exposed. Mirrors the same
        // idempotency the RX2 DDC path (SetRx2Enabled / SetExtraReceivers) relies on.
        if (_extraRxFreqHz[receiverIndex] == next) return;
        _extraRxFreqHz[receiverIndex] = next;
        if (_rxTask is not null) SendCmdHighPriority(run: true);
    }

    /// <summary>
    /// Sets the per-radio frequency-correction factor (issue #325). The
    /// caller is responsible for re-pushing the current dial Hz via
    /// <see cref="SetVfoAHz"/> after this so the new factor reaches the
    /// wire — this method on its own only mutates the multiplier used by
    /// the next tune-write.
    /// </summary>
    public void SetFrequencyCorrectionFactor(double factor) =>
        Interlocked.Exchange(ref _freqCorrectionBits, BitConverter.DoubleToInt64Bits(factor));

    public double FrequencyCorrectionFactor =>
        BitConverter.Int64BitsToDouble(Interlocked.Read(ref _freqCorrectionBits));

    /// <summary>
    /// Test seam (issue #325): the corrected NCO frequency that would be
    /// written to the wire on the next CmdHighPriority. Equals the last
    /// <see cref="SetVfoAHz"/> argument multiplied by
    /// <see cref="FrequencyCorrectionFactor"/>.
    /// </summary>
    internal uint CorrectedRxFreqHzForTesting => _rxFreqHz;

    /// <summary>Test accessor: the (correction-applied) TX DUC NCO frequency.</summary>
    internal uint TxDucFreqHzForTesting => _txDucFreqHz;

    /// <summary>Test accessor: whether the TX DUC is on the independent split-TX override.</summary>
    internal bool TxDucIndependentForTesting => _txDucIndependent;

    public void SetSampleRateKhz(int rateKhz)
    {
        _sampleRateKhz = rateKhz;
        if (_rxTask is not null)
        {
            SendCmdRx();
        }
    }

    /// <summary>
    /// Set the connected board kind. Should be called once at connect time,
    /// before <see cref="StartAsync"/>. Plumbed from
    /// <c>DspPipelineService.ConnectP2Async</c> so RX-decode quirks scoped to
    /// specific board families can be applied without polluting the hot path
    /// with an out-of-band lookup.
    /// </summary>
    public void SetBoardKind(HpsdrBoardKind kind)
    {
        _boardKind = kind;
    }

    /// <summary>
    /// 0x0A wire-byte alias variant (issue #218). Pushed from
    /// <c>DspPipelineService.ConnectP2Async</c> alongside
    /// <see cref="SetBoardKind"/>. Only consulted when
    /// <c>_boardKind == OrionMkII</c>; ignored otherwise. Selecting
    /// <see cref="OrionMkIIVariant.AnvelinaPro3"/> unlocks the byte 1397
    /// DX OC write in <see cref="SendCmdHighPriority"/> for the EU2AV
    /// Anvelina-PRO3 extension (issue #407).
    /// </summary>
    public void SetOrionMkIIVariant(OrionMkIIVariant variant)
    {
        _variant = variant;
        if (_rxTask is not null) SendCmdHighPriority(run: true);
    }

    /// <summary>
    /// Per-board, per-sample-rate gain correction applied on top of the
    /// int24→[-1,+1] normalisation in <c>HandleDdcPacket</c>.
    ///
    /// Hermes-class radios on Protocol 2 (notably the Brick2 SDR, which is
    /// Hermes-fork firmware on Hermes-Lite-style hardware) deliver RX IQ at
    /// 48 kHz with ~+29 dB more amplitude than at higher sample rates,
    /// because the on-board CIC/decimator gain isn't compensated by the
    /// firmware at the lowest decimation factor. Without this scaler the
    /// S-meter and panadapter clip / saturate at 48 kHz on Brick2 — the
    /// symptom that left linoobs's waterfall blank in issue #171.
    ///
    /// Reference: deskhpsdr <c>src/new_protocol.c:2516-2530</c> — the
    /// scaler is gated on <c>NEW_DEVICE_HERMES</c> (wire byte 0x01) only,
    /// NOT on <c>NEW_DEVICE_HERMES2</c> (wire byte 0x02). HermesII firmware
    /// (ANAN-10E / ANAN-100B) does not exhibit the +29 dB 48 kHz lift, so
    /// the scaler must stay gated on <see cref="HpsdrBoardKind.Hermes"/>
    /// alone — widening it to HermesII would knock 29 dB off legitimate
    /// ANAN-10E / ANAN-100B RX. Constant <c>0.0354813389</c> is
    /// <c>10^(-29/20)</c>.
    ///
    /// All other (board, rate) combinations return 1.0 — vanilla HL2, ANAN
    /// 10E / 100B, ANAN G2 / 7000DLE / 8000DLE / Orion, etc. are unaffected.
    /// </summary>
    public static double IqGainCorrection(HpsdrBoardKind board, int sampleRateKhz)
    {
        if (board == HpsdrBoardKind.Hermes && sampleRateKhz == 48)
        {
            return 0.0354813389; // -29 dB
        }
        return 1.0;
    }

    public void SetNumAdc(byte numAdc)
    {
        _numAdc = numAdc;
    }

    public void SetPreamp(bool on)
    {
        _preampOn = on;
        if (_rxTask is not null) SendCmdHighPriority(run: true);
    }

    public void SetAttenuator(int db)
    {
        _rxStepAttnDb = (byte)Math.Clamp(db, 0, 31);
        if (_rxTask is not null) SendCmdHighPriority(run: true);
    }

    /// <summary>
    /// Enable / disable the XVTR control line on ANAN-8000DLE / Anvelina
    /// chassis (`DLE_outputs[0]` per `Orion.v:2119`). Other boards / variants:
    /// no observable effect (Hermes doesn't decode byte 1400; non-8000DLE
    /// Orion_MkII variants leave the pin unconnected).
    /// </summary>
    public void SetXvtrEnabled(bool enabled)
    {
        _dleOutputs = enabled
            ? (byte)(_dleOutputs | 0x01)
            : (byte)(_dleOutputs & ~0x01);
        if (_rxTask is not null) SendCmdHighPriority(run: true);
    }

    /// <summary>
    /// Mute / unmute the IO1 control line on ANAN-8000DLE / Anvelina
    /// chassis (`DLE_outputs[1]` per `Orion.v:2122`). Polarity is inverted:
    /// the RTL comment is "low to enable, high to mute" — `muted=true` sets
    /// the bit.
    /// </summary>
    public void SetIo1Muted(bool muted)
    {
        _dleOutputs = muted
            ? (byte)(_dleOutputs | 0x02)
            : (byte)(_dleOutputs & ~0x02);
        if (_rxTask is not null) SendCmdHighPriority(run: true);
    }

    /// <summary>
    /// Enable / disable AUTO_TUNE on ANAN-8000DLE / Anvelina chassis
    /// (`DLE_outputs[2]` per `Orion.v:2125`).
    /// </summary>
    public void SetAutoTuneEnabled(bool enabled)
    {
        _dleOutputs = enabled
            ? (byte)(_dleOutputs | 0x04)
            : (byte)(_dleOutputs & ~0x04);
        if (_rxTask is not null) SendCmdHighPriority(run: true);
    }

    /// <summary>
    /// Set the second-ADC RX step attenuator (0..31 dB), written to byte
    /// 1442 of the High Priority Command (`Attenuator1` per
    /// `High_Priority_CC.v:186-189`). Used for dual-RX dual-ADC boards
    /// where RX1 sits on a separate ADC and needs independent gain.
    /// </summary>
    public void SetRx1Attenuator(int db)
    {
        _rx1StepAttnDb = (byte)Math.Clamp(db, 0, 31);
        if (_rxTask is not null) SendCmdHighPriority(run: true);
    }

    /// <summary>
    /// Enable/disable ADC dither and the ADC digital-output randomizer in
    /// CmdRx bytes 5/6. Thetis drives these as global G2 options and writes
    /// the same value to ADC0..2, which Zeus mirrors through
    /// <see cref="AdcOptionMask"/>.
    /// </summary>
    public void SetAdcDitherRandom(bool ditherEnabled, bool randomEnabled)
    {
        _adcDitherEnabled = ditherEnabled;
        _adcRandomEnabled = randomEnabled;
        if (_rxTask is not null) SendCmdRx();
    }

    public void SetDriveByte(byte value)
    {
        _driveByte = value;
        if (_rxTask is not null) SendCmdHighPriority(run: true);
    }

    public void SetOcMasks(byte txMask, byte rxMask)
    {
        _ocTxMask = (byte)(txMask & 0x7F);
        _ocRxMask = (byte)(rxMask & 0x7F);
        if (_rxTask is not null) SendCmdHighPriority(run: true);
    }

    /// <summary>
    /// Anvelina-PRO3 DX OC masks (issue #407, EU2AV
    /// Open_Collector_Anvelina_DX spec). 4-bit masks: bit 0 = DX OUT 7,
    /// bit 1 = DX OUT 8, bit 2 = DX OUT 9, bit 3 = DX OUT 10. Narrowed
    /// to 0x0F on entry so spurious upper bits can't drift into the
    /// reserved [7:5] field on the wire. Stored unconditionally; the
    /// wire-encode in <see cref="SendCmdHighPriority"/> is the gate.
    /// </summary>
    public void SetOcDxMasks(byte txMask, byte rxMask)
    {
        _ocDxTxMask = (byte)(txMask & 0x0F);
        _ocDxRxMask = (byte)(rxMask & 0x0F);
        if (_rxTask is not null) SendCmdHighPriority(run: true);
    }

    /// <summary>
    /// Set the per-band TX/RX antenna relays (external-ports plan — antenna
    /// slice, #804). <paramref name="txAntWire"/> / <paramref name="rxAntWire"/>
    /// are 1-based wire selectors (1=ANT1, 2=ANT2, 3=ANT3); out-of-range coerces
    /// to ANT1. <paramref name="hasTxAntennaRelays"/> is the board/variant relay
    /// gate (from <c>BoardCapabilities.HasTxAntennaRelays</c>) — when false the
    /// alex0[26:24] TX-antenna bits stay on ANT1 regardless of selection, so a
    /// non-relay variant never advertises ANT2/3.
    ///
    /// SAFETY: the Alex relay matrix must never be hot-switched while keyed.
    /// When MOX or TUNE is active this defers the new selection into
    /// <see cref="_pendingTxAntenna"/>/<see cref="_pendingRxAntenna"/> and does
    /// NOT re-push HighPriority; the deferred value is flushed and emitted on the
    /// next unkey edge (<see cref="SetMox"/>/<see cref="SetTune"/> off). PS / MOX
    /// / OC / duplex bytes are untouched here.
    /// </summary>
    public void SetAntennas(int txAntWire, int rxAntWire, bool hasTxAntennaRelays)
        => SetAntennas(txAntWire, rxAntWire, hasTxAntennaRelays, rxAuxInput: 0, mkiiBpfRxSelect: false);

    /// <summary>
    /// Overload that also sets the RX auxiliary input
    /// (<paramref name="rxAuxInput"/>: 0=base ANT, 1=EXT1, 2=EXT2, 3=XVTR,
    /// 4=BYPASS) and the MkII-BPF master RX-select gate. The K36/BYPASS pick is
    /// emitted on alex0 bit 11, but PureSignal external feedback OWNS that bit
    /// while PS is armed — the arbitration runs in SendCmdHighPriority AFTER the
    /// operator aux, so an aux=BYPASS choice can never steal the PS coupler (the
    /// PS-K36 firewall). Same mid-key deferral as the base antennas.
    /// </summary>
    public void SetAntennas(int txAntWire, int rxAntWire, bool hasTxAntennaRelays, int rxAuxInput, bool mkiiBpfRxSelect)
    {
        int tx = (txAntWire is 1 or 2 or 3) ? txAntWire : 1;
        int rx = (rxAntWire is 1 or 2 or 3) ? rxAntWire : 1;
        int aux = (rxAuxInput is >= 0 and <= 4) ? rxAuxInput : 0;
        _hasTxAntennaRelays = hasTxAntennaRelays;
        _mkiiBpfRxSelect = mkiiBpfRxSelect;

        if (_moxOn || _tuneActive)
        {
            // Keyed: stash the desired state, leave the live relay bits alone.
            _pendingTxAntenna = tx;
            _pendingRxAntenna = rx;
            _pendingRxAuxInput = aux;
            return;
        }

        bool changed = _txAntenna != tx || _rxAntenna != rx || _rxAuxInput != aux;
        _txAntenna = tx;
        _rxAntenna = rx;
        _rxAuxInput = aux;
        _pendingTxAntenna = -1;
        _pendingRxAntenna = -1;
        _pendingRxAuxInput = -1;
        if (changed && _rxTask is not null) SendCmdHighPriority(run: true);
    }

    /// <summary>
    /// Set the normalized Thetis-style RF filter matrix. Custom ranges and
    /// bypass policies are resolved inside <see cref="ComputeAlexWord"/> so the
    /// wire path remains the single source of Alex bit truth. Mid-key edits are
    /// deferred with the antenna relays and flushed on the next unkey edge.
    /// </summary>
    public void SetRfFilters(RfFilterRuntimeSettings? settings)
    {
        if (_moxOn || _tuneActive)
        {
            _pendingRfFilters = settings;
            _hasPendingRfFilters = true;
            return;
        }

        bool changed = !Equals(_rfFilters, settings);
        _rfFilters = settings;
        _pendingRfFilters = null;
        _hasPendingRfFilters = false;
        if (changed && _rxTask is not null) SendCmdHighPriority(run: true);
    }

    // Apply any antenna selection deferred while keyed. Called on the unkey edge
    // from SetMox/SetTune AFTER the MOX/TUNE flag clears, so the relay/filter
    // re-push happens at idle, never under power. Returns true if a re-push is
    // warranted (the caller's SendCmdHighPriority covers it).
    private bool FlushPendingAntennas()
    {
        if (_pendingTxAntenna < 0
            && _pendingRxAntenna < 0
            && _pendingRxAuxInput < 0
            && !_hasPendingRfFilters)
            return false;
        int tx = _pendingTxAntenna >= 0 ? _pendingTxAntenna : _txAntenna;
        int rx = _pendingRxAntenna >= 0 ? _pendingRxAntenna : _rxAntenna;
        int aux = _pendingRxAuxInput >= 0 ? _pendingRxAuxInput : _rxAuxInput;
        bool changed = _txAntenna != tx || _rxAntenna != rx || _rxAuxInput != aux;
        _txAntenna = tx;
        _rxAntenna = rx;
        _rxAuxInput = aux;
        _pendingTxAntenna = -1;
        _pendingRxAntenna = -1;
        _pendingRxAuxInput = -1;
        if (_hasPendingRfFilters)
        {
            changed |= !Equals(_rfFilters, _pendingRfFilters);
            _rfFilters = _pendingRfFilters;
            _pendingRfFilters = null;
            _hasPendingRfFilters = false;
        }
        return changed;
    }

    public void SetPaEnabled(bool enabled)
    {
        _paEnabled = enabled;
        if (_rxTask is not null) SendCmdGeneral();
    }

    public void SetMox(bool on)
    {
        _moxOn = on;
        if (!on) ResetTxIq();
        // Unkey edge: apply any antenna selection deferred while keyed before the
        // HighPriority re-push, so the relay matrix switches at idle and the very
        // next HP frame carries the operator's new antenna. Only flush when fully
        // unkeyed (a TUNE could still be active).
        if (!on && !_tuneActive) FlushPendingAntennas();
        if (_rxTask is not null) PushTransmitEdge(on);
    }

    public void SetTune(bool on)
    {
        _tuneActive = on;
        if (!on) ResetTxIq();
        if (!on && !_moxOn) FlushPendingAntennas();
        if (_rxTask is not null) PushTransmitEdge(on);
    }

    /// <summary>
    /// Common MOX/TUNE transmit-edge wire push. For every board this re-pushes
    /// HighPriority (run + the MOX/PTT bit derived from <c>_moxOn || _tuneActive</c>).
    ///
    /// On the single-ADC G2E (issue #960), and ONLY while PS is armed AND the
    /// burn-zone time-mux interlock is lifted, DDC0's descriptor depends on the
    /// transmit-burst state, so the edge also re-emits the Receive-Specific
    /// command to swap DDC0 between the feedback interleave and the user RX:
    ///   - key-DOWN: SendCmdRx FIRST (arm the DDC0 feedback descriptor,
    ///     byte 1363 = 0x02) THEN SendCmdHighPriority (assert MOX) — reconfigure
    ///     the DDC before keying.
    ///   - key-UP: SendCmdHighPriority FIRST (drop the wire MOX, preserving the
    ///     #870 kill-RF-before-teardown ordering) THEN SendCmdRx (revert DDC0 to
    ///     the user-RX descriptor) so RX audio returns promptly.
    /// <c>_psBlockFill</c> is reset on both edges so a partial feedback block is
    /// never stitched across a user-RX/feedback transition. The keepalive
    /// re-emits SendCmdRx ~5 Hz, so a single dropped edge self-heals within
    /// ~200 ms. For every other board, and on the G2E with the interlock down or
    /// PS disarmed, this emits NO extra CmdRx — the wire is byte-identical to the
    /// historical single <c>SendCmdHighPriority(run: true)</c>.
    /// </summary>
    private void PushTransmitEdge(bool keyDown)
    {
        bool timeMuxEdge = _psFeedbackEnabled && TimeMuxesPsFeedbackOnDdc0(_boardKind);
        if (!timeMuxEdge)
        {
            SendCmdHighPriority(run: true);
            return;
        }

        if (keyDown)
        {
            _psBlockFill = 0;
            SendCmdRx();                    // arm DDC0 feedback interleave first
            SendCmdHighPriority(run: true); // then assert MOX
        }
        else
        {
            SendCmdHighPriority(run: true); // drop wire MOX first (#870)
            _psBlockFill = 0;
            SendCmdRx();                    // then revert DDC0 to user RX
        }
    }

    /// <summary>
    /// Arm or disarm PureSignal feedback streaming. The exact wire behaviour is
    /// board-specific and routed through the per-board predicates — this summary
    /// describes the two families the code actually composes.
    ///
    /// DUAL-ADC family (OrionMkII / Saturn / G2, <see cref="ReservesPsFeedbackDdcs"/>):
    ///   - <c>SendCmdRx</c> enables the dedicated DDC0+DDC1 feedback pair
    ///     alongside the user-visible RX on DDC2 and sets byte 1363 = 0x02 so the
    ///     radio streams paired DDC0/DDC1 IQ on port 1035.
    ///   - <c>SendCmdHighPriority</c> sets <c>ALEX_PS_BIT</c> and, during xmit,
    ///     mirrors the DDC0/DDC1 phase words to the TX-DUC frequency
    ///     (<see cref="ComposesPsFeedbackWire"/> is true).
    ///
    /// SINGLE-ADC time-mux family (ANAN-G2E / HermesC10 and ANAN-10E / HermesII,
    /// <see cref="TimeMuxesPsFeedbackOnDdc0"/>): the HermesC10 gateware forms the
    /// feedback pair INSIDE DDC0's packer, so the path is materially different
    /// from Orion and this method does NOT compose the Orion layout for it:
    ///   - <c>SendCmdRx</c> arms DDC0 ONLY — no DDC1 enable. The coupler
    ///     (rx_I[0] = temp_ADC) is interleaved with the hardwired internal TX-DAC
    ///     reference (rx_I[NR] = temp_DACD) coupler-first / reference-second by
    ///     the Rx0 FIFO controller under byte 1363 = 0x02 (SyncRx[0][1];
    ///     Hermes.v:717-720, Rx_specific_C&amp;C.v:181). The descriptor is armed
    ///     only during a TX burst (via <c>PushTransmitEdge</c>) and DDC0 reverts
    ///     to the operator's RX at rest.
    ///   - There is explicitly NO <c>ALEX_PS_BIT</c> and NO DDC0/DDC1 phase
    ///     mirror — that scheme is Orion-only and
    ///     <see cref="ComposesPsFeedbackWire"/>(HermesC10) is false.
    ///   - byte 59 (Angelia_atten_Tx0) is seeded to the protective floor while
    ///     armed (<see cref="SeedsTxAdcProtection"/>) so the DAC reference + PA
    ///     coupler cannot slam the single shared RX ADC at 0 dB on key-down.
    ///
    /// The packet decoder consumes the same coupler-first/reference-second paired
    /// format (6B + 6B per sample) for both families. When off the radio reverts
    /// to the standard non-PS RX layout, byte 59 is restored to the operator's
    /// prior value, and any in-flight paired packets are discarded.
    /// </summary>
    public void SetPsFeedbackEnabled(bool on)
    {
        if (_psFeedbackEnabled == on) return;
        _psFeedbackEnabled = on;
        // Reset accumulator so we don't mix old samples with new on a re-arm.
        _psBlockFill = 0;

        // Byte-59 TX-time ADC-overload protection seed (PureSignal hard rule).
        // On a single-ADC time-mux board (ANAN-10E / HermesII, gated dark by
        // Hermes10ePsTimeMuxOnAir) the TX-DAC reference + PA coupler land on the
        // one RX ADC during a key-down; byte 59 (Angelia_atten_Tx0) is the
        // gateware's TX-time attenuator for that ADC (Hermes.v:1483
        // `atten0 = FPGA_PTT ? atten0_on_Tx : Attenuator0`,
        // Tx_specific_C&C.v:182-183). Because ComposesPsFeedbackWire(HermesII) is
        // deliberately false, ComposeCmdTxBuffer takes the non-PS branch and
        // emits p[57]=p[58]=p[59]=_txStepAttnDb — so seeding _txStepAttnDb to the
        // protective floor while armed pushes byte 59 protective, and restoring
        // the operator's prior value on disarm returns the wire to normal. The
        // seed/restore is the ONLY non-byte-identical effect, and it is reached
        // only when SeedsTxAdcProtection is true — now the 10E (HermesII) ONLY.
        // The G2E (HermesC10) is deliberately excluded (#960, KB2UKA 2026-07-01):
        // its single ADC is protected by the operator's EXTERNAL sampler pad, not
        // byte 59, so forcing 31 dB over-attenuated the padded tap and deadlocked
        // PS (dead meter on External). Leaving byte 59 at the operator's value
        // lets the external pad set the level (Thetis-parity). Every other board,
        // and the 10E with its kill-switch forced false, takes the historical path
        // untouched (the dual-ADC G2/OrionMkII never qualifies).
        bool seededTxAttn = false;
        if (SeedsTxAdcProtection(_boardKind))
        {
            if (on)
            {
                _txAttnBeforePsSeed = _txStepAttnDb;
                if (_txStepAttnDb != PsTxAdcProtectFloorDb)
                {
                    _txStepAttnDb = PsTxAdcProtectFloorDb;
                    seededTxAttn = true;
                }
            }
            else if (_txAttnBeforePsSeed is byte restore)
            {
                if (_txStepAttnDb != restore)
                {
                    _txStepAttnDb = restore;
                    seededTxAttn = true;
                }
                _txAttnBeforePsSeed = null;
            }
        }

        if (_rxTask is not null)
        {
            // Re-emit RX-spec (DDC enables / sync bit) and HighPriority (PS
            // bit / DDC0/1 phase) so the radio honours the new state on the
            // very next sample window.
            SendCmdRx();
            // Push the seeded/restored byte 59 — only when the seed actually
            // moved the value, so non-10E (and flag-false) arms emit no extra
            // CmdTx and stay byte-identical to today.
            if (seededTxAttn) SendCmdTx();
            SendCmdHighPriority(run: true);
        }
    }

    /// <summary>
    /// Choose between Internal feedback coupler and External (Bypass)
    /// feedback antenna. Drives <c>ALEX_RX_ANTENNA_BYPASS</c> in alex0
    /// during xmit + PS armed (pihpsdr new_protocol.c:1284-1296). No
    /// effect on RxSpecific / TxSpecific buffers.
    /// </summary>
    public void SetPsFeedbackSource(bool external)
    {
        if (_psFeedbackExternal == external) return;
        _psFeedbackExternal = external;
        if (_rxTask is not null) SendCmdHighPriority(run: true);
    }

    public ChannelReader<PsFeedbackFrame> PsFeedbackFrames => _psFeedbackFrames.Reader;

    /// <summary>
    /// Push a block of interleaved float IQ (−1..+1) into the TX-DUC stream.
    /// The block is buffered; when the accumulator reaches 240 complex
    /// samples a 1444-byte P2 packet is sent to port 1029 (pihpsdr
    /// new_protocol.c:1909-1942 — new_protocol_txiq_thread). Caller owns the
    /// input buffer; we copy. Samples are scaled by pihpsdr's ggain=0.896
    /// (transmitter.c:1761) to compensate for the end-of-chain FIR gain and
    /// then quantized to signed 24-bit BE.
    /// </summary>
    public void SendTxIq(ReadOnlySpan<float> iqInterleaved)
    {
        if (_sock is null || _rxTask is null) return;
        if ((iqInterleaved.Length & 1) != 0)
            throw new ArgumentException("interleaved length must be even (I,Q pairs)", nameof(iqInterleaved));

        Interlocked.Add(ref _txIqInputComplexSamples, iqInterleaved.Length / 2);
        lock (_txIqGate)
        {
            int idx = 0;
            while (idx < iqInterleaved.Length)
            {
                int capacity = _txIqScratch.Length - _txIqScratchCount;
                int copyLen = Math.Min(capacity, iqInterleaved.Length - idx);
                iqInterleaved.Slice(idx, copyLen).CopyTo(_txIqScratch.AsSpan(_txIqScratchCount));
                _txIqScratchCount += copyLen;
                idx += copyLen;
                if (_txIqScratchCount >= _txIqScratch.Length)
                {
                    FlushTxIqLocked();
                }
            }
        }
    }

    private void ResetTxIq()
    {
        lock (_txIqGate) _txIqScratchCount = 0;
        // Drain any queued-but-unsent packets so a fresh key-down starts
        // from an empty FIFO model and the radio isn't playing 10 ms of
        // the previous transmission's IQ when PTT re-engages.
        long drained = 0;
        while (_txIqQueue.Reader.TryRead(out _))
        {
            drained++;
            DecrementTxIqQueuedPacketsIfPositive();
        }
        if (drained > 0)
        {
            Interlocked.Add(ref _txIqResetDrainedPackets, drained);
        }
    }

    private void FlushTxIqLocked()
    {
        // The 0.896 trim pihpsdr applies in transmitter.c:1707-1712 is
        // CW-path-only — it compensates for the CW shaped pulse skipping
        // WDSP TXA's end-of-chain FIR. The regular TXA path (which is what
        // Zeus feeds — mic and TUN both go through TXA) takes the samples
        // unscaled (transmitter.c:1739-1754), so no pre-quantize trim here.
        var p = new byte[BufLen];
        WriteBeU32(p, 0, _seqCmdTxIq++);
        for (int i = 0; i < TxIqSamplesPerPacket; i++)
        {
            float fi = _txIqScratch[i * 2];
            float fq = _txIqScratch[i * 2 + 1];
            int vi = Int24Clamp(fi);
            int vq = Int24Clamp(fq);
            int off = 4 + i * 6;
            p[off + 0] = (byte)((vi >> 16) & 0xff);
            p[off + 1] = (byte)((vi >> 8) & 0xff);
            p[off + 2] = (byte)(vi & 0xff);
            p[off + 3] = (byte)((vq >> 16) & 0xff);
            p[off + 4] = (byte)((vq >> 8) & 0xff);
            p[off + 5] = (byte)(vq & 0xff);
        }
        // Enqueue instead of sending inline. The sender task drains this at
        // the DAC rate so the radio's TX FIFO stays level — sending the full
        // 8-packet burst from one WDSP cycle straight to the wire overfills
        // then starves the FIFO, showing up as a pulsed carrier.
        DropStaleTxIqPacketsIfBackedUpLocked();
        if (_txIqQueue.Writer.TryWrite(p))
        {
            Interlocked.Increment(ref _txIqPacketsQueued);
            Interlocked.Increment(ref _txIqQueuedPackets);
        }
        else
        {
            Interlocked.Increment(ref _txIqQueueWriteFailures);
        }
        _txIqScratchCount = 0;
    }

    private void DropStaleTxIqPacketsIfBackedUpLocked()
    {
        while (Volatile.Read(ref _txIqQueuedPackets) >= TxIqMaxQueuedPackets
               && _txIqQueue.Reader.TryRead(out _))
        {
            DecrementTxIqQueuedPacketsIfPositive();
        }
    }

    private void DecrementTxIqQueuedPacketsIfPositive()
    {
        while (true)
        {
            long current = Volatile.Read(ref _txIqQueuedPackets);
            if (current <= 0) return;
            if (Interlocked.CompareExchange(ref _txIqQueuedPackets, current - 1, current) == current) return;
        }
    }

    private void TxIqSenderLoop(CancellationToken ct)
    {
        // Promote this dedicated sender to the platform pro-audio class so the
        // Photino WebView render (desktop mode) can't preempt the radio TX-FIFO
        // feed. Synchronous loop (no await) so it never bounces to a ThreadPool
        // worker and loses the promotion mid-transmit. #559 desktop fix.
        RealtimeThreadPriority.PromoteCallingThreadToProAudio(_log);
        // Port of pihpsdr's new_protocol_txiq_thread (new_protocol.c:1909-1997).
        // Maintains a software model of the radio's TX FIFO fill level: each
        // packet adds 240 samples, wall-clock elapses drain at 192 kHz. When
        // the modeled level exceeds the target (1250 samples ≈ 6.5 ms) we hold
        // for 1 ms before sending the next packet. Below the target we send
        // as fast as the queue delivers — that's the startup ramp that fills
        // the radio's FIFO to its steady-state depth.
        var reader = _txIqQueue.Reader;
        var ep = new IPEndPoint(_radioEndpoint!.Address, 1029);
        double fifoSamples = 0.0;
        long lastTicks = Stopwatch.GetTimestamp();
        double ticksPerSecond = Stopwatch.Frequency;
        // 1 Hz TX-IQ rate log (mirrors P1's p1.tx.rate). The radio's DUC needs
        // 192 kHz = 800 packets/s of 240 samples. On Windows a coarse timer
        // floors Task.Delay-paced sends near ~380/s (the relay-hang symptom);
        // this line is how we confirm the timeBeginPeriod(1) fix restores 800/s.
        int rateCount = 0;
        long lastRateTicks = lastTicks;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // Block this dedicated thread until a packet is queued (or
                    // the channel completes / we're cancelled). No await → the
                    // thread keeps its pro-audio promotion across the wait.
                    if (!reader.WaitToReadAsync(ct).AsTask().GetAwaiter().GetResult()) break;
                }
                catch (OperationCanceledException) { break; }
                catch (ChannelClosedException) { break; }
                if (!reader.TryRead(out var packet)) continue;
                DecrementTxIqQueuedPacketsIfPositive();
                if (packet is null)
                {
                    Interlocked.Increment(ref _txIqSendFailures);
                    _log.LogWarning("p2.txiq dropped null packet");
                    continue;
                }

                // Drain by wall-clock since the previous send.
                long now = Stopwatch.GetTimestamp();
                double elapsedSec = (now - lastTicks) / ticksPerSecond;
                fifoSamples -= elapsedSec * TxDacSampleRate;
                if (fifoSamples < 0.0) fifoSamples = 0.0;
                lastTicks = now;

                // If the radio's FIFO would overflow, wait a tick before the
                // next send. The 1 ms delay is coarse but well within the
                // FIFO's 6.5 ms target headroom, so no underrun risk.
                if (fifoSamples > TxFifoTargetSamples)
                {
                    Thread.Sleep(1);
                    if (ct.IsCancellationRequested) break;
                    now = Stopwatch.GetTimestamp();
                    elapsedSec = (now - lastTicks) / ticksPerSecond;
                    fifoSamples -= elapsedSec * TxDacSampleRate;
                    if (fifoSamples < 0.0) fifoSamples = 0.0;
                    lastTicks = now;
                }

                fifoSamples += TxIqSamplesPerPacket;
                try
                {
                    _sock!.SendTo(packet, ep);
                    rateCount++;
                    Interlocked.Increment(ref _txIqPacketsSent);
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException ex)
                {
                    Interlocked.Increment(ref _txIqSendFailures);
                    _log.LogWarning(ex, "p2.txiq send failed");
                }

                if (now - lastRateTicks >= ticksPerSecond)
                {
                    // Diagnostics must NEVER take down the TX-IQ sender — the
                    // outer catch exits the loop on any exception, so a bad log
                    // call here silently stops all TX. (It did: ChannelReader.Count
                    // throws NotSupportedException on an unbounded channel, which
                    // killed the sender 24ms into key-down — no TX output at all.)
                    try
                    {
                        Volatile.Write(ref _txIqLastPacketsPerSecond, rateCount);
                        Interlocked.Exchange(ref _txIqLastFifoModelSamples, (long)Math.Round(fifoSamples));
                        Interlocked.Exchange(ref _txIqLastRateUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
                        _log.LogInformation("p2.tx.rate pkts/s={Pps} fifoModel={Fifo:F0}",
                            rateCount, fifoSamples);
                    }
                    catch { /* never let a diagnostic kill TX */ }
                    rateCount = 0;
                    lastRateTicks = now;
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "p2.txiq sender exited with error");
        }
    }

    internal static int Int24Clamp(float v)
    {
        if (!float.IsFinite(v)) return 0;
        if (v >  1.0f) v =  1.0f;
        if (v < -1.0f) v = -1.0f;
        // 8_388_607 = 2^23 - 1. Using the ceiling (8_388_608) would map +1.0
        // to a value that, when sign-extended on the far side, wraps to
        // −8_388_608 — a full-scale negative spike on the loudest sample.
        return (int)MathF.Round(v * 8_388_607.0f);
    }

    private void SendCmdGeneral()
    {
        var p = new byte[60];
        WriteBeU32(p, 0, _seqCmdGeneral++);
        p[4] = 0x00;
        WriteBeU16(p, 5, 1025);
        WriteBeU16(p, 7, 1026);
        WriteBeU16(p, 9, 1027);
        WriteBeU16(p, 11, 1025);
        WriteBeU16(p, 13, 1028);
        WriteBeU16(p, 15, 1029);
        WriteBeU16(p, 17, 1035);
        WriteBeU16(p, 19, 1026);
        WriteBeU16(p, 21, 1027);
        p[23] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(24), 512);
        p[26] = 16;
        p[27] = 0;
        p[28] = 32;
        // Matches pihpsdr new_protocol_general for ORION2/SATURN hardware.
        //
        // [37] = 0x08: pihpsdr writes this on ORION2/SATURN. The upstream
        // HDL `General_CC.v:136-140` (Hermes Protocol-2 v10.7 and
        // Orion_MkII v2.2.10) only decodes `cmd_data[0]` at byte 37
        // (Time_stamp/VITA_49/VNA — all driven from the same bit). Bit 3
        // is NOT read by `General_CC.v`, and the radio is already in
        // phase-word mode by default — `Hermes.v` / `Orion.v` wire the
        // host-supplied 32-bit phase word straight into the receiver
        // mixers (see `C122_phase_word` in Hermes.v line 1135 and 1284).
        // Kept at 0x08 for parity with pihpsdr; no observable effect on
        // this gateware revision. Issue #416.
        //
        // [38] = 0x01: hardware-timer enable (`HW_timer_enable`, decoded
        // at `General_CC.v:141`).
        p[37] = 0x08;
        p[38] = 0x01;
        // [58] bit 0 = PA enable (piHPSDR `new_protocol.c:658-677`; Thetis
        // `network.c` SendGeneral). The old hard-coded 0x01 became the
        // default; PaSettingsStore now owns the bit.
        p[58] = (byte)(_paEnabled ? 0x01 : 0x00);
        p[59] = 0x03;
        _sock!.SendTo(p, new IPEndPoint(_radioEndpoint!.Address, 1024));
    }

    // macOS IP_BOUND_IF socket-option name. Defined in `netinet/in.h` as 25;
    // not exposed as a named constant in .NET so we declare it here.
    // Behavior: forces traffic on this socket to egress a specific interface
    // by ifindex, bypassing the routing table's interface selection.
    private const int IP_BOUND_IF = 25;

    /// <summary>
    /// Best-effort macOS ARP-priming workaround. Sends a single zero-byte
    /// UDP datagram to the radio's port 1024 bound (via IP_BOUND_IF) to the
    /// interface the kernel chose for the radio's IP. The kernel resolves
    /// ARP as a side effect so subsequent real packets ship promptly instead
    /// of racing the first SendTo. Mirrors deskhpsdr's <c>p2_prime_route()</c>
    /// at <c>src/new_protocol.c:453-474</c>.
    ///
    /// Caller is the only authority on platform gating — this helper assumes
    /// it's being invoked on macOS already. Every failure path is swallowed
    /// and logged; the priming is opportunistic, never load-bearing for the
    /// connect succeeding.
    /// </summary>
    internal static void PrimeMacOSUdpRoute(IPAddress radioIp, ILogger log)
    {
        try
        {
            // Step 1: ask the kernel which local IP it would use to reach
            // the radio. UDP Connect doesn't generate any traffic — it only
            // installs a "default destination" in the socket and resolves
            // the route — so LocalEndPoint after Connect is the local IP
            // the kernel routes through.
            int ifIndex;
            using (var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                probe.Connect(new IPEndPoint(radioIp, 1024));
                var localIp = (probe.LocalEndPoint as IPEndPoint)?.Address;
                if (localIp is null)
                {
                    log.LogDebug("p2.prime.macos no localIp for radio={Radio}", radioIp);
                    return;
                }
                ifIndex = FindInterfaceIndexForLocalAddress(localIp);
                if (ifIndex <= 0)
                {
                    log.LogDebug("p2.prime.macos no interface for local={Local} radio={Radio}", localIp, radioIp);
                    return;
                }
            }

            // Step 2: open a fresh UDP socket, bind it to that interface
            // index, and send a single byte to port 1024 on the radio. The
            // datagram is intentionally garbage — port 1024 (CmdGeneral
            // from-host) is one the radio's protocol layer ignores on a
            // payload it can't parse, but the link-layer ARP exchange that
            // *gets* it there is the whole point.
            using var prime = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            var ifIndexBytes = BitConverter.GetBytes(ifIndex);
            prime.SetRawSocketOption(
                optionLevel: (int)SocketOptionLevel.IP,
                optionName: IP_BOUND_IF,
                optionValue: ifIndexBytes);
            prime.SendTo(new byte[] { 0 }, new IPEndPoint(radioIp, 1024));
            log.LogInformation("p2.prime.macos sent radio={Radio} ifIndex={Ix}", radioIp, ifIndex);
        }
        catch (Exception ex)
        {
            // ARP priming is opportunistic; the regular SendCmdGeneral that
            // follows will resolve ARP eventually. Don't fail the connect
            // over a priming hiccup.
            log.LogDebug(ex, "p2.prime.macos failed radio={Radio}", radioIp);
        }
    }

    /// <summary>
    /// Look up the OS interface index for the NIC that owns
    /// <paramref name="localAddr"/>. Returns 0 if no match — the priming
    /// caller treats that as "skip priming and continue."
    /// </summary>
    internal static int FindInterfaceIndexForLocalAddress(IPAddress localAddr)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            var props = nic.GetIPProperties();
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.Equals(localAddr))
                {
                    var ipv4 = props.GetIPv4Properties();
                    return ipv4?.Index ?? 0;
                }
            }
        }
        return 0;
    }

    // Windows surfaces ICMP port-unreachable as SocketError 10054 on the next recv.
    // Disabling it at the ioctl level keeps the socket clean; the ConnectionReset
    // catch in RxLoop is a belt-and-suspenders fallback if the ioctl is unavailable.
    private const int SIO_UDP_CONNRESET = -1744830452; // 0x9800000C

    internal static void DisableUdpConnReset(Socket s)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { s.IOControl(SIO_UDP_CONNRESET, new byte[4], null); }
        catch (SocketException) { /* best effort — some Windows editions block the ioctl */ }
    }

    // Returns the local IPv4 address of the first NIC whose subnet contains radioIp.
    // Returns null when no subnet match is found (single-NIC host, radio behind a
    // router, etc.) — the caller falls back to IPAddress.Any in that case.
    internal static IPAddress? FindLocalAddressForSubnet(IPAddress radioIp)
    {
        if (radioIp.AddressFamily != AddressFamily.InterNetwork) return null;
        var radioBytes = radioIp.GetAddressBytes();
        foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (iface.OperationalStatus != OperationalStatus.Up) continue;
            if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in iface.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var mask = ua.IPv4Mask;
                if (mask is null || mask.Equals(IPAddress.Any)) continue;
                var local = ua.Address.GetAddressBytes();
                var m = mask.GetAddressBytes();
                bool same = true;
                for (int i = 0; i < 4; i++)
                    if ((local[i] & m[i]) != (radioBytes[i] & m[i])) { same = false; break; }
                if (same) return ua.Address;
            }
        }
        return null;
    }

    /// <summary>
    /// Per-board base DDC index for the user-visible RX. OrionMkII/Saturn/G2
    /// family reserves DDC0/DDC1 for PureSignal feedback so the operator's RX
    /// lives at DDC2. Single-ADC Hermes-class radios (Brick2 on wire byte
    /// 0x01; ANAN-10E / ANAN-100B on wire byte 0x02; ANAN-G2E / HermesC10 on
    /// wire byte 0x14) have no reserved PS feedback slots — the RX is at
    /// DDC0. Default OrionMkII preserves Zeus' historical P2 wire shape for
    /// every existing board.
    /// Reference: deskhpsdr <c>src/new_protocol.c:1692-1698</c> — only
    /// <c>NEW_DEVICE_ANGELIA / ORION / ORION2 / SATURN</c> use the
    /// <c>ddc = 2 + i</c> offset; <c>NEW_DEVICE_HERMES</c> and
    /// <c>NEW_DEVICE_HERMES2</c> both fall through to <c>ddc = i</c>.
    /// Thetis groups HermesC10 alongside Hermes/HermesII in its P2 channel
    /// setup (Console/console.cs:8610-8612) — the N1GP G2E firmware emulates
    /// a single-ADC Hermes-class device on the wire.
    /// </summary>
    public static int RxBaseDdc(HpsdrBoardKind board) => board switch
    {
        HpsdrBoardKind.Hermes    => HermesRxDdc,
        HpsdrBoardKind.HermesII  => HermesRxDdc,
        HpsdrBoardKind.HermesC10 => HermesRxDdc,
        _                        => G2RxDdc,
    };

    /// <summary>
    /// True iff <b>Zeus</b> reserves the front DDC slots (DDC0/DDC1) for the
    /// PureSignal feedback pair on this board — i.e. the dual-ADC
    /// OrionMkII/Saturn/G2 family, where the user-visible RX lives at DDC2
    /// (<see cref="RxBaseDdc"/> == 2). False for the single-ADC Hermes-class
    /// radios (Hermes/0x01, HermesII/0x02, ANAN-G2E·HermesC10/0x14) where Zeus
    /// maps the operator's only RX to DDC0 (<see cref="RxBaseDdc"/> == 0).
    ///
    /// This is the single board predicate that decides whether Zeus may put PS
    /// feedback on DDC0 — both when composing the wire (compose side) and when
    /// demuxing inbound IQ (receive side). Deriving it from
    /// <see cref="RxBaseDdc"/> keeps the two sides from ever drifting: if a
    /// board's RX base is DDC0, Zeus must not also treat that DDC as a feedback
    /// slot. Issue #960 — on the single-ADC G2E the board-agnostic
    /// "DDC0 == PS feedback" assumption diverted the operator's only RX stream
    /// into the PS path the moment PS armed, killing RX audio.
    ///
    /// IMPORTANT — what this predicate does and does NOT mean: it does NOT mean
    /// the G2E hardware is incapable of PureSignal. The N1GP G2E gateware
    /// genuinely supports Orion-style PS on its single LTC2208 (Hermes.v: NR=2,
    /// no DDC2): with command byte 1363 (SyncRx[0]) set to 0x02 — the exact
    /// byte Zeus already writes to arm the Orion feedback pair — Rx_fifo_ctrl0
    /// (Hermes.v:717-720) muxes the TX-DAC reference (rx_I[NR]) and the ADC
    /// feedback (rx_I[0]) into DDC0 as the identical Orion interleaved pair, and
    /// the firmware comments record "PS works" (Hermes.v:139, ported from
    /// Angelia P2 v11.6, N1GP/Phil PS work at :174). The reason Zeus returns
    /// <c>false</c> here for the G2E is a SOFTWARE choice, not a hardware limit:
    /// because the G2E has only one ADC, arming that interleaved pair consumes
    /// DDC0 and the operator's user RX would have to relocate to DDC1
    /// (receiver_inst1, also fed from temp_ADC). Zeus does not yet implement
    /// that single-ADC relocation, so this predicate DISABLES PS on the G2E to
    /// protect RX audio — it does not make PS work. Making PS actually function
    /// on the G2E (keep writing byte 1363 = 0x02 AND move user RX to DDC1 while
    /// PS is armed) is a larger, G2E-bench-gated change tracked in #960.
    /// </summary>
    public static bool ReservesPsFeedbackDdcs(HpsdrBoardKind board)
        => RxBaseDdc(board) != 0;

    /// <summary>
    /// RX-thread dispatch decision (issue #960): should an inbound DDC0 packet
    /// (UDP src port 1035) be consumed as a PureSignal paired-feedback frame
    /// instead of a user-RX IQ frame? True only when PS feedback is armed AND
    /// the packet is on DDC0 AND the board actually reserves DDC0 for the PS
    /// feedback pair (<see cref="ReservesPsFeedbackDdcs"/>).
    ///
    /// On the dual-ADC OrionMkII/Saturn/G2 family DDC0 is a genuine reserved
    /// feedback slot (user RX lives at DDC2), so the board term is a constant
    /// <c>true</c> and this is bit-for-bit the historical
    /// <c>_psFeedbackEnabled &amp;&amp; ddcIndex == 0</c> behaviour. On the
    /// single-ADC Hermes-class boards (HermesC10/ANAN-G2E, Hermes, HermesII)
    /// Zeus maps DDC0 to the operator's only RX, so this returns <c>false</c>
    /// and the packet stays on the user-RX audio path — without the gate,
    /// arming PS diverted every RX0 packet into the feedback path and the WDSP
    /// RX0 audio channel starved (1.4M-sample underrun, dead receive).
    ///
    /// Note (#960): returning <c>false</c> on the G2E DISABLES PureSignal there
    /// to keep RX audio alive — it does NOT implement PS. The G2E gateware can
    /// do Orion-style PS on DDC0 (byte 1363 SyncRx[0]=0x02 interleave); making
    /// it work requires also relocating user RX to DDC1, a separate change.
    /// See <see cref="ReservesPsFeedbackDdcs"/>.
    /// </summary>
    /// <summary>
    /// Burn-zone interlock (PureSignal hard rule, issue #960). The G2E
    /// single-ADC time-multiplexed PS feedback path (compose + de-interleave,
    /// below) is fully wired but held DARK in production until BOTH coupled
    /// pre-conditions land with KB2UKA sign-off:
    ///   (A) the byte-59 (Angelia_atten_Tx0) protective seed is pushed to the
    ///       wire on connect/arm in
    ///       <c>RadioService.ApplyPsHwPeakForConnection</c>. The single shared
    ///       LTC2208's TX-time input attenuator is muxed by transmit state in
    ///       gateware — Hermes.v:1710
    ///       <c>assign atten0 = FPGA_PTT ? atten0_on_Tx : Attenuator0</c>, where
    ///       <c>atten0_on_Tx = Angelia_atten_Tx0 =</c> TxSpecific byte 59
    ///       (Tx_specific_C&amp;C.v:182-185). Zeus emits byte 59 on every
    ///       TxSpecific packet but only ever as <c>_txStepAttnDb</c>, which
    ///       defaults to 0 — it never seeds a PS-protective value there, so a
    ///       fresh G2E first key-down with PS armed would slam the PA coupler
    ///       into the one ADC at 0 dB. Seeding it touches a PS
    ///       calibration default — a PS-hard-rule change, NOT done autonomously.
    ///   (B) OK1BR bench-verifies first-key-down ADC-overload on a real G2E
    ///       (#289) — Zeus has no G2E bench.
    /// With this <c>false</c> the G2E path is byte-identical to the
    /// PS-disabled-on-single-ADC behaviour shipped in commit a7fc99b: RX audio
    /// survives, <c>ps.correcting</c> stays false. Flipping it true autonomously
    /// is forbidden — see CLAUDE.md "Hard Rules — PureSignal".
    /// </summary>
    /// <remarks>
    /// v0.10.8 (#960): ENABLED by default with KB2UKA sign-off, so single-ADC
    /// PureSignal engages on the normal PS arm for the ANAN-G2E — no separate
    /// operator switch, identical UX to any other PS board. The byte-59
    /// ADC-overload protection (<see cref="SeedsTxAdcProtection"/>) now covers
    /// HermesC10 too, so a first key-down with PS armed seeds the protective
    /// floor instead of slamming the coupler into the one ADC at 0 dB. Retained
    /// as an internal kill-switch (set false to disable) — there is NO
    /// operator-facing toggle. Experimental: validated in the virtual-radio
    /// loopback; real-G2E field validation in progress (#289).
    /// </remarks>
    internal static bool G2ePsTimeMuxOnAir = true;

    /// <summary>
    /// Burn-zone interlock (PureSignal hard rule) for the ANAN-10E
    /// (<see cref="HpsdrBoardKind.HermesII"/>) single-ADC time-multiplexed PS
    /// feedback path — the sibling of <see cref="G2ePsTimeMuxOnAir"/>, scoped to
    /// the 10E ONLY. It is held DARK (false) in production: while false the
    /// HermesII wire/behaviour is byte-identical to the PS-disabled-on-single-ADC
    /// path shipped today (RX audio survives, no feedback descriptor, no byte-59
    /// seed). Flipped true ONLY by tests / the gated loopback emulator.
    ///
    /// Firmware ground truth (the 10E is byte-for-byte the same wire as the G2E,
    /// differing only in the register name and the reference source):
    ///   - <c>anan-10e_100b/P2-gateware/Hermes_v10.3/Rx_specific_C&amp;C.v:181</c>
    ///     — command byte 1363 is the <c>Mux</c> register on the 10E
    ///     (<c>1363: Mux &lt;= udp_rx_data;</c>); the old SyncRx read is commented
    ///     out at :175-178. <c>Mux[1]</c> = the Rx1 PS select.
    ///   - <c>Hermes.v:1080-1093</c> — <c>receiver2 receiver_inst1</c> (Rx1→DDC1)
    ///     takes <c>in_data(Mux[1] ? temp_DACD : temp_ADC)</c>, slaving Rx1's
    ///     input to the TX-DAC reference when <c>Mux[1]</c> is set; rate/freq
    ///     slaved to Rx0.
    ///   - <c>Hermes.v:684-687</c> — Rx0's single FIFO controller interleaves
    ///     Sync-first (<c>rx_I[0]</c> = ADC coupler) then main
    ///     (<c>rx_I[1]</c> = DAC reference) under <c>.Sync(Mux)</c> — the exact
    ///     coupler-first / reference-second order the existing paired decoder
    ///     (<see cref="DecodePsPairForTest"/>) consumes. Host enables DDC0 ONLY.
    ///   - <c>Hermes.v:1483</c> <c>atten0 = FPGA_PTT ? atten0_on_Tx : Attenuator0</c>
    ///     with <c>Tx_specific_C&amp;C.v:182-183</c> mapping byte 59 to
    ///     <c>Angelia_atten_Tx0</c> — the TX-time ADC attenuator that protects the
    ///     one RX ADC from the DAC feedback during a key-down (the byte-59 seed).
    ///
    /// Flipping this true autonomously is forbidden — see CLAUDE.md
    /// "Hard Rules — PureSignal". The production flag flip and the final byte-59
    /// seed value (<see cref="PsTxAdcProtectFloorDb"/>) require KB2UKA sign-off
    /// plus 10E bench verification of first-key-down ADC overload.
    /// </summary>
    /// <remarks>
    /// v0.10.8 (#1209): ENABLED by default with KB2UKA sign-off, so single-ADC
    /// PureSignal engages on the normal PS arm for the ANAN-10E — no separate
    /// operator switch. The byte-59 ADC-overload protection
    /// (<see cref="SeedsTxAdcProtection"/>) seeds the protective floor on key-down.
    /// Retained as an internal kill-switch (set false to disable); NO
    /// operator-facing toggle. Experimental: validated in the virtual-radio
    /// loopback; real-10E field validation in progress.
    /// </remarks>
    internal static bool Hermes10ePsTimeMuxOnAir = true;

    /// <summary>
    /// TX-time ADC-overload protection floor for the single-ADC time-mux PS
    /// path (the byte-59 / <c>Angelia_atten_Tx0</c> seed). On a single-ADC board
    /// the TX-DAC reference and PA coupler land on the one RX ADC during a TX
    /// burst; byte 59 attenuates that ADC's TX-time input (gateware muxes it in
    /// on <c>FPGA_PTT</c> — <c>Hermes.v:1483</c>, <c>Tx_specific_C&amp;C.v:182-183</c>).
    /// 31 dB = the maximum (safest) attenuation, seeded so a fresh first key-down
    /// with PS armed cannot slam the coupler into the ADC at 0 dB. The final
    /// production value is bench-cal pending and KB2UKA-gated.
    /// </summary>
    internal const byte PsTxAdcProtectFloorDb = 31;

    /// <summary>
    /// True iff Zeus must seed the byte-59 (<c>Angelia_atten_Tx0</c>) TX-time ADC
    /// attenuator to its protective floor (<see cref="PsTxAdcProtectFloorDb"/>)
    /// while PS feedback is armed on this board — the single-ADC time-mux case
    /// where the TX-DAC reference + PA coupler hit the one RX ADC on a TX burst.
    ///
    /// Scoped to the ANAN-10E (<see cref="HpsdrBoardKind.HermesII"/>) under
    /// <see cref="Hermes10ePsTimeMuxOnAir"/> ONLY.
    ///
    /// The ANAN-G2E (<see cref="HpsdrBoardKind.HermesC10"/>) is DELIBERATELY
    /// EXCLUDED (#960, KB2UKA sign-off 2026-07-01). The G2E has NO internal
    /// feedback coupler — its single ADC only ever sees the operator's EXTERNAL
    /// sampler tap through the RX-bypass relay (a properly-sized post-PA pad, e.g.
    /// −55…−65 dB rated for 1500 W). That external pad IS the ADC-overload
    /// protection, so forcing byte 59 to 31 dB was pure over-attenuation stacked
    /// on the pad — it drove the coupler below <c>calcc</c>'s convergence floor
    /// and, because <c>PsAutoAttenuateService</c> only walks byte 59 down once
    /// feedback reads &gt; 0, it deadlocked there: the observed "PS meter never
    /// moves on the G2E even on External." Leaving byte 59 at the operator's value
    /// lets the external pad set the level, matching Thetis (which does not force a
    /// TX-time attenuation for an external feedback tap). Every other board — and
    /// the 10E with its kill-switch forced false — returns false and is
    /// byte-identical to the historical path.
    /// </summary>
    internal static bool SeedsTxAdcProtection(HpsdrBoardKind board)
        => board == HpsdrBoardKind.HermesII && Hermes10ePsTimeMuxOnAir;

    /// <summary>
    /// True iff this board does Orion-style PureSignal on a SINGLE ADC by
    /// time-multiplexing DDC0 (issue #960) — the N1GP ANAN-G2E / HermesC10
    /// gateware. With command byte 1363 (SyncRx[0]) bit 1 (0x02) the Rx0 FIFO
    /// controller interleaves the ADC coupler (rx_I[0] = temp_ADC, emitted
    /// FIRST) with the hardwired internal TX-DAC reference (rx_I[NR] =
    /// temp_DACD, emitted SECOND) onto DDC0 — Hermes.v:717-720 / :1229 / :1241,
    /// Rx_fifo_ctrl.v (Sync data first, then data), Rx_specific_C&amp;C.v:181
    /// maps byte 1363 -> SyncRx[0]. That coupler-first/reference-second order
    /// matches Zeus's existing paired decoder (<see cref="DecodePsPairForTest"/>:
    /// first slot -> rx, second slot -> tx), so the de-interleave is reused
    /// unchanged. Unlike the dual-ADC family (<see cref="ReservesPsFeedbackDdcs"/>)
    /// DDC0 is also the operator's only RX, so the feedback descriptor may be
    /// armed ONLY during a TX burst and DDC0 reverts to user RX at rest. Gated
    /// per board by its own burn-zone interlock so the on-air path stays dark
    /// until that board's byte-59 safety seed lands and it is bench-verified:
    ///   - <see cref="HpsdrBoardKind.HermesC10"/> (ANAN-G2E) →
    ///     <see cref="G2ePsTimeMuxOnAir"/>.
    ///   - <see cref="HpsdrBoardKind.HermesII"/> (ANAN-10E) →
    ///     <see cref="Hermes10ePsTimeMuxOnAir"/>. The 10E presents the identical
    ///     coupler-first/reference-second interleave on DDC0 via command byte
    ///     1363 — the <c>Mux</c> register on the 10E, where <c>Mux[1]</c> swings
    ///     Rx1's input to <c>temp_DACD</c> (the TX-DAC reference) — so the same
    ///     compose/decode machinery applies (anan-10e_100b/P2-gateware/
    ///     Hermes_v10.3/Hermes.v:684-687, :1080-1093, Rx_specific_C&amp;C.v:181).
    /// Plain <see cref="HpsdrBoardKind.Hermes"/> is deliberately excluded — its
    /// single-ADC mux is not gateware-verified here and has no bench. Both flags
    /// default false, so this is dark for every board in production.
    /// </summary>
    public static bool TimeMuxesPsFeedbackOnDdc0(HpsdrBoardKind board)
        => board switch
        {
            HpsdrBoardKind.HermesC10 => G2ePsTimeMuxOnAir,
            HpsdrBoardKind.HermesII  => Hermes10ePsTimeMuxOnAir,
            _                        => false,
        };

    /// <summary>
    /// RX-thread dispatch decision (issue #960) — should an inbound DDC0 packet
    /// (UDP src port 1035) be consumed as a PureSignal paired-feedback frame
    /// instead of a user-RX IQ frame? True when PS feedback is armed AND the
    /// packet is on DDC0 AND either:
    ///   - the board permanently reserves DDC0 for the feedback pair
    ///     (<see cref="ReservesPsFeedbackDdcs"/>, dual-ADC OrionMkII/Saturn/G2 —
    ///     <paramref name="txKeyed"/> irrelevant, the user RX is at DDC2), or
    ///   - the board time-multiplexes feedback onto DDC0
    ///     (<paramref name="timeMuxOnDdc0"/>, single-ADC G2E) AND transmit is
    ///     asserted (<paramref name="txKeyed"/> = MOX || TUNE). At rest DDC0 is
    ///     the operator's only RX, so feedback must NEVER engage off-burst.
    ///
    /// <paramref name="timeMuxOnDdc0"/> is passed explicitly (rather than read
    /// inside from <see cref="TimeMuxesPsFeedbackOnDdc0"/>) so the compose and
    /// dispatch sites share one freshly-derived value and the pure logic stays
    /// deterministically testable; production sites pass
    /// <c>TimeMuxesPsFeedbackOnDdc0(board)</c>, which is dark until the burn-zone
    /// interlock lifts.
    /// </summary>
    public static bool RoutesDdc0ToPsFeedback(
        bool psFeedbackEnabled, int ddcIndex, HpsdrBoardKind board,
        bool txKeyed, bool timeMuxOnDdc0)
        => psFeedbackEnabled && ddcIndex == 0
           && (ReservesPsFeedbackDdcs(board)
               || (timeMuxOnDdc0 && txKeyed));

    /// <summary>
    /// Back-compat overload (dual-ADC semantics): no time-mux, not keyed. Equal
    /// to the historical
    /// <c>psFeedbackEnabled &amp;&amp; ddcIndex == 0 &amp;&amp; ReservesPsFeedbackDdcs(board)</c>
    /// for every board, so existing callers and the dual-ADC G2 path stay
    /// byte-identical.
    /// </summary>
    public static bool RoutesDdc0ToPsFeedback(
        bool psFeedbackEnabled, int ddcIndex, HpsdrBoardKind board)
        => RoutesDdc0ToPsFeedback(psFeedbackEnabled, ddcIndex, board,
            txKeyed: false, timeMuxOnDdc0: false);

    /// <summary>
    /// Wire-compose decision (issue #960), the compose-side twin of
    /// <see cref="RoutesDdc0ToPsFeedback"/>: may Zeus place PureSignal feedback
    /// bytes on ANY outbound command for this board when PS feedback is armed?
    /// True only when PS feedback is armed AND the board reserves the DDC0/DDC1
    /// feedback pair (<see cref="ReservesPsFeedbackDdcs"/>).
    ///
    /// This is the single predicate every PS wire-compose site funnels through —
    /// the RxSpecific DDC-enable/sync block, the TxSpecific PA-protection bytes
    /// (p[57..59]), and the HighPriority DDC0/DDC1 phase mirror + ALEX_PS /
    /// ALEX_RX_ANTENNA_BYPASS coupler bits. Routing them all through one board
    /// predicate keeps them consistent: on the single-ADC Hermes-class boards
    /// (incl. ANAN-G2E·HermesC10) NONE of those bytes ever reach the wire, so an
    /// "armed" PS state can no longer flip the RX front-end onto the feedback
    /// coupler (external K36 tap) during a TX burst for no benefit. On the
    /// dual-ADC OrionMkII/Saturn/G2 family this equals the bare
    /// <c>_psFeedbackEnabled</c> it replaces, so every wire command is
    /// byte-for-byte unchanged there.
    ///
    /// NB: this gates only the WIRE bytes — it does not change PS arm/disarm
    /// state, persistence, or the WDSP engine, all of which remain owned by the
    /// burn-zone PS path. On the G2E it therefore DISABLES PS (protecting RX
    /// audio); it does not implement it. See <see cref="ReservesPsFeedbackDdcs"/>.
    /// </summary>
    public static bool ComposesPsFeedbackWire(
        bool psFeedbackEnabled, HpsdrBoardKind board)
        => psFeedbackEnabled && ReservesPsFeedbackDdcs(board);

    internal static byte AdcOptionMask(byte numAdc)
    {
        // Thetis SetADCDither/SetADCRandom writes ADC0, ADC1, and ADC2
        // together, and CmdRx masks those three bits into bytes 5/6. For the
        // two-ADC G2 this preserves Thetis parity; firmware ignores any
        // non-present ADC bit.
        return numAdc == 0 ? (byte)0 : (byte)0x07;
    }

    internal static byte Rx2AdcSource(byte numAdc, HpsdrBoardKind boardKind)
    {
        // RX2 shares RX1's ADC (ADC0 = the main receive antenna). RX1's DDC is
        // always configured on ADC0, so a second receiver listening on a
        // different band must read the same ADC to hear the same antenna.
        //
        // The earlier code sourced ADC1 on dual-ADC Orion/Saturn/G2 boards on
        // the theory that ADC1 is "the RX2 input". On a G2 that is the separate
        // RX2/EXT antenna jack — empty on a normal single-antenna station, so
        // DDC3 clocked out at full rate but carried silence (verified on a live
        // G2: RX2 IQ RMS 9.6e-6 vs RX1 6.2e-4, a flat panadapter). Defaulting to
        // ADC0 makes RX2 work for the common same-antenna case. A future RX2
        // antenna/ADC selector can opt back into ADC1 for true diversity / a
        // second feedline; until that UX exists, ADC0 is the only correct default.
        _ = numAdc;
        _ = boardKind;
        return 0x00;
    }

    // Static byte composer — pure function over (seq, numAdc, sampleRateKhz,
    // psEnabled, boardKind, ADC options). Exposed internal so wire-format
    // tests don't need a live socket. SendCmdRx constructs the same bytes and
    // pushes to UDP. boardKind defaults to OrionMkII for backward compat —
    // every pre-Brick2 caller (and every pre-Brick2 test) keeps the DDC2 wire
    // shape and zero ADC option bytes.
    internal static byte[] ComposeCmdRxBuffer(
        uint seq,
        byte numAdc,
        ushort sampleRateKhz,
        bool psEnabled,
        HpsdrBoardKind boardKind = HpsdrBoardKind.OrionMkII,
        bool adcDitherEnabled = false,
        bool adcRandomEnabled = false,
        bool rx2Enabled = false,
        bool g2eFeedbackBurst = false)
    {
        var p = new byte[BufLen];
        WriteBeU32(p, 0, seq);
        p[4] = numAdc;
        byte adcMask = AdcOptionMask(numAdc);
        p[5] = adcDitherEnabled ? adcMask : (byte)0;
        p[6] = adcRandomEnabled ? adcMask : (byte)0;

        if (g2eFeedbackBurst)
        {
            // G2E single-ADC time-mux PS feedback descriptor (issue #960).
            // During a TX burst DDC0 carries the coupler(ADC)+TX-DAC-reference
            // interleave: byte 1363 = 0x02 arms the SyncRx[0][1] mux so the Rx0
            // FIFO controller interleaves rx_I[0]=temp_ADC (coupler, emitted
            // first) with rx_I[NR]=temp_DACD (the hardwired reference, emitted
            // second) — Hermes.v:717-720 / :1229 / :1241, Rx_specific_C&C.v:181.
            // ONLY DDC0 is enabled: the reference is the internal rx_I[NR]
            // receiver synced into Rx0, NOT a separate DDC, so no DDC1 enable
            // (Hermes.v Rx0_fifo_ctrl_inst .Sync_data_in_I(rx_I[0])). The user
            // RX is relinquished for the burst (we are transmitting). 192 kHz /
            // 24-bit, ADC0 — same feedback geometry the dual-ADC family uses.
            // Single-ADC only; never composed for boards that reserve the
            // DDC0/DDC1 pair. Caller gates this on
            // TimeMuxesPsFeedbackOnDdc0(board) && txKeyed, so it is dark in
            // production until the burn-zone interlock lifts.
            p[7] = 0x01;            // DDC0 enable only (no DDC1)
            p[17] = 0x00;           // DDC0 source = ADC0
            WriteBeU16(p, 18, 192); // DDC0 sample rate = 192 kHz (0x00C0 BE)
            p[22] = 24;             // DDC0 = 24-bit IQ
            p[1363] = 0x02;         // SyncRx[0][1] coupler/reference interleave
            return p;
        }

        int rxDdc = RxBaseDdc(boardKind);
        byte ddcEnable = (byte)(1 << rxDdc);

        if (psEnabled)
        {
            // This branch composes ONLY the dual-ADC Orion-style layout: the
            // dedicated DDC0+DDC1 feedback pair (byte 1363 sync) alongside the
            // user RX on DDC2, for boards where Zeus reserves the front DDCs for
            // feedback — the OrionMkII/Saturn/G2 family (ReservesPsFeedbackDdcs).
            //
            // The single-ADC Hermes-class radios (Hermes/0x01, HermesII/0x02,
            // HermesC10/0x14) do NOT take this branch — DDC0 is the operator's
            // only RX, so the Orion pair layout does not apply. This is NOT "PS
            // off" for the G2E/10E: as of v0.10.8 (#960) those boards perform
            // Orion-style PS by TIME-MUXING the feedback pair onto DDC0 during a
            // TX burst via the separate g2eFeedbackBurst descriptor above (byte
            // 1363 = 0x02 coupler/reference interleave, DDC0-only), gated by
            // TimeMuxesPsFeedbackOnDdc0 + txKeyed. Do NOT "restore" a disable
            // here or add the reserve-DDC layout for the G2E — that would break
            // the shipped single-ADC time-mux path and mis-route DDC0.
            // ReservesPsFeedbackDdcs is the same board set as the old explicit
            // `!= Hermes && != HermesII && != HermesC10` test (issue #960).
            if (ReservesPsFeedbackDdcs(boardKind))
            {
                ddcEnable |= 0x01;
                p[17] = 0x00;
                WriteBeU16(p, 18, 192);
                p[22] = 24;
                p[23] = numAdc;
                WriteBeU16(p, 24, 192);
                p[28] = 24;
                p[1363] = 0x02;
            }
        }

        // Second receiver (RX2): enable its own DDC (RxBaseDdc + 1) so it
        // streams an independent IQ flow we can tune to a different band. Skip
        // if it would collide with the primary RX DDC. The PS feedback pair
        // (DDC0/1 on Orion-family) is only armed during PureSignal, so RX2 on
        // DDC3 doesn't conflict with it.
        int rx2Ddc = Rx2Ddc(boardKind);
        bool rx2Active = rx2Enabled && rx2Ddc != rxDdc;
        if (rx2Active) ddcEnable |= (byte)(1 << rx2Ddc);

        p[7] = ddcEnable;
        int off = 17 + rxDdc * 6;
        p[off + 0] = 0x00;
        WriteBeU16(p, off + 1, sampleRateKhz);
        p[off + 5] = 24;

        if (rx2Active)
        {
            // RX2 DDC config block: source the same ADC as RX1 (ADC0, the main
            // receive antenna) so a second receiver on another band hears the
            // same feedline — see Rx2AdcSource. Sourcing ADC1 fed the empty
            // RX2/EXT jack and produced a silent DDC on a normal station.
            int off2 = 17 + rx2Ddc * 6;
            p[off2 + 0] = Rx2AdcSource(numAdc, boardKind);
            WriteBeU16(p, off2 + 1, sampleRateKhz);
            p[off2 + 5] = 24;
        }
        return p;
    }

    /// <summary>
    /// A single user receiver's DDC assignment for the generalized
    /// Receive-Specific composer: which physical ADC feeds it (0 or 1 on a
    /// dual-ADC board) and its IQ sample rate in kHz. The DDC index is derived
    /// from the board's RX base (<see cref="RxBaseDdc"/>) plus the receiver's
    /// position in the list.
    /// </summary>
    internal readonly record struct DdcReceiverSpec(byte AdcSource, ushort SampleRateKhz);

    /// <summary>
    /// Generalized "Receive Specific" composer for up to <see cref="MaxRxDdc"/>
    /// concurrent DDCs, each independently assignable to either ADC and to its
    /// own sample rate — the N-receiver foundation for full multi-RX operation.
    /// The legacy <see cref="ComposeCmdRxBuffer"/> covers the RX1(+RX2)(+PS)
    /// special case and stays byte-pinned by the wire tests; this method
    /// produces byte-identical output for the equivalent single-/dual-receiver
    /// inputs (see ComposeCmdRxBufferForReceiversTests) and extends cleanly to
    /// all 8 DDCs.
    ///
    /// Receiver <c>i</c> is placed on DDC <c>RxBaseDdc(board) + i</c>.
    /// PureSignal, when armed on Orion-family boards, additionally claims
    /// DDC0/DDC1 for its feedback pair (192 kHz/24-bit + the byte-1363 sync)
    /// exactly as the legacy composer does; callers must not assign user
    /// receivers onto those reserved slots while PS is armed.
    /// </summary>
    internal static byte[] ComposeCmdRxBufferForReceivers(
        uint seq,
        byte numAdc,
        IReadOnlyList<DdcReceiverSpec> receivers,
        bool psEnabled = false,
        HpsdrBoardKind boardKind = HpsdrBoardKind.OrionMkII,
        bool adcDitherEnabled = false,
        bool adcRandomEnabled = false)
    {
        ArgumentNullException.ThrowIfNull(receivers);
        var p = new byte[BufLen];
        WriteBeU32(p, 0, seq);
        p[4] = numAdc;
        byte adcMask = AdcOptionMask(numAdc);
        p[5] = adcDitherEnabled ? adcMask : (byte)0;
        p[6] = adcRandomEnabled ? adcMask : (byte)0;

        int baseDdc = RxBaseDdc(boardKind);
        byte ddcEnable = 0;

        // PS feedback pair (DDC0+DDC1, byte 1363 sync) — Zeus only composes it
        // for boards where it reserves the front DDCs for feedback (the dual-ADC
        // OrionMkII/Saturn/G2 family). Mirror the legacy composer exactly there;
        // no PS block on single-ADC Hermes-class. ReservesPsFeedbackDdcs is the
        // same board set as the old explicit `!= Hermes && != HermesII &&
        // != HermesC10` test (issue #960). NB: skipping the block on the G2E
        // DISABLES PS to protect its single-ADC RX — it is not a hardware limit
        // (the G2E gateware can do Orion-style PS on DDC0; see
        // ReservesPsFeedbackDdcs). Implementing it needs user RX relocated off
        // DDC0, a separate G2E-bench-gated change.
        bool psHardware = ReservesPsFeedbackDdcs(boardKind);
        if (psEnabled && psHardware)
        {
            ddcEnable |= 0x01;
            WriteDdcConfigBlock(p, ddc: 0, adc: 0x00, sampleRateKhz: 192);
            WriteDdcConfigBlock(p, ddc: 1, adc: numAdc, sampleRateKhz: 192);
            p[1363] = 0x02;
        }

        for (int i = 0; i < receivers.Count; i++)
        {
            int ddc = baseDdc + i;
            if (ddc > MaxRxDdc - 1)
                throw new ArgumentOutOfRangeException(nameof(receivers),
                    $"receiver {i} maps to DDC{ddc}, exceeding the {MaxRxDdc}-DDC protocol ceiling");
            ddcEnable |= (byte)(1 << ddc);
            WriteDdcConfigBlock(p, ddc, receivers[i].AdcSource, receivers[i].SampleRateKhz);
        }

        p[7] = ddcEnable;
        return p;
    }

    // Writes one DDC's 6-byte Receive-Specific config block at offset
    // 17 + ddc*6: [+0] ADC source, [+1..2] sample rate (kHz, big-endian),
    // [+5] sample size (bit depth). Matches the inline writes in
    // ComposeCmdRxBuffer byte-for-byte; bits defaults to 24 (the only depth any
    // supported board uses).
    private static void WriteDdcConfigBlock(byte[] p, int ddc, byte adc, ushort sampleRateKhz, byte bits = 24)
    {
        int off = 17 + ddc * 6;
        p[off + 0] = adc;
        WriteBeU16(p, off + 1, sampleRateKhz);
        p[off + 5] = bits;
    }

    private void SendCmdRx()
    {
        // Mirrors pihpsdr new_protocol_receive_specific for the MkII:
        //   n_adc = G2 has two physical ADCs; DDC2 enabled by bit 2 in the
        //   enable mask; the DDC config block sits at 17 + 2*6 = 29. DDC0/1
        //   stay disabled by default — those slots are reserved by the radio
        //   for the PureSignal / Diversity hardware pair. When PS is armed,
        //   ComposeCmdRxBuffer also enables DDC0, configures DDC0/1 at
        //   192 kHz / 24-bit, and sets byte 1363 = 0x02 to sync DDC1→DDC0
        //   (pihpsdr new_protocol.c:1611-1630).
        int extras = Volatile.Read(ref _extraReceiverCount);
        // G2E single-ADC time-mux PS (#960): during a TX burst with PS armed,
        // DDC0 carries the coupler+TX-DAC-reference interleave instead of the
        // user RX. Derived fresh, nothing persisted; dark in production until the
        // burn-zone interlock lifts (TimeMuxesPsFeedbackOnDdc0). The keepalive
        // re-emits this command ~5 Hz, so a dropped MOX edge self-heals from the
        // live state. The user RX (incl. RX2/extras) is relinquished for the
        // burst, so the feedback descriptor takes priority over the extras path.
        bool g2eFeedbackBurst = _psFeedbackEnabled
            && TimeMuxesPsFeedbackOnDdc0(_boardKind)
            && (_moxOn || _tuneActive);
        byte[] p;
        if (extras > 0 && !g2eFeedbackBurst)
        {
            // Full multi-DDC: RX1 + RX2 + RX3.. as a contiguous DDC run via the
            // N-receiver composer. For the RX1(+RX2)(+PS) subset this is
            // byte-identical to the legacy composer (ComposeCmdRxBufferForReceivers
            // tests), so existing behaviour is preserved; extras add DDC config
            // blocks for RxBaseDdc+2.. only. PS DDC0/1 handling is inside the
            // composer and untouched.
            byte rxAdc = Rx2AdcSource(_numAdc, _boardKind); // ADC0 (same antenna)
            var specs = new List<DdcReceiverSpec>(2 + extras)
            {
                new DdcReceiverSpec(rxAdc, (ushort)_sampleRateKhz),  // RX1
                new DdcReceiverSpec(rxAdc, (ushort)_sampleRateKhz),  // RX2
            };
            for (int k = 0; k < extras; k++)
                specs.Add(new DdcReceiverSpec(_extraRxAdc[2 + k], (ushort)_sampleRateKhz));
            p = ComposeCmdRxBufferForReceivers(
                _seqCmdRx++,
                _numAdc,
                specs,
                _psFeedbackEnabled,
                _boardKind,
                _adcDitherEnabled,
                _adcRandomEnabled);
        }
        else
        {
            p = ComposeCmdRxBuffer(
                _seqCmdRx++,
                _numAdc,
                (ushort)_sampleRateKhz,
                _psFeedbackEnabled,
                _boardKind,
                _adcDitherEnabled,
                _adcRandomEnabled,
                Volatile.Read(ref _rx2Enabled) != 0,
                g2eFeedbackBurst);
        }
        _sock!.SendTo(p, new IPEndPoint(_radioEndpoint!.Address, 1025));
    }

    private void SendCmdTx()
    {
        var cw = new CwKeyerWireConfig
        {
            Active = Volatile.Read(ref _cwActive),
            Mode = _cwMode,
            SpeedWpm = _cwSpeedWpm,
            SidetoneHz = _cwSidetoneHz,
            SidetoneLevel = _cwSidetoneLevel,
        };
        // Board-gate the PS-armed TxSpecific shape (p[57..59] PA-protection /
        // step-att for the TX-DAC reference) the same way every other PS wire
        // site is gated (#960): only boards that reserve the feedback DDCs emit
        // it. On the single-ADC G2E this keeps the TX command byte-identical to
        // the non-PS path even when PS is "armed" upstream. Byte-for-byte
        // unchanged on the dual-ADC OrionMkII/Saturn/G2 family.
        bool psWire = ComposesPsFeedbackWire(_psFeedbackEnabled, _boardKind);
        var p = ComposeCmdTxBuffer(_seqCmdTx++, (ushort)_sampleRateKhz, _txStepAttnDb, _paEnabled, psWire, _micControl, _lineInGain, cw);
        _sock!.SendTo(p, new IPEndPoint(_radioEndpoint!.Address, 1026));
    }

    /// <summary>
    /// Configure the radio's <b>internal (FPGA) CW keyer</b> — issue #1032.
    /// On Protocol 2 the host does not key CW: setting this (in CW mode) arms
    /// the gateware so a physical key/paddle on the rear KEY jack keys the
    /// transmitter directly. Latches the operator values and re-pushes the
    /// TxSpecific packet so the radio honours them on the next transmit cycle
    /// (load-bearing — the radio keeps its last-remembered config otherwise,
    /// the same pihpsdr transmit_specific risk noted on the audio front-end).
    /// No-op on the wire until the RX task is live; the values are latched and
    /// emitted on the first CmdTx after connect.
    /// </summary>
    public void SetCwKeyerConfig(in CwKeyerWireConfig cw)
    {
        Volatile.Write(ref _cwActive, cw.Active);
        _cwMode = cw.Mode;
        _cwSpeedWpm = cw.SpeedWpm;
        _cwSidetoneHz = cw.SidetoneHz;
        _cwSidetoneLevel = cw.SidetoneLevel;
        if (_rxTask is not null) SendCmdTx();
    }

    /// <summary>
    /// Set the audio front-end from already-composed wire bytes
    /// (external-audio-jacks re-port). The TX-audio source model resolves
    /// byte-50 mic_control and byte-51 line_in_gain as pure functions of the
    /// selected <c>TxAudioSource</c> upstream (RadioService / ExternalPortEncoder),
    /// so the client just latches them and re-pushes CmdTx on the next TX cycle.
    /// With <c>micControl == 0 &amp;&amp; lineInGain == 0</c> (the Host source) the
    /// CmdTx tail is byte-identical to today. Re-pushing CmdTx on every change is
    /// load-bearing — the radio keeps its last-remembered input otherwise
    /// (pihpsdr transmit_specific risk).
    /// </summary>
    public void SetAudioFrontEndBytes(byte micControl, byte lineInGain)
    {
        _micControl = micControl;
        _lineInGain = lineInGain;
        if (_rxTask is not null) SendCmdTx();
    }

    // Test-seamed Compose for the CmdTx (TxSpecific) packet. Layout per
    // pihpsdr new_protocol.c new_protocol_tx_specific (1502-1594) and
    // saturnmain.c::saturn_handle_duc_specific:
    //   bytes 0..3   : sequence (BE)
    //   byte  4      : num_dac (1 on G2)
    //   bytes 14..15 : DAC sample rate kHz (BE) — Zeus-only; Saturn ignores
    //   byte  57     : reserved on Saturn (FPGA does not read)
    //   byte  58     : ADC1 TX step attenuator (TX-DAC reference loopback)
    //   byte  59     : ADC0 TX step attenuator (PA-coupler feedback)
    //
    // pihpsdr's PA-protection / PS asymmetry (new_protocol.c:1540-1547):
    //   if (xmit && pa_enabled) { p[58]=31; p[59]=31; }   // protect both ADCs
    //   if (puresignal)         { p[59]=tx->attenuation; } // ONLY byte 59
    //
    // Byte 58 is NEVER overridden by PS — the TX-DAC reference ADC needs
    // its own protection independent of where AutoAttenuate parks the
    // PA-feedback ADC. If we let PS write to byte 58 too, the first
    // attenuator step drops the loopback reference in lockstep with the
    // feedback, leaving the gain ratio uncorrected and starving calcc.
    //
    // When PS is OFF we keep the historical Zeus shape (value across
    // 57/58/59) — operator's normal voice TX has been validated working
    // with that wire form on G2 MkII, so we don't ship a wire change
    // beyond the PS-armed window in this patch.
    internal static byte[] ComposeCmdTxBuffer(uint seq, ushort sampleRateKhz, byte txStepAttnDb, bool paEnabled, bool psEnabled, byte micControl = 0, byte lineInGain = 0, CwKeyerWireConfig cw = default)
    {
        var p = new byte[60];
        WriteBeU32(p, 0, seq);
        p[4] = 1;                 // num_dac
        WriteBeU16(p, 14, sampleRateKhz);

        // Internal (FPGA) CW keyer — issue #1032. Byte 5 is the CW mode_control
        // bitfield and is set ONLY in CW mode (cw.Active); bytes 6-13/17 carry
        // the keyer parameters. Layout = pihpsdr new_protocol.c
        // new_protocol_tx_specific (transmit_specific_buffer[5..17]) and
        // OpenHPSDR Ethernet Protocol v4.4 "Transmitter Specific" (pp.29-30).
        // When cw.Active is false byte 5 stays 0 and the CW bytes stay 0, so a
        // non-CW (SSB/AM/FM/DIG) transmit is byte-identical to the historical
        // wire form. The host never sets MOX for internal CW — the gateware
        // self-keys off the rear KEY jack once byte-5 bit-1 is set.
        if (cw.Active)
        {
            // Gateware-pinned bits use ternary-OR (not `if`) so the const
            // false/true folds to an expression rather than unreachable code.
            byte mode = CwModeCwSelected;
            mode |= CwKeyerReverse ? CwModeReverse : (byte)0;
            // Straight = no element timing (external keyer/bug pass-through);
            // Iambic-A sets the iambic bit; Iambic-B sets iambic + Mode B.
            if (cw.Mode == CwKeyerMode.IambicA) mode |= CwModeIambic;
            else if (cw.Mode == CwKeyerMode.IambicB) mode |= (byte)(CwModeIambic | CwModeModeB);
            mode |= cw.SidetoneLevel != 0 ? CwModeSidetone : (byte)0;
            mode |= CwKeyerSpacing ? CwModeStrictSpacing : (byte)0;
            mode |= CwKeyerBreakIn ? CwModeBreakIn : (byte)0;
            p[5] = mode;

            p[6] = (byte)(cw.SidetoneLevel & 0x7F);
            WriteBeU16(p, 7, (ushort)Math.Clamp(cw.SidetoneHz, 0, 0xFFFF));
            int wpm = Math.Clamp(cw.SpeedWpm, 1, 60);
            p[9] = (byte)wpm;
            p[10] = CwKeyerWeight;
            WriteBeU16(p, 11, (ushort)Math.Clamp(CwKeyerHangMs, 0, 0xFFFF));
            // RF (key-down) delay, clamped to 900/WPM — pihpsdr's documented
            // FPGA iambic-keyer-bug workaround (new_protocol.c:1500-1503).
            int rfDelay = Math.Min(CwKeyerRfDelayMs, 900 / wpm);
            p[13] = (byte)Math.Clamp(rfDelay, 0, 255);
            p[17] = CwKeyerRampMs;
        }

        // Audio front-end (external-audio-jacks re-port). Byte 50 = mic_control
        // flags, byte 51 = line_in_gain — Thetis network.c:1234,1236. Both
        // default 0, so an untouched audio front-end is byte-identical to the
        // historical all-zero tail.
        p[50] = micControl;
        p[51] = lineInGain;

        if (psEnabled)
        {
            // PS-armed canonical pihpsdr shape: byte 58 holds PA-protection
            // for the TX-DAC reference; byte 59 takes the dynamic step-att
            // so calcc can read the post-PA envelope.
            p[57] = 0;
            p[58] = paEnabled ? (byte)31 : (byte)0;
            p[59] = txStepAttnDb;
        }
        else
        {
            // Historical Zeus shape — preserved so normal voice TX wire
            // form is unchanged. Revisit when bringing the full pihpsdr
            // PA-protection invariant to non-PS TX.
            p[57] = txStepAttnDb;
            p[58] = txStepAttnDb;
            p[59] = txStepAttnDb;
        }
        return p;
    }

    /// <summary>
    /// Set the TX step attenuator (0..31 dB) and re-emit the CmdTx packet
    /// so the radio honours the new value on the next transmit cycle. Bytes
    /// 57/58/59 — Thetis network.c:1238-1242. Used by the PS auto-attenuate
    /// loop to ramp feedback level into the 128..181 window when calcc
    /// rejects fits because the loopback is too hot or too quiet.
    /// </summary>
    /// <summary>Current TX feedback step attenuation in dB (the ATTOnTX value
    /// last written via <see cref="SetTxAttenuationDb"/>). Sticky across
    /// MOX/unkey — the radio holds it until changed — so the PureSignal
    /// auto-attenuate dance can read ground truth on re-arm instead of
    /// assuming 0 and desyncing its model from the radio.</summary>
    public byte TxStepAttnDb => _txStepAttnDb;

    public void SetTxAttenuationDb(byte db)
    {
        if (db > 31) db = 31;
        if (_txStepAttnDb == db) return;
        _txStepAttnDb = db;
        if (_rxTask is not null) SendCmdTx();
        _log.LogInformation("p2.txAttn db={Db}", db);
    }

    // RX front-end bytes of the CmdHighPriority (port 1027) packet. Grouped
    // so the Mercury-preamp bit and the two ADC step-attenuators have one
    // golden-byte seam to lock the wire offsets against regression. Issue
    // #126 — the preamp bit at byte 1403 was inert on Angelia / ANAN-100D
    // until the RadioService → DspPipelineService → Protocol2Client forwarding
    // landed; this offset must not silently move again. Byte-identical to the
    // inline writes it replaces.
    //   1403 — Mercury attenuator byte: bit 0 = RX0 preamp, bit 1 = RX1 preamp
    //          (Thetis network.c:1037)
    //   1442 — ADC1 step attenuator 0-31 dB (`Attenuator1`, High_Priority_CC.v:186-189)
    //   1443 — ADC0 step attenuator 0-31 dB (Thetis network.c:1057)
    internal static void WriteRxFrontEndBytes(
        Span<byte> p, bool preampOn, byte adc1StepAttnDb, byte adc0StepAttnDb)
    {
        p[1403] = (byte)(preampOn ? 0x01 : 0x00);
        p[1442] = adc1StepAttnDb;
        p[1443] = adc0StepAttnDb;
    }

    private void SendCmdHighPriority(bool run)
    {
        var p = new byte[BufLen];
        WriteBeU32(p, 0, _seqCmdHp++);
        // PureSignal feedback bytes (DDC0/DDC1 phase mirror + ALEX_PS / bypass
        // coupler bits) may go on the wire ONLY when Zeus reserves the front
        // DDCs for feedback on this board (issue #960). On the single-ADC
        // Hermes-class boards (incl. ANAN-G2E·HermesC10) DDC0 is the operator's
        // only RX and Zeus disables PS, so this is false and NO PS bytes are
        // composed — even if the engine/state "armed" PS upstream. Without this
        // the ALEX_PS / bypass bits flipped the RX front-end onto the feedback
        // coupler during every TX burst (external K36 tap) for no benefit. On
        // the dual-ADC OrionMkII/Saturn/G2 family this is identical to the bare
        // `_psFeedbackEnabled` it replaces, so the wire is byte-for-byte
        // unchanged there.
        bool psWire = ComposesPsFeedbackWire(_psFeedbackEnabled, _boardKind);
        // Byte 4 bit 0 = run, bit 1 = PTT. Thetis network.c:924-925 and
        // pihpsdr new_protocol.c:746-757 both set bit 1 whenever the radio
        // should key — covers both mic-MOX and TUN. Without this bit the
        // radio stays in RX regardless of drive / tune state.
        p[4] = (byte)((_moxOn || _tuneActive ? 0x02 : 0x00) | (run ? 0x01 : 0x00));

        // Frequency field is a PHASE word (general[37] bit 3 set) — radio
        // reads a 32-bit phase increment, not Hz. pihpsdr computes this as
        //   phase = freq_hz * 2^32 / 122_880_000
        // The G2 MkII puts the user-visible RX0 at DDC slot 2, so the phase
        // goes to bytes 9 + 2*4 = 17..20. Hermes-class (Brick2) puts RX0 at
        // DDC slot 0, so the phase goes to bytes 9..12 (`9 + 0*4`). Bytes
        // not used by the active RX slot stay zero on the non-PS path. TX
        // DUC phase is written to 329..332 regardless of board.
        uint rxPhase = (uint)(_rxFreqHz * HzToPhase);
        int rxDdc = RxBaseDdc(_boardKind);
        WriteBeU32(p, 9 + rxDdc * 4, rxPhase);
        // TX DUC NCO phase. Normally _txDucFreqHz == _rxFreqHz, so this is
        // identical to rxPhase (DUC follows RX0). For dual-RX split TX it is set
        // independently to VFO B (SetTxDucFrequency), so the carrier lands on B
        // while the RX0 DDC above stays on VFO A — fixing the two-carrier bug.
        WriteBeU32(p, 329, (uint)(_txDucFreqHz * HzToPhase));

        // Second receiver (RX2): tune its own DDC's NCO to _rx2FreqHz so it
        // demodulates an independent band. Each DDC's phase word lives at
        // bytes 9 + ddc*4 (DDC3 -> 21..24 on Orion-family). Only written when
        // RX2 is enabled; otherwise the slot stays zero like the other unused
        // DDCs on the non-PS path.
        if (Volatile.Read(ref _rx2Enabled) != 0)
        {
            int rx2Ddc = Rx2Ddc(_boardKind);
            uint rx2Phase = (uint)(_rx2FreqHz * HzToPhase);
            WriteBeU32(p, 9 + rx2Ddc * 4, rx2Phase);
        }

        // Extra receivers (RX3+): tune each contiguous DDC's NCO. Receiver index
        // 2+k maps to DDC RxBaseDdc+2+k, phase word at 9 + ddc*4. Additive — only
        // the configured extras are written; unused DDC slots stay zero. RX1/RX2
        // and the PureSignal DDC0/1 phase writes above are untouched.
        int extraN = Volatile.Read(ref _extraReceiverCount);
        if (extraN > 0)
        {
            int baseDdc = RxBaseDdc(_boardKind);
            for (int k = 0; k < extraN; k++)
            {
                int rcvr = 2 + k;
                uint phase = (uint)(_extraRxFreqHz[rcvr] * HzToPhase);
                WriteBeU32(p, 9 + (baseDdc + rcvr) * 4, phase);
            }
        }

        // PureSignal — when armed, DDC0 + DDC1 phase words also need to
        // track the TX frequency during xmit so the feedback DDC samples
        // the actual TX coupler signal. pihpsdr new_protocol.c:827-839.
        // Board-gated via psWire so DDC0's phase is never overwritten on a
        // single-ADC board where DDC0 carries the operator's RX (#960).
        if (psWire && _moxOn)
        {
            // For now mirror the RX freq onto the TX side — the radio's
            // single-VFO assumption today means TX = RX. Multi-VFO support
            // is a follow-up.
            uint txPhase = rxPhase;
            WriteBeU32(p, 9, txPhase);     // DDC0 = TX freq
            WriteBeU32(p, 13, txPhase);    // DDC1 = TX freq
        }

        // Drive level (0..255) at byte 345. Set by RadioService after applying
        // per-band PA gain calibration. Honored by the radio only while run=1
        // and TX is keyed elsewhere (byte 4 bit 1). piHPSDR `new_protocol.c:860`.
        p[345] = _driveByte;

        // OC outputs (7-bit mask) shifted left by 1 into byte 1401. The TX
        // mask applies whenever MOX is on (whether keyed by the operator or
        // by TUN) and the RX mask applies otherwise. The piHPSDR-style global
        // "OCtune" override was removed in #124 for hardware-safety reasons:
        // a global override layered on top of the per-band OC TX mask could
        // hand an external amp a confused band-select state during a steady
        // tune carrier and damage the finals. Thetis behaves this way too.
        byte ocBits = (_moxOn || _tuneActive) ? _ocTxMask : _ocRxMask;
        p[1401] = (byte)((ocBits & 0x7F) << 1);

        // Anvelina-PRO3 DX OC extension (USEROUT7..10) at byte 1397, bits
        // [4:1]. EU2AV's Open_Collector_Anvelina_DX for Thetis spec
        // (issue #407): bit 1 -> USEROUT7, bit 2 -> USEROUT8,
        // bit 3 -> USEROUT9, bit 4 -> USEROUT10. Bit 0 is reserved-internal
        // and bits [7:5] are reserved-future — both MUST be transmitted as
        // 0. Gated on board+variant so the byte stays at 0 on every other
        // 0x0A-family radio (G2 / G2_1K / 7000DLE / 8000DLE / OrionMkII
        // original / Red Pitaya), matching pre-#407 behaviour. Firmware
        // additionally gates with run=1 at the FPGA so the bytes are
        // harmless until streaming is up.
        if (_boardKind == HpsdrBoardKind.OrionMkII
            && _variant == OrionMkIIVariant.AnvelinaPro3)
        {
            byte dxBits = (_moxOn || _tuneActive) ? _ocDxTxMask : _ocDxRxMask;
            p[1397] = (byte)((dxBits & 0x0F) << 1);
        }

        // DLE_outputs byte for ANAN-8000DLE / AnvelinaPro3 (`Orion.v` line
        // 2119/2122/2125). Default 0; flipped by SetXvtrEnabled /
        // SetIo1Muted / SetAutoTuneEnabled. Hermes RTL doesn't decode byte
        // 1400, so emission is harmless on non-Orion_MkII boards. Issue #414.
        p[1400] = _dleOutputs;

        // RX front-end: Mercury preamp bit (1403) + ADC0/ADC1 step
        // attenuators (1443/1442). _rx1StepAttnDb / _rxStepAttnDb default to 0
        // and are set via SetRx1Attenuator on dual-RX dual-ADC boards (#415).
        WriteRxFrontEndBytes(p, _preampOn, _rx1StepAttnDb, _rxStepAttnDb);

        // Alex words. Bit positions and BPF selections per pihpsdr's alex.h +
        // new_protocol.c (function new_protocol_high_priority, device cases
        // NEW_DEVICE_ORION2 / NEW_DEVICE_SATURN). Offsets: Alex0 at 1432..1435,
        // Alex1 at 1428..1431. During TX both words need ALEX_TX_RELAY
        // (pihpsdr new_protocol.c:989-992) so the T/R relay on the LPF board
        // flips to the TX path; without it the TX signal reaches the antenna
        // through the RX filters and DAC images radiate as out-of-band
        // harmonics. Alex1 additionally gets RX_GNDonTX to short the RX input
        // while keyed, protecting the ADC.
        bool xmit = _moxOn || _tuneActive;
        bool rx2Enabled = Volatile.Read(ref _rx2Enabled) != 0;
        // TX-antenna relay select (external-ports plan — antenna slice, #804).
        // Gate the alex0[26:24] emission on the board/variant relay population: a
        // non-relay variant stays on ANT1 and never advertises ANT2/3. _txAntenna
        // is the 1-based wire selector and is only ever updated at idle (mid-key
        // changes defer), so the relay matrix is never hot-switched.
        int txAntWire = _hasTxAntennaRelays ? _txAntenna : 1;
        // alex0 ANT1/2/3 bits [26:24] are STATE-MULTIPLEXED (pihpsdr
        // new_protocol.c:1331-1357): during RX they reflect the RX antenna;
        // during TX — or when RX is on an aux input (EXT/XVTR/BYPASS), where the
        // ANT relay isn't used for RX — they reflect the TX antenna. alex1 ALWAYS
        // reflects TX. Without this, changing the TX antenna also moved the RX
        // path because alex0 always carried the TX antenna.
        int alex0AntWire = (xmit || _rxAuxInput != 0)
            ? txAntWire
            : ((_rxAntenna is 1 or 2 or 3) ? _rxAntenna : 1);
        // TX low-pass filter source. The alex LPF on BOTH words is a TX-path
        // filter and MUST follow the TX frequency, not RX1. _txDucFreqHz is the
        // TX-freq source of truth (== _rxFreqHz when not split, == VFO B for
        // dual-RX split TX). During RX the LPF is inert (TX relay open), so leave
        // it on _rxFreqHz to keep the non-TX wire byte-identical. WITHOUT this,
        // dual-RX split TX — where _rxFreqHz stays on VFO A while the DUC moves to
        // VFO B — left the TX LPF on the RX1 band and the filter board rejected
        // the VFO-B carrier, so there was NO RF output on a different band.
        uint txLpfFreqHz = xmit ? _txDucFreqHz : _rxFreqHz;
        // alex0 BPF follows RX1 (_rxFreqHz); LPF follows the TX freq.
        uint alex0Common = ComputeAlexWord(
            _rxFreqHz,
            txLpfFreqHz,
            txAnt: txAntWire,
            board: _boardKind,
            rfFilters: _rfFilters,
            xmit: xmit,
            psEnabled: psWire);
        // alex1 keeps the TX antenna from txAntWire; alex0 swaps in the
        // state-correct antenna (clear [26:24], re-OR via the shared encoder).
        uint alex0 = (alex0Common & ~ALEX_TX_ANTENNA_MASK) | EncodeTxAntennaBits(alex0AntWire)
                     | (xmit ? ALEX_TX_RELAY : 0u);
        uint alex1 = ComposeAlex1Word(
            _rxFreqHz,
            _rx2FreqHz,
            txLpfFreqHz,
            rx2Enabled,
            xmit,
            psWire,          // board-gated PS arm (#960): no ALEX_PS on single-ADC
            _boardKind,
            txAntWire,
            _rfFilters);
        // RX auxiliary input select (external-ports plan — antenna slice, #804).
        // Emitted on alex0 — XVTR/EXT1/EXT2/BYPASS bits 8..11 (+ Saturn RX_SELECT
        // bit 14). ORDER IS LOAD-BEARING (the PS-K36 firewall): the operator aux
        // bits are composed FIRST, then the PureSignal feedback routing below is
        // applied AFTER and OWNS the K36/BYPASS (bit 11) relay while PS is armed.
        // An operator aux=BYPASS choice can never steal the PS coupler. Computed
        // via the shared pure helper so the golden test and the wire agree.
        alex0 |= ComposeRxAuxBits(_rxAuxInput, _mkiiBpfRxSelect);
        // ----- PureSignal feedback routing — applied AFTER operator aux. DO NOT
        //       MOVE OR MODIFY: this block must stay physically last so the PS
        //       coupler bit always wins over the operator aux (PS-K36 firewall).
        // ALEX_PS_BIT (0x00040000): pihpsdr new_protocol.c:994-998 ORs this
        // into alex0 (during xmit) and alex1 (always-on while PS armed).
        // ComposeAlex1Word owns the alex1 side because it also owns ADC1/RX2
        // filter-bank selection.
        if (psWire && xmit) alex0 |= AlexPsBit;
        // External (Bypass) feedback antenna — pihpsdr new_protocol.c:1284-
        // 1296 ORs ALEX_RX_ANTENNA_BYPASS into alex0 only during xmit when
        // PS is armed and the operator selected the external path. Internal
        // coupler leaves this bit clear.
        //
        // G2E (#960 follow-up): on the full-Orion dual-ADC wire psWire owns this,
        // but the single-ADC HermesC10 does time-mux PS with psWire=false — yet
        // its ONE ADC can only see an EXTERNAL sampler tap through this relay.
        // RoutesExternalPsFeedbackBypass adds ONLY the HermesC10 arm, so the 10E
        // and every other board stay byte-identical. G2ePsTimeMuxOnAir is true, so
        // this path is LIVE in production; it is "bench-gated" only in the sense
        // that no G2E is on our bench, so first-key-down behaviour is proven by
        // construction from the C10 gateware and confirmed on a real G2E via the
        // p2.ps.g2e log (peakFb going non-zero once the external tap is connected).
        if (RoutesExternalPsFeedbackBypass(_boardKind, _psFeedbackEnabled, _psFeedbackExternal)
            && xmit)
        {
            alex0 |= AlexRxAntennaBypass;
        }
        WriteBeU32(p, 1428, alex1);
        WriteBeU32(p, 1432, alex0);
        _sock!.SendTo(p, new IPEndPoint(_radioEndpoint!.Address, 1027));

        _log.LogInformation(
            "p2.cmd_hp.tx run={Run} mox={Mox} tun={Tun} board={Board} variant={Variant} ocTx=0x{OcTx:X2} ocRx=0x{OcRx:X2} ocDxTx=0x{OcDxTx:X2} ocDxRx=0x{OcDxRx:X2} -> p[1401]=0x{B1401:X2} p[1397]=0x{B1397:X2}",
            run, _moxOn, _tuneActive, _boardKind, _variant,
            _ocTxMask, _ocRxMask, _ocDxTxMask, _ocDxRxMask,
            p[1401], p[1397]);
    }

    // ANAN-7000 / Orion-II / Saturn (G2 MkII) BPF board constants. Copied
    // verbatim from pihpsdr's alex.h — these are the RX BPF selections the
    // MkII's filter board expects. The older ALEX_*_HPF constants used for
    // ANAN-100 / classic Alex do NOT work on MkII — the filter board
    // silently selects "nothing" and all RF is cut off at the ADC.
    private const uint ALEX_ANAN7000_RX_BYPASS_BPF = 0x00001000;
    private const uint ALEX_ANAN7000_RX_160_BPF    = 0x00000040;
    private const uint ALEX_ANAN7000_RX_80_60_BPF  = 0x00000020;
    private const uint ALEX_ANAN7000_RX_40_30_BPF  = 0x00000010;
    private const uint ALEX_ANAN7000_RX_20_15_BPF  = 0x00000002;
    private const uint ALEX_ANAN7000_RX_12_10_BPF  = 0x00000004;
    private const uint ALEX_ANAN7000_RX_6_PRE_BPF  = 0x00000008;

    // TX LPF constants (used during TX; harmless during RX but worth setting
    // correctly so the BPF board stays in a sane latched state if the radio
    // momentarily T/Rs).
    private const uint ALEX_160_LPF        = 0x00800000;
    private const uint ALEX_80_LPF         = 0x00400000;
    private const uint ALEX_60_40_LPF      = 0x00200000;
    private const uint ALEX_30_20_LPF      = 0x00100000;
    private const uint ALEX_17_15_LPF      = 0x80000000;
    private const uint ALEX_12_10_LPF      = 0x40000000;
    private const uint ALEX_6_BYPASS_LPF   = 0x20000000;

    // TX antenna select.
    private const uint ALEX_TX_ANTENNA_1   = 0x01000000;
    private const uint ALEX_TX_ANTENNA_2   = 0x02000000;
    private const uint ALEX_TX_ANTENNA_3   = 0x04000000;
    // TX-antenna field [26:24] — cleared before re-OR'ing the state-multiplexed
    // antenna in SendCmdHighPriority (external-ports plan — antenna slice, #804).
    private const uint ALEX_TX_ANTENNA_MASK = ALEX_TX_ANTENNA_1 | ALEX_TX_ANTENNA_2 | ALEX_TX_ANTENNA_3;

    // Flips the T/R relay on the LPF board so the TX path reaches the antenna
    // through the selected TX LPF instead of through the RX BPF path. OR'd
    // into both alex0 and alex1 during TX (pihpsdr new_protocol.c:989-992).
    private const uint ALEX_TX_RELAY       = 0x08000000;
    // PureSignal feedback-coupler enable. OR'd into alex1 always when PS is
    // armed and into alex0 during xmit (pihpsdr new_protocol.c:994-998).
    internal const uint AlexPsBit          = 0x00040000;
    // PS External (Bypass) antenna select — pihpsdr new_protocol.c:1284-1296
    // ORs ALEX_RX_ANTENNA_BYPASS into alex0 during xmit + PS armed when the
    // operator picks the external feedback path. Internal coupler leaves
    // this bit clear.
    internal const uint AlexRxAntennaBypass = 0x00000800;
    // Operator RX-auxiliary input select (external-ports plan — antenna slice,
    // #804). alex0 bits 8..10 — pihpsdr alex.h: XVTR=bit8, EXT1=bit9, EXT2=bit10
    // (BYPASS/K36 is bit 11 = AlexRxAntennaBypass above, shared with PS external
    // feedback). Alex0Anan7000RxSelect (bit 14) is the Saturn/MkII-BPF master
    // RX-select gate, OR'd alongside any non-base aux on those boards (alex.h).
    internal const uint AlexRxAntennaXvtr   = 0x00000100;
    internal const uint AlexRxAntennaExt1   = 0x00000200;
    internal const uint AlexRxAntennaExt2   = 0x00000400;
    internal const uint Alex0Anan7000RxSelect = 0x00004000;
    // Alex1-only: grounds the RX input while keyed so the hot TX field doesn't
    // back-feed into the Mercury ADC (pihpsdr alex.h ANAN7000_RX_GNDonTX).
    private const uint ALEX1_ANAN7000_RX_GNDonTX = 0x00000100;

    // Classic Alex RX HPF bits (Hermes / ANAN-10/100/100D/200D / HermesII).
    // pihpsdr alex.h:78-84. Note these bit positions collide with
    // ANAN7000 RX BPF bits — they're the same bits in the Alex0 word
    // but mean DIFFERENT filters depending on which board is connected.
    // Issue #413.
    private const uint ALEX_13MHZ_HPF  = 0x00000002;  // bit 1
    private const uint ALEX_20MHZ_HPF  = 0x00000004;  // bit 2
    private const uint ALEX_6M_PREAMP  = 0x00000008;  // bit 3 (35 MHz HPF + LNA)
    private const uint ALEX_9_5MHZ_HPF = 0x00000010;  // bit 4
    private const uint ALEX_6_5MHZ_HPF = 0x00000020;  // bit 5
    private const uint ALEX_1_5MHZ_HPF = 0x00000040;  // bit 6
    private const uint ALEX_BYPASS_HPF = 0x00001000;  // bit 12

    /// <summary>
    /// Should the RX-aux BYPASS relay (alex0 bit 11, <see cref="AlexRxAntennaBypass"/>)
    /// route an EXTERNAL PureSignal feedback tap into the RX ADC on this board?
    ///
    /// Two paths qualify:
    ///  • The full-Orion dual-ADC PS wire (<see cref="ComposesPsFeedbackWire"/>):
    ///    pihpsdr new_protocol.c:1284-1296 ORs the bit during xmit when PS is
    ///    armed and the operator selected the external path.
    ///  • The single-ADC HermesC10 / ANAN-G2E time-mux PS path (#960 follow-up):
    ///    here <see cref="ComposesPsFeedbackWire"/> is <c>false</c>, but the board's
    ///    ONE ADC can only see an external sampler tap (post-PA pad into the
    ///    RXbypass / K36 jack — how the real G2E operator runs PS) through this
    ///    same relay. Without the bit the external feedback never reaches the ADC
    ///    and calcc can never converge — the observed "PS doesn't work on the G2E".
    ///
    /// IMPORTANT — the <paramref name="psExternal"/> toggle is honoured ONLY on the
    /// dual-ADC family, where "Internal" selects the board's dedicated feedback ADC1.
    /// The single-ADC HermesC10 has NO internal feedback coupler: its one ADC is the
    /// raw LTC2208 (<c>temp_ADC = INA</c>, Hermes.v:1117-1130) with no internal mux,
    /// so PureSignal feedback is physically reachable ONLY through this external
    /// bypass relay (bit 11 "RX 1 Out", SPI.v:47). "Internal" is not a valid source
    /// on this board — it silently leaves the ADC on the antenna and the PS meter
    /// dead ("as if PS isn't on"). So on HermesC10 the relay is routed whenever PS is
    /// armed, regardless of the operator's Internal/External pick. Every OTHER board
    /// still requires <paramref name="psExternal"/> and stays byte-identical; the 10E
    /// (HermesII) is deliberately left off until a 10E owner can bench-confirm the
    /// same routing. The caller still ANDs the transmit-edge (xmit / MOX) condition.
    /// </summary>
    internal static bool RoutesExternalPsFeedbackBypass(
        HpsdrBoardKind board, bool psEnabled, bool psExternal)
    {
        if (!psEnabled) return false;
        // Single-ADC G2E: external tap is the ONLY feedback path — route it on any
        // PS-armed burst, independent of the (physically meaningless) Internal pick.
        if (board == HpsdrBoardKind.HermesC10) return G2ePsTimeMuxOnAir;
        // Dual-ADC family: honour the operator's Internal(ADC1)/External(bypass) pick.
        return psExternal && ComposesPsFeedbackWire(psEnabled, board);
    }

    /// <summary>
    /// Compose the alex0 word the way <see cref="SendCmdHighPriority"/>
    /// does, exposed internal so wire-format tests can assert the
    /// PureSignal-related bits without standing up a socket. Mirrors the
    /// in-line logic at SendCmdHighPriority &gt; alex0 calculation.
    /// </summary>
    internal static uint ComposeAlex0ForTest(
        uint rxFreqHz,
        bool moxOn,
        bool psEnabled,
        bool psExternal,
        HpsdrBoardKind board = HpsdrBoardKind.OrionMkII,
        int txAntWire = 1,
        bool hasTxAntennaRelays = false,
        int rxAuxInput = 0,
        bool mkiiBpfRxSelect = false,
        int rxAntWire = 1,
        RfFilterRuntimeSettings? rfFilters = null)
    {
        // Mirrors the SendCmdHighPriority alex0 path EXACTLY, in the same order,
        // so the golden test asserts real behaviour:
        //   1. base BPF/LPF bits + STATE-MULTIPLEXED ANT1/2/3 (RX ant on RX,
        //      TX ant on xmit / RX-on-aux) — pihpsdr new_protocol.c:1331-1357
        //   2. TX_RELAY during xmit
        //   3. operator RX-aux bits — composed FIRST
        //   4. PureSignal feedback routing — applied AFTER, owns K36 (firewall)
        // Default args (rx ant = tx ant = ANT1, no relays/aux) reproduce the
        // prior word, so the older golden tests stay byte-identical.
        int wire = hasTxAntennaRelays ? txAntWire : 1;
        int alex0AntWire = (moxOn || rxAuxInput != 0)
            ? wire
            : ((rxAntWire is 1 or 2 or 3) ? rxAntWire : 1);
        uint alexCommon = ComputeAlexWord(
            rxFreqHz,
            rxFreqHz,
            txAnt: wire,
            board: board,
            rfFilters: rfFilters,
            xmit: moxOn,
            psEnabled: psEnabled);
        uint alex0 = (alexCommon & ~ALEX_TX_ANTENNA_MASK) | EncodeTxAntennaBits(alex0AntWire)
                     | (moxOn ? ALEX_TX_RELAY : 0u);
        alex0 |= ComposeRxAuxBits(rxAuxInput, mkiiBpfRxSelect);
        // PS bit is the full-Orion dual-ADC wire ONLY — single-ADC boards
        // (HermesC10/G2E, HermesII/10E) never set ALEX_PS. Gate on the same
        // predicate the live path uses so this helper mirrors the wire exactly.
        if (ComposesPsFeedbackWire(psEnabled, board) && moxOn) alex0 |= AlexPsBit;
        if (RoutesExternalPsFeedbackBypass(board, psEnabled, psExternal) && moxOn)
            alex0 |= AlexRxAntennaBypass;
        return alex0;
    }

    internal static uint ComposeAlex1Word(
        uint rxFreqHz,
        uint rx2FreqHz,
        uint txLpfFreqHz,
        bool rx2Enabled,
        bool moxOn,
        bool psEnabled,
        HpsdrBoardKind board = HpsdrBoardKind.OrionMkII,
        int txAntWire = 1,
        RfFilterRuntimeSettings? rfFilters = null)
    {
        // The alex1 word carries TWO independent filter selections that must NOT
        // share a frequency when RX2 / the TX VFO sit on different bands:
        //   • BPF (ComputeAlexWord's 1st arg) = the RX2 RECEIVE preselector —
        //     follows RX2's band when RX2 is enabled, else RX1.
        //   • LPF (2nd arg) = the TX LOW-PASS filter. Per pihpsdr
        //     (new_protocol.c) the LPF is a TX-path filter carried on BOTH alex
        //     words during TX, so it MUST follow the TX frequency, exactly like
        //     alex0. <paramref name="txLpfFreqHz"/> is the TX-freq source of truth
        //     (== RX1 when not split, == VFO B for dual-RX split TX).
        // History: the original code computed BOTH from RX2's frequency, so with
        // RX2 on a different band the alex1 LPF rejected the TX carrier (no RF).
        // The first fix used RX1's freq for the LPF, which is correct ONLY while
        // the TX shares RX1's LO; for dual-RX split TX (where RX1 stays on VFO A
        // and the TX DUC moves to VFO B independently) the LPF must follow the TX
        // freq explicitly — hence the dedicated txLpfFreqHz argument.
        //
        // During RX the LPF is inert (TX relay open); the caller passes RX1's
        // freq then so the alex1 word stays byte-identical outside of transmit.
        uint rx2BpfFreqHz = rx2Enabled ? rx2FreqHz : rxFreqHz;
        uint lpfFreqHz = moxOn ? txLpfFreqHz : rx2BpfFreqHz;
        // alex1 ALWAYS reflects the TX antenna (external-ports plan — antenna
        // slice, #804) — it never carries the RX antenna, so the TX-antenna
        // selector is threaded straight through here.
        uint alex1 = ComputeAlexWord(
            rx2BpfFreqHz,
            lpfFreqHz,
            txAnt: txAntWire,
            board: board,
            rfFilters: rfFilters,
            xmit: moxOn,
            psEnabled: psEnabled);
        if (moxOn) alex1 |= ALEX_TX_RELAY | ALEX1_ANAN7000_RX_GNDonTX;
        if (psEnabled) alex1 |= AlexPsBit;
        return alex1;
    }

    /// <summary>
    /// Compose the alex1 word the way <see cref="SendCmdHighPriority"/> does,
    /// exposed internal so wire-format tests can assert the alex1 (offset 1428)
    /// PureSignal / TX-relay / RX-ground bits without standing up a socket.
    /// Single-RX, ANT1 default — the antenna-slice golden net pins these bits.
    /// </summary>
    internal static uint ComposeAlex1ForTest(
        uint rxFreqHz,
        bool moxOn,
        bool psEnabled,
        HpsdrBoardKind board = HpsdrBoardKind.OrionMkII,
        RfFilterRuntimeSettings? rfFilters = null)
    {
        uint alexCommon = ComputeAlexWord(
            rxFreqHz,
            rxFreqHz,
            txAnt: 1,
            board: board,
            rfFilters: rfFilters,
            xmit: moxOn,
            psEnabled: psEnabled);
        uint alex1 = alexCommon | (moxOn ? ALEX_TX_RELAY | ALEX1_ANAN7000_RX_GNDonTX : 0u);
        if (psEnabled) alex1 |= AlexPsBit;
        return alex1;
    }

    internal static uint ComputeAlexWord(
        uint rxFreqHz,
        uint txFreqHz,
        int txAnt,
        HpsdrBoardKind board = HpsdrBoardKind.OrionMkII,
        RfFilterRuntimeSettings? rfFilters = null,
        bool xmit = false,
        bool psEnabled = false)
    {
        uint word = 0;
        bool classicAlex = board is HpsdrBoardKind.Hermes or HpsdrBoardKind.HermesII;
        bool forceRxBypass = rfFilters is not null
            && (rfFilters.RxBypassAll
                || (xmit && rfFilters.RxBypassOnTx)
                || (xmit && psEnabled && rfFilters.RxBypassOnPureSignal));

        word |= classicAlex
            ? BpfBitsClassicAlex(rxFreqHz, rfFilters, forceRxBypass)
            : BpfBitsAnan7000(rxFreqHz, rfFilters, forceRxBypass);
        // LPF bit positions and band thresholds are identical across both
        // filter-board generations (classic Alex on Hermes/ANAN-100 vs
        // ANAN-7000/Saturn BPF board) — confirmed against pihpsdr alex.h
        // and new_protocol.c. Same call for every board.
        word |= LpfBits(txFreqHz, rfFilters);
        word |= EncodeTxAntennaBits(txAnt);
        return word;
    }

    /// <summary>
    /// Pure encoder for the Protocol-2 alex0/alex1 TX-antenna bits [26:24]
    /// (external-ports plan — antenna slice, #804). Single source of the
    /// ANT1/2/3 → bit math, shared by <see cref="ComputeAlexWord"/>,
    /// <see cref="SendCmdHighPriority"/>'s state-mux re-OR, and the
    /// external-port encoder seam. Out-of-range → ANT1.
    /// </summary>
    internal static uint EncodeTxAntennaBits(int txAnt) => txAnt switch
    {
        1 => ALEX_TX_ANTENNA_1,
        2 => ALEX_TX_ANTENNA_2,
        3 => ALEX_TX_ANTENNA_3,
        _ => ALEX_TX_ANTENNA_1,
    };

    /// <summary>
    /// Pure encoder for the Protocol-2 alex0 RX-auxiliary-input bits
    /// (external-ports plan — antenna slice, #804). Single source of the
    /// aux-relay math, shared by <see cref="SendCmdHighPriority"/> and the
    /// PS-K36 arbitration golden test.
    ///
    /// <paramref name="rxAux"/> selects the aux feed: 0=base ANT (no aux bits),
    /// 1=EXT1, 2=EXT2, 3=XVTR, 4=BYPASS(K36). On MkII-BPF (Saturn) boards the
    /// master RX-select bit (bit 14) is OR'd in alongside any non-base aux so the
    /// jacks are actually gated onto the RX (alex.h); classic-Alex boards leave
    /// bit 14 clear. Returns 0 for the base ANT path — byte-identical to today's
    /// alex0 when no aux is selected.
    ///
    /// NOTE on the K36 (BYPASS) bit: this helper returns
    /// <see cref="AlexRxAntennaBypass"/> for an operator BYPASS pick, but the
    /// caller applies PureSignal feedback routing AFTER this, so PS owns the relay
    /// while armed (the PS-K36 firewall). This helper never inspects PS state —
    /// the arbitration is purely "PS OR runs last".
    /// </summary>
    internal static uint ComposeRxAuxBits(int rxAux, bool mkiiBpfRxSelect)
    {
        uint bits = rxAux switch
        {
            1 => AlexRxAntennaExt1,
            2 => AlexRxAntennaExt2,
            3 => AlexRxAntennaXvtr,
            4 => AlexRxAntennaBypass,
            _ => 0u,
        };
        if (bits != 0 && mkiiBpfRxSelect) bits |= Alex0Anan7000RxSelect;
        return bits;
    }

    // RX BPF band splits lifted from pihpsdr new_protocol.c
    // (function new_protocol_high_priority, ADC0 BPFfreq selection).
    // ANAN-7000 / Orion-II / Saturn filter board only — see
    // BpfBitsClassicAlex for the Hermes / ANAN-100 layout.
    internal static uint BpfBitsAnan7000(uint freqHz)
    {
        if (freqHz <  1_500_000u) return ALEX_ANAN7000_RX_BYPASS_BPF;
        if (freqHz <  2_100_000u) return ALEX_ANAN7000_RX_160_BPF;
        if (freqHz <  5_500_000u) return ALEX_ANAN7000_RX_80_60_BPF;
        if (freqHz < 11_000_000u) return ALEX_ANAN7000_RX_40_30_BPF;
        if (freqHz < 22_000_000u) return ALEX_ANAN7000_RX_20_15_BPF;
        if (freqHz < 35_000_000u) return ALEX_ANAN7000_RX_12_10_BPF;
        return ALEX_ANAN7000_RX_6_PRE_BPF;
    }

    private static uint BpfBitsAnan7000(uint freqHz, RfFilterRuntimeSettings? rfFilters, bool forceBypass)
    {
        if (forceBypass) return ALEX_ANAN7000_RX_BYPASS_BPF;
        if (rfFilters?.CustomMatrixEnabled == true)
        {
            var resolved = ResolveCustomFilterBits(
                rfFilters.Anan7000RxFilters,
                freqHz,
                Anan7000RxKeyToBits,
                ALEX_ANAN7000_RX_BYPASS_BPF);
            if (resolved.HasValue) return resolved.Value;
        }
        return BpfBitsAnan7000(freqHz);
    }

    // RX HPF band splits for the classic Alex filter board (Hermes,
    // ANAN-10, ANAN-100, ANAN-100D, ANAN-200D, HermesII / ANAN-10E,
    // ANAN-100B). Bit positions and thresholds lifted from pihpsdr
    // alex.h and new_protocol.c:1154-1168. Note these are high-pass
    // filters (not band-pass like the ANAN-7000 BPF board) — same low
    // bits in the Alex0 word but different semantic per board.
    // Issue #413.
    internal static uint BpfBitsClassicAlex(uint freqHz)
    {
        if (freqHz <  1_800_000u) return ALEX_BYPASS_HPF;
        if (freqHz <  6_500_000u) return ALEX_1_5MHZ_HPF;
        if (freqHz <  9_500_000u) return ALEX_6_5MHZ_HPF;
        if (freqHz < 13_000_000u) return ALEX_9_5MHZ_HPF;
        if (freqHz < 20_000_000u) return ALEX_13MHZ_HPF;
        if (freqHz < 50_000_000u) return ALEX_20MHZ_HPF;
        return ALEX_6M_PREAMP;
    }

    private static uint BpfBitsClassicAlex(uint freqHz, RfFilterRuntimeSettings? rfFilters, bool forceBypass)
    {
        if (forceBypass) return ALEX_BYPASS_HPF;
        if (rfFilters?.CustomMatrixEnabled == true)
        {
            var resolved = ResolveCustomFilterBits(
                rfFilters.ClassicAlexRxFilters,
                freqHz,
                ClassicAlexRxKeyToBits,
                ALEX_BYPASS_HPF);
            if (resolved.HasValue) return resolved.Value;
        }
        return BpfBitsClassicAlex(freqHz);
    }

    // TX LPF band splits. Thresholds match pihpsdr new_protocol.c:1204-1218
    // exactly (strict > rather than >= so the band edges route the way the
    // LPF board expects; off-by-one on a threshold lets the wrong filter
    // pass harmonics at e.g. 24.9 MHz or 16.4 MHz). Bit positions and
    // thresholds are identical across classic Alex and ANAN-7000 BPF
    // boards — only the RX filter selection differs per board. Issue #413
    // renamed this from LpfBitsAnan7000 to LpfBits to reflect the shared
    // scope.
    internal static uint LpfBits(uint freqHz)
    {
        if (freqHz > 35_600_000u) return ALEX_6_BYPASS_LPF;
        if (freqHz > 24_000_000u) return ALEX_12_10_LPF;
        if (freqHz > 16_500_000u) return ALEX_17_15_LPF;
        if (freqHz >  8_000_000u) return ALEX_30_20_LPF;
        if (freqHz >  5_000_000u) return ALEX_60_40_LPF;
        if (freqHz >  2_500_000u) return ALEX_80_LPF;
        return ALEX_160_LPF;
    }

    private static uint LpfBits(uint freqHz, RfFilterRuntimeSettings? rfFilters)
    {
        if (rfFilters?.CustomMatrixEnabled == true)
        {
            var resolved = ResolveCustomFilterBits(
                rfFilters.TxFilters,
                freqHz,
                TxLpfKeyToBits,
                ALEX_6_BYPASS_LPF);
            if (resolved.HasValue) return resolved.Value;
        }
        return LpfBits(freqHz);
    }

    private static uint? ResolveCustomFilterBits(
        IReadOnlyList<RfFilterRangeDto>? ranges,
        uint freqHz,
        Func<string, uint?> keyToBits,
        uint bypassBits)
    {
        if (ranges is null) return null;
        foreach (var range in ranges)
        {
            if (range is null) continue;
            long start = Math.Max(0L, range.StartHz);
            long end = Math.Max(start, range.EndHz);
            if (freqHz < start || freqHz > end) continue;
            if (range.ForceBypass) return bypassBits;
            return keyToBits(range.Key);
        }
        return null;
    }

    private static uint? Anan7000RxKeyToBits(string key) => NormalizeFilterKey(key) switch
    {
        "bypass" => ALEX_ANAN7000_RX_BYPASS_BPF,
        "160" => ALEX_ANAN7000_RX_160_BPF,
        "80_60" => ALEX_ANAN7000_RX_80_60_BPF,
        "40_30" => ALEX_ANAN7000_RX_40_30_BPF,
        "20_15" => ALEX_ANAN7000_RX_20_15_BPF,
        "12_10" => ALEX_ANAN7000_RX_12_10_BPF,
        "6_pre" => ALEX_ANAN7000_RX_6_PRE_BPF,
        _ => null,
    };

    private static uint? ClassicAlexRxKeyToBits(string key) => NormalizeFilterKey(key) switch
    {
        "bypass" => ALEX_BYPASS_HPF,
        "1_5" => ALEX_1_5MHZ_HPF,
        "6_5" => ALEX_6_5MHZ_HPF,
        "9_5" => ALEX_9_5MHZ_HPF,
        "13" => ALEX_13MHZ_HPF,
        "20" => ALEX_20MHZ_HPF,
        "6_pre" => ALEX_6M_PREAMP,
        _ => null,
    };

    private static uint? TxLpfKeyToBits(string key) => NormalizeFilterKey(key) switch
    {
        "160" => ALEX_160_LPF,
        "80" => ALEX_80_LPF,
        "60_40" => ALEX_60_40_LPF,
        "30_20" => ALEX_30_20_LPF,
        "17_15" => ALEX_17_15_LPF,
        "12_10" => ALEX_12_10_LPF,
        "6_bypass" => ALEX_6_BYPASS_LPF,
        _ => null,
    };

    private static string NormalizeFilterKey(string? key)
        => (key ?? string.Empty).Trim().ToLowerInvariant().Replace('-', '_');

    // Mirrors pihpsdr's new_protocol_timer_thread:
    //   HighPriority every 100 ms, RX/TX specific every 200 ms, General
    //   every 800 ms. The G2 MkII expects this cadence once the hardware
    //   watchdog is enabled in CmdGeneral[38] — without it the radio
    //   treats the stream as abandoned and freezes IQ within ~1 s.
    private async Task KeepaliveLoop(CancellationToken ct)
    {
        int cycle = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
                cycle = (cycle % 8) + 1;
                SendCmdHighPriority(run: true);
                switch (cycle)
                {
                    case 2: case 4: case 6:
                        SendCmdRx();
                        break;
                    case 1: case 3: case 5: case 7:
                        SendCmdTx();
                        break;
                    case 8:
                        SendCmdRx();
                        SendCmdGeneral();
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "p2.keepalive exited with error");
        }
    }

    private void RxLoop(CancellationToken ct)
    {
        // Pro-audio promotion (#559): RX carries the PureSignal paired-DDC
        // feedback synchronously into FeedPsFeedbackBlock; at default priority
        // it contends with the Photino WebView render. Promote so feedback
        // timing stays tight under desktop load. Per-thread, dedicated thread
        // (StartAsync uses LongRunning).
        RealtimeThreadPriority.PromoteCallingThreadToProAudio(_log);
        var buf = new byte[2048];
        var sock = _sock!;
        sock.ReceiveTimeout = 500;

        // Per-source-port IQ packet tally, rolled into _rxPortRateSnapshot once
        // per ~1 s for diagnostics (SnapshotRxPortPacketRates). Shows which DDC
        // ports the radio is actually streaming — the ground truth for RX2/
        // multi-receiver health. Single-threaded loop, so plain locals here.
        var portPkts = new long[MaxRxDdc];
        long lastPortRollMs = Environment.TickCount64;

        // #1148 RX-IQ delivery telemetry. DDC0 datagram inter-arrival gap
        // distribution + per-window sequence-gap count, emitted at ~1 Hz
        // alongside the port-rate roll. The crux of the radio-speaker crackle
        // investigation: an OFF-vs-ON capture shows whether inbound IQ arrives
        // irregularly / under-rate (radio or physical clock coupling) versus
        // with host socket loss (NIC / scheduling). All locals — RxLoop is a
        // single dedicated thread. Allocation-light: one fixed gap ring + a
        // reusable sort scratch, plus running min/max/sum; the only per-second
        // work is a bounded Array.Sort in the emit branch.
        const int GapRingSize = 512; // power of two for the cheap index wrap
        var gapRingUs = new long[GapRingSize];
        var gapSortScratch = new long[GapRingSize];
        int gapRingPos = 0, gapRingFill = 0;
        long gapCount = 0, gapSumUs = 0, gapMinUs = long.MaxValue, gapMaxUs = 0;
        long lastDdc0ArrivalTicks = 0;
        bool haveDdc0Arrival = false;
        long lastDroppedSnapshot = Interlocked.Read(ref _droppedFrames);
        double usPerTick = 1_000_000.0 / Stopwatch.Frequency;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n;
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                try
                {
                    n = sock.ReceiveFrom(buf, ref from);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
                {
                    continue;
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
                {
                    // Windows WSAECONNRESET (10054): a prior SendTo provoked an ICMP
                    // port-unreachable and Windows surfaces it on the next recv.
                    // Linux silently discards this; without the catch here the IQ
                    // loop dies on the first stray ICMP → frozen panadapter.
                    continue;
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.Interrupted
                                              || ex.SocketErrorCode == SocketError.OperationAborted)
                {
                    break;
                }

                var srcPort = ((IPEndPoint)from).Port;
                if (srcPort >= RxDataPortBase && srcPort < RxDataPortBase + MaxRxDdc)
                {
                    int ddc = srcPort - RxDataPortBase;
                    portPkts[ddc]++;

                    // Measure inter-arrival on DDC0 only — the primary RX stream
                    // that feeds the DSP tick (and the radio-speaker sink). Its
                    // sequence-gap counter (_droppedFrames) is DDC0-specific too,
                    // so the two correlate in one log line.
                    if (ddc == 0)
                    {
                        long arr = Stopwatch.GetTimestamp();
                        if (haveDdc0Arrival)
                        {
                            long gapUs = (long)((arr - lastDdc0ArrivalTicks) * usPerTick);
                            gapCount++;
                            gapSumUs += gapUs;
                            if (gapUs < gapMinUs) gapMinUs = gapUs;
                            if (gapUs > gapMaxUs) gapMaxUs = gapUs;
                            gapRingUs[gapRingPos] = gapUs;
                            gapRingPos = (gapRingPos + 1) & (GapRingSize - 1);
                            if (gapRingFill < GapRingSize) gapRingFill++;
                        }
                        lastDdc0ArrivalTicks = arr;
                        haveDdc0Arrival = true;
                    }

                    long nowMs = Environment.TickCount64;
                    if (nowMs - lastPortRollMs >= 1000)
                    {
                        long ddc0Pkts = portPkts[0];
                        lock (_rxPortRateLock)
                        {
                            Array.Copy(portPkts, _rxPortRateSnapshot, MaxRxDdc);
                            _rxPortRateWindowMs = nowMs;
                        }
                        Array.Clear(portPkts);
                        lastPortRollMs = nowMs;

                        long droppedNow = Interlocked.Read(ref _droppedFrames);
                        long seqGaps = droppedNow - lastDroppedSnapshot;
                        lastDroppedSnapshot = droppedNow;

                        long meanUs = gapCount > 0 ? gapSumUs / gapCount : 0;
                        long minUs = gapCount > 0 ? gapMinUs : 0;
                        long p99Us = DiagStats.Percentile(gapRingUs, gapSortScratch, gapRingFill, 0.99);

                        // sockDrops=na: .NET's Socket exposes no portable
                        // per-socket dropped-datagram counter, so OS recv-buffer
                        // overflow is invisible from here. The discriminator the
                        // reporter needs: seqGaps WITHOUT a matching host drop ⇒
                        // the radio under-sent / streamed irregularly (radio or
                        // physical clock coupling); seqGaps WITH host drops ⇒
                        // NIC / scheduling. Reconcile seqGaps against the OS
                        // (`ss -u` / `netstat -su` RcvbufErrors) when an
                        // ON-vs-OFF radio-speaker capture diverges.
                        _log.LogInformation(
                            "p2.rxdiag ddc0pkts/s={Pps} seqGaps={SeqGaps} gapUs(min/mean/max/p99)={Min}/{Mean}/{Max}/{P99} sockDrops=na",
                            ddc0Pkts, seqGaps, minUs, meanUs, gapMaxUs, p99Us);

                        gapCount = 0; gapSumUs = 0; gapMinUs = long.MaxValue; gapMaxUs = 0;
                        gapRingPos = 0; gapRingFill = 0;
                    }
                }
                if (srcPort >= RxDataPortBase && srcPort < RxDataPortBase + MaxRxDdc && n == BufLen)
                {
                    int ddcIndex = srcPort - RxDataPortBase;
                    if (RoutesDdc0ToPsFeedback(_psFeedbackEnabled, ddcIndex, _boardKind,
                            txKeyed: _moxOn || _tuneActive,
                            timeMuxOnDdc0: TimeMuxesPsFeedbackOnDdc0(_boardKind)))
                    {
                        // PS-armed paired-DDC packet: 6B DDC0 (TX-mod-IQ) + 6B
                        // DDC1 (feedback) interleaved per sample. pihpsdr
                        // process_ps_iq_data, new_protocol.c:2463-2510. On the
                        // single-ADC G2E (time-mux) the same paired layout arrives
                        // on DDC0 ONLY during a TX burst; at rest this predicate is
                        // false and the packet stays user RX (and the burn-zone
                        // interlock keeps it dark in production entirely).
                        HandlePsPairedPacket(buf);
                    }
                    else
                    {
                        HandleDdcPacket(buf, ddcIndex);
                        // Keyed heartbeat: on the G2E, if PS is armed + keyed but the
                        // DDC0 packet did NOT route to the paired-feedback path (burst
                        // not forming, wrong rate, or reverted to plain RX), still tick
                        // the ~1 Hz diagnostic so the bench sees an explicit frames=0
                        // line instead of silence. Inert on every other board.
                        if (ddcIndex == 0 && ShouldLogG2ePsWireDiag(
                                _boardKind, _psFeedbackEnabled, _moxOn || _tuneActive))
                        {
                            MaybeEmitG2ePsDiag();
                        }
                    }
                }
                else if (srcPort == 1025 && n >= HiPriStatusMinBytes)
                {
                    // Hi-priority status (issue #174). Thetis decodes this at
                    // network.c:683-756 (case portIdx == 0). The fields we
                    // surface drive the operator's TX power meter — without
                    // this, the bar reads zero on every P2-connected radio
                    // because TxMetersService had no telemetry feed.
                    HandleHiPriStatusPacket(buf, n);
                }
                else if (srcPort == 1026)
                {
                    // Radio-mic stream (external-audio-jacks re-port): the
                    // radio's digitised analog jack (mic / line-in /
                    // balanced-XLR), 4-byte BE seq + 64 × int16 BE @ 48 kHz =
                    // 132 bytes (pihpsdr new_protocol.c process_mic_data).
                    // Dropped unless a radio audio source is armed AND a handler
                    // is attached — the volatile read is the only added cost
                    // under Host. The handler decodes + re-blocks synchronously
                    // on this thread.
                    var radioMic = _radioMicHandler;
                    if (radioMic is not null && n >= 132)
                        radioMic(new ReadOnlySpan<byte>(buf, 0, n));
                }
                // wideband ADC0..7 (1027..1034) intentionally ignored — a
                // separate feature.
            }
        }
        finally
        {
            _iqFrames.Writer.TryComplete();
        }
    }

    private void HandleDdcPacket(byte[] buf, int ddcIndex)
    {
        var seq = BinaryPrimitives.ReadUInt32BigEndian(buf);
        if (ddcIndex == 0)
        {
            if (_haveFirstDdc0 && seq != _lastDdc0Seq + 1)
            {
                Interlocked.Increment(ref _droppedFrames);
            }
            _haveFirstDdc0 = true;
            _lastDdc0Seq = seq;
        }

        // 238 complex samples: I (int24 BE) + Q (int24 BE), starting at byte 16.
        const int samplesPerPacket = DiscoverySamplesPerPacket;
        int sampleDoubles = samplesPerPacket * 2;
        // Pool the IQ buffer when a synchronous sink is attached (the normal DSP
        // path): OnIqFrame copies the samples inline (engine.FeedIq) and the
        // RxIqAvailable contract requires subscribers to copy, so the array is
        // dead the instant OnIqFrame returns and we recycle it in the finally
        // below. At 1536 kHz (~6.5k packets/s) this removes ~24 MB/s of Gen0
        // garbage whose GC pauses were a direct cause of worker stalls. The
        // channel fallback (no sink) hands the array to an async reader with no
        // return back-channel, so it keeps plain GC allocation.
        var sinkSnap = Volatile.Read(ref _rxSink);
        bool pooled = sinkSnap != null;
        double[] samples = pooled
            ? ArrayPool<double>.Shared.Rent(sampleDoubles)
            : new double[sampleDoubles];
        // 1/2^23 normalises int24 to [-1,+1]; the per-board correction is 1.0
        // for everything except Hermes@48 kHz (Brick2 quirk — see IqGainCorrection).
        double scale = (1.0 / 8388608.0) * IqGainCorrection(_boardKind, _sampleRateKhz);
        for (int i = 0; i < samplesPerPacket; i++)
        {
            int off = 16 + i * 6;
            int iRaw = (buf[off] << 16) | (buf[off + 1] << 8) | buf[off + 2];
            if ((iRaw & 0x800000) != 0) iRaw |= unchecked((int)0xFF000000);
            int qRaw = (buf[off + 3] << 16) | (buf[off + 4] << 8) | buf[off + 5];
            if ((qRaw & 0x800000) != 0) qRaw |= unchecked((int)0xFF000000);
            samples[i * 2] = iRaw * scale;
            samples[i * 2 + 1] = qRaw * scale;
        }

        // Map the incoming RX IQ stream to a logical receiver so the DSP can
        // route RX2 to its own channel without per-board DDC knowledge. Some
        // firmware uses source port 1035 + logical stream index (RX2 == 1);
        // other builds expose the DDC slot number (Orion/G2 RX2 == DDC3).
        int receiverIndex = ReceiverIndexForRxStream(
            ddcIndex,
            _boardKind,
            Volatile.Read(ref _rx2Enabled) != 0,
            Volatile.Read(ref _extraReceiverCount));

        var frame = new IqFrame(
            InterleavedSamples: new ReadOnlyMemory<double>(samples, 0, samplesPerPacket * 2),
            SampleCount: samplesPerPacket,
            SampleRateHz: _sampleRateKhz * 1000,
            Sequence: seq,
            TimestampNs: _stopwatch.ElapsedTicks * 1_000_000_000L / Stopwatch.Frequency,
            ReceiverIndex: receiverIndex);

        Interlocked.Increment(ref _totalFrames);
        // iter5: prefer the synchronous sink (snapshotted above) — bypasses the
        // Channel<T> hop that costs a TP wake-up per frame on the consumer.
        if (pooled)
        {
            try { sinkSnap!.OnIqFrame(in frame); }
            catch (Exception ex) { _log.LogError(ex, "p2.rx.sink_threw kind=iq"); }
            finally { ArrayPool<double>.Shared.Return(samples); }
        }
        else
        {
            _iqFrames.Writer.TryWrite(frame);
        }
    }

    // 1 Hz log throttle for hi-pri status. The first packet logs immediately
    // so a fresh connect produces a clear "we're seeing the radio's
    // telemetry" line; subsequent packets log at most once per second. Read
    // and written only on the RX thread, so plain ticks are fine.
    private long _lastHiPriLogTicks;

    /// <summary>
    /// Decode the Protocol-2 hi-priority status packet (UDP 1025). Field
    /// offsets mirror Thetis <c>network.c:689-716</c>:
    /// <list type="bullet">
    ///   <item>byte 0 — bit 0 PTT, bit 1 Dot, bit 2 Dash, bit 4 PLL locked, bit 7 sidetone</item>
    ///   <item>byte 1 — ADC0..7 overload bits</item>
    ///   <item>bytes 2..3 — exciter power ADC (BE u16)</item>
    ///   <item>bytes 10..11 — PA forward power ADC (BE u16)</item>
    ///   <item>bytes 18..19 — PA reverse power ADC (BE u16)</item>
    ///   <item>bytes 26..27 — hardware LEDs</item>
    ///   <item>bytes 35..38 — ADC0/ADC1 max magnitude</item>
    ///   <item>bytes 45..55 — supply volts, user ADCs, user digital input</item>
    /// </list>
    /// Pure function — exposed for unit tests against captured radio
    /// payloads. The power fields require the 0..19 range; newer diagnostic
    /// fields are decoded opportunistically when the payload reaches their
    /// offsets.
    /// </summary>
    public static P2TelemetryReading DecodeHiPriStatus(ReadOnlySpan<byte> buf)
    {
        ushort exciter = BinaryPrimitives.ReadUInt16BigEndian(buf.Slice(2, 2));
        ushort fwd = BinaryPrimitives.ReadUInt16BigEndian(buf.Slice(10, 2));
        ushort rev = BinaryPrimitives.ReadUInt16BigEndian(buf.Slice(18, 2));
        bool ptt = (buf[0] & 0x01) != 0;
        bool dot = (buf[0] & 0x02) != 0;
        bool dash = (buf[0] & 0x04) != 0;
        bool pll = (buf[0] & 0x10) != 0;
        bool sidetone = (buf[0] & 0x80) != 0;
        return new P2TelemetryReading(
            FwdAdc: fwd,
            RevAdc: rev,
            ExciterAdc: exciter,
            PttIn: ptt,
            PllLocked: pll,
            DotIn: dot,
            DashIn: dash,
            SidetoneActive: sidetone,
            AdcOverloadBits: buf.Length > 1 ? buf[1] : (byte)0,
            Adc0MaxMagnitude: ReadBeU16OrZero(buf, 35),
            Adc1MaxMagnitude: ReadBeU16OrZero(buf, 37),
            SupplyVoltsAdc: ReadBeU16OrZero(buf, 45),
            UserAdc0: ReadBeU16OrZero(buf, 53),
            UserAdc1: ReadBeU16OrZero(buf, 51),
            UserAdc2: ReadBeU16OrZero(buf, 49),
            UserAdc3: ReadBeU16OrZero(buf, 47),
            UserDigitalIn: buf.Length > 55 ? buf[55] : (byte)0,
            HardwareLeds: ReadBeU16OrZero(buf, 26));
    }

    private static ushort ReadBeU16OrZero(ReadOnlySpan<byte> buf, int offset) =>
        buf.Length >= offset + 2
            ? BinaryPrimitives.ReadUInt16BigEndian(buf.Slice(offset, 2))
            : (ushort)0;

    /// <summary>
    /// RX-thread handler for the hi-priority status packet. Decodes via
    /// <see cref="DecodeHiPriStatus"/>, throttle-logs at 1 Hz, and dispatches
    /// to <see cref="TelemetryReceived"/> subscribers.
    /// </summary>
    private void HandleHiPriStatusPacket(byte[] buf, int length)
    {
        // Skip the 4-byte BE u32 sequence number that prefixes every P2 UDP
        // packet (Thetis network.c:531 — `memcpy(bufp, readbuf + 4, 56)`).
        // Without this slice the decoder reads the sequence bytes for
        // exciter/fwd/rev — that's the bug behind issue #174's "exciter
        // climbs by 1, FWD/REV stuck at zero" log signature.
        var payload = buf.AsSpan(HiPriSeqHeaderBytes, length - HiPriSeqHeaderBytes);
        var reading = DecodeHiPriStatus(payload);

        Interlocked.Increment(ref _hiPriPackets);

        var rawHandler = HiPriorityStatusPayloadReceived;
        if (rawHandler is not null)
        {
            try { rawHandler(payload.ToArray()); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "p2.hi_pri.raw_handler threw");
            }
        }

        // Throttled log so an operator (or a rack-test session for #174) can
        // confirm the path is alive without spamming. Cadence matches P1's
        // `p1.tx.rate` line: one line / second while a stream is active.
        // Promoted to Information so `dotnet run` / journalctl renders it
        // without a debug-level config tweak — the operator's first
        // post-fix sanity check needs to be friction-free.
        long nowTicks = _stopwatch.ElapsedTicks;
        long elapsedMs = (nowTicks - _lastHiPriLogTicks) * 1000 / Stopwatch.Frequency;
        if (_lastHiPriLogTicks == 0 || elapsedMs >= 1000)
        {
            _lastHiPriLogTicks = nowTicks;
            _log.LogInformation(
                "p2.hi_pri.rx pkts={Pkts} fwd={Fwd} rev={Rev} exc={Exc} ptt={Ptt} pll={Pll} ov=0x{Ov:X2} supply={Supply}",
                Interlocked.Read(ref _hiPriPackets),
                reading.FwdAdc, reading.RevAdc, reading.ExciterAdc,
                reading.PttIn, reading.PllLocked,
                reading.AdcOverloadBits, reading.SupplyVoltsAdc);
        }

        // Subscriber list is captured once so a handler that unsubscribes
        // mid-invocation doesn't NRE. Same pattern P1 uses.
        var handler = TelemetryReceived;
        if (handler is not null)
        {
            try { handler(reading); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "p2.hi_pri.handler threw");
            }
        }
    }

    /// <summary>
    /// Test seam: run a hi-priority status body (the bytes that follow the
    /// 4-byte sequence header — i.e. payload[0] is the PTT/Dot/Dash/PLL byte)
    /// through the exact decode + dispatch path the UDP-1025 RX loop uses,
    /// raising <see cref="TelemetryReceived"/>. Lets higher layers
    /// integration-test the telemetry wiring (e.g. PTT-IN → MOX) against a real
    /// client instance without standing up a socket. Not a hot path.
    /// </summary>
    internal void RaiseHiPriStatusForTest(ReadOnlySpan<byte> bodyAfterSeqHeader)
    {
        var buf = new byte[HiPriSeqHeaderBytes + bodyAfterSeqHeader.Length];
        bodyAfterSeqHeader.CopyTo(buf.AsSpan(HiPriSeqHeaderBytes));
        HandleHiPriStatusPacket(buf, buf.Length);
    }

    // PS-armed packet shape on UDP 1035: 16-byte header (4 seq, 8 timestamp,
    // 4 reserved) followed by 119 sample pairs at 12 bytes each (6B DDC0 +
    // 6B DDC1). 16 + 119*12 = 1444 = BufLen. We accumulate into the 1024-
    // sample paired buffers and emit a PsFeedbackFrame per full block.
    //
    // Sample layout per pair (big-endian, signed 24-bit), per pihpsdr
    // new_protocol.c:1615-1616:
    //   off+0..2 : DDC0 I  (PS_RX_FEEDBACK — post-PA coupler — pscc's "rx")
    //   off+3..5 : DDC0 Q
    //   off+6..8 : DDC1 I  (PS_TX_FEEDBACK — TX-DAC loopback — pscc's "tx")
    //   off+9..11: DDC1 Q
    private void HandlePsPairedPacket(byte[] buf)
    {
        var seq = BinaryPrimitives.ReadUInt32BigEndian(buf);
        // Read samplesperframe from the packet header (pihpsdr
        // new_protocol.c:2475). G2 at 192 kHz emits 238 samples/frame
        // = 119 pairs/packet — the prior hardcoded literal happened to
        // match. Defensive bounds check + fallback to 119 on any garbage
        // value keeps the decoder working if the radio reports something
        // unexpected (older firmware, future variants).
        int samplesPerFrame = (buf[14] << 8) | buf[15];
        int samplesPerPacket = samplesPerFrame / 2;
        if (samplesPerPacket <= 0 || samplesPerPacket > 200)
        {
            _log.LogWarning(
                "p2.psPaired bad samplesPerFrame={N}, falling back to 119",
                samplesPerFrame);
            samplesPerPacket = 119;
        }

        // HermesC10 (ANAN-G2E) PS wire instrumentation gate — computed once per
        // packet (see field block near _psBlockStartSeq). When true we track the
        // peak coupler/reference magnitudes in the decode loop below and emit a
        // ~1 Hz diagnostic. Inert (false) for every other board — dual-ADC boards
        // also reach this method but are excluded by ShouldLogG2ePsWireDiag.
        bool g2eDiag = ShouldLogG2ePsWireDiag(
            _boardKind, _psFeedbackEnabled, _moxOn || _tuneActive);

        for (int i = 0; i < samplesPerPacket; i++)
        {
            int off = 16 + i * 12;
            // DecodePsPairForTest is the canonical mapping (DDC0=rx, DDC1=tx).
            // Reusing it here keeps the test-asserted contract identical to
            // the live decode path so the regression guard is real.
            var (sampleRxI, sampleRxQ, sampleTxI, sampleTxQ) =
                DecodePsPairForTest(new ReadOnlySpan<byte>(buf, off, 12));
            if (g2eDiag)
            {
                // Peak |coupler| (rx_I[0]) and |reference| (rx_I[NR]) as a cheap
                // max-of-|I|,|Q| — the level the next real-G2E bench needs to see.
                float couplerMag = MathF.Max(MathF.Abs(sampleRxI), MathF.Abs(sampleRxQ));
                float refMag = MathF.Max(MathF.Abs(sampleTxI), MathF.Abs(sampleTxQ));
                if (couplerMag > _g2ePsDiagPeakCoupler) _g2ePsDiagPeakCoupler = couplerMag;
                if (refMag > _g2ePsDiagPeakReference) _g2ePsDiagPeakReference = refMag;
            }
            _psRxI[_psBlockFill] = sampleRxI;
            _psRxQ[_psBlockFill] = sampleRxQ;
            _psTxI[_psBlockFill] = sampleTxI;
            _psTxQ[_psBlockFill] = sampleTxQ;

            if (_psBlockFill == 0) _psBlockStartSeq = seq;
            _psBlockFill++;

            if (_psBlockFill >= PsFeedbackBlockSize)
            {
                // Copy out — caller may reuse the buffers immediately.
                var txI = new float[PsFeedbackBlockSize];
                var txQ = new float[PsFeedbackBlockSize];
                var rxI = new float[PsFeedbackBlockSize];
                var rxQ = new float[PsFeedbackBlockSize];
                Array.Copy(_psTxI, txI, PsFeedbackBlockSize);
                Array.Copy(_psTxQ, txQ, PsFeedbackBlockSize);
                Array.Copy(_psRxI, rxI, PsFeedbackBlockSize);
                Array.Copy(_psRxQ, rxQ, PsFeedbackBlockSize);
                var psFrame = new PsFeedbackFrame(txI, txQ, rxI, rxQ, _psBlockStartSeq);
                // iter5: prefer the synchronous sink when attached.
                var psSinkSnap = Volatile.Read(ref _rxSink);
                if (psSinkSnap != null)
                {
                    try { psSinkSnap.OnPsFeedbackFrame(in psFrame); }
                    catch (Exception ex) { _log.LogError(ex, "p2.rx.sink_threw kind=psfb"); }
                }
                else
                {
                    _psFeedbackFrames.Writer.TryWrite(psFrame);
                }
                _psBlockFill = 0;
            }
        }

        if (g2eDiag)
        {
            _g2ePsDiagFramesInWindow++;
            MaybeEmitG2ePsDiag();
        }
    }

    /// <summary>
    /// Emit the ~1 Hz HermesC10 (ANAN-G2E) PS wire diagnostic when the window has
    /// elapsed, then reset the window. Called from two places, both on the RX
    /// thread (no synchronisation needed): once per PS-paired packet in
    /// <see cref="HandlePsPairedPacket"/> (with <c>frames</c> already incremented),
    /// AND as a keyed heartbeat from the RX dispatch loop on every DDC0 packet
    /// during a keyed PS burst. The heartbeat is load-bearing for the bench: without
    /// it the line only prints when feedback pairs actually arrive, so a totally
    /// dead feedback path emits NOTHING and the tester cannot tell "zero frames"
    /// (routing/burst broken) from "frames but flat level" (byte-59 / pad too high).
    /// With it the tester always gets a line, and <c>frames=0</c> vs <c>frames=N,
    /// peakFb≈0</c> is the one-line discriminator (see the #1249 bench notes).
    /// </summary>
    private void MaybeEmitG2ePsDiag()
    {
        long nowMs = Environment.TickCount64;
        if (_g2ePsDiagWindowStartMs == 0) _g2ePsDiagWindowStartMs = nowMs;
        if (nowMs - _g2ePsDiagWindowStartMs < 1000) return;

        // Mirrors the existing p1.tx.rate / p2.rxdiag 1 Hz cadence.
        //   arm     = byte 1363 = 0x02 (SyncRx[0][1]) is on the wire whenever this
        //             path runs;
        //   external= the operator's Internal/External feedback pick;
        //   bypass  = whether alex0 bit 11 (RX 1 Out) is actually routed on the wire
        //             for this keyed PS burst — the single-ADC G2E's ONLY tap path,
        //             now armed regardless of the Internal/External pick (#960);
        //   atten   = the byte-59 (Angelia_atten_Tx0) TX-time ADC attenuation the
        //             host is sending (stacks on the operator's external pad);
        //   frames  = DDC0 feedback packets on port 1035 in the window;
        //   peakFb / peakRef = de-interleaved coupler/reference magnitudes (0..1).
        // Read-only diagnostic.
        bool bypass = RoutesExternalPsFeedbackBypass(
                          _boardKind, _psFeedbackEnabled, _psFeedbackExternal)
                      && (_moxOn || _tuneActive);
        _log.LogInformation(
            "p2.ps.g2e arm={Arm} external={External} bypass={Bypass} atten={Atten} frames={Frames} peakFb={PeakFb:F4} peakRef={PeakRef:F4}",
            1, _psFeedbackExternal, bypass, _txStepAttnDb, _g2ePsDiagFramesInWindow,
            _g2ePsDiagPeakCoupler, _g2ePsDiagPeakReference);
        _g2ePsDiagWindowStartMs = nowMs;
        _g2ePsDiagFramesInWindow = 0;
        _g2ePsDiagPeakCoupler = 0f;
        _g2ePsDiagPeakReference = 0f;
    }

    /// <summary>
    /// Gate for the read-only HermesC10 (ANAN-G2E) PS wire instrumentation
    /// (<c>p2.ps.g2e</c>): emit ONLY on the ANAN-G2E, and only while PS feedback
    /// is armed AND transmit is asserted (the single-ADC time-mux burst is the
    /// only time DDC0 carries feedback). Deliberately excludes the sibling 10E
    /// (<see cref="HpsdrBoardKind.HermesII"/>) — the diagnostic is scoped to the
    /// G2E bench (#960, lb5va) — and every dual-ADC board (which also reaches the
    /// paired-packet decoder via <see cref="ReservesPsFeedbackDdcs"/>), so it is
    /// inert everywhere else. Pure predicate, no side effects — unit-testable
    /// without sockets.
    /// </summary>
    internal static bool ShouldLogG2ePsWireDiag(
        HpsdrBoardKind board, bool psFeedbackEnabled, bool txKeyed)
        => board == HpsdrBoardKind.HermesC10 && psFeedbackEnabled && txKeyed;

    private static int SignExtend24(int raw)
    {
        if ((raw & 0x800000) != 0) raw |= unchecked((int)0xFF000000);
        return raw;
    }

    /// <summary>
    /// Test seam — decode a single sample-pair from a PS paired packet and
    /// return the (rxI, rxQ, txI, txQ) destination assignments per the
    /// pihpsdr DDC0=RX_FEEDBACK / DDC1=TX_FEEDBACK contract. Used by tests
    /// to guard against re-introducing the round-1 swap bug.
    /// </summary>
    internal static (float rxI, float rxQ, float txI, float txQ)
        DecodePsPairForTest(ReadOnlySpan<byte> pair)
    {
        if (pair.Length < 12) throw new ArgumentException("pair must be 12 bytes", nameof(pair));
        const float scale = 1f / 8388608f;
        int d0i = SignExtend24((pair[0]  << 16) | (pair[1]  << 8) | pair[2]);
        int d0q = SignExtend24((pair[3]  << 16) | (pair[4]  << 8) | pair[5]);
        int d1i = SignExtend24((pair[6]  << 16) | (pair[7]  << 8) | pair[8]);
        int d1q = SignExtend24((pair[9]  << 16) | (pair[10] << 8) | pair[11]);
        // DDC0 -> rx, DDC1 -> tx.
        return (d0i * scale, d0q * scale, d1i * scale, d1q * scale);
    }

    public void Dispose()
    {
        try { StopAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { }
        _sock?.Dispose();
        _sock = null;
        _rxCts?.Dispose();
        _rxCts = null;
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
        _sock?.Dispose();
        _sock = null;
        _rxCts?.Dispose();
        _rxCts = null;
    }

    private static void WriteBeU16(byte[] buf, int offset, int value)
    {
        buf[offset] = (byte)((value >> 8) & 0xff);
        buf[offset + 1] = (byte)(value & 0xff);
    }

    private static void WriteBeU32(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)((value >> 24) & 0xff);
        buf[offset + 1] = (byte)((value >> 16) & 0xff);
        buf[offset + 2] = (byte)((value >> 8) & 0xff);
        buf[offset + 3] = (byte)(value & 0xff);
    }
}
