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

import { decodeDisplayFrame, FrameDecodeError, MSG_TYPE_DISPLAY_FRAME } from './frame';
import { AudioFrameDecodeError, MSG_TYPE_AUDIO_PCM, decodeAudioFrame } from '../audio/frame';
import { getAudioClient } from '../audio/audio-client';
import { isNativeAudio } from '../audio/host-mode';
import { useMicPeakStore } from '../audio/mic-peak-store';
import { useConnectionStore, type WisdomPhase } from '../state/connection-store';
import { hasActiveFrameConsumers, useDisplayStore } from '../state/display-store';
import { useTxStore } from '../state/tx-store';
import { useBandPlanStore } from '../state/bandPlan';
import { useRxMetersStore } from '../state/rx-meters-store';
import { warnOnce } from '../util/logger';
import { wsUrl as buildWsUrl } from '../serverUrl';

const INITIAL_BACKOFF_MS = 1000;
const MAX_BACKOFF_MS = 8000;

// Binary WS frame type for TX meters v2: 1 type byte + 20 × f32 LE.
// Payload order (must match Zeus.Contracts/TxMetersFrame.cs v2):
//   fwdWatts, refWatts, swr,
//   micPk, micAv, eqPk, eqAv,
//   lvlrPk, lvlrAv, lvlrGr,
//   cfcPk, cfcAv, cfcGr,
//   compPk, compAv,
//   alcPk, alcAv, alcGr,
//   outPk, outAv.
// Bypassed WDSP stages emit ≤ −200 dBFS (near the WDSP −400 sentinel) and
// `*Gr` fields stay at 0 when the stage is idle.
export const MSG_TYPE_TX_METERS_V2 = 0x16;
const TX_METERS_V2_BYTES = 1 + 4 * 20;

// RX S-meter: 1 type byte + 1 × f32 LE (dBm). Broadcast at ~5 Hz from
// DspPipelineService; server clamps floor to −160 dBm before send.
export const MSG_TYPE_RX_METER = 0x14;
const RX_METER_BYTES = 1 + 4;

// RX meters v2 (RxMetersV2Frame, plan §1.3): 1 type byte + 7 × f32 LE = 29 B.
// Broadcast at 5 Hz from DspPipelineService; carries SignalPk/SignalAv,
// AdcPk/AdcAv, AgcGain (signed dB), AgcEnvPk/AgcEnvAv. Cal offset is
// applied server-side to the dBm fields; ADC + AgcGain are board-independent.
// 0x14 is kept on the wire in parallel for older clients and the simple
// SMeterLive view.
export const MSG_TYPE_RX_METERS_V2 = 0x19;
const RX_METERS_V2_BYTES = 1 + 4 * 7;

// Alert frame: 1 type byte + 1 kind byte + UTF-8 message (variable length).
// Server emits when SWR > 2.5 sustained ≥500 ms (PRD FR-6). Kind 0 = SWR trip.
export const MSG_TYPE_ALERT = 0x13;

// HL2 PA temperature: 1 type byte + 1 × f32 LE (°C). Broadcast at 2 Hz
// regardless of MOX state; server clamps to [-40, 125] °C before send.
// Operator-visible in the transport bar chip with 50/55 °C warning zones.
export const MSG_TYPE_PA_TEMP = 0x17;
const PA_TEMP_BYTES = 1 + 4;

// PureSignal stage telemetry. 1 type byte + 4 (feedback) + 4 (correctionDb)
// + 1 (calState) + 1 (correcting bool) + 4 (maxTxEnvelope) = 15 bytes.
// Broadcast at 10 Hz only while PsEnabled is armed — keeps the wire quiet
// when PS is off. Server-side bare-payload like TxMetersV2 (no header).
export const MSG_TYPE_PS_METERS = 0x18;
const PS_METERS_BYTES = 1 + 4 + 4 + 1 + 1 + 4;

// Band plan changed: 1 type byte + UTF-8 region ID. Server emits when region
// changes or plan is edited. Client refetches /api/bands/current. Originally
// 0x18 on the issue-65 branch; renumbered to 0x1B on merge with develop to
// resolve the collision with PsMeters above.
export const MSG_TYPE_BAND_PLAN_CHANGED = 0x1B;

