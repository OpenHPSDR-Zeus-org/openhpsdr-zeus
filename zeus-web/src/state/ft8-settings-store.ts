// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Persisted FT8/FT4/WSPR workspace preferences (auto-seq behaviour, decode depth,
// editable macros, logging prefs, and the per-mode waterfall/display view).
// Server-backed via /api/ft8/settings?mode= so the operator's choices survive
// desktop restarts — the old client-only state was lost with the webview's
// port-scoped storage.
//
// PER-MODE: FT8, FT4 and WSPR each keep an independent record (keyed by mode),
// so a change to one never bleeds into the others. The backend is the source of
// truth: each mode hydrates from GET on demand and NEVER auto-POSTs (same lesson
// as TCI/WSJT-X/spotting) — only an explicit operator edit writes. Defaults
// mirror Zeus.Contracts.Ft8Settings exactly, so nothing an operator feels changes
// until they touch a control.

import { create } from 'zustand';
import {
  DIGITAL_MODES,
  FT8_SETTINGS_DEFAULTS,
  getFt8Settings,
  postFt8Settings,
  type DigitalMode,
  type Ft8Settings,
} from '../api/ft8-settings';
import { useFt8Store } from './ft8-store';

type ByMode<T> = Record<DigitalMode, T>;

function seedByMode<T>(value: T): ByMode<T> {
  return { FT8: value, FT4: value, WSPR: value };
}

interface Ft8SettingsState {
  /** The persisted settings for each mode. Seeded with defaults until hydrated. */
  byMode: ByMode<Ft8Settings>;
  /** True once a mode's first server hydrate has completed (success or failure). */
  hydrated: ByMode<boolean>;

  hydrate: (mode: DigitalMode, signal?: AbortSignal) => Promise<void>;
  /** Merge a partial change into a mode's settings and persist it to the server.
   *  Optimistic: the local value updates immediately; the server response (the
   *  normalized/clamped settings) reconciles. */
  update: (mode: DigitalMode, patch: Partial<Ft8Settings>) => Promise<void>;
}

// Seed the live FT8/FT4 decoder pass count from a freshly-hydrated/edited mode's
// depth, but ONLY when that mode is the one currently engaged in the pop-out
// (WSPR has no shared-engine pass concept). Keeps the operator's decode-depth
// choice in effect the moment they touch it, without restarting the decoder for
// a mode that isn't on the air.
function seedEngineDepthIfActive(mode: DigitalMode, decodePasses: number): void {
  if (mode === 'WSPR') return;
  if (mode !== useFt8Store.getState().protocol) return;
  if (decodePasses !== useFt8Store.getState().passes) {
    useFt8Store.getState().setPasses(decodePasses);
  }
}

export const useFt8SettingsStore = create<Ft8SettingsState>((set, get) => ({
  byMode: seedByMode(FT8_SETTINGS_DEFAULTS),
  hydrated: seedByMode(false),

  hydrate: async (mode, signal) => {
    try {
      const settings = await getFt8Settings(mode, signal);
      set((s) => ({
        byMode: { ...s.byMode, [mode]: settings },
        hydrated: { ...s.hydrated, [mode]: true },
      }));
      seedEngineDepthIfActive(mode, settings.decodePasses);
    } catch {
      set((s) => ({ hydrated: { ...s.hydrated, [mode]: true } }));
    }
  },

  update: async (mode, patch) => {
    const next = { ...get().byMode[mode], ...patch };
    set((s) => ({ byMode: { ...s.byMode, [mode]: next } })); // optimistic
    try {
      const saved = await postFt8Settings(mode, next);
      set((s) => ({ byMode: { ...s.byMode, [mode]: saved } }));
    } catch {
      // Persist failed (offline) — optimistic value stands; a later hydrate
      // reconciles. Never throw into the UI for a settings toggle.
    }
  },
}));

// One-shot hydrate of every mode on module load (mirrors wsjtx-store /
// spotting-store) so the menu and pop-out show the operator's saved per-mode
// config immediately on first paint.
if (typeof window !== 'undefined') {
  for (const mode of DIGITAL_MODES) {
    void useFt8SettingsStore.getState().hydrate(mode);
  }
}
