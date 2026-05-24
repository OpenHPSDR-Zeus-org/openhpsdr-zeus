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

import { create } from 'zustand';
import {
  NR_CONFIG_DEFAULT,
  type ConnectionStatus,
  type NrConfigDto,
  type RadioStateDto,
  type RxMode,
  type ZoomLevel,
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
  mode: RxMode;
  filterLowHz: number;
  filterHighHz: number;
  filterPresetName: string | null;
  filterAdvancedPaneOpen: boolean;
  txFilterLowHz: number;
  txFilterHighHz: number;
  sampleRate: number;
  agcTopDb: number;
  autoAgcEnabled: boolean;
  agcOffsetDb: number;
  rxAfGainDb: number;
  attenDb: number;
  autoAttEnabled: boolean;
  attOffsetDb: number;
  adcOverloadWarning: boolean;
  // Board kind only known from the discovery list at connect time — StateDto
  // doesn't echo it. Null after a page reload while already connected; the
  // preamp guard treats null as "show", which is the safe default (an HL2
  // preamp toggle does nothing harmful, just nothing useful).
  boardId: string | null;
  // Connected protocol — 'P1' or 'P2', or null when disconnected. Set by
  // ConnectPanel on a successful /api/connect or /api/connect/p2 call so
  // protocol-gated features (e.g. PureSignal v1 — P2 only) can disable
  // their controls cleanly without round-tripping the discovery list.
  // TODO(ps-p1): once Protocol1 PureSignal lands, this gate can drop the
  // PS-toggle disabled branch.
  connectedProtocol: 'P1' | 'P2' | null;
  preampOn: boolean;
  nr: NrConfigDto;
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
  applyState: (s: RadioStateDto) => void;
  setInflight: (v: boolean) => void;
  setBoardId: (id: string | null) => void;
  setConnectedProtocol: (p: 'P1' | 'P2' | null) => void;
  setPreampOn: (on: boolean) => void;
  setNr: (nr: NrConfigDto) => void;
  setZoomLevel: (level: ZoomLevel) => void;
  setLastConnectedEndpoint: (ep: string | null) => void;
  setWisdomPhase: (phase: WisdomPhase) => void;
  setWisdomStatus: (status: string) => void;
};

export const useConnectionStore = create<ConnectionState>((set) => ({
  status: 'Disconnected',
  endpoint: null,
  vfoHz: 14_200_000,
  mode: 'USB',
  filterLowHz: 150,
  filterHighHz: 2850,
  filterPresetName: 'VAR1',
  filterAdvancedPaneOpen: false,
  txFilterLowHz: 150,
  txFilterHighHz: 2850,
  sampleRate: 192_000,
  agcTopDb: 45,
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
  nr: { ...NR_CONFIG_DEFAULT },
  zoomLevel: 1,
  inflight: false,
  lastConnectedEndpoint: null,
  // Default to 'ready' so a page-load before the WS attach doesn't show the
  // pulse spuriously. The server overrides on attach with the real phase.
  wisdomPhase: 'ready',
  wisdomStatus: '',
  applyState: (s) =>
    set({
      status: s.status,
      endpoint: s.endpoint,
      vfoHz: s.vfoHz,
      mode: s.mode,
      filterLowHz: s.filterLowHz,
      filterHighHz: s.filterHighHz,
      filterPresetName: s.filterPresetName,
      filterAdvancedPaneOpen: s.filterAdvancedPaneOpen,
      txFilterLowHz: s.txFilterLowHz,
      txFilterHighHz: s.txFilterHighHz,
      sampleRate: s.sampleRate,
      agcTopDb: s.agcTopDb,
      autoAgcEnabled: s.autoAgcEnabled,
      agcOffsetDb: s.agcOffsetDb,
      rxAfGainDb: s.rxAfGainDb,
      attenDb: s.attenDb,
      autoAttEnabled: s.autoAttEnabled,
      attOffsetDb: s.attOffsetDb,
      adcOverloadWarning: s.adcOverloadWarning,
      nr: s.nr,
      zoomLevel: s.zoomLevel,
    }),
  setInflight: (inflight) => set({ inflight }),
  setBoardId: (boardId) => set({ boardId }),
  setConnectedProtocol: (connectedProtocol) => set({ connectedProtocol }),
  setPreampOn: (preampOn) => set({ preampOn }),
  setNr: (nr) => set({ nr }),
  setZoomLevel: (zoomLevel) => set({ zoomLevel }),
  setLastConnectedEndpoint: (lastConnectedEndpoint) =>
    set({ lastConnectedEndpoint }),
  setWisdomPhase: (wisdomPhase) => set({ wisdomPhase }),
  setWisdomStatus: (wisdomStatus) => set({ wisdomStatus }),
}));
