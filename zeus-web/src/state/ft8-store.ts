// SPDX-License-Identifier: GPL-2.0-or-later
//
// FT8/FT4 decode store. Holds the rolling list of decoded messages for the FT8
// workspace decode table, plus the enable/protocol/native-availability state.
//
// Decodes arrive as 0x38 Ft8Decode WS frames (one per completed UTC slot),
// parsed in realtime/ws-client.ts and applied via ingest(). Enable/disable and
// status hydrate from /api/ft8 (server-authoritative). Mirrors the chat-store /
// ptt-store split: REST for control state, WS push for the live stream.

import { create } from 'zustand';
import { nearestDigitalBand } from '../dsp/digital-segments';
import {
  configureRadioForDigital,
  restoreRadioWhenIdle,
  snapshotRadio,
  type RadioModeSnapshot,
} from './digital-mode';
import { useConnectionStore } from './connection-store';

export type Ft8ProtocolName = 'FT8' | 'FT4';

/** One decoded message line as it arrives on the wire (camelCase JSON). */
export interface Ft8DecodeDto {
  snrDb: number;
  dtSec: number;
  freqHz: number;
  score: number;
  text: string;
}

/** A completed slot's decodes for one receiver (the 0x38 payload). */
export interface Ft8DecodeBatch {
  receiver: number;
  slotStartUnixMs: number;
  protocol: Ft8ProtocolName;
  decodes: Ft8DecodeDto[];
}

/** A flattened decode row for the table (one message + its slot context). */
export interface Ft8Row extends Ft8DecodeDto {
  id: string;
  receiver: number;
  protocol: Ft8ProtocolName;
  slotStartUnixMs: number;
}

/** Keep the table bounded — FT8 produces ~10-30 decodes every 15 s. */
const MAX_ROWS = 500;

interface Ft8State {
  /** Workspace overlay visibility — independent of decoder enable so the shell
   *  is viewable on a dev box without the radio / native lib. */
  open: boolean;
  nativeAvailable: boolean;
  enabled: boolean;
  receiver: number;
  protocol: Ft8ProtocolName;
  passes: number;
  rows: Ft8Row[];
  error: string | null;
  /** Active band label (e.g. "20m") for the workspace band selector. */
  band: string;
  /** RX config captured at entry so exit restores the radio. */
  priorRadio: RadioModeSnapshot | null;

  /** Open the FT8 workspace: configure the radio (DIGU + wide filter + QSY to
   *  the band dial) and start decoding. `prior` carries a pre-digital snapshot
   *  from another digital mode being switched away from, so exit restores the
   *  operator's TRUE pre-digital config rather than the other mode's DIGU dial. */
  openWorkspace: (opts?: {
    receiver?: number;
    protocol?: Ft8ProtocolName;
    prior?: RadioModeSnapshot;
  }) => void;
  /** Close the workspace, stop decoding, and restore the prior radio config.
   *  `restore: false` skips the radio restore (used when switching directly to
   *  another digital mode, which reconfigures the radio itself). */
  closeWorkspace: (opts?: { restore?: boolean }) => void;
  /** Switch FT8↔FT4 in-place (re-QSY + re-enable) without leaving the workspace. */
  switchProtocol: (protocol: Ft8ProtocolName) => void;
  /** QSY to a band's dial for the active protocol (workspace band buttons). */
  qsyBand: (bandName: string) => void;
  /** Apply a 0x38 decode batch (newest rows first). */
  ingest: (batch: Ft8DecodeBatch) => void;
  /** Hydrate enable/native/protocol from GET /api/ft8. */
  refreshStatus: (signal?: AbortSignal) => Promise<void>;
  /** Enter FT8/FT4 decode on a receiver. */
  enable: (opts?: { receiver?: number; protocol?: Ft8ProtocolName; passes?: number }) => Promise<boolean>;
  /** Leave FT8/FT4 decode. */
  disable: () => Promise<void>;
  /** Clear the decode table. */
  clear: () => void;
  /** Set the decode depth (passes 1..4). Applies live: if the decoder is
   *  currently enabled it re-enables on the same receiver/protocol to push the
   *  new pass count to the backend; otherwise it just updates the value used by
   *  the next enable. Persistence is the settings store's job, not this one. */
  setPasses: (passes: number) => void;
}

