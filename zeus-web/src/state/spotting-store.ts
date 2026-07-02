// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

import { create } from 'zustand';
import {
  getSpottingStatus,
  postSpottingConfig,
  type SpottingConfig,
  type SpottingStatus,
} from '../api/spotting';

// The Zeus Digital plugin (its IPluginSettings store) is the source of truth.
// The form initialises from the plugin's /spotting/status once it arrives —
// which fails harmlessly while the plugin is absent (the panel greys out).
// Do NOT seed from localStorage and do NOT auto-POST on load (same lesson as
// TCI/CAT/WSJT-X). Both uploaders default OFF — network egress is opt-in only.
const DEFAULT_CONFIG: SpottingConfig = {
  pskReporterEnabled: false,
  wsprnetEnabled: false,
  callsign: '',
  grid: '',
};

export type SpottingStoreState = {
  config: SpottingConfig;
  status: SpottingStatus | null;

  refreshStatus: () => Promise<void>;
  saveConfig: (cfg: SpottingConfig) => Promise<SpottingStatus>;
};

export const useSpottingStore = create<SpottingStoreState>((set, get) => ({
  config: DEFAULT_CONFIG,
  status: null,

  refreshStatus: async () => {
    try {
      const status = await getSpottingStatus();
      const current = get().config;
      const synced =
        current.pskReporterEnabled === status.pskReporterEnabled &&
        current.wsprnetEnabled === status.wsprnetEnabled &&
        current.callsign === status.callsign &&
        current.grid === status.grid;
      set({
        status,
        config: synced
          ? current
          : {
              pskReporterEnabled: status.pskReporterEnabled,
              wsprnetEnabled: status.wsprnetEnabled,
              callsign: status.callsign,
              grid: status.grid,
            },
      });
    } catch {
      /* transient — next refresh recovers */
    }
  },

  saveConfig: async (cfg) => {
    const status = await postSpottingConfig(cfg);
    set({
      status,
      config: {
        pskReporterEnabled: status.pskReporterEnabled,
        wsprnetEnabled: status.wsprnetEnabled,
        callsign: status.callsign,
        grid: status.grid,
      },
    });
    return status;
  },
}));

// Initial status probe (one-shot — config applies live so no polling needed).
if (typeof window !== 'undefined') {
  void useSpottingStore.getState().refreshStatus();
}