// MOX/TUN state edge: 1 type byte + moxOn:u8 + tunOn:u8 = 3 bytes.
// Server broadcasts on every MOX or TUN transition from any source (UI,
// TCI, SWR trip, TX timeout) so the frontend stays in sync even when the
// edge originates outside the web UI.
export const MSG_TYPE_MOX_STATE = 0x1c;
const MOX_STATE_BYTES = 3;

// WDSP wisdom status: 1 type byte + 1 phase byte (0=idle, 1=building, 2=ready)
// + optional UTF-8 status text trailer (e.g. "Planning COMPLEX FORWARD FFT
// size 1024"). Pushed once on WS attach and again on every transition AND
// every per-step status change emitted by the server's wisdom_get_status()
// poll. Splash uses the status text to show the live build sub-step.
export const MSG_TYPE_WISDOM_STATUS = 0x15;
const WISDOM_STATUS_MIN_BYTES = 1 + 1;

// 0x1a — reserved. Previously VstHostEvent on the drifted plugin-host
// branch; left as a wire-gap so a stale frontend build can't accidentally
// decode whatever lands at this slot next.

// Mic uplink (client → server). Payload: 960 × f32le = 3840 bytes preceded by
// the 1-byte type, total 3841 bytes. 960 samples = 20 ms @ 48 kHz mono.
// Contract: PRD FR-2, server TxAudioIngest handler.
export const MSG_TYPE_MIC_PCM = 0x20;
const MIC_PCM_SAMPLES = 960;
const MIC_PCM_BYTES = 1 + MIC_PCM_SAMPLES * 4;

// Mic peak telemetry (server → client). Only emitted by NativeMicCapture in
// desktop host mode (the SPA's own AudioWorklet path is disabled there by
// Phase 2c). Payload: [0x1D][peakDbfs:f32 LE][tsUnixMs:i64 LE] = 13 bytes.
// Browser mode never receives this frame; if it ever does, we drop it
// defensively (the AnalyserNode path keeps driving the meter).
// Contract: Zeus.Contracts/MicPeakFrame.cs. Originally 0x1C on the
// audio-native branch; renumbered to 0x1D on merge with develop to
// resolve the collision with MoxState above.
export const MSG_TYPE_MIC_PEAK = 0x1d;
const MIC_PEAK_BYTES = 1 + 4 + 8;

// Audio Suite chain-order broadcast. Server pushes this whenever the
// operator reorders the chain via the tile strip (any client), or
// when a plugin is installed / uninstalled — so other connected
// clients update their tile sequence without polling. Payload:
// [0x1E][csvUtf8…] — comma-separated plugin IDs in chain order.
// Contract: Zeus.Contracts/AudioChainOrderFrame.cs.
export const MSG_TYPE_AUDIO_CHAIN_ORDER = 0x1e;

// Shared by startRealtime / sendMicPcm. Single WS instance at a time; writes
// are no-ops when the socket isn't open.
let activeWs: WebSocket | null = null;

function wsUrl(path: string): string {
  return buildWsUrl(path);
}

/**
 * Send a 960-sample mic PCM block to the server as a binary WS frame.
 * No-op when disconnected, so callers can blast blocks at 50 Hz without
 * needing to gate on connection state.
 */
export function sendMicPcm(samples: Float32Array): void {
  // Phase 2c — desktop mode captures mic natively in the host process
  // (Phase 2b). The hook in use-mic-uplink.ts already skips startMicUplink
  // entirely in this mode, but if any future caller emits 0x20 frames
  // directly we still must not put them on the wire (the server's native
  // capture would race the duplicate uplink).
  if (isNativeAudio()) return;
  const ws = activeWs;
  if (!ws || ws.readyState !== WebSocket.OPEN) return;
  if (samples.length !== MIC_PCM_SAMPLES) {
    warnOnce(
      'ws-mic-pcm-size',
      `mic block must be ${MIC_PCM_SAMPLES} samples; got ${samples.length}`,
    );
    return;
  }
  const buf = new ArrayBuffer(MIC_PCM_BYTES);
  const view = new DataView(buf);
  view.setUint8(0, MSG_TYPE_MIC_PCM);
  // Copy the block payload; DataView writes are host-endian-agnostic and
  // match the server's BitConverter.ToSingle on little-endian hosts. Use
  // setFloat32 per-sample with explicit LE for a portable wire format.
  for (let i = 0; i < MIC_PCM_SAMPLES; i++) {
    view.setFloat32(1 + i * 4, samples[i] ?? 0, true);
  }
  try {
    ws.send(buf);
  } catch (err) {
    warnOnce('ws-mic-send', 'mic send failed', err);
  }
}

