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
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.

import { create } from 'zustand';
import {
  fetchPaSettings,
  fetchPaDefaults,
  updatePaSettings,
  type PaBandSettings,
  type PaGlobalSettings,
  type PaSettings,
} from '../api/pa';

// Canonical HF band order — must match Zeus.Server.Hosting/BandUtils.HfBands. The
// settings panel iterates this for a consistent row order even when the
// backend returns bands in a different order or omits some.
export const HF_BANDS: readonly string[] = [
  '160m', '80m', '60m', '40m', '30m', '20m', '17m', '15m', '12m', '10m', '6m',
] as const;

function defaultBand(band: string): PaBandSettings {
  return { band, paGainDb: 0, disablePa: false, ocTx: 0, ocRx: 0, autoOcMask: 0, ocDxTx: 0, ocDxRx: 0 };
}

function defaultState(): PaSettings {
  return {
    global: { paEnabled: true, paMaxPowerWatts: 0 },
    bands: HF_BANDS.map(defaultBand),
  };
}

// Canonicalize the array coming back from the backend: fill in missing bands
// with defaults so the UI doesn't have to guard for holes.
function canonicalize(s: PaSettings): PaSettings {
  const byBand = new Map<string, PaBandSettings>(s.bands.map((b) => [b.band, b]));
  return {
    global: s.global,
    bands: HF_BANDS.map((b) => byBand.get(b) ?? defaultBand(b)),
  };
}

type PaStore = {
  settings: PaSettings;
  loaded: boolean;
  inflight: boolean;
  error: string | null;
  // boardOverride lets the radio-selector preview another board's defaults
  // for empty rows without persisting the preference. Undefined = use the
  // server's effective board (connected > preferred).
  load: (boardOverride?: string) => Promise<void>;
  save: () => Promise<void>;
  setGlobal: (patch: Partial<PaGlobalSettings>) => void;
  setBand: (band: string, patch: Partial<Omit<PaBandSettings, 'band'>>) => void;
  // Overwrite per-band PaGainDb + global PaMaxPowerWatts with the requested
  // board's pure defaults. Does NOT persist — the operator still has to
  // press APPLY. OC masks / Disable-PA / PaEnabled are preserved because
  // those are wiring preferences, not per-board data.
  resetToBoardDefaults: (boardOverride?: string) => Promise<void>;
  // Per-tab "Copy from OC RX/TX" action in the PA Settings panel (issue
  // #407 design v2). Mirrors the source side's OC masks — both the
  // standard 1..7 and the Anvelina ext 8..11 (USEROUT7..10) — onto the
  // destination side for every band in one shot. Stops short of APPLY;
  // the operator still has to persist via the modal footer.
  copyOcMasks: (direction: 'tx->rx' | 'rx->tx') => void;
};

export const usePaStore = create<PaStore>((set, get) => ({
  settings: defaultState(),
  loaded: false,
  inflight: false,
  error: null,

  load: async (boardOverride) => {
    set({ inflight: true, error: null });
    try {
      const s = await fetchPaSettings(undefined, boardOverride);
      set({ settings: canonicalize(s), loaded: true, inflight: false });
    } catch (err) {
      set({
        error: err instanceof Error ? err.message : String(err),
        inflight: false,
      });
    }
  },

  save: async () => {
    set({ inflight: true, error: null });
    try {
      const s = await updatePaSettings(get().settings);
      set({ settings: canonicalize(s), inflight: false });
    } catch (err) {
      set({
        error: err instanceof Error ? err.message : String(err),
        inflight: false,
      });
    }
  },

  setGlobal: (patch) =>
    set((s) => ({ settings: { ...s.settings, global: { ...s.settings.global, ...patch } } })),

  setBand: (band, patch) =>
    set((s) => ({
      settings: {
        ...s.settings,
        bands: s.settings.bands.map((b) => (b.band === band ? { ...b, ...patch } : b)),
      },
    })),

  copyOcMasks: (direction) =>
    set((s) => ({
      settings: {
        ...s.settings,
        bands: s.settings.bands.map((b) =>
          direction === 'tx->rx'
            ? { ...b, ocRx: b.ocTx, ocDxRx: b.ocDxTx }
            : { ...b, ocTx: b.ocRx, ocDxTx: b.ocDxRx },
        ),
      },
    })),

  resetToBoardDefaults: async (boardOverride) => {
    set({ inflight: true, error: null });
    try {
      const defaults = await fetchPaDefaults(boardOverride);
      const byBand = new Map(defaults.bands.map((b) => [b.band, b]));
      set((s) => ({
        settings: {
          global: {
            ...s.settings.global,
            paMaxPowerWatts: defaults.global.paMaxPowerWatts,
          },
          bands: s.settings.bands.map((b) => ({
            ...b,
            paGainDb: byBand.get(b.band)?.paGainDb ?? b.paGainDb,
          })),
        },
        inflight: false,
      }));
    } catch (err) {
      set({
        error: err instanceof Error ? err.message : String(err),
        inflight: false,
      });
    }
  },
}));
