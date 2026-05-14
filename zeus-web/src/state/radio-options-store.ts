// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// HL2 optional toggles (currently: Band Volts PWM output). Mirrors
// /api/radio/hl2-options, which always responds 200 regardless of the
// connected board kind — non-HL2 radios simply return `bandVolts: false`
// and ignore writes. The gating for whether the panel is visible at all
// lives one layer up in `BoardCapabilities.hasHl2OptionalToggles`.
//
// Pattern mirrors pa-store / vst-host-store: optimistic local update on
// toggle with a rollback on server error; an inflight flag for the panel
// to show a "saving…" indicator while the PUT is in flight.

import { create } from 'zustand';

export interface Hl2Options {
  bandVolts: boolean;
}

const DEFAULT_OPTIONS: Hl2Options = { bandVolts: false };

function parse(raw: unknown): Hl2Options {
  if (!raw || typeof raw !== 'object') return DEFAULT_OPTIONS;
  const r = raw as Record<string, unknown>;
  return {
    bandVolts: typeof r.bandVolts === 'boolean' ? r.bandVolts : false,
  };
}

export async function fetchHl2Options(signal?: AbortSignal): Promise<Hl2Options> {
  const res = await fetch('/api/radio/hl2-options', { signal });
  if (!res.ok) throw new Error(`GET /api/radio/hl2-options → ${res.status}`);
  return parse(await res.json());
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
  return parse(await res.json());
}

type RadioOptionsStore = {
  options: Hl2Options;
  loaded: boolean;
  inflight: boolean;
  error: string | null;
  load: () => Promise<void>;
  setBandVolts: (next: boolean) => Promise<void>;
};

export const useRadioOptionsStore = create<RadioOptionsStore>((set, get) => ({
  options: DEFAULT_OPTIONS,
  loaded: false,
  inflight: false,
  error: null,

  load: async () => {
    set({ inflight: true, error: null });
    try {
      const o = await fetchHl2Options();
      set({ options: o, loaded: true, inflight: false });
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
}));
