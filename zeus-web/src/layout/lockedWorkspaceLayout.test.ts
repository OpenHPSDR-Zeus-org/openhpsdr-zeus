// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it } from 'vitest';

import {
  deriveWorkspaceLayout,
  reconcileReportedToStored,
  type DeriveOptions,
  type DeriveTile,
  type DerivedWorkspaceLayout,
} from './lockedWorkspaceLayout';
import type { Layout } from 'react-grid-layout';

// Mirror of the FlexWorkspace constants so the tests read against real values.
const R = 15; // WORKSPACE_ROW_HEIGHT_PX
const M = 3; // WORKSPACE_GRID_MARGIN_PX
const BASE_OPTS: Omit<DeriveOptions, 'containerHeight'> = {
  authoredRowHeightPx: R,
  gridMarginPx: M,
  rowGapShare: 6,
  targetRows: 48,
  minRowHeightPx: 0.1,
};

function opts(containerHeight: number): DeriveOptions {
  return { ...BASE_OPTS, containerHeight };
}

function tile(p: Partial<DeriveTile> & { uid: string }): DeriveTile {
  return {
    x: 0,
    y: 0,
    w: 12,
    h: 4,
    locked: false,
    minH: 2,
    ...p,
  };
}

/** On-screen pixel height a tile renders at for a given grid rowHeight/margin. */
function pxHeight(h: number, rowHeight: number, rowMargin: number): number {
  return h * rowHeight + (h - 1) * rowMargin;
}

function maxBottom(d: DerivedWorkspaceLayout): number {
  return d.placements.reduce((mx, p) => Math.max(mx, p.y + p.h), 0);
}

function byUid(d: DerivedWorkspaceLayout, uid: string) {
  const p = d.placements.find((x) => x.uid === uid);
  if (!p) throw new Error(`no placement for ${uid}`);
  return p;
}

describe('deriveWorkspaceLayout — no locked tiles', () => {
  it('reproduces the uniform shrink and never compensates', () => {
    const tiles = [
      tile({ uid: 'a', y: 0, h: 20 }),
      tile({ uid: 'b', y: 20, h: 30 }), // layoutRows 50 > target
    ];
    const d = deriveWorkspaceLayout(tiles, opts(600));
    expect(d.compensated).toBe(false);
    expect(d.unlockedScale).toBe(1);
    // Placements pass through stored geometry untouched.
    expect(byUid(d, 'b')).toMatchObject({ y: 20, h: 30 });
    // rowHeight is the shrunk uniform value (< authored) for an overflow layout.
    expect(d.rowHeight).toBeLessThan(R);
  });
});

describe('deriveWorkspaceLayout — locked tile, layout fits', () => {
  it('renders at authored rowHeight with stored geometry', () => {
    const tiles = [
      tile({ uid: 'lock', y: 0, h: 20, locked: true }),
      tile({ uid: 'b', y: 20, h: 10 }), // layoutRows 30
    ];
    // Tall container: fitRows = floor((900+3)/18) = 50 >= 30.
    const d = deriveWorkspaceLayout(tiles, opts(900));
    expect(d.compensated).toBe(false);
    expect(d.rowHeight).toBe(R);
    expect(d.rowMargin).toBe(M);
    expect(byUid(d, 'lock')).toMatchObject({ h: 20 });
    expect(byUid(d, 'b')).toMatchObject({ h: 10 });
  });
});

describe('deriveWorkspaceLayout — locked tile, overflow', () => {
  const tiles = [
    tile({ uid: 'lock', x: 0, y: 0, w: 12, h: 20, locked: true }),
    tile({ uid: 'b', x: 0, y: 20, w: 12, h: 20 }),
    tile({ uid: 'c', x: 12, y: 0, w: 12, h: 40 }),
  ]; // layoutRows = 40

  it('keeps the locked tile at its exact stored height while unlocked shrink', () => {
    // container 600 → fitRows = floor(603/18) = 33 < 40 → must compensate.
    const d = deriveWorkspaceLayout(tiles, opts(600));
    expect(d.compensated).toBe(true);
    expect(d.rowHeight).toBe(R);
    // Locked tile is untouched.
    expect(byUid(d, 'lock')).toMatchObject({ x: 0, y: 0, w: 12, h: 20 });
    // Unlocked tiles shrank.
    expect(byUid(d, 'b').h).toBeLessThan(20);
    expect(byUid(d, 'c').h).toBeLessThan(40);
    // Everything fits the viewport (no scroll).
    expect(maxBottom(d)).toBeLessThanOrEqual(33);
  });

  it('respects unlocked tile minH floors', () => {
    const tight = [
      tile({ uid: 'lock', y: 0, h: 18, locked: true }),
      tile({ uid: 'b', y: 18, h: 20, minH: 6 }),
    ];
    // container small enough to force max shrink: fitRows tuned so b cannot go
    // below minH=6 (lock 18 + b 6 = 24).
    const d = deriveWorkspaceLayout(tight, opts(450)); // fitRows floor(453/18)=25
    expect(byUid(d, 'b').h).toBeGreaterThanOrEqual(6);
    expect(byUid(d, 'lock').h).toBe(18);
  });
});

