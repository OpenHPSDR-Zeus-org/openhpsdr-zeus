// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// HL2 optional toggles (currently: Band Volts PWM output). Mirrors
// /api/radio/hl2-options, which always responds 200 regardless of the
// connected board kind — non-HL2 radios simply return `bandVolts: false`
// and ignore writes. The gating for whether the panel is visible at all
// lives one layer up in `BoardCapabilities.hasHl2OptionalToggles`.
//
// Pattern mirrors pa-store: optimistic local update on
// toggle with a rollback on server error; an inflight flag for the panel
// to show a "saving…" indicator while the PUT is in flight.

import { create } from 'zustand';

export interface Hl2Options {
  bandVolts: boolean;
}

export interface G2Options {
  ditherEnabled: boolean;
  randomEnabled: boolean;
  maxRxFreqMHz: number;
  supported: boolean;
  rx1AttenuatorDb: number;
  rx1AttenuatorMinDb: number;
  rx1AttenuatorMaxDb: number;
  rx1AttenuatorSupported: boolean;
}

const DEFAULT_OPTIONS: Hl2Options = { bandVolts: false };
const DEFAULT_G2_OPTIONS: G2Options = {
  ditherEnabled: true,
  randomEnabled: true,
  maxRxFreqMHz: 60,
  supported: false,
  rx1AttenuatorDb: 0,
  rx1AttenuatorMinDb: 0,
  rx1AttenuatorMaxDb: 31,
  rx1AttenuatorSupported: false,
};

function parseHl2(raw: unknown): Hl2Options {
  if (!raw || typeof raw !== 'object') return DEFAULT_OPTIONS;
  const r = raw as Record<string, unknown>;
  return {
    bandVolts: typeof r.bandVolts === 'boolean' ? r.bandVolts : false,
  };
}

function parseG2(raw: unknown): G2Options {
  if (!raw || typeof raw !== 'object') return DEFAULT_G2_OPTIONS;
  const r = raw as Record<string, unknown>;
  const min = typeof r.rx1AttenuatorMinDb === 'number' && Number.isFinite(r.rx1AttenuatorMinDb)
    ? Math.max(0, Math.min(31, Math.round(r.rx1AttenuatorMinDb)))
    : DEFAULT_G2_OPTIONS.rx1AttenuatorMinDb;
  const max = typeof r.rx1AttenuatorMaxDb === 'number' && Number.isFinite(r.rx1AttenuatorMaxDb)
    ? Math.max(min, Math.min(31, Math.round(r.rx1AttenuatorMaxDb)))
    : DEFAULT_G2_OPTIONS.rx1AttenuatorMaxDb;
  const rx1AttenuatorDb = typeof r.rx1AttenuatorDb === 'number' && Number.isFinite(r.rx1AttenuatorDb)
    ? Math.max(min, Math.min(max, Math.round(r.rx1AttenuatorDb)))
    : DEFAULT_G2_OPTIONS.rx1AttenuatorDb;
  return {
    ditherEnabled:
      typeof r.ditherEnabled === 'boolean'
        ? r.ditherEnabled
        : DEFAULT_G2_OPTIONS.ditherEnabled,
    randomEnabled:
      typeof r.randomEnabled === 'boolean'
        ? r.randomEnabled
        : DEFAULT_G2_OPTIONS.randomEnabled,
    maxRxFreqMHz:
      typeof r.maxRxFreqMHz === 'number' && Number.isFinite(r.maxRxFreqMHz)
        ? r.maxRxFreqMHz
        : DEFAULT_G2_OPTIONS.maxRxFreqMHz,
    supported:
      typeof r.supported === 'boolean'
        ? r.supported
        : DEFAULT_G2_OPTIONS.supported,
    rx1AttenuatorDb,
    rx1AttenuatorMinDb: min,
    rx1AttenuatorMaxDb: max,
    rx1AttenuatorSupported:
      typeof r.rx1AttenuatorSupported === 'boolean'
        ? r.rx1AttenuatorSupported
        : DEFAULT_G2_OPTIONS.rx1AttenuatorSupported,
  };
}

export async function fetchHl2Options(signal?: AbortSignal): Promise<Hl2Options> {
  const res = await fetch('/api/radio/hl2-options', { signal });
  if (!res.ok) throw new Error(`GET /api/radio/hl2-options → ${res.status}`);
  return parseHl2(await res.json());
}

