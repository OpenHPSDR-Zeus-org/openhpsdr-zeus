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
  getWsjtxStatus,
  postWsjtxConfig,
  type WsjtxConfig,
  type WsjtxStatus,
} from '../api/wsjtx';

// The backend (WsjtxConfigStore on disk) is the source of truth. The form
// initialises from /api/wsjtx/status once it arrives. Do NOT seed from
// localStorage and do NOT auto-POST on load (same lesson as TCI/CAT).
const DEFAULT_CONFIG: WsjtxConfig = {
  enabled: false,
  host: '127.0.0.1',
  port: 2237,
};

export type WsjtxStoreState = {
  config: WsjtxConfig;
  status: WsjtxStatus | null;

  refreshStatus: () => Promise<void>;
  saveConfig: (cfg: WsjtxConfig) => Promise<WsjtxStatus>;
};

export const useWsjtxStore = create<WsjtxStoreState>((set, get) => ({
  config: DEFAULT_CONFIG,
  status: null,

  refreshStatus: async () => {
    try {
      const status = await getWsjtxStatus();
      const current = get().config;
      const synced =
        current.enabled === status.enabled &&
        current.host === status.host &&
        current.port === status.port;
      set({
        status,
        config: synced
          ? current
          : { enabled: status.enabled, host: status.host, port: status.port },
      });
    } catch {
      /* transient — next refresh recovers */
    }
  },

  saveConfig: async (cfg) => {
    const status = await postWsjtxConfig(cfg);
    set({ config: cfg, status });
    return status;
  },
}));

// Initial status probe (one-shot — config applies live so no polling needed).
if (typeof window !== 'undefined') {
  void useWsjtxStore.getState().refreshStatus();
}