export const useFt8Store = create<Ft8State>((set, get) => ({
  open: false,
  nativeAvailable: false,
  enabled: false,
  receiver: -1,
  protocol: 'FT8',
  passes: 3,
  rows: [],
  error: null,
  band: '20m',
  priorRadio: null,

  openWorkspace: (opts) => {
    const protocol = opts?.protocol ?? get().protocol;
    // Snapshot the radio once per entry so re-entry (protocol switch without
    // closing) doesn't overwrite the original config with the DIGU state. A
    // `prior` passed in from a mode switch is the operator's TRUE pre-digital
    // config and takes precedence over re-snapshotting the (already-DIGU) radio.
    const prior = opts?.prior ?? get().priorRadio ?? snapshotRadio();
    const band = nearestDigitalBand(useConnectionStore.getState().vfoHz).name;
    set({ open: true, protocol, band, priorRadio: prior });
    void (async () => {
      await configureRadioForDigital(protocol, band);
      await get().enable({ receiver: opts?.receiver, protocol });
    })();
  },

  closeWorkspace: (opts) => {
    const restore = opts?.restore !== false;
    const prior = get().priorRadio;
    set({ open: false, priorRadio: null });
    void (async () => {
      await get().disable();
      // Defer the restore until the radio is idle so disengaging mid-TX doesn't
      // strand it in DIGU. Skipped entirely when switching to another digital
      // mode (that mode reconfigures the radio itself).
      if (restore) restoreRadioWhenIdle(prior);
    })();
  },

  switchProtocol: (protocol) => {
    if (protocol === get().protocol) return;
    const band = get().band;
    set({ protocol });
    void (async () => {
      await configureRadioForDigital(protocol, band);
      await get().enable({ protocol });
    })();
  },

  qsyBand: (bandName) => {
    set({ band: bandName });
    // Reuse the full digital-config path (DIGU + flat filter + QSY + framing),
    // not a VFO-only move: a cross-band QSY trips the server's per-band mode
    // recall, so we must re-assert DIGU/filter AFTER the QSY or decode breaks.
    void configureRadioForDigital(get().protocol, bandName);
  },

  ingest: (batch) =>
    set((s) => {
      const incoming: Ft8Row[] = batch.decodes.map((d, i) => ({
        ...d,
        id: `${batch.receiver}:${batch.slotStartUnixMs}:${i}`,
        receiver: batch.receiver,
        protocol: batch.protocol,
        slotStartUnixMs: batch.slotStartUnixMs,
      }));
      // Newest first, bounded.
      const rows = [...incoming, ...s.rows].slice(0, MAX_ROWS);
      return { rows };
    }),

  refreshStatus: async (signal) => {
    try {
      const res = await fetch('/api/ft8', { signal });
      if (!res.ok) throw new Error(`GET /api/ft8 → ${res.status}`);
      const j = (await res.json()) as Record<string, unknown>;
      set({
        nativeAvailable: j.nativeAvailable === true,
        enabled: j.enabled === true,
        receiver: typeof j.receiver === 'number' ? j.receiver : -1,
        protocol: j.protocol === 'FT4' ? 'FT4' : 'FT8',
        passes: typeof j.passes === 'number' ? j.passes : 3,
        error: null,
      });
    } catch (e) {
      set({ error: e instanceof Error ? e.message : String(e) });
    }
  },

  enable: async (opts) => {
    try {
      const res = await fetch('/api/ft8/enable', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({
          receiver: opts?.receiver ?? 0,
          protocol: opts?.protocol ?? get().protocol,
          passes: opts?.passes ?? get().passes,
        }),
      });
      if (!res.ok) throw new Error(`POST /api/ft8/enable → ${res.status}`);
      const j = (await res.json()) as Record<string, unknown>;
      const ok = j.enabled === true;
      set({
        enabled: ok,
        nativeAvailable: j.nativeAvailable === true || ok,
        receiver: ok ? (opts?.receiver ?? 0) : get().receiver,
        protocol: opts?.protocol ?? get().protocol,
        error: ok ? null : 'FT8 native decoder unavailable',
      });
      return ok;
    } catch (e) {
      set({ error: e instanceof Error ? e.message : String(e) });
      return false;
    }
  },

  disable: async () => {
    try {
      await fetch('/api/ft8/disable', { method: 'POST' });
    } catch {
      /* best-effort */
    }
    set({ enabled: false, receiver: -1 });
  },

  clear: () => set({ rows: [] }),

  setPasses: (passes) => {
    const p = Math.min(4, Math.max(1, Math.round(passes)));
    const { enabled, receiver, protocol } = get();
    set({ passes: p });
    if (enabled) {
      void get().enable({ receiver: receiver >= 0 ? receiver : 0, protocol, passes: p });
    }
  },
}));
