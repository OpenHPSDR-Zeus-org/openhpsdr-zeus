// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it } from 'vitest';
import type { Layout } from 'react-grid-layout';

import {
  WORKSPACE_DRAG_COMPACTOR,
  WORKSPACE_RESIZE_COMPACTOR,
  autoFitDroppedPanel,
  createWorkspaceDragCompactor,
  resolveResizeOverlaps,
  tidyWorkspacePlacements,
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

  it('relocates (does not swap) on a glancing overlap', () => {
    // Drop `a` only one row into `b` (overlap is 1 of b's 4 rows, < 50%) → no
    // swap; `a` relocates to the nearest free slot and `b` stays exactly put.
    const next = drop(base, 'a', { x: 0, y: 1 });
    expect(next.find((i) => i.i === 'b')).toMatchObject({ x: 0, y: 4 });
    // `a` did not take `b`'s slot — it settled into free space instead.
    expect(next.find((i) => i.i === 'a')).not.toMatchObject({ x: 0, y: 4 });
    expectNoCollisions(next);
  });

  it('never moves a locked panel a tile is dropped onto', () => {
    const layout: Layout = [
      { i: 'dragged', x: 0, y: 0, w: 6, h: 4 },
      { i: 'locked', x: 0, y: 4, w: 6, h: 4, static: true },
    ];
    const next = drop(layout, 'dragged', { x: 0, y: 4 });
    expect(next.find((i) => i.i === 'locked')).toMatchObject({
      x: 0,
      y: 4,
      static: true,
    });
    // The dragged tile relocates off the locked footprint; nothing else moves.
    expectNoCollisions(next);
  });

  it('keeps a large panel at full size when relocating around a locked tile', () => {
    const layout: Layout = [
      { i: 'locked', x: 0, y: 0, w: 24, h: 4, static: true },
      { i: 'hero', x: 0, y: 6, w: 18, h: 20 },
    ];
    // Drop hero up onto the locked banner — it must not shrink, just relocate.
    const next = drop(layout, 'hero', { x: 0, y: 0 });
    expect(next.find((i) => i.i === 'hero')).toMatchObject({ w: 18, h: 20 });
    expect(next.find((i) => i.i === 'locked')).toMatchObject({
      x: 0,
      y: 0,
      static: true,
    });
    expectNoCollisions(next);
  });

  it('places a large dropped panel without an exponential search blowup', () => {
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
    const start = performance.now();
    const next = drop(layout, 'hero', { x: 6, y: 4 });
    const elapsedMs = performance.now() - start;
    expect(elapsedMs).toBeLessThan(250);
    expect(next.find((i) => i.i === 'hero')).toMatchObject({ w: 18, h: 38 });
    expectNoCollisions(next);
  });
});

describe('resolveResizeOverlaps — local, no cascade', () => {
  it('nudges only the overlapped neighbour aside when a tile grows', () => {
    const layout: Layout = [
      { i: 'top', x: 0, y: 0, w: 6, h: 4 }, // already grown into `below`
      { i: 'below', x: 0, y: 2, w: 6, h: 2 },
      { i: 'far', x: 0, y: 10, w: 6, h: 2 },
    ];
    const next = resolveResizeOverlaps(layout, 'top', 24);
    expect(next.find((i) => i.i === 'top')).toMatchObject({ y: 0, h: 4 });
    // `below` hops to the nearest free slot, `far` is left exactly where it was.
    expect(next.find((i) => i.i === 'far')).toMatchObject({ y: 10 });
    expectNoCollisions(next);
  });

  it('does not move a locked neighbour', () => {
    const layout: Layout = [
      { i: 'top', x: 0, y: 0, w: 6, h: 4 },
      { i: 'locked', x: 0, y: 2, w: 6, h: 2, static: true },
    ];
    const next = resolveResizeOverlaps(layout, 'top', 24);
    expect(next.find((i) => i.i === 'locked')).toMatchObject({
      x: 0,
      y: 2,
      static: true,
    });
  });
});

describe('tidyWorkspacePlacements — explicit pack', () => {
  it('closes vertical gaps while keeping each column', () => {
    const layout: Layout = [
      { i: 'top', x: 0, y: 0, w: 6, h: 2 },
      { i: 'lower', x: 0, y: 6, w: 6, h: 2 },
      { i: 'side', x: 6, y: 4, w: 6, h: 2 },
    ];
    const next = tidyWorkspacePlacements(layout);
    expect(next.find((i) => i.i === 'lower')).toMatchObject({ x: 0, y: 2 });
    expect(next.find((i) => i.i === 'side')).toMatchObject({ x: 6, y: 0 });
    expectNoCollisions(next);
  });

  it('packs movable tiles around a locked tile without moving it', () => {
    const layout: Layout = [
      { i: 'locked', x: 0, y: 4, w: 6, h: 2, static: true },
      { i: 'below', x: 0, y: 8, w: 6, h: 2 },
    ];
    const next = tidyWorkspacePlacements(layout);
    expect(next.find((i) => i.i === 'locked')).toMatchObject({ y: 4 });
    // `below` magnets up to just under the locked tile, not through it.
    expect(next.find((i) => i.i === 'below')).toMatchObject({ x: 0, y: 6 });
    expectNoCollisions(next);
  });
});
