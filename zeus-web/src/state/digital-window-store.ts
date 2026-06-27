// SPDX-License-Identifier: GPL-2.0-or-later
//
// digital-window-store — POSITION ONLY for the floating FT8/FT4/WSPR pop-out
// (DigitalWindow). Unlike freedv-window-store this carries NO open flag and NO
// size: open/close is owned entirely by ft8-store.open / wspr-store.open (the
// single source of truth — engaging the mode opens the pop-out, un-toggling
// closes it), and the window is deliberately NOT resizable (fixed WIDTH/HEIGHT,
// ~2× the FreeDV popup, like a FreeDV-style command pop-out). Only the drag
// position is persisted, so the pop-out reopens where the operator left it.

import { create } from 'zustand';

/** Fixed pop-out size — roughly 2× the FreeDV popup (340×520). The window is
 *  not resizable; the body scrolls within this frame. HEIGHT is clamped to the
 *  viewport at render time so it stays fully grabbable on a laptop. */
export const DIGITAL_WINDOW_WIDTH = 640;
export const DIGITAL_WINDOW_HEIGHT = 980;

const POS_KEY = 'zeus.digitalWindow.pos';

function loadPos(): { x: number; y: number } {
  // Default near the top-right so the pop-out doesn't cover the panadapter.
  const fallback = {
    x:
      typeof window !== 'undefined'
        ? Math.max(40, window.innerWidth - DIGITAL_WINDOW_WIDTH - 40)
        : 120,
    y: 96,
  };
  try {
    const raw = localStorage.getItem(POS_KEY);
    if (!raw) return fallback;
    const parsed = JSON.parse(raw) as Partial<typeof fallback>;
    return {
      x: typeof parsed.x === 'number' ? parsed.x : fallback.x,
      y: typeof parsed.y === 'number' ? parsed.y : fallback.y,
    };
  } catch {
    return fallback;
  }
}

function savePos(pos: { x: number; y: number }): void {
  try {
    localStorage.setItem(POS_KEY, JSON.stringify(pos));
  } catch {
    /* private-mode / quota — position just isn't persisted */
  }
}

interface DigitalWindowState {
  x: number;
  y: number;
  setPosition: (x: number, y: number) => void;
}

export const useDigitalWindowStore = create<DigitalWindowState>((set) => {
  const initial = loadPos();
  return {
    x: initial.x,
    y: initial.y,
    setPosition: (x, y) => {
      set({ x, y });
      savePos({ x, y });
    },
  };
});
