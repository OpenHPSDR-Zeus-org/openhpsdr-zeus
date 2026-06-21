// SPDX-License-Identifier: GPL-2.0-or-later
//
// Hardware-PTT-IN status store. Holds the live footswitch / mic-PTT / rear-KEY
// level for the Radio Settings "PTT-IN: idle / keyed" lamp, plus the
// MOX-promotion enable gate and the fixed release hang.
//
// `keyed` is written from the WS dispatcher on every PttStatusFrame (0x37).
// The frame source is per-protocol on the server (P1 HardwarePttChanged, P2
// UDP-1025 PttIn) so the lamp tracks the physical input regardless of board.
// `enabled` / `hangMs` hydrate from GET /api/radio/ptt-status on mount and
// re-sync on a PUT toggle — server-authoritative, never clobbered on connect.

import { create } from 'zustand';

export interface PttStatus {
  /** Live PTT-IN level — true while the hardware footswitch/mic-PTT is held. */
  keyed: boolean;
  /** Whether hardware PTT is promoted to MOX. Defaults OFF server-side (opt-in). */
  enabled: boolean;
  /** Fixed release hang in ms (250). Read-only label; knob is out of scope. */
  hangMs: number;
}

// The server returns ExternalPttStatusDto (camelCase JSON). We only consume the
// `enabled` gate and `hangTimeMs` here; the live lamp level rides the WS frame.
function parse(raw: unknown): { enabled: boolean; hangMs: number } {
  const r = (raw && typeof raw === 'object' ? raw : {}) as Record<string, unknown>;
  return {
    enabled: typeof r.enabled === 'boolean' ? r.enabled : false,
    hangMs: typeof r.hangTimeMs === 'number' ? r.hangTimeMs : 250,
  };
}

export async function fetchPttStatus(signal?: AbortSignal): Promise<{ enabled: boolean; hangMs: number }> {
  const res = await fetch('/api/radio/ptt-status', { signal });
  if (!res.ok) throw new Error(`GET /api/radio/ptt-status → ${res.status}`);
  return parse(await res.json());
}

export async function updatePttEnable(
  enabled: boolean,
  signal?: AbortSignal,
): Promise<{ enabled: boolean; hangMs: number }> {
  const res = await fetch('/api/radio/ptt-status', {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ enabled }),
    signal,
  });
  if (!res.ok) throw new Error(`PUT /api/radio/ptt-status → ${res.status}`);
  return parse(await res.json());
}

interface PttState extends PttStatus {
  loaded: boolean;
  inflight: boolean;
  error: string | null;
  /** WS-driven live lamp update (PttStatusFrame). */
  setKeyed: (keyed: boolean) => void;
  /** Hydrate enable/hang from the REST snapshot. */
  load: () => Promise<void>;
  /** Toggle the MOX-promotion gate (optimistic, server-authoritative). */
  setEnabled: (enabled: boolean) => Promise<void>;
  __resetForTests: () => void;
}

export const usePttStore = create<PttState>((set, get) => ({
  keyed: false,
  enabled: false,
  hangMs: 250,
  loaded: false,
  inflight: false,
  error: null,

  setKeyed: (keyed) => set({ keyed }),

  load: async () => {
    set({ inflight: true, error: null });
    try {
      const s = await fetchPttStatus();
      // Don't clobber the live lamp with the REST snapshot — the WS frame is
      // the authority for `keyed` once connected.
      set({ enabled: s.enabled, hangMs: s.hangMs, loaded: true, inflight: false });
    } catch (err) {
      set({ error: err instanceof Error ? err.message : String(err), inflight: false });
    }
  },

  setEnabled: async (enabled) => {
    const prev = get().enabled;
    set({ enabled, inflight: true, error: null });
    try {
      const s = await updatePttEnable(enabled);
      set({ enabled: s.enabled, hangMs: s.hangMs, inflight: false });
    } catch (err) {
      set({ enabled: prev, error: err instanceof Error ? err.message : String(err), inflight: false });
    }
  },

  __resetForTests: () =>
    set({ keyed: false, enabled: false, hangMs: 250, loaded: false, inflight: false, error: null }),
}));
