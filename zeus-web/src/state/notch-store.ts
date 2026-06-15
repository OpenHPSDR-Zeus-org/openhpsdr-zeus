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

// Manual and auto notch filters ("dynamically notch out EMF").
//
// A notch is an absolute-frequency band the operator paints onto the spectrum
// or Signal Intelligence detects as a persistent narrow EMF bar. NotchOverlay
// masks the band out of the panadapter/waterfall and the backend applies the
// same list to WDSP manual-notch filters so the interference is removed from
// the audio too.
//
// Notches are absolute Hz (not display-relative), so they stay glued to the
// interference across tuning. Persisted server-side; localStorage is only a
// browser cache / legacy migration fallback. `armed` and `pending` are
// transient UI state, never persisted.

import { create } from 'zustand';
import { useDisplayStore } from './display-store';

export type Notch = {
  id: string;
  centerHz: number;
  widthHz: number;
  source?: 'manual' | 'auto';
};

function toWireNotches(notches: Notch[]) {
  return notches.map((n) => ({
    centerHz: n.centerHz,
    widthHz: n.widthHz,
    active: true,
    source: n.source ?? 'manual',
  }));
}

async function postNotches(notches: Notch[]): Promise<void> {
  if (typeof fetch !== 'function') return;
  await fetch('/api/rx/notches', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ notches: toWireNotches(notches) }),
  });
}

// Push the full notch list to the backend, which persists it and applies it to
// WDSP's manual-notch database. Fire-and-forget so a transport hiccup cannot
// break the drawing interaction.
function doPush(): void {
  try {
    void postNotches(useNotchStore.getState().notches).catch(() => {});
  } catch {
    // never let a notch UI action throw on a transport hiccup.
  }
}

// Trailing debounce so a live edge-resize drag (many updateNotch calls per
// second) coalesces into one POST instead of flooding the WDSP rewrite path.
let pushTimer: ReturnType<typeof setTimeout> | null = null;
function pushToBackend(): void {
  if (pushTimer) clearTimeout(pushTimer);
  pushTimer = setTimeout(() => {
    pushTimer = null;
    doPush();
  }, 120);
}

// Floor on notch width so a stray click doesn't create an unusably thin (or
// zero-width) notch. EMF birdies are narrow; voice-wide notches are the upper
// end of normal use.
const MIN_NOTCH_WIDTH_HZ = 20;
const STORAGE_KEY = 'zeus.notches';

let idCounter = 0;
function nextId(): string {
  idCounter += 1;
  return `n${idCounter}`;
}

function readPersisted(): Notch[] {
  try {
    if (typeof localStorage === 'undefined') return [];
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return [];
    return normalizeNotches(JSON.parse(raw), false);
  } catch {
    return [];
  }
}

function normalizeNotches(raw: unknown, serverPayload: boolean): Notch[] {
  if (!Array.isArray(raw)) return [];
  const out: Notch[] = [];
  for (const item of raw) {
    const row = item as { centerHz?: unknown; widthHz?: unknown; active?: unknown; source?: unknown };
    if (serverPayload && row.active === false) continue;
    const c = row.centerHz;
    const w = row.widthHz;
    if (typeof c === 'number' && typeof w === 'number' && Number.isFinite(c) && Number.isFinite(w)) {
      const source = row.source === 'auto' ? 'auto' : undefined;
      out.push({ id: nextId(), centerHz: c, widthHz: Math.max(MIN_NOTCH_WIDTH_HZ, w), source });
    }
  }
  return out;
}

function persist(notches: Notch[]): void {
  try {
    if (typeof localStorage === 'undefined') return;
    localStorage.setItem(
      STORAGE_KEY,
      JSON.stringify(notches.map((n) => ({ centerHz: n.centerHz, widthHz: n.widthHz, source: n.source }))),
    );
  } catch {
    // quota / private mode — in-memory state remains the source of truth.
  }
}

