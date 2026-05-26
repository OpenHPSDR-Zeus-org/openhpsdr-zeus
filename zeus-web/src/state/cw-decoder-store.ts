// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

import { create } from 'zustand';

export type CwDecoderState = 'idle' | 'listening' | 'held';

export interface CwDecoderHistoryEntry {
  timestamp: string; // UTC HH:mm:ss
  text: string;
}

export type CwDecoderStore = {
  state: CwDecoderState;
  currentText: string; // Characters currently being decoded
  history: CwDecoderHistoryEntry[];
  maxHistoryLines: number;
  wpm: number;
  snrDb: number;
  confidence: number;

  // Actions
  setEnabled: (enabled: boolean) => void;
  toggleHold: () => void;
  clear: () => void;
  clearHistory: () => void;
  appendChar: (char: string, wpm: number, snrDb: number, confidence: number) => void;
  addToHistory: (text: string) => void;
  updateStats: (wpm: number, snrDb: number, confidence: number) => void;
};

const MAX_HISTORY_LINES = 20;

export const useCwDecoderStore = create<CwDecoderStore>((set) => ({
  state: 'idle',
  currentText: '',
  history: [],
  maxHistoryLines: MAX_HISTORY_LINES,
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
      currentText: '',
      wpm: 0,
      snrDb: 0,
      confidence: 0,
    }),

  clearHistory: () =>
    set({
      history: [],
    }),

  appendChar: (char, wpm, snrDb, confidence) =>
    set((s) => {
      const newText = s.currentText + char;
      // Auto-add to history when we have a word
      if (char === ' ' && s.currentText.length > 0) {
        const historyEntry: CwDecoderHistoryEntry = {
          timestamp: new Date().toUTCString().slice(17, 25), // HH:mm:ss
          text: s.currentText.trim(),
        };
        const history = [historyEntry, ...s.history].slice(0, s.maxHistoryLines);
        return {
          currentText: '',
          history,
          wpm,
          snrDb,
          confidence,
        };
      }
      return {
        currentText: newText,
        wpm,
        snrDb,
        confidence,
      };
    }),

  addToHistory: (text) =>
    set((s) => {
      if (text.trim() === '') return s;
      const entry: CwDecoderHistoryEntry = {
        timestamp: new Date().toUTCString().slice(17, 25),
        text: text.trim(),
      };
      const history = [entry, ...s.history].slice(0, s.maxHistoryLines);
      return { history };
    }),

  updateStats: (wpm, snrDb, confidence) =>
    set({
      wpm,
      snrDb,
      confidence,
    }),
}));