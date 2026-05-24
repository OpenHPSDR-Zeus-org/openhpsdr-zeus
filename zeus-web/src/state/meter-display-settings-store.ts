// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

import { create } from 'zustand';
import {
  fetchMeterDisplaySettings,
  updateMaxDisplayedWatts,
  updateSMeterOffsetDb,
  MAX_DISPLAYED_WATTS_DEFAULT,
  MAX_DISPLAYED_WATTS_MAX,
  MAX_DISPLAYED_WATTS_MIN,
  SMETER_OFFSET_DEFAULT_DB,
  SMETER_OFFSET_MIN_DB,
  SMETER_OFFSET_MAX_DB,
} from '../api/meter-display-settings';

// Display-side meter calibration knobs (GitHub #426). Persisted server-side
// in zeus-prefs.db (LiteDB) so the calibration follows the operator across
// browsers / devices. NO localStorage mirror per project_litedb_tx_filter
// _persistence / project_rotator_resume lessons.
//
// The component should call setSMeterOffsetDbLocal(...) on every keystroke
// (responsive display) and persistSMeterOffsetDb(...) on blur / Enter so
// the server only sees the committed value.

type MeterDisplaySettingsState = {
  sMeterOffsetDb: number;
  // 0 = "no override, use radio's MaxWatts as the meter full scale".
  maxDisplayedWatts: number;
  hydrated: boolean;
  setSMeterOffsetDbLocal: (dB: number) => void;
  persistSMeterOffsetDb: (dB: number) => Promise<void>;
  setMaxDisplayedWattsLocal: (w: number) => void;
  persistMaxDisplayedWatts: (w: number) => Promise<void>;
};

function clampOffset(v: number): number {
  if (!Number.isFinite(v)) return SMETER_OFFSET_DEFAULT_DB;
  if (v < SMETER_OFFSET_MIN_DB) return SMETER_OFFSET_MIN_DB;
  if (v > SMETER_OFFSET_MAX_DB) return SMETER_OFFSET_MAX_DB;
  return v;
}

function clampMaxWatts(v: number): number {
  if (!Number.isFinite(v)) return MAX_DISPLAYED_WATTS_DEFAULT;
  if (v <= 0) return MAX_DISPLAYED_WATTS_DEFAULT;
  if (v < MAX_DISPLAYED_WATTS_MIN) return MAX_DISPLAYED_WATTS_MIN;
  if (v > MAX_DISPLAYED_WATTS_MAX) return MAX_DISPLAYED_WATTS_MAX;
  return v;
}

export const useMeterDisplaySettingsStore = create<MeterDisplaySettingsState>((set) => ({
  sMeterOffsetDb: SMETER_OFFSET_DEFAULT_DB,
  maxDisplayedWatts: MAX_DISPLAYED_WATTS_DEFAULT,
  hydrated: false,
  setSMeterOffsetDbLocal: (dB) => {
    set({ sMeterOffsetDb: clampOffset(dB) });
  },
  persistSMeterOffsetDb: async (dB) => {
    const next = clampOffset(dB);
    set({ sMeterOffsetDb: next });
    try {
      const result = await updateSMeterOffsetDb(next);
      set({
        sMeterOffsetDb: result.sMeterOffsetDb,
        maxDisplayedWatts: result.maxDisplayedWatts,
      });
    } catch {
      // Network / server failure — keep the local value. Next commit
      // will retry. We deliberately do NOT roll back: the operator's
      // intent is the value they just dialed in.
    }
  },
  setMaxDisplayedWattsLocal: (w) => {
    set({ maxDisplayedWatts: clampMaxWatts(w) });
  },
  persistMaxDisplayedWatts: async (w) => {
    const next = clampMaxWatts(w);
    set({ maxDisplayedWatts: next });
    try {
      const result = await updateMaxDisplayedWatts(next);
      set({
        sMeterOffsetDb: result.sMeterOffsetDb,
        maxDisplayedWatts: result.maxDisplayedWatts,
      });
    } catch {
      // See above — keep the local value on failure.
    }
  },
}));

async function hydrateFromServer(): Promise<void> {
  try {
    const server = await fetchMeterDisplaySettings();
    useMeterDisplaySettingsStore.setState({
      sMeterOffsetDb: server.sMeterOffsetDb,
      maxDisplayedWatts: server.maxDisplayedWatts,
      hydrated: true,
    });
  } catch {
    // Backend unreachable — keep the defaults. Stay unhydrated so the
    // next successful fetch (or a PUT) populates the store.
    useMeterDisplaySettingsStore.setState({ hydrated: false });
  }
}

void hydrateFromServer();
