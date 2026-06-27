// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Persisted FT8/FT4 workspace preferences (auto-seq behaviour, decode depth,
// editable macros, logging prefs). Server-backed via /api/ft8/settings so the
// operator's choices survive desktop restarts — the old client-only state was
// lost with the webview's port-scoped storage.
//
// The backend (Ft8SettingsStore on disk) is the source of truth: the store
// hydrates from GET on load and NEVER auto-POSTs (same lesson as TCI/WSJT-X/
// spotting) — only an explicit operator edit writes. Defaults mirror
// Zeus.Contracts.Ft8Settings exactly, so nothing an operator feels changes until
// they touch a control.

import { create } from 'zustand';
import {
  FT8_SETTINGS_DEFAULTS,
  getFt8Settings,
  postFt8Settings,
  type Ft8Settings,
} from '../api/ft8-settings';
import { useFt8Store } from './ft8-store';

interface Ft8SettingsState {
  settings: Ft8Settings;
  /** True once the first server hydrate has completed (success or failure). */
  hydrated: boolean;

  hydrate: (signal?: AbortSignal) => Promise<void>;
  /** Merge a partial change into the current settings and persist to the server.
   *  Optimistic: the local value updates immediately; the server response (the
   *  normalized/clamped settings) reconciles. */
  update: (patch: Partial<Ft8Settings>) => Promise<void>;
}

export const useFt8SettingsStore = create<Ft8SettingsState>((set, get) => ({
  settings: FT8_SETTINGS_DEFAULTS,
  hydrated: false,

  hydrate: async (signal) => {
    try {
      const settings = await getFt8Settings(signal);
      set({ settings, hydrated: true });
      // Seed the live decoder pass count from the persisted depth so the
      // operator's choice is in effect the moment they open the workspace. Only
      // push when it actually differs from the engine's current value — the
      // default (3) already matches Ft8Service.DecodePasses, so a fresh operator
      // never has the decoder restarted at a shallower depth than before.
      if (settings.decodePasses !== useFt8Store.getState().passes) {
        useFt8Store.getState().setPasses(settings.decodePasses);
      }
    } catch {
      set({ hydrated: true });
    }
  },

  update: async (patch) => {
    const next = { ...get().settings, ...patch };
    set({ settings: next }); // optimistic
    try {
      const saved = await postFt8Settings(next);
      set({ settings: saved });
    } catch {
      // Persist failed (offline) — optimistic value stands; a later hydrate
      // reconciles. Never throw into the UI for a settings toggle.
    }
  },
}));

// One-shot hydrate on module load (mirrors wsjtx-store / spotting-store).
if (typeof window !== 'undefined') {
  void useFt8SettingsStore.getState().hydrate();
}
