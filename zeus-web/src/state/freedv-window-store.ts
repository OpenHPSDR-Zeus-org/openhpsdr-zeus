// SPDX-License-Identifier: GPL-2.0-or-later
//
// freedv-window-store — open/close + position state for the floating FreeDV
// popup window. Selecting FreeDV mode pops this window open (see App.tsx);
// it carries the same FreeDvPanel body the workspace tile used to, but as a
// draggable overlay instead of a docked panel. Position is persisted to
// localStorage so the window reopens where the operator last left it.

import { create } from 'zustand';

export const FREEDV_WINDOW_MIN_WIDTH = 300;
export const FREEDV_WINDOW_MIN_HEIGHT = 360;
const DEFAULT_WIDTH = 340;
const DEFAULT_HEIGHT = 520;

const POS_KEY = 'zeus.freedvWindow.pos';

function loadPos(): { x: number; y: number; width: number; height: number } {
  // Default near the top-right so the popup doesn't cover the panadapter.
  const fallback = {
    x: typeof window !== 'undefined' ? Math.max(80, window.innerWidth - DEFAULT_WIDTH - 40) : 120,
    y: 96,
    width: DEFAULT_WIDTH,
    height: DEFAULT_HEIGHT,
  };
  try {
    const raw = localStorage.getItem(POS_KEY);
    if (!raw) return fallback;
    const parsed = JSON.parse(raw) as Partial<typeof fallback>;
    return {
      x: typeof parsed.x === 'number' ? parsed.x : fallback.x,
      y: typeof parsed.y === 'number' ? parsed.y : fallback.y,
      width: typeof parsed.width === 'number' ? parsed.width : fallback.width,
      height: typeof parsed.height === 'number' ? parsed.height : fallback.height,
    };
  } catch {
    return fallback;
  }
}

function savePos(pos: { x: number; y: number; width: number; height: number }) {
  try {
    localStorage.setItem(POS_KEY, JSON.stringify(pos));
  } catch {
    /* private-mode / quota — position just isn't persisted */
  }
}

interface FreeDvWindowState {
  isOpen: boolean;
  x: number;
  y: number;
  width: number;
  height: number;
  open: () => void;
  close: () => void;
  toggle: () => void;
  setPosition: (x: number, y: number) => void;
  setSize: (width: number, height: number) => void;
}

export const useFreeDvWindowStore = create<FreeDvWindowState>((set, get) => {
  const initial = loadPos();
  return {
    isOpen: false,
    x: initial.x,
    y: initial.y,
    width: initial.width,
    height: initial.height,
    open: () => set({ isOpen: true }),
    close: () => set({ isOpen: false }),
    toggle: () => set({ isOpen: !get().isOpen }),
    setPosition: (x, y) => {
      set({ x, y });
      const s = get();
      savePos({ x, y, width: s.width, height: s.height });
    },
    setSize: (width, height) => {
      const w = Math.max(FREEDV_WINDOW_MIN_WIDTH, width);
      const h = Math.max(FREEDV_WINDOW_MIN_HEIGHT, height);
      set({ width: w, height: h });
      const s = get();
      savePos({ x: s.x, y: s.y, width: w, height: h });
    },
  };
});
