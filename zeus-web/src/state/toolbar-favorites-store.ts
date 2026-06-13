// SPDX-License-Identifier: GPL-2.0-or-later
//
// Per-toolbar favorite-slot store for the Mode/Band/Step pickers in the
// control strip. Each kind keeps an ordered list of three slot keys; the
// dropdown lets the operator drag any option onto a slot to pin it. Step
// also stores the currently-selected step value so the toolbar widget and
// the side-stack widget agree on a single tuning step.
//
// Persistence lives on the BACKEND (zeus-prefs.db via /api/toolbar-settings),
// not browser localStorage. The Photino desktop webview binds a fresh
// OS-assigned random loopback port on every launch, so a per-origin
// localStorage value was orphaned each launch and the tuning step reset to
// its 500 Hz default. Server-side storage survives the port shuffle and a
// backend restart, and follows the operator across every browser pointed at
// the Zeus instance. Same pattern as display-settings-store.ts.

import { create } from 'zustand';
import { fetchToolbarSettings, updateToolbarSettings } from '../api/toolbar-settings';

export type ToolbarFavKind = 'mode' | 'band' | 'step';

const DEFAULT_MODE: readonly string[] = ['USB', 'LSB', 'CWU'];
const DEFAULT_BAND: readonly string[] = ['40m', '20m', '15m'];
const DEFAULT_STEP: readonly string[] = ['100', '500', '1000'];
const DEFAULT_STEP_HZ = 500;

type ToolbarFavoritesState = {
  mode: string[];
  band: string[];
  step: string[];
  stepHz: number;
  setFavorites: (kind: ToolbarFavKind, slots: string[]) => void;
  setStepHz: (hz: number) => void;
};

// Debounced server save. setStepHz / setFavorites can fire several times in
// quick succession (e.g. spinning the step picker); batch into a single POST
// after a 1 s quiet period, the same debounce display-settings-store.ts uses
// for dB-range saves.
let saveTimer: ReturnType<typeof setTimeout> | null = null;

function scheduleSave(): void {
  if (saveTimer) clearTimeout(saveTimer);
  saveTimer = setTimeout(() => {
    const s = useToolbarFavoritesStore.getState();
    void updateToolbarSettings({
      mode: s.mode,
      band: s.band,
      step: s.step,
      stepHz: s.stepHz,
    });
  }, 1000);
}

export const useToolbarFavoritesStore = create<ToolbarFavoritesState>()((set) => ({
  // Defaults until the server-side fetch lands (see hydrateFromServer at the
  // bottom of this file). On first interaction before hydration completes the
  // operator briefly sees the defaults — acceptable, same first-paint
  // trade-off as display-settings-store.ts.
  mode: [...DEFAULT_MODE],
  band: [...DEFAULT_BAND],
  step: [...DEFAULT_STEP],
  stepHz: DEFAULT_STEP_HZ,
  setFavorites: (kind, slots) => {
    if (slots.length !== 3) return;
    if (kind === 'mode') set({ mode: slots });
    else if (kind === 'band') set({ band: slots });
    else if (kind === 'step') set({ step: slots });
    scheduleSave();
  },
  setStepHz: (hz) => {
    set({ stepHz: hz });
    scheduleSave();
  },
}));

// One-shot hydration from the backend at module load. Server values of null
// mean the field was never stored (fresh install / first run after upgrade);
// keep the in-memory defaults in that case. When the server has nothing at
// all, push the current defaults up once so subsequent restarts find them
// persisted.
async function hydrateFromServer(): Promise<void> {
  let server: Awaited<ReturnType<typeof fetchToolbarSettings>>;
  try {
    server = await fetchToolbarSettings();
  } catch {
    // Backend unreachable; leave defaults in place. The next setStepHz /
    // setFavorites will POST to the server.
    return;
  }

  useToolbarFavoritesStore.setState({
    ...(server.mode ? { mode: server.mode } : {}),
    ...(server.band ? { band: server.band } : {}),
    ...(server.step ? { step: server.step } : {}),
    ...(server.stepHz !== null ? { stepHz: server.stepHz } : {}),
  });

  // Nothing stored server-side yet — push the current values (server's or our
  // defaults) up so the next restart finds them persisted. One-time migration
  // for operators upgrading from localStorage-only storage.
  if (server.stepHz === null) {
    scheduleSave();
  }
}

void hydrateFromServer();
