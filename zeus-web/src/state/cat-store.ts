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
  getCatStatus,
  postCatConfig,
  testCatPort,
  type CatConfig,
  type CatStatus,
  type CatTestResult,
} from '../api/cat';

// The backend (CatConfigStore on disk) is the source of truth for CAT runtime
// config. The form initialises from /api/cat/status once it arrives; this
// constant is only a transient placeholder until the first status refresh.
// Do NOT seed from localStorage and do NOT auto-POST on load — that produced a
// phantom "configuration changed — restart" warning every page load in TCI
// when localStorage drifted from disk (same lesson).
const DEFAULT_CONFIG: CatConfig = {
  enabled: false,
  bindAddress: '127.0.0.1',
  port: 19090,
};

export type CatStoreState = {
  config: CatConfig;
  status: CatStatus | null;
  testInFlight: boolean;
  lastTestResult: CatTestResult | null;

  refreshStatus: () => Promise<void>;
  saveConfig: (cfg: CatConfig) => Promise<CatStatus>;
  test: (bindAddress: string, port: number) => Promise<CatTestResult>;
};

export const useCatStore = create<CatStoreState>((set, get) => ({
  config: DEFAULT_CONFIG,
  status: null,
  testInFlight: false,
  lastTestResult: null,

  refreshStatus: async () => {
    try {
      const status = await getCatStatus();
      const current = get().config;
      const synced =
        current.enabled === status.pendingEnabled
          && current.bindAddress === status.pendingBindAddress
          && current.port === status.pendingPort;
      set({
        status,
        config: synced ? current : {
          enabled: status.pendingEnabled,
          bindAddress: status.pendingBindAddress,
          port: status.pendingPort,
        },
      });
    } catch {
      /* transient — next poll recovers */
    }
  },

  saveConfig: async (cfg) => {
    const status = await postCatConfig(cfg);
    set({ config: cfg, status });
    return status;
  },

  test: async (bindAddress, port) => {
    set({ testInFlight: true, lastTestResult: null });
    const result = await testCatPort(bindAddress, port);
    set({ testInFlight: false, lastTestResult: result });
    return result;
  },
}));

// Initial status probe + 2 s polling while the page is alive AND CAT is
// enabled (skip the fetch in the disabled-default case — nothing to show).
if (typeof window !== 'undefined') {
  void useCatStore.getState().refreshStatus();
  window.setInterval(() => {
    if (!useCatStore.getState().config.enabled) return;
    void useCatStore.getState().refreshStatus();
  }, 2000);
}