export function startRealtime(path = '/ws'): () => void {
  let ws: WebSocket | null = null;
  let backoff = INITIAL_BACKOFF_MS;
  let timer: ReturnType<typeof setTimeout> | null = null;
  let stopped = false;

  const { pushFrame, setConnected } = useDisplayStore.getState();

  const connect = () => {
    if (stopped) return;
    try {
      ws = new WebSocket(wsUrl(path));
    } catch (err) {
      warnOnce('ws-construct-failed', 'WebSocket construction failed', err);
      schedule();
      return;
    }
    ws.binaryType = 'arraybuffer';
    activeWs = ws;

    ws.onopen = () => {
      backoff = INITIAL_BACKOFF_MS;
      setConnected(true);
    };
    ws.onclose = () => {
      setConnected(false);
      // PRD FR-6: if the WS drops while keyed, the UI must not keep showing TX.
      // Server-side, StreamingHub drops MOX on its end — this is the paired
      // client-side cleanup so the MOX button reverts to RX even if we can't
      // round-trip a POST (the HTTP path may be down too).
      const txOnClose = useTxStore.getState();
      if (txOnClose.moxOn) txOnClose.setMoxOn(false);
      if (txOnClose.localMicArmed) txOnClose.setLocalMicArmed(false);
      if (activeWs === ws) activeWs = null;
      ws = null;
      schedule();
    };
    ws.onerror = () => {
      /* onclose will fire next */
    };
    ws.onmessage = (ev) => {
      if (!(ev.data instanceof ArrayBuffer)) return;
      try {
        const peekType = new DataView(ev.data).getUint8(0);
        if (peekType === MSG_TYPE_DISPLAY_FRAME) {
          // Skip the decode + push when no spectrum surface is mounted.
          // decodeDisplayFrame allocates two Float32Arrays per tick and
          // pushFrame fans the new state through every store subscriber —
          // both are wasted when the panadapter, waterfall, and filter
          // mini-pan are all closed. Consumers register via
          // registerFrameConsumer() in display-store.ts; the moment any of
          // them mounts we resume decoding on the next tick.
          if (!hasActiveFrameConsumers()) return;
          const frame = decodeDisplayFrame(ev.data);
          pushFrame(frame);
          return;
        }
        if (peekType === MSG_TYPE_AUDIO_PCM) {
          // Phase 2c — desktop-mode opt-out. When the host process renders
          // RX audio natively, the server should not emit 0x02 frames at
          // all (Phase 2b). Drop here without decoding so an in-flight
          // frame at the moment of mode switch doesn't allocate a
          // Float32Array we'd immediately throw away.
          if (isNativeAudio()) return;
          const audio = decodeAudioFrame(ev.data);
          getAudioClient().push(audio);
          return;
        }
        if (peekType === MSG_TYPE_TX_METERS_V2) {
          if (ev.data.byteLength < TX_METERS_V2_BYTES) {
            warnOnce(
              'ws-tx-meters-v2-short',
              `tx meters v2 frame too short: ${ev.data.byteLength}`,
            );
            return;
          }
          const dv = new DataView(ev.data);
          useTxStore.getState().setMeters({
            fwdWatts: dv.getFloat32(1, true),
            refWatts: dv.getFloat32(5, true),
            swr: dv.getFloat32(9, true),
            micPk: dv.getFloat32(13, true),
            micAv: dv.getFloat32(17, true),
            eqPk: dv.getFloat32(21, true),
            eqAv: dv.getFloat32(25, true),
            lvlrPk: dv.getFloat32(29, true),
            lvlrAv: dv.getFloat32(33, true),
            lvlrGr: dv.getFloat32(37, true),
            cfcPk: dv.getFloat32(41, true),
            cfcAv: dv.getFloat32(45, true),
            cfcGr: dv.getFloat32(49, true),
            compPk: dv.getFloat32(53, true),
            compAv: dv.getFloat32(57, true),
            alcPk: dv.getFloat32(61, true),
            alcAv: dv.getFloat32(65, true),
            alcGr: dv.getFloat32(69, true),
            outPk: dv.getFloat32(73, true),
            outAv: dv.getFloat32(77, true),
          });
          return;
        }
        if (peekType === MSG_TYPE_MIC_PEAK) {
          // Phase 4 — desktop-mode mic level. In browser mode the server
          // does not emit this frame (NativeMicCapture isn't registered);
          // if a misconfigured server ever pushes it, the existing
          // AudioWorklet path is authoritative and we drop the wire frame.
          if (!isNativeAudio()) return;
          if (ev.data.byteLength < MIC_PEAK_BYTES) {
            warnOnce(
              'ws-mic-peak-short',
              `mic peak frame too short: ${ev.data.byteLength}`,
            );
            return;
          }
          const dv = new DataView(ev.data);
          const peakDbfs = dv.getFloat32(1, true);
          // i64 LE — DataView gives us BigInt; convert to number (safe up
          // to 2^53 ms ≈ year +287,000 AD, comfortably beyond any wall
          // clock we'll see).
          const tsUnixMs = Number(dv.getBigInt64(5, true));
          useMicPeakStore.getState().setPeak(peakDbfs, tsUnixMs);
          return;
        }
        if (peekType === MSG_TYPE_AUDIO_CHAIN_ORDER) {
          // Variable-length CSV payload — empty payload encodes the
          // "no plugins installed" empty-chain case.
          const bytes = new Uint8Array(ev.data, 1);
          const csv = new TextDecoder('utf-8').decode(bytes);
          const ids = csv.length === 0 ? [] : csv.split(',');
          // Lazy import keeps the audio-suite-store out of the ws-client
          // module graph for clients that never open the Audio Suite
          // window (e.g. the no-plugins-installed first-run experience).
          void import('../state/audio-suite-store').then((m) => {
            m.useAudioSuiteStore.getState().setChainOrderFromServer(ids);
          });
          return;
        }
        if (peekType === MSG_TYPE_PA_TEMP) {
          if (ev.data.byteLength < PA_TEMP_BYTES) {
            warnOnce(
              'ws-pa-temp-short',
              `PA temp frame too short: ${ev.data.byteLength}`,
            );
            return;
          }
          const tempC = new DataView(ev.data).getFloat32(1, true);
          useTxStore.getState().setPaTempC(tempC);
          return;
        }
        if (peekType === MSG_TYPE_PS_METERS) {
          if (ev.data.byteLength < PS_METERS_BYTES) {
            warnOnce(
              'ws-ps-meters-short',
              `PS meters frame too short: ${ev.data.byteLength}`,
            );
            return;
          }
          const dv = new DataView(ev.data);
          useTxStore.getState().setPsMeters({
            feedbackLevel: dv.getFloat32(1, true),
            correctionDb: dv.getFloat32(5, true),
            calState: dv.getUint8(9),
            correcting: dv.getUint8(10) !== 0,
            maxTxEnvelope: dv.getFloat32(11, true),
          });
          return;
        }
        if (peekType === MSG_TYPE_RX_METER) {
          if (ev.data.byteLength < RX_METER_BYTES) {
            warnOnce(
              'ws-rx-meter-short',
              `rx meter frame too short: ${ev.data.byteLength}`,
            );
            return;
          }
          const dbm = new DataView(ev.data).getFloat32(1, true);
          useTxStore.getState().setRxDbm(dbm);
          return;
        }
        if (peekType === MSG_TYPE_RX_METERS_V2) {
          if (ev.data.byteLength < RX_METERS_V2_BYTES) {
            warnOnce(
              'ws-rx-meters-v2-short',
              `rx meters v2 frame too short: ${ev.data.byteLength}`,
            );
            return;
          }
          const dv = new DataView(ev.data);
          useRxMetersStore.getState().setMeters({
            signalPk: dv.getFloat32(1, true),
            signalAv: dv.getFloat32(5, true),
            adcPk: dv.getFloat32(9, true),
            adcAv: dv.getFloat32(13, true),
            agcGain: dv.getFloat32(17, true),
            agcEnvPk: dv.getFloat32(21, true),
            agcEnvAv: dv.getFloat32(25, true),
          });
          return;
        }
        if (peekType === MSG_TYPE_WISDOM_STATUS) {
          if (ev.data.byteLength < WISDOM_STATUS_MIN_BYTES) {
            warnOnce(
              'ws-wisdom-short',
              `wisdom frame too short: ${ev.data.byteLength}`,
            );
            return;
          }
          const raw = new DataView(ev.data).getUint8(1);
          const phase: WisdomPhase =
            raw === 1 ? 'building' : raw === 2 ? 'ready' : 'idle';
          const status =
            ev.data.byteLength > WISDOM_STATUS_MIN_BYTES
              ? new TextDecoder('utf-8').decode(
                  new Uint8Array(ev.data, WISDOM_STATUS_MIN_BYTES),
                )
              : '';
          const store = useConnectionStore.getState();
          store.setWisdomPhase(phase);
          store.setWisdomStatus(status);
          return;
        }
        if (peekType === MSG_TYPE_ALERT) {
          if (ev.data.byteLength < 2) {
            warnOnce('ws-alert-short', `alert frame too short: ${ev.data.byteLength}`);
            return;
          }
          const dv = new DataView(ev.data);
          const kind = dv.getUint8(1);
          const msgBytes = new Uint8Array(ev.data, 2);
          const message = new TextDecoder('utf-8').decode(msgBytes);
          useTxStore.getState().setAlert({ kind, message });
          return;
        }
        if (peekType === MSG_TYPE_BAND_PLAN_CHANGED) {
          void useBandPlanStore.getState().refresh();
          return;
        }
        if (peekType === MSG_TYPE_MOX_STATE) {
          if (ev.data.byteLength < MOX_STATE_BYTES) {
            warnOnce('ws-mox-state-short', `mox state frame too short: ${ev.data.byteLength}`);
            return;
          }
          const dv = new DataView(ev.data);
          const moxOn = dv.getUint8(1) !== 0;
          const tunOn = dv.getUint8(2) !== 0;
          const txStore = useTxStore.getState();
          if (moxOn) {
            txStore.setMoxOn(true);
          } else if (tunOn) {
            txStore.setTunOn(true);
          } else {
            txStore.setMoxOn(false);
            txStore.setTunOn(false);
            // Server forced MOX off (SWR trip, TX timeout, TCI key-up) — also
            // disarm the local mic so the browser stops pushing samples even if
            // the operator never released their MoxButton / spacebar. Inverse
            // is intentional asymmetric: server-side MOX-on never raises
            // localMicArmed (only local interaction does — see issue #346).
            if (txStore.localMicArmed) txStore.setLocalMicArmed(false);
          }
          return;
        }
        warnOnce(
          `ws-msgtype-${peekType}`,
          `ignoring msgType 0x${peekType.toString(16)}`,
        );
      } catch (err) {
        if (err instanceof FrameDecodeError || err instanceof AudioFrameDecodeError) {
          warnOnce(`ws-decode-${err.message.slice(0, 32)}`, err.message);
        } else {
          warnOnce('ws-decode-unknown', 'frame decode failed', err);
        }
      }
    };
  };

  const schedule = () => {
    if (stopped) return;
    timer = setTimeout(connect, backoff);
    backoff = Math.min(backoff * 2, MAX_BACKOFF_MS);
  };

  connect();

  return () => {
    stopped = true;
    if (timer != null) clearTimeout(timer);
    if (ws) {
      ws.onopen = null;
      ws.onclose = null;
      ws.onerror = null;
      ws.onmessage = null;
      ws.close();
      if (activeWs === ws) activeWs = null;
      ws = null;
    }
    setConnected(false);
  };
}
