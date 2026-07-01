// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

namespace Zeus.VirtualRadio.P2;

/// <summary>
/// Protocol-2 wire constants the emulator shares with the client it mirrors.
///
/// Zeus's <c>Protocol2Client</c> declares these same numbers as <c>private
/// const</c> (BufLen, RxDataPortBase, HiPriSeqHeaderBytes, the 238/240 sample
/// geometries), so <c>InternalsVisibleTo</c> cannot reach them — they are
/// re-declared here with a precise source citation, and the anti-drift
/// guarantee is the round-trip tests: every packet the emulator emits is fed
/// back through the REAL client decode (<c>ReplyParser.TryParse</c>,
/// <c>Protocol2Client.DecodeHiPriStatus</c>,
/// <c>Protocol2Client.DecodePsPairForTest</c>) and every command the emulator
/// decodes is produced by the REAL composer
/// (<c>Protocol2Client.ComposeCmdRxBuffer</c> /
/// <c>ComposeCmdTxBuffer</c>). A wire-format change in the client therefore
/// reds a round-trip test rather than drifting silently.
/// </summary>
internal static class P2Wire
{
    // ---- UDP port map (Protocol2Client.cs:577-1591, SendCmdGeneral) -------
    // Host → radio destination ports.
    public const int CmdGeneralPort = 1024;       // discovery + general config
    public const int CmdRxPort = 1025;            // receive-specific (DDC enable, byte 1363 Mux)
    public const int CmdTxPort = 1026;            // transmit-specific (byte 59 TX attenuator)
    public const int CmdHighPriorityPort = 1027;  // run/MOX, NCO phases, drive byte 345
    public const int TxIqPort = 1029;             // TX-DUC IQ from the host

    // Radio → host SOURCE ports (the client demuxes inbound by source port —
    // Protocol2Client.RxLoop, the #1 thing to get wrong). The emulator MUST
    // send each stream from the matching socket.
    public const int HiPriStatusSrcPort = 1025;   // hi-priority status (FWD/REV/exciter/PTT)
    public const int RxDataPortBase = 1035;       // DDC0 RX-IQ + PS feedback (Protocol2Client.cs:122)

    // ---- Packet geometry --------------------------------------------------
    public const int BufLen = 1444;                   // Protocol2Client.cs:82
    public const int DiscoveryPacketLength = 60;      // RadioDiscoveryService.cs:58
    public const int CmdSmallLength = 60;             // CmdGeneral / CmdTx length

    // Plain RX DDC: 238 complex samples, int24 BE I+Q from byte 16
    // (Protocol2Client.HandleDdcPacket, DiscoverySamplesPerPacket).
    public const int RxSamplesPerPacket = 238;
    public const int RxSampleStride = 6;              // 3B I + 3B Q
    public const int RxPayloadOffset = 16;

    // PS paired feedback: 16-byte header, samplesPerFrame BE u16 at [14..15]
    // = 238 → 119 pairs of 12 bytes from byte 16 (Protocol2Client.HandlePsPairedPacket).
    public const int PsSamplesPerFrame = 238;
    public const int PsPairsPerPacket = 119;
    public const int PsPairStride = 12;               // 6B coupler(DDC0=rx) + 6B reference(DDC1=tx)
    public const int PsPayloadOffset = 16;
    public const int PsSamplesPerFrameOffset = 14;

    // TX-IQ ingest: BE u32 seq, then 240 complex int24 BE from byte 4
    // (Protocol2Client.FlushTxIqLocked).
    public const int TxIqSamplesPerPacket = 240;
    public const int TxIqPayloadOffset = 4;
    public const int TxIqSampleStride = 6;

    // ---- Hi-priority status (Protocol2Client.DecodeHiPriStatus) -----------
    // A 4-byte BE u32 sequence prefixes EVERY P2 UDP packet; the status body
    // starts at byte 4. The min length the client requires is 4 + 20 = 24.
    public const int HiPriSeqHeaderBytes = 4;         // Protocol2Client.cs:94
    public const int HiPriStatusMinBody = 20;
    public const int HiPriStatusPacketLength = 60;    // 4 seq + 56 body (Thetis memcpy 56)

    // ---- Field offsets read/written on the wire ---------------------------
    // CmdHighPriority (port 1027):
    public const int HpRunMoxByte = 4;                // bit0 run, bit1 (0x02) PTT/MOX
    public const byte HpRunBit = 0x01;
    public const byte HpMoxBit = 0x02;
    public const int HpDdc0PhaseOffset = 9;           // DDC0 RX NCO phase (Hermes-class RxBaseDdc=0)
    public const int HpTxDucPhaseOffset = 329;        // TX DUC NCO phase
    public const int HpDriveByte = 345;               // drive level 0..255
    public const int HpAlex0Offset = 1432;            // alex0 BE u32 (Protocol2Client.cs WriteBeU32(p,1432,alex0))
    public const uint AlexBypassBit = 0x00000800;     // alex0 bit 11 = RX 1 Out / external PS tap relay (SPI.v:47)

    // CmdRx (port 1025):
    public const int RxNumAdcByte = 4;
    public const int RxDdcEnableByte = 7;
    // Byte 1363 bit 1 (0x02) arms the single-ADC PS time-mux. It is a DIFFERENT
    // register on each single-ADC firmware but the same offset+bit: SyncRx[0] on
    // the ANAN-G2E C10 gateware (Rx_specific_C&C.v:181, Hermes.v:720) and the Mux
    // register on the ANAN-10E Hermes_v10.3 (Rx_specific_C&C.v:181). Not byte 1443.
    public const int RxMuxByte = 1363;
    public const byte RxMuxPsBit = 0x02;              // SyncRx[0][1] (C10) / Mux[1] (10E) — Rx PS time-mux select
    public const int RxDdc0SampleRateOffset = 18;     // BE u16 kHz in the burst descriptor

    // CmdTx (port 1026):
    public const int TxMicControlByte = 50;
    public const int TxLineInGainByte = 51;
    public const int TxStepAttnByte = 59;             // Angelia_atten_Tx0 (byte-59 safety seed)

    // CmdGeneral / discovery (port 1024): byte 4 discriminates.
    public const int GeneralCmdByte = 4;
    public const byte GeneralConnect = 0x00;          // CmdGeneral
    public const byte DiscoveryProbe = 0x02;          // discovery request (RadioDiscoveryService)

    // Hz → 32-bit phase increment (Protocol2Client.HzToPhase). Used only to
    // decode the host's commanded RX/TX frequency back out for observability.
    public const double HzToPhase = 34.952533333333333;

    public const double Int24FullScale = 8_388_608.0; // 2^23
    public const int Int24Max = 8_388_607;
    public const int Int24Min = -8_388_608;
}
