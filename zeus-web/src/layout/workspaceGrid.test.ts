// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it } from 'vitest';
import { moveElement } from 'react-grid-layout/core';
import type { Layout } from 'react-grid-layout';

import {
  WORKSPACE_DRAG_COMPACTOR,
  WORKSPACE_RESIZE_COMPACTOR,
  autoFitDroppedPanel,
  liftLayoutToTop,
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

function fitMovedElement(
  layout: Layout,
  dragged: Layout[number],
  x: number | undefined,
  y: number | undefined,
) {
  const previous = { ...dragged };
  return autoFitDroppedPanel(
    moveElement(
      layout,
      dragged,
      x,
      y,
      true,
      WORKSPACE_DRAG_COMPACTOR.preventCollision,
      WORKSPACE_DRAG_COMPACTOR.type,
      24,
      WORKSPACE_DRAG_COMPACTOR.allowOverlap,
    ),
    24,
    previous,
  );
}

describe('workspace grid collision policy', () => {
  const baseLayout: Layout = [
    { i: 'dragged', x: 0, y: 0, w: 6, h: 2 },
    { i: 'below', x: 0, y: 2, w: 6, h: 2 },
  ];

  it('keeps drag layouts sparse while clearing transient moved flags', () => {
    const next = WORKSPACE_DRAG_COMPACTOR.compact(
      [{ i: 'dragged', x: 3, y: 4, w: 5, h: 2, moved: true }],
      24,
    );

    expect(next).toEqual([
      { i: 'dragged', x: 3, y: 4, w: 5, h: 2, moved: false },
    ]);
  });

  it('cascades drag displacement through the default right-column stack', () => {
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
    const dragged = layout[1]!;

    const moved = moveElement(
      layout,
      dragged,
      18,
      16,
      true,
      WORKSPACE_DRAG_COMPACTOR.preventCollision,
      WORKSPACE_DRAG_COMPACTOR.type,
      24,
      WORKSPACE_DRAG_COMPACTOR.allowOverlap,
    );
    const next = WORKSPACE_DRAG_COMPACTOR.compact(moved, 24);

    expect(next.find((item) => item.i === 'filterpresets')).toMatchObject({
      x: 18,
      y: 16,
      moved: false,
    });
    expect(next.find((item) => item.i === 'tx')).toMatchObject({ y: 26 });
    expect(next.find((item) => item.i === 'txmeters')).toMatchObject({ y: 36 });
    expect(next.find((item) => item.i === 'dsp')).toMatchObject({ y: 48 });
    expectNoCollisions(next);
  });

  it('pushes a colliding panel out of the dragged panel target', () => {
    const layout = cloneLayout(baseLayout);
    const dragged = layout[0]!;

    const next = fitMovedElement(layout, dragged, undefined, 2);

    // After the drop the whole arrangement is lifted to row 0
    // (liftLayoutToTop), so the dragged panel sits at the top and the
    // displaced neighbour lands below it — the colliding panel is still
    // pushed clear of the dragged target, just top-anchored.
    expect(next.find((item) => item.i === 'dragged')).toMatchObject({
      x: 0,
      y: 0,
      w: 6,
      h: 2,
    });
    expect(next.find((item) => item.i === 'below')).toMatchObject({
      x: 0,
      y: 2,
    });
    expectNoCollisions(next);
  });

  it('pushes an occupied gap panel out of the dropped panel target', () => {
    const layout: Layout = cloneLayout([
      { i: 'dragged', x: 0, y: 0, w: 10, h: 2, minW: 2, minH: 2 },
      { i: 'right', x: 14, y: 0, w: 10, h: 2 },
    ]);
    const dragged = layout[0]!;

    const next = fitMovedElement(layout, dragged, 8, 0);

    expect(next.find((item) => item.i === 'dragged')).toMatchObject({
      x: 8,
      y: 0,
      w: 10,
      h: 2,
    });
    expect(next.find((item) => item.i === 'right')).toMatchObject({
      x: 14,
      y: 2,
      w: 10,
      h: 2,
    });
    expectNoCollisions(next);
  });

  it('displaces occupied panels when the dropped footprint is covered', () => {
    const layout: Layout = cloneLayout([
      { i: 'dragged', x: 0, y: 0, w: 12, h: 8, minW: 4, minH: 2 },
      { i: 'left-block', x: 0, y: 0, w: 8, h: 6 },
      { i: 'right-block', x: 8, y: 2, w: 16, h: 4 },
    ]);
    const dragged = layout[0]!;

    const next = fitMovedElement(layout, dragged, 2, 2);

    // The dropped panel keeps its x and size; its y is top-anchored to 0 by
    // liftLayoutToTop (it was the topmost item after the displacement), and the
    // covered blocks are pushed clear below it without overlap.
    expect(next.find((item) => item.i === 'dragged')).toMatchObject({
      x: 2,
      y: 0,
      w: 12,
      h: 8,
    });
    expectNoCollisions(next);
  });

  it('pushes a colliding panel down when the previous slot is blocked', () => {
    const layout: Layout = cloneLayout([
      { i: 'dragged', x: 0, y: 0, w: 6, h: 2 },
      { i: 'old-slot-neighbor', x: 6, y: 0, w: 6, h: 2 },
      { i: 'wide', x: 0, y: 2, w: 12, h: 2 },
    ]);
    const dragged = layout[0]!;

    const next = fitMovedElement(layout, dragged, undefined, 2);

    expect(next.find((item) => item.i === 'dragged')).toMatchObject({
      x: 0,
      y: 2,
      w: 6,
      h: 2,
    });
    expect(next.find((item) => item.i === 'wide')).toMatchObject({
      x: 0,
      y: 4,
      w: 12,
      h: 2,
    });
    expectNoCollisions(next);
  });

  it('top-anchors a two-panel column after the dragged panel clears the slot', () => {
    const layout = cloneLayout(baseLayout);
    const dragged = layout[0]!;

    const next = fitMovedElement(layout, dragged, undefined, 4);

    // Dragging the top panel down past its neighbour and dropping leaves a
    // top gap; liftLayoutToTop normalizes the column so the topmost panel
    // (here `below`, which kept its slot) re-anchors to row 0 and the column
    // stays compact rather than ratcheting the layout height downward on each
    // move. Relative order (below above dragged) and no-collision are
    // preserved.
    const draggedY = next.find((item) => item.i === 'dragged')!.y;
    const belowY = next.find((item) => item.i === 'below')!.y;
    expect(Math.min(draggedY, belowY)).toBe(0);
    expect(draggedY).not.toBe(belowY);
    expectNoCollisions(next);
  });

  it('pushes a lower panel down when resize grows into it', () => {
    const layout = cloneLayout(baseLayout);
    layout[0]!.h = 4;

    const next = WORKSPACE_RESIZE_COMPACTOR.compact(layout, 24);

    expect(next.find((item) => item.i === 'dragged')?.h).toBe(4);
    expect(next.find((item) => item.i === 'below')?.y).toBe(4);
  });

  it('cascades resize pushes through stacked panels', () => {
    const layout: Layout = [
      { i: 'resized', x: 0, y: 0, w: 6, h: 5 },
      { i: 'middle', x: 0, y: 2, w: 6, h: 2 },
      { i: 'bottom', x: 0, y: 4, w: 6, h: 2 },
    ];

    const next = WORKSPACE_RESIZE_COMPACTOR.compact(layout, 24);

    expect(next.find((item) => item.i === 'middle')?.y).toBe(5);
    expect(next.find((item) => item.i === 'bottom')?.y).toBe(7);
  });

  it('does not compact existing vertical gaps during resize', () => {
    const layout: Layout = [
      { i: 'top', x: 0, y: 0, w: 6, h: 2 },
      { i: 'lower', x: 0, y: 6, w: 6, h: 2 },
    ];

    const next = WORKSPACE_RESIZE_COMPACTOR.compact(layout, 24);

    expect(next.find((item) => item.i === 'lower')?.y).toBe(6);
  });
});

function layoutHeight(layout: Layout): number {
  return layout.reduce((max, item) => Math.max(max, item.y + item.h), 0);
}

describe('liftLayoutToTop', () => {
  it('re-anchors the topmost panel to row zero, preserving relative spacing', () => {
    const lifted = liftLayoutToTop([
      { i: 'a', x: 0, y: 4, w: 6, h: 2 },
      { i: 'b', x: 6, y: 7, w: 6, h: 3 },
    ]);

    expect(lifted.find((item) => item.i === 'a')).toMatchObject({ y: 0 });
    // b sat 3 rows below a (y 7 vs 4); the gap is preserved after the lift.
    expect(lifted.find((item) => item.i === 'b')).toMatchObject({ y: 3 });
  });

  it('is a no-op when a panel already sits at row zero', () => {
    const layout: Layout = [
      { i: 'a', x: 0, y: 0, w: 6, h: 2 },
      { i: 'b', x: 0, y: 5, w: 6, h: 2 },
    ];

    const lifted = liftLayoutToTop(cloneLayout(layout));

    expect(lifted).toEqual(layout);
  });

  it('preserves the panel count and never produces a NaN coordinate', () => {
    const lifted = liftLayoutToTop([
      { i: 'a', x: 0, y: 9, w: 6, h: 2 },
      { i: 'b', x: 6, y: 9, w: 6, h: 2 },
      { i: 'c', x: 0, y: 12, w: 6, h: 2 },
    ]);

    expect(lifted).toHaveLength(3);
    for (const item of lifted) {
      expect(Number.isNaN(item.x)).toBe(false);
      expect(Number.isNaN(item.y)).toBe(false);
    }
  });

  it('tolerates an empty layout', () => {
    expect(liftLayoutToTop([])).toEqual([]);
  });
});

describe('workspace layout height stays bounded across many moves', () => {
  // Regression for the "screen goes blank after moving a bunch of panels" leak:
  // drags only ever push neighbours DOWN, so without an upward re-anchor the
  // layout height ratchets up each move until FlexWorkspace floors rowHeight to
  // sub-pixel and every tile blanks. autoFitDroppedPanel now lifts to the top
  // on each persisted drop, so the height tracks the content, not the move
  // count.
  function fit(layout: Layout, dragged: Layout[number], x: number, y: number) {
    return fitMovedElement(layout, dragged, x, y);
  }

  it('does not grow the layout height as panels are repeatedly moved', () => {
    let layout: Layout = cloneLayout([
      { i: 'one', x: 0, y: 0, w: 6, h: 4 },
      { i: 'two', x: 6, y: 0, w: 6, h: 4 },
      { i: 'three', x: 12, y: 0, w: 6, h: 4 },
      { i: 'four', x: 18, y: 0, w: 6, h: 4 },
    ]);

    const startHeight = layoutHeight(layout);
    const ids = ['one', 'two', 'three', 'four'];

    // Drag each panel onto another panel's footprint many times in a row,
    // forcing displacement on every move.
    for (let move = 0; move < 24; move += 1) {
      const id = ids[move % ids.length]!;
      const dragged = layout.find((item) => item.i === id)!;
      // Target a busy region near the top so panels get pushed down.
      const targetX = (move % 4) * 2;
      layout = fit(layout, dragged, targetX, 0);

      // Invariants on every move: no NaN, count preserved, a panel at the top.
      expect(layout).toHaveLength(ids.length);
      for (const item of layout) {
        expect(Number.isNaN(item.y)).toBe(false);
        expect(Number.isNaN(item.x)).toBe(false);
      }
      expect(layout.some((item) => item.y === 0)).toBe(true);
      expectNoCollisions(layout);
    }

    // The crux: height must not have ballooned. A handful of 4-row tiles can
    // stack at most a few deep; the pre-fix bug grew this without bound.
    expect(layoutHeight(layout)).toBeLessThanOrEqual(startHeight * ids.length);
  });
});