export type NotchState = {
  notches: Notch[];
  /** When armed, a spectrum drag paints a new notch instead of tuning. */
  armed: boolean;
  /** Live preview band while dragging a new notch (not persisted). */
  pending: { centerHz: number; widthHz: number } | null;
  addNotch: (centerHz: number, widthHz: number) => void;
  /** Resize/move an existing notch (used by the waterfall edge-drag handles). */
  updateNotch: (id: string, centerHz: number, widthHz: number) => void;
  removeNotch: (id: string) => void;
  clearAll: () => void;
  replaceAutoNotches: (notches: Array<{ centerHz: number; widthHz: number }>) => void;
  clearAutoNotches: () => void;
  setArmed: (v: boolean) => void;
  toggleArmed: () => void;
  setPending: (p: { centerHz: number; widthHz: number } | null) => void;
};

export const useNotchStore = create<NotchState>((set, get) => ({
  notches: readPersisted(),
  armed: false,
  pending: null,
  addNotch: (centerHz, widthHz) => {
    if (!Number.isFinite(centerHz) || !Number.isFinite(widthHz)) return;
    const notch: Notch = { id: nextId(), centerHz, widthHz: Math.max(MIN_NOTCH_WIDTH_HZ, widthHz) };
    const notches = [...get().notches, notch];
    set({ notches });
    persist(notches);
    pushToBackend();
  },
  updateNotch: (id, centerHz, widthHz) => {
    if (!Number.isFinite(centerHz) || !Number.isFinite(widthHz)) return;
    const w = Math.max(MIN_NOTCH_WIDTH_HZ, widthHz);
    const notches = get().notches.map((n) => (n.id === id ? { ...n, centerHz, widthHz: w } : n));
    set({ notches });
    persist(notches);
    pushToBackend();
  },
  removeNotch: (id) => {
    const notches = get().notches.filter((n) => n.id !== id);
    set({ notches });
    persist(notches);
    pushToBackend();
  },
  clearAll: () => {
    set({ notches: [] });
    persist([]);
    pushToBackend();
  },
  replaceAutoNotches: (autoNotches) => {
    const manual = get().notches.filter((n) => n.source !== 'auto');
    const auto = autoNotches
      .filter((n) => Number.isFinite(n.centerHz) && Number.isFinite(n.widthHz))
      .map((n): Notch => ({
        id: nextId(),
        centerHz: n.centerHz,
        widthHz: Math.max(MIN_NOTCH_WIDTH_HZ, n.widthHz),
        source: 'auto',
      }));
    const notches = [...manual, ...auto];
    set({ notches });
    persist(notches);
    pushToBackend();
  },
  clearAutoNotches: () => {
    const notches = get().notches.filter((n) => n.source !== 'auto');
    if (notches.length === get().notches.length) return;
    set({ notches });
    persist(notches);
    pushToBackend();
  },
  setArmed: (armed) => set({ armed, pending: armed ? get().pending : null }),
  toggleArmed: () => set((s) => ({ armed: !s.armed, pending: null })),
  setPending: (pending) => set({ pending }),
}));

let hydratePromise: Promise<void> | null = null;

export function hydrateNotchesFromBackend(): Promise<void> {
  if (hydratePromise) return hydratePromise;
  hydratePromise = (async () => {
    const localBefore = useNotchStore.getState().notches;
    try {
      if (typeof fetch !== 'function') return;
      const res = await fetch('/api/rx/notches');
      if (!res.ok) throw new Error(`GET /api/rx/notches ${res.status}`);
      const serverNotches = normalizeNotches(await res.json(), true);

      if (serverNotches.length > 0 || localBefore.length === 0) {
        useNotchStore.setState({ notches: serverNotches });
        persist(serverNotches);
        return;
      }

      // One-time migration path for operators upgrading from localStorage-only
      // notches before backend persistence existed.
      await postNotches(localBefore).catch(() => {});
    } catch {
      // Preserve pre-server behavior if the backend is temporarily unavailable.
      await postNotches(localBefore).catch(() => {});
    } finally {
      hydratePromise = null;
    }
  })();
  return hydratePromise;
}

// Hydrate notches whenever the radio (re)connects. The websocket toggles
// displayStore.connected on open/close.
let lastConnected = useDisplayStore.getState().connected;
if (lastConnected) void hydrateNotchesFromBackend();
useDisplayStore.subscribe((s) => {
  if (s.connected && !lastConnected) void hydrateNotchesFromBackend();
  lastConnected = s.connected;
});
