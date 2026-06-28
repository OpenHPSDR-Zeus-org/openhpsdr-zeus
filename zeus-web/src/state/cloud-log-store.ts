// SPDX-License-Identifier: GPL-2.0-or-later
//
// HTTP cloud-log uploader store (Wavelog/Cloudlog + Club Log realtime). The
// backend (CloudLogConfigStore + CredentialStore) is the source of truth: the
// form initialises from GET /api/log/cloud/config once it arrives. Secrets are
// WRITE-ONLY — the status only ever reports presence booleans, never the keys —
// so credentials POST through a separate endpoint. Egress is OFF by default.

import { create } from 'zustand';
import {
  getCloudLogStatus,
  postCloudLogConfig,
  postCloudLogCredentials,
  type CloudLogConfig,
  type CloudLogCredentials,
  type CloudLogStatus,
} from '../api/cloud-log';

const DEFAULT_STATUS: CloudLogStatus = {
  wavelog: { enabled: false, baseUrl: '', stationProfileId: '', hasApiKey: false },
  clubLog: { enabled: false, email: '', callsign: '', hasPassword: false, hasApiKey: false },
};

export type CloudLogStoreState = {
  status: CloudLogStatus;
  refreshStatus: () => Promise<void>;
  saveConfig: (cfg: CloudLogConfig) => Promise<CloudLogStatus>;
  saveCredentials: (creds: CloudLogCredentials) => Promise<CloudLogStatus>;
};

export const useCloudLogStore = create<CloudLogStoreState>((set) => ({
  status: DEFAULT_STATUS,

  refreshStatus: async () => {
    try {
      set({ status: await getCloudLogStatus() });
    } catch {
      /* transient — next refresh recovers */
    }
  },

  saveConfig: async (cfg) => {
    const status = await postCloudLogConfig(cfg);
    set({ status });
    return status;
  },

  saveCredentials: async (creds) => {
    const status = await postCloudLogCredentials(creds);
    set({ status });
    return status;
  },
}));

// Initial status probe (one-shot — config applies live so no polling needed).
if (typeof window !== 'undefined') {
  void useCloudLogStore.getState().refreshStatus();
}