export async function updateHl2Options(
  patch: Partial<Hl2Options>,
  signal?: AbortSignal,
): Promise<Hl2Options> {
  const res = await fetch('/api/radio/hl2-options', {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(patch),
    signal,
  });
  if (!res.ok) throw new Error(`PUT /api/radio/hl2-options → ${res.status}`);
  return parseHl2(await res.json());
}

export async function fetchG2Options(signal?: AbortSignal): Promise<G2Options> {
  const res = await fetch('/api/radio/g2-options', { signal });
  if (!res.ok) throw new Error(`GET /api/radio/g2-options → ${res.status}`);
  return parseG2(await res.json());
}

export async function updateG2Options(
  patch: Partial<Pick<G2Options, 'ditherEnabled' | 'randomEnabled' | 'rx1AttenuatorDb'>>,
  signal?: AbortSignal,
): Promise<G2Options> {
  const res = await fetch('/api/radio/g2-options', {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(patch),
    signal,
  });
  if (!res.ok) throw new Error(`PUT /api/radio/g2-options → ${res.status}`);
  return parseG2(await res.json());
}

type RadioOptionsStore = {
  options: Hl2Options;
  g2Options: G2Options;
  loaded: boolean;
  inflight: boolean;
  error: string | null;
  load: () => Promise<void>;
  setBandVolts: (next: boolean) => Promise<void>;
  setG2Dither: (next: boolean) => Promise<void>;
  setG2Random: (next: boolean) => Promise<void>;
  setG2Rx1Attenuator: (next: number) => Promise<void>;
};

export const useRadioOptionsStore = create<RadioOptionsStore>((set, get) => ({
  options: DEFAULT_OPTIONS,
  g2Options: DEFAULT_G2_OPTIONS,
  loaded: false,
  inflight: false,
  error: null,

  load: async () => {
    set({ inflight: true, error: null });
    try {
      const [options, g2Options] = await Promise.all([
        fetchHl2Options(),
        fetchG2Options(),
      ]);
      set({ options, g2Options, loaded: true, inflight: false });
    } catch (err) {
      set({
        error: err instanceof Error ? err.message : String(err),
        inflight: false,
      });
    }
  },

  setBandVolts: async (next) => {
    const prev = get().options;
    // Optimistic: flip the local flag immediately so the checkbox feels
    // responsive, then confirm against the server's echoed JSON.
    set({ options: { ...prev, bandVolts: next }, inflight: true, error: null });
    try {
      const o = await updateHl2Options({ bandVolts: next });
      set({ options: o, inflight: false });
    } catch (err) {
      set({
        options: prev,
        error: err instanceof Error ? err.message : String(err),
        inflight: false,
      });
    }
  },

  setG2Dither: async (next) => {
    const prev = get().g2Options;
    set({
      g2Options: { ...prev, ditherEnabled: next },
      inflight: true,
      error: null,
    });
    try {
      const o = await updateG2Options({ ditherEnabled: next });
      set({ g2Options: o, inflight: false });
    } catch (err) {
      set({
        g2Options: prev,
        error: err instanceof Error ? err.message : String(err),
        inflight: false,
      });
    }
  },

  setG2Random: async (next) => {
    const prev = get().g2Options;
    set({
      g2Options: { ...prev, randomEnabled: next },
      inflight: true,
      error: null,
    });
    try {
      const o = await updateG2Options({ randomEnabled: next });
      set({ g2Options: o, inflight: false });
    } catch (err) {
      set({
        g2Options: prev,
        error: err instanceof Error ? err.message : String(err),
        inflight: false,
      });
    }
  },

  setG2Rx1Attenuator: async (next) => {
    const prev = get().g2Options;
    const clamped = Math.max(
      prev.rx1AttenuatorMinDb,
      Math.min(prev.rx1AttenuatorMaxDb, Math.round(next)),
    );
    set({
      g2Options: { ...prev, rx1AttenuatorDb: clamped },
      inflight: true,
      error: null,
    });
    try {
      const o = await updateG2Options({ rx1AttenuatorDb: clamped });
      set({ g2Options: o, inflight: false });
    } catch (err) {
      set({
        g2Options: prev,
        error: err instanceof Error ? err.message : String(err),
        inflight: false,
      });
    }
  },
}));
