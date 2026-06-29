// SPDX-License-Identifier: GPL-2.0-or-later
//
// N1MM-format UDP broadcaster store. The backend (N1mmBroadcaster on disk) is
// the source of truth: the form initialises from GET /api/n1mm/config once it
// arrives. Like the WSJT-X store, do NOT seed from localStorage and do NOT
// auto-POST on load. Egress is OFF by default.

import { create } from 'zustand';
import {
  getN1mmConfig,
  postN1mmConfig,
  N1MM_DEFAULT_PORT,
  type N1mmConfig,
} from '../api/n1mm';

const DEFAULT_CONFIG: N1mmConfig = {
  enabled: false,
  host: '127.0.0.1',
  port: N1MM_DEFAULT_PORT,
};

export type N1mmStoreState = {
  config: N1mmConfig;
  refreshConfig: () => Promise<void>;
  saveConfig: (cfg: N1mmConfig) => Promise<N1mmConfig>;
};

export const useN1mmStore = create<N1mmStoreState>((set) => ({
  config: DEFAULT_CONFIG,

  refreshConfig: async () => {
    try {
      set({ config: await getN1mmConfig() });
    } catch {
      /* transient — next refresh recovers */
    }
  },

  saveConfig: async (cfg) => {
    const saved = await postN1mmConfig(cfg);
    set({ config: saved });
    return saved;
  },
}));

// Initial config probe (one-shot — config applies live so no polling needed).
if (typeof window !== 'undefined') {
  void useN1mmStore.getState().refreshConfig();
}
