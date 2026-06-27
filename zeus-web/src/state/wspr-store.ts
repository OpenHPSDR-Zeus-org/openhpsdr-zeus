// SPDX-License-Identifier: GPL-2.0-or-later
//
// WSPR spot store. WSPR is a beacon mode — no QSO, no TX sequencing — so this is
// just a rolling list of received spots for the WSPR workspace table, plus the
// enable/native state. Spots arrive as 0x39 WsprSpot WS frames (one per 120 s
// UTC slot), parsed in realtime/ws-client.ts and applied via ingest(). Enable/
// disable hydrate from /api/wspr. Mirrors ft8-store.

import { create } from 'zustand';
import { dialHzFor, nearestDigitalBand } from '../dsp/digital-segments';
import {
  configureRadioForDigital,
  restoreRadioWhenIdle,
  snapshotRadio,
  type RadioModeSnapshot,
} from './digital-mode';
import { useConnectionStore } from './connection-store';

/** One decoded WSPR spot as it arrives on the wire (camelCase JSON). */
export interface WsprSpotDto {
  snrDb: number;
  dtSec: number;
  freqMhz: number;
  driftHz: number;
  message: string; // "<callsign> <grid4> <dBm>"
}

/** A completed slot's spots for one receiver (the 0x39 payload). */
export interface WsprSpotBatch {
  receiver: number;
  slotStartUnixMs: number;
  dialFreqMhz: number;
  spots: WsprSpotDto[];
}

/** A flattened spot row for the table. */
export interface WsprRow extends WsprSpotDto {
  id: string;
  slotStartUnixMs: number;
  /** Parsed from message for table columns. */
  callsign: string;
  grid: string;
  powerDbm: number | null;
}

const MAX_ROWS = 500;

function parseSpotMessage(message: string): { callsign: string; grid: string; powerDbm: number | null } {
  const t = message.trim().split(/\s+/);
  const powerDbm = t[2] != null && /^\d+$/.test(t[2]) ? parseInt(t[2], 10) : null;
  return { callsign: t[0] ?? '', grid: t[1] ?? '', powerDbm };
}

interface WsprState {
  open: boolean;
  nativeAvailable: boolean;
  enabled: boolean;
  band: string;
  rows: WsprRow[];
  error: string | null;
  priorRadio: RadioModeSnapshot | null;

  openWorkspace: (opts?: { prior?: RadioModeSnapshot }) => void;
  closeWorkspace: (opts?: { restore?: boolean }) => void;
  ingest: (batch: WsprSpotBatch) => void;
  refreshStatus: (signal?: AbortSignal) => Promise<void>;
  enable: (band: string) => Promise<boolean>;
  disable: () => Promise<void>;
  qsyBand: (bandName: string) => void;
  clear: () => void;
}

export const useWsprStore = create<WsprState>((set, get) => ({
  open: false,
  nativeAvailable: false,
  enabled: false,
  band: '20m',
  rows: [],
  error: null,
  priorRadio: null,

  openWorkspace: (opts) => {
    // A `prior` from a mode switch is the operator's TRUE pre-digital config and
    // wins over re-snapshotting the (already-DIGU) radio.
    const prior = opts?.prior ?? get().priorRadio ?? snapshotRadio();
    const band = nearestDigitalBand(useConnectionStore.getState().vfoHz).name;
    set({ open: true, band, priorRadio: prior });
    void (async () => {
      await configureRadioForDigital('WSPR', band);
      await get().enable(band);
    })();
  },

  closeWorkspace: (opts) => {
    const restore = opts?.restore !== false;
    const prior = get().priorRadio;
    set({ open: false, priorRadio: null });
    void (async () => {
      await get().disable();
      // Deferred until idle so disengaging mid-beacon-TX doesn't strand the
      // radio; skipped when switching to another digital mode.
      if (restore) restoreRadioWhenIdle(prior);
    })();
  },

  ingest: (batch) =>
    set((s) => {
      const incoming: WsprRow[] = batch.spots.map((d, i) => ({
        ...d,
        ...parseSpotMessage(d.message),
        id: `${batch.receiver}:${batch.slotStartUnixMs}:${i}`,
        slotStartUnixMs: batch.slotStartUnixMs,
      }));
      const rows = [...incoming, ...s.rows].slice(0, MAX_ROWS);
      return { rows };
    }),

  refreshStatus: async (signal) => {
    try {
      const res = await fetch('/api/wspr', { signal });
      if (!res.ok) throw new Error(`GET /api/wspr → ${res.status}`);
      const j = (await res.json()) as Record<string, unknown>;
      set({
        nativeAvailable: j.nativeAvailable === true,
        enabled: j.enabled === true,
        error: null,
      });
    } catch (e) {
      set({ error: e instanceof Error ? e.message : String(e) });
    }
  },

  enable: async (band) => {
    try {
      const dialHz = dialHzFor('WSPR', band) ?? 14_095_600;
      const res = await fetch('/api/wspr/enable', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ receiver: 0, dialFreqMhz: dialHz / 1e6 }),
      });
      if (!res.ok) throw new Error(`POST /api/wspr/enable → ${res.status}`);
      const j = (await res.json()) as Record<string, unknown>;
      const ok = j.enabled === true;
      set({
        enabled: ok,
        nativeAvailable: j.nativeAvailable === true || ok,
        error: ok ? null : 'WSPR native decoder unavailable',
      });
      return ok;
    } catch (e) {
      set({ error: e instanceof Error ? e.message : String(e) });
      return false;
    }
  },

  disable: async () => {
    try {
      await fetch('/api/wspr/disable', { method: 'POST' });
    } catch {
      /* best-effort */
    }
    set({ enabled: false });
  },

  qsyBand: (bandName) => {
    set({ band: bandName });
    void (async () => {
      // Full digital re-config (DIGU + flat filter + QSY) so a cross-band move
      // re-asserts DIGU after the server's per-band mode recall; then re-arm the
      // WSPR decoder on the new dial.
      await configureRadioForDigital('WSPR', bandName);
      if (get().enabled) await get().enable(bandName);
    })();
  },

  clear: () => set({ rows: [] }),
}));
