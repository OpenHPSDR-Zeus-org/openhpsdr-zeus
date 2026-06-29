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
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

// Per-receiver waterfall dB-window overrides for the multi-DDC display.
//
// The global waterfall window (display-settings-store wfDbMin/wfDbMax) is RX1's
// window and the default for every other receiver. When the operator focuses a
// receiver and drags the waterfall dB scale, the adjustment is stored HERE,
// keyed by 0-based rxIndex (RX2 = 1, RX3+ = 2..), so each receiver keeps its
// own scale. RX1 (index 0) is never stored here — it stays on the global
// window (which already round-trips to server prefs).
//
// Until a receiver has an explicit override, its effective window is the global
// window shifted by its floor-normalization offset (anchored to the median floor
// across all panes), so floors line up out of the box. Once the operator drags a
// receiver's scale, that absolute window sticks and persists (localStorage).

import { create } from 'zustand';
import { useDisplaySettingsStore } from './display-settings-store';
import { floorNormalizationOffsetDb } from '../dsp/floor-normalization';

const STORAGE_KEY = 'zeus.rxWfDbWindows';

// Absolute dB bounds + minimum span a window may occupy. Mirrors the spirit of
// display-settings-store's clamp so a drag can't push the window off-scale.
const ABS_MIN_DB = -200;
const ABS_MAX_DB = 20;

export type RxWfWindow = { wfDbMin: number; wfDbMax: number };

/** Shift a window by deltaDb, preserving span and clamping into the abs range. */
function shiftWindow(min: number, max: number, deltaDb: number): RxWfWindow {
  let nmin = min + deltaDb;
  let nmax = max + deltaDb;
  if (nmin < ABS_MIN_DB) {
    const c = ABS_MIN_DB - nmin;
    nmin += c;
    nmax += c;
  }
  if (nmax > ABS_MAX_DB) {
    const c = nmax - ABS_MAX_DB;
    nmin -= c;
    nmax -= c;
  }
  return { wfDbMin: nmin, wfDbMax: nmax };
}

function readSaved(): Record<number, RxWfWindow> {
  try {
    if (typeof localStorage === 'undefined') return {};
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return {};
    const parsed = JSON.parse(raw) as Record<string, RxWfWindow>;
    const out: Record<number, RxWfWindow> = {};
    for (const [k, v] of Object.entries(parsed)) {
      const idx = Number.parseInt(k, 10);
      if (
        Number.isFinite(idx) &&
        idx >= 1 &&
        typeof v?.wfDbMin === 'number' &&
        typeof v?.wfDbMax === 'number' &&
        v.wfDbMin < v.wfDbMax
      ) {
        out[idx] = { wfDbMin: v.wfDbMin, wfDbMax: v.wfDbMax };
      }
    }
    return out;
  } catch {
    return {};
  }
}

function writeSaved(overrides: Record<number, RxWfWindow>): void {
  try {
    if (typeof localStorage !== 'undefined')
      localStorage.setItem(STORAGE_KEY, JSON.stringify(overrides));
  } catch {
    /* ignore */
  }
}

type RxDbWindowState = {
  overrides: Record<number, RxWfWindow>;
  /** Set an absolute window for a receiver (rxIndex >= 1). */
  setRxWfWindow: (rxIndex: number, wfDbMin: number, wfDbMax: number) => void;
  /** Shift a receiver's effective window by deltaDb (creates an override). */
  shiftRxWfWindow: (rxIndex: number, deltaDb: number) => void;
  /** Drop a receiver's override so it falls back to the normalized default. */
  clearRxWfWindow: (rxIndex: number) => void;
  /** Drop EVERY override so all receivers fall back to floor normalization. */
  clearAllRxWfWindows: () => void;
};

export const useRxDbWindowStore = create<RxDbWindowState>((set, get) => ({
  overrides: readSaved(),

  setRxWfWindow: (rxIndex, wfDbMin, wfDbMax) => {
    if (rxIndex < 1 || !(wfDbMin < wfDbMax)) return;
    const overrides = { ...get().overrides, [rxIndex]: { wfDbMin, wfDbMax } };
    writeSaved(overrides);
    set({ overrides });
  },

  shiftRxWfWindow: (rxIndex, deltaDb) => {
    if (rxIndex < 1) return;
    const cur = effectiveRxWfWindow(rxIndex);
    const next = shiftWindow(cur.wfDbMin, cur.wfDbMax, deltaDb);
    const overrides = { ...get().overrides, [rxIndex]: next };
    writeSaved(overrides);
    set({ overrides });
  },

  clearRxWfWindow: (rxIndex) => {
    if (!(rxIndex in get().overrides)) return;
    const overrides = { ...get().overrides };
    delete overrides[rxIndex];
    writeSaved(overrides);
    set({ overrides });
  },

  clearAllRxWfWindows: () => {
    if (Object.keys(get().overrides).length === 0) return;
    writeSaved({});
    set({ overrides: {} });
  },
}));

/**
 * The effective waterfall dB window for a receiver index.
 * - RXn (>=1) with an explicit override: that absolute window.
 * - Otherwise (incl. RX1): the global window shifted by the receiver's
 *   floor-normalization offset, so every pane's noise floor lands at the shared
 *   median level. With one receiver the offset is 0 → the raw global window.
 */
export function effectiveRxWfWindow(rxIndex: number): RxWfWindow {
  const ds = useDisplaySettingsStore.getState();
  const ov = rxIndex >= 1 ? useRxDbWindowStore.getState().overrides[rxIndex] : undefined;
  if (ov) return ov;
  const off = floorNormalizationOffsetDb(rxIndex);
  return { wfDbMin: ds.wfDbMin + off, wfDbMax: ds.wfDbMax + off };
}
