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

import { create } from 'zustand';
import { msSinceOptimisticTuneFor } from './view-center';
import {
  AGC_CONFIG_DEFAULT,
  NR_CONFIG_DEFAULT,
  SQUELCH_CONFIG_DEFAULT,
  TX_LEVELING_CONFIG_DEFAULT,
  type AgcConfigDto,
  type BandpassWindow,
  type ConnectionStatus,
  type NrConfigDto,
  type RadioStateDto,
  type Rx2AudioMode,
  type RxMode,
  type SquelchConfigDto,
  type TxVfo,
  type TxLevelingConfigDto,
  type ZoomLevel,
  type ReceiverDto,
} from '../api/client';

// WDSP wisdom bootstrap phase, mirroring the server's WisdomPhase enum.
// 'idle' = initializer hasn't started yet (first ms after boot),
// 'building' = WDSPwisdom is running (up to ~2 min on a fresh machine),
// 'ready' = FFTW plans are cached and /api/connect is accepting. The
// ConnectPanel disables + pulses Connect while !== 'ready'.
export type WisdomPhase = 'idle' | 'building' | 'ready';

export type ConnectionState = {
  status: ConnectionStatus;
  endpoint: string | null;
  vfoHz: number;
  vfoBHz: number;
  rx2Enabled: boolean;
  rx2AudioMode: Rx2AudioMode;
  rx2AfGainDb: number;
  txVfo: TxVfo;
  // Authoritative TX target as a receiver index (0=RX1, 1=RX2, >=2 extra DDC);
  // txVfo stays the legacy A/B projection. Driven by the VFO panel TX-select.
  txReceiverIndex: number;
  rxFocus: TxVfo;
  // Which exposed receiver the operator is working in the multi-DDC panels
  // (0=RX1..). Drives the VFO-lane / hero highlight across all DDCs; rxFocus
  // stays the A/B stitched-view focus and mirrors this for indices 0/1.
  focusedRxIndex: number;
  mode: RxMode;
  modeB: RxMode;
  filterLowHz: number;
  filterHighHz: number;
  filterLowHzB: number;
  filterHighHzB: number;
  filterPresetName: string | null;
  filterPresetNameB: string | null;
  filterAdvancedPaneOpen: boolean;
  txFilterLowHz: number;
  txFilterHighHz: number;
  // SSB bandpass "rectangularity" (#871). Independent RX/TX selectors.
  rxFilterWindow: BandpassWindow;
  txFilterWindow: BandpassWindow;
  sampleRate: number;
  agcTopDb: number;
  agc: AgcConfigDto;
  squelch: SquelchConfigDto;
  txLeveling: TxLevelingConfigDto;
  autoAgcEnabled: boolean;
  agcOffsetDb: number;
  rxAfGainDb: number;
  attenDb: number;
  autoAttEnabled: boolean;
  attOffsetDb: number;
  adcOverloadWarning: boolean;
  // Multi-DDC receivers (wire v2). Server-projected per-receiver list: index
  // 0 = RX1, 1 = RX2, >= 2 = extra hardware DDCs. Empty until the first state
  // frame. The RECEIVERS settings panel reads this to render per-DDC controls.
  receivers: ReceiverDto[];
  // DDC / receiver ceiling for this build (WireContract.MaxReceivers); the
  // RECEIVERS panel caps the exposed-count control against it.
  maxReceivers: number;
  // Board kind only known from the discovery list at connect time — StateDto
  // doesn't echo it. Null after a page reload while already connected; the
  // preamp guard treats null as "show", which is the safe default (an HL2
  // preamp toggle does nothing harmful, just nothing useful).
  boardId: string | null;
  // Connected protocol — 'P1' or 'P2', or null when disconnected. Set by
  // ConnectPanel on a successful /api/connect or /api/connect/p2 call so
  // protocol-gated features can disable their controls cleanly without
  // round-tripping the discovery list.
  connectedProtocol: 'P1' | 'P2' | null;
  preampOn: boolean;
  // CTUN (click-tune / centred tuning). When true, a panadapter click tunes
  // the dial off-centre with the hardware NCO frozen; the pan-tune gesture
  // reads this to skip the view-centre nudge so the dial marker roams instead
  // of recentring. Server-authoritative; toggled via the CTUN transport button.
  ctunEnabled: boolean;
  // Hardware NCO / panadapter centre. The frequency-axis ruler drag moves this
  // without touching vfoHz so the operator can pan to off-screen spectrum.
  radioLoHz: number;
  cwPitchHz: number;
  nr: NrConfigDto;
  // NR3 (RNNoise): native availability (libwdsp RNNR exports) + the operator-
  // installed model name (null = none). NR3 appears in the NR cycle only when
  // available AND a model is installed.
  wdspNr3RnnrAvailable: boolean;
  nr3ModelName: string | null;
  zoomLevel: ZoomLevel;
  inflight: boolean;
  // Endpoint of the most recently successful /api/connect. Survives a
  // disconnect so ConnectPanel can float it to the top of the next scan.
  // Intentionally in-memory only — no localStorage yet.
  lastConnectedEndpoint: string | null;
  wisdomPhase: WisdomPhase;
  // Live WDSP wisdom_get_status() text streamed by the server while
  // wisdomPhase === 'building'. Empty otherwise.
  wisdomStatus: string;
  /** Apply a server StateDto. `opts.trustVfo` (default true) marks the
   *  caller as an explicit command echo whose VFO values must always apply
   *  (drag release, keyboard flush, zoom/mode/band responses — clamps and
   *  server-side corrections included). The 1 Hz App.tsx poll passes
   *  trustVfo:false: a poll response generated just before the operator's
   *  latest tune would otherwise rewind the dial mid-gesture (issue #597
   *  rubber-band). Suppression is time-boxed to the optimistic-tune window
   *  so a quiet dial always reconverges to server truth. */
  applyState: (s: RadioStateDto, opts?: { trustVfo?: boolean }) => void;
  setInflight: (v: boolean) => void;
  setBoardId: (id: string | null) => void;
  setConnectedProtocol: (p: 'P1' | 'P2' | null) => void;
  setPreampOn: (on: boolean) => void;
  setNr: (nr: NrConfigDto) => void;
  setAgc: (agc: AgcConfigDto) => void;
  setSquelch: (squelch: SquelchConfigDto) => void;
  setTxLeveling: (txLeveling: TxLevelingConfigDto) => void;
  setRxFocus: (rxFocus: TxVfo) => void;
  setFocusedRxIndex: (index: number) => void;
  setZoomLevel: (level: ZoomLevel) => void;
  setLastConnectedEndpoint: (ep: string | null) => void;
  setWisdomPhase: (phase: WisdomPhase) => void;
  setWisdomStatus: (status: string) => void;
};

