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

// Manual notch filters ("dynamically notch out EMF").
//
// A notch is an absolute-frequency band the operator paints onto the spectrum.
// Phase 1 (here) is display-only: NotchOverlay masks the band out of the
// panadapter/waterfall so a strong birdie/EMF carrier stops dominating the
// view. Phase 2 wires the same list to real WDSP manual-notch filters so the
// interference is removed from the audio too — the store stays the single
// source of truth either way.
//
// Notches are absolute Hz (not display-relative), so they stay glued to the
// interference across tuning. Persisted to localStorage; `armed` and `pending`
// are transient UI state, never persisted.

import { create } from 'zustand';
import { useDisplayStore } from './display-store';

export type Notch = {
  id: string;
  centerHz: number;
  widthHz: number;
};

// Push the full notch list to the backend, which applies it to WDSP's manual-
// notch database (real audio notch). Fire-and-forget: localStorage is the
// client-side source of truth, so a failed POST just means the notch isn't
// applied to the audio yet — it re-pushes on the next change or reconnect.
function doPush(): void {
  try {
    const notches = useNotchStore.getState().notches.map((n) => ({
      centerHz: n.centerHz,
      widthHz: n.widthHz,
      active: true,
    }));
    void fetch('/api/rx/notches', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ notches }),
    }).catch(() => {});
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
    const parsed = JSON.parse(raw) as unknown;
    if (!Array.isArray(parsed)) return [];
    const out: Notch[] = [];
    for (const item of parsed) {
      const c = (item as Notch)?.centerHz;
      const w = (item as Notch)?.widthHz;
      if (typeof c === 'number' && typeof w === 'number' && Number.isFinite(c) && Number.isFinite(w)) {
        out.push({ id: nextId(), centerHz: c, widthHz: Math.max(MIN_NOTCH_WIDTH_HZ, w) });
      }
    }
    return out;
  } catch {
    return [];
  }
}

function persist(notches: Notch[]): void {
  try {
    if (typeof localStorage === 'undefined') return;
    localStorage.setItem(
      STORAGE_KEY,
      JSON.stringify(notches.map((n) => ({ centerHz: n.centerHz, widthHz: n.widthHz }))),
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
  setArmed: (armed) => set({ armed, pending: armed ? get().pending : null }),
  toggleArmed: () => set((s) => ({ armed: !s.armed, pending: null })),
  setPending: (pending) => set({ pending }),
}));

// Re-apply notches whenever the radio (re)connects: a backend restart starts
// with an empty WDSP notch database, and the client (localStorage) is the
// source of truth. The websocket toggles displayStore.connected on open/close.
let lastConnected = useDisplayStore.getState().connected;
if (lastConnected) pushToBackend();
useDisplayStore.subscribe((s) => {
  if (s.connected && !lastConnected) pushToBackend();
  lastConnected = s.connected;
});
