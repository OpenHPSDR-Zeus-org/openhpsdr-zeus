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
  getCatSerialStatus,
  putCatSerialConfig,
  testCatSerialPort,
  defaultPortConfig,
  CAT_SERIAL_PORT_COUNT,
  type CatSerialPortConfig,
  type CatSerialStatus,
  type CatSerialPortStatus,
  type CatSerialTestResult,
} from '../api/catSerial';

// The backend (CatSerialConfigStore on disk) is the source of truth. The form
// hydrates from /api/cat/serial/status once it arrives. As in cat-store, do NOT
// seed from localStorage and do NOT auto-POST on load.
function defaultConfigs(): CatSerialPortConfig[] {
  return Array.from({ length: CAT_SERIAL_PORT_COUNT }, defaultPortConfig);
}

function stripConfig(p: CatSerialPortStatus): CatSerialPortConfig {
  return {
    enabled: p.enabled,
    portName: p.portName,
    baudRate: p.baudRate,
    parity: p.parity,
    dataBits: p.dataBits,
    stopBits: p.stopBits,
  };
}

function sameConfig(a: CatSerialPortConfig, b: CatSerialPortConfig): boolean {
  return a.enabled === b.enabled
    && a.portName === b.portName
    && a.baudRate === b.baudRate
    && a.parity === b.parity
    && a.dataBits === b.dataBits
    && a.stopBits === b.stopBits;
}

export type CatSerialTestState = { index: number; result: CatSerialTestResult };

export type CatSerialStoreState = {
  config: CatSerialPortConfig[];
  status: CatSerialStatus | null;
  testingIndex: number | null;
  lastTest: CatSerialTestState | null;

  refreshStatus: () => Promise<void>;
  saveConfig: (ports: CatSerialPortConfig[]) => Promise<CatSerialStatus>;
  test: (index: number, port: CatSerialPortConfig) => Promise<CatSerialTestResult>;
};

export const useCatSerialStore = create<CatSerialStoreState>((set, get) => ({
  config: defaultConfigs(),
  status: null,
  testingIndex: null,
  lastTest: null,

  refreshStatus: async () => {
    try {
      const status = await getCatSerialStatus();
      const current = get().config;
      const serverConfig = status.ports.map(stripConfig);
      // Form edits live in the panel's component-local state, NOT here — store
      // `config` only ever holds defaults / last-saved / adopted server truth.
      // So keep the SAME reference when the server already matches (the panel's
      // useEffect([config]) then won't refire and reset the form); only adopt a
      // new array when disk genuinely differs (external change / post-save truth).
      const synced = serverConfig.length === current.length
        && serverConfig.every((c, i) => current[i] !== undefined && sameConfig(c, current[i]!));
      set({ status, config: synced ? current : serverConfig });
    } catch {
      /* transient — next poll recovers */
    }
  },

  saveConfig: async (ports) => {
    const status = await putCatSerialConfig(ports);
    set({ config: ports, status });
    return status;
  },

  test: async (index, port) => {
    set({ testingIndex: index, lastTest: null });
    try {
      const result = await testCatSerialPort(port);
      set({ testingIndex: null, lastTest: { index, result } });
      return result;
    } catch (e) {
      const result: CatSerialTestResult = { ok: false, error: e instanceof Error ? e.message : 'test failed' };
      set({ testingIndex: null, lastTest: { index, result } });
      return result;
    }
  },
}));

// Initial probe + 2 s polling while the page is alive AND at least one serial
// port is enabled (nothing to poll otherwise).
if (typeof window !== 'undefined') {
  void useCatSerialStore.getState().refreshStatus();
  window.setInterval(() => {
    if (!useCatSerialStore.getState().config.some((p) => p.enabled)) return;
    void useCatSerialStore.getState().refreshStatus();
  }, 2000);
}
