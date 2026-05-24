// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

import { create } from 'zustand';
import {
  getRotatorConfig,
  getRotatorStatus,
  postRotatorConfig,
  setRotatorAz,
  stopRotator,
  testRotator,
  type RotctldConfig,
  type RotctldStatus,
  type RotctldTestResult,
} from '../api/rotator';

// Defaults match the backend record so the form has sensible values until the
// first /api/rotator/config response lands. The backend is the sole source of
// truth — config is persisted server-side in zeus-prefs.db, not in localStorage.
const DEFAULT_CONFIG: RotctldConfig = {
  enabled: false,
  host: '127.0.0.1',
  port: 4533,
  pollingIntervalMs: 500,
};

export type RotatorStoreState = {
  config: RotctldConfig;
  status: RotctldStatus | null;
  testInFlight: boolean;
  lastTestResult: RotctldTestResult | null;

  refreshConfig: () => Promise<void>;
  refreshStatus: () => Promise<void>;
  saveConfig: (cfg: RotctldConfig) => Promise<RotctldStatus>;
  setAzimuth: (az: number) => Promise<RotctldStatus | null>;
  stop: () => Promise<void>;
  test: (host: string, port: number) => Promise<RotctldTestResult>;
};

export const useRotatorStore = create<RotatorStoreState>((set) => ({
  config: DEFAULT_CONFIG,
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
    return status;
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
  void useRotatorStore.getState().refreshStatus();
  window.setInterval(() => {
    if (!useRotatorStore.getState().config.enabled) return;
    void useRotatorStore.getState().refreshStatus();
  }, 1000);
}
