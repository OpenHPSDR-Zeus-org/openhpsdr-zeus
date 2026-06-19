// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it } from 'vitest';
import { moveElement } from 'react-grid-layout/core';
import type { Layout } from 'react-grid-layout';

import {
  WORKSPACE_DRAG_COMPACTOR,
  WORKSPACE_RESIZE_COMPACTOR,
  autoFitDroppedPanel,
  createWorkspaceDragCompactor,
  layoutPlacementsEqual,
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
  const previousLayout = cloneLayout(layout);
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
    { item: previous, layout: previousLayout },
  );
}

function dragAndCompact(
  layout: Layout,
  dragged: Layout[number],
  x: number | undefined,
  y: number | undefined,
) {
  const dragStart = { item: { ...dragged }, layout: cloneLayout(layout) };
  const compactor = createWorkspaceDragCompactor(() => dragStart);
  return compactor.compact(
    moveElement(
      layout,
      dragged,
      x,
      y,
      true,
      compactor.preventCollision,
      compactor.type,
      24,
      compactor.allowOverlap,
    ),
    24,
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

  it('swaps a lower panel into the dragged panel old slot during live drag', () => {
    const layout = cloneLayout(baseLayout);
    const dragged = layout[0]!;

    const next = dragAndCompact(layout, dragged, undefined, 2);

    expect(next.find((item) => item.i === 'dragged')).toMatchObject({
      x: 0,
      y: 2,
      moved: false,
    });
    expect(next.find((item) => item.i === 'below')).toMatchObject({
      x: 0,
      y: 0,
    });
    expectNoCollisions(next);
  });

  it('swaps the covered panel into the old slot and keeps the stack tight', () => {
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

    const next = dragAndCompact(layout, dragged, 18, 16);

    expect(next.find((item) => item.i === 'filterpresets')).toMatchObject({
      x: 18,
      y: 16,
      moved: false,
    });
    expect(next.find((item) => item.i === 'tx')).toMatchObject({
      x: 12,
      y: 0,
    });
    expect(next.find((item) => item.i === 'txmeters')).toMatchObject({ y: 26 });
    expect(next.find((item) => item.i === 'dsp')).toMatchObject({ y: 38 });
    expectNoCollisions(next);
  });

  it('swaps a colliding panel into the dragged panel old slot on drop', () => {
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
      y: 0,
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
      x: 0,
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

  it('snaps the dragged panel up against the occupied slot once clear', () => {
    const layout = cloneLayout(baseLayout);
    const dragged = layout[0]!;

    const next = fitMovedElement(layout, dragged, undefined, 4);

    expect(next.find((item) => item.i === 'dragged')?.y).toBe(2);
    expect(next.find((item) => item.i === 'below')?.y).toBe(0);
  });

  it('clears transient moved flags after auto-fitting a dropped panel', () => {
    const layout = cloneLayout(baseLayout);
    const dragged = layout[0]!;

    const next = fitMovedElement(layout, dragged, undefined, 2);

    expect(next.every((item) => item.moved === false)).toBe(true);
    expect(WORKSPACE_DRAG_COMPACTOR.compact(next, 24)).toEqual(next);
    expectNoCollisions(next);
  });

  it('compares persisted placements without transient grid fields', () => {
    expect(
      layoutPlacementsEqual(
        [
          { i: 'a', x: 0, y: 0, w: 6, h: 2, moved: true },
          { i: 'b', x: 6, y: 0, w: 6, h: 2, minW: 2 },
        ],
        [
          { i: 'b', x: 6, y: 0, w: 6, h: 2 },
          { i: 'a', x: 0, y: 0, w: 6, h: 2, moved: false },
        ],
      ),
    ).toBe(true);

    expect(
      layoutPlacementsEqual(
        [
          { i: 'a', x: 0, y: 0, w: 6, h: 2 },
          { i: 'b', x: 6, y: 0, w: 6, h: 2 },
        ],
        [
          { i: 'a', x: 0, y: 1, w: 6, h: 2 },
          { i: 'a', x: 0, y: 0, w: 6, h: 2 },
        ],
      ),
    ).toBe(false);
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
