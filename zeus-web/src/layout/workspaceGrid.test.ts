// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it } from 'vitest';
import type { Layout } from 'react-grid-layout';

import {
  WORKSPACE_DRAG_COMPACTOR,
  WORKSPACE_RESIZE_COMPACTOR,
  autoFitDroppedPanel,
  createWorkspaceDragCompactor,
  resolveResizeOverlaps,
} from './workspaceGrid';

function cloneLayout(layout: Layout): Layout {
  return layout.map((item) => ({ ...item }));
}

function expectNoCollisions(layout: Layout) {
  for (let i = 0; i < layout.length; i += 1) {
    const a = layout[i]!;
    for (let j = i + 1; j < layout.length; j += 1) {
      const b = layout[j]!;
      expect(
        a.x + a.w <= b.x ||
          a.x >= b.x + b.w ||
          a.y + a.h <= b.y ||
          a.y >= b.y + b.h,
      ).toBe(true);
    }
  }
}

/** Build a drop snapshot: `previous` is where the dragged tile started; `dropped`
 *  is the full layout with the dragged tile moved to the drop cell (overlap
 *  allowed, as the live free compactor leaves it). */
function drop(
  original: Layout,
  draggedId: string,
  to: { x: number; y: number },
): Layout {
  const previous = original.find((i) => i.i === draggedId)!;
  const dropped = cloneLayout(original).map((i) =>
    i.i === draggedId ? { ...i, x: to.x, y: to.y, moved: true } : i,
  );
  return autoFitDroppedPanel(dropped, 24, {
    item: { ...previous },
    layout: cloneLayout(original),
  });
}

describe('free-grid live compactors are inert', () => {
  it('drag compactor moves nothing — only clears the moved flag', () => {
    const layout: Layout = [
      { i: 'a', x: 0, y: 0, w: 6, h: 4, moved: true },
      { i: 'b', x: 0, y: 4, w: 6, h: 4 },
      { i: 'c', x: 6, y: 0, w: 6, h: 6 },
    ];
    const next = WORKSPACE_DRAG_COMPACTOR.compact(cloneLayout(layout), 24);
    expect(next).toEqual([
      { i: 'a', x: 0, y: 0, w: 6, h: 4, moved: false },
      { i: 'b', x: 0, y: 4, w: 6, h: 4, moved: false },
      { i: 'c', x: 6, y: 0, w: 6, h: 6, moved: false },
    ]);
  });

  it('createWorkspaceDragCompactor also leaves neighbours untouched', () => {
    const layout: Layout = [
      { i: 'dragged', x: 0, y: 6, w: 6, h: 2, moved: true },
      { i: 'other', x: 0, y: 0, w: 6, h: 2 },
    ];
    const compactor = createWorkspaceDragCompactor(() => null);
    const next = compactor.compact(cloneLayout(layout), 24);
    expect(next.find((i) => i.i === 'other')).toMatchObject({ x: 0, y: 0 });
    expect(next.find((i) => i.i === 'dragged')).toMatchObject({ x: 0, y: 6 });
  });

  it('resize compactor leaves an overlap for the stop handler to resolve', () => {
    // Live resize uses the free compactor: it does not push neighbours. (The
    // overlap is resolved on resize stop by resolveResizeOverlaps.)
    const layout: Layout = [
      { i: 'top', x: 0, y: 0, w: 6, h: 4 },
      { i: 'below', x: 0, y: 2, w: 6, h: 2 },
    ];
    const next = WORKSPACE_RESIZE_COMPACTOR.compact(cloneLayout(layout), 24);
    expect(next.find((i) => i.i === 'below')).toMatchObject({ x: 0, y: 2 });
  });
});

