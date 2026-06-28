// SPDX-License-Identifier: GPL-2.0-or-later
//
// digital-window-store tests — the position-only store for the FT8/FT4/WSPR
// pop-out. It deliberately has NO open flag (open/close is owned by
// ft8-store/wspr-store) and NO size (the window is not resizable); this verifies
// only the drag-position contract and its localStorage persistence.

import { beforeEach, describe, expect, it, vi } from 'vitest';

const POS_KEY = 'zeus.digitalWindow.pos';

// Local vitest has no real localStorage (see feedback memory) — install a stub so
// the persistence round-trip is deterministic regardless of environment.
beforeEach(() => {
  const mem = new Map<string, string>();
  vi.stubGlobal('localStorage', {
    getItem: (k: string) => mem.get(k) ?? null,
    setItem: (k: string, v: string) => void mem.set(k, v),
    removeItem: (k: string) => void mem.delete(k),
    clear: () => mem.clear(),
  });
  vi.resetModules();
});

describe('digital-window-store', () => {
  it('exposes fixed (non-resizable) window dimensions ~2× the FreeDV popup', async () => {
    const mod = await import('./digital-window-store');
    expect(mod.DIGITAL_WINDOW_WIDTH).toBeGreaterThanOrEqual(600);
    expect(mod.DIGITAL_WINDOW_HEIGHT).toBeGreaterThanOrEqual(900);
  });

  it('has no open flag or size mutators (open/close lives in the mode stores)', async () => {
    const { useDigitalWindowStore } = await import('./digital-window-store');
    const s = useDigitalWindowStore.getState() as unknown as Record<string, unknown>;
    expect(s.isOpen).toBeUndefined();
    expect(s.open).toBeUndefined();
    expect(s.setSize).toBeUndefined();
    expect(typeof s.setPosition).toBe('function');
  });

  it('persists the drag position and reloads it on next import', async () => {
    const first = await import('./digital-window-store');
    first.useDigitalWindowStore.getState().setPosition(321, 222);
    expect(localStorage.getItem(POS_KEY)).toBe(JSON.stringify({ x: 321, y: 222 }));

    // Fresh module load should hydrate from the persisted position.
    vi.resetModules();
    const second = await import('./digital-window-store');
    const st = second.useDigitalWindowStore.getState();
    expect(st.x).toBe(321);
    expect(st.y).toBe(222);
  });
});
