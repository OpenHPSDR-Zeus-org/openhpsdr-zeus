// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

import { create } from 'zustand';

export type CwDecoderState = 'idle' | 'listening' | 'held';

export type CwDecoderStore = {
  state: CwDecoderState;
  // Continuous decoded stream. CW has no line structure (no carriage
  // returns), so this is one flowing buffer the panel scrolls — not a list of
  // per-word lines. Capped to the last `maxChars` so it can't grow unbounded.
  text: string;
  maxChars: number;
  wpm: number;
  snrDb: number;
  confidence: number;

  // Actions
  setEnabled: (enabled: boolean) => void;
  toggleHold: () => void;
  clear: () => void;
  appendText: (chunk: string, wpm: number, snrDb: number, confidence: number) => void;
  updateStats: (wpm: number, snrDb: number, confidence: number) => void;
};

const MAX_CHARS = 4000;

export const useCwDecoderStore = create<CwDecoderStore>((set) => ({
  state: 'idle',
  text: '',
  maxChars: MAX_CHARS,
  wpm: 0,
  snrDb: 0,
  confidence: 0,

  setEnabled: (enabled) =>
    set((s) => ({
      state: s.state === 'held' && enabled ? 'held' : enabled ? 'listening' : 'idle',
    })),

  toggleHold: () =>
    set((s) => ({
      state: s.state === 'listening' ? 'held' : s.state === 'held' ? 'listening' : s.state,
    })),

  clear: () =>
    set({
      text: '',
      wpm: 0,
      snrDb: 0,
      confidence: 0,
    }),

  appendText: (chunk, wpm, snrDb, confidence) =>
    set((s) => ({
      text: (s.text + chunk).slice(-s.maxChars),
      wpm,
      snrDb,
      confidence,
    })),

  updateStats: (wpm, snrDb, confidence) =>
    set({
      wpm,
      snrDb,
      confidence,
    }),
}));
