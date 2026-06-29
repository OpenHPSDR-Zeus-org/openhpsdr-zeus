// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

import { create } from 'zustand';
import {
  getDxClusterStatus,
  putDxClusterConfig,
  connectDxCluster,
  disconnectDxCluster,
  type DxClusterConfig,
  type DxClusterStatus,
} from '../api/dxcluster';

// The backend (DxClusterConfigStore on disk) is the source of truth. The form
// initialises from /api/dxcluster/status once it arrives; this constant is only
// a transient placeholder. The password is never returned by the API (only
// hasPassword), so the local password field stays whatever the operator typed.
const DEFAULT_CONFIG: DxClusterConfig = {
  enabled: false,
  host: '',
  port: 7373,
  callsign: '',
  password: '',
  loginCommands: '',
  autoConnect: false,
};

export type DxClusterStoreState = {
  config: DxClusterConfig;
  status: DxClusterStatus | null;

  refreshStatus: () => Promise<void>;
  saveConfig: (cfg: DxClusterConfig) => Promise<DxClusterStatus>;
  connect: () => Promise<DxClusterStatus>;
  disconnect: () => Promise<DxClusterStatus>;
};

export const useDxClusterStore = create<DxClusterStoreState>((set, get) => ({
  config: DEFAULT_CONFIG,
  status: null,

  refreshStatus: async () => {
    try {
      const status = await getDxClusterStatus();
      // Sync non-secret config fields from the backend so the form never
      // disagrees with disk. Preserve the locally-typed password (the API
      // never echoes it back) and skip the sync if the operator has unsaved
      // edits to the synced fields.
      const current = get().config;
      const synced =
        current.enabled === status.enabled &&
        current.host === status.host &&
        current.port === status.port &&
        current.callsign === status.callsign &&
        current.loginCommands === status.loginCommands &&
        current.autoConnect === status.autoConnect;
      set({
        status,
        config: synced
          ? current
          : {
              enabled: status.enabled,
              host: status.host,
              port: status.port,
              callsign: status.callsign,
              password: current.password,
              loginCommands: status.loginCommands,
              autoConnect: status.autoConnect,
            },
      });
    } catch {
      /* transient — next poll recovers */
    }
  },

  saveConfig: async (cfg) => {
    const status = await putDxClusterConfig(cfg);
    set({ config: cfg, status });
    return status;
  },

  connect: async () => {
    const status = await connectDxCluster();
    set({ status });
    return status;
  },

  disconnect: async () => {
    const status = await disconnectDxCluster();
    set({ status });
    return status;
  },
}));

// Initial status probe + 3 s polling while the page is alive AND the cluster is
// enabled. Status changes drive the panel's connection indicator / spot count.
if (typeof window !== 'undefined') {
  void useDxClusterStore.getState().refreshStatus();
  window.setInterval(() => {
    if (!useDxClusterStore.getState().config.enabled) return;
    void useDxClusterStore.getState().refreshStatus();
  }, 3000);
}
