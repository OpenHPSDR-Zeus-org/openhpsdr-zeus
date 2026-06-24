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

import { create } from 'zustand';
import {
  getRotatorConfig,
  getRotatorMultiConfig,
  getRotatorStatus,
  postRotatorConfig,
  postRotatorMultiConfig,
  setRotatorActiveSlot,
  setRotatorAz,
  stopRotator,
  testRotator,
  type RotctldConfig,
  type RotctldMultiConfig,
  type RotctldStatus,
  type RotctldTestResult,
} from '../api/rotator';

// HF band names — keep in sync with BandUtils.HfBands on the backend.
export const ROTATOR_BANDS: ReadonlyArray<string> = [
  '160m', '80m', '60m', '40m', '30m', '20m', '17m', '15m', '12m', '10m', '6m',
];

// Defaults match the backend record so the form has sensible values until the
// first /api/rotator/config response lands. The backend is the sole source of
// truth — config is persisted server-side in zeus-prefs.db, not in localStorage.
const DEFAULT_CONFIG: RotctldConfig = {
  enabled: false,
  host: '127.0.0.1',
  port: 4533,
  pollingIntervalMs: 500,
};

const DEFAULT_MULTI: RotctldMultiConfig = {
  activeSlotId: 1,
  autoRoute: false,
  slots: [
    {
      id: 1,
      label: 'Rotator 1',
      enabled: false,
      host: '127.0.0.1',
      port: 4533,
      bands: [...ROTATOR_BANDS],
      pollingIntervalMs: 500,
    },
  ],
};

export type RotatorStoreState = {
  config: RotctldConfig;
  multi: RotctldMultiConfig;
  status: RotctldStatus | null;
  testInFlight: boolean;
  lastTestResult: RotctldTestResult | null;

  refreshConfig: () => Promise<void>;
  refreshMultiConfig: () => Promise<void>;
  refreshStatus: () => Promise<void>;
  saveConfig: (cfg: RotctldConfig) => Promise<RotctldStatus>;
  saveMultiConfig: (cfg: RotctldMultiConfig) => Promise<RotctldMultiConfig>;
  setActiveSlot: (slotId: number) => Promise<RotctldStatus | null>;
  setAzimuth: (az: number) => Promise<RotctldStatus | null>;
  stop: () => Promise<void>;
  test: (host: string, port: number) => Promise<RotctldTestResult>;
};

export const useRotatorStore = create<RotatorStoreState>((set) => ({
  config: DEFAULT_CONFIG,
  multi: DEFAULT_MULTI,
  status: null,
  testInFlight: false,
  lastTestResult: null,

  refreshConfig: async () => {
    try {
      const config = await getRotatorConfig();
      set({ config });
    } catch {
      /* transient — leave defaults in place */
    }
  },

  refreshMultiConfig: async () => {
    try {
      const multi = await getRotatorMultiConfig();
      set({ multi });
    } catch {
      /* transient — leave defaults in place */
    }
  },

  refreshStatus: async () => {
    try {
      const status = await getRotatorStatus();
      set({ status });
    } catch {
      /* transient — next poll recovers */
    }
  },

  saveConfig: async (cfg) => {
    const status = await postRotatorConfig(cfg);
    set({ config: cfg, status });
    // Active slot's host/port changed — re-hydrate the multi-slot snapshot too.
    void useRotatorStore.getState().refreshMultiConfig();
    return status;
  },

  saveMultiConfig: async (cfg) => {
    const multi = await postRotatorMultiConfig(cfg);
    set({ multi });
    // Status reflects the (possibly new) active slot.
    void useRotatorStore.getState().refreshStatus();
    void useRotatorStore.getState().refreshConfig();
    return multi;
  },

  setActiveSlot: async (slotId) => {
    try {
      const status = await setRotatorActiveSlot(slotId);
      set({ status });
      void useRotatorStore.getState().refreshMultiConfig();
      void useRotatorStore.getState().refreshConfig();
      return status;
    } catch {
      return null;
    }
  },

  setAzimuth: async (az) => {
    try {
      const status = await setRotatorAz(az);
      set({ status });
      return status;
    } catch {
      return null;
    }
  },

  stop: async () => {
    try {
      const status = await stopRotator();
      set({ status });
    } catch {
      /* ignore */
    }
  },

  test: async (host, port) => {
    set({ testInFlight: true, lastTestResult: null });
    const result = await testRotator(host, port);
    set({ testInFlight: false, lastTestResult: result });
    return result;
  },
}));

// Hydrate config + status from the backend at module load, then poll status at
// 1 s while the page is alive AND rotctld is enabled. When disabled there's
// nothing to reconcile — skip the fetch to avoid an idle-RX HTTP wakeup.
//
// Note: we deliberately do NOT POST anything on load. The backend hydrates its
// own config from LiteDB at startup; pushing a cached client copy here would
// race that hydration and re-enable a rotator the operator already turned off.
if (typeof window !== 'undefined') {
  void useRotatorStore.getState().refreshConfig();
  void useRotatorStore.getState().refreshMultiConfig();
  void useRotatorStore.getState().refreshStatus();
  window.setInterval(() => {
    // Poll while ANY slot is enabled — not just the active one. In a
    // multi-rotator setup the active slot can be disabled (e.g. auto-route
    // hasn't switched yet) while another slot is live; gating on the active
    // slot alone would freeze the status readout exactly in the case this
    // feature exists for.
    const st = useRotatorStore.getState();
    if (!st.config.enabled && !st.multi.slots.some((s) => s.enabled)) return;
    void useRotatorStore.getState().refreshStatus();
  }, 1000);
}