export const useConnectionStore = create<ConnectionState>((set) => ({
  status: 'Disconnected',
  endpoint: null,
  vfoHz: 14_200_000,
  vfoBHz: 14_200_000,
  rx2Enabled: false,
  rx2AudioMode: 'both',
  rx2AfGainDb: 0,
  receivers: [],
  maxReceivers: 8,
  txVfo: 'A',
  txReceiverIndex: 0,
  rxFocus: 'A',
  focusedRxIndex: 0,
  mode: 'USB',
  modeB: 'USB',
  filterLowHz: 150,
  filterHighHz: 2850,
  filterLowHzB: 150,
  filterHighHzB: 2850,
  filterPresetName: 'VAR1',
  filterPresetNameB: 'VAR1',
  filterAdvancedPaneOpen: false,
  txFilterLowHz: 150,
  txFilterHighHz: 2850,
  rxFilterWindow: 'Normal',
  txFilterWindow: 'Normal',
  sampleRate: 192_000,
  agcTopDb: 45,
  agc: { ...AGC_CONFIG_DEFAULT },
  squelch: { ...SQUELCH_CONFIG_DEFAULT },
  txLeveling: { ...TX_LEVELING_CONFIG_DEFAULT },
  autoAgcEnabled: false,
  agcOffsetDb: 0,
  rxAfGainDb: 0,
  attenDb: 0,
  autoAttEnabled: true,
  attOffsetDb: 0,
  adcOverloadWarning: false,
  boardId: null,
  connectedProtocol: null,
  preampOn: false,
  ctunEnabled: false,
  radioLoHz: 14_200_000,
  cwPitchHz: 600,
  nr: { ...NR_CONFIG_DEFAULT },
  wdspNr3RnnrAvailable: false,
  nr3ModelName: null,
  zoomLevel: 1,
  inflight: false,
  lastConnectedEndpoint: null,
  // Default to 'ready' so a page-load before the WS attach doesn't show the
  // pulse spuriously. The server overrides on attach with the real phase.
  wisdomPhase: 'ready',
  wisdomStatus: '',
  applyState: (s, opts) =>
    set((prev) => {
      const trustVfo = opts?.trustVfo ?? true;
      return {
        status: s.status,
        endpoint: s.endpoint,
        vfoHz:
          trustVfo || msSinceOptimisticTuneFor('A') >= 1500
            ? s.vfoHz
            : prev.vfoHz,
        vfoBHz:
          trustVfo || msSinceOptimisticTuneFor('B') >= 1500
            ? s.vfoBHz
            : prev.vfoBHz,
        rx2Enabled: s.rx2Enabled,
        rx2AudioMode: s.rx2AudioMode,
        rx2AfGainDb: s.rx2AfGainDb,
        receivers: s.receivers ?? prev.receivers,
        maxReceivers: s.maxReceivers ?? prev.maxReceivers,
        txVfo: s.txVfo,
        txReceiverIndex: s.txReceiverIndex ?? prev.txReceiverIndex,
        rxFocus: s.rx2Enabled ? prev.rxFocus : 'A',
        // UI-only focus — preserved across server state reconciles.
        focusedRxIndex: prev.focusedRxIndex,
        mode: s.mode,
        modeB: s.modeB,
        filterLowHz: s.filterLowHz,
        filterHighHz: s.filterHighHz,
        filterLowHzB: s.filterLowHzB,
        filterHighHzB: s.filterHighHzB,
        filterPresetName: s.filterPresetName,
        filterPresetNameB: s.filterPresetNameB,
        filterAdvancedPaneOpen: s.filterAdvancedPaneOpen,
        txFilterLowHz: s.txFilterLowHz,
        txFilterHighHz: s.txFilterHighHz,
        rxFilterWindow: s.rxFilterWindow,
        txFilterWindow: s.txFilterWindow,
        sampleRate: s.sampleRate,
        agcTopDb: s.agcTopDb,
        agc: s.agc,
        squelch: s.squelch,
        txLeveling: s.txLeveling,
        autoAgcEnabled: s.autoAgcEnabled,
        agcOffsetDb: s.agcOffsetDb,
        rxAfGainDb: s.rxAfGainDb,
        attenDb: s.attenDb,
        autoAttEnabled: s.autoAttEnabled,
        attOffsetDb: s.attOffsetDb,
        adcOverloadWarning: s.adcOverloadWarning,
        preampOn: s.preampOn,
        ctunEnabled: s.ctunEnabled,
        radioLoHz: s.radioLoHz,
        cwPitchHz: s.cwPitchHz,
        nr: s.nr,
        wdspNr3RnnrAvailable: s.wdspNr3RnnrAvailable,
        nr3ModelName: s.nr3ModelName,
        zoomLevel: s.zoomLevel,
      };
    }),
  setInflight: (inflight) => set({ inflight }),
  setBoardId: (boardId) => set({ boardId }),
  setConnectedProtocol: (connectedProtocol) => set({ connectedProtocol }),
  setPreampOn: (preampOn) => set({ preampOn }),
  setNr: (nr) => set({ nr }),
  setAgc: (agc) => set({ agc }),
  setSquelch: (squelch) => set({ squelch }),
  setTxLeveling: (txLeveling) => set({ txLeveling }),
  setRxFocus: (rxFocus) => set({ rxFocus }),
  // Focus a receiver in the multi-DDC panels. Mirror into rxFocus for the
  // RX1/RX2 stitched view so the existing A/B focus stays consistent.
  setFocusedRxIndex: (focusedRxIndex) =>
    set(
      focusedRxIndex <= 1
        ? { focusedRxIndex, rxFocus: focusedRxIndex === 1 ? 'B' : 'A' }
        : { focusedRxIndex },
    ),
  setZoomLevel: (zoomLevel) => set({ zoomLevel }),
  setLastConnectedEndpoint: (lastConnectedEndpoint) =>
    set({ lastConnectedEndpoint }),
  setWisdomPhase: (wisdomPhase) => set({ wisdomPhase }),
  setWisdomStatus: (wisdomStatus) => set({ wisdomStatus }),
}));
