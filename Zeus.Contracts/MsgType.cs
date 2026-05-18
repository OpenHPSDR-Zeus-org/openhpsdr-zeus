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

namespace Zeus.Contracts;

public enum MsgType : byte
{
    // Server → client (RX display + audio)
    DisplayFrame = 0x01,
    AudioPcm = 0x02,
    Status = 0x03,

    // Client → server (TX uplink). f32le mono samples at 48 kHz, framed into
    // 960-sample (20 ms) blocks. 0x20 chosen to live in a "2x = uplink" nibble
    // so future client→server types (PTT heartbeats, etc.) cluster together
    // and stay visually distinct from the 0x1x server→client telemetry.
    MicPcm = 0x20,

    // Server → client (TX telemetry + protection)
    TxMeters = 0x11,
    TxStatus = 0x12,
    Alert = 0x13,

    // Server → client (RX signal strength, dBm)
    RxMeter = 0x14,

    // Server → client (DSP bootstrap state). Broadcast when the WDSPwisdom
    // FFTW plan cache transitions between idle/building/ready; also pushed
    // once per client at WS attach so late joiners get the current state.
    WisdomStatus = 0x15,

    // Server → client (TX telemetry v2). Compatible additive extension of
    // TxMeters (0x11): carries average readings alongside peak for every
    // stage, plus CFC/COMP stages that v1 omitted. Operators need the
    // average to judge level and the peak to catch clipping; v1's peak-only
    // payload hid transient overshoots inside the smoothing window. v1 is
    // left in the enum for decoder interop / historical clients but the
    // server only broadcasts v2 after the feat/tx-audio-meters branch.
    TxMetersV2 = 0x16,

    // Server → client (HL2 PA temperature in °C, MCP9700 sensor). Separate
    // from the TX meter frame because temperature is a protection signal
    // the operator wants to see during RX-only operation too — the HL2
    // gateware auto-disables TX at 55 °C (Q6 sensor) — and it moves on a
    // seconds timescale, so bolting it onto the 10 Hz TX meter cadence
    // would be overkill. Broadcast at 2 Hz always.
    PaTemp = 0x17,

    // PureSignal stage telemetry. Broadcast at 10 Hz only while PsEnabled is
    // armed — keeps the wire quiet during normal operation. Carries WDSP
    // GetPSInfo readouts (info[4] = feedback level, info[14] = correcting
    // bit, info[15] = cal-state enum) plus a derived correction-depth dB
    // and the GetPSMaxTX envelope peak. Bare-payload like TxMetersV2 (no
    // 16-byte header) — same 10 Hz rate logic.
    PsMeters = 0x18,

    // Server → client (RX telemetry v2). Compatible additive extension of
    // RxMeter (0x14): carries the full set of RXA stage meters — signal
    // peak/avg (calibrated dBm), ADC peak/avg (dBFS), AGC gain (signed dB,
    // positive = boosting), and AGC envelope peak/avg (calibrated dBm).
    // Bare-payload like TxMetersV2 (no 16-byte header), broadcast at the
    // same 5 Hz cadence as RxMeter. The legacy 5-byte 0x14 frame stays in
    // flight for older clients (e.g. SMeterLive) — 0x19 is purely additive.
    RxMetersV2 = 0x19,

    // 0x1A — reserved (previously VstHostEvent on the drifted plugin-host
    // branch). Left as a gap rather than reassigned to avoid colliding with
    // any zeus-web build that hasn't been refreshed yet.

    // Server → client (band plan changed). Broadcast when the active region
    // changes or the operator edits the plan. Payload: [type:1][regionIdUtf8…].
    // Frontend refetches GET /api/bands/current on receipt.
    // Originally 0x18 on the issue-65 branch; renumbered to 0x1B on merge
    // with develop to resolve the collision with PsMeters above.
    BandPlanChanged = 0x1B,

    // Server → client (MOX/TUN state edge). Broadcast on every MOX or TUN
    // transition regardless of source (UI click, TCI trx command, SWR trip,
    // TX timeout). Payload: [type:1][moxOn:u8][tunOn:u8] — 3 bytes total.
    // Allows the frontend to track transmit state even when the source of
    // the edge is not the web UI (e.g. TCI client sends trx:0,true;).
    MoxState = 0x1C,

    // Server → client (mic peak level). Broadcast at ~10 Hz by
    // NativeMicCapture only in desktop host mode — the SPA's getUserMedia
    // analyser is intentionally disabled there (Phase 2c) so the MicMeter
    // would otherwise be flat. Server mode never emits this frame; remote
    // browser operators continue to drive their MicMeter via getUserMedia.
    // Payload: [type:1][peakDbfs:f32 LE][tsUnixMs:i64 LE] = 13 bytes total.
    // See MicPeakFrame.cs. Originally 0x1C on the audio-native branch;
    // renumbered to 0x1D on merge with develop to resolve the collision
    // with MoxState above.
    MicPeak = 0x1D,

    // Server → client (audio plugin chain order). Broadcast whenever a
    // user reorders the chain via the Audio Suite window's tile strip,
    // OR when a plugin is installed / uninstalled (so other connected
    // clients refresh their tile order without polling). Payload:
    // [type:1][csvUtf8…] — comma-separated plugin IDs in chain order
    // (head = first in chain, drives mic first). UTF-8 for forward
    // compatibility with non-ASCII plugin IDs even though current IDs
    // are reverse-DNS ASCII. See AudioChainOrderFrame.cs.
    AudioChainOrder = 0x1E,
}
