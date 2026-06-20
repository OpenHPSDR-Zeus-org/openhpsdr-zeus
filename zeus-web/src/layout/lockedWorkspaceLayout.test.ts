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

/** On-screen pixel height a tile actually renders at in a derived layout. */
function renderedPx(d: DerivedWorkspaceLayout, uid: string): number {
  return pxHeight(byUid(d, uid).h, d.rowHeight, d.rowMargin);
}

/** Pixel bottom edge of the whole derived layout (RGL absoluteStrategy). */
function bottomPx(d: DerivedWorkspaceLayout): number {
  return d.placements.reduce((mx, p) => {
    const bottom =
      (d.rowHeight + d.rowMargin) * p.y + pxHeight(p.h, d.rowHeight, d.rowMargin);
    return Math.max(mx, bottom);
  }, 0);
}

describe('deriveWorkspaceLayout — locking is inert', () => {
  it('does not change the row height or unlocked geometry when a tile is locked', () => {
    const base: DeriveTile[] = [
      tile({ uid: 'a', x: 0, y: 0, w: 12, h: 20 }),
      tile({ uid: 'b', x: 0, y: 20, w: 12, h: 30 }), // layoutRows 50 → overflow on 600px
    ];
    const before = deriveWorkspaceLayout(base, opts(600));
    expect(before.rowHeight).toBeLessThan(R); // workspace runs below authored

    // Capture a's exact on-screen height, then lock it (what the UI does).
    const capturedPx = renderedPx(before, 'a');
    const after = deriveWorkspaceLayout(
      base.map((t) =>
        t.uid === 'a' ? { ...t, locked: true, lockedHeightPx: capturedPx } : t,
      ),
      opts(600),
    );

    // No jump: row height unchanged, locked tile still its captured size, and
    // the unlocked neighbour is where it was (its y reflows under the locked
    // tile's compensated span, which equals its stored row to sub-pixel — the
    // reconcile step restores the exact stored row for persistence).
    expect(after.rowHeight).toBeCloseTo(before.rowHeight, 4);
    expect(renderedPx(after, 'a')).toBeCloseTo(capturedPx, 3);
    const beforeB = byUid(before, 'b');
    const afterB = byUid(after, 'b');
    expect(afterB.x).toBe(beforeB.x);
    expect(afterB.w).toBe(beforeB.w);
    expect(afterB.h).toBe(beforeB.h);
    expect(afterB.y).toBeCloseTo(beforeB.y, 3);
  });
});

describe('deriveWorkspaceLayout — locking preserves gaps', () => {
  it('does not pull tiles up to close a gap when a panel is locked', () => {
    // The free grid allows gaps. Locking must NOT magnet the other tiles up to
    // close them — that was the "everything jumps when I lock a panel" bug.
    const base: DeriveTile[] = [
      tile({ uid: 'a', x: 0, y: 0, w: 12, h: 6 }),
      // Deliberate gap: b sits at y=12, not y=6.
      tile({ uid: 'b', x: 0, y: 12, w: 12, h: 6 }),
    ];
    const before = deriveWorkspaceLayout(base, opts(900));
    const capturedPx = renderedPx(before, 'a');
    const after = deriveWorkspaceLayout(
      base.map((t) =>
        t.uid === 'a' ? { ...t, locked: true, lockedHeightPx: capturedPx } : t,
      ),
      opts(900),
    );
    // The gap survives — b stays at its stored row, not pulled up under a.
    expect(byUid(after, 'a')).toMatchObject({ x: 0, y: 0 });
    expect(byUid(after, 'b')).toMatchObject({ x: 0, y: 12 });
  });
});

describe('deriveWorkspaceLayout — locking is inert (short layout)', () => {
  it('does not grow unlocked tiles when the layout fits below authored density', () => {
    // A short docked column: layoutRows (12) < target (48), so the no-lock
    // uniform path already runs BELOW authored R (the target-row divisor shrinks
    // it) even though the layout would fit at R. Locking must stay inert here —
    // it must not jump rowHeight up toward R and grow the unlocked neighbour.
    const base: DeriveTile[] = [
      tile({ uid: 'a', x: 0, y: 0, w: 12, h: 8 }),
      tile({ uid: 'b', x: 0, y: 8, w: 12, h: 4 }),
    ];
    const before = deriveWorkspaceLayout(base, opts(600));
    expect(before.rowHeight).toBeLessThan(R); // shrunk by the target-row divisor

    const capturedPx = renderedPx(before, 'a');
    const after = deriveWorkspaceLayout(
      base.map((t) =>
        t.uid === 'a' ? { ...t, locked: true, lockedHeightPx: capturedPx } : t,
      ),
      opts(600),
    );

    // No upward jump: the locked tile keeps its captured height and the
    // unlocked neighbour does not grow.
    expect(after.rowHeight).toBeCloseTo(before.rowHeight, 4);
    expect(renderedPx(after, 'a')).toBeCloseTo(capturedPx, 3);
    expect(renderedPx(after, 'b')).toBeCloseTo(renderedPx(before, 'b'), 3);
  });
});

describe('deriveWorkspaceLayout — frozen size', () => {
  const layout = (fillH: number): DeriveTile[] => [
    tile({
      uid: 'lock',
      x: 0,
      y: 0,
      w: 12,
      h: 20,
      locked: true,
      lockedHeightPx: 240,
    }),
    tile({ uid: 'fill', x: 0, y: 20, w: 24, h: fillH }),
  ];

  it('renders a locked tile at exactly its captured height in any viewport', () => {
    for (const [container, fillH] of [
      [900, 10],
      [600, 30],
      [500, 60],
    ] as const) {
      const d = deriveWorkspaceLayout(layout(fillH), opts(container));
      expect(renderedPx(d, 'lock')).toBeCloseTo(240, 3);
    }
  });

  it('shrinks unlocked tiles (never the locked one) as the workspace grows', () => {
    const small = deriveWorkspaceLayout(layout(10), opts(600));
    const big = deriveWorkspaceLayout(layout(80), opts(600)); // much taller layout

    expect(renderedPx(small, 'lock')).toBeCloseTo(240, 3);
    expect(renderedPx(big, 'lock')).toBeCloseTo(240, 3);
    // The growth is absorbed by the unlocked tile via a smaller row height.
    expect(big.rowHeight).toBeLessThan(small.rowHeight);
  });

  it('keeps the whole layout within the viewport (never scrolls)', () => {
    const d = deriveWorkspaceLayout(layout(80), opts(600));
    expect(bottomPx(d)).toBeLessThanOrEqual(601); // +1px float slack
  });
});

describe('deriveWorkspaceLayout — legacy locked tile (no captured height)', () => {
  it('holds it at its no-lock-density height so locking stays inert', () => {
    const base: DeriveTile[] = [
      tile({ uid: 'a', x: 0, y: 0, w: 12, h: 20 }),
      tile({ uid: 'b', x: 0, y: 20, w: 12, h: 30 }),
    ];
    const before = deriveWorkspaceLayout(base, opts(600));
    const beforePx = renderedPx(before, 'a');
    // Lock 'a' with NO lockedHeightPx (a tile persisted before the field).
    const after = deriveWorkspaceLayout(
      base.map((t) => (t.uid === 'a' ? { ...t, locked: true } : t)),
      opts(600),
    );
    expect(renderedPx(after, 'a')).toBeCloseTo(beforePx, 1);
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
