// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it } from 'vitest';
import { moveElement } from 'react-grid-layout/core';
import type { Layout } from 'react-grid-layout';

import {
  WORKSPACE_DRAG_COMPACTOR,
  WORKSPACE_RESIZE_COMPACTOR,
  autoFitDroppedPanel,
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

  it('pushes a colliding panel out of the dragged panel target', () => {
    const layout = cloneLayout(baseLayout);
    const dragged = layout[0]!;

    const next = fitMovedElement(layout, dragged, undefined, 2);

    expect(next.find((item) => item.i === 'dragged')).toMatchObject({
      x: 0,
      y: 2,
      w: 6,
      h: 2,
    });
    expect(next.find((item) => item.i === 'below')).toMatchObject({
      x: 0,
      y: 4,
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

    expect(next.find((item) => item.i === 'dragged')).toMatchObject({
      x: 2,
      y: 2,
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

  it('lets the dragged panel jump below the occupied slot once clear', () => {
    const layout = cloneLayout(baseLayout);
    const dragged = layout[0]!;

    const next = fitMovedElement(layout, dragged, undefined, 4);

    expect(next.find((item) => item.i === 'dragged')?.y).toBe(4);
    expect(next.find((item) => item.i === 'below')?.y).toBe(2);
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