describe('deriveWorkspaceLayout — locked size invariance', () => {
  it('holds the locked tile pixel height constant as the workspace grows/shrinks', () => {
    const make = (extraRows: number): DeriveTile[] => [
      tile({ uid: 'lock', x: 0, y: 0, w: 12, h: 20, locked: true }),
      tile({ uid: 'fill', x: 0, y: 20, w: 24, h: 20 + extraRows }),
    ];
    const authoredPx = pxHeight(20, R, M); // 20*15 + 19*3 = 357

    // Across a fitting layout AND an overflowing one, the locked tile renders at
    // exactly the same pixel height.
    for (const [container, extra] of [
      [900, 0],
      [900, 40],
      [600, 0],
      [600, 60],
    ] as const) {
      const d = deriveWorkspaceLayout(make(extra), opts(container));
      const lock = byUid(d, 'lock');
      expect(pxHeight(lock.h, d.rowHeight, d.rowMargin)).toBeCloseTo(authoredPx, 5);
    }
  });
});

describe('deriveWorkspaceLayout — every tile locked', () => {
  it('falls back to uniform shrink (no unlocked tile can absorb slack)', () => {
    const tiles = [
      tile({ uid: 'a', y: 0, h: 30, locked: true }),
      tile({ uid: 'b', y: 30, h: 30, locked: true }),
    ];
    const d = deriveWorkspaceLayout(tiles, opts(600));
    expect(d.compensated).toBe(false);
    expect(d.rowHeight).toBeLessThan(R); // uniform shrink applied to all
  });
});

describe('reconcileReportedToStored', () => {
  const stored = new Map([
    ['lock', { x: 0, y: 0, w: 12, h: 20 }],
    ['b', { x: 0, y: 20, w: 12, h: 20 }],
  ]);

  it('passes the report through unchanged when not compensated', () => {
    const derived: DerivedWorkspaceLayout = {
      rowHeight: 12,
      rowMargin: 3,
      placements: [],
      compensated: false,
      unlockedScale: 1,
    };
    const reported: Layout = [
      { i: 'b', x: 1, y: 2, w: 6, h: 7 } as Layout[number],
    ];
    expect(reconcileReportedToStored(reported, derived, stored)).toEqual(reported);
  });

  it('restores stored geometry for a pure echo of the derived layout', () => {
    const derived: DerivedWorkspaceLayout = {
      rowHeight: R,
      rowMargin: M,
      placements: [
        { uid: 'lock', x: 0, y: 0, w: 12, h: 20 },
        { uid: 'b', x: 0, y: 20, w: 12, h: 13 }, // shrunk render height
      ],
      compensated: true,
      unlockedScale: 0.65,
    };
    const reported: Layout = [
      { i: 'lock', x: 0, y: 0, w: 12, h: 20 } as Layout[number],
      { i: 'b', x: 0, y: 20, w: 12, h: 13 } as Layout[number], // echo
    ];
    const out = reconcileReportedToStored(reported, derived, stored);
    // The shrunken render height is reverted to the stored authored height.
    expect(out.find((x) => x.i === 'b')).toMatchObject({ h: 20 });
  });

  it('un-scales the height of a genuine edit during compensation', () => {
    const derived: DerivedWorkspaceLayout = {
      rowHeight: R,
      rowMargin: M,
      placements: [{ uid: 'b', x: 0, y: 20, w: 12, h: 13 }],
      compensated: true,
      unlockedScale: 0.5,
    };
    // User resized b in render space to h=10 (differs from derived h=13).
    const reported: Layout = [
      { i: 'b', x: 0, y: 20, w: 12, h: 10 } as Layout[number],
    ];
    const out = reconcileReportedToStored(reported, derived, stored);
    expect(out.find((x) => x.i === 'b')).toMatchObject({ h: 20 }); // 10 / 0.5
  });
});
