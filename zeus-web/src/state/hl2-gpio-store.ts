// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// HL2 user GPIO (external-ports plan, Phase 5). Mirrors /api/radio/hl2-gpio:
// the 4-bit user_dig_out mask → register 0x0a (wire 0x14) C3[3:0] MCP23008.
// Server-authoritative (the backend pushes the persisted mask to the live
// client on connect), so the frontend only loads + PUTs operator edits. HL2
// only; `supported` gates the toggle group.

import { create } from 'zustand';

export interface Hl2Gpio {
  supported: boolean;
  bits: number; // 4-bit mask
}

const DEFAULT: Hl2Gpio = { supported: false, bits: 0 };

function parse(raw: unknown): Hl2Gpio {
  if (!raw || typeof raw !== 'object') return DEFAULT;
  const r = raw as Record<string, unknown>;
  return {
    supported: typeof r.supported === 'boolean' ? r.supported : false,
    bits: typeof r.bits === 'number' ? r.bits & 0x0f : 0,
  };
}

export async function fetchHl2Gpio(signal?: AbortSignal): Promise<Hl2Gpio> {
  const res = await fetch('/api/radio/hl2-gpio', { signal });
  if (!res.ok) throw new Error(`GET /api/radio/hl2-gpio → ${res.status}`);
  return parse(await res.json());
}

export async function updateHl2Gpio(
  bits: number,
  signal?: AbortSignal,
): Promise<Hl2Gpio> {
  const res = await fetch('/api/radio/hl2-gpio', {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ bits: bits & 0x0f }),
    signal,
  });
  if (!res.ok) throw new Error(`PUT /api/radio/hl2-gpio → ${res.status}`);
  return parse(await res.json());
}

type Hl2GpioStore = {
  state: Hl2Gpio;
  inflight: boolean;
  error: string | null;
  load: () => Promise<void>;
  setBit: (bit: number, on: boolean) => Promise<void>;
};

export const useHl2GpioStore = create<Hl2GpioStore>((set, get) => ({
  state: DEFAULT,
  inflight: false,
  error: null,

  load: async () => {
    set({ inflight: true, error: null });
    try {
      const s = await fetchHl2Gpio();
      set({ state: s, inflight: false });
    } catch (err) {
      set({
        error: err instanceof Error ? err.message : String(err),
        inflight: false,
      });
    }
  },

  setBit: async (bit, on) => {
    const prev = get().state;
    const mask = 1 << bit;
    const nextBits = (on ? prev.bits | mask : prev.bits & ~mask) & 0x0f;
    set({ state: { ...prev, bits: nextBits }, inflight: true, error: null });
    try {
      const s = await updateHl2Gpio(nextBits);
      set({ state: s, inflight: false });
    } catch (err) {
      set({
        state: prev,
        error: err instanceof Error ? err.message : String(err),
        inflight: false,
      });
    }
  },
}));
