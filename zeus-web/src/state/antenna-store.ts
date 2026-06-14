// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Per-band TX/RX antenna relay selection (external-ports plan, Phase 2).
// Mirrors /api/radio/antenna, which is server-authoritative: the backend reads
// AntennaSettingsStore and pushes the active band's selection to the live radio
// through RecomputePaAndPush (the same path OC uses), so the frontend never
// clobbers the server on connect — it only loads + PUTs operator edits.
//
// The GET response carries the relay-capability gates so the panel can render
// the right selectors; the per-band rows let the operator stage selections for
// every band even when only the current band is shown.

import { create } from 'zustand';

export type AntennaName = 'Ant1' | 'Ant2' | 'Ant3';

export interface AntennaBand {
  band: string;
  txAnt: AntennaName;
  rxAnt: AntennaName;
}

export interface AntennaSettings {
  hasTxAntennaRelays: boolean;
  hasRxAntennaRelays: boolean;
  bands: AntennaBand[];
}

const DEFAULT_SETTINGS: AntennaSettings = {
  hasTxAntennaRelays: false,
  hasRxAntennaRelays: false,
  bands: [],
};

function parseAnt(v: unknown): AntennaName {
  return v === 'Ant2' || v === 'Ant3' ? v : 'Ant1';
}

function parse(raw: unknown): AntennaSettings {
  if (!raw || typeof raw !== 'object') return DEFAULT_SETTINGS;
  const r = raw as Record<string, unknown>;
  const bandsRaw = Array.isArray(r.bands) ? r.bands : [];
  return {
    hasTxAntennaRelays:
      typeof r.hasTxAntennaRelays === 'boolean' ? r.hasTxAntennaRelays : false,
    hasRxAntennaRelays:
      typeof r.hasRxAntennaRelays === 'boolean' ? r.hasRxAntennaRelays : false,
    bands: bandsRaw.map((b) => {
      const e = (b ?? {}) as Record<string, unknown>;
      return {
        band: typeof e.band === 'string' ? e.band : '',
        txAnt: parseAnt(e.txAnt),
        rxAnt: parseAnt(e.rxAnt),
      };
    }),
  };
}

export async function fetchAntennaSettings(
  signal?: AbortSignal,
): Promise<AntennaSettings> {
  const res = await fetch('/api/radio/antenna', { signal });
  if (!res.ok) throw new Error(`GET /api/radio/antenna → ${res.status}`);
  return parse(await res.json());
}

export async function updateAntennaBand(
  band: string,
  txAnt: AntennaName,
  rxAnt: AntennaName,
  signal?: AbortSignal,
): Promise<AntennaSettings> {
  const res = await fetch('/api/radio/antenna', {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ band, txAnt, rxAnt }),
    signal,
  });
  if (!res.ok) throw new Error(`PUT /api/radio/antenna → ${res.status}`);
  return parse(await res.json());
}

type AntennaStore = {
  settings: AntennaSettings;
  loaded: boolean;
  inflight: boolean;
  error: string | null;
  load: () => Promise<void>;
  setBand: (band: string, txAnt: AntennaName, rxAnt: AntennaName) => Promise<void>;
};

export const useAntennaStore = create<AntennaStore>((set, get) => ({
  settings: DEFAULT_SETTINGS,
  loaded: false,
  inflight: false,
  error: null,

  load: async () => {
    set({ inflight: true, error: null });
    try {
      const s = await fetchAntennaSettings();
      set({ settings: s, loaded: true, inflight: false });
    } catch (err) {
      set({
        error: err instanceof Error ? err.message : String(err),
        inflight: false,
      });
    }
  },

  setBand: async (band, txAnt, rxAnt) => {
    // Optimistic local update, rollback on error — same idiom as pa-store /
    // radio-options-store. The PUT returns the canonical settings which we
    // adopt so the local view stays in lockstep with the server.
    const prev = get().settings;
    const nextBands = (() => {
      const found = prev.bands.some((b) => b.band === band);
      if (found) {
        return prev.bands.map((b) =>
          b.band === band ? { ...b, txAnt, rxAnt } : b,
        );
      }
      return [...prev.bands, { band, txAnt, rxAnt }];
    })();
    set({ settings: { ...prev, bands: nextBands }, inflight: true, error: null });
    try {
      const s = await updateAntennaBand(band, txAnt, rxAnt);
      set({ settings: s, inflight: false });
    } catch (err) {
      set({
        settings: prev,
        error: err instanceof Error ? err.message : String(err),
        inflight: false,
      });
    }
  },
}));
