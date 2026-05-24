// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
// See LICENSE for the full GPL text.

import { create } from 'zustand';
import {
  fetchPanWfSplit,
  updatePanWfSplit,
  PAN_WF_DEFAULT_PERCENT,
  PAN_WF_MIN_PERCENT,
  PAN_WF_MAX_PERCENT,
} from '../api/pan-wf-split';

// Vertical pan/wf split (panadapter share, 10..90). Persisted server-side
// in zeus-prefs.db (LiteDB) so the choice follows the operator across
// browsers / devices — same pattern as BottomPin. NO localStorage mirror
// per project_litedb_tx_filter_persistence / project_rotator_resume.
//
// Live drag: setPanPercentLive() updates the in-memory value at pointer
// rate so the canvases reflow immediately. Persistence is a debounced
// (~300 ms after drag-end) PUT via persistPanPercent(); we send a final
// PUT when the operator releases. The store never mirrors to
// localStorage.

type PanWfSplitState = {
  panPercent: number;
  hydrated: boolean;
  setPanPercentLive: (pct: number) => void;
  persistPanPercent: (pct: number) => Promise<void>;
};

function clamp(v: number): number {
  if (!Number.isFinite(v)) return PAN_WF_DEFAULT_PERCENT;
  if (v < PAN_WF_MIN_PERCENT) return PAN_WF_MIN_PERCENT;
  if (v > PAN_WF_MAX_PERCENT) return PAN_WF_MAX_PERCENT;
  return v;
}

let debounceTimer: ReturnType<typeof setTimeout> | null = null;

export const usePanWfSplitStore = create<PanWfSplitState>((set) => ({
  panPercent: PAN_WF_DEFAULT_PERCENT,
  hydrated: false,
  setPanPercentLive: (pct) => {
    set({ panPercent: clamp(pct) });
  },
  persistPanPercent: async (pct) => {
    const next = clamp(pct);
    set({ panPercent: next });
    if (debounceTimer !== null) {
      clearTimeout(debounceTimer);
      debounceTimer = null;
    }
    await new Promise<void>((resolve) => {
      debounceTimer = setTimeout(async () => {
        debounceTimer = null;
        try {
          const result = await updatePanWfSplit({ panPercent: next });
          set({ panPercent: result.panPercent });
        } catch {
          // Network / server failure — keep the live value; next drag will
          // try again. We deliberately do NOT roll back because the
          // operator's intent is the local value they just dragged to.
        }
        resolve();
      }, 300);
    });
  },
}));

async function hydrateFromServer(): Promise<void> {
  try {
    const server = await fetchPanWfSplit();
    usePanWfSplitStore.setState({ panPercent: server.panPercent, hydrated: true });
  } catch {
    // Backend unreachable — keep the default 50%. The store stays unhydrated
    // so the next successful fetch (or a drag-end PUT) can populate it.
    usePanWfSplitStore.setState({ hydrated: false });
  }
}

void hydrateFromServer();
