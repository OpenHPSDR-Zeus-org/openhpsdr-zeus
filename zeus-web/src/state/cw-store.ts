// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

import { create } from 'zustand';
import {
  DEFAULT_CW_SETTINGS,
  fetchCwSettings,
  saveCwSettings,
  type CwSettings,
} from '../api/cw';

// CW engine state — must match the byte values of the C# CwEngineState
// enum (Zeus.Contracts/CwEngineStatus.cs). Idle is the resting state with
// no MOX; Sending is mid-playback; Stopping/Aborting are transitional.
export type CwEngineState = 'idle' | 'sending' | 'stopping' | 'aborting';

export const CW_STATE_FROM_BYTE: Record<number, CwEngineState> = {
  0: 'idle',
  1: 'sending',
  2: 'stopping',
  3: 'aborting',
};

export type CwEngineStatus = {
  state: CwEngineState;
  /** Text currently being keyed out (empty when state is idle). */
  text: string;
  /** Playback WPM for the current message — needed by the macro pad to
   * estimate per-character progress without polling. */
  wpm: number;
  /** Jobs still waiting in the queue after the current one. 0 when idle. */
  queueDepth: number;
  /** Local timestamp (ms) when the most recent status frame arrived.
   * Used by the panel to highlight the active character based on elapsed
   * time × WPM — server only emits frames on state edges, so the per-
   * character animation is reconstructed client-side. */
  receivedAtMs: number;
};

const IDLE_STATUS: CwEngineStatus = {
  state: 'idle',
  text: '',
  wpm: 0,
  queueDepth: 0,
  receivedAtMs: 0,
};

type CwStore = {
  settings: CwSettings;
  /** Optimistic save: caller mutates the store immediately, then the PUT
   * either succeeds (server may normalise — re-apply the response) or
   * fails (revert). Use this for one-shot saves (macro edit, slider
   * release). For high-frequency updates (slider drag) use
   * <see cref="setSettingsLocal"/> + <see cref="commitDebounced"/>. */
  patchSettings: (patch: Partial<CwSettings>) => Promise<void>;
  /** Update settings in-memory only; does NOT round-trip to the server.
   * Used by the WPM slider during drag so the UI tracks the pointer
   * without a 100-PUT thundering herd. Pair with <see cref="commitDebounced"/>
   * to schedule the final save. */
  setSettingsLocal: (patch: Partial<CwSettings>) => void;
  /** Schedule a server PUT for the named field after a quiet period.
   * The latest scheduled value wins — earlier scheduled PUTs are
   * cancelled. Fixes the "slider snaps back" race where rapid PUTs
   * from drag could complete out of order. */
  commitDebounced: (patch: Partial<CwSettings>) => void;
  setMacro: (index: number, value: string) => Promise<void>;
  /** Append a new empty macro slot. The operator typically clicks edit
   * on the freshly-added slot to fill it in. */
  addMacro: () => Promise<void>;
  removeMacro: (index: number) => Promise<void>;
  status: CwEngineStatus;
  /** Called by the WS dispatcher on every CwEngineStatus frame. */
  setStatusFromServer: (s: CwEngineStatus) => void;
};

// Debounce timer for commitDebounced. Lives at module scope so a re-render
// of CwPanel doesn't reset it; we only ever want one PUT in flight per
// "burst" of operator input.
const DEBOUNCE_MS = 250;
let debounceTimer: ReturnType<typeof setTimeout> | null = null;

export const useCwStore = create<CwStore>((set, get) => ({
  // Mirror the API's defaults — backend Get() ships the same values on
  // a fresh install, so the UI doesn't flicker through "empty" while the
  // hydrate fetch is in flight.
  settings: DEFAULT_CW_SETTINGS,
  patchSettings: async (patch) => {
    const prev = get().settings;
    const next = { ...prev, ...patch };
    set({ settings: next });
    try {
      const server = await saveCwSettings(patch);
      // Server may normalise (clamp WPM, truncate a long macro, etc.).
      // Re-apply only if it actually changed something, to avoid an extra
      // render on the common no-op-normalisation path.
      if (!shallowEqualCwSettings(server, next)) {
        set({ settings: server });
      }
    } catch {
      // Roll back the optimistic update so the UI stays consistent with
      // what the server actually persisted. The user will see their edit
      // revert, which is the honest signal that the save failed.
      set({ settings: prev });
    }
  },
  setSettingsLocal: (patch) => {
    set((s) => ({ settings: { ...s.settings, ...patch } }));
  },
  commitDebounced: (patch) => {
    if (debounceTimer !== null) clearTimeout(debounceTimer);
    debounceTimer = setTimeout(() => {
      debounceTimer = null;
      // Don't roll back on debounced saves — the operator has moved on,
      // and a stale rollback that flips the slider back mid-typing would
      // be more disorienting than a silent failure. The next save will
      // retry; if the backend is truly down, the operator's next page
      // load will resync from the persisted state.
      void saveCwSettings(patch).catch(() => undefined);
    }, DEBOUNCE_MS);
  },
  setMacro: async (index, value) => {
    const macros = [...get().settings.macros];
    macros[index] = value;
    await get().patchSettings({ macros });
  },
  addMacro: async () => {
    const macros = [...get().settings.macros, ''];
    await get().patchSettings({ macros });
  },
  removeMacro: async (index) => {
    const macros = get().settings.macros.filter((_, i) => i !== index);
    await get().patchSettings({ macros });
  },
  status: IDLE_STATUS,
  setStatusFromServer: (s) => set({ status: s }),
}));

function shallowEqualCwSettings(a: CwSettings, b: CwSettings): boolean {
  if (
    a.wpm !== b.wpm ||
    a.farnsworthWpm !== b.farnsworthWpm ||
    a.sidetoneGainDb !== b.sidetoneGainDb ||
    a.sidetoneHz !== b.sidetoneHz
  )
    return false;
  if (a.macros.length !== b.macros.length) return false;
  for (let i = 0; i < a.macros.length; i++) {
    if (a.macros[i] !== b.macros[i]) return false;
  }
  return true;
}

// Hydrate on module load. Same pattern as bottom-pin-store; tolerates a
// failed fetch by leaving the defaults in place — subsequent patchSettings
// will retry via PUT.
export async function hydrateCwSettings(): Promise<void> {
  try {
    const server = await fetchCwSettings();
    useCwStore.setState({ settings: server });
  } catch {
    /* network failure — keep defaults */
  }
}

void hydrateCwSettings();