describe('drop placement — move-only + swap', () => {
  const base: Layout = [
    { i: 'a', x: 0, y: 0, w: 6, h: 4 },
    { i: 'b', x: 0, y: 4, w: 6, h: 4 },
  ];

  it('keeps a panel exactly where it is dropped on empty space', () => {
    const next = drop(base, 'a', { x: 12, y: 0 });
    expect(next.find((i) => i.i === 'a')).toMatchObject({ x: 12, y: 0 });
    expect(next.find((i) => i.i === 'b')).toMatchObject({ x: 0, y: 4 });
    expectNoCollisions(next);
  });

  it('does not move other panels when one is dropped into a free gap', () => {
    const layout: Layout = [
      { i: 'a', x: 0, y: 0, w: 6, h: 4 },
      { i: 'b', x: 0, y: 4, w: 6, h: 4 },
      { i: 'c', x: 6, y: 0, w: 6, h: 10 },
    ];
    const next = drop(layout, 'a', { x: 12, y: 0 });
    // b and c are completely undisturbed — the reported bug must not recur.
    expect(next.find((i) => i.i === 'b')).toMatchObject({ x: 0, y: 4 });
    expect(next.find((i) => i.i === 'c')).toMatchObject({ x: 6, y: 0 });
    expectNoCollisions(next);
  });

  it('swaps two same-footprint panels dropped squarely onto each other', () => {
    const next = drop(base, 'a', { x: 0, y: 4 });
    expect(next.find((i) => i.i === 'a')).toMatchObject({ x: 0, y: 4 });
    expect(next.find((i) => i.i === 'b')).toMatchObject({ x: 0, y: 0 });
    expectNoCollisions(next);
  });

  it('stays where dropped (overlaps) on a glancing overlap — never relocates down', () => {
    // Drop `a` one row into `b` (overlap 1 of b's 4 rows, < 50%) → no swap. `a`
    // STAYS exactly where dropped, overlapping b; b never moves and nothing is
    // pushed below the fold.
    const next = drop(base, 'a', { x: 0, y: 1 });
    expect(next.find((i) => i.i === 'b')).toMatchObject({ x: 0, y: 4 });
    expect(next.find((i) => i.i === 'a')).toMatchObject({ x: 0, y: 1 });
  });

  it('overlaps a locked tile on drop without moving it (no relocate)', () => {
    const layout: Layout = [
      { i: 'dragged', x: 0, y: 0, w: 6, h: 4 },
      { i: 'locked', x: 0, y: 4, w: 6, h: 4, static: true },
    ];
    const next = drop(layout, 'dragged', { x: 0, y: 4 });
    // Locked tile is untouched; the dragged tile stays where dropped, on top.
    expect(next.find((i) => i.i === 'locked')).toMatchObject({
      x: 0,
      y: 4,
      static: true,
    });
    expect(next.find((i) => i.i === 'dragged')).toMatchObject({ x: 0, y: 4 });
  });

  it('keeps a large dropped panel at full size, overlapping a locked tile (no relocate)', () => {
    const layout: Layout = [
      { i: 'locked', x: 0, y: 0, w: 24, h: 4, static: true },
      { i: 'hero', x: 0, y: 6, w: 18, h: 20 },
    ];
    // Drop hero up onto the locked banner — it stays where dropped at full size;
    // the locked banner does not move.
    const next = drop(layout, 'hero', { x: 0, y: 0 });
    expect(next.find((i) => i.i === 'hero')).toMatchObject({
      x: 0,
      y: 0,
      w: 18,
      h: 20,
    });
    expect(next.find((i) => i.i === 'locked')).toMatchObject({
      x: 0,
      y: 0,
      static: true,
    });
  });

  it('keeps a large dropped panel at full size where dropped (no relocate search)', () => {
    const layout: Layout = cloneLayout([
      { i: 'filter', x: 0, y: 0, w: 12, h: 10 },
      { i: 'filterpresets', x: 12, y: 0, w: 6, h: 10 },
      { i: 'hero', x: 0, y: 10, w: 18, h: 38 },
      { i: 'vfo', x: 18, y: 0, w: 6, h: 11 },
      { i: 'smeter', x: 18, y: 11, w: 6, h: 5 },
      { i: 'tx', x: 18, y: 16, w: 6, h: 10 },
      { i: 'txmeters', x: 18, y: 26, w: 6, h: 12 },
      { i: 'dsp', x: 18, y: 38, w: 6, h: 10 },
    ]);
    const next = drop(layout, 'hero', { x: 6, y: 4 });
    // Stays exactly where dropped, full size — no downward free-slot search.
    expect(next.find((i) => i.i === 'hero')).toMatchObject({
      x: 6,
      y: 4,
      w: 18,
      h: 38,
    });
  });
});

describe('resolveResizeOverlaps — overlap allowed, locked protected', () => {
  it('leaves the overlapped neighbour exactly in place — the resized tile overlaps it', () => {
    const layout: Layout = [
      { i: 'top', x: 0, y: 0, w: 6, h: 4 }, // grown down over `below`
      { i: 'below', x: 0, y: 2, w: 6, h: 2 },
      { i: 'far', x: 0, y: 10, w: 6, h: 2 },
    ];
    const next = resolveResizeOverlaps(layout, 'top', 24);
    expect(next.find((i) => i.i === 'top')).toMatchObject({ y: 0, h: 4 });
    // `below` and `far` are both left exactly where they were — nothing is
    // pushed to a free slot (which is what used to bring the scrollbar back).
    expect(next.find((i) => i.i === 'below')).toMatchObject({ x: 0, y: 2 });
    expect(next.find((i) => i.i === 'far')).toMatchObject({ x: 0, y: 10 });
  });

  it('clamps the resized tile out of a locked neighbour instead of covering it', () => {
    const layout: Layout = [
      { i: 'top', x: 0, y: 0, w: 6, h: 4 }, // grown down into the locked tile
      { i: 'locked', x: 0, y: 2, w: 6, h: 2, static: true },
    ];
    const next = resolveResizeOverlaps(layout, 'top', 24);
    // The locked tile never moves; the resized tile is trimmed back to its top.
    expect(next.find((i) => i.i === 'locked')).toMatchObject({
      x: 0,
      y: 2,
      static: true,
    });
    expect(next.find((i) => i.i === 'top')).toMatchObject({ y: 0, h: 2 });
  });
});

