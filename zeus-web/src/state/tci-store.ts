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
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

import { create } from 'zustand';
import {
  getTciStatus,
  postTciConfig,
  testTciPort,
  type TciConfig,
  type TciStatus,
  type TciTestResult,
} from '../api/tci';

// The backend (TciConfigStore on disk) is the source of truth for TCI runtime
// config. The form initialises from /api/tci/status once it arrives; this
// constant is only used as a transient placeholder until the first status
// refresh completes. Do NOT seed from localStorage and do NOT auto-POST on
// load — that produced a phantom "configuration changed — restart" warning
// every page load when localStorage drifted from disk.
const DEFAULT_CONFIG: TciConfig = {
  enabled: false,
  bindAddress: '127.0.0.1',
  port: 40001,
};

export type TciStoreState = {
  config: TciConfig;
  status: TciStatus | null;
  testInFlight: boolean;
  lastTestResult: TciTestResult | null;

  refreshStatus: () => Promise<void>;
  saveConfig: (cfg: TciConfig) => Promise<TciStatus>;
  test: (bindAddress: string, port: number) => Promise<TciTestResult>;
};

export const useTciStore = create<TciStoreState>((set, get) => ({
  config: DEFAULT_CONFIG,
  status: null,
  testInFlight: false,
  lastTestResult: null,

  refreshStatus: async () => {
    try {
      const status = await getTciStatus();
      // Sync the form-default config from the backend's pending values
      // (what will be applied on next restart) so the panel never shows a
      // value that disagrees with disk-persisted state. Skip the sync if
      // the user has unsaved local edits — saveConfig already pushed those
      // and we don't want to clobber them mid-edit.
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
    const status = await postTciConfig(cfg);
    set({ config: cfg, status });
    return status;
  },

  test: async (bindAddress, port) => {
    set({ testInFlight: true, lastTestResult: null });
    const result = await testTciPort(bindAddress, port);
    set({ testInFlight: false, lastTestResult: result });
    return result;
  },
}));

// Initial status probe at module load + 2 s polling while the page is alive
// AND TCI is enabled. Status changes drive the panel's RequiresRestart flag
// and client count — neither matters when the listener isn't running, so skip
// the fetch in the disabled-default case.
if (typeof window !== 'undefined') {
  void useTciStore.getState().refreshStatus();
  window.setInterval(() => {
    if (!useTciStore.getState().config.enabled) return;
    void useTciStore.getState().refreshStatus();
  }, 2000);
}
